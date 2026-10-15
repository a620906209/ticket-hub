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
- 兩個端點個別覆寫：建立訂單收緊、建立座位圖放寬。
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

RBL-007～010 全部由 Kestrel 的 HTTP/1.1 解析器處理：`Content-Length` 超過上限時第一次讀取即丟 `BadHttpRequestException`(413)，不等本文；chunked 以累計讀取量判斷；`Content-Length` 以後的位元組被當成下一個請求的開頭（RBL-009 只要求「不被當作此請求本文、不 500」）；本文傳送停滯由 `MinRequestBodyDataRate`（預設 240 bytes/s、5 秒 grace period）中止並回 408 或關閉連線。這些行為以測試鎖住，不在應用程式重寫。

### 4. `GlobalExceptionHandler` 依 `BadHttpRequestException.StatusCode` 回應

- 所有 `Microsoft.AspNetCore.Http.BadHttpRequestException`（Kestrel 與 MVC 丟出的型別都繼承自它）一律依其 `StatusCode` 回應，不只本文讀取階段；其他來源丟出的 `BadHttpRequestException`（例如參數讀取失敗）同樣由原本的 500 改為其 4xx，這是刻意的一致化：`StatusCode` 由框架判定為請求錯誤，回 500 本來就不正確。回應規則：`Status = ex.StatusCode`；`title` 在 413 時為 `Request body too large.`，其他狀態碼用 `ReasonPhrases.GetReasonPhrase(statusCode)`；`traceId` extension 與既有寫法相同。
- 記錄 `LogWarning`，模板只帶狀態碼、HTTP method、路徑樣板與 traceId；**不傳入例外物件、不帶例外訊息**（MVC 版的 Message／InnerException 可能含 Content-Type 值或 JSON 路徑等請求細節），也不讀取、不記錄本文。攻擊者可大量觸發 413 產生 Warning，每筆日誌很小，接受此日誌量放大。
- 其他例外維持原本 500 + `LogError`，不改。
- 若回應已開始送出（`HttpContext.Response.HasStarted`），沿用既有處理器的行為（框架不會再寫入）。
- 替代方案：用 `UseStatusCodePages` 或在 controller 捕捉 —— 例外發生在模型繫結、controller 之前，捕捉不到；也違反「Controller 內不散落 try-catch」，捨棄。
- **待 task 1.1 確認的前提**：`BadHttpRequestException` 會穿過 MVC 的 `SystemTextJsonInputFormatter`／`BodyModelBinder` 傳到 `GlobalExceptionHandler`（formatter 預設 `ExceptionPolicy = MalformedInputFormatterException`，只把 `InputFormatterException` 轉為 ModelState）。若 task 1.1 實測發現超過上限被轉成 400 ValidationProblem 而非 500，停止並回報，因為需要改用不同的攔截點，Decision 4 須重寫。

### 5. 測試以 `UseKestrel()` 的專用 factory 跑真正 Kestrel

- 新增 `KestrelWebApplicationFactory : CustomWebApplicationFactory`（沿用 Postgres／Redis Testcontainers），比照 `LogCapturingWebApplicationFactory` 自行在 `ConfigureServices` 加 `AddSingleton<ILogEventSink>(LogSink)`；不移除既有類別的 `sealed`，避免動到既有測試基礎設施。建構時呼叫 `UseKestrel(0)`（隨機 port），測試以 factory 提供的 `HttpClient`（`BaseAddress` 指向真正 port）呼叫。
- 為何不用 TestServer：TestServer 不套用 Kestrel 全域上限，RBL-001／002／007～010 會假通過或假失敗。`[RequestSizeLimit]` 在 TestServer 上雖可生效，但為一致起見所有 RBL 測試都走 Kestrel。
- RBL-007～010 用 `TcpClient` 直接寫 HTTP/1.1 原始位元組，才能偽造 `Content-Length`、控制 chunked 分段與停止傳送；RBL-007 只送標頭後就讀回應，以「讀到 413 前沒送任何本文」證明不等本文。RBL-010 斷言在 30 秒內讀到 4xx 或連線被關閉（含 `IOException`／讀到 0 bytes）。
- 「超過上限 1 byte」的本文：用有效 JSON 加空白補足到精確長度，排除 JSON 格式錯誤造成的 400 干擾；長度用 `Encoding.UTF8.GetByteCount` 斷言，避免算錯而假通過。
- 「不建立訂單／座位圖、庫存不變」用 factory 的 DbContext 查詢斷言。
- RBL-012 用 `LogSink` 斷言：存在一筆 Warning、`TraceId` 屬性等於回應的 `traceId`；不存在任何 Error 等級日誌（含框架的 `ExceptionHandlerMiddleware`；.NET 10 在 `IExceptionHandler` 回傳 true 時預設不再記 Error，由 task 1.1 實測確認；若框架仍記 Error，停下回報，因為 spec RBL-012 須調整）；所有日誌渲染文字與屬性值都不含本文的哨兵字串（本文中放一段唯一的哨兵字串）。
- RBL-013 沿用既有 500 測試（若無則以注入一個會丟例外的 test double 新增）。

### 6. Docker Compose 容器實測

`docker compose up -d api` 後，在 `api` 容器內以 `curl` 對 `http://localhost:8080` 送超過上限的本文（容器內 `localhost` 指容器自身，此處正是要打自己的 Kestrel；這與連線字串禁止 localhost 的規則不衝突），確認 413 + ProblemDetails；再從宿主以 `${API_HOST_PORT:-8080}` 送一次，確認對外 port 同樣生效。結果記錄在 tasks.md。

## 安全確認

- **輸入驗證**：本文大小在 Kestrel（`MaxRequestBodySize`）與 MVC filter（`[RequestSizeLimit]`）層限制，發生在模型繫結與 FluentValidation 之前；既有 Validator 不變。
- **SQL／shell 拼接**：無。本 change 不新增任何查詢或指令。
- **資料庫**：不新增查詢，無參數化或 N+1 議題；被 413 拒絕的請求不會進到 Handler，不開 DB 連線。
- **權限**：不改任何授權規則。`OrdersController` 沿用 `[Authorize]`、建立座位圖沿用 `RequireOrganizerContext` policy；授權在 `UseAuthorization` 執行，早於 `[RequestSizeLimit]` filter，未授權請求先得到 401／403（RBL-014）。全域上限不分身分，匿名端點同樣受保護。
- **日誌**：413 等請求錯誤記 Warning，只帶狀態碼、method、路徑樣板、traceId，不讀取也不記錄本文（RBL-012）。
- **前端**：不變，沒有新的 DOM 渲染或 API 呼叫。

## Risks / Trade-offs

- [`place-order-request-limits` merge 前，selections 超過約 150 筆的訂單會被 16KB 擋下] → 前端（`EventDetailPage.vue` `handleConfirmOrder`）每個選取的座位送一筆、每個計數票種送一筆；`Event.MaxTicketsPerOrder` 是 `int?`，未設定時前端沒有筆數上限，所以在兩個 change 之間，未設上限的活動一次選超過約 150 席會得到 413。接受此風險：後續 change 本來就把筆數限制為 50（超過 50 一樣被拒），兩個 change 預計連續 merge；16KB 對 50 筆最長欄位（約 5.3KB）留約 3 倍餘裕。
- [上限提早回 413 時用戶端仍在上傳，可能收到連線 reset 而不是 413] → 這是 HTTP/1.1 標準行為；測試用「超過 1 byte」讓本文已送完，RBL-007 則在送本文前讀回應，兩者都能穩定讀到 413。
- [座位圖 10MB 仍可讓 Approved Organizer 送大本文，且並發送多個] → 已知缺口：建立座位圖端點目前**沒有 rate limit**（`AdminVenuesController` 無 `[EnableRateLimiting]`，`Program.cs` 只有 place-order／confirm-order／login／captcha 四個 policy）。唯一邊界是需已切換至 Approved Organizer 的授權，且授權早於 `[RequestSizeLimit]`，匿名者無法觸發 10MB 讀取。座位數上限與此端點的 rate limit 另案處理，不在本 change。
- [`BadHttpRequestException` 在 TestServer 以外的路徑行為與預期不同] → task 1.1 先實測修正前行為，Decision 4 標記為前提，不符即停。
- [Kestrel 的 408／連線關閉行為隨版本變動] → RBL-010 只斷言「30 秒內結束、非 500」，不綁特定狀態碼。

## Migration Plan

- 部署：無 schema 或設定檔變更，直接部署新版即可。
- 回滾：還原版本即回到 Kestrel 預設 30MB 與全部 500 的行為，無資料影響。
- 部署前通知直接呼叫 API 的客戶端（proposal BREAKING 說明）。
