# Tasks

> 所有指令都在容器內執行（例如 `docker compose exec api dotnet test`），不在本機執行 dotnet。
>
> 本 change 沒有 AC 測試對應例外：所有 AC 都以 xUnit 涵蓋。整合測試用 Testcontainers，不連開發用的 db 服務；6.1 的 compose 實測是額外的部署形態確認，不取代任何 AC 測試。
>
> 以下 `KestrelTests` 指 `tests/ProjectC.WebApi.Tests/RequestBodyLimits/RequestBodyLimitsKestrelTests.cs`（使用 `KestrelWebApplicationFactory`，design 決策 5）；`HandlerTests` 指 `tests/ProjectC.WebApi.Tests/ExceptionHandling/GlobalExceptionHandlerTests.cs`（直接建構 `GlobalExceptionHandler` 與 `DefaultHttpContext` 的單元測試，不啟動主機）。

## AC 與測試對應

| AC | 測試檔／方法 | 任務 |
| --- | --- | --- |
| RBL-001 | `KestrelTests`：`Login_WhenBodyExceedsGlobalLimit_Returns413`（本文 `GlobalBytes + 1`，斷言 413、不是 500） | 1.1、3.1 |
| RBL-002 | `KestrelTests`：`Login_WhenBodyEqualsGlobalLimit_IsNotRejectedForSize`（本文恰為 `GlobalBytes`、不是有效登入資料；斷言 4xx 且不是 413） | 3.1 |
| RBL-003 | `KestrelTests`：`PlaceOrder_WhenBodyExceedsEndpointLimit_Returns413AndCreatesNoOrder`（本文 `PlaceOrderBytes + 1`，內容是可建立的計數訂單；斷言 413、訂單筆數與該票種可售量不變） | 3.2 |
| RBL-004 | `KestrelTests`：`PlaceOrder_WhenBodyEqualsEndpointLimit_CreatesOrder`（同一筆計數項目以空白補足到恰為 `PlaceOrderBytes`；斷言建立成功） | 3.2 |
| RBL-014 | `KestrelTests`：`PlaceOrder_WhenUnauthenticatedAndBodyTooLarge_Returns401`（未登入、本文 `PlaceOrderBytes + 1`；斷言 401、不是 413） | 3.2 |
| RBL-005 | `KestrelTests`：`CreateSeatMap_WhenBodyBetweenGlobalAndEndpointLimit_Returns201`（有效座位圖，本文大於 `GlobalBytes`；斷言 201 且座位筆數等於送出數量） | 3.2 |
| RBL-006 | `KestrelTests`：`CreateSeatMap_WhenBodyExceedsEndpointLimit_Returns413AndCreatesNoSeatMap`（本文 `CreateSeatMapBytes + 1`；斷言 413、該場館沒有座位） | 3.2 |
| RBL-007 | `KestrelTests`：`Login_WhenDeclaredContentLengthExceedsLimit_Returns413BeforeBodySent`（`TcpClient`，只送標頭 `Content-Length: 2097152`，不送本文即讀回應；斷言 413） | 3.3 |
| RBL-008 | `KestrelTests`：`Login_WhenChunkedBodyExceedsLimit_Returns413`（`TcpClient`，chunked 分段合計 `GlobalBytes + 1`；斷言 413） | 3.3 |
| RBL-009 | `KestrelTests`：`Login_WhenContentLengthSmallerThanSentBytes_TreatsOnlyDeclaredBytesAsBody`（`TcpClient`，`Content-Length: 2` 送 `{}` 後接 `GlobalBytes + 1` bytes 額外資料；斷言第一個回應是登入端點對 `{}` 的 4xx、不是 413／500；`LogSink` 沒有 Error 等級日誌；`LogSink` 中 `RequestPath` 為 `/api/auth/login` 的請求摘要日誌恰好一筆且狀態碼不是 413／500，證明額外資料沒有被當作同一請求的本文。額外資料被 Kestrel 解析成第二個請求時的回應不斷言，只要求不是 500。Kestrel 可能因第二個請求解析失敗而關閉連線，所以寫入額外資料時的 `IOException`／reset 不視為失敗，第一個回應仍須讀到） | 3.3 |
| RBL-010 | `KestrelTests`：`Login_WhenClientStallsBeforeDeclaredLength_EndsRequestWithin30Seconds`（`TcpClient`，`Content-Length: 1000` 只送 10 bytes 後停止、不關連線；斷言 30 秒內讀到 4xx 回應或連線被關閉，若有回應不是 500） | 3.3 |
| RBL-011 | `KestrelTests`：`PlaceOrder_WhenBodyTooLarge_ReturnsProblemDetails`（斷言 `status` 413、`title` `Request body too large.`、`traceId` 非空）；`HandlerTests`：`TryHandleAsync_WhenBadHttpRequest413_WritesProblemDetailsWith413` | 2.1、3.2 |
| RBL-012 | `KestrelTests`：`PlaceOrder_WhenBodyTooLarge_LogsWarningWithoutBody`（本文放唯一哨兵字串；斷言一筆 Warning 的 `TraceId` 等於回應 `traceId`、沒有 Error 等級日誌、所有日誌的渲染文字與屬性值都不含哨兵字串）；`HandlerTests`：`TryHandleAsync_WhenBadHttpRequest_LogsWarningNotError`（Theory：413／400／408，斷言回應狀態碼等於例外狀態碼且只記 Warning） | 2.1、3.2 |
| RBL-013 | `HandlerTests`：`TryHandleAsync_WhenOtherException_Returns500AndLogsError`（`InvalidOperationException`，斷言 500、`title` `An unexpected error occurred.`、記 Error）；既有 `Admin/OrganizerScopingDataCorruptionTests` 的 500 測試維持通過 | 2.1、5.1 |

## 0. 開工條件

- [ ] 0.1 確認目前分支為 `feature/request-body-size-limits` 且從最新 master 建立；design.md 的決策 4「待 task 1.1 確認的前提」尚未被推翻。驗證：`git log master..HEAD` 只有本 change 的 commit。

## 1. 修正前行為記錄

- [ ] 1.1 先建立 `KestrelWebApplicationFactory`（design 決策 5：繼承 `CustomWebApplicationFactory`、自行掛 `InMemoryLogEventSink`、`UseKestrel(0)`），寫 RBL-001 測試，但暫時把本文改為 `30_000_001` bytes（Kestrel 預設上限 + 1），在修正前執行。目的是確認 design 決策 4 的前提：Kestrel 的 `BadHttpRequestException` 會傳到 `GlobalExceptionHandler`。驗證：測試失敗，記錄實際狀態碼與 `LogSink` 中的日誌等級，預期為 500 + Error。
  - 若狀態碼是 413：代表例外沒有進 `GlobalExceptionHandler`，決策 4 的修改範圍不同，停下回報。
  - 若是 400 ValidationProblem：代表 MVC 把它轉成 ModelState 錯誤，決策 4 須重寫，停下回報。
  - 同時記錄 `LogSink` 中所有 Error 等級日誌的 `SourceContext`。修正後（2.1 完成）在 3.1 再看一次：若仍有 `GlobalExceptionHandler` 以外的框架 Error 日誌（例如 `ExceptionHandlerMiddleware`），停下回報，因為 RBL-012「不記錄 Error」須調整。
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
  - RBL-005 的座位數與每席長度在測試中算出，並斷言序列化後本文大於 `GlobalBytes`，避免資料變動後退化成未超過全域上限的假通過。
- [ ] 3.3 寫 RBL-007～010 的 `TcpClient` 測試（design 決策 3、5）。每個測試讀回應時設讀取逾時（RBL-010 為 30 秒，其他 10 秒），逾時視為失敗。驗證：
  - 全部通過。
  - RBL-007 反向驗證：把全域上限暫時調大到 `2097152` 以上時測試失敗（伺服器會等本文、讀取逾時）。驗證後還原。
  - RBL-008 反向驗證：把 chunked 合計改成 `GlobalBytes` 時測試失敗（不再是 413）。驗證後還原。

## 4. 防禦性檢查

- [ ] 4.1 本 change 只動 WebApi 層（Program.cs、Controller attribute、GlobalExceptionHandler），不屬於 CLAUDE.md「防禦性檢查規則」適用的 Application／Repository 層，不套用 hardener。若實作時改到 Application 或 Repository 層，停下回報（超出本 change 範圍）。驗證：`git diff --stat master` 只列出 `src/ProjectC.WebApi/**`、`tests/ProjectC.WebApi.Tests/**` 與本 change 的 openspec 檔案。

## 5. 全部測試

- [ ] 5.1 執行全部後端測試（`docker compose exec api dotnet test`）。驗證：全部通過，沒有 skipped；既有測試（含 `OrganizerScopingDataCorruptionTests` 的 500 測試、k6 seeder 建 2000 席座位圖相關測試）不受影響。若有失敗，先在乾淨 master 重跑確認是否為既有 flake（`docs/project-scope.md` §8 記錄的 WSL2 時鐘問題），是則記錄、不是則停下回報。

## 6. Docker Compose 容器實測

- [ ] 6.1 `docker compose up -d api` 後依 design 決策 6 實測，完成紀錄附上每個指令的狀態碼與回應本文。登入端點有 login rate limit（預設 5 次／60 秒，429 早於本文讀取），以下 3 次登入請求在同一個 60 秒內未超過；若任何一次得到 429，等 60 秒後重送並在紀錄註明，不把 429 當成通過：
  - 容器內：`docker compose exec api sh -c "head -c 1048577 /dev/zero | tr '\0' ' ' | curl -s -o - -w '%{http_code}' -H 'Content-Type: application/json' --data-binary @- http://localhost:8080/api/auth/login"`，預期 413 與 ProblemDetails（`title` `Request body too large.`）。
  - 容器內：同一指令改為 1,048,576 bytes，預期不是 413 也不是 500。
  - 宿主：以 `${API_HOST_PORT:-8080}` 對外 port 重送 1,048,577 bytes，預期 413。
  - `docker compose logs api` 確認對應的 Warning 日誌、沒有 Error。

## Workflow follow-up

- 實作完成後呼叫 `strict-reviewer`，通過後 archive 本 change（同步新的 `request-body-limits` 主 spec），merge 回 master。
- merge 後才開始 `place-order-request-limits` 的 task 0.1。
