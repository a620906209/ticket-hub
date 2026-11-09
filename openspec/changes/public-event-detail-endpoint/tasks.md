# Tasks

> 對應 spec：`event-catalog`（ADDED：EVT-DETAIL-001～008）、`buyer-web-ui`（ADDED：BW-DETAIL-001～005；MODIFIED：三條既有 Requirement 的詳情頁資料來源敘述）。`query-caching` 不修改。
> 所有後端命令在容器內執行：`docker compose exec -T api dotnet test <專案> --filter "..."`；前端：`docker compose exec -T web npm run test -- <檔案>`。
> 每個 task 的「完成紀錄」於實作時依 CLAUDE.md「證據貼原文」填寫（RED：命令、失敗測試名、Expected／Actual 原文，需在修改正式程式碼前；GREEN：命令與摘要行；重構後重跑或 N/A）。核心查詢 task（1.1）另預留「還原驗證」紀錄，使用 `scripts/verify-test-evidence.sh`（mutate／restore 規則見 CLAUDE.md；mutate 後 `touch` 被改檔案再重建）。

## 1. 後端單一活動查詢

- [x] 1.1 在 `tests/ProjectC.Application.Tests/Events/GetEventById/` 新增 `GetEventByIdHandlerTests`：先寫測試，涵蓋 EVT-DETAIL-001（取得存在活動，欄位與列表同筆一致）、EVT-DETAIL-002（不存在回 `ErrorType.NotFound`）、EVT-DETAIL-004（快取命中不呼叫 Repository）、EVT-DETAIL-005（未命中時 `GetAllAsync` 恰好呼叫一次並寫回 `GetEventsHandler.CacheKey`）、EVT-DETAIL-006（失效後再查得新值）、EVT-DETAIL-008 的回退路徑（以既有 `FakeQueryCache` 表示「未命中」：查 DB 後回傳該活動、不存在仍回 `NotFound`；fake 不得拋 Redis 例外，因 `IQueryCache` 契約為內部吞例外不外拋，真實 Redis 斷線行為由 1.2 的 HTTP 層測試驗證）。預期 RED：先建立 `GetEventByIdHandler` 空殼（簽章 `Task<Result<EventDto>> HandleAsync(Guid, CancellationToken)`，一律回 `NotFound`）使其可編譯，測試因 Expected／Actual 不符失敗（不得以編譯失敗當 RED）。再實作：呼叫 `GetEventsHandler.HandleAsync` 後 `FirstOrDefault(e => e.Id == id)`，找不到回 `Error.NotFound`。並依 hardener 清單先列防禦分支：`CancellationToken` 傳遞、`Guid.Empty` 視為不存在。
  - EVT-DETAIL-004 須斷言快取命中時 Repository 的**任何方法**（含 `GetByIdAsync`、`GetAllAsync`）皆未被呼叫（以 strict mock／所有方法計數），否則無法殺掉「略過 `GetEventsHandler` 改直接 `GetByIdAsync`」的 mutate。
  - 因 RED 空殼一律回 `NotFound`，EVT-DETAIL-002 及 EVT-DETAIL-008 的不存在分支、`Guid.Empty` 案例在 RED 時即通過；RED 紀錄須列出「RED 時預期已通過」的測試名稱，並以 mutate 證明它們能失敗（例如把實作改成一律回傳列表第一筆活動，這些測試必須失敗）。
  - RED 紀錄（正式程式碼修改前；空殼 `GetEventByIdHandler` 建構子接 `GetEventsHandler`、`HandleAsync` 一律回 `NotFound`；另在 `FakeEventRepository` 加 `LastGetAllToken` 供 token 斷言）：
    - 命令：`docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~GetEventByIdHandlerTests"`
    - 失敗測試與訊息原文：
      - `HandleAsync_WhenEventExists_ReturnsSameDtoAsListEntry`：`Expected result.IsSuccess to be True, but found False.`
      - `HandleAsync_PassesCancellationTokenToRepository`：`Expected _eventRepository.LastGetAllToken to be System.Threading.CancellationToken {CanBeCanceled = True, IsCancellationRequested = False, ...}, but found <null>.`
      - `HandleAsync_WhenListCacheHits_DoesNotCallAnyRepositoryMethod`：`Expected result.Value to be ProjectC.Application.Events.GetEvents.EventDto {... Title = "Cached" ...}, but found <null>.`
      - `HandleAsync_WhenListCacheMisses_QueriesAllOnceAndWritesListCache`：`System.NullReferenceException : Object reference not set to an instance of an object.`（`result.Value!.Id`，空殼 Value 為 null）
      - `HandleAsync_WhenCacheUnavailableTreatedAsMiss_StillReturnsEventFromDatabase`：`Expected result.IsSuccess to be True, but found False.`
      - `HandleAsync_AfterListCacheInvalidated_ReflectsLatestValue`：`System.NullReferenceException : Object reference not set to an instance of an object.`
    - 摘要行：`Failed!  - Failed:     6, Passed:     3, Skipped:     0, Total:     9, Duration: 67 ms - ProjectC.Application.Tests.dll (net10.0)`
    - RED 時預期已通過（空殼一律 NotFound）：`HandleAsync_WhenEventDoesNotExist_ReturnsNotFound`、`HandleAsync_WhenIdIsEmptyGuid_ReturnsNotFound`、`HandleAsync_WhenCacheMissesAndEventDoesNotExist_ReturnsNotFound`，以下方 mutate 1／2 證明能失敗。
  - GREEN 紀錄：同一命令，`Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 32 ms - ProjectC.Application.Tests.dll (net10.0)`
  - 還原驗證紀錄（`bash scripts/verify-test-evidence.sh mutate`，一次一個改動，測試命令同上；每次輸出末行「結果：測試如預期失敗」，檔案已自動還原，`git status` 僅剩預期變更）：
    - mutate 1：`e.Id == eventId` → `e.Id != eventId`：`Failed!  - Failed:     7, Passed:     2, Skipped:     0, Total:     9`；失敗：`..._WhenEventExists_ReturnsSameDtoAsListEntry`、`..._WhenIdIsEmptyGuid_ReturnsNotFound`、`..._WhenEventDoesNotExist_ReturnsNotFound`、`..._WhenListCacheHits_DoesNotCallAnyRepositoryMethod`、`..._WhenListCacheMisses_QueriesAllOnceAndWritesListCache`、`..._WhenCacheUnavailableTreatedAsMiss_StillReturnsEventFromDatabase`、`..._AfterListCacheInvalidated_ReflectsLatestValue`
    - mutate 2：`events.FirstOrDefault(e => e.Id == eventId)` → `events.FirstOrDefault()`（一律回第一筆）：`Failed!  - Failed:     3, Passed:     6, Skipped:     0, Total:     9`；失敗：`..._WhenEventExists_ReturnsSameDtoAsListEntry`、`..._WhenIdIsEmptyGuid_ReturnsNotFound`、`..._WhenEventDoesNotExist_ReturnsNotFound`
    - mutate 3：`_getEventsHandler.HandleAsync(cancellationToken)` → `HandleAsync(CancellationToken.None)`：`Failed!  - Failed:     1, Passed:     8, Skipped:     0, Total:     9`；失敗：`..._PassesCancellationTokenToRepository`
    - 未做「略過 `GetEventsHandler` 改直接 `GetByIdAsync`」的 mutate：該改動需新增 Repository 依賴，無法以單一字串替換表達；`EVT-DETAIL-004`／`005` 測試分別斷言 `GetByIdCallCount == 0` 與 `GetAllCallCount == 1`，且 Handler 建構子僅接受 `GetEventsHandler`，此路徑在結構上不存在。
    - 因空殼＋新增 Handler 為未追蹤檔，`restore`（stash）會使測試專案無法編譯（exit 2），故以 mutate 取代。
  - 重構後重跑：N/A（無重構）
  - 第 2 輪（Opus 建議 ③）更新：刪除 `HandleAsync_WhenCacheUnavailableTreatedAsMiss_StillReturnsEventFromDatabase`（與未命中測試的設定與斷言相同、未模擬無法使用）。上列 RED／GREEN／mutate 的 `Total: 9` 與 mutate 1 失敗清單中該測試名稱，為刪除前（9 個測試）的當時原文；刪除後現況為 8 個測試，同一篩選重跑：`Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8, Duration: 31 ms - ProjectC.Application.Tests.dll (net10.0)`。刪除的測試不影響 mutate 的殺死力（mutate 1 另有 6 個測試失敗）。EVT-DETAIL-008 的回退行為改由 1.2 的 `QueryCachingFailOpenTests` HTTP 層測試（Redis 不可用：存在活動 `200`、不存在 `404`、Warning log）涵蓋。

- [x] 1.2 加入 HTTP 層測試，分兩處（factory 不可混用）：
  - `tests/ProjectC.WebApi.Tests/Events/EventsControllerTests.cs`（既有檔，`CustomWebApplicationFactory`）：EVT-DETAIL-001（`200` 且 body 欄位與同一活動在 `GET /api/events` 一致）、EVT-DETAIL-002（`404` ProblemDetails）、EVT-DETAIL-007（匿名與已登入不同角色回應相同）。
  - 使用 `CachingComponentTestWebApplicationFactory`（含 `CountingEventRepository`，真實 Redis，放在既有 `QueryCaching*Tests` 類別或同目錄新類別）：EVT-DETAIL-003（`/api/events/not-a-guid` 回 `404`，`CountingEventRepository` 計數為 0，且列表快取 key 未被寫入，比照 QC-ACCESS-003 測試手法，不使用不存在的 `IQueryCache` 呼叫計數）、EVT-DETAIL-005（冷快取 `GetAllAsync` 恰一次、列表 key 被寫入、之後 `GET /api/events` 不再查庫）。
  - 使用 `RedisUnavailableWebApplicationFactory`（`QueryCachingFailOpenTests.cs` 既有手法）：EVT-DETAIL-008（存在的活動回 `200`、不存在的活動回 `404`、且有 Redis 連線失敗 Warning log）。
  - EVT-DETAIL-004 的 HTTP 層對應：先預熱列表快取，再呼叫單筆端點，`CountingEventRepository` 計數不增加。`CountingEventRepository` 目前只計 `GetAllAsync`（`GetByIdAsync` 為 pass-through 不計數），本 task 一併擴充為兩個方法分別計數，EVT-DETAIL-003 的「Repository 計數為 0」改為兩者皆為 0。
  - 1.2 的 404 案例（EVT-DETAIL-002、003、008 不存在分支）在 RED 空殼（回 `NotFound()`）下即通過，RED 紀錄列出這些測試，並以 mutate 證明能失敗（例如把路由改為 `{id}` 去掉 `:guid` 約束使 003 失敗、讓 action 一律回 `NotFound()` 使 001 失敗）。
  - EVT-DETAIL-006 的真實失效觸發由既有 QC-EVT-INV-002 涵蓋，本 task 不重複；1.1 以 handler 層驗證。
  - 預期 RED：在 `EventsController` 加入尚未實作的 `GetEventById` action（回 `NotFound()`）後測試因狀態碼／body 不符失敗。再實作：`[HttpGet("{id:guid}")]` 呼叫 `GetEventByIdHandler`，`result.ToActionResult(Ok)`；於 DI 註冊 `GetEventByIdHandler`（lifetime 同 `GetEventsHandler`，理由記入 PR 說明）。
  - RED 紀錄（正式程式碼修改前；`EventsController` 先加 `[HttpGet("{id:guid}")] GetEventById` 回 `NotFound()`；測試檔：`EventsControllerTests`（3）、新增 `QueryCachingEventDetailTests`（3）、`QueryCachingFailOpenTests`（1）；`CountingEventRepository` 擴充 `GetByIdAsyncCallCount`）：
    - 命令：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~GetEventById"`
    - 失敗測試與訊息原文：
      - `EventsControllerTests.GetEventById_AsAnonymousBuyerAndAdmin_ReturnsIdenticalPublicContent`：`Expected anonymousResponse.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.`
      - `EventsControllerTests.GetEventById_WhenEventExists_Returns200WithSameFieldsAsListEntry`：`Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.`
      - `EventsControllerTests.GetEventById_WhenEventDoesNotExist_Returns404ProblemDetails`：`Expected body.RootElement.GetProperty("title").GetString() to be a match with the expectation, but it differs at index 3: "Not Found" vs "NotFound"`（空殼的框架 404 title 為 `Not Found`，既有 NotFound 對應代碼為 `NotFound`）
      - `QueryCachingEventDetailTests.GetEventById_WhenListCacheIsWarm_DoesNotQueryRepository`：`Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.`
      - `QueryCachingEventDetailTests.GetEventById_WhenListCacheIsCold_QueriesAllOnceWritesListCacheAndLaterListHitsIt`：同上 200／404
      - `QueryCachingFailOpenTests.GetEventById_WhenRedisUnreachable_FallsBackToDatabaseAndLogsWarning`：`Expected existingResponse.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.`
    - 摘要行：`Failed!  - Failed:     6, Passed:     1, Skipped:     0, Total:     7, Duration: 9 s - ProjectC.WebApi.Tests.dll (net10.0)`
    - RED 時預期已通過：`QueryCachingEventDetailTests.GetEventById_WithNonGuidId_Returns404WithoutTouchingRepositoryOrCache`（空殼路由同樣不進 Handler），以下方 mutate 1 證明能失敗。
  - GREEN 紀錄：實作 `GetEventById` action（`_getEventByIdHandler.HandleAsync` + `result.ToActionResult(Ok)`）與 `AddScoped<GetEventByIdHandler>()`（Scoped，理由：與 `GetEventsHandler` 一致，且其依賴 Scoped Repository，不可更長）後同一命令：`Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: 20 s - ProjectC.WebApi.Tests.dll (net10.0)`
  - 還原驗證紀錄（`scripts/verify-test-evidence.sh mutate`，一次一個改動；結果末行「測試如預期失敗」，檔案已還原）：
    - mutate 1：`[HttpGet("{id:guid}")]` → `[HttpGet("{id}")]`：`Failed:     1, Passed:     6, Total:     7`；失敗：`GetEventById_WithNonGuidId_Returns404WithoutTouchingRepositoryOrCache`：`Expected response.StatusCode to be HttpStatusCode.NotFound {value: 404}, but found HttpStatusCode.BadRequest {value: 400}.`
    - mutate 2：`return result.ToActionResult(Ok);` → `return Ok(result.Value);`（`GetEventById` 內）：`Failed:     2, Passed:     5, Total:     7`；失敗：`EventsControllerTests.GetEventById_WhenEventDoesNotExist_Returns404ProblemDetails`、`QueryCachingFailOpenTests.GetEventById_WhenRedisUnreachable_FallsBackToDatabaseAndLogsWarning`（不存在分支）
    - 「讓 action 一律回 `NotFound()` 使 001 失敗」即 RED 空殼本身（上方 RED 紀錄 6 個失敗）。
  - 重構後重跑：N/A（無重構）

## 2. 前端詳情頁改用單一活動 API

- [x] 2.1 在 `web/src/api/events.ts` 新增 `getEventById(eventId)`；於 `EventDetailPage.test.ts` 先改寫測試：全檔 `getEvents` mock 改為 `getEventById`（既有測試名稱列入 RED 紀錄）；新增 BW-DETAIL-001（不呼叫 `getEvents`）、BW-DETAIL-002（`getEventById`、座位、票種三者皆以 `ApiError` 404 拒絕（真實情境）→ 顯示「找不到這個活動」且無錯誤訊息）、BW-DETAIL-003（涵蓋兩個組合：僅 `getEventById` 以 `ApiError` 500 拒絕；以及 `getEventById` 500 且座位／票種 404 → 皆顯示「載入活動資訊失敗」且無「找不到」，以活動請求的結果為判定依據）、BW-DETAIL-004（既有測試 `EventDetailPage.test.ts` 中以 `getEvents toHaveBeenCalledTimes(2)` 驗證重載的案例改為 `getEventById` 兩次、`getEvents` 零次；須各有一個測試涵蓋三條重新載入路徑：下單一般失敗後、下單成功後（`EventDetailPage.vue:759`）、下單成功後離頁導覽失敗而留在本頁時的重新同步（`:420`），每個都斷言 `getEventById` 被再次呼叫且 `getEvents` 零次）、BW-DETAIL-005（`getEventById` 200，座位或票種以 `ApiError` 500 拒絕 → 顯示「載入活動資訊失敗」且無「找不到」，各一個測試）。BW-DETAIL-001 另斷言並行載入：以不 resolve 的 deferred promise 讓 `getEventById`、座位、票種三個呼叫在任一者 resolve 前皆已發出。預期 RED：先加 `getEventById` 空殼（`reject`）使測試可執行，因斷言不符失敗（非編譯／匯入失敗）。再實作 `loadData`：依設計決策 3 以 `Promise.allSettled`（或等效）並行載入，活動 `404` → `event=null`，其他失敗沿用 `toErrorMessage(error, '載入活動資訊失敗')`。同時更新其他掛載 `EventDetailPage` 並 mock `getEvents` 的前端測試：`orderConfirmationFlow.test.ts`（以全套 Vitest 為準；`BuyerLayout.test.ts`、`elementPlusOnDemand.test.ts` 掛載的是列表頁，其 `getEvents` mock 不得更動）。實作時先列出 `grep getEvents web/src` 的所有受影響測試。`loadData` 實作後以 `Promise.allSettled` 判定：活動 `404` → `event=null`；活動非 `404` 失敗，或活動成功但座位／票種任一失敗 → 沿用 `toErrorMessage(error, '載入活動資訊失敗')`，不得出現有活動內容卻無錯誤訊息的情況（BW-DETAIL-005）。注意現行模板在載入失敗時  為 null，頂端 el-alert 與「找不到這個活動」的 el-empty 會同時顯示（BW-FLOW-004 未斷言其不存在）；BW-DETAIL-003／005 要求失敗時不顯示「找不到」，故 `EventDetailPage.vue` 的 el-empty 條件須加上 `!errorMessage`，BW-FLOW-004 等既有載入失敗測試需確認不受影響（屬行為變更，測試於 RED 時應能抓到）。2.1 的 BW-DETAIL-004 測試可參考既有 BW-CONFIRM-015、BW-CONFIRM-020 離頁失敗案例，遷移為斷言 `getEventById` 第 2 次被呼叫；既有 `toHaveBeenCalledTimes(1)` 的 401／SalesNotOpen「不重載」測試須保留語意。
  - RED 紀錄（正式程式碼修改前；`getEventById` 空殼 `Promise.reject(new Error('not implemented'))`，`EventDetailPage.vue` 仍呼叫 `getEvents`；全檔 `getEvents` mock 已遷移為 `getEventById`）：`docker compose exec -T web npx vitest run src/pages/buyer/EventDetailPage.test.ts src/pages/buyer/orderConfirmationFlow.test.ts`
    - 摘要行：` Test Files  2 failed (2)` ／ `      Tests  164 failed | 6 passed (170)`。
    - 新增案例失敗：`[BW-DETAIL-001]`～`[BW-DETAIL-005]` 共 6 個（含 003、005 的各 2 個子案例）皆失敗，例如 `[BW-DETAIL-002] loadData_WhenAllThreeRequestsReturn404_ShowsNotFoundWithoutErrorAlert`：`AssertionError: expected true to be false // Object.is equality`（`pageError(wrapper).exists()` 為 true：空殼拒絕使頁面顯示載入失敗而非找不到）。
    - 既有測試失敗（因頁面仍呼叫 `getEvents`，`getEventById` 空殼拒絕）：例如 `選擇可售座位並成功下單`：`Error: Cannot call trigger on an empty DOMWrapper.`（`EventDetailPage.test.ts:327`）；`orderConfirmationFlow.test.ts` 的 `confirmOrder_WhenSucceeded_EventListShowsNoticeLinkingToSameOrderId：'座位（BW-ORDER-001）'`、`'純計數（BW-ORDER-007）'`、`'混合（BW-ORDER-008）'` 同樣失敗。失敗原因皆為目標行為不符（資料來源未改），非編譯／匯入錯誤。
    - BW-DETAIL-004：既有重載案例（`下單時座位已被搶先鎖定…` 的 `getEventById` ×2、`[BW-CONFIRM-015] …WhenNavigationToEventListFails…` 與 `[BW-CONFIRM-020] …WhenLeavingFailsAfterSuccess…` 各加 `getEventById` ×2、`getEvents` 零次斷言）併入上述既有測試的失敗。
  - GREEN 紀錄：實作 `getEventById`（`authorizedRequest(`/events/${eventId}`)`）、`loadData` 改 `Promise.allSettled`（活動 404 → `event=null`；其他任一失敗 → 拋出並走 `toErrorMessage`；成功才寫入）、el-empty 條件加 `!errorMessage`。同一命令：` Test Files  2 passed (2)`／`      Tests  170 passed (170)`；全套 `docker compose exec -T web npx vitest run`：` Test Files  48 passed (48)`／`      Tests  712 passed (712)`；`docker compose exec -T web npx vue-tsc --noEmit` 無輸出（通過）。
  - 還原驗證（前端 `verify-test-evidence.sh` 只認 dotnet 的 `Failed!`，對 Vitest 會判為「無法判定」，故以手動 mutate：備份 → 套用單一改動 → 跑同一命令 → 以備份還原，`git diff --stat` 還原後與改動前一致）：
    - M1 el-empty 條件去掉 `&& !errorMessage`：`Tests  4 failed | 166 passed (170)`；失敗＝`[BW-DETAIL-003]` 兩個子案例、`[BW-DETAIL-005]` 兩個子案例。
    - M2 活動 404 判定恆為 true（`isNotFoundError(eventResult.reason)` → `true`）：`Tests  8 failed | 162 passed (170)`；失敗含 `[BW-DETAIL-003]` 兩個子案例、`[BW-FLOW-004] 活動載入失敗…`、`[BW-FLOW-003] (i)(j)(k)`、`[BW-FLOW-007]` 等。
    - M3 只在活動失敗時拋出（略過座位／票種失敗檢查）：`Tests  2 failed | 168 passed (170)`；失敗＝`[BW-DETAIL-005]` 兩個子案例。
  - 偏差備註：BW-DETAIL-003／005 spec 文字寫「顯示『載入活動資訊失敗』」，但 `loadData` 沿用既有 `toErrorMessage`——ApiError 會顯示回應自身訊息（如 `API 請求失敗（狀態碼 500）`），只有非 ApiError 才用固定文案。測試對 ApiError 500 斷言頁面錯誤存在且不顯示「找不到」，固定文案由非 ApiError 的既有 `[BW-FLOW-004]` 鎖定。使用者決定改 spec 文字（2026-11-09）：buyer-web-ui 的 BW-DETAIL-003／005 與 design 決策 3 已改為「顯示既有載入失敗訊息」，實作與測試不變。
  - 重構後重跑：N/A（無重構）。
  - 第 2 輪更新：新增 `[BW-DETAIL-002] loadData_WhenEventIs404ButSeatsAndTicketTypesFailWith500_ShowsNotFoundWithoutErrorAlert`（鎖定「以活動請求結果為準」）。上列 RED／GREEN 的 170 為新增前的當時原文，新增後為 171。該案例以手動 mutate 驗證（把 404 判斷改為「活動與座位皆 404 才視為不存在」）：`× [BW-DETAIL-002] loadData_WhenEventIs404ButSeatsAndTicketTypesFailWith500_ShowsNotFoundWithoutErrorAlert`、`AssertionError: expected 'API 請求失敗（狀態碼 404）' to contain '找不到這個活動'`、`Tests  1 failed | 167 skipped (168)`；還原後（`cmp` 與備份一致）`Test Files  2 passed (2)`／`Tests  171 passed (171)`。

## 3. 負載測試核對

- [x] 3.1 `loadtest/lib/admin-api.js` 的 `assertEventOnSale` 改為 `getOk(\`/api/events/${eventId}\`)`，移除 `Array.isArray`／`find`，保留販售中判斷與既有失敗訊息格式；同步 `loadtest/README.md` 若有提到列表核對。保留既有的 `withTags({}, 'setup')`。`assertEventOnSale` 另有 `loadtest/fixtures/purchase-flow-ux-event.js:73` 呼叫端，一併納入驗證。TDD N/A：k6 腳本無自動化單元測試（`loadtest/tests` 沒有涉及 `assertEventOnSale` 或 `/api/events` 的檢查，design 決策 4）；替代驗證（有可判定的通過／失敗斷言）：
    1. 先依 loadtest README 跑 seeder（`--buyers 2`），再以 `bash loadtest/fixtures/run-purchase-flow-ux-event.sh` 實際跑 purchase-flow-ux fixture（需其設定的 `PFUX_FIXTURE_LOCK_TOKEN`，不可直接呼叫 k6），執行到 `assertEventOnSale` 通過為止，exit code 為 0。
    2. 以 `scripts/verify-test-evidence.sh mutate` 暫時把傳給 `assertEventOnSale` 的 id 換成 `00000000-0000-0000-0000-000000000000`（fixture 的 eventId 來自剛建立的活動、無 env 可替換），執行同一路徑，必須 fail 且訊息含 `GET /api/events/<實際 id> -> 404`（`describeFailure` 格式）（失敗訊息由原本的「not found in GET /api/events」改為 `describeFailure` 格式，語意不變、格式改變）。
    3. 驗證會寫入 dev DB（建立活動、票種、訂單；fixture 保留上限 RETENTION_LIMIT=6），完成後依 loadtest README「清理」段落清理，並以 restore 還原腳本。
    4. 以 curl 對 `/api/events/{id}` 核對回應欄位含 `salesStartAtUtc`、`salesEndAtUtc`、`isQueueModeEnabled`、`startAtUtc`。
  - 完成紀錄：TDD N/A（理由如上，k6 腳本無自動化測試）。`loadtest/lib/admin-api.js` 的 `assertEventOnSale` 改為 `getOk(`/api/events/${eventId}`, withTags({}, 'setup'))`，移除 `Array.isArray`／`find`，販售中判斷不變；`loadtest/README.md` 僅在步驟 2 以 `/api/events` 輪詢 api 就緒，未提及列表核對，不需修改。替代驗證：
    1. seeder `--buyers 2` → 輪詢就緒 → `bash loadtest/fixtures/run-purchase-flow-ux-event.sh`：`exit=0`，輸出 `PURCHASE_FLOW_UX_EVENT_ID=4e984fd5-a969-4ec3-a739-42b5269d246c`（執行到 `assertEventOnSale` 通過）。
    2. 暫時把 `purchase-flow-ux-event.js` 的 `assertEventOnSale(eventId)` 改為零 GUID（`verify-test-evidence.sh mutate` 只認 dotnet 的 `Failed!`，對 k6 會判為無法判定，改手動備份／還原，`git status` 還原後該檔無變更）：`exit=107`，`level=error msg="GoError: setup failed: GET /api/events/00000000-0000-0000-0000-000000000000 -> 404 {"title":"NotFound","status":404,"detail":"Event '00000000-0…`（`describeFailure` 格式，含狀態碼 404）。
    3. curl `/api/events/4e984fd5-…` 回應含 `"startAtUtc":"2026-11-10T07:42:25.279Z"`、`"isQueueModeEnabled":false`、`"salesStartAtUtc":"2026-11-09T07:41:25.279Z"`、`"salesEndAtUtc":null`。
    4. 清理：驗證共建立 5 個 `[PurchaseFlowUx] 2026-11-09T07:4…` 活動（含下述無效的前兩次），以交易刪除其訂單／票券／票種／座位／座位圖／場館（`remaining_pfux = 0`），並刪除 `loadtest/.output/tokens.json`。
    - 過程中的坑（已記錄，非程式缺陷）：首次執行得 404 的原因是 api 的 `dotnet watch` 卡在 `ENC0033`（切分支後需重啟）；重啟後又因 1.2 的 mutate 2（`Ok(result.Value)`）留下的編譯產物未重建，不存在的 id 回 204 而非 404。`touch` 兩個原始檔並 `docker compose restart api` 後才得到正確的 404。mutate 結束後務必 `touch` 並確認 api 重建。上述 1、2 的有效結果皆在重建後取得。

## 4. 防禦性最終檢查與審查

- [x] 4.1 hardener 最終檢查：對 `GetEventByIdHandler`、`EventsController.GetEventById` 套用 `.claude/skills/hardener/SKILL.md` 清單（參數驗證、Entity 存在性、`CancellationToken` 傳遞、例外邊界、日誌遮蔽）；若有補強依 TDD 先補 RED 測試。
  - 完成紀錄：套用 hardener 清單，未發現缺少的防禦分支，程式碼無修改（不需補 RED 測試）：參數驗證——`{id:guid}` 路由限制擋掉非 GUID（`/api/events/not-a-guid` 回 404，路由層），`Guid.Empty` 走不存在分支回 404（`HandleAsync_WhenIdIsEmptyGuid_ReturnsNotFound`）；Entity 存在性——找不到回 `Error.NotFound`（`EVT-DETAIL-002`、HTTP 404 ProblemDetails 測試）；狀態機——無狀態轉換，N/A；`CancellationToken`——傳給 `GetEventsHandler.HandleAsync`（`HandleAsync_PassesCancellationTokenToRepository` 類案例以 `FakeEventRepository.LastGetAllToken` 鎖定）；並發——唯讀、無寫入，N/A；例外邊界——Redis 失效沿用 `GetEventsHandler` 既有 fail-open（`GetEventById_WhenRedisUnreachable_FallsBackToDatabaseAndLogsWarning`），其餘例外交全域 `IExceptionHandler`，Controller 無 try-catch；日誌遮蔽——新程式碼不寫 log、不含個資。

- [x] 4.2 全套測試：`docker compose exec -T api dotnet test`（各測試專案）與 `docker compose exec -T web npm run test`，逐專案貼摘要行（含 Passed／Failed／Skipped 數）；任何 skipped 需列出原因。
  - 完成紀錄：`docker compose exec -T api dotnet test`：
    - `Passed!  - Failed:     0, Passed:   153, Skipped:     0, Total:   153, Duration: 126 ms - ProjectC.Domain.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   609, Skipped:     0, Total:   609, Duration: 2 s - ProjectC.Application.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 10 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   280, Skipped:     0, Total:   280, Duration: 52 s - ProjectC.Infrastructure.Tests.dll (net10.0)`
    - `Failed!  - Failed:     1, Passed:   787, Skipped:     0, Total:   788, Duration: 3 m 22 s - ProjectC.WebApi.Tests.dll (net10.0)`：失敗 `ProjectC.WebApi.Tests.Startup.ServiceRegistrationSnapshotTests.ServiceRegistrations_MatchCommittedBaseline`，訊息 `多出的項目：Scoped | ProjectC.Application.Events.GetEventById.GetEventByIdHandler | ProjectC.Application.Events.GetEventById.GetEventByIdHandler, but found 1`——新增 `GetEventByIdHandler` 的 DI 註冊使 DI 基準檔需有意更新（測試設計如此）。在 `ServiceRegistrationSnapshot.baseline.txt` 加入該一行（只此一行差異）後重跑 `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests`：`Passed!  - Failed:     0, Passed:   788, Skipped:     0, Total:   788, Duration: 3 m 59 s - ProjectC.WebApi.Tests.dll (net10.0)`。
    - 前端 `docker compose exec -T web npx vitest run`：` Test Files  48 passed (48)`／`      Tests  712 passed (712)`；`vue-tsc --noEmit` 通過；`npm run build` 完成（`✓ built in 1.92s`）。
    - skipped：0。
  - 第 2 輪（程式碼與測試最終版，Opus 建議 ①②③ 之後）重跑全套，上列為第 1 輪當時原文，現況原文：
    - `Passed!  - Failed:     0, Passed:   153, Skipped:     0, Total:   153, Duration: 118 ms - ProjectC.Domain.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   608, Skipped:     0, Total:   608, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)`（刪 1 個測試）
    - `Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 9 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   280, Skipped:     0, Total:   280, Duration: 52 s - ProjectC.Infrastructure.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   788, Skipped:     0, Total:   788, Duration: 3 m 7 s - ProjectC.WebApi.Tests.dll (net10.0)`（含已補基準線的 ServiceRegistrationSnapshotTests）
    - 前端：` Test Files  48 passed (48)`／`      Tests  713 passed (713)`（新增 1 個）。skipped：0。

- [x] 4.3 審查迴圈：依序 `strict-reviewer` 與全新的對抗式 subagent，修正至兩者皆無 blocking；再以 herdr Claude Opus 做交叉審查。每輪結果與 commit 記於此。
  - 完成紀錄（進行中）：第 1 輪（未 commit 的工作樹）——`strict-reviewer`：PASS，無 blocking（自行重跑全套通過；未逐項核對 tasks.md 證據原文，已列為審查限制）。全新對抗式 general-purpose subagent：無 blocking；次要建議 ① BW-DETAIL-004 無獨立標題的測試——已由 `下單時座位已被搶先鎖定…`、`[BW-CONFIRM-015] …WhenNavigationToEventListFails…`、`[BW-CONFIRM-020] …WhenLeavingFailsAfterSuccess…` 三案例以 `getEventById` ×2、`getEvents` 零次斷言涵蓋三條重載路徑；② `isNotFoundError` 只看 status 404（行為邊界，不處理）；④ 每次單筆查詢反序列化整份列表（design 已記錄取捨）。herdr Claude Opus 5.5 交叉審查（2026-11-09，工作樹未 commit，基底 1fb2e32）：無 blocking，6 項建議；採納 ①②③：
    - ① buyer-web-ui Requirement 段落改為「以活動請求結果為準」，BW-DETAIL-002 Scenario 補同句，新增測試 `[BW-DETAIL-002] loadData_WhenEventIs404ButSeatsAndTicketTypesFailWith500_ShowsNotFoundWithoutErrorAlert`；mutate（404 判斷加上「座位也 404」）使其失敗：`AssertionError: expected 'API 請求失敗（狀態碼 404）' to contain '找不到這個活動'`，`Tests  1 failed | 167 skipped (168)`；還原後 `Test Files  2 passed (2)` / `Tests  171 passed (171)`，`vue-tsc exit 0`。
    - ② `EventDetailPage.test.ts` 三條重載路徑測試名稱加上 `[BW-DETAIL-004]`。
    - ③ 刪除 `GetEventByIdHandlerTests.HandleAsync_WhenCacheUnavailableTreatedAsMiss_StillReturnsEventFromDatabase`（與未命中測試重複、未模擬無法使用；EVT-DETAIL-008 由 `QueryCachingFailOpenTests` 的 HTTP 層測試涵蓋；1.1 與 mutate 紀錄中對該測試名稱的引用為當時狀態）；刪除後 `Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8`。
    - 不處理：④⑤⑥（Handler 不依身份、token 為直接轉呼叫、既有行為未退化）。
    - 因動到規格與測試，須重跑 `strict-reviewer` 與全新對抗式 subagent（第 2 輪，進行中）。
  - 第 2 輪（工作樹未 commit，基底 1fb2e32）：`strict-reviewer` 先 FAIL（B-001～B-003，皆為 tasks.md 證據與現況不符，程式碼無需改）；補齊 1.1／2.1／4.2 證據後第 3 輪 `strict-reviewer`：PASS，無 blocking（重跑 Application 608、GetEventByIdHandlerTests 8、前端全套 713、相關前端 171 皆與紀錄相符）。全新對抗式 general-purpose subagent：無 blocking；建議 ① 每次單筆查詢反序列化整份列表（design 決策 1 已接受）、② 剛建立活動 TTL 內可能 404（EVT-DETAIL-001 已寫明，行為同舊版）、③ `as PromiseFulfilledResult` 強轉型略笨重（不處理）。兩者皆無 blocking，審查迴圈結束；Opus 交叉審查見上。

## 5. 前端真實瀏覽器驗證

- [x] 5.1 以 claude-in-chrome 實際操作：開啟存在活動的詳情頁（網路面板確認呼叫 `/api/events/{id}` 而非 `/api/events`，活動資訊與座位正常）；開啟不存在但格式合法的活動 id 確認顯示「找不到這個活動」；以瀏覽器重新載入頁面，以及製造一次下單失敗（例如座位已被搶先鎖定）後的重新載入，確認再次呼叫單筆端點而非列表。數字與狀態以頁面文字或 DB 核對，不只依截圖。
  - 進行中紀錄（2026-11-09，https://localhost:5173，Demo Admin 登入）：
    - 存在活動 `2479627a-…`：整頁載入後 `performance.getEntriesByType('resource')` 的 `/api/` 請求為 `/api/events/2479627a-…`、`/seats`、`/ticket-types`，無 `/api/events` 列表請求；頁面文字顯示「城市之聲 年度演唱會 2026」、販售期間、A/B/C 分區票價（NT$3,800/2,800/1,800）、A 區 80、B 區 120、C 區 150 個座位、「每筆訂單限購 4 張」。
    - 不存在活動 `00000000-0000-4000-8000-000000000001`：三個請求皆 404，頁面 innerText 為「找不到這個活動」。
    - 整頁重新載入即上述整頁載入，行為相同。
    - 下單失敗後重新載入：在詳情頁選 A 區 1 號、進入確認步驟後，以 psql 將該 EventSeat（`9b285cde-…`）設為被另一個不存在的訂單 `11111111-…` 保留 10 分鐘（模擬他人搶先鎖座），再勾選同意並按「確認訂單」。清除 resource timing 後的 `/api/` 請求依序為 `/api/orders` 409、`/api/events/2479627a-…` 200、`/seats` 200、`/ticket-types` 200，無 `/api/events` 列表請求；頁面訊息「Seat '9b285cde-…' is no longer available.」，網址回到 `?step=confirm` 之前的詳情頁，A 區 1 號 aria-label 為「保留中」。事後以 psql 還原該座位（`HeldByOrderId`/`HeldUntilUtc` 皆 NULL），該活動保留中座位數為 0，無殘留訂單保留。
  - 完成紀錄：2026-11-09 以上三項（存在活動、不存在活動、下單失敗後重載）皆已於真實瀏覽器驗證，頁面文字／請求狀態／DB 核對一致。

## 6. 規格同步檢查

- [x] 6.1 歸檔前確認：`event-catalog`、`buyer-web-ui` delta 與實作一致；`docs/api-conventions.md`、`docs/api-conventions-todo.md` 本 change 不需更新（Change 2 處理）；`openspec validate public-event-detail-endpoint` 通過。
  - 完成紀錄：（實作時填寫）
  - 完成紀錄（2026-11-09）：`event-catalog` delta（EVT-DETAIL-001～008）與 `buyer-web-ui` delta（BW-DETAIL-001～005、MODIFIED 兩條 Requirement 的 `GET /api/events/{id}` 描述）已與實作逐項核對一致（EVT 與 BW 各 Scenario 的對應測試見 1.1、1.2、2.1、3.1，並經 strict-reviewer 與兩位審查者核對）。`docs/api-conventions.md` 第 66、71 行的 `api/events` 僅涉及路由前綴例外與列表分頁後續 change，單筆端點未新增 ErrorType、Policy、Claim 或路由前綴，不需更新；`docs/api-conventions-todo.md` 本 change 不改（Change 2 處理）。`openspec validate public-event-detail-endpoint`：`Change 'public-event-detail-endpoint' is valid`，僅一個 WARNING：`event-catalog/spec.md: ADDED "公開單一活動查詢端點": Requirement text is very long (>500 characters)`（長 Requirement 警告，與專案既有 spec 同類，非錯誤）。
