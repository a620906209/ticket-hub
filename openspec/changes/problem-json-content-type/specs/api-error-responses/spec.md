## ADDED Requirements

### Requirement: 框架層錯誤回應的 Content-Type 為 application/problem+json
由 `GlobalExceptionHandler` 產生的 500、400、408、413、503 回應，以及請求頻率限制產生的 429 回應，`Content-Type` MUST 為 `application/problem+json`。回應本文 MUST 維持 `status`、`title`、`traceId` 欄位，狀態碼與其他 header（例如 429 的 `Retry-After`）MUST 不因 Content-Type 而改變。框架對無 endpoint 或無 body 的 404、405、415 不在本 Requirement 範圍。

#### Scenario: AER-036 未預期例外的 Content-Type
- **WHEN** 請求處理過程中發生未預期例外
- **THEN** 回應狀態碼為 500，`Content-Type` 為 `application/problem+json`

#### Scenario: AER-037 BadHttpRequestException 的 Content-Type
- **WHEN** 框架以 `BadHttpRequestException` 回報 400、408 或 413
- **THEN** 回應 `Content-Type` 為 `application/problem+json`，`title` 與 `traceId` 不變

#### Scenario: AER-038 資料庫逾時的 Content-Type
- **WHEN** 請求因資料庫逾時回 503
- **THEN** 回應 `Content-Type` 為 `application/problem+json`

#### Scenario: AER-039 限流 429 的 Content-Type
- **WHEN** 請求被請求頻率限制拒絕
- **THEN** 回應狀態碼為 429，`Content-Type` 為 `application/problem+json`，`Retry-After` header（有提供時）、`title` 為 `TooManyRequests` 與 `traceId` 不變

#### Scenario: AER-040 實際 HTTP 回應的 Content-Type
- **WHEN** 對真實主機送出超過本文上限且經 `GlobalExceptionHandler` 處理的請求（413），以及超過限流門檻的請求（429）
- **THEN** 兩者的 HTTP 回應 `Content-Type` 皆為 `application/problem+json`
