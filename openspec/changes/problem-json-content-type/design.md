# Design

## Context

- `GlobalExceptionHandler.TryHandleAsync` 以 `WriteAsJsonAsync(problemDetails, options: null, contentType, ct)` 寫回應，目前只有 503 時傳入 `application/problem+json`（`contentType = null` 時框架預設為 `application/json`）。
- 429 的 `OnRejected`（`RateLimitingServiceCollectionExtensions`）呼叫 `WriteAsJsonAsync(problemDetails, ct)`，沒有指定 Content-Type。
- `JwtBearerProblemDetailsResponses.WriteProblemAsync` 已寫死 `"application/problem+json"`。
- 三處各自建 `ProblemDetails`（欄位相同：`Status`、`Title`、`traceId` extension），寫入方式與 Content-Type 字串重複。
- 前端 `httpClient.ts` 以 `contentType.includes('json')` 判斷是否解析，`application/problem+json` 仍符合。

## Goals / Non-Goals

**Goals:**
- 上述三處的錯誤回應 Content-Type 一致且只在一處定義。

**Non-Goals:**
- 不改 body 結構、`Title` 代碼、狀態碼、header。
- 不處理框架層無 body 的 404／405／415。
- 不合併三處建立 `ProblemDetails` 的邏輯（只抽「寫出」這一步；各處的 Title／log 行為差異大，合併屬範圍外）。

## Decisions

### 決策 1：共用寫入方法放 `WebApi/Common`，只負責設 Content-Type 並寫出
新增 `ProblemDetailsResponseWriter`（static，`Task WriteAsync(HttpContext, ProblemDetails, CancellationToken)`），內部以 `WriteAsJsonAsync(problemDetails, options: null, contentType: "application/problem+json", ct)` 寫出；狀態碼仍由呼叫端設定。三處改呼叫它。
- 替代方案：各處補字串。拒絕：已有三處重複，漏改一處就再不一致。
- 替代方案：用 `IProblemDetailsService`。拒絕：行為取決於 `AddProblemDetails` 設定與 `Accept` 協商，會改變現有輸出，超出「只改 Content-Type」範圍。
- 為純靜態、無狀態、不涉及 DI，故不註冊進容器。

### 決策 2：移除 `GlobalExceptionHandler` 的 503 條件式與常數
全部分支統一走共用方法，`ProblemJsonContentType` 常數與註解一併刪除。

### 決策 3：驗證分單元與整合兩層
- 單元：`GlobalExceptionHandlerTests` 對 500、400／408／413（BadHttpRequest）、503 各斷言 `Response.ContentType`；429 是 lambda、無單元入口，由整合測試覆蓋。
- 整合：429 於既有 `SeatMapRateLimitingTests`、413 於既有 `RequestBodyLimitsKestrelTests` 補 Content-Type 斷言，確認實際 HTTP 回應（直接 `DefaultHttpContext` 看不到 Kestrel／middleware 組合）。
- 503 已有 Content-Type 斷言（`GlobalExceptionHandlerTests` 與 `DatabaseTimeoutHttpResponseTests`，目前用 `StartWith`），實作時順手改為與決策 4 一致的字串相等；500、400／408／413、429 沒有既有斷言，不會因改動而壞。

### 決策 4：Content-Type 不附 charset
`WriteAsJsonAsync` 指定 `contentType` 時原樣輸出 `application/problem+json`，與 503 及 JWT 401／403 現況一致；測試以字串相等斷言（不用 `StartsWith`），避免日後悄悄附上參數而不被發現。若實測框架附加了 charset，以實測為準並同步修正 spec 與測試，不在本設計預設其行為。

## 跨模組慣例對照

- 錯誤格式（`docs/api-conventions.md`「所有錯誤回應為 RFC 7807 `ProblemDetails`（`application/problem+json`）」）：沿用；本 change 讓實作補齊與該句一致，文件僅補一句指向新 Requirement。
- `Title` 代碼、`traceId`、狀態碼對照：不變。
- 認證（JWT 401／403）：既有行為不變，僅改呼叫共用方法。
- 命名、分頁、路由前綴：不適用（無端點新增）。

## Risks / Trade-offs

- [依賴 `Content-Type: application/json` 的外部 client／腳本] → 已知消費者只有前端（`includes('json')` 相容）與 k6 腳本；實作時 grep `load-test/`、`scripts/`、前端 `src` 確認無嚴格比對（tasks 3.2）。
- [共用方法讓三處耦合] → 只抽一個無狀態靜態方法、不含業務邏輯，耦合面極小。
