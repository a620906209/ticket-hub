## ADDED Requirements

### Requirement: 資料庫逾時回應 503 ServiceUnavailable
請求處理過程中因資料庫命令逾時或取得資料庫連線逾時而失敗時，`GlobalExceptionHandler` MUST 回應 `application/problem+json`：狀態碼 503、`Title` 為 `ServiceUnavailable`（機器代碼，沿用「框架層錯誤的 Title 為機器代碼」）、含非空的 `traceId`（值為 `HttpContext.TraceIdentifier`）、不含 `Detail`。此類失敗為資料庫暫時無法即時服務而非程式缺陷，因此不適用 AER-001 的 500 對照；框架層錯誤代碼對照於此補充：資料庫逾時（503）為 `ServiceUnavailable`。非逾時的資料庫例外與其他未預期例外 MUST 維持 500 `InternalServerError`。日誌 MUST 以 Warning 記錄且帶 `TraceId`，MUST NOT 包含 SQL 文字或連線字串。

#### Scenario: AER-DB-001 命令逾時回 503
- **WHEN** 某請求的資料庫命令因超過 `Database:CommandTimeoutSeconds` 而逾時
- **THEN** 回應狀態碼為 503、`Content-Type` 為 `application/problem+json`，`title` 為 `ServiceUnavailable`，`traceId` 非空且等於該請求的 `HttpContext.TraceIdentifier`，不含 `detail`

#### Scenario: AER-DB-002 取得連線逾時回 503
- **WHEN** 某請求因連線池耗盡、等待連線超過 `Database:ConnectionTimeoutSeconds` 而失敗
- **THEN** 回應與 AER-DB-001 相同：503、`ServiceUnavailable`、含 `traceId`、不含 `detail`

#### Scenario: AER-DB-003 逾時例外被包在其他例外內仍判定為逾時
- **WHEN** 逾時例外（命令逾時）為內層例外，外層為 `DbUpdateException` 等包裝例外
- **THEN** 回應同樣為 503 `ServiceUnavailable`

#### Scenario: AER-DB-004 非逾時例外維持 500
- **WHEN** 請求處理過程中拋出非逾時的資料庫例外（例如 `NpgsqlException` 內層不含逾時例外）或一般 `InvalidOperationException`
- **THEN** 回應為 500、`title` 為 `InternalServerError`（AER-001 行為不變）

#### Scenario: AER-DB-005 日誌不含 SQL 與連線字串
- **WHEN** 發生資料庫逾時，且例外訊息或資料中含有 SQL 文字與連線字串哨兵值
- **THEN** 該請求的 Warning 日誌帶 `TraceId`，其輸出不含 SQL 哨兵值與連線字串哨兵值
