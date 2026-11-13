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

- [ ] 1.1 新增 `ProblemDetailsResponseWriter`（`WebApi/Common`），`GlobalExceptionHandler` 全部分支改用它並移除 503 條件式與常數（對應 AER-036、037、038）
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
    - RED（正式程式碼修改前；命令、失敗測試名稱、Expected／Actual 原文）：
    - GREEN：
    - 重構後重跑（或 N/A）：
    - 還原驗證（`bash scripts/verify-test-evidence.sh restore`，命令與輸出原文）：

## 2. 429、413 實際回應與 JWT 共用（TDD）

- [ ] 2.1 限流 `OnRejected` 改用共用方法（對應 AER-039、AER-040）
  - 測試先行（`tests/ProjectC.WebApi.Tests/Admin/SeatMapRateLimitingTests.cs` 既有 429 測試補斷言，或同檔新增測試）：被限流的 429 回應 `Content-Type` 為 `application/problem+json`，`Retry-After` 仍存在、`title` 為 `TooManyRequests`
  - 預期 RED：`Content-Type` 斷言以 `application/json`（實際值以執行結果為準）失敗
  - 最小實作：`OnRejected` 設狀態碼與 `Retry-After` 後呼叫共用方法
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~RateLimiting"` 全數通過（含既有限流測試）
  - 重構：N/A 或重跑同命令
  - 紀錄（原文）：
    - RED：
    - GREEN：
    - 重構後重跑（或 N/A）：
    - 還原驗證：
- [ ] 2.2 413 整合斷言（對應 AER-040）：`tests/ProjectC.WebApi.Tests/RequestBodyLimits/RequestBodyLimitsKestrelTests.cs` `PlaceOrder_WhenBodyTooLarge_ReturnsProblemDetails`（會解析 ProblemDetails 本文、確實經 `GlobalExceptionHandler` 的 413 測試）補 `Content-Type` 斷言；Kestrel 依宣告 Content-Length 提前回的 413 可能不經 handler，不在此補
  - 預期 RED：須在 1.1 修改正式程式碼前先加此斷言並記錄失敗訊息；1.1 完成後 GREEN
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~RequestBodyLimitsKestrel"` 通過
  - 紀錄（原文）：
    - RED：
    - GREEN：
- [ ] 2.3 `JwtBearerProblemDetailsResponses.WriteProblemAsync` 改呼叫共用方法並刪除寫死字串；既有 JWT 401／403 測試（AER-012～016）仍全數通過
  - TDD：N/A，純重構、行為不變，既有測試已涵蓋（含 Content-Type）；不新增測試
  - 驗證：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~JwtChallengeProblemDetailsTests"` 通過
  - 紀錄（原文）：

## 3. 文件與最終檢查

- [ ] 3.1 `docs/api-conventions.md` 錯誤格式一節補一句：框架層錯誤（500／400／408／413／429／503）Content-Type 為 `application/problem+json`，詳見 `api-error-responses` 主 spec；驗證：文件與 spec 一致（TDD N/A：純文件）
- [ ] 3.2 grep `load-test/`、`scripts/`、前端 `src` 無對錯誤回應 `application/json` 的嚴格比對；驗證：命令與結果貼於紀錄
- [ ] 3.3 hardener 最終檢查：套用 `.claude/skills/hardener/SKILL.md`（共用方法為單純寫出、無新 I/O 分支，重點核對 `CancellationToken` 傳遞），結果記錄於此
- [ ] 3.4 全套測試：`docker compose exec -T api dotnet test`，各測試專案摘要行貼原文
  - 紀錄（原文）：

## Workflow follow-up

- spec-reviewer → design-hardener，PASS 後暫停等使用者交叉審查。
- 實作經 `apply-in-pane`，審查迴圈依 CLAUDE.md；完成後歸檔並合併，不自動 commit／push。
