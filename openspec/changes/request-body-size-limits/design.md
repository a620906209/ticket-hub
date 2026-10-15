# Design

## Context

動機見 proposal.md「Why」。影響設計的現況：

- `Program.cs` 只有 `AddControllers()`，沒有 `ConfigureKestrel`、沒有任何 `RequestSizeLimit`；`api` 容器以 `ASPNETCORE_URLS=http://0.0.0.0:8080` 讓 Kestrel 直接對外，前面沒有 reverse proxy。
- 管線順序：TraceId middleware → `UseSerilogRequestLogging` → `UseExceptionHandler()`（`GlobalExceptionHandler`）→ `UseAuthentication` → … → `UseRateLimiter` → controllers。
- `GlobalExceptionHandler.TryHandleAsync` 對所有例外 `LogError` 並回 500 ProblemDetails（`title` 為 `An unexpected error occurred.`，含 `traceId` extension）。
- 接受集合的請求 DTO 只有兩個：`PlaceOrderRequest.Selections`（50 筆最長欄位約 5.3KB）與 `CreateSeatMapRequest.Seats`（座位數無上限，每席最長約 130 bytes；k6 建 2000 席約 90KB）。
- WebApi 整合測試全部透過 `WebApplicationFactory<Program>` 的 TestServer；TestServer 不經過 Kestrel，`KestrelServerOptions.Limits` 在 TestServer 上不生效。`Microsoft.AspNetCore.Mvc.Testing` 10.0.11 提供 `UseKestrel()`，可讓 factory 改用真正的 Kestrel 監聽本機 port。
- 既有 `InMemoryLogEventSink` 可斷言 Serilog 結構化日誌；掛載它的 `LogCapturingWebApplicationFactory` 是 `sealed`，不能被繼承。

## Goals / Non-Goals

**Goals:**

- 全域上限在 Kestrel 層生效，涵蓋所有現有與未來的端點，包括匿名端點。
- 兩個端點個別覆寫：建立訂單收緊、建立座位圖放寬；放寬的座位圖端點另加座位數上限與 organizer 分區 rate limit，讓單一請求與單一 Organizer 的資源耗用都有邊界。
- 本文讀取階段的請求錯誤（413／400／408）回對應 4xx 與一致的 ProblemDetails，記 Warning。
- 測試在真正的 Kestrel 上驗證，另在 compose `api` 容器實測。

**Non-Goals:**

- 不限制請求標頭大小、URL 長度、連線數（維持 Kestrel 預設）。
- 不調整 `MinRequestBodyDataRate`、`RequestHeadersTimeout` 等逾時設定（維持 Kestrel 預設，RBL-010 依賴預設值）。
- 不新增 reverse proxy，也不改 compose。目前 compose 只有開發形態（SDK 映像 + bind mount），Kestrel 直接對外；日後若加 reverse proxy，proxy 自身的本文上限與 forwarded header 信任邊界另案處理。
- 不限制回應本文大小。
- 不處理 multipart／檔案上傳（目前沒有上傳端點；未來新增時另訂上限）。

## Decisions

### 1. 上限常數集中在 WebApi 的 `RequestBodyLimits` static class

`GlobalBytes = 1_048_576`、`PlaceOrderBytes = 16_384`、`CreateSeatMapBytes = 10_485_760`，`Program.cs` 與 controller attribute 都引用它，測試也引用它計算「上限 + 1」的本文長度。

- 為何不用 `IOptions`／appsettings：attribute 參數必須是編譯期常數；上限是安全預設而非部署可調參數，可調反而多一個被設錯的入口。與 `place-order-request-limits` 的 `PlaceOrderLimits` 同樣作法。
- 放 WebApi 而非 Application：本文大小是 HTTP 傳輸層概念，Application 不知道 HTTP。

### 2. 全域上限用 `ConfigureKestrel`，個別上限用 `[RequestSizeLimit]`

- `builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = RequestBodyLimits.GlobalBytes)`。
- `OrdersController.PlaceOrder` 加 `[RequestSizeLimit(RequestBodyLimits.PlaceOrderBytes)]`；`AdminVenuesController` 建立座位圖 action 加 `[RequestSizeLimit(RequestBodyLimits.CreateSeatMapBytes)]`。
- `[RequestSizeLimit]` 透過 `IHttpMaxRequestBodySizeFeature` 在讀取本文前覆寫該請求的上限，可以比全域大或小；它是 MVC filter，在 `UseAuthentication`／`UseAuthorization` 之後才執行，所以未登入請求先得到 401（符合 spec「身分驗證先於本文讀取」）。
- 替代方案：自訂 middleware 依路由設上限 —— 路由字串與 controller 分離、易漏改，捨棄。只用 `ConfigureKestrel` 設 10MB —— 其他端點承受 10MB，失去主要保護，捨棄。

### 3. Kestrel 本身處理長度不一致的情境，應用程式不另寫邏輯

RBL-007～010 全部由 Kestrel 的 HTTP/1.1 解析器處理：`Content-Length` 超過上限時第一次讀取即丟 `BadHttpRequestException`(413)，不等本文；chunked 以累計讀取量判斷；`Content-Length` 以後的位元組被當成下一個請求的開頭（RBL-009 只要求「不被當作此請求本文、不 500」）；本文傳送停滯由 `MinRequestBodyDataRate`（預設 240 bytes/s、5 秒 grace period）判定，應用程式仍在讀本文時丟 `BadHttpRequestException`(408)。這些行為以測試鎖住，不在應用程式重寫。

所有本文上限與逾時錯誤都是在**應用程式讀取本文時**才丟出，不是在進入 pipeline 前由 Kestrel 直接拒絕：`[RequestSizeLimit]` 必須能在讀本文前改寫該請求的上限，所以 Kestrel 不能在 pipeline 之前依 `Content-Length` 拒絕。實測證據見決策 4「實測證據」。

不在本 change 保證範圍的情況：應用程式已在讀完本文前回應（例如 JSON 前綴已是語法錯誤，模型繫結回 400），之後用戶端停止傳送，Kestrel 會花最多 5 秒排空剩餘本文，逾時就以 RST 關閉連線（實測 log：`automatic draining of the request body timed out after taking over 5 seconds`，Information 等級）。此時回應已送出，不屬於本文讀取錯誤。

### 4. `GlobalExceptionHandler` 依 `BadHttpRequestException.StatusCode` 回應

- 所有 `Microsoft.AspNetCore.Http.BadHttpRequestException`（Kestrel 與 MVC 丟出的型別都繼承自它）一律依其 `StatusCode` 回應，不只本文讀取階段；其他來源丟出的 `BadHttpRequestException`（例如參數讀取失敗）同樣由原本的 500 改為其 4xx，這是刻意的一致化：`StatusCode` 由框架判定為請求錯誤，回 500 本來就不正確。回應規則：`Status = ex.StatusCode`；`title` 在 413 時為 `Request body too large.`，其他狀態碼用 `ReasonPhrases.GetReasonPhrase(statusCode)`；`traceId` extension 與既有寫法相同。
- 記錄 `LogWarning`，模板只帶狀態碼、HTTP method、路徑樣板與 traceId；**不傳入例外物件、不帶例外訊息**（MVC 版的 Message／InnerException 可能含 Content-Type 值或 JSON 路徑等請求細節），也不讀取、不記錄本文。攻擊者可大量觸發 413 產生 Warning，每筆日誌很小，接受此日誌量放大。
- 其他例外維持原本 500 + `LogError`，不改。
- 若回應已開始送出（`HttpContext.Response.HasStarted`），沿用既有處理器的行為（框架不會再寫入）。
- 替代方案：用 `UseStatusCodePages` 或在 controller 捕捉 —— 例外發生在模型繫結、controller 之前，捕捉不到；也違反「Controller 內不散落 try-catch」，捨棄。
- **實測證據（2026-10-16）**：在 `api` 容器（SDK 10.0.401）建一次性實驗程式（`/tmp`，不進版控，用完已刪除），設定與本專案相同的形態：`ConfigureKestrel` 全域 1024 bytes、`[ApiController]` + `[FromBody]` record、一個 action 加 `[RequestSizeLimit(4096)]`、`AddProblemDetails` + `UseExceptionHandler()` + 自訂 `IExceptionHandler`、全部 logger 開到 Debug。結果：
  - `Content-Length` 2000（本文送完）、chunked 2000、只送標頭 `Content-Length: 2097152` 不送本文、`[RequestSizeLimit(4096)]` 收 5000：全部進入 `IExceptionHandler`，例外型別 `Microsoft.AspNetCore.Server.Kestrel.Core.BadHttpRequestException`、`StatusCode` 413，回應是 handler 寫的本文。只送標頭的情境不等本文就回 413。
  - 恰好 1024 bytes、`[RequestSizeLimit(4096)]` 收 3000：正常進 action。
  - `Content-Length: 1000`、本文送出有效 JSON 前綴 `{"email":"` 後停止：約 5 秒後進入 `IExceptionHandler`，`StatusCode` 408。
  - `Content-Length: 2` 送 `{}` 後接 2000 bytes 的 `A`：action 只被呼叫一次（計數器 +1），`A…` 被 Kestrel 當成下一個請求的請求行，以 `Invalid request line` 拒絕並關閉連線，沒有進入應用程式 pipeline。
  - 全程沒有任何 Error／Critical 等級日誌：`ExceptionHandlerMiddleware` 在 handler 回傳 true 時不記 Error；Kestrel 對這些錯誤只記 Debug（`Microsoft.AspNetCore.Server.Kestrel.BadRequests`）與 Information（排空逾時）。
  - MVC 的 `SystemTextJsonInputFormatter` 沒有把 `BadHttpRequestException` 轉成 ModelState 400。
  - chunked 格式錯誤（第二次實驗，同日）：chunk size 為 `ZZ` 得到 `Bad chunk size data.`、chunk 資料後缺 CRLF 得到 `Bad chunk suffix.`，兩者都進入 `IExceptionHandler`，`StatusCode` 400，沒有 Error 日誌（RBL-015）。
  實驗程式沒有本專案的 Serilog、TraceId middleware、驗證與 rate limiter，所以 task 1.1 仍在真正的應用程式上確認同樣的前提；若結果是 413 未經 `GlobalExceptionHandler`、或 400 ValidationProblem，停止並回報。

### 5. 測試以 `UseKestrel()` 的專用 factory 跑真正 Kestrel

- 新增 `KestrelWebApplicationFactory : CustomWebApplicationFactory`（沿用 Postgres／Redis Testcontainers），比照 `LogCapturingWebApplicationFactory` 自行在 `ConfigureServices` 加 `AddSingleton<ILogEventSink>(LogSink)`；不移除既有類別的 `sealed`，避免動到既有測試基礎設施。建構時呼叫 `UseKestrel(0)`（隨機 port），測試以 factory 提供的 `HttpClient`（`BaseAddress` 指向真正 port）呼叫。
- 為何不用 TestServer：TestServer 不套用 Kestrel 全域上限，RBL-001／002／007～010 會假通過或假失敗。`[RequestSizeLimit]` 在 TestServer 上雖可生效，但為一致起見所有 RBL 測試都走 Kestrel。
- RBL-007～010 用 `TcpClient` 直接寫 HTTP/1.1 原始位元組，才能偽造 `Content-Length`、控制 chunked 分段與停止傳送；RBL-007 只送標頭後就讀回應，以「讀到 413 前沒送任何本文」證明不等本文。RBL-010 送出有效 JSON 前綴後停止（確保應用程式仍在讀本文，不會因語法錯誤提早回應），斷言 30 秒內讀到 408 ProblemDetails。RBL-009 的額外資料用不構成合法請求行的位元組，並以 Serilog 請求摘要日誌的總筆數（不分路徑）作為「進入應用程式 pipeline 的請求數」。
- 「超過上限 1 byte」的本文：用有效 JSON 加空白補足到精確長度，排除 JSON 格式錯誤造成的 400 干擾；長度用 `Encoding.UTF8.GetByteCount` 斷言，避免算錯而假通過。
- 「不建立訂單／座位圖、庫存不變」用 factory 的 DbContext 查詢斷言。
- RBL-012 的日誌範圍：`LogSink` 收到的**整個 process 經 Serilog 輸出的所有日誌**，包含 `Microsoft.AspNetCore.*` 與 Kestrel category（Serilog 透過 `UseSerilog` 接管所有 `ILogger`，category 篩選沿用 appsettings 的 `MinimumLevel`：`Microsoft.AspNetCore` 為 Warning）。斷言：Warning 以上的事件中，來自 `GlobalExceptionHandler` 的恰好一筆 Warning、`TraceId` 屬性等於回應的 `traceId`；任何 category 都沒有 Error／Fatal（實測證據顯示框架不會記，見決策 4）；所有日誌渲染文字與屬性值都不含本文的哨兵字串（本文中放一段唯一的哨兵字串）。
- RBL-013 沿用既有 500 測試（若無則以注入一個會丟例外的 test double 新增）。

### 6. Docker Compose 容器實測

`docker compose up -d api` 後，在 `api` 容器內以 `curl` 對 `http://localhost:8080` 送超過上限的本文（容器內 `localhost` 指容器自身，此處正是要打自己的 Kestrel；這與連線字串禁止 localhost 的規則不衝突），確認 413 + ProblemDetails；再從宿主以 `${API_HOST_PORT:-8080}` 送一次，確認對外 port 同樣生效。結果記錄在 tasks.md。

### 7. 座位數上限 50,000 席，放在 `CreateSeatMapRequestValidator`

- 新增 Application 層 `SeatMapLimits.MaxSeats = 50_000`（`Venues/CreateSeatMap`），Validator 與測試都引用它。與決策 1 同理不用 `IOptions`：上限是安全預設，不是部署可調參數。
- Validator 規則：`Seats` 不為 null 時 `Count <= MaxSeats`，訊息 `Seats must not contain more than 50000 items.`；`RuleForEach(Seats)` 加 `.When(x => x.Seats is null || x.Seats.Count <= SeatMapLimits.MaxSeats)`，超過上限時不逐筆驗證，避免對 10MB 本文跑 5 萬次以上的子 Validator 並產生同等數量的錯誤訊息。比照 `place-order-request-limits` 的「筆數超過只回筆數錯誤」作法。
- 拒絕時機：`CreateSeatMapHandler.HandleAsync` 第一步就是 Validator，失敗即回 `Error.Validation`（400），所以場地查詢、`GroupBy` 重複檢查、`SeatMap`／`Seat` entity 建立、EF 追蹤與交易都不會發生。Handler 不改。
- 為何 50,000：50,000 席最長欄位（每席約 131 bytes）約 6.5MB，落在 10MB 本文上限內，讓「座位數上限」而不是「本文上限」決定合法請求的範圍；一般欄位長度（每席約 40 bytes）50,000 席約 2MB。單一場館 5 萬席已涵蓋大型體育場；k6 壓測建 2,000 席。以 JSON 跳脫（`\uXXXX`）灌長欄位的請求可能先撞到 10MB 得 413，這是可接受的：合法管理介面不會這樣送。
- 只限制新建立的請求，不影響既有座位圖；沒有 schema 或資料遷移。
- 不在 Domain 加 guard：`SeatMap.AddSeat` 逐筆加入，單一 entity 無法在不知道請求總量的情況下判斷；這是請求層級的輸入限制，放 Validator。

### 8. 建立座位圖 rate limit：新的 `create-seat-map` policy，以 OrganizerId 分區

- `Program.cs` 的 `AddRateLimiter` 新增 `rateLimiterOptions.AddPolicy("create-seat-map", httpContext => CreateOrganizerPartition(httpContext))`，`AdminVenuesController` 建立座位圖 action 加 `[EnableRateLimiting("create-seat-map")]`。沿用既有 `OnRejected`（429 ProblemDetails + `Retry-After`），不另寫拒絕回應。
- 分區鍵：`httpContext.User.TryGetOrganizerId(out var organizerId)` 成功時用 `organizerId.ToString()`。授權（`RequireOrganizerContext` policy）在 `UseAuthorization` 執行，早於 `UseRateLimiter`，所以進到限流時 claim 一定合法；若仍取不到，用固定鍵 `"no-organizer"`，讓所有這類請求共用同一個額度（fail-closed，不會因取不到分區鍵而不限流）。
- 為何以 Organizer 而非會員分區：同一 Organizer 的多個成員帳號共用額度，擋「多帳號聚合」；`Venue`／`SeatMap` 是平台共用資源，但呼叫端的 Organizer 是可驗證、需經核准才能取得的身分，比 IP 更適合作為管理端點的分區。
- 設定：新增 `SeatMapRateLimitingOptions`（`Application/Common`，section `SeatMapRateLimiting`），`[Range(1, int.MaxValue)]` 的 `PermitLimit = 10`、`WindowSeconds = 60`，註冊、啟動時強制解析與 `appsettings.json` 寫法逐行比照 `CaptchaRateLimitingOptions`。`QueueLimit = 0`。
- 為何 10 次／60 秒：建立座位圖是低頻的後台操作（建場館時一次），k6 每次執行建 1 張；10 次／分鐘不影響正常操作，同時把單一 Organizer 的 10MB 讀取與反序列化壓到每分鐘最多 10 次。
- 與本文上限的順序：`UseRateLimiter` 早於 MVC 的 `[RequestSizeLimit]` filter 與模型繫結，額度用完時直接 429，不讀本文；額度未用完時，超過 10MB 的請求仍消耗一次額度後得到 413。被限流的請求若用戶端仍在上傳大本文，429 送出後 Kestrel 只依全域 1MB 上限嘗試排空剩餘本文（`[RequestSizeLimit]` 的 10MB 要到 MVC filter 才生效），用戶端可能看到連線關閉而不是 429；這對伺服器有利（不讀 10MB），SRL-001 以小本文驗證 429 格式。
- 測試基礎設施：`CustomWebApplicationFactory` 加 `SeatMapRateLimiting:PermitLimit = 1000`（比照既有 `LoginRateLimiting`），否則十多個透過 API 建座位圖的既有測試會互相撞額度。SRL 測試用自己的 factory 覆寫為小額度。

## 安全確認

- **輸入驗證**：本文大小在 Kestrel（`MaxRequestBodySize`）與 MVC filter（`[RequestSizeLimit]`）層限制，發生在模型繫結與 FluentValidation 之前。座位數上限在 Application 層 FluentValidation（`CreateSeatMapRequestValidator`，決策 7），是 Handler 的第一步；其他既有 Validator 規則不變。
- **SQL／shell 拼接**：無。本 change 不新增任何查詢或指令。
- **資料庫**：不新增查詢，無參數化或 N+1 議題；被 413／429 拒絕的請求不會進到 Handler，座位數超過上限的請求在 Validator 就回 400，不查場地、不開交易。
- **權限**：不改任何授權規則。`OrdersController` 沿用 `[Authorize]`、建立座位圖沿用 `RequireOrganizerContext` policy；授權在 `UseAuthorization` 執行，早於 `UseRateLimiter` 與 `[RequestSizeLimit]` filter，未授權請求先得到 401／403（RBL-014），也不消耗座位圖 rate limit 額度。全域上限不分身分，匿名端點同樣受保護。
- **濫用防護**：建立座位圖以 OrganizerId 分區限流（決策 8），同一 Organizer 的多個成員帳號共用額度。
- **日誌**：413 等請求錯誤記 Warning，只帶狀態碼、method、路徑樣板、traceId，不讀取也不記錄本文（RBL-012）。
- **前端**：不變，沒有新的 DOM 渲染或 API 呼叫。

## Risks / Trade-offs

- [`place-order-request-limits` merge 前，selections 超過約 150 筆的訂單會被 16KB 擋下] → 前端（`EventDetailPage.vue` `handleConfirmOrder`）每個選取的座位送一筆、每個計數票種送一筆；`Event.MaxTicketsPerOrder` 是 `int?`，未設定時前端沒有筆數上限，所以在兩個 change 之間，未設上限的活動一次選超過約 150 席會得到 413。接受此風險：後續 change 本來就把筆數限制為 50（超過 50 一樣被拒），兩個 change 預計連續 merge；16KB 對 50 筆最長欄位（約 5.3KB）留約 3 倍餘裕。
- [上限提早回 413 時用戶端仍在上傳，可能收到連線 reset 而不是 413] → 這是 HTTP/1.1 標準行為；測試用「超過 1 byte」讓本文已送完，RBL-007 則在送本文前讀回應，兩者都能穩定讀到 413。
- [座位圖 10MB 本文的資源耗用] → 以三層邊界限制：10MB 本文上限（讀取量）、50,000 席座位數上限（決策 7，Handler 處理量）、organizer 分區 10 次／60 秒（決策 8，單一 Organizer 的頻率）。授權早於三者，匿名者無法觸發 10MB 讀取。
  - 10MB 的依據：每席最長約 131 bytes（`ZoneCode`、`SeatNumber` 各 50 字元加 JSON 結構），50,000 席約 6.5MB；1MB 只容納約 7,700 席最長欄位座位，會擋到中大型場館。
  - 記憶體預算：被座位數上限擋下的請求仍會完整讀取並反序列化最多 10MB 本文（約 26 萬筆一般長度的座位物件，估計暫用數十 MB），但不跑逐筆驗證、不建 entity；通過驗證的請求最多 50,000 筆 entity 被 EF 追蹤。超過上限的請求因 `RuleForEach` 被跳過，單筆欄位的 `MaximumLength(50)` 也不會套用，單筆字串長度只由 10MB 本文總量封頂。仍接受的殘餘風險：rate limit 是 Fixed Window 計次，不限並發，同一 Organizer 可在同一視窗開頭並發送出 10 個 10MB 請求，且在視窗交界前後各用滿額度時，短時間內最多 20 個；額度存在 process 記憶體，以單一 `api` instance 計，現行 compose 只有一個 instance 時成立，日後水平擴展到 N 個 instance 時有效額度變成 10 × N，需改用分散式限流（另案）；多個不同的 Approved Organizer 可各自用滿額度。兩者都需要經核准的 Organizer 身分，且可在 Seq 追溯到 OrganizerId。
  - 不在本 change：以座位圖建立活動時會為每個座位建立 `EventSeat`，50,000 席座位圖會在建立活動時寫入 50,000 筆；建立活動端點的行為不變。
  - 重新評估的觸發條件：Seq 中建立座位圖端點出現 413 或座位數超過上限的 400（代表合法場館需要更大上限），或出現 429（額度可能太緊），或該端點請求耗時／容器記憶體出現異常尖峰。記憶體估算以 tasks 7.2 的實測記錄為準。
- [`BadHttpRequestException` 在 TestServer 以外的路徑行為與預期不同] → task 1.1 先實測修正前行為，確認決策 4「實測證據」最後一段所列的前提，不符即停。
- [Kestrel 的 408／連線關閉行為隨版本變動] → RBL-010 斷言 408（決策 4 實測證據，SDK 10.0.401）；升級 ASP.NET Core 後若 RBL-010 失敗，先重做決策 4 的實驗確認是框架行為改變，再決定更新 spec 或程式。

## Migration Plan

- 部署：無 schema 變更；`appsettings.json` 新增 `SeatMapRateLimiting` 區段，缺漏時採用相同預設值，不需改環境變數。
- 回滾：還原版本即回到 Kestrel 預設 30MB 與全部 500 的行為，無資料影響。
- 部署前通知直接呼叫 API 的客戶端（proposal BREAKING 說明）。
