## Context

`TicketingServiceCollectionExtensions.AddTicketingServices` 無條件註冊 `MockPaymentGateway` 與 `MockEmailNotificationService`（Singleton）。專案已有同型機制可比照：`CaptchaOptionsValidator`（`IValidateOptions<T>` + `IHostEnvironment.IsProduction()`）與 `ResolveEagerlyValidatedOptions` 啟動時強制解析與記 Warning（captcha-test-mode）。需要明示放行的既有部署（事實，已讀取）：`k8s/10-configmap.yaml`（`ASPNETCORE_ENVIRONMENT=Production`）；`deploy/docker-compose.verify.yml`（未設環境，映像預設 `Production`，由 `scripts/verify-production-images.sh` 使用）。`docker-compose.yml` 為 Development、`docker-compose.loadtest-release.yml` 明載仍為 Development，兩者不受影響（修正 Grill 時「loadtest 需設旗標」的假設）。

## Goals / Non-Goals

**Goals**
- Production 下未明示放行即拒絕以 Mock 付款／Email 啟動；明示放行時留下 Warning。
- 為 ⑤⑥ 保留最小改動路徑：真實實作註冊後，檢查自動通過。

**Non-Goals**
- 真實金流／Email；`AlwaysSucceed` 行為；Captcha guard；Production 以外環境（如 Staging）的守門；Provider 設定項。

## Decisions

1. **以 DI 解析出的實作型別判斷**（Grill Q3）。Validator 建構子注入 `IPaymentGateway`、`IEmailNotificationService`，以 `is MockPaymentGateway`／`is MockEmailNotificationService` 判斷。理由：不新增 Provider 設定項、不會被設定遺漏繞過；測試以替身取代註冊即可模擬「已換成真實實作」。代價：WebApi 需引用 Infrastructure 的 Mock 型別（WebApi 本就引用全部，不違反分層）。
2. **機制比照 Captcha**：`MockServicesOptions { bool AllowInProduction }`（Section `Mocks`）、`MockServicesOptionsValidator : IValidateOptions<MockServicesOptions>`（放 `WebApi/Extensions`，因需 `IHostEnvironment`）、不使用 `ValidateOnStart`，改由 `ResolveEagerlyValidatedOptions` 強制解析觸發驗證並記 Warning。理由：與既有啟動驗證集中於同一處，行為一致。Validator 為 Singleton（所注入服務皆 Singleton，符合 DI 生命週期原則）。
3. **單一旗標**（Grill Q2）：`Mocks:AllowInProduction`。⑤ 換掉 Email、⑥ 換掉付款後，對應型別檢查自動通過，最後可整個移除旗標。
4. **預設 `false`、缺漏視為 `false`**：旗標為 bool，無法解析（如 `yes`）時 `ConfigurationBinder` 本就拋例外，視為 fail-loud，不另寫處理（MOCKGUARD-007 以測試鎖定此行為）。
5. **只守 Production**，環境比對沿用 `IsProduction()`（不分大小寫）。Staging 等自訂環境名稱不在範圍；風險：以非 Production 名稱上線可繞過——記為已知限制，與 Captcha guard 一致。
6. **Warning 恰好一筆**：只在「Production 且旗標為 true 且至少一項為 Mock」時記錄；內容列出 Mock 服務名稱，不含任何設定值或機敏資訊。
7. **Registration snapshot**：`ServiceRegistrationSnapshot.baseline.txt` 因新增 options／validator 註冊而需更新；更新時逐行核對新增項目僅限本 change 註冊。

## 跨模組慣例對照

不適用：本 change 不新增或修改 API 端點、錯誤回應、認證／授權、DTO 欄位或列表端點；失敗型態為啟動階段 `OptionsValidationException`，不經 HTTP。

## Risks / Trade-offs

- 既有 Production 部署漏設旗標會啟動失敗 → 預期行為；K8s ConfigMap 與 verify compose 於本 change 內同步設定，`verify-k8s-manifests.sh` 與 `verify-production-images.sh` 作為回歸驗證。
- 旗標被誤帶進真實收費環境 → Warning 日誌可被觀測；文件明載「僅限展示／驗證」；⑤⑥ 完成後移除旗標。
- 型別判斷依賴具體 Mock 類別名稱；Mock 改名時測試會失敗而非靜默通過（測試以型別引用，非字串）。

## Migration Plan

單次部署即生效，無資料移轉。回滾：還原映像即可（旗標為純啟動檢查）。

## Open Questions

無。
