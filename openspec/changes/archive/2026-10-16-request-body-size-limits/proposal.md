# Proposal

## Why

API 目前沒有任何請求本文大小設定（2026-10-16 查 `src`：沒有 `MaxRequestBodySize`、`RequestSizeLimit`、`ConfigureKestrel`），compose 也沒有 reverse proxy，`api` 容器的 Kestrel 直接對外，因此每個端點都接受最大約 30MB（Kestrel 預設 30,000,000 bytes）的本文。攻擊者可以對任何接受本文的端點送出接近 30MB 的 JSON，ASP.NET Core 會先完整讀取、反序列化，Validator 才有機會拒絕，期間消耗 CPU、記憶體與 GC。會員分區的 rate limit 擋不住多帳號或遭竊 Token 的聚合請求，登入、註冊等匿名端點更沒有會員分區可用。

另外，`GlobalExceptionHandler` 對所有例外一律回 500；本文超過上限時 Kestrel 丟出的 `BadHttpRequestException`（自帶 413 等狀態碼）預期會被改寫成 500，呼叫端無法分辨是自己的請求太大還是伺服器故障（tasks 1.1 實測確認）。

這個 change 是 `place-order-request-limits`（其 design.md「前置依賴」）的前置，須先 merge。

## What Changes

- 全域請求本文上限 1MB（1,048,576 bytes），套用到所有端點。
- 建立訂單 `POST /api/orders` 個別上限 16KB（16,384 bytes），比全域更嚴格。
- 建立座位圖 `POST /api/admin/venues/{venueId}/seat-maps` 個別放寬到 10MB（10,485,760 bytes）：1MB 只容納約 7,700 席最長欄位的座位，會擋到中大型場館。放寬本文後須同時限制單一請求的資源耗用，所以一併：
  - 座位數上限 50,000 席（Application 層 Validator，超過回 400，在查場地、檢查重複、建立 entity、開交易之前拒絕）；50,000 席最長欄位約 6.5MB，落在 10MB 內。
  - 獨立的 organizer 分區 rate limit（預設 10 次／60 秒、不排隊，超過回 429 + `Retry-After`），限制同一 Organizer 反覆送大本文。
- 本文超過上限時回 413，ProblemDetails 格式與既有錯誤回應一致（`status`、`title`、`traceId`）。
- `GlobalExceptionHandler` 遇到 `BadHttpRequestException` 時改用例外本身的狀態碼（413、400、408 等）回應 ProblemDetails，記 Warning 而不是 Error；其他例外維持 500。
- **BREAKING（僅限直接呼叫 API 的客戶端）**：超過上限的本文原本可被處理，改為 413。本專案前端與 k6 壓測的請求都遠小於上限（k6 建 2000 席座位圖約 90KB）。

## Capabilities

### New Capabilities

- `request-body-limits`：API 請求本文大小上限、端點個別上限、超過上限與本文讀取錯誤的回應格式。

### Modified Capabilities

- `event-management`：「透過管理 API 建立場地與座位圖」新增單一請求座位數上限 50,000 席。
- `api-rate-limiting`：新增「建立座位圖端點的請求頻率限制」Requirement（organizer 分區）。

（`GlobalExceptionHandler` 的 500 行為原本沒有寫進任何 spec Requirement，本文讀取錯誤的回應規則寫在新的 `request-body-limits`。）

顆粒度：共 3 個 capability（1 新增、2 修改），未超過 CLAUDE.md 的 3 個門檻；座位數上限與 rate limit 是放寬座位圖本文上限的必要配套，拆成獨立 change 會讓 10MB 上限在配套 merge 前沒有資源邊界，所以不拆。

## Impact

- 後端（WebApi 層）：`Program.cs` 設定 Kestrel 全域上限與新的 `create-seat-map` rate limit policy；`OrdersController.PlaceOrder`、`AdminVenuesController` 建立座位圖 action 加個別上限，建立座位圖 action 另加 `[EnableRateLimiting]`；`GlobalExceptionHandler` 處理 `BadHttpRequestException`；新增集中上限常數。
- 後端（Application 層）：`CreateSeatMapRequestValidator` 加座位數上限；新增 `SeatMapLimits` 常數與 `SeatMapRateLimitingOptions` 設定類別（比照既有 `CaptchaRateLimitingOptions` 放 `Application/Common`）。
- 測試：WebApi 整合測試新增一個以 `WebApplicationFactory.UseKestrel()` 跑真正 Kestrel 的 factory（TestServer 不經過 Kestrel，全域上限在 TestServer 上不會生效）；另以 Docker Compose 的 `api` 容器實測。
- 前端、Domain、Infrastructure、資料庫 schema、compose 設定：不變。`appsettings.json` 新增 `SeatMapRateLimiting` 區段（值與程式預設相同）。
- 測試：`CustomWebApplicationFactory` 須把 `SeatMapRateLimiting:PermitLimit` 調高（比照既有 `LoginRateLimiting:PermitLimit = 1000`），因為十多個既有測試檔透過 API 建座位圖；不繼承它的獨立 factory 逐一確認（tasks 4.3）。
- 後續：`place-order-request-limits` 的 task 0.1 以本 change archive 並 merge 為開工條件。
