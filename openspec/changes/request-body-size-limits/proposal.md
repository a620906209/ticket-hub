# Proposal

## Why

API 目前沒有任何請求本文大小設定（2026-10-16 查 `src`：沒有 `MaxRequestBodySize`、`RequestSizeLimit`、`ConfigureKestrel`），compose 也沒有 reverse proxy，`api` 容器的 Kestrel 直接對外，因此每個端點都接受最大約 30MB（Kestrel 預設 30,000,000 bytes）的本文。攻擊者可以對任何接受本文的端點送出接近 30MB 的 JSON，ASP.NET Core 會先完整讀取、反序列化，Validator 才有機會拒絕，期間消耗 CPU、記憶體與 GC。會員分區的 rate limit 擋不住多帳號或遭竊 Token 的聚合請求，登入、註冊等匿名端點更沒有會員分區可用。

另外，`GlobalExceptionHandler` 對所有例外一律回 500；本文超過上限時 Kestrel 丟出的 `BadHttpRequestException`（自帶 413 等狀態碼）預期會被改寫成 500，呼叫端無法分辨是自己的請求太大還是伺服器故障（tasks 1.1 實測確認）。

這個 change 是 `place-order-request-limits`（其 design.md「前置依賴」）的前置，須先 merge。

## What Changes

- 全域請求本文上限 1MB（1,048,576 bytes），套用到所有端點。
- 建立訂單 `POST /api/orders` 個別上限 16KB（16,384 bytes），比全域更嚴格。
- 建立座位圖 `POST /api/admin/venues/{venueId}/seat-maps` 個別放寬到 10MB（10,485,760 bytes）：座位數沒有上限，1MB 只容納約 7,700 席最長欄位的座位；這個端點需要已切換至 Approved Organizer，暴露面較小。
- 本文超過上限時回 413，ProblemDetails 格式與既有錯誤回應一致（`status`、`title`、`traceId`）。
- `GlobalExceptionHandler` 遇到 `BadHttpRequestException` 時改用例外本身的狀態碼（413、400、408 等）回應 ProblemDetails，記 Warning 而不是 Error；其他例外維持 500。
- **BREAKING（僅限直接呼叫 API 的客戶端）**：超過上限的本文原本可被處理，改為 413。本專案前端與 k6 壓測的請求都遠小於上限（k6 建 2000 席座位圖約 90KB）。

## Capabilities

### New Capabilities

- `request-body-limits`：API 請求本文大小上限、端點個別上限、超過上限與本文讀取錯誤的回應格式。

### Modified Capabilities

（無。既有 `api-rate-limiting` 管請求次數，不涉及本文大小；`GlobalExceptionHandler` 的 500 行為原本沒有寫進任何 spec Requirement。）

## Impact

- 後端（WebApi 層）：`Program.cs` 設定 Kestrel 全域上限；`OrdersController.PlaceOrder`、`AdminVenuesController` 建立座位圖 action 加個別上限；`GlobalExceptionHandler` 處理 `BadHttpRequestException`；新增集中上限常數。
- 測試：WebApi 整合測試新增一個以 `WebApplicationFactory.UseKestrel()` 跑真正 Kestrel 的 factory（TestServer 不經過 Kestrel，全域上限在 TestServer 上不會生效）；另以 Docker Compose 的 `api` 容器實測。
- 前端、Application、Domain、Infrastructure、資料庫 schema、compose 設定：不變。
- 後續：`place-order-request-limits` 的 task 0.1 以本 change archive 並 merge 為開工條件。
