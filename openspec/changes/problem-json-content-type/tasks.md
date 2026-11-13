# Tasks

## AC → 測試對照

| Scenario | 對應 task／測試 |
|---|---|
| AER-036 未預期例外的 Content-Type | 1.1 `GlobalExceptionHandlerTests` |
| AER-037 BadHttpRequestException 的 Content-Type | 1.1 |
| AER-038 資料庫逾時的 Content-Type | 1.1 |
| AER-039 限流 429 的 Content-Type | 2.1 `SeatMapRateLimitingTests` |
| AER-040 實際 HTTP 回應的 Content-Type | 2.1（429）＋ 2.2 `RequestBodyLimitsKestrelTests`（413） |

## 1. 共用寫入方法與 GlobalExceptionHandler（TDD）

- [x] 1.1 新增 `ProblemDetailsResponseWriter`（`WebApi/Common`），`GlobalExceptionHandler` 全部分支改用它並移除 503 條件式與常數（對應 AER-036、037、038）
  - 測試先行（`tests/ProjectC.WebApi.Tests/ExceptionHandling/GlobalExceptionHandlerTests.cs`）：
    - `TryHandleAsync_WhenUnexpectedException_WritesProblemJsonContentType`（500）
    - `TryHandleAsync_WhenBadHttpRequest_WritesProblemJsonContentType`（Theory：400、408、413）
    - `TryHandleAsync_WhenDatabaseTimeout_WritesProblemJsonContentType`（503，現況應已通過，固定既有行為；並把該檔既有 503 Content-Type 的 `StartWith` 斷言改為字串相等）
    - 斷言 `httpContext.Response.ContentType.Should().Be("application/problem+json")`
  - 預期 RED：500 與 400／408／413 測試以 `Expected ...ContentType to be "application/problem+json", but ...`（實際值以執行結果為準，預期為 `application/json`）失敗；503 測試通過；編譯失敗不算 RED
  - 最小實作：新增共用方法；`GlobalExceptionHandler` 改呼叫，刪除 `ProblemJsonContentType` 與條件式
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~GlobalExceptionHandlerTests"` 全數通過
  - 重構：N/A 或重跑同命令
  - 紀錄（原文）：
    - RED（正式程式碼修改前；2.1、2.2 的斷言也已先加入）：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~GlobalExceptionHandlerTests"`
      ```
      Failed ...GlobalExceptionHandlerTests.TryHandleAsync_WhenBadHttpRequest_WritesProblemJsonContentType(statusCode: 408) [17 ms]
      Failed ...GlobalExceptionHandlerTests.TryHandleAsync_WhenBadHttpRequest_WritesProblemJsonContentType(statusCode: 400) [2 ms]
      Failed ...GlobalExceptionHandlerTests.TryHandleAsync_WhenBadHttpRequest_WritesProblemJsonContentType(statusCode: 413) [2 ms]
      Failed ...GlobalExceptionHandlerTests.TryHandleAsync_WhenUnexpectedException_WritesProblemJsonContentType [2 ms]
       Expected httpContext.Response.ContentType to be a match with the expectation, but it differs at index 12:
       "…plication/json; charset=utf-8" (actual) / "…plication/problem+json" (expected)
      Failed!  - Failed:     4, Passed:    16, Skipped:     0, Total:    20, Duration: 103 ms - ProjectC.WebApi.Tests.dll (net10.0)
      ```
      503 測試 `TryHandleAsync_WhenDatabaseTimeout_WritesProblemJsonContentType` 與已改為 `Be` 的 `AssertServiceUnavailable` 通過，符合預期（固定既有行為）。實測預設 Content-Type 為 `application/json; charset=utf-8`。
    - GREEN：`...--filter "FullyQualifiedName~GlobalExceptionHandlerTests"`
      `Passed!  - Failed:     0, Passed:    20, Skipped:     0, Total:    20, Duration: 84 ms - ProjectC.WebApi.Tests.dll (net10.0)`
    - 重構後重跑：N/A（共用方法已是最小形態）
    - 還原驗證（`bash scripts/verify-test-evidence.sh restore --path <ProblemDetailsResponseWriter、JwtBearerProblemDetailsResponses、GlobalExceptionHandler、RateLimitingServiceCollectionExtensions 四個正式檔> -- docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~GlobalExceptionHandlerTests|FullyQualifiedName~SeatMapRateLimitingTests|FullyQualifiedName~RequestBodyLimitsKestrelTests"`）：
      `Saved working directory and index state WIP on feature/problem-json-content-type: 835cf21 文件：修正 problem-json-content-type 規格審查的 warning`；`git stash list` → `stash@{0}: WIP on feature/problem-json-content-type: 835cf21 文件：修正 problem-json-content-type 規格審查的 warning`
      失敗（Expected `application/problem+json`，actual `application/json; charset=utf-8`）：`GlobalExceptionHandlerTests.TryHandleAsync_WhenBadHttpRequest_WritesProblemJsonContentType(statusCode: 408/400/413)`、`TryHandleAsync_WhenUnexpectedException_WritesProblemJsonContentType`、`SeatMapRateLimitingTests.CreateSeatMap_WhenOrganizerExceedsLimit_Returns429ProblemDetailsAndCreatesNoSeatMap`、`RequestBodyLimitsKestrelTests.PlaceOrder_WhenBodyTooLarge_ReturnsProblemDetails`
      `Failed!  - Failed:     6, Passed:    34, Skipped:     0, Total:    40, Duration: 19 s - ProjectC.WebApi.Tests.dll (net10.0)`
      `結果：測試如預期失敗`；`$ git stash pop` 成功，`git status` 還原為 3 個正式檔 modified、Writer untracked、3 個測試檔 modified。

## 2. 429、413 實際回應與 JWT 共用（TDD）

- [x] 2.1 限流 `OnRejected` 改用共用方法（對應 AER-039、AER-040）
  - 測試先行（`tests/ProjectC.WebApi.Tests/Admin/SeatMapRateLimitingTests.cs` 既有 429 測試補斷言，或同檔新增測試）：被限流的 429 回應 `Content-Type` 為 `application/problem+json`，`Retry-After` 仍存在、`title` 為 `TooManyRequests`
  - 預期 RED：`Content-Type` 斷言以 `application/json`（實際值以執行結果為準）失敗
  - 最小實作：`OnRejected` 設狀態碼與 `Retry-After` 後呼叫共用方法
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~RateLimiting"` 全數通過（含既有限流測試）
  - 重構：N/A 或重跑同命令
  - 紀錄（原文）：
    - RED：與 1.1 同次執行（正式程式碼未改）：`...--filter "FullyQualifiedName~SeatMapRateLimitingTests"`
      `Failed ...SeatMapRateLimitingTests.CreateSeatMap_WhenOrganizerExceedsLimit_Returns429ProblemDetailsAndCreatesNoSeatMap [687 ms]`
      `Expected rejected.Content.Headers.ContentType!.ToString() to be a match with the expectation, but it differs at index 12:`（actual `…plication/json; charset=utf-8`）
      `Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3, Duration: 3 s - ProjectC.WebApi.Tests.dll (net10.0)`
    - GREEN：`...--filter "FullyQualifiedName~RateLimiting"`
      `Passed!  - Failed:     0, Passed:    52, Skipped:     0, Total:    52, Duration: 14 s - ProjectC.WebApi.Tests.dll (net10.0)`
    - 重構後重跑：N/A
    - 還原驗證：併入 1.1 的還原驗證（同一次 stash 涵蓋 `RateLimitingServiceCollectionExtensions.cs`），`SeatMapRateLimitingTests...Returns429ProblemDetailsAndCreatesNoSeatMap` 如上失敗。
- [x] 2.2 413 整合斷言（對應 AER-040）：`tests/ProjectC.WebApi.Tests/RequestBodyLimits/RequestBodyLimitsKestrelTests.cs` `PlaceOrder_WhenBodyTooLarge_ReturnsProblemDetails`（會解析 ProblemDetails 本文、確實經 `GlobalExceptionHandler` 的 413 測試）補 `Content-Type` 斷言；Kestrel 依宣告 Content-Length 提前回的 413 可能不經 handler，不在此補
  - 預期 RED：須在 1.1 修改正式程式碼前先加此斷言並記錄失敗訊息；1.1 完成後 GREEN
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~RequestBodyLimitsKestrel"` 通過
  - 紀錄（原文）：
    - RED（1.1 修改正式程式碼前）：`...--filter "FullyQualifiedName~RequestBodyLimitsKestrelTests"`
      `Failed ...RequestBodyLimitsKestrelTests.PlaceOrder_WhenBodyTooLarge_ReturnsProblemDetails [239 ms]`
      `Expected response.Content.Headers.ContentType!.ToString() to be a match with the expectation, but it differs at index 12:`（actual `…plication/json; charset=utf-8`）
      `Failed!  - Failed:     1, Passed:    16, Skipped:     0, Total:    17, Duration: 14 s - ProjectC.WebApi.Tests.dll (net10.0)`
    - GREEN：`Passed!  - Failed:     0, Passed:    17, Skipped:     0, Total:    17, Duration: 15 s - ProjectC.WebApi.Tests.dll (net10.0)`
- [x] 2.3 `JwtBearerProblemDetailsResponses.WriteProblemAsync` 改呼叫共用方法並刪除寫死字串；既有 JWT 401／403 測試（AER-012～016）仍全數通過
  - TDD：N/A，純重構、行為不變，既有測試已涵蓋（含 Content-Type）；不新增測試
  - 驗證：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~JwtChallengeProblemDetailsTests"` 通過
  - 紀錄（原文）：`Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 3 s - ProjectC.WebApi.Tests.dll (net10.0)`

## 3. 文件與最終檢查

- [x] 3.1 `docs/api-conventions.md` 錯誤格式一節補一句：框架層錯誤（500／400／408／413／429／503）Content-Type 為 `application/problem+json`，詳見 `api-error-responses` 主 spec；驗證：文件與 spec 一致（TDD N/A：純文件）
  - 紀錄：`docs/api-conventions.md` 第 2 節「錯誤格式」首句後補：框架層錯誤（500／400／408／413／429／503）Content-Type 一律為 `application/problem+json`（不附 charset），指向 `api-error-responses` 主 spec；與 spec 一致。
- [x] 3.2 grep `load-test/`、`scripts/`、前端 `src` 無對錯誤回應 `application/json` 的嚴格比對；驗證：命令與結果貼於紀錄
  - 紀錄：`grep -rniE "application/json|content-type|contentType" scripts` → 僅 `scripts/verify-k8s-manifests.sh:524` 與 `scripts/verify-production-images.sh:167` 的**請求** header（`-H 'Content-Type: application/json'`），無對回應的嚴格比對。`load-test/` 目錄不存在（實為 `docs/load-test`，`grep -rnE "Content-Type|content-type" docs/load-test` 無結果）。前端 `web/src/api/httpClient.ts:43,91` 以 `contentType.includes('json')` 判斷，`application/problem+json` 相容；其餘 `web/src` 的 `application/json` 為測試 mock、請求 header 與產生型別。
- [x] 3.3 hardener 最終檢查：套用 `.claude/skills/hardener/SKILL.md`（共用方法為單純寫出、無新 I/O 分支，重點核對 `CancellationToken` 傳遞），結果記錄於此
  - 紀錄：核對本次變更方法——`ProblemDetailsResponseWriter.WriteAsync` 接收並傳遞 `CancellationToken` 給 `WriteAsJsonAsync`；`GlobalExceptionHandler.TryHandleAsync` 傳入其 `cancellationToken`；限流 `OnRejected` 傳入其 `cancellationToken`；JWT `WriteProblemAsync` 傳 `httpContext.RequestAborted`（沿用既有）。參數驗證／Entity 存在性／狀態機／並發／例外邊界／日誌遮蔽：本次無新增 I/O 分支、未動日誌，均不適用。結論：無缺漏防禦分支，未因此修改程式碼。
- [x] 3.4 全套測試：`docker compose exec -T api dotnet test`，各測試專案摘要行貼原文
  - 紀錄（原文，`docker compose exec -T api dotnet test`）：
    ```
    Passed!  - Failed:     0, Passed:   153, Skipped:     0, Total:   153, Duration: 107 ms - ProjectC.Domain.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   632, Skipped:     0, Total:   632, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 10 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
      Failed ProjectC.Infrastructure.Tests.Caching.QueryCacheTtlSafetyNetTests.GetEventsHandler_WhenInvalidationHappensWhileQueryInFlight_CacheTemporarilyResurrectsStaleValue_ButExpiresWithinTtl [7 s]
    Failed!  - Failed:     1, Passed:   307, Skipped:     0, Total:   308, Duration: 1 m 13 s - ProjectC.Infrastructure.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   990, Skipped:     0, Total:   990, Duration: 3 m 53 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
    Infrastructure.Tests 的 1 個失敗與本變更無關（QueryCache TTL 測試，本變更未動 Infrastructure；專案已知 WSL2 時鐘造成時間類 flake）。單獨重跑：`docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~QueryCacheTtlSafetyNetTests"` → `Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 31 s - ProjectC.Infrastructure.Tests.dll (net10.0)`。全套第一次未全綠，如實記錄。

## Workflow follow-up

- spec-reviewer → design-hardener，PASS 後暫停等使用者交叉審查。
- 實作經 `apply-in-pane`，審查迴圈依 CLAUDE.md；完成後歸檔並合併，不自動 commit／push。
