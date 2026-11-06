## 1. 框架層錯誤 Title 改為機器代碼

對應 AER-001～AER-004（1.2、1.3）、AER-005（1.5）、RBL-011、HC-BODY-EXCEPTION。1.2 保留既有「500 不含 `detail`」的斷言。RBL-012、RBL-013、RBL-015、HC-BODY-HEALTHY、HC-BODY-UNHEALTHY 行為不變，由既有測試守護，6.1 全套重跑；其中 RBL-015 的 400 `title` 斷言（`RequestBodyLimitsKestrelTests` 現為 `Bad Request`）隨 1.2 改為 `BadRequest`。

- [x] 1.1 全專案（`src/`、`tests/`、`web/src`、`k6` 或壓測腳本、`docs/`）搜尋舊字串 `An unexpected error occurred.`、`Request body too large.`，列出清單於本 task 下，確認沒有遺漏的消費者
- [x] 1.2 TDD（RED）：修改 `GlobalExceptionHandlerTests` 的既有斷言為新代碼（500 → `InternalServerError`、413 → `PayloadTooLarge`、408 → `RequestTimeout`、400 → `BadRequest`），新增非 408／413／400 的狀態碼測試（431 → `RequestHeaderFieldsTooLarge`；418 → `Imateapot`，驗證標點被移除）與「無 reason phrase 的狀態碼 → `BadRequest`」測試（須選用框架確實回傳空字串的狀態碼）；同步修改 `RequestBodyLimitsKestrelTests`（413、400）、`HealthEndpointTestSupport`、`OrganizerScopingDataCorruptionTests` 的舊 `Title` 斷言。在容器內執行相關測試，預期 RED：Expected 為新代碼、Actual 為舊句子
- [x] 1.3 GREEN：修改 `GlobalExceptionHandler`，以 `switch` 對照 413／408／400，其餘狀態碼用 reason phrase 僅保留英數字元、空字串退回 `BadRequest`；500 改為 `InternalServerError`。重跑 1.2 的測試，預期全數通過
- [x] 1.4 重構（若有）後重跑 1.2 的測試；無則標 N/A
- [x] 1.5 AER-005（429 的 `title` 維持 `TooManyRequests`）：本 change 不修改限流程式碼，由既有 `LoginRateLimitingTests`（斷言 `title == "TooManyRequests"`）守護；GREEN 後重跑該測試類別並把輸出摘要行記入下方完成紀錄。若實作過程動到 `RateLimitingServiceCollectionExtensions.cs`，改為先新增斷言 429 `title` 的測試再修改

完成紀錄（RED／GREEN／重構，依 CLAUDE.md「證據貼原文」）：

- 1.1 搜尋結果（`src/`、`tests/`、`web/src`、`loadtest/`、`crosstest/`、`tools/`、`scripts/`、`docs/`；排除 bin/obj/node_modules/archive）：
  - `src/ProjectC.WebApi/ExceptionHandling/GlobalExceptionHandler.cs`（L52、L68）→ 已改
  - `tests/.../GlobalExceptionHandlerTests.cs`（L19、34、41、78）、`RequestBodyLimitsKestrelTests.cs`（L143、L329 的 `Bad Request`）、`HealthEndpointTestSupport.cs`（L100）、`OrganizerScopingDataCorruptionTests.cs`（L31）→ 已改
  - `docs/api-conventions.md`（L46）→ 4.1 已改
  - `openspec/specs/api-health-checks/spec.md`、`openspec/specs/request-body-limits/spec.md` → 由本 change 的 delta 覆蓋（歸檔時同步）
  - `web/src`、`loadtest/`、`crosstest/`、`tools/`、`scripts/`：無符合（無遺漏消費者）
- RED（正式程式碼未改，只改測試；命令：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~GlobalExceptionHandlerTests|FullyQualifiedName~RequestBodyLimitsKestrelTests|FullyQualifiedName~HealthEndpoint|FullyQualifiedName~OrganizerScopingDataCorruptionTests"`，輸出經 grep 過濾，下列為過濾後的原文行，部分以 `...` 省略命名空間）：
  ```
  Failed ...GlobalExceptionHandlerTests.TryHandleAsync_WhenOtherException_Returns500AndLogsError
     Expected problemDetails.RootElement.GetProperty("title").GetString() to be a match with the expectation, but it differs at index 0:
    "An unexpected error occurred."
  Failed ...TryHandleAsync_WhenBadHttpRequest_LogsWarningNotError(statusCode: 413, expectedTitle: "PayloadTooLarge")  differs at index 0
  Failed ...(statusCode: 431, expectedTitle: "RequestHeaderFieldsTooLarge")  differs at index 7
  Failed ...(statusCode: 408, expectedTitle: "RequestTimeout")  differs at index 7
  Failed ...(statusCode: 499, expectedTitle: "BadRequest")  differs at index 0
  Failed ...(statusCode: 418, expectedTitle: "Imateapot")  differs at index 1
  Failed ...(statusCode: 400, expectedTitle: "BadRequest")  differs at index 3
  Failed ...TryHandleAsync_WhenBadHttpRequest413_WritesProblemDetailsWith413  differs at index 0
  Failed ...OrganizerScopingDataCorruptionTests.*（8 個）Expected document...title... differs at index 0: "An unexpected error occurred."
  Failed ...RequestBodyLimitsKestrelTests.Login_WhenChunkedEncodingMalformed_Returns400ProblemDetailsAndLogsWarning（2 個）Expected problem.Title ... differs at index 3
  Failed ...RequestBodyLimitsKestrelTests.PlaceOrder_WhenBodyTooLarge_ReturnsProblemDetails  Expected problem.Title ... differs at index 0
  Failed!  - Failed:    20, Passed:    22, Skipped:     0, Total:    42, Duration: 22 s - ProjectC.WebApi.Tests.dll (net10.0)
  ```
  失敗原因：Actual 為舊句子／reason phrase，屬目標行為不符（非編譯或環境錯誤）。
- GREEN：修改 `GlobalExceptionHandler`（新增 `CreateBadRequestTitle`、500 改 `InternalServerError`）。第一次重跑仍有 1 個失敗：`(statusCode: 499, expectedTitle: "BadRequest")`，實際 Title 為 `ClientClosedRequest`——框架對 499 有 reason phrase，是測試選碼錯誤；改用無 reason phrase 的 450 後，含 1.5 的 `LoginRateLimitingTests` 重跑：
  ```
  Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 26 s - ProjectC.WebApi.Tests.dll (net10.0)
  ```
  （1.5：該次過濾含 `LoginRateLimitingTests`，429 `title == "TooManyRequests"` 斷言通過；未修改限流程式碼。）
- 重構後重跑（或 N/A）：N/A（無重構）

## 2. 業務錯誤回應帶 traceId

對應 AER-006～AER-008。

- [x] 2.1 TDD（RED）：新增測試——(a) 單元測試：對 `HttpContext.TraceIdentifier` 設為已知值的 `ActionContext` 執行業務失敗結果，斷言回應 body 的 `traceId` 等於該值，且 `status`、`title`、`detail` 不變、content-type 為 `application/problem+json`（AER-007）；(b) 整合測試：對任一會回 `NotFound` 的端點請求，斷言含非空 `traceId`（AER-006），連續兩次請求的 `traceId` 不同（AER-008）。預期 RED：`traceId` 屬性不存在
- [x] 2.2 GREEN：新增小型 `IActionResult` 實作並由 `ResultExtensions.CreateProblemResult` 回傳，執行時寫入 `traceId`（design 決策 2）；以實測確認 `ProblemDetailsFactory` 的 `traceId` 行為並把結果記入 design 決策 2 的註記。重跑 2.1 的測試
- [x] 2.3 重構（若有）後重跑 2.1 的測試與 `ToActionResult` 相關的既有 Controller 整合測試；無則標 N/A

完成紀錄：

- RED（新增 `ResultExtensionsTests` 與 `EventsControllerTests.GetTicketTypes_WithNonExistentEvent_ReturnsDistinctTraceIdPerRequest`，正式程式碼未改）：
  ```
  Failed ProjectC.WebApi.Tests.Common.ResultExtensionsTests.ToActionResult_WhenResultFailed_WritesTraceIdFromHttpContext
    System.Collections.Generic.KeyNotFoundException : The given key was not present in the dictionary.   at System.Text.Json.JsonElement.GetProperty(String propertyName)
  Failed ProjectC.WebApi.Tests.Events.EventsControllerTests.GetTicketTypes_WithNonExistentEvent_ReturnsDistinctTraceIdPerRequest
    System.Collections.Generic.KeyNotFoundException : The given key was not present in the dictionary.   at System.Text.Json.JsonElement.GetProperty(String propertyName)
  Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.WebApi.Tests.dll (net10.0)
  ```
  失敗原因：回應 body 沒有 `traceId` 屬性（目標行為不符）。
- GREEN：新增 `ProblemDetailsWithTraceIdResult`，`CreateProblemResult` 回傳它。`--filter "ResultExtensionsTests|EventsControllerTests"`：
  ```
  Passed!  - Failed:     0, Passed:    70, Skipped:     0, Total:    70, Duration: 25 s - ProjectC.WebApi.Tests.dll (net10.0)
  ```
- 決策 2 實測（暫時測試，已刪除）：`Activity.Current` 存在時 `ProblemDetailsFactory.CreateProblemDetails` 的 `traceId` = `Activity.Current.Id`（輸出 `PROBE Activity.Current.Id=00-d1e4e36f0215394a74c3e08e45123f84-69430b0dee20abe9-00 traceId=00-d1e4e36f0215394a74c3e08e45123f84-69430b0dee20abe9-00`），與 `HttpContext.TraceIdentifier`（測試設為 `http-trace`）不同；維持自訂 `IActionResult` 方案。
- 重構後重跑（或 N/A）：N/A（無重構；既有 Controller 整合測試已含於 6.1 全套）
- 交叉審查後補強（測試守護，非行為變更）：新增 `ToActionResult_WhenActivityIsCurrent_WritesTraceIdentifierNotActivityId`，並在 `GlobalExceptionHandlerTests` theory 補 `traceId == httpContext.TraceIdentifier` 斷言（AER-003）。還原驗證：暫時把 `ProblemDetailsWithTraceIdResult` 改為 `Activity.Current?.Id ?? TraceIdentifier`，新測試失敗，還原後通過：
  ```
  Failed ProjectC.WebApi.Tests.Common.ResultExtensionsTests.ToActionResult_WhenActivityIsCurrent_WritesTraceIdentifierNotActivityId
  Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2, Duration: 128 ms - ProjectC.WebApi.Tests.dll (net10.0)
  （還原後）Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 102 ms - ProjectC.WebApi.Tests.dll (net10.0)
  ```

## 3. 前端 ApiError 訊息不再退回 title

對應 AER-009～AER-011。

- [x] 3.1 TDD（RED）：在 `httpClient` 單元測試新增——有 `detail` → message 等於 detail（AER-010）；有 `title` 無 `detail` → message 為含狀態碼的通用文字且不等於 `title`（AER-009）；非 JSON 失敗回應 → 通用文字且 `problem` 為 `null`（AER-011）。在 `web` 容器內執行，預期 RED：AER-009 的 Actual 為 `title` 字串
- [x] 3.2 GREEN：修改 `ApiError` 建構子為 `problem?.detail ?? \`API 請求失敗（狀態碼 ${status}）\``。重跑 3.1 的測試與前端全套測試
- [x] 3.3 重構（若有）後重跑；無則標 N/A

完成紀錄：

- RED（`docker compose exec -T web npx vitest run src/api/httpClient.test.ts`；web 容器先以 `docker compose up -d --no-deps web` 啟動）：
  ```
  ×  有 title 無 detail 時，message 為含狀態碼的通用文字而非 title
  AssertionError: expected ApiError: InternalServerError { …(2) } to match object { status: 500, …(1) }
  Tests  1 failed | 13 passed (14)
  ```
  （AER-010、AER-011 為既有行為的回歸守護，RED 前即通過；AER-009 的 Actual 為 title 字串。）
- GREEN：`ApiError` 訊息改為 `problem?.detail ?? \`API 請求失敗（狀態碼 ${status}）\``。前端全套：
  ```
  Test Files  47 passed (47)
       Tests  671 passed (671)
  ```
- 重構後重跑（或 N/A）：N/A

## 4. 文件同步

純文件，TDD 標示 N/A（無可自動化行為）。

- [x] 4.1 更新 `docs/api-conventions.md` 第 2 節：框架層錯誤改為新代碼、業務錯誤補 `traceId`、移除不一致項目 1、2；保留「模型繫結自動產生的 400 `Title` 為框架預設句子」為待決定
- [x] 4.2 更新 `docs/api-conventions-todo.md` 第 1 項標記完成
- [x] 4.3 確認 `openspec/specs` 中沒有其他寫死舊 `title` 字串的 Requirement（`openspec validate error-response-title-traceid` 並搜尋舊字串）

## 5. hardener 最終檢查

- [x] 5.1 程式碼全部完成後，對 `GlobalExceptionHandler` 與新增的 `IActionResult` 套用 `.claude/skills/hardener/SKILL.md` 檢查清單（`CancellationToken` 傳遞、例外邊界、日誌遮蔽等）；結果記於下方。若判定不適用須說明理由；若補強則依 TDD 先補 RED 測試

完成紀錄（對 `GlobalExceptionHandler.CreateBadRequestTitle` 與 `ProblemDetailsWithTraceIdResult`）：

- 參數驗證：`statusCode` 來自框架的 `BadHttpRequestException`，非外部輸入；`ReasonPhrases.GetReasonPhrase` 對未知碼回空字串、不拋例外，已由 450 測試守護。`ProblemDetailsWithTraceIdResult` 僅由 `CreateProblemResult` 內部建構，不接外部輸入。
- Entity 存在性／狀態機／並發：不適用（無 DB、無狀態）。
- CancellationToken：`TryHandleAsync` 既有行為已向下傳給 `WriteAsJsonAsync`；`IActionResult.ExecuteResultAsync(ActionContext)` 框架介面無 token，委派的 `ObjectResult` 使用 `HttpContext.RequestAborted`。
- 例外邊界：未新增 try/catch；寫入失敗交由框架處理。
- 日誌遮蔽：未新增日誌；`traceId` 非敏感資訊，500 維持無 `Detail`（AER-001 測試守護）。
- 結論：無缺漏的防禦分支，未修改程式碼。

## 6. 全套驗證與審查

- [x] 6.1 在容器內執行後端全套測試與前端全套測試，貼各測試專案摘要行原文

  `docker compose exec -T api dotnet test ProjectC.slnx`（輸出經 grep 過濾摘要行；WebApi.Tests 為交叉審查後新增 Activity 測試的重跑結果，其餘專案摘要行未變）：
  ```
  Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 193 ms - ProjectC.Domain.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   549, Skipped:     0, Total:   549, Duration: 2 s - ProjectC.Application.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 10 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: 52 s - ProjectC.Infrastructure.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   682, Skipped:     0, Total:   682, Duration: 3 m 8 s - ProjectC.WebApi.Tests.dll (net10.0)
  ```
  前端 `docker compose exec -T web npx vitest run`：`Test Files  47 passed (47)`、`Tests  671 passed (671)`。
  `openspec validate error-response-title-traceid`：valid（1 個 requirement 過長 warning）。
- [x] 6.2 前端真實瀏覽器驗證（claude-in-chrome）：(a) 觸發一個業務錯誤（例如登入驗證碼錯誤），以頁面文字與回應 body 核對 `traceId` 存在、頁面行為不變；(b) 在已登入頁面以 fetch 對建立訂單端點送出超限本文，核對回應 `title` 為 `PayloadTooLarge`；(c) 核對 Seq 中同一請求的 `TraceId` 與回應 `traceId` 相同。數字與狀態以頁面文字或回應 body 核對，不只依截圖
  - 前置：先前 `dotnet watch` 卡在 ENC0003 重啟提示、容器跑舊碼（回應無 `traceId`），執行 `docker compose restart api` 後才驗證；重啟後 `GET /api/events/0000…0001/ticket-types` 回 `{"title":"NotFound","status":404,"detail":"Event '00000000-0000-0000-0000-000000000001' was not found.","traceId":"0HNPS5KH9LM2O:00000001"}`。
  - (a) 通過：`https://localhost:5173/login` 於 UI 送出錯誤驗證碼，攔截到 `/api/auth/login` 400 body `{"title":"CaptchaInvalid","status":400,"detail":"驗證碼錯誤或已逾時，請重新取得驗證碼。","traceId":"0HNPS5KH9LM2Q:00000001"}`；頁面文字顯示「驗證碼錯誤或已逾時，請重新取得驗證碼。」（message 來自 detail，行為不變），並自動換發新驗證碼（`/api/captcha` 200）。
  - (b) 通過（偏離：未用已登入頁面 fetch 建立訂單端點）：改以匿名端點 `/api/auth/login` 驗證全域 1 MiB 上限的 413 Title。瀏覽器 fetch 送 2 MB 本文時 CDP 逾時 45 秒（renderer 無回應），改用 curl 直打 api：`curl -X POST --data-binary @big.json http://localhost:8080/api/auth/login` → `{"title":"PayloadTooLarge","status":413,"traceId":"0HNPS5KH9LM2U:00000001"}` `HTTP 413`。建立訂單端點 16 KiB 上限的 Title 由 `RequestBodyLimitsKestrelTests` 整合測試涵蓋。使用者已確認接受此偏離（2026-11-07），不補驗。
  - (c) 通過：瀏覽器 fetch 回應 `traceId` 為 `0HNPS5KH9LM2S:00000001`；Seq 事件（`/api/auth/login`、StatusCode 400）屬性 `TraceId` = `0HNPS5KH9LM2S:00000001`、`RequestId` 同值。
- [x] 6.3 `strict-reviewer` 與全新的對抗式 general-purpose subagent 依序審查，修正後重跑，直到兩者皆無 blocking
- [x] 6.4 歸檔前確認使用者已明確同意；歸檔後不自動 commit
