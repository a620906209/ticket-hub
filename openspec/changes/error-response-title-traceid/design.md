## Context

目前 `ProblemDetails` 的產生點有三處：

- `ResultExtensions.CreateProblemResult`：`Title = error.Type.ToString()`、帶 `Detail`，**無 `traceId`**。
- `GlobalExceptionHandler`：500 的 `Title = "An unexpected error occurred."`；`BadHttpRequestException` 時 413 為 `"Request body too large."`，其餘為 `ReasonPhrases.GetReasonPhrase(statusCode)`；皆帶 `traceId = HttpContext.TraceIdentifier`，無 `Detail`。
- `RateLimitingServiceCollectionExtensions` 的 `OnRejected`：`Title = "TooManyRequests"`，帶 `traceId`。

`TraceIdLoggingMiddleware` 把 `HttpContext.TraceIdentifier` 寫進該請求所有 log 的 `TraceId`，`observability` spec 要求它與回應的 `traceId` 為同一個值。

前端只在以下位置依 `problem.title` 比對**業務**代碼：`EventDetailPage.vue`（`RealNameRequired`、`SalesNotOpen`、`SalesClosed`、`CaptchaInvalid`、`QueueAdmissionRequired`）、`LoginPage.vue`／`RegisterPage.vue`（`CaptchaInvalid`）、`ticketRedemptionOutcome.ts`（`HolderVerificationRequired`、`InvalidTicketSignature`）。沒有任何程式依賴 500、413、429 的 `title`。唯一間接依賴是 `httpClient.ts` 的 `ApiError` 訊息備援 `detail ?? title ?? 狀態碼文字`。

## Goals / Non-Goals

**Goals:**
- 全站 `ProblemDetails.Title` 一律為機器代碼。
- 業務錯誤回應可用 `traceId` 對照 log。
- 改名不讓使用者看到內部代碼字串。

**Non-Goals:**
- JWT middleware 的 401／403 body（另開 `jwt-challenge-problem-details`，依賴本 change 定下的代碼規則）。
- 驗證錯誤逐欄位結構（另開 `validation-error-fields`）。
- `[ApiController]` 對模型繫結失敗自動產生的 400，其 `Title` 為框架預設句子（`One or more validation errors occurred.`），歸入 `validation-error-fields` 處理；本 change 完成後 `api-conventions.md` 須如實保留此條為待決定。
- `Detail` 內容調整、時間欄位後綴、分頁。

## Decisions

### 決策 1：框架層 Title 用明確對照表，其餘狀態碼以 reason phrase 移除非英數字元為備援

413／408／400 以 `switch` 明確對照 `PayloadTooLarge`／`RequestTimeout`／`BadRequest`，不依賴 `ReasonPhrases` 的字串在不同框架版本是否改動（例如 413 的 reason phrase 在不同版本曾是 `Request Entity Too Large` 與 `Payload Too Large`）。其他 4xx 以 `ReasonPhrases.GetReasonPhrase(statusCode)` 僅保留英文字母與數字（保留原大小寫，移除空白與標點如 `I'm a teapot` 的 `'`）作為代碼，空字串時退回 `BadRequest`，使未列舉的狀態碼也是不含空白與標點的單一代碼字串，而不是回到句子。此規則只保證「無空白、無標點」，不保證每個字首大寫；這類狀態碼極少見，不為它們維護對照表。

捨棄的方案：全部都用 reason phrase 移除非英數字元。理由：代碼會隨框架版本漂移，而 `Title` 是前端與測試的穩定契約（`api-conventions.md` 明文「不可隨意改名」）。

### 決策 2：業務錯誤的 traceId 由自訂 `IActionResult` 在執行時取值

`ResultExtensions` 是無 `HttpContext` 的靜態擴充方法，且被約 10 個 Controller 的 `ToActionResult` 呼叫。若改簽章傳入 `HttpContext` 會動到所有 Controller，違反最小變更。改為讓 `CreateProblemResult` 回傳一個小型 `IActionResult` 實作（WebApi 層、與 `ResultExtensions` 同資料夾），其 `ExecuteResultAsync(ActionContext)` 在執行時把 `context.HttpContext.TraceIdentifier` 寫入 `ProblemDetails.Extensions["traceId"]`，再委派給內部 `ObjectResult` 輸出（保留 `StatusCode` 與 content negotiation 行為）。

捨棄的方案：改用 MVC 的 `ProblemDetailsFactory`。理由：我的理解是其預設會以 `Activity.Current?.Id ?? HttpContext.TraceIdentifier` 填 `traceId`，在有 Activity 時與 `observability` spec 要求的「等於 `HttpContext.TraceIdentifier`」不同；此為實作時須以實測確認的假設，若實測證明兩者相同仍維持本決策（更小的變更面）。

### 決策 3：前端移除 title 備援，不新增使用者訊息對照

`ApiError` 訊息改為 `detail ?? 狀態碼文字`。500 刻意無 `Detail`（避免外洩內部資訊），改名後若仍退回 `title` 會把 `InternalServerError` 顯示給使用者。不在本 change 新增「依 title 代碼對照友善訊息」機制：目前各頁面已自行處理需要特殊文案的代碼，其餘情況顯示通用狀態碼文字與現況相當。

### 決策 4：Capability 切分

新增 `api-error-responses` 承載跨模組錯誤契約（現行主 spec 沒有集中描述），修改 `request-body-limits` 與 `api-health-checks` 中寫死舊 `title` 字串的 Requirement。`api-rate-limiting` 的 429 描述未寫死 `title` 以外的變動，不修改。`observability` 的 500 body 欄位集合（`status`、`title`、`traceId`）不變，不修改。

### 決策 5：安全確認與 RED 還原驗證

CLAUDE.md 安全確認問題：本 change 不新增外部輸入、不碰 DB、不改權限檢查、不呼叫外部 API，輸入驗證／資料庫／權限各題皆不適用；`traceId` 只是請求識別碼，不含內部資訊，500 維持無 `Detail` 由 AER-001 的測試守住。前端題：不渲染使用者輸入，Auth Header 流程不變。本 change 不是 bug fix，也不屬下單、排隊、座位／庫存核心業務邏輯，不要求 stash 的 RED 還原驗證。

### 決策 6：介面歸類

本 change 不新增介面；自訂 `IActionResult` 屬 WebApi 層的框架型別，不涉及 Domain／Application 介面歸類。

## Risks / Trade-offs

- **BREAKING 的 Title 字串**：外部消費者若比對舊句子會失效。已搜尋前端原始碼無依賴；測試與 k6 腳本中的舊字串於 tasks 1 全專案搜尋並同步。
- **自訂 `IActionResult` 繞過 `ObjectResult` 結果篩選器行為**：以內部委派 `ObjectResult.ExecuteResultAsync` 降低差異；以整合測試驗證 status、content-type（`application/problem+json`）與 body 欄位未變。
- **fallback 代碼的可預測性**：極少見的 4xx（如 431）代碼由框架 reason phrase 決定，已用 AER-004 固定規則並以單元測試覆蓋一個非 408／413／400 的狀態碼。

## Migration Plan

純程式碼變更，無 DB migration。後端與前端同一次部署；即使前端舊版搭配新後端，舊版在 500 時會顯示 `InternalServerError`，屬可接受的短暫差異，不需分階段。回滾為還原 commit。

## Open Questions

無。（`ProblemDetailsFactory` 的 `traceId` 行為屬決策 2 的實測確認項，不影響最終選擇。）
