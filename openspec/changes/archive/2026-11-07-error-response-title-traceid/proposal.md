## Why

`ProblemDetails.Title` 目前兩種風格混用：業務錯誤與 429 是 PascalCase 機器代碼（`NotFound`、`TooManyRequests`），500／413 卻是人類可讀句子（`An unexpected error occurred.`、`Request body too large.`）；而業務錯誤回應沒有 `traceId`，使用者回報問題時無法與 log 對照。前端判斷錯誤種類只依 `Title` 代碼，混用風格會讓日後新增的判斷分支無所適從。這是 `docs/api-conventions.md` 第 2 節標示的前兩項不一致，也是 `docs/api-conventions-todo.md` 第 1 項。

## What Changes

- 框架層錯誤（`GlobalExceptionHandler`）的 `Title` 改為機器代碼：500 → `InternalServerError`、413 → `PayloadTooLarge`、408 → `RequestTimeout`、400 → `BadRequest`；其他 `BadHttpRequestException` 狀態碼以 reason phrase 僅保留英文字母與數字後作為代碼。429 的 `TooManyRequests` 維持不變。**BREAKING**：500、413 的 `Title` 字串改變（已確認前端不依賴這兩者，見 design）。
- 業務錯誤（`ResultExtensions` 將 `Result` 轉換出的 `ProblemDetails`）補上 `traceId` extension，值為 `HttpContext.TraceIdentifier`，與框架層錯誤及 `TraceIdLoggingMiddleware` 寫入 log 的 `TraceId` 為同一個值。`Title`、`Detail`、`Status` 不變。
- 前端 `web/src/api/httpClient.ts` 的 `ApiError` 訊息備援由 `detail ?? title ?? 狀態碼文字` 改為 `detail ?? 狀態碼文字`，不再退回 `title`（500 刻意不帶 `Detail`，否則使用者會看到 `InternalServerError`）。
- 更新受影響的既有測試與 `docs/api-conventions.md` 第 2 節（移除已解決的不一致項目 1、2）。

不在範圍內：JWT middleware 的 401／403 ProblemDetails（另開 `jwt-challenge-problem-details`）、驗證錯誤逐欄位結構、框架對模型繫結失敗自動產生的 400 的 `Title`、`Detail` 內容調整、時間欄位後綴、分頁。

## Capabilities

### New Capabilities

- `api-error-responses`: 全站 `ProblemDetails` 錯誤回應的跨模組契約——`Title` 為機器代碼的規則、框架層錯誤的代碼對照、業務錯誤帶 `traceId`、前端不得把 `title` 當使用者訊息備援。

### Modified Capabilities

- `request-body-limits`: 「本文讀取錯誤以一致的 ProblemDetails 回應」Requirement 中，413 的 `title` 由 `Request body too large.` 改為 `PayloadTooLarge`。
- `api-health-checks`: 「探測回應不暴露內部資訊」Requirement 的例外情境中，500 的 `title` 由 `An unexpected error occurred.` 改為 `InternalServerError`。

## Impact

- 後端：`src/ProjectC.WebApi/ExceptionHandling/GlobalExceptionHandler.cs`、`src/ProjectC.WebApi/Common/ResultExtensions.cs`（可能新增同資料夾的小型 `IActionResult` 實作）。
- 前端：`web/src/api/httpClient.ts` 及其單元測試。
- 測試：`GlobalExceptionHandlerTests`、`RequestBodyLimitsKestrelTests`、`HealthEndpointTestSupport`、`OrganizerScopingDataCorruptionTests` 中斷言舊 `Title` 字串者；新增業務錯誤 `traceId` 與前端備援的測試。
- 文件：`docs/api-conventions.md` 第 2 節、`docs/api-conventions-todo.md` 第 1 項標記完成。
- 無 DB／migration、無新相依、無 API 路由變更。
- Change 顆粒度：影響 3 個 capability（1 新增、2 修改），tasks 遠少於 30 項，不需拆分。
