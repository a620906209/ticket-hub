# api-error-responses Specification

## Purpose
規範 API 錯誤回應（ProblemDetails）的共通格式：框架層錯誤的 Title 為穩定機器代碼、業務錯誤回應帶 traceId 以便與日誌對照，以及前端不得以 Title 作為使用者訊息備援。

## Requirements

### Requirement: 框架層錯誤的 Title 為機器代碼
由 `GlobalExceptionHandler`、請求頻率限制與 `Result` 業務失敗轉換所產生的 `ProblemDetails` 錯誤回應，`Title` MUST 為不含空白與標點的穩定機器代碼，不得為人類可讀句子。框架對模型繫結失敗自動產生的 400 由「模型繫結失敗的 400 採一致的驗證錯誤格式」規範（`Title` 為 `Validation`）；JWT 認證 scheme 產生的 401／403 另由「認證挑戰與授權拒絕回應為 ProblemDetails」規範。框架層錯誤（`GlobalExceptionHandler` 與限流）的代碼對照如下：未預期例外（500）為 `InternalServerError`；請求本文過大（413）為 `PayloadTooLarge`；請求逾時（408）為 `RequestTimeout`；請求格式錯誤（400）為 `BadRequest`；請求頻率限制（429）為 `TooManyRequests`。其他以 `BadHttpRequestException` 表示的狀態碼，`Title` MUST 為該狀態碼的 reason phrase 僅保留英文字母與數字（移除空白、標點等其他字元）後的字串；框架無法提供 reason phrase，或移除後為空字串時 MUST 為 `BadRequest`。這些回應 MUST 維持不含 `Detail`（500）的既有行為，且 `status`、`traceId` 欄位不變。

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

### Requirement: 輸入驗證失敗的回應含逐欄位錯誤
端點因請求 Validator（FluentValidation）的欄位驗證失敗而回應 400 `Validation` 時，`ProblemDetails` MUST 含 `errors` 欄位，結構為「欄位名稱 → 該欄位的錯誤訊息字串陣列」；同一欄位有多則訊息時 MUST 全數列入該欄位的陣列。`status`、`title`、`detail`、`traceId` 的既有行為 MUST 不變，其中 `detail` MUST 仍為驗證器回報的所有訊息，依回報順序以單一空白串接的字串。不是由請求 Validator 產生、而是 Handler／Service 內的業務判斷所回傳的 `Validation` 錯誤（例如下單時跨項目的同一活動檢查）MUST NOT 含 `errors`。框架模型繫結階段（含非 nullable 參考型別缺漏或為 null 的隱含必填檢查）就被拒絕的請求不會進入 Validator，由「模型繫結失敗的 400 採一致的驗證錯誤格式」規範；欄位存在但為空字串時模型繫結通過，由 Validator 處理，適用本 Requirement。

#### Scenario: AER-019 單一欄位驗證失敗
- **WHEN** 建立活動的請求只有 `title` 超過 200 字元（本文含 `title` 且為非空字串，Validator 的長度規則失敗）
- **THEN** 回應狀態碼為 400，`title` 為 `Validation`，`errors` 含 `title` 鍵且其陣列至少一則訊息，`errors` 不含其他欄位，`traceId` 非空

#### Scenario: AER-020 多個欄位同時失敗
- **WHEN** 請求同時有兩個以上欄位驗證失敗
- **THEN** `errors` 對每個失敗欄位各有一個鍵，每個鍵的陣列只含該欄位的訊息

#### Scenario: AER-021 同一欄位多則訊息
- **WHEN** 同一欄位同時違反兩條以上驗證規則
- **THEN** `errors` 中該欄位的陣列含全部訊息，數量等於違反的規則數

#### Scenario: AER-022 detail 維持合併字串
- **WHEN** 任一欄位驗證失敗
- **THEN** `detail` 等於驗證器回報的所有訊息依回報順序以單一空白串接的字串，與引進 `errors` 前的 `detail` 逐字相同；`errors` 內各欄位陣列的訊息順序與回報順序一致

#### Scenario: AER-032 跨欄位交錯時 detail 依回報順序
- **WHEN** 建立活動請求的 `salesStartAtUtc`、`salesEndAtUtc`、`salesStartAtUtc` 依序各產生一則驗證訊息（m1、m2、m3，欄位交錯）
- **THEN** `detail` 為 `m1 m2 m3`（回報順序），`errors.salesStartAtUtc` 為 [m1, m3]，`errors.salesEndAtUtc` 為 [m2]

#### Scenario: AER-035 空字串欄位由 Validator 處理
- **WHEN** 建立活動的請求 `title` 為空字串（其餘欄位合法）
- **THEN** 回應狀態碼為 400，`title` 為 `Validation`，`errors` 的鍵集合恰為 [`title`] 且陣列至少一則訊息，含 `detail` 與非空 `traceId`

#### Scenario: AER-023 非欄位驗證的 Validation 錯誤不含 errors
- **WHEN** 下單請求因 Handler／Service 內「所選項目不屬於同一個活動」的業務判斷被拒絕（400 `Validation`）
- **THEN** 回應含 `detail` 與 `traceId`，不含 `errors`

### Requirement: errors 欄位名稱採 camelCase 並保留巢狀路徑
`errors` 的鍵 MUST 與 JSON 請求本文中的欄位路徑一致：以 `.` 分隔的每一段，首字母轉為小寫，其餘字元保持原樣；集合索引（例如 `[0]`）MUST 原樣保留。

#### Scenario: AER-024 巢狀集合欄位路徑
- **WHEN** 下單請求的第 1 筆選購項目（索引 0）的 `quantity` 驗證失敗
- **THEN** `errors` 含 `selections[0].quantity` 鍵，不含 `Selections[0].Quantity`

#### Scenario: AER-025 多字詞欄位名稱
- **WHEN** 建立活動的請求中 `startAtUtc` 驗證失敗
- **THEN** `errors` 含 `startAtUtc` 鍵，字元大小寫其餘部分不變

### Requirement: 模型繫結失敗的 400 採一致的驗證錯誤格式
由框架模型繫結失敗（請求本文 JSON 格式錯誤、欄位型別不符、請求本文為空、必填欄位缺漏或為 null 所觸發的隱含必填檢查）自動產生的 400，回應 MUST 為 `application/problem+json`，`status` 為 400、`title` 為 `Validation`，含 `errors` 與 `traceId`，其值 MUST 等於 `HttpContext.TraceIdentifier`（不是 `Activity` 的 id）。`errors` 的鍵 MUST 比照「errors 欄位名稱採 camelCase 並保留巢狀路徑」，且 MUST NOT 帶框架內部的 JSON 路徑前綴 `$.`；整個請求本文層級（沒有對應欄位）的錯誤，鍵 MUST 為空字串；`errors` MUST NOT 含請求本文的 Controller 參數名稱（例如 `request`）形式的鍵，且 MUST NOT 含空陣列的鍵；`errors` 的鍵數 MUST 大於 0——若濾除參數名鍵後 `errors` 沒有任何鍵，其訊息 MUST 改列於空字串鍵，與請求本文層級錯誤一致。此後備規則無對應的 HTTP Scenario（實測的請求觸發不到），由工廠單元測試覆蓋。回應 MUST NOT 含 `detail`。本 Requirement 涵蓋請求本文繫結失敗；其他來源的繫結失敗（query、route）同樣經過同一格式，但不另設 Scenario。

#### Scenario: AER-026 欄位型別不符
- **WHEN** 建立活動的請求中 `maxTicketsPerOrder` 傳入字串 `"abc"`
- **THEN** 回應狀態碼為 400，Content-Type 為 `application/problem+json`，`title` 為 `Validation`，`errors` 的鍵集合恰為 [`maxTicketsPerOrder`]，`traceId` 等於 `HttpContext.TraceIdentifier`，不含 `detail`

#### Scenario: AER-027 JSON 格式錯誤
- **WHEN** 請求本文不是合法 JSON
- **THEN** 回應狀態碼為 400，Content-Type 為 `application/problem+json`，`title` 為 `Validation`，`errors` 的鍵數大於 0 且每個鍵的陣列至少一則訊息，含非空 `traceId`，不含 `detail`

#### Scenario: AER-028 請求本文為空
- **WHEN** 需要請求本文的端點收到 `Content-Type: application/json` 且本文為空的請求
- **THEN** 回應狀態碼為 400，Content-Type 為 `application/problem+json`，`title` 為 `Validation`，`errors` 的鍵集合恰為 [空字串]，`traceId` 等於 `HttpContext.TraceIdentifier`，不含 `detail`

#### Scenario: AER-033 必填欄位缺漏的隱含必填檢查
- **WHEN** 建立活動的請求缺少 `title`，或 `title` 為 null
- **THEN** 回應狀態碼為 400，`title` 為 `Validation`，`errors` 含 `title` 鍵，不含 `detail`，`traceId` 等於 `HttpContext.TraceIdentifier`

#### Scenario: AER-034 請求本文為字面值 null
- **WHEN** 建立活動的請求 `Content-Type: application/json` 且本文為字面值 `null`
- **THEN** 回應狀態碼為 400，`title` 為 `Validation`，`errors` 的鍵集合恰為 [空字串]，不含參數名稱鍵，`traceId` 等於 `HttpContext.TraceIdentifier`，不含 `detail`

#### Scenario: AER-029 錯誤鍵不含 JSON 路徑前綴
- **WHEN** 任一欄位型別不符
- **THEN** `errors` 的鍵數大於 0，且每個鍵都不以 `$` 開頭

### Requirement: 前端保留回應中的逐欄位錯誤
前端 `ApiError.problem` MUST 保留 API 回應中的 `errors`，供呼叫端讀取；`ApiError.message` 的決定規則 MUST 不變（依「前端錯誤訊息不得以 Title 作為備援」）。本 Requirement 不要求任何畫面依 `errors` 逐欄位顯示錯誤。

#### Scenario: AER-030 problem 保留 errors
- **WHEN** API 回傳含 `errors` 的 400 `ProblemDetails`
- **THEN** `ApiError.problem.errors` 等於回應中的 `errors`，`ApiError.message` 等於 `detail`

#### Scenario: AER-031 既有表單的錯誤顯示不變
- **WHEN** 既有表單送出後收到含 `errors` 的 400
- **THEN** 畫面顯示的錯誤訊息與引進 `errors` 前相同（取 `detail`）
