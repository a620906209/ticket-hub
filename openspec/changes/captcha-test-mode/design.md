## Context

`LoginHandler`、`RegisterMemberHandler`、`JoinPurchaseQueueHandler` 一律注入 `ICaptchaService`（`Application/Common/Interfaces`），唯一實作是 `RedisCaptchaService`（`GETDEL` 一次性核對、fail-closed）。`CaptchaOptions` 只有 `TtlSeconds`，由 `AddCaptcha` 註冊並在 `ResolveEagerlyValidatedOptions` 啟動時強制解析（`CAPTCHA-STORE-004`）。需求背景見 `docs/todo/2026-11-10-captcha-test-switch-todo.md`。

## Goals / Non-Goals

**Goals**
- 非 Production 環境可用設定鍵讓驗證碼核對一律通過，使 agent／腳本能自動登入、註冊、加入購買佇列。
- Production 無法被設定檔意外開啟，錯誤在啟動時暴露。

**Non-Goals**
- 不改前端、`GET /api/captcha` 契約、錯誤格式、限流、Redis 存放；不動 k8s manifest；不重跑 K8s 3.3。
- 不提供「固定答案」模式，也不從 Redis 讀答案（等同繞過驗證碼）。

## Decisions

1. **設定鍵 `Captcha:VerificationMode`，列舉 `Enforced`（預設）／`AcceptAny`。** 屬性預設值為 `Enforced`，缺漏即強制驗證。替代方案 `Captcha:Enabled`（bool）：語意是「關掉」，拼錯或型別錯誤的行為不直觀；列舉可擴充且名稱自帶警示。
2. **以 DI 工廠依模式選實作，不在 Handler 加分支。** `ICaptchaService` 改為 Scoped 工廠：`AcceptAny` 回傳新的 `AcceptAnyCaptchaService`（Infrastructure，`VerifyAsync` 恆為通過、不碰 Redis），否則回傳 `RedisCaptchaService`（改為以具體型別註冊後由工廠取得）。Handler 與 `GetCaptchaHandler` 不變，符合「多型靠 DI 切換，不用 if」。
3. **Production 防護放在 `IValidateOptions<CaptchaOptions>`（WebApi，注入 `IHostEnvironment`）。** 規則：Production 且 `AcceptAny` → 失敗；`VerificationMode` 不是已定義列舉值 → 失敗。經 `ResolveEagerlyValidatedOptions` 既有的 `GetRequiredService<CaptchaOptions>()` 觸發，丟 `OptionsValidationException`，與 `CAPTCHA-STORE-004` 同機制。驗證器放 WebApi 而非 Application：Application 不應依賴 Hosting 抽象。環境名稱使用 `IHostEnvironment.IsProduction()`（不分大小寫）。替代方案「Production 直接忽略該鍵」：靜默失效會讓人誤以為已關閉，不如啟動失敗明確。
4. **無效值處理。** 列舉名稱拼錯時，ConfigurationBinder 在 options 解析階段即拋例外（非 `OptionsValidationException`）；不對應任何模式的數值（如 `99`）則由驗證器以 `Enum.IsDefined` 擋下。兩者都在啟動階段失敗，不會靜默改用預設值（`CAPTCHA-MODE-008`，spec 只要求「啟動階段擋下並拋出例外」）。
5. **Warning 日誌在 `ResolveEagerlyValidatedOptions` 記錄。** 該處已是啟動時解析 `CaptchaOptions` 的位置，用 `ILoggerFactory.CreateLogger("ProjectC.Captcha")` 在 `AcceptAny` 時記一筆 Warning，`Enforced` 不記（`CAPTCHA-MODE-007/009`）。日誌不含任何機敏資訊。
6. **介面歸類。** 本 change 不新增介面；`AcceptAnyCaptchaService` 實作既有 `ICaptchaService`，放 Infrastructure，歸類理由不變（純技術性介面，位於 `Application/Common/Interfaces`）。

## 跨模組慣例對照

本 change 不新增或修改 API 端點、錯誤回應、認證、欄位命名或列表端點：`CaptchaInvalid`／`Validation`／429 的格式不變，`docs/api-conventions.md` 無需更新。N/A。

## 安全確認

- 輸入驗證：設定鍵由 `IValidateOptions` 驗證；請求欄位驗證（token／answer 必填、長度上限）仍由既有 FluentValidation 執行，`AcceptAny` 不繞過（`CAPTCHA-MODE-005`）。無 SQL／shell 拼接。
- 資料庫：不涉及；`AcceptAny` 不讀寫 Redis。
- 權限：非端點功能，由部署設定控制；未授權者無法改變設定。Production 以啟動失敗防護。
- 前端：不改。機敏資訊：設定值非機敏；日誌不含 token／答案。
- 風險：非 Production 環境（含共用的 staging）若誤開 `AcceptAny`，驗證碼形同失效；以啟動 Warning 提示，且部署預設不設此鍵。

## Risks / Trade-offs

- 只有 `Production` 環境名稱受保護；若部署用了其他環境名稱（如 `Staging`）仍可開啟。接受此取捨，Warning 日誌與 `deployment-env-vars` 文件註記作為補強。
- `ICaptchaService` 註冊由具體型別改為工廠，`ServiceRegistrationSnapshot.baseline.txt` 的對應行須同步（見 tasks）。

## Migration Plan

無資料或 API 遷移；未設定該鍵時行為與現況相同，可隨一般部署上線，回滾即還原映像。
