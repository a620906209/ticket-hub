# Tasks

> 對應 spec：`event-catalog`（ADDED：EVT-LIST-001～006、EVT-LIST-ORDER-001～004）、`event-catalog`（MODIFIED：EVT-DETAIL-004～006 key 同步）、`ticket-purchase`（MODIFIED：TP-BROWSE-RN-001 key 同步）、`real-name-verification`（MODIFIED：部署切換 Requirement 的 key 名同步，無 Scenario 行為變更）、`query-caching`（MODIFIED：QC-EVT-001～003、QC-EVT-INV-001／002、QC-TTL-001～004、QC-ACCESS-001～003、QC-EVT-VER-001～005）、`buyer-web-ui`（MODIFIED：「瀏覽活動列表」Scenario；ADDED：BW-LIST-PAGE-001～008）。
> 所有後端命令在容器內執行：`docker compose exec -T api dotnet test <專案> --filter "..."`；前端：`docker compose exec -T web npm run test -- <檔案>`。
> 每個 task 的「完成紀錄」於實作時依 CLAUDE.md「證據貼原文」填寫（RED：命令、失敗測試名、Expected／Actual 原文，需在修改正式程式碼前；GREEN：命令與摘要行；重構後重跑或 N/A）。本 change 非 bug fix、也非下單／排隊／座位／庫存核心邏輯，「RED 還原驗證」以 mutate 輔助（排序與切頁各至少一個改動）；其餘 N/A。
> 前提：`IEventRepository.GetAllAsync` 不改（`PurchaseQueueAdmissionService` 也使用）；`GetEventByIdHandler`、`CreateEventHandler`、`SetEventQueueModeHandler` 行為不變。

## 1. 後端：排序、切頁與 v3 快取（Application）

- [x] 1.1 在 `tests/ProjectC.Application.Tests/Events/GetEvents/GetEventsHandlerTests.cs`（既有檔）先寫測試並更新既有測試的建構（Handler 建構子新增 `IValidator<PageRequest>`、`HandleAsync` 改收 `PageRequest`）：
  - 排序（EVT-LIST-ORDER-001～003）：建立時間新到舊、null 最後、同時間（含同為 null）依 `Id` 升冪（以 `Guid.CompareTo` 產生預期順序）。
  - 切頁（EVT-LIST-001～004）：預設 1／20、`page=3,pageSize=20` 的第 41–45 筆、超過最後一頁回空 `items` 且 `totalCount` 不變、空資料 `totalCount=0`；逐頁讀取每筆恰好一次（EVT-LIST-ORDER-004）。
  - 驗證（EVT-LIST-005 handler 層）：`page=0`、`page=10001`、`pageSize=0`、`pageSize=101` 回 `Result.Failure(Validation)` 且 `errors` 指出對應欄位，Repository 與 `IQueryCache` **皆未被呼叫**（`GetAsync`／`SetAsync`／`GetAllAsync` 計數為 0）。
  - 快取（QC-EVT-001～003、QC-EVT-VER-005）：未命中時 `SetAsync` 的 key 為 `query-cache:events:list:v3`，寫入的是**全部**活動（請求 `page=2,pageSize=5` 也一樣）且已排序；命中時 Repository 不被呼叫、且切頁結果與未命中相同；空列表仍寫入快取。
  - `GetAllOrderedAsync`（新增的內部方法）：回傳整份有序列表、命中／未命中行為同上。
  - hardener 對照：`CancellationToken` 傳遞到 `GetAsync`／`SetAsync`／`GetAllAsync`（斷言收到的 token）；`GetAllAsync` 回傳 null 列表不在此層防禦（Repository 契約回傳非 null），不新增分支。
  - 預期 RED：測試對尚未存在的 `HandleAsync(PageRequest, ...)`／`GetAllOrderedAsync`／`v3` 編譯失敗**不算 RED**；先加介面空殼（建構子加 `IValidator<PageRequest>`、`HandleAsync` 回空頁、`GetAllOrderedAsync` 沿用現行未排序內容、`CacheKey` 仍為 `v2`）使專案可編譯，再以斷言失敗（Expected／Actual）作為 RED。
  - 空殼會讓同專案的 `GetEventByIdHandler` 呼叫端與測試編譯失敗，因此 1.1 一併做兩處過渡改動（過渡寫法是 1.2 RED 的來源）：`GetEventByIdHandler`（`GetEventByIdHandler.cs:18`）暫時改為 `HandleAsync(new PageRequest(1, 20), ct)` 取 `.Value.Items` 做 `FirstOrDefault`（刻意保留「只看第一頁」缺陷）；`GetEventByIdHandlerTests` 的 `new GetEventsHandler(...)` 補上 validator 參數。
  - 快取 key 的斷言（含 QC-EVT-VER 系列）一律寫字面值 `query-cache:events:list:v3`，不引用 `GetEventsHandler.CacheKey`，避免常數被改回去時測試仍通過。
  - 實作：`CacheKey` 升為 `...:list:v3`；新增 `GetAllOrderedAsync`（現行 cache-aside 邏輯，未命中時 `events.OrderBy(e => e.CreatedAtUtc == null).ThenByDescending(e => e.CreatedAtUtc).ThenBy(e => e.Id)` 後才轉 `EventDto`）；`HandleAsync(PageRequest, ct)`：先驗證 → `GetAllOrderedAsync` → `Skip/Take` → `PagedResultDto<EventDto>`。
  - 完成紀錄（2026-11-09）：
    - 測試先行：先加空殼（建構子加 `IValidator<PageRequest>`、`HandleAsync(PageRequest)` 回空頁、`GetAllOrderedAsync` 沿用未排序內容、`CacheKey` 仍 v2）使專案可編譯；`GetEventByIdHandler` 過渡為只取第一頁（`new PageRequest(1, 20)`）。`FakeQueryCache` 新增 `LastGetToken`／`LastSetToken` 供 token 斷言。
    - RED（正式程式碼修改前）：`docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~GetEventsHandlerTests"`
      ```
      Failed ...GetEventsHandlerTests.HandleAsync_WithDefaultPaging_ReturnsFirstTwentyAndTotalCount
        Expected page.TotalCount to be 45, but found 0 (difference of -45).
      Failed ...GetEventsHandlerTests.HandleAsync_WhenCreatedAtDiffers_OrdersNewestFirst
        Expected page.Items.Select(e => e.Id) to be equal to {{8ab55d06-...}, {44f25d93-...}, {2cbff3a5-...}}, but found empty collection.
      Failed ...GetEventsHandlerTests.HandleAsync_WhenPagingOutOfRange_ReturnsValidationErrorWithoutTouchingCacheOrRepository(page: 1, pageSize: 101, expectedField: "PageSize")
        Expected result.IsSuccess to be False, but found True.
      Failed ...GetEventsHandlerTests.HandleAsync_PassesCancellationTokenToCacheAndRepository
        Expected _queryCache.LastGetToken to be System.Threading.CancellationToken {...}, but found <null>.
      Failed ...GetEventsHandlerTests.GetAllOrderedAsync_WhenCacheMisses_ReturnsWholeOrderedListAndCachesIt
        Expected all.Select(e => e.Id) to be equal to {{1853db1b-...}, {c8089930-...}, {91c3dc6d-...}}, but {{91c3dc6d-...}, {c8089930-...}, {1853db1b-...}} differs at index 0.
      Failed ...GetEventsHandlerTests.HandleAsync_WhenNoEventsExist_StillWritesEmptyListToCache
        Expected _eventRepository.GetAllCallCount to be 1 because 第二次應命中快取的空列表，不再查詢資料庫, but found 0 (difference of -1).
      Failed!  - Failed:    19, Passed:     1, Skipped:     0, Total:    20, Duration: 213 ms - ProjectC.Application.Tests.dll (net10.0)
      ```
      （節錄自輸出尾段 150 行，並非 19 筆全文；失敗原因皆為斷言不符，非編譯或環境錯誤。）
    - GREEN：同命令 → `Passed!  - Failed:     0, Passed:    20, Skipped:     0, Total:    20, Duration: 147 ms - ProjectC.Application.Tests.dll (net10.0)`
    - mutate（`bash scripts/verify-test-evidence.sh mutate`，一次一個改動，結束後已還原，`git diff --stat` 僅剩實作改動）：
      - `.ThenByDescending(e => e.CreatedAtUtc)` → `.ThenBy(e => e.CreatedAtUtc)`：`Failed!  - Failed:     4, Passed:    16, Skipped:     0, Total:    20`（含 `HandleAsync_WhenCreatedAtDiffers_OrdersNewestFirst`）；結果：測試如預期失敗，exit=0
      - `.OrderBy(e => e.CreatedAtUtc == null)` → `.OrderByDescending(e => e.CreatedAtUtc == null)`：`Failed!  - Failed:     4, Passed:    16, Skipped:     0, Total:    20`（含 `HandleAsync_WhenCreatedAtIsNull_PlacesThoseEventsLast`、`HandleAsync_WhenCreatedAtTies_OrdersByIdAscending`）；結果：測試如預期失敗，exit=0
      - `all.Skip((pageRequest.Page - 1) * pageRequest.PageSize)` → `all.Skip(pageRequest.Page * pageRequest.PageSize)`：`Failed!  - Failed:     8, Passed:    12, Skipped:     0, Total:    20`（含 `HandleAsync_OnLastPartialPage_ReturnsRemainingItems`：Expected page.Items to contain 5 item(s), but found 0: {empty}.）；結果：測試如預期失敗，exit=0
    - 重構：N/A。
- [x] 1.2 `GetEventByIdHandler` 改呼叫 `GetEventsHandler.GetAllOrderedAsync`，在 `tests/ProjectC.Application.Tests/Events/GetEventById/GetEventByIdHandlerTests.cs` 更新既有測試的建構並新增：活動排在第 2 頁之後（共 25 筆、目標為最舊）仍回 `Success`（EVT-LIST-006）；既有 EVT-DETAIL-001～008 的 handler 層測試維持通過。
  - 預期 RED：1.1 的過渡寫法下（`GetEventByIdHandler` 只看第一頁），「目標在第 2 頁之後」測試因找不到而 `NotFound`（Expected IsSuccess True／Actual False）。GREEN：改呼叫 `GetAllOrderedAsync`。
  - 完成紀錄（2026-11-09）：
    - RED（1.1 GREEN 後、`GetEventByIdHandler` 仍為只看第一頁的過渡寫法）：`docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~GetEventByIdHandlerTests"`
      ```
      Failed ProjectC.Application.Tests.Events.GetEventById.GetEventByIdHandlerTests.HandleAsync_WhenEventIsBeyondFirstListPage_ReturnsSuccess
        Expected result.IsSuccess to be True because 25 筆時最舊的活動排在第 2 頁，不得因列表分頁而查不到, but found False.
      Failed!  - Failed:     1, Passed:     8, Skipped:     0, Total:     9, Duration: 88 ms - ProjectC.Application.Tests.dll (net10.0)
      ```
    - GREEN（改呼叫 `GetAllOrderedAsync`）：`--filter "FullyQualifiedName~GetEventByIdHandlerTests|FullyQualifiedName~GetEventsHandlerTests"` → `Passed!  - Failed:     0, Passed:    29, Skipped:     0, Total:    29, Duration: 136 ms - ProjectC.Application.Tests.dll (net10.0)`
    - 既有 `GetEventByIdHandlerTests` 的 key 改為字面值 `v3`、建構補 validator；重構：N/A。

## 2. 後端：Controller 與整合測試（WebApi／Infrastructure）

- [x] 2.1 在 `tests/ProjectC.WebApi.Tests/Events/EventsControllerTests.cs`（既有檔，`CustomWebApplicationFactory`）更新既有 `GET /api/events` 測試為新回應結構，並新增：EVT-LIST-001（匿名、未帶參數：`page=1,pageSize=20`、`totalCount`、`items` 欄位與 `GET /api/events/{id}` 一致）、EVT-LIST-002／003／004、EVT-LIST-005（`pageSize=101`、`page=0`、`page=10001`、`page=abc`、`page=` 皆 400 `Validation`，`errors` 鍵為小寫 `page`／`pageSize`）、EVT-LIST-ORDER-001～004（以 HTTP 建立時間不同、null 的活動驗證順序與逐頁不重複）、EVT-LIST-006（排在第 2 頁之後的活動 `GET /api/events/{id}` 回 200）。
  - 隔離策略：`EventsControllerTests` 的 `IClassFixture<CustomWebApplicationFactory>` 共用 DB 與 Redis，公開列表是全域資料。依賴精確總數或絕對位置的情境（EVT-LIST-001～003、ORDER）放進**獨立的測試類別**（各自獨立 factory／全新容器）；EVT-LIST-004（`totalCount=0`）只在 handler 層驗證，HTTP 層不測。其餘共用 fixture 的測試只斷言「基準 `totalCount`＋自己建立的筆數」與自己建立之活動間的相對順序。
  - 測試資料操作：以 `ExecuteUpdate` 或 SQL 改 `CreatedAtUtc` 不會失效快取，更新後必須先刪除 `query-cache:events:list:v3`（或確保更新前沒有任何 `GET /api/events`）。
  - 邊界：EVT-LIST-005 沿用 `PaginationTestSupport.InvalidPagingQueries` 的 Theory，另補 LP-VALID 邊界值（`pageSize=100`、`page=10000` 回 200）與 `2147483647`。
  - 連帶測試：`AdminEventsControllerTests` 的 `QueryEvents_WhenStoredStartIsUtc_AllQueryEndpointsReturn200`（`ReadStartAtUtcJsonAsync`）與 `QueryEvents_WhenStoredMaxTicketsPerOrderExceedsSystemLimit_ListsStillLoad`（`ReadMaxTicketsPerOrderJsonAsync`）把公開列表根節點當陣列解析，改傳 `isPaged: true`；實作時另以 `rg '"/api/events"' tests --glob '*.cs'` 逐檔判斷是否需更新 body 解析。
  - 預期 RED：action 仍回整份陣列時，結構與 400 斷言失敗（Expected／Actual 取自實際輸出）。
  - 實作：`EventsController.GetEvents` 改為 `(CancellationToken, [FromQuery] int page = PageRequest.DefaultPage, [FromQuery] int pageSize = PageRequest.DefaultPageSize)`，呼叫 `HandleAsync(new PageRequest(page, pageSize), ct)` 並 `result.ToActionResult(Ok)`；維持匿名；更新 `ServiceRegistrationSnapshot.baseline.txt`（若 `GetEventsHandler` 的註冊簽名有變）。
  - 完成紀錄（2026-11-09）：
    - RED（`EventsController.GetEvents` 仍為過渡寫法：回整份陣列、無分頁參數）：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~EventsListPagingTests|FullyQualifiedName~EventsControllerTests"`
      ```
      Expected response.StatusCode to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.OK {value: 200}.   （無效分頁參數 Theory 各案例）
      System.Text.Json.JsonException : The JSON value could not be converted to ProjectC.Application.Common.Paging.PagedResultDto`1[ProjectC.Application.Events.GetEvents.EventDto]. Path: $
      Expected document.RootElement.ValueKind to be JsonValueKind.Object {value: 1}, but found JsonValueKind.Array {value: 2}.   （未帶參數 envelope 測試）
      System.InvalidOperationException : The requested operation requires an element of type 'Object', but the target element has type 'Array'.   （GetEvents_AsAnonymous_DoesNotExposeAdminOnlyFields）
      Failed!  - Failed:    23, Passed:    68, Skipped:     0, Total:    91, Duration: 28 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - GREEN（實作 page／pageSize 與 `HandleAsync` + `ToActionResult(Ok)`，並把 `AdminEventsControllerTests` 兩處公開列表解析改為 `isPaged: true`）：同一命令 → `Passed!  - Failed:     0, Passed:    91, Skipped:     0, Total:    91, Duration: 28 s - ProjectC.WebApi.Tests.dll (net10.0)`（`FullyQualifiedName~EventsControllerTests` 也涵蓋 `AdminEventsControllerTests`）。
    - mutation（`bash scripts/verify-test-evidence.sh mutate --file src/ProjectC.WebApi/Controllers/EventsController.cs --from "new PageRequest(page, pageSize)" --to "new PageRequest(1, 20)" -- <同上命令>`）：
      ```
        Failed ProjectC.WebApi.Tests.Events.EventsListPagingTests.GetEvents_OverFortyFiveEvents_ReturnsPartialLastPageAndEmptyPageBeyondIt
        Failed ProjectC.WebApi.Tests.Events.EventsControllerTests.GetEvents_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(query: "?pageSize=0", expectedErrorKeys: ["pageSize"])
        Failed ProjectC.WebApi.Tests.Events.EventsControllerTests.GetEvents_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(query: "?page=0", expectedErrorKeys: ["page"])
        Failed ProjectC.WebApi.Tests.Events.EventsControllerTests.GetEvents_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(query: "?page=0&pageSize=101", expectedErrorKeys: ["page", "pageSize"])
        Failed ProjectC.WebApi.Tests.Events.EventsControllerTests.GetEvents_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(query: "?page=10001", expectedErrorKeys: ["page"])
        Failed ProjectC.WebApi.Tests.Events.EventsControllerTests.GetEvents_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(query: "?page=-1", expectedErrorKeys: ["page"])
        Failed ProjectC.WebApi.Tests.Events.EventsControllerTests.GetEvents_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(query: "?page=2147483647", expectedErrorKeys: ["page"])
        Failed ProjectC.WebApi.Tests.Events.EventsControllerTests.GetEvents_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(query: "?pageSize=101", expectedErrorKeys: ["pageSize"])
        Failed ProjectC.WebApi.Tests.Events.EventsListPagingTests.GetEvents_WalkingAllPages_OrdersByCreatedAtDescWithNullLastAndIdTiebreak
        Failed ProjectC.WebApi.Tests.Events.EventsControllerTests.GetEvents_WithUpperBoundPagingParameters_Returns200WithEnvelope
      Failed!  - Failed:    10, Passed:    81, Skipped:     0, Total:    91, Duration: 28 s - ProjectC.WebApi.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
      腳本結束後 `git diff` 確認 controller 已還原為實作版本。
    - 其他公開列表呼叫端：`rg 'GetAsync\("/api/events..."\)'` 其餘多為只檢查狀態碼或快取行為的測試，留待 2.2 同步；`ServiceRegistrationSnapshot.baseline.txt` 無需更新（DI 註冊未變）。重構：N/A。
- [x] 2.2 快取整合測試與既有測試的 key 同步（`CachingComponentTestWebApplicationFactory`，真實 Redis）。已知含 `v2` 字串或 `EventListCacheKeyV2` 的檔案：WebApi.Tests 的 `RealNameEventListCacheTests`（TP-BROWSE-RN-001）、`QueryCachingAccessScopeTests`（QC-ACCESS-001～003）、`QueryCachingComponentTests`、`QueryCachingFailOpenTests`、`QueryCachingEventDetailTests`（EVT-DETAIL-004～006）；Infrastructure.Tests 的 `QueryCacheEventInvalidationOrderingTests`、`QueryCacheTtlSafetyNetTests`（QC-TTL-001～004，第 31 行常數 `EventListCacheKeyV2`）；Application.Tests 的 `CreateEventHandlerTests`（約第 216 行，QC-EVT-INV）。實作時以 `rg "events:list:v2|EventListCacheKeyV2" tests --glob '*.cs'` 重新列出全部，不得遺漏：
  - QC-TTL-003：TTL 內持續命中，改觀察 v3 key；QC-ACCESS-003：eventId 格式錯誤在路由層被拒絕，行為不變，僅確認既有測試仍通過（key 同步後）；EVT-DETAIL-004～006、TP-BROWSE-RN-001：預置／斷言的 key 改為 v3，行為斷言不變。
  - 既有測試改為引用 `GetEventsHandler.CacheKey` 或改為 `v3`，並以回應 `items` 取代原本整份陣列斷言。
  - 新增 QC-EVT-VER-001（`list` 與 `list:v2` 存有未排序內容、v3 不存在 → 回傳排序後的頁面、v3 寫入完整有序列表、兩個舊 key 內容不變）、QC-EVT-VER-002（建立活動／開關熱門搶購模式只清除 v3，另外兩個 key 不變）、QC-EVT-VER-003（整個過程中 `list` 與 `list:v2` 不存在）、QC-EVT-VER-004（v3 原始內容兩個銷售期間屬性存在且 null 值不被省略）、QC-EVT-VER-005（請求 `page=2,pageSize=1` 後 v3 仍含全部活動且已排序）；另新增「不同 `page`／`pageSize` 共用同一個 v3 key，第二次請求命中（`CountingEventRepository` 的 `GetAllAsync` 計數不增加）」。
  - QC-EVT-INV-001／002、QC-TTL-001／002／004、QC-ACCESS-001／002 既有測試只需 key 與結構同步，行為斷言不變；Redis 不可用的 fail-open 測試（`QueryCachingFailOpenTests`）維持：Redis 連線失敗時 `GET /api/events` 仍回 200 分頁結構並有 Warning log。
  - 預期 RED：key 與結構更新後、尚未升版前的測試失敗；新增的 VER／共用 key 測試先因 key 仍為 `v2` 或內容未排序而失敗。
  - 完成紀錄（2026-11-09）：
    - RED（還原驗證：`bash scripts/verify-test-evidence.sh restore --path <WebApi 的 5 個 2.2 測試檔> -- docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~QueryCaching|FullyQualifiedName~RealNameEventListCacheTests"`；正式程式碼已是 v3 + 分頁，測試檔暫時還原為 2.2 之前的 v2／陣列版本，`git stash pop` 已還原，`Dropped refs/stash@{0}`）：
      ```
      Failed ProjectC.WebApi.Tests.Events.RealNameEventListCacheTests.Gates_WhenCachedEventListSaysRealNameNotRequired_StillRejectUnregisteredMember
        Expected cached.HasValue to be True because 暖機請求應已寫入活動列表快取，否則以下改寫沒有意義, but found False.
      Failed ProjectC.WebApi.Tests.Events.QueryCachingComponentTests.GetEvents_CalledTwice_SecondCallHitsCacheAndDoesNotQueryDatabaseAgain
        System.Text.Json.JsonException : The JSON value could not be converted to System.Collections.Generic.List`1[ProjectC.Application.Events.GetEvents.EventDto]. Path: $
      Failed ProjectC.WebApi.Tests.Events.QueryCachingEventDetailTests.GetEventById_WhenListCacheIsCold_QueriesAllOnceWritesListCacheAndLaterListHitsIt
        Expected (GetRedisDatabase(factory).KeyExistsAsync(EventListCacheKeyV2)) to be True, but found False.
      Failed ProjectC.WebApi.Tests.Events.QueryCachingComponentTests.SetEventQueueMode_WhenBothV1AndV2KeysExist_RemovesOnlyV2Key
        Expected (redis.KeyExistsAsync(EventListCacheKeyV2)) to be False, but found True.
      Failed!  - Failed:    15, Passed:    16, Skipped:     0, Total:    31, Duration: 1 m 51 s - ProjectC.WebApi.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
      （僅列代表性 4 筆；共 15 個失敗。新增的 VER-001／005 與共用 key 測試在還原版不存在，其失效證明見下方 mutation。）
    - GREEN（key 同步為 v3、結構改讀 `items`、`GetEventsHandler` 建構補 `PageRequestValidator` 並改呼叫 `GetAllOrderedAsync`；新增 `PreviousEventListCacheKeyV2` 版 QC-EVT-VER-001／002／003、新增 VER-005 與「不同分頁共用單一快取」測試）：
      ```
      Passed!  - Failed:     0, Passed:    33, Skipped:     0, Total:    33, Duration: 1 m 52 s - ProjectC.WebApi.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11, Duration: 30 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:    41, Skipped:     0, Total:    41, Duration: 168 ms - ProjectC.Application.Tests.dll (net10.0)
      ```
      （命令：WebApi `--filter "FullyQualifiedName~QueryCaching|FullyQualifiedName~RealNameEventListCacheTests"`；Infrastructure `--filter "FullyQualifiedName~QueryCacheEventInvalidationOrderingTests|FullyQualifiedName~QueryCacheTtlSafetyNetTests"`；Application `--filter "FullyQualifiedName~CreateEventHandlerTests"`。）
    - mutation（`bash scripts/verify-test-evidence.sh mutate --file src/ProjectC.Application/Events/GetEvents/GetEventsHandler.cs --from "list:v3" --to "list:v2" -- docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~QueryCachingComponentTests"`）：
      ```
        Failed ProjectC.WebApi.Tests.Events.QueryCachingComponentTests.GetEvents_WhenRequestingSecondPage_StoresWholeSortedListInV3
        Failed ProjectC.WebApi.Tests.Events.QueryCachingComponentTests.GetEvents_CalledTwice_SecondCallHitsCacheAndDoesNotQueryDatabaseAgain
        Failed ProjectC.WebApi.Tests.Events.QueryCachingComponentTests.NewVersion_SequenceNeverReadsOrWritesOlderEventListKeys
        Failed ProjectC.WebApi.Tests.Events.QueryCachingComponentTests.SetQueueMode_ByOwningOrganizer_InvalidatesEventListCache
        Failed ProjectC.WebApi.Tests.Events.QueryCachingComponentTests.SetEventQueueMode_WhenOlderKeysExist_RemovesOnlyV3Key
        Failed ProjectC.WebApi.Tests.Events.QueryCachingComponentTests.SetQueueMode_ByOtherOrganizer_ReturnsNotFoundAndDoesNotInvalidateCache
        Failed ProjectC.WebApi.Tests.Events.QueryCachingComponentTests.CreateEvent_WhenOlderKeysExist_RemovesOnlyV3Key
        Failed ProjectC.WebApi.Tests.Events.QueryCachingComponentTests.GetEvents_OnCacheMiss_WritesV2WithBothSalesWindowPropertiesEvenWhenNull
        Failed ProjectC.WebApi.Tests.Events.QueryCachingComponentTests.GetEvents_WhenOnlyOlderKeysExist_IgnoresThemAndWritesSortedV3
      Failed!  - Failed:     9, Passed:     2, Skipped:     0, Total:    11, Duration: 28 s - ProjectC.WebApi.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
      還原後已 `touch` 該檔再重跑上方 GREEN（mutate 還原不更新 mtime 會讓增量建置沿用被改動的二進位，曾因此誤判失敗）。重構：N/A。
- [x] 2.3 hardener 最終檢查（`GetEventsHandler`、`GetEventByIdHandler`、`EventsController` 全部完成後，套用 `.claude/skills/hardener/SKILL.md` 一次）：檢查參數驗證（`PageRequest` 驗證在碰快取前）、`CancellationToken` 傳遞、快取不可用的 fail-open、例外邊界；發現缺口依 TDD 補測試。無修改時記錄「已確認符合、無修改」。
  - 完成紀錄（2026-11-09）：已確認符合、無修改。逐項對照 hardener 清單（目標：`GetEventsHandler`、`GetEventByIdHandler`、`EventsController.GetEvents`）：
    - 參數驗證：`HandleAsync` 先 `_validator.ValidateAsync`，失敗回 `Result.Failure(ToValidationError())`，在碰快取與 Repository 之前；1.1 的驗證 Theory 斷言快取與 Repository 零呼叫，2.1 的 `InvalidPagingQueries` Theory 在 HTTP 層斷言 400 與欄位錯誤。`(Page-1)*PageSize` 只在驗證後計算（上限 9999×100，不溢位）。
    - Entity 存在性：`GetEventByIdHandler` 查無回 `Error.NotFound`（既有測試涵蓋）；列表查無資料為空 `items`、`totalCount=0`（1.1 測試）。
    - `CancellationToken`：`HandleAsync_PassesCancellationTokenToCacheAndRepository` 斷言原 token 傳到快取讀、快取寫與 Repository（1.1 記錄）。
    - fail-open／例外邊界：快取讀寫失敗由既有 `IQueryCache` 實作吞掉並記 Warning，`QueryCachingFailOpenTests` 在 2.2 同步為分頁結構後通過；Handler 內無 try/catch，技術性例外往外交給全域 `IExceptionHandler`。
    - 並發／日誌／狀態機：本次變更無寫入、無交易、無鎖、無新增 log，不適用。
    - 無程式碼修改，因此不需重跑 strict-reviewer 的額外輪次；重構：N/A。

## 3. 前端：API 層、型別與買家活動列表頁

- [x] 3.1 `web/src/api/events.ts` 與 `web/src/types/apiResponses.ts`：`getEvents(page)` 以 `?page=` 呼叫並回傳既有的 `PagedResult<EventSummary>`；在 `web` 容器執行 `npm run generate:api-types`，檢查 `api.generated.ts` diff 只含 `GET /api/events` 的 `page`／`pageSize` 查詢參數與回應結構，確認 `vue-tsc -b` 通過。同時把其他以 `getEvents` 陣列 mock 的前端測試改為分頁結構：`web/src/layouts/BuyerLayout.test.ts`、`web/src/pages/elementPlusOnDemand.test.ts`、`web/src/pages/buyer/orderConfirmationFlow.test.ts`、`web/src/pages/buyer/EventDetailPage.test.ts`（實作時以 `rg "getEvents" web/src --glob '*.test.ts'` 列出全部；`PlatformLayout.test.ts` 等若亦有，一併處理）。
  - 預期 RED：更新 `web/src/api/events` 對應測試（若有）與 `EventListPage.test.ts` 的 mock 為分頁結構後，頁面尚未改，測試失敗。
  - 完成紀錄（2026-11-09）：
    - 變更：`events.ts` 改為 `getEvents(page = 1): Promise<PagedResult<EventSummary>>` 呼叫 `/events?page=${page}`；`PagedResult<T>` 已存在於 `apiResponses.ts`，無需修改。新增 `web/src/api/events.test.ts`；`BuyerLayout.test.ts`、`elementPlusOnDemand.test.ts`（含 `createDeferred<PagedResult<EventSummary>>`）、`orderConfirmationFlow.test.ts`、`EventListPage.test.ts` 的 mock 改為 `{ items, page, pageSize, totalCount }`。`rg "getEvents" web/src --glob '*.test.ts'` 全部列出後確認 `EventDetailPage.test.ts` 只斷言 `getEvents` 未被呼叫，`PlatformLayout.test.ts` 無 `getEvents` mock，兩者無需修改。
    - RED（正式程式碼修改前；`docker compose exec -T web npx vitest run src/api/events.test.ts src/layouts/BuyerLayout.test.ts src/pages/elementPlusOnDemand.test.ts src/pages/buyer/orderConfirmationFlow.test.ts src/pages/buyer/EventListPage.test.ts`），失敗原因皆為目標行為不符（未帶 `page`、頁面仍把回應當陣列）：
      ```
       FAIL  src/api/events.test.ts > ... > getEvents_WithPage_RequestsThatPageAndReturnsEnvelope
      AssertionError: expected '/api/events' to be '/api/events?page=2' // Object.is equality
      Expected: "/api/events?page=2"
      Received: "/api/events"
       FAIL  src/api/events.test.ts > ... > getEvents_WithoutPage_RequestsFirstPage
      AssertionError: expected '/api/events' to be '/api/events?page=1' // Object.is equality
      Expected: "/api/events?page=1"
      Received: "/api/events"
       FAIL  src/pages/elementPlusOnDemand.test.ts > [BS-012] ... > 買家首頁：沒有活動時顯示 el-empty
      AssertionError: expected false to be true // Object.is equality
       FAIL  src/pages/buyer/EventListPage.test.ts > EventListPage（買家）販售狀態 > [BW-SALES-013] ...
      AssertionError: expected [ '', '', '', '' ] to deeply equal [ 'NotOpen Event', 'Open Event', …(1) ]
       Test Files  3 failed | 2 passed (5)
            Tests  4 failed | 20 passed (24)
      ```
    - GREEN（`events.ts` 修改後，同命令去掉 `EventListPage.test.ts`）：`events.test.ts` 2 passed、`BuyerLayout.test.ts` 8 passed、`orderConfirmationFlow.test.ts` 3 passed；`elementPlusOnDemand.test.ts` 的「買家首頁：沒有活動時顯示 el-empty」仍失敗（`Test Files  1 failed | 3 passed (4)`，`Tests  1 failed | 17 passed (18)`），因頁面尚未改，於 3.2 轉綠。
    - `npm run generate:api-types`：第一次產生的 diff 不含 `page`／`pageSize`，原因是 `api` 容器的 `dotnet watch` 仍服務舊的 `GET /api/events` 簽章（hot reload 未套用 controller 簽章變更；啟動時間 07:41）；`docker compose restart api` 後重新產生，`GET /api/events` 的 `query` 變為 `{ page?: number | string; pageSize?: number | string }`。diff 另含 `"/api/events/{id}"` 路徑新增（35 行，前一個 change `public-event-detail-endpoint` 當時未重新產生型別而遺漏）；除這兩處外無其他變更。
    - `docker compose exec -T web npx vue-tsc -b`：無輸出、通過（3.2 完成後執行）。
- [x] 3.2 `web/src/pages/buyer/EventListPage.test.ts`（既有檔）先寫測試並更新既有測試的 mock 為 `{ items, page, pageSize, totalCount }`：BW-LIST-PAGE-001（換頁以 `page=2` 重新呼叫、載入中狀態、列表換為第 2 頁）、002（總數不超過一頁不顯示 `el-pagination`）、003（`totalCount=0` 顯示「目前沒有活動」且不顯示分頁）、004（空頁且 `totalCount>0` 只重試一次最後一頁，重試仍空不再重試）、005（換頁失敗退回前一頁、列表保留、顯示錯誤）、006（首次載入失敗顯示錯誤、不顯示分頁與空狀態）、007（快速換頁丟棄過期回應）、008（頁碼不進網址、離頁返回回第 1 頁），並維持既有的販售狀態標籤與訂單建立提示測試。
  - 預期 RED：頁面仍呼叫舊的 `getEvents()` 且把回應當陣列時，分頁相關測試失敗（取實際輸出原文）。
  - 模板注意：現行為 `el-alert v-if=errorMessage` → `el-empty v-else-if` → 格線 `v-else`，錯誤時會隱藏列表；BW-LIST-PAGE-005 要求換頁失敗時保留前一頁列表並顯示錯誤，v-if 鏈須改為「錯誤提示與列表可並存」，首次載入失敗（BW-LIST-PAGE-006）仍不顯示列表、分頁與空狀態。
  - 實作：`EventListPage.vue` 沿用 `admin/EventListPage.vue` 的換頁邏輯（`currentPage`／`loadedPage`／`pageSize`／`totalCount`、請求序號、空頁重試、失敗還原）；`el-pagination` 於 `totalCount > pageSize` 時顯示；每次成功載入更新 `loadedAtMs`；`router-link` 卡片、`SalesStatusTag`、`placedOrderId` 提示不變。
  - 完成紀錄（2026-11-09）：
    - 實作：`EventListPage.vue` 新增 `currentPage`／`loadedPage`／`pageSize`／`totalCount`／`hasLoadedPage`、請求序號、空頁重試一次、失敗還原頁碼；模板改為 `el-empty v-if="hasLoadedPage && totalCount === 0"`、`div.event-grid v-else-if="hasLoadedPage || !errorMessage"`（換頁失敗保留列表；首次載入失敗不顯示列表）、`el-pagination v-if="hasLoadedPage && totalCount > pageSize"`；每次成功載入更新 `loadedAtMs`。另新增測試 BW-LIST-PAGE-004b（重試仍空不再重試）、007b（重試最後一頁期間的過期回應）。
    - RED（頁面修改前；`docker compose exec -T web npx vitest run src/pages/buyer/EventListPage.test.ts src/pages/elementPlusOnDemand.test.ts`）：
      ```
       FAIL  ... > [BW-SALES-013] ...        AssertionError: expected [ '', '', '', '' ] to deeply equal [ 'NotOpen Event', 'Open Event', …(1) ]
       FAIL  ... > [BW-LIST-PAGE-001] ...    （頁面把物件當陣列）
       FAIL  ... > [BW-LIST-PAGE-002] ...    AssertionError: expected [ DOMWrapper{ …(3) }, …(3) ] to have a length of 20 but got 4
       FAIL  ... > [BW-LIST-PAGE-003] ...    AssertionError: expected '活動列表Invalid Date販售中Invalid Date販售中Inv…' to contain '目前沒有活動'
       FAIL  ... > [BW-LIST-PAGE-004] / 004b / 005 / 007 / 008
       FAIL  src/pages/elementPlusOnDemand.test.ts > ... > 買家首頁：沒有活動時顯示 el-empty   AssertionError: expected false to be true
       Test Files  2 failed (2)
            Tests  10 failed | 10 passed (20)
      ```
      （BW-LIST-PAGE-006 在 RED 時已通過：既有行為「首次失敗顯示錯誤、無列表」本就成立，作為保留行為的回歸測試。）
    - GREEN：測試中的載入遮罩斷言改為 `vi.waitFor` + 可見性判斷（Element Plus 非同步套用、延遲移除遮罩，與 `AdminOrderListPage.test.ts` 同作法）後，`EventListPage.test.ts` `Test Files  1 passed (1)`、`Tests  16 passed (16)`；`elementPlusOnDemand.test.ts` 5 passed。
    - mutate／還原驗證（前端 vitest 不適用 `verify-test-evidence.sh`，其只認 dotnet `Failed!`，改以備份檔手動套用單一改動後 `cmp` 確認還原；每次一個改動，還原後 `touch`）：
      - 移除 `currentPage.value = loadedPage.value` → `FAIL [BW-LIST-PAGE-005] AssertionError: expected 2 to be 1`（1 failed | 14 passed）。
      - 移除第一次 await 後的過期檢查 → `FAIL [BW-LIST-PAGE-007] AssertionError: expected 'Event p2-0' to be 'Event p3-0'`。
      - 重試改請求 `resolvedPage + 1` → `FAIL [BW-LIST-PAGE-004] AssertionError: expected [ 1, 3, 3 ] to deeply equal [ 1, 3, 2 ]`。
      - 分頁元件條件改為 `hasLoadedPage` → `FAIL [BW-LIST-PAGE-002]`、`FAIL [BW-LIST-PAGE-003]`，皆 `AssertionError: expected true to be false`。
      - 移除重試 await 後的過期檢查：第一輪 mutation 仍全數通過（15 passed），證明缺測試；補 BW-LIST-PAGE-007b 後同改動 → `FAIL [BW-LIST-PAGE-007b] AssertionError: expected 'Event p2-0' to be 'Event p1-0'`（1 failed | 15 passed）。
      - 全部還原後 `cmp` 輸出 `RESTORED_OK`。
    - 全套前端（`docker compose exec -T web npm run test`）：`Test Files  49 passed (49)`、`Tests  725 passed (725)`。`vue-tsc -b` 無輸出、通過。

## 4. 文件

- [x] 4.1 `docs/api-conventions.md` 第 3 節「列表分頁」把 `GET /api/events` 由「後續 change」移到「已套用」（註明排序規則、單一整份快取 `v3`、單一活動端點不受影響）；`docs/api-conventions-todo.md` 第 6 項勾選；刪除 `docs/public-event-list-pagination-todo.md`。TDD：N/A（純文件）。
  - 完成紀錄（2026-11-09）：TDD N/A（純文件，無可自動化行為）。`docs/api-conventions.md` 第 3 節「列表分頁」列：`GET /api/events` 由「後續 change」移到「已套用」，註明排序同 `GET /api/admin/events`、單一整份快取 `query-cache:events:list:v3` 於記憶體切頁且不同 `page`／`pageSize` 共用、舊鍵 `list`／`list:v2` 不讀不寫、單一活動端點不受分頁影響。`docs/api-conventions-todo.md` 第 6 項「遷移其餘端點」改為 `[x]`。已刪除 `docs/public-event-list-pagination-todo.md`（工作樹刪除，未暫存、未 commit；`rg public-event-list-pagination-todo` 除本 change 自身的 proposal／design／tasks 外無其他引用）。

## 5. 全套驗證與收尾

- [x] 5.1 全套測試：`docker compose exec -T api dotnet test`（各專案）與 `docker compose exec -T web npm run test`，逐專案貼摘要行原文；`vue-tsc -b`、eslint 變更檔通過。
  - 完成紀錄（2026-11-09）：`docker compose exec -T api dotnet test --no-restore`（5 個測試專案）：
    ```
    Passed!  - Failed:     0, Passed:   153, Skipped:     0, Total:   153, Duration: 156 ms - ProjectC.Domain.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   624, Skipped:     0, Total:   624, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 10 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   280, Skipped:     0, Total:   280, Duration: 52 s - ProjectC.Infrastructure.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   808, Skipped:     0, Total:   808, Duration: 3 m 23 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
    前端 `docker compose exec -T web npm run test`：`Test Files  49 passed (49)`、`Tests  725 passed (725)`；`npx vue-tsc -b` 無輸出通過；`npx eslint <變更的 .ts/.vue，排除 api.generated.ts>` 結束碼 0、無輸出。無 skipped。
- [x] 5.2 真實瀏覽器驗證（claude-in-chrome，https://localhost:5173）：以 `docker compose exec db psql` 暫時新增至 45 場活動（插入後 DEL `query-cache:events:list:v3` 或等 TTL 過期）（含建立時間 null 與同時間者；記錄新增前後筆數，結束後還原），驗證匿名開啟買家列表：第 1 頁 20 張卡片、分頁元件顯示總數 45、換到第 3 頁顯示 5 張、頁碼不在網址、點進活動詳情頁（單一活動端點）後返回回到第 1 頁；`GET /api/events?pageSize=101` 回 400；數字以頁面文字與 DB 核對，不只看截圖。
  - 完成紀錄（2026-11-09，claude-in-chrome，https://localhost:5173）：
    - 準備：新增前 `Events` 7 筆；以 `insert ... select` 複製既有活動列新增 38 筆（標題 `ZZ-PAGETEST nn`，3 筆 `CreatedAtUtc` 為 null、3 筆同為 `2026-01-01 00:00:00+00`），新增後 45 筆，並 `DEL query-cache:events:list:v3`。
    - 第 1 頁（頁面 DOM 文字）：`.event-card` 20 張、分頁元件文字 `Total 45`、作用中頁碼 1、網址 `https://localhost:5173/`；第一張 `ZZ-PAGETEST 07`。
    - 點分頁 3（真實點擊）：卡片 5 張，標題依序 `ZZ-PAGETEST 05, 04, 02, 03, 01`，網址仍為 `https://localhost:5173/`（頁碼不在網址）。與 DB `order by "CreatedAtUtc" desc nulls last, "Id" asc offset 40` 結果逐筆一致（05、04 同時間依 Id 升冪；02、03、01 為 null 依 Id 升冪）。
    - 點第一張卡片進入詳情頁 `/events/c8bd8b15-9d43-49ce-9419-b1b033339f5f`（`h1` 為 `ZZ-PAGETEST 05`，即第 3 頁的活動，單一活動端點可取得）；`history.back()` 返回後作用中頁碼 1、卡片 20 張、`Total 45`（回到第 1 頁）。
    - `GET /api/events?pageSize=101` → 400（`pageSize` 欄位錯誤）。
    - 還原：`delete from "Events" where "Title" like 'ZZ-PAGETEST %'` 後 `Events` 回到 7 筆，並 `DEL query-cache:events:list:v3`。
- [x] 5.3 實作完成後執行 `graphify update .`，並在交接摘要列出 spec 同步狀態（`event-catalog`、`query-caching`、`buyer-web-ui`）。
  - 完成紀錄（2026-11-09）：`graphify update .` 輸出 `Code graph updated`（graph.json、graph.html、GRAPH_REPORT.md 已更新）。spec 同步狀態：實作與 delta spec 一致，無偏差；`event-catalog`（列表分頁、排序、單一活動端點不受影響）、`query-caching`（v3 單一整份快取、舊鍵不讀不寫）、`buyer-web-ui`（BW-LIST-PAGE-001～008）的 delta 已涵蓋本次行為，主 spec 於 archive 時同步，現階段不需另改。`docs/api-conventions.md`、`docs/api-conventions-todo.md` 已同步。維護性：分頁載入邏輯（請求序號、空頁重試、失敗還原）與 `admin/EventListPage.vue` 重複，目前僅兩處、行為有差異（買家保留列表與 `hasLoadedPage`），暫不抽 composable，出現第三處再抽。命名與既有慣例一致。
