# Proposal

## Why

`IPaymentGateway` 與 `IEmailNotificationService` 在所有環境無條件綁定 Mock 實作：Production 下「確認付款」不收款即成功、通知 Email 不會送出，而系統不會有任何警示。2026-11-12 Production Readiness Review 將此列為 C-1（Critical）與 H-2；真實金流（⑥）與真實 Email（⑤）是 L／M 級工作，短期內無法完成。本 change 先以啟動階段的 fail-fast 守門，把風險從「可能被誤上線而靜默收不到錢」降為「明確拒絕啟動」，為 ⑤⑥ 爭取時間。

## What Changes

- 新增設定鍵 `Mocks:AllowInProduction`（bool，預設 `false`）。
- 環境為 Production 且 `IPaymentGateway` 或 `IEmailNotificationService` 解析為 Mock 實作、旗標又不為 `true` 時，啟動階段拋出 `OptionsValidationException`，訊息指出是哪一項 Mock 與如何明示放行。
- 旗標為 `true` 且實際綁定 Mock 時，Production 啟動允許通過，並記錄一筆 Warning 日誌（列出仍為 Mock 的服務）。
- 非 Production 環境不受影響，不記錄此 Warning。
- 需要明示放行的既有部署：`k8s/10-configmap.yaml`（Production 環境、kind 本機驗證）與 `deploy/docker-compose.verify.yml`（以 Production 映像驗證，預設環境為 Production）設定 `Mocks__AllowInProduction=true`。
- 文件同步：README 已知限制、`docs/review/TODO.md` C-1 標為部分緩解、`docs/2026-11-12-k8s-guide.md`、`.env.example`。

## Capabilities

### New Capabilities
- `mock-service-production-guard`: Production 環境下 Mock 付款與 Mock Email 綁定的啟動守門與明示放行機制。

### Modified Capabilities
<!-- 無：既有 capability 的 Requirement 不變；Mock 實作本身的行為（AlwaysSucceed 等）不動。 -->

## Impact

- 程式：`src/ProjectC.WebApi/Extensions/`（新 options、validator、註冊與啟動時 Warning）；不動 Domain／Application／Infrastructure。
- 設定／部署：`k8s/10-configmap.yaml`、`deploy/docker-compose.verify.yml`；`docker-compose.loadtest-release.yml` 環境仍為 Development，不受影響。
- 測試：WebApi.Tests Startup 目錄新增 fail-fast 與 Warning 測試；`ServiceRegistrationSnapshot.baseline.txt` 會多註冊項目。
- 風險：忘了加旗標的既有 Production 部署會啟動失敗——這是預期行為。
- 排除：真實金流／Email（⑤⑥）、`AlwaysSucceed` 行為、Captcha 既有 guard、`Production` 以外環境（如 Staging）的守門。
