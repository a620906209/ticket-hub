## MODIFIED Requirements

### Requirement: 探測回應不暴露內部資訊
探測端點回報探測結果時，回應內容 SHALL 只有純文字 `Healthy` 或 `Unhealthy`。探測處理發生未預期例外時，系統 SHALL 回傳 HTTP 500 的標準錯誤回應。任何回應皆 MUST NOT 包含相依服務名稱、連線資訊或例外訊息，因為這兩個端點不需驗證即可存取。

#### Scenario: HC-BODY-HEALTHY 成功回應只有狀態文字
- **WHEN** 探測結果為成功
- **THEN** 回應內容完全等於 `Healthy`

#### Scenario: HC-BODY-UNHEALTHY 失敗回應不含內部細節
- **WHEN** 資料庫無法連線，呼叫端發出 `GET /health/ready`
- **THEN** 回應內容完全等於 `Unhealthy`

#### Scenario: HC-BODY-EXCEPTION 例外回應為標準錯誤格式且不含例外訊息
- **WHEN** 探測處理發生未預期例外，呼叫端呼叫探測端點
- **THEN** 系統回傳 HTTP 500，回應內容為 ProblemDetails JSON，只有 `status`、`title`、`traceId` 三個欄位，其 `status` 為 500、`title` 為 `InternalServerError`、`traceId` 不為空，且回應內容不含例外訊息、例外型別、堆疊、相依服務名稱或連線資訊
