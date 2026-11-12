# mock-service-production-guard Specification

## Purpose
TBD - created by archiving change production-guard-for-mocks. Update Purpose after archive.

## Requirements

### Requirement: Production 環境綁定 Mock 付款或 Mock Email 時預設拒絕啟動，須明示放行
系統 SHALL 提供設定鍵 `Mocks:AllowInProduction`（bool），設定缺漏時 MUST 視為 `false`。環境為 Production（環境名稱比對不分大小寫），且 `IPaymentGateway` 實際解析為 `MockPaymentGateway` 或 `IEmailNotificationService` 實際解析為 `MockEmailNotificationService`，而 `Mocks:AllowInProduction` 不為 `true` 時，系統 MUST 在啟動階段拋出 `OptionsValidationException` 並拒絕啟動；訊息 MUST 指出是付款或 Email（兩者皆為 Mock 時兩者都列出）與放行所需的設定鍵。判斷依據為 DI 實際解析的實作型別，不依賴額外的 Provider 設定值；兩項服務皆非 Mock 實作時，不論旗標值為何都 MUST 允許啟動。

#### Scenario: MOCKGUARD-001 Production 無旗標且綁定 Mock 時啟動失敗
- **WHEN** 環境為 Production，`Mocks:AllowInProduction` 未設定，且付款與 Email 皆為 Mock 實作
- **THEN** 啟動階段拋出 `OptionsValidationException`，訊息同時指出 `IPaymentGateway`（付款）與 `IEmailNotificationService`（Email）為 Mock，並提到 `Mocks:AllowInProduction`

#### Scenario: MOCKGUARD-002 旗標明確為 false 時同樣失敗
- **WHEN** 環境為 Production，`Mocks:AllowInProduction` 為 `false`（環境名稱大小寫任意，例如 `production`）
- **THEN** 啟動失敗，行為與 MOCKGUARD-001 相同

#### Scenario: MOCKGUARD-003 只有其中一項為 Mock 時仍失敗並只列出該項
- **WHEN** 環境為 Production、旗標未設定，付款已替換為非 Mock 實作、Email 仍為 Mock
- **THEN** 啟動失敗，訊息只指出 Email 為 Mock，不列付款

#### Scenario: MOCKGUARD-004 非 Mock 實作不受旗標影響
- **WHEN** 環境為 Production、旗標未設定，付款與 Email 皆為非 Mock 實作
- **THEN** 系統正常啟動，不記錄本 Requirement 的 Warning

#### Scenario: MOCKGUARD-005 非 Production 環境不受影響
- **WHEN** 環境為 Development 或 Testing，旗標未設定，付款與 Email 為 Mock 實作
- **THEN** 系統正常啟動，且不記錄本 Requirement 的 Warning

### Requirement: 明示放行 Mock 時 Production 可啟動並留下警示記錄
`Mocks:AllowInProduction` 為 `true`、環境為 Production 且至少一項服務為 Mock 實作時，系統 MUST 允許啟動，並 MUST 在啟動階段記錄恰好一筆 Warning 等級日誌，指出哪些服務仍為 Mock、不得用於收費。旗標值無法解析為 bool 時，系統 MUST 啟動失敗，不得以預設值靜默取代。旗標只放行此項檢查，不改變 Mock 實作本身的行為（`AlwaysSucceed` 等設定照舊）。

#### Scenario: MOCKGUARD-006 旗標為 true 時 Production 啟動並記錄一筆 Warning
- **WHEN** 環境為 Production，`Mocks:AllowInProduction` 為 `true`，付款與 Email 皆為 Mock 實作
- **THEN** 系統正常啟動，且啟動階段恰有一筆 Warning 日誌，內容指出付款與 Email 為 Mock

#### Scenario: MOCKGUARD-007 旗標值無法解析時啟動失敗
- **WHEN** `Mocks:AllowInProduction` 設為非 bool 的值（例如 `yes`）
- **THEN** 系統在啟動階段失敗並拋出例外，不以 `false` 靜默取代

#### Scenario: MOCKGUARD-008 放行後 Mock 行為不變
- **WHEN** Production 以 `Mocks:AllowInProduction = true` 啟動，且 `MockPaymentGateway:AlwaysSucceed` 維持預設
- **THEN** 確認訂單的付款結果與放行前（Development）一致，旗標不改變付款或 Email 的處理結果

### Requirement: 既有以 Production 執行 Mock 的部署須明示放行
`k8s/10-configmap.yaml`（kind 本機驗證）與 `deploy/docker-compose.verify.yml`（Production 映像驗證）MUST 顯式設定 `Mocks__AllowInProduction=true`，使其在守門生效後仍可啟動；`.env.example` 與 K8s 指南 MUST 說明此旗標的用途與「僅限展示／驗證、不得用於收費」。

#### Scenario: MOCKGUARD-009 K8s 與 verify compose 設有明示放行
- **WHEN** 檢視 `k8s/10-configmap.yaml` 與 `deploy/docker-compose.verify.yml`
- **THEN** 兩者皆含 `Mocks__AllowInProduction` 設為 `true`（鍵名恰好出現一次）
