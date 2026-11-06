# api-error-responses Specification

## Purpose
規範 API 錯誤回應（ProblemDetails）的共通格式：框架層錯誤的 Title 為穩定機器代碼、業務錯誤回應帶 traceId 以便與日誌對照，以及前端不得以 Title 作為使用者訊息備援。

## Requirements

### Requirement: 框架層錯誤的 Title 為機器代碼
由 `GlobalExceptionHandler`、請求頻率限制與 `Result` 業務失敗轉換所產生的 `ProblemDetails` 錯誤回應，`Title` MUST 為不含空白與標點的穩定機器代碼，不得為人類可讀句子。框架對模型繫結失敗自動產生的 400 不在本 Requirement 範圍，其 `Title` 規則由後續 change 定義；JWT 認證 scheme 產生的 401／403 另由「認證挑戰與授權拒絕回應為 ProblemDetails」規範。框架層錯誤（`GlobalExceptionHandler` 與限流）的代碼對照如下：未預期例外（500）為 `InternalServerError`；請求本文過大（413）為 `PayloadTooLarge`；請求逾時（408）為 `RequestTimeout`；請求格式錯誤（400）為 `BadRequest`；請求頻率限制（429）為 `TooManyRequests`。其他以 `BadHttpRequestException` 表示的狀態碼，`Title` MUST 為該狀態碼的 reason phrase 僅保留英文字母與數字（移除空白、標點等其他字元）後的字串；框架無法提供 reason phrase，或移除後為空字串時 MUST 為 `BadRequest`。這些回應 MUST 維持不含 `Detail`（500）的既有行為，且 `status`、`traceId` 欄位不變。

#### Scenario: AER-001 未預期例外的 Title
- **WHEN** 請求處理過程中發生未預期例外
- **THEN** 回應狀態碼為 500，`ProblemDetails.title` 為 `InternalServerError`，含非空的 `traceId`，不含 `detail`

#### Scenario: AER-002 本文過大的 Title
- **WHEN** 請求本文超過端點的大小上限
- **THEN** 回應狀態碼為 413，`title` 為 `PayloadTooLarge`，含 `traceId`

#### Scenario: AER-003 請求逾時與格式錯誤的 Title
- **WHEN** 框架以 `BadHttpRequestException` 回報狀態碼 408 或 400
- **THEN** 回應 `title` 分別為 `RequestTimeout`、`BadRequest`，含 `traceId`

#### Scenario: AER-004 其他 BadHttpRequestException 狀態碼
- **WHEN** 框架以 `BadHttpRequestException` 回報 408、413、400 以外的 4xx 狀態碼
- **THEN** 回應 `title` 為該狀態碼 reason phrase 僅保留英文字母與數字後的字串（保留原大小寫、只移除非英數字元；例如 431 為 `RequestHeaderFieldsTooLarge`，含標點的 `I'm a teapot` 為 `Imateapot`），框架無 reason phrase 或移除後為空字串時為 `BadRequest`

#### Scenario: AER-005 限流 Title 不變
- **WHEN** 請求被請求頻率限制拒絕
- **THEN** 回應 `title` 為 `TooManyRequests`

### Requirement: 業務錯誤回應帶 traceId
系統將 `Result` 的業務失敗轉換為 `ProblemDetails` 回應時，MUST 在 `extensions` 帶上 `traceId`，其值 MUST 為 `HttpContext.TraceIdentifier`，與框架層錯誤回應及該請求日誌中的 `TraceId` 為同一個值。`status`、`title`（`ErrorType` 名稱）與 `detail` 的既有行為 MUST 不變。

#### Scenario: AER-006 業務錯誤帶 traceId
- **WHEN** 任一端點因業務失敗（例如查詢不存在的資源）回傳 `NotFound`
- **THEN** 回應 `ProblemDetails` 含非空的 `traceId`，且 `status` 為 404、`title` 為 `NotFound`、`detail` 為原錯誤訊息

#### Scenario: AER-007 traceId 等於 HttpContext.TraceIdentifier
- **WHEN** 業務失敗結果在 `HttpContext.TraceIdentifier` 為已知值的請求中執行
- **THEN** 回應的 `traceId` 等於該值；該值與日誌 `TraceId` 的等價由 `observability` 主 spec 既有的關聯值 Requirement 保證，本 Scenario 不重複驗證

#### Scenario: AER-008 不同請求的 traceId 不同
- **WHEN** 同一個用戶端連續送出兩次會得到業務錯誤的請求
- **THEN** 兩次回應的 `traceId` 互不相同

### Requirement: 前端錯誤訊息不得以 Title 作為備援
前端 `ApiError` 的訊息 MUST 依序取 `ProblemDetails.detail`，其次為含 HTTP 狀態碼的通用文字；MUST NOT 以 `title` 作為訊息備援，因為 `title` 是供程式判斷的機器代碼，不是給使用者閱讀的文字。判斷錯誤種類的程式碼仍以 `ApiError.problem.title` 比對代碼。

#### Scenario: AER-009 無 detail 時使用狀態碼文字
- **WHEN** API 回傳含 `title` 但不含 `detail` 的 `ProblemDetails`（例如 500）
- **THEN** `ApiError.message` 為含該 HTTP 狀態碼的通用文字，不等於 `title`

#### Scenario: AER-010 有 detail 時使用 detail
- **WHEN** API 回傳含 `detail` 的 `ProblemDetails`
- **THEN** `ApiError.message` 等於 `detail`

#### Scenario: AER-011 非 JSON 回應使用狀態碼文字
- **WHEN** API 回傳非 JSON 內容的失敗回應
- **THEN** `ApiError.message` 為含該 HTTP 狀態碼的通用文字，`ApiError.problem` 為 `null`

### Requirement: 認證挑戰與授權拒絕回應為 ProblemDetails
由 JWT 認證 scheme 的 Challenge／Forbid 所產生的 401 與 403（`[Authorize]` 未通過認證、角色或 Policy 未滿足、控制器 `Forbid()`），回應 MUST 為 `application/problem+json` 的 `ProblemDetails`，包含 `status`、`title` 與 `traceId`：401 的 `title` MUST 為 `Unauthorized`、403 的 `title` MUST 為 `Forbidden`，`traceId` MUST 為 `HttpContext.TraceIdentifier`。這類回應 MUST NOT 含 `detail`，避免在匿名可達的回應多洩漏失敗原因。401 回應的狀態碼與 `WWW-Authenticate` header MUST 與 JwtBearer 框架預設逐字相同，不因加上 body 而改變。由 `Result` 業務失敗轉換而來的 401／403（例如帳密錯誤、非訂單本人）不屬於本 Requirement，沿用「業務錯誤回應帶 traceId」，其 `detail` 維持原行為。

#### Scenario: AER-012 未帶 token 的 401
- **WHEN** 呼叫需要登入的端點且未帶 `Authorization` header
- **THEN** 回應狀態碼為 401，`title` 為 `Unauthorized`，含非空 `traceId`，不含 `detail`，`WWW-Authenticate` header 與框架預設逐字相同

#### Scenario: AER-013 無效或過期 token 的 401
- **WHEN** 呼叫需要登入的端點且帶格式錯誤、簽章錯誤或已過期的 token
- **THEN** 回應狀態碼為 401，`title` 為 `Unauthorized`，含非空 `traceId`，不含 `detail`，`WWW-Authenticate` header 與框架預設逐字相同

#### Scenario: AER-014 角色不足的 403
- **WHEN** 已登入但不具 Admin 角色的會員呼叫 `AdminOnly` 保護的端點
- **THEN** 回應狀態碼為 403，`title` 為 `Forbidden`，含非空 `traceId`，不含 `detail`

#### Scenario: AER-015 缺少合法主辦方情境的 403
- **WHEN** 已登入但 token 不帶合法 `OrganizerId` 的使用者呼叫 `RequireOrganizerContext` 保護的端點
- **THEN** 回應狀態碼為 403，`title` 為 `Forbidden`，含非空 `traceId`，不含 `detail`

#### Scenario: AER-016 控制器 Forbid 的 403
- **WHEN** 控制器動作以 `Forbid()` 結束請求
- **THEN** 回應狀態碼為 403，body 與 AER-014 相同格式

#### Scenario: AER-017 traceId 等於 HttpContext.TraceIdentifier
- **WHEN** 401 或 403 回應產生於 `HttpContext.TraceIdentifier` 為已知值的請求
- **THEN** 回應的 `traceId` 等於該值

#### Scenario: AER-018 業務失敗的 401／403 不受影響
- **WHEN** 端點因業務失敗回傳 401 或 403（例如 `POST /api/auth/login` 密碼錯誤）
- **THEN** 回應仍含原 `detail`，行為與「業務錯誤回應帶 traceId」一致
