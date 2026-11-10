# Proposal

## Why

登入、註冊、加入購買佇列三個 Handler 一律呼叫 `ICaptchaService.VerifyAsync`，而 `Captcha` 設定區段只有 `TtlSeconds`，沒有任何可關閉驗證的設定。結果是 agent 或腳本無法自動完成這三個流程，`k8s-local-manifests` 的真實瀏覽器驗證（3.3）就必須由使用者手動填三次驗證碼（`docs/todo/2026-11-10-captcha-test-switch-todo.md`）。本 change 提供只在非 Production 環境可用、預設關閉的測試模式。

## What Changes

- 新增設定鍵 `Captcha:VerificationMode`，值為 `Enforced`（預設）或 `AcceptAny`。
  - `Enforced`：行為與現況完全相同。
  - `AcceptAny`：`ICaptchaService.VerifyAsync` 一律回傳通過；`GET /api/captcha` 仍產生並回傳 token 與圖片，前端畫面與 API 契約不變。
- 設定缺漏時視為 `Enforced`；值不對應任何已定義模式（拼字錯誤、未定義數值）時，於啟動階段擋下。
- 在 Production 環境偵測到 `AcceptAny` 時，啟動即失敗（`OptionsValidationException`，比照 `CAPTCHA-STORE-004` 的 Fail Fast 機制）。
- `AcceptAny` 啟用時，於啟動記錄一筆 Warning 日誌。
- 更新 `docs/todo/2026-11-10-deployment-env-vars.md`：把 `Captcha__VerificationMode` 列為一般設定，並註明「只允許非 Production、部署預設不設」。
- **非變更**：不改前端、`GET /api/captcha` 的回應格式、錯誤格式（`CaptchaInvalid` 不變）、驗證碼限流（`CaptchaRateLimiting`）與 Redis 存放方式；不動 k8s manifest（預設不開 `AcceptAny`）；不在本 change 重跑 K8s 3.3 瀏覽器驗證（另做）；不加任何對外 API 或前端開關。

## Capabilities

### New Capabilities
無。

### Modified Capabilities
- `captcha-verification`：新增「驗證模式設定」Requirement（預設 `Enforced`、`AcceptAny` 的行為與限制、Production 啟動即失敗、設定值驗證、啟動 Warning 日誌）。既有 Requirement 的 Enforced 行為不變。

## Impact

- 程式：`CaptchaOptions`（新增 `VerificationMode` 與列舉）、`CaptchaServiceCollectionExtensions`（依模式選擇 `ICaptchaService` 實作、註冊環境驗證）、新增 `AcceptAnyCaptchaService`（Infrastructure）、`EagerOptionsValidationExtensions`（啟動 Warning）。
- 測試：新增設定驗證、`AcceptAny` 行為、Production 拒絕啟動的測試；`ServiceRegistrationSnapshot.baseline.txt` 因 DI 註冊變動需同步。
- 文件：`docs/todo/2026-11-10-deployment-env-vars.md`；`docs/api-conventions.md` 不需更新（本 change 不涉及認證、錯誤格式、命名或分頁慣例，設定鍵不在該文件範圍）。
- 套件／API／資料庫：無變動。
- Change 顆粒度：1 個 capability，tasks 預計約 8 項，不需拆分。
