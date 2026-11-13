# Proposal

## Why

`docs/api-conventions.md` 規定所有錯誤回應為 `application/problem+json`，但 `GlobalExceptionHandler` 的 400／408／413／500 分支與限流 429 的 `OnRejected` 實際回 `application/json`（只有 503 與 JWT 401／403 符合）。依 RFC 7807 解析錯誤的 client、閘道或監控會因 Content-Type 不符而無法辨識；`GlobalExceptionHandler` 目前還靠一行 503 條件式讓單一分支例外，同一個字串在三處各自重複，漏改一處就會再不一致。

## What Changes

- `GlobalExceptionHandler` 全部分支（400／408／413／500，以及既有 503）與 429 `OnRejected` 一律回 `application/problem+json`。
- 在 `WebApi/Common` 新增共用的 ProblemDetails 寫入方法，`GlobalExceptionHandler`、429 `OnRejected`、`JwtBearerProblemDetailsResponses` 三處改呼叫它；移除 503 條件式與重複的 Content-Type 字串。
- 回應 body（`status`、`title`、`traceId`）、狀態碼與 header 不變，只改 Content-Type。
- 框架層無 body 的 404／405／415 不在範圍。
- `docs/api-conventions.md` 補一句指向新 Requirement。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `api-error-responses`: 新增一條 Requirement，規範框架層錯誤回應（500、400、408、413、429、503）的 Content-Type 為 `application/problem+json`；既有 Requirement 不改。

## Impact

- 程式：`src/ProjectC.WebApi/ExceptionHandling/GlobalExceptionHandler.cs`、`Extensions/RateLimitingServiceCollectionExtensions.cs`、`Common/JwtBearerProblemDetailsResponses.cs`，新增 `Common` 下的共用寫入類別。
- 測試：`GlobalExceptionHandlerTests` 補各分支 Content-Type 斷言；整合測試補 429（`SeatMapRateLimitingTests`）與 413（`RequestBodyLimitsKestrelTests`）。
- 文件：`docs/api-conventions.md`。
- 前端：`httpClient.ts` 以 `contentType.includes('json')` 判斷，不受影響。無 DB、設定或依賴變動。
- 規模：1 個 capability、約 4 項 task，不需拆分。
