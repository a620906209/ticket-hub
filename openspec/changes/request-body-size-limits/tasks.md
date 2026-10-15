# Tasks

> 所有指令都在容器內執行（例如 `docker compose exec api dotnet test`），不在本機執行 dotnet。
>
> 本 change 沒有 AC 測試對應例外：所有 AC 都以 xUnit 涵蓋。整合測試用 Testcontainers，不連開發用的 db 服務；7.1 的 compose 實測是額外的部署形態確認，不取代任何 AC 測試。
>
> `ValidatorTests` 指 `tests/ProjectC.Application.Tests/Venues/CreateSeatMap/CreateSeatMapRequestValidatorTests.cs`（新檔）；`HandlerTests(座位圖)` 指同目錄既有的 `CreateSeatMapHandlerTests.cs`；`SeatMapRateLimitingTests` 指 `tests/ProjectC.WebApi.Tests/Admin/SeatMapRateLimitingTests.cs`（新檔，用自己的小額度 factory）；`SeatMapRateLimitingOptionsTests` 指 `tests/ProjectC.Application.Tests/Common/SeatMapRateLimitingOptionsTests.cs`（比照 `CaptchaRateLimitingOptionsTests`）。
>
> 以下 `KestrelTests` 指 `tests/ProjectC.WebApi.Tests/RequestBodyLimits/RequestBodyLimitsKestrelTests.cs`（使用 `KestrelWebApplicationFactory`，design 決策 5）；`HandlerTests` 指 `tests/ProjectC.WebApi.Tests/ExceptionHandling/GlobalExceptionHandlerTests.cs`（直接建構 `GlobalExceptionHandler` 與 `DefaultHttpContext` 的單元測試，不啟動主機）。

## AC 與測試對應

| AC | 測試檔／方法 | 任務 |
| --- | --- | --- |
| RBL-001 | `KestrelTests`：`Login_WhenBodyExceedsGlobalLimit_Returns413`（本文 `GlobalBytes + 1`，斷言 413、不是 500） | 1.1、3.1 |
| RBL-002 | `KestrelTests`：`Login_WhenBodyEqualsGlobalLimit_IsNotRejectedForSize`（本文恰為 `GlobalBytes`、不是有效登入資料；斷言 4xx 且不是 413） | 3.1 |
| RBL-003 | `KestrelTests`：`PlaceOrder_WhenBodyExceedsEndpointLimit_Returns413AndCreatesNoOrder`（本文 `PlaceOrderBytes + 1`，內容是可建立的計數訂單；斷言 413、訂單筆數與該票種可售量不變） | 3.2 |
| RBL-004 | `KestrelTests`：`PlaceOrder_WhenBodyEqualsEndpointLimit_CreatesOrder`（Arrange 沿用既有成功下單測試的前置：符合下單條件的會員、可售活動、庫存足夠的計數票種、數量在 `MaxTicketsPerOrder` 內；同一筆計數項目在 JSON 結尾 `}` 之後以空白補足到恰為 `PlaceOrderBytes`。先送一次未補空白的同內容本文到另一個相同條件的活動，斷言成功，作為「payload 本身符合業務規則」的對照；再送補足長度的本文，斷言建立成功。對照失敗時測試失敗訊息須指出是前置資料問題，不是大小上限） | 3.2 |
| RBL-014 | `KestrelTests`：`PlaceOrder_WhenUnauthenticatedAndBodyTooLarge_Returns401`（未登入、本文 `PlaceOrderBytes + 1`；斷言 401、不是 413） | 3.2 |
| RBL-005 | `KestrelTests`：`CreateSeatMap_WhenBodyBetweenGlobalAndEndpointLimit_Returns201`（有效座位圖，本文大於 `GlobalBytes`；斷言 201 且座位筆數等於送出數量） | 3.2 |
| RBL-006 | `KestrelTests`：`CreateSeatMap_WhenBodyExceedsEndpointLimit_Returns413AndCreatesNoSeatMap`（本文 `CreateSeatMapBytes + 1`；斷言 413、該場館沒有座位） | 3.2 |
| RBL-007 | `KestrelTests`：`Login_WhenDeclaredContentLengthExceedsLimit_Returns413BeforeBodySent`（`TcpClient`，只送標頭 `Content-Length: 2097152`，不送本文即讀回應；斷言 413） | 3.3 |
| RBL-008 | `KestrelTests`：`Login_WhenChunkedBodyExceedsLimit_Returns413`（`TcpClient`，chunked 分段合計 `GlobalBytes + 1`；斷言 413） | 3.3 |
| RBL-009 | `KestrelTests`：`Login_WhenContentLengthSmallerThanSentBytes_TreatsOnlyDeclaredBytesAsBody`（`TcpClient`，`Content-Length: 2` 送 `{}` 後接 `GlobalBytes + 1` bytes 額外資料；斷言第一個回應是登入端點對 `{}` 的 4xx、不是 413／500；`LogSink` 沒有 Error 等級日誌；額外資料用 `A` 重複（不構成合法請求行）；請求開始前 `LogSink.Clear()`，結束後 `LogSink` 中**不分路徑**的 Serilog 請求摘要日誌（`UseSerilogRequestLogging` 每個進入應用程式 pipeline 的請求一筆）恰好一筆，且其 `RequestPath` 為 `/api/auth/login`、狀態碼不是 413／500，證明只有第一個請求進入應用程式、額外資料沒有成為第二個請求或第一個請求的本文。第一個回應依其 `Content-Length`／chunked 結尾精確讀取，不把後續位元組併入；額外資料之後若讀到 Kestrel 的 400 或連線關閉都接受，只要求不是 500。Kestrel 可能因第二個請求解析失敗而關閉連線，所以寫入額外資料時的 `IOException`／reset 不視為失敗，第一個回應仍須讀到） | 3.3 |
| RBL-010 | `KestrelTests`：`Login_WhenClientStallsBeforeDeclaredLength_EndsRequestWithin30Seconds`（`TcpClient`，`Content-Length: 1000` 只送 10 bytes 有效 JSON 前綴 `{"email":"` 後停止、不關連線；斷言 30 秒內讀到 408、回應為 ProblemDetails 且含 `traceId`；連線關閉而沒有回應視為失敗。日誌依 design 決策 5 的範圍斷言：`GlobalExceptionHandler` 恰好一筆 Warning 且 `TraceId` 等於回應 `traceId`、任何 category 都沒有 Error／Fatal、所有日誌的渲染文字與屬性值都不含送出的 JSON 前綴中的哨兵字串（前綴改為 `{"email":"<哨兵>` 形式，長度仍小於 `Content-Length`）） | 3.3 |
| RBL-015 | `KestrelTests`：`Login_WhenChunkedEncodingMalformed_Returns400ProblemDetailsAndLogsWarning`（Theory，`TcpClient`：chunk size 為 `ZZ`／chunk 資料後缺 CRLF；兩種本文都含哨兵字串。斷言 400、ProblemDetails 的 `status` 400、`title` `Bad Request`、`traceId` 非空；日誌斷言同 RBL-010） | 3.3 |
| RBL-011 | `KestrelTests`：`PlaceOrder_WhenBodyTooLarge_ReturnsProblemDetails`（斷言 `status` 413、`title` `Request body too large.`、`traceId` 非空）；`HandlerTests`：`TryHandleAsync_WhenBadHttpRequest413_WritesProblemDetailsWith413` | 2.1、3.2 |
| RBL-012 | `KestrelTests`：`PlaceOrder_WhenBodyTooLarge_LogsWarningWithoutBody`（本文放唯一哨兵字串；依 design 決策 5 的日誌範圍斷言：`GlobalExceptionHandler` 恰好一筆 Warning 且 `TraceId` 等於回應 `traceId`、任何 category 都沒有 Error／Fatal、所有日誌的渲染文字與屬性值都不含哨兵字串）；`HandlerTests`：`TryHandleAsync_WhenBadHttpRequest_LogsWarningNotError`（Theory：413／400／408，斷言回應狀態碼等於例外狀態碼且只記 Warning） | 2.1、3.2 |
| RBL-013 | `HandlerTests`：`TryHandleAsync_WhenOtherException_Returns500AndLogsError`（`InvalidOperationException`，斷言 500、`title` `An unexpected error occurred.`、記 Error）；既有 `Admin/OrganizerScopingDataCorruptionTests` 的 500 測試維持通過 | 2.1、6.1 |
| EVT-VENUE-004 | `ValidatorTests`：`Validate_WhenSeatsExceedMax_ReturnsOnlyCountError`（`MaxSeats + 1` 席，其中含重複座位與空白 `ZoneCode`；斷言錯誤恰好一筆、屬性為 `Seats`、訊息為座位數上限）；`HandlerTests(座位圖)`：`HandleAsync_WhenSeatsExceedMax_ReturnsValidationWithoutTouchingRepositories`（斷言 `ErrorType.Validation`，`IVenueRepository.GetByIdAsync`、`ISeatMapRepository.Add`、`IUnitOfWork.BeginTransactionAsync` 都沒被呼叫）；`AdminVenuesControllerTests`：`CreateSeatMap_WhenSeatsExceedMax_Returns400AndCreatesNoSeatMap`（存在的場地，斷言 400、ValidationProblem 的錯誤只有 `Seats` 一個 key 且訊息為座位數上限、該場地座位圖數不變） | 4.1、4.2 |
| EVT-VENUE-005 | `ValidatorTests`：`Validate_WhenSeatsEqualMax_IsValid`（恰好 `MaxSeats` 席、欄位合法且不重複） | 4.1 |
| SRL-001 | `SeatMapRateLimitingTests`：`CreateSeatMap_WhenOrganizerExceedsLimit_Returns429ProblemDetailsAndCreatesNoSeatMap`（factory 設 `PermitLimit = 2`；先送 3 次未登入請求斷言皆 401、再以已登入但未切換 Organizer 的帳號送 3 次斷言皆 403；再以同一 Organizer 送 2 次成功、第 3 次斷言 429、`status` 429、`title` 非空、`traceId` 非空、有 `Retry-After`，且該場地座位圖數等於 2——同時證明 401／403 不消耗額度；最後送一次 JSON 語法錯誤的本文（例如 `{"seats":`），斷言仍為 429 而不是 400，證明限流早於本文讀取） | 4.3 |
| SRL-002 | `SeatMapRateLimitingTests`：`CreateSeatMap_WhenOtherOrganizerExhaustedLimit_StillCreatesSeatMap`（Organizer A 用完額度、得到 429 後，Organizer B 的成員建立成功 201） | 4.3 |
| SRL-003 | `SeatMapRateLimitingOptionsTests`：`Bind_WhenSectionMissing_UsesDefaults`（`PermitLimit` 10、`WindowSeconds` 60） | 4.3 |
| SRL-004 | `SeatMapRateLimitingOptionsTests`：`Validate_WhenValueIsZeroOrNegative_Fails`（Theory）；`RateLimitingOptionsFailFastTests` 新增 `SeatMapRateLimiting` 的 0／負數啟動失敗 Theory | 4.3 |

## 0. 開工條件

- [ ] 0.1 確認目前分支為 `feature/request-body-size-limits` 且從最新 master 建立；design.md 決策 4「實測證據」最後一段所列、由 task 1.1 在真正應用程式上確認的前提尚未被推翻。驗證：`git log master..HEAD` 只有本 change 的 commit。

## 1. 修正前行為記錄

- [ ] 1.1 先建立 `KestrelWebApplicationFactory`（design 決策 5：繼承 `CustomWebApplicationFactory`、自行掛 `InMemoryLogEventSink`、`UseKestrel(0)`），寫 RBL-001 測試，但暫時把本文改為 `30_000_001` bytes（Kestrel 預設上限 + 1），在修正前執行。目的是確認 design 決策 4 的前提：Kestrel 的 `BadHttpRequestException` 會傳到 `GlobalExceptionHandler`。驗證：測試失敗，記錄實際狀態碼與 `LogSink` 中的日誌等級，預期為 500 + Error。
  - 若狀態碼是 413：代表例外沒有進 `GlobalExceptionHandler`，決策 4 的修改範圍不同，停下回報。
  - 若是 400 ValidationProblem：代表 MVC 把它轉成 ModelState 錯誤，決策 4 須重寫，停下回報。
  - 同時記錄 `LogSink` 中所有 Error 等級日誌的 `SourceContext`，預期只有 `GlobalExceptionHandler`（design 決策 4 實測證據顯示框架不記 Error）。若有其他 category 的 Error，停下回報，因為 RBL-012 的日誌範圍須調整。
  - 記錄後把本文改回 `GlobalBytes + 1`（此時在修正前預期得到 400 或 500，記錄實際值，不判定）。
- [ ] 1.2 確認 `KestrelWebApplicationFactory` 確實走 Kestrel：測試內斷言 factory 的 `HttpClient.BaseAddress` 是 `http://127.0.0.1:<port>` 或 `http://localhost:<port>`，且 `Server` 服務型別為 `KestrelServer`（`factory.Services.GetRequiredService<IServer>()`）；port 不等於 8080（測試通常在 `api` 容器內執行，容器設有 `ASPNETCORE_URLS=http://0.0.0.0:8080`，須確認 `UseKestrel(0)` 用隨機 port、沒有沿用環境變數的 8080 而與運作中的 api 衝突）。此測試須在 `api` 容器內實際執行一次。驗證：斷言通過；把 `UseKestrel` 拿掉時斷言失敗，驗證後還原。

## 2. GlobalExceptionHandler

- [ ] 2.1 依 design 決策 4 修改 `GlobalExceptionHandler`，新增 `HandlerTests` 的 3 個方法（AC 表 RBL-011～013）。驗證：
  - 3 個測試通過。
  - 反向驗證：把 `BadHttpRequestException` 分支拿掉時 RBL-011／012 的 `HandlerTests` 失敗；把分支條件改成攔截所有例外時 RBL-013 失敗。驗證後還原。
  - `HandlerTests` 的 Warning 斷言同時檢查日誌的訊息模板與屬性不含 `HttpContext.Request.Body` 的內容（測試在 Request.Body 放哨兵字串），且日誌項目沒有附帶例外物件、訊息不含例外 Message（測試建構的 `BadHttpRequestException` Message 放另一個哨兵字串）。

## 3. 本文上限設定與 Kestrel 整合測試

- [ ] 3.1 新增 `RequestBodyLimits` 常數類別與 `ConfigureKestrel` 全域上限（design 決策 1、2），寫 RBL-001、RBL-002 測試。驗證：兩個測試通過；反向驗證：拿掉 `ConfigureKestrel` 時 RBL-001 失敗；把全域上限改成 `GlobalBytes - 1` 時 RBL-002 失敗。驗證後還原。本文長度一律以 `Encoding.UTF8.GetByteCount` 斷言等於目標值。
- [ ] 3.2 在 `OrdersController.PlaceOrder`、`AdminVenuesController` 建立座位圖 action 加 `[RequestSizeLimit]`，寫 RBL-003～006、RBL-011、RBL-012、RBL-014 的 `KestrelTests`。驗證：
  - 全部通過。
  - 反向驗證：拿掉 PlaceOrder 的 attribute 時 RBL-003／011／012 失敗（本文 16,385 bytes 低於全域 1MB，會被正常處理）；拿掉座位圖的 attribute 時 RBL-005 失敗（被全域 1MB 擋下得到 413）。驗證後還原。
  - RBL-005 的座位數與每席長度在測試中算出，並斷言序列化後本文大於 `GlobalBytes`、座位數不超過 `SeatMapLimits.MaxSeats`，避免資料變動後退化成未超過全域上限的假通過，或被座位數上限擋下。
- [ ] 3.3 寫 RBL-007～010、RBL-015 的 `TcpClient` 測試（design 決策 3、5）。每個測試讀回應時設讀取逾時（RBL-010 為 30 秒，其他 10 秒），逾時視為失敗。驗證：
  - 全部通過。
  - RBL-007 反向驗證：把全域上限暫時調大到 `2097152` 以上時測試失敗（伺服器會等本文、讀取逾時）。驗證後還原。
  - RBL-008 反向驗證：把 chunked 合計改成 `GlobalBytes` 時測試失敗（不再是 413）。驗證後還原。
  - RBL-010／015 反向驗證：暫時拿掉 `GlobalExceptionHandler` 的 `BadHttpRequestException` 分支時兩者都失敗（回 500、記 Error）。驗證後還原。

## 4. 建立座位圖的座位數上限與 rate limit

- [ ] 4.1 新增 `SeatMapLimits.MaxSeats`，依 design 決策 7 修改 `CreateSeatMapRequestValidator`，新增 `ValidatorTests` 的 EVT-VENUE-004／005 測試。驗證：測試通過；反向驗證：拿掉 `RuleForEach` 的 `.When` 時 EVT-VENUE-004 的「只有一筆錯誤」斷言失敗；把上限改成 `MaxSeats - 1` 時 EVT-VENUE-005 失敗。驗證後還原。
- [ ] 4.2 寫 `HandlerTests(座位圖)` 與 `AdminVenuesControllerTests` 的 EVT-VENUE-004 測試（Handler 不改）。驗證：通過；反向驗證：暫時拿掉 Validator 的座位數規則時兩者都失敗（Handler 會查場地；整合測試因含重複座位得到非 400 或錯誤內容不是座位數）。驗證後還原。整合測試走 TestServer 即可（`[RequestSizeLimit]` 10MB 在 TestServer 上生效，50,001 席一般長度約 2MB）。
- [ ] 4.3 依 design 決策 8 新增 `SeatMapRateLimitingOptions`（註冊、啟動解析、`appsettings.json` 比照 `CaptchaRateLimitingOptions`）、`create-seat-map` policy 與 `[EnableRateLimiting]`；`CustomWebApplicationFactory` 加 `SeatMapRateLimiting:PermitLimit = 1000`；不繼承它、自行繼承 `WebApplicationFactory<Program>` 的 factory（至少 `ObservabilityWebApplicationFactory`、`LoginRateLimitedWebApplicationFactory`、`CaptchaRateLimitedWebApplicationFactory`，實作時以 `grep "WebApplicationFactory<Program>"` 列出全部）逐一確認：會透過 API 建座位圖的同樣加此覆寫，不會建的記錄理由；寫 SRL-001～004 測試。驗證：
  - 測試通過。
  - 反向驗證：拿掉 `[EnableRateLimiting]` 時 SRL-001 失敗（語法錯誤本文得到 400）；把分區鍵改成固定字串時 SRL-002 失敗；拿掉 `[Range]` 或啟動解析時 SRL-004 失敗。驗證後還原。
  - 跑一次全部 `ProjectC.WebApi.Tests`，確認既有建座位圖的測試沒有因額度得到 429（`CustomWebApplicationFactory` 的覆寫有效）。

## 5. 防禦性檢查

- [ ] 5.1 第 4 節改到 Application 層（`CreateSeatMapRequestValidator`、新常數與設定類別），所有程式碼變更完成後依 CLAUDE.md「防禦性檢查規則」套用一次 `.claude/skills/hardener/SKILL.md`，對象為 `CreateSeatMapRequestValidator` 與 `CreateSeatMapHandler` 的驗證路徑。驗證：記錄清單每一項的結論；若因此修改程式碼，重跑第 4 節測試。`git diff --stat master` 只列出 `src/ProjectC.WebApi/**`、`src/ProjectC.Application/**`（僅上述檔案）、`tests/**` 與本 change 的 openspec 檔案；出現 Domain／Infrastructure 變更時停下回報。

## 6. 全部測試

- [ ] 6.1 執行全部後端測試（`docker compose exec api dotnet test`）。驗證：全部通過，沒有 skipped；既有測試（含 `OrganizerScopingDataCorruptionTests` 的 500 測試、k6 seeder 建 2000 席座位圖相關測試）不受影響。若有失敗，先在乾淨 master 重跑確認是否為既有 flake（`docs/project-scope.md` §8 記錄的 WSL2 時鐘問題），是則記錄、不是則停下回報。

## 7. Docker Compose 部署 smoke test

> 7.1 是部署形態的 smoke test，只確認 compose wiring（環境變數、port 映射、Serilog 輸出）下真正的 `api` 容器有套用上限。它**不是** AC 驗證、不取代第 1～4 節任何自動化測試；任何 RBL 的通過與否以 xUnit 結果為準。

- [ ] 7.1 `docker compose up -d api` 後依 design 決策 6 實測，完成紀錄附上每個指令的狀態碼與回應本文。登入端點有 login rate limit（預設 5 次／60 秒，429 早於本文讀取），以下 3 次登入請求在同一個 60 秒內未超過；若任何一次得到 429，等 60 秒後重送並在紀錄註明，不把 429 當成通過：
  - 容器內：`docker compose exec api sh -c "head -c 1048577 /dev/zero | tr '\0' ' ' | curl -s -o - -w '%{http_code}' -H 'Content-Type: application/json' --data-binary - http://localhost:8080/api/auth/login"`，預期 413 與 ProblemDetails（`title` `Request body too large.`）。
  - 容器內：同一指令改為 1,048,576 bytes，預期不是 413 也不是 500。
  - 宿主：以 `${API_HOST_PORT:-8080}` 對外 port 重送 1,048,577 bytes，預期 413。
  - `docker compose logs api` 確認對應的 Warning 日誌、沒有 Error。
- [ ] 7.2 記憶體估算實測（design Risks「記憶體預算」，不是 AC）：`api` 容器內以登入取得一個 Approved Organizer 的 token，並發送出 10 個接近 10MB、`Seats` 超過 `SeatMapLimits.MaxSeats` 的建立座位圖請求，同時以 `docker stats --no-stream` 每秒取樣 `api` 記憶體。完成紀錄附上請求前基準、峰值與 10 個請求的狀態碼（預期皆 400，額度內）。若容器被 OOM kill、出現 500，或峰值比基準增加超過 1GB，停下回報，重新檢討 design 的上限值。

## Workflow follow-up

- 實作完成後呼叫 `strict-reviewer`，通過後 archive 本 change（同步新的 `request-body-limits` 主 spec，以及 `event-management`、`api-rate-limiting` 的 delta），merge 回 master。
- merge 後才開始 `place-order-request-limits` 的 task 0.1。
