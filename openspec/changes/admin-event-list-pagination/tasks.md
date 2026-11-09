# Tasks

> 對應 spec：`event-management`（MODIFIED：EVT-LIST-001～009）、`admin-web-ui`（ADDED：AWU-EVENT-PAGE-001～009）；`list-pagination` 沿用不修改（LP-* 場景由 2.1 的 HTTP 層測試覆蓋）。
> 既有 Scenario 覆蓋：EVT-LIST-001～004 語意未變，由更新後的既有測試（改為斷言分頁 envelope）覆蓋——`GetAdminEventsHandlerTests`、`AdminEventsControllerTests`；EVT-LIST-005（公開端點不回傳後台欄位）由不在本次修改範圍的 `EventsControllerTests.cs` 既有測試覆蓋，於 6.2 全套測試確認持續通過；各 task 的 RED 證據須逐一列出受影響的既有測試名稱。
> 所有後端命令在容器內執行：`docker compose exec -T api dotnet test <專案> --filter "..."`；前端：`docker compose exec -T web npm run test -- <檔案>`。
> 每個 task 的「完成紀錄」於實作時依 CLAUDE.md「證據貼原文」填寫（RED：命令、失敗測試名、Expected／Actual 原文、需在修改正式程式碼前；GREEN：命令與摘要行；重構後重跑或 N/A）。核心列表查詢的 task（1.1、2.1）另預留「還原驗證」紀錄，使用 `scripts/verify-test-evidence.sh`（mutate／restore 規則見 CLAUDE.md；mutate 後以 `touch` 被改檔案再重建，避免容器沿用舊 dll）。

## 1. Repository 分頁查詢（Domain／Infrastructure）

- [x] 1.1 在 `tests/ProjectC.Infrastructure.Tests/Events/` 新增 Testcontainers 整合測試 `EventRepositoryPagingTests`，測 `GetPageByOrganizerIdAsync(organizerId, skip, take, ct)`：先寫測試。涵蓋 EVT-LIST-001／006（`skip`／`take` 切頁；`TotalCount` 只計該 Organizer 名下活動，不含他人）、EVT-LIST-008（`CreatedAtUtc` 各不相同且寫入順序打亂 → 新到舊；建立時間相同 → `Id` 升冪；`CreatedAtUtc` 為 null 的活動排在所有有建立時間的活動之後，且 null 之間依 `Id` 升冪；逐頁合併後每筆恰好一次）、LP-RESP-003／004（skip 超過總數回空 `Items` 但 `TotalCount` 為實際總數；無資料回空與 0）。預期 RED：先在 `IEventRepository`／`EventRepository` 加入方法簽章並回 `new EventPage([], 0)` 使其可編譯（同步新增 Domain `EventPage`），測試因 Expected 筆數／順序不符失敗（不得以編譯失敗當 RED）。再實作：同一個範圍 query 供 `CountAsync` 與取頁，`AsNoTracking().OrderBy(e => e.CreatedAtUtc == null).ThenByDescending(e => e.CreatedAtUtc).ThenBy(e => e.Id).Skip().Take()`。並從 `IEventRepository` 與 `EventRepository` 移除 `GetByOrganizerIdAsync`。
  - RED 紀錄（修改正式程式碼前；`GetPageByOrganizerIdAsync` 先為回 `new EventPage([], 0)` 的空殼，故為行為失敗而非編譯失敗）：`docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~EventRepositoryPagingTests"`
    ```
    Failed ...EventRepositoryPagingTests.GetPageByOrganizerIdAsync_WhenInsertedOutOfOrder_ReturnsNewestFirst
      Expected page.Items.Select(e => e.Id) to be equal to {{5e18b3ee-0000-0000-0000-000000000004}, ... {5e18b3ee-0000-0000-0000-000000000000}}, but found empty collection.
    Failed ...GetPageByOrganizerIdAsync_WhenCreatedAtEqual_OrdersByIdAscending
      Expected page.Items.Select(e => e.Id) to be equal to {{48aeb540-0000-0000-0000-000000000001}, {...02}, {...03}}, but found empty collection.
    Failed ...GetPageByOrganizerIdAsync_WhenSomeCreatedAtNull_PutsNullLastAndEachEventAppearsOnceAcrossPages
    Failed ...GetPageByOrganizerIdAsync_WhenSkipBeyondTotal_ReturnsEmptyItemsWithActualTotalCount
    Failed ...GetPageByOrganizerIdAsync_WhenOtherOrganizerHasEvents_PagesAndCountsOnlyCallerOrganizerEvents
    Failed!  - Failed:     5, Passed:     1, Skipped:     0, Total:     6, Duration: 487 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
    ```
  - GREEN 紀錄（同命令，實作後）：`Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 686 ms - ProjectC.Infrastructure.Tests.dll (net10.0)`；`GetByOrganizerIdAsync` 於 2.1 Handler 改完後從介面與實作移除（1.1 期間暫留以維持編譯）。
  - 還原驗證紀錄（`bash scripts/verify-test-evidence.sh mutate`，一次一個改動，每次結果皆為「測試如預期失敗」）：
    ```
    MUTATION OrderBy(e => e.CreatedAtUtc == null) -> OrderBy(e => false)
      Failed ...WhenSomeCreatedAtNull_PutsNullLastAndEachEventAppearsOnceAcrossPages
      Expected collected to be equal to {{63d8b68c-...0002}, {...0003}, {...0001}, {...0005}, {...0006}}, but {{63d8b68c-...0005}, {...0006}, {...0002}, {...0003}, {...0001}} differs at index 0.
      Failed!  - Failed:     1, Passed:     5, Skipped:     0, Total:     6
    MUTATION ThenByDescending(e => e.CreatedAtUtc) -> ThenBy(e => e.CreatedAtUtc)
      Failed ...WhenInsertedOutOfOrder_ReturnsNewestFirst / ...WhenSomeCreatedAtNull_... / ...WhenOtherOrganizerHasEvents_...
      Failed!  - Failed:     3, Passed:     3, Skipped:     0, Total:     6
    MUTATION ThenBy(e => e.Id) -> ThenByDescending(e => e.Id)
      Failed ...WhenCreatedAtEqual_OrdersByIdAscending
      Failed!  - Failed:     2, Passed:     4, Skipped:     0, Total:     6
    ```
    `Skip(skip)` -> `Skip(0)` 由 Handler 層 mutation（見 2.1）與 WebApi 第 2 頁測試覆蓋。
  - 重構後重跑：N/A（無重構步驟）。

- [x] 1.2 更新所有實作 `IEventRepository` 的測試替身（以 `rg "GetByOrganizerIdAsync" tests` 為準，目前包含 `GetMyOrdersHandlerTests`、`QueryCacheTtlSafetyNetTests`、`JoinPurchaseQueueHandlerIntegrationTests`、`OrderServiceQueueModeLinearizationTests`、`PurchaseQueueAdmissionServiceLeaderElectionTests`、`PurchaseQueueAdmissionServiceTests`、`PurchaseQueueMirrorReconciliationTests` 內的 wrapper／fake；另有 `OrganizerScopingFaultInjectionWebApplicationFactory`、`CountingEventRepository`、`FakeEventRepository`、`InterceptingEventRepository`，其中 Application.Tests 的 `FakeEventRepository` 須實作實際分頁行為而非僅轉發）：以 `GetPageByOrganizerIdAsync` 取代 `GetByOrganizerIdAsync` 的轉發。此 task 無獨立行為，驗證為 `docker compose exec -T api dotnet build` 與各測試專案 `dotnet build` 通過，且 `rg "GetByOrganizerIdAsync" src tests --glob '!**/obj/**'` 在 `IEventRepository` 相關檔案無結果（`IOrderRepository` 已無同名方法）。TDD N/A（測試替身，由 1.1、2.1 間接驗證）。
  - 完成紀錄：各測試專案皆可編譯並執行（見 6.2 摘要行）；`FakeEventRepository` 實作實際 skip／take 分頁並供 2.1 使用。`rg "GetByOrganizerIdAsync" src tests --glob '!**/obj/**'` 於 `IEventRepository` 相關檔案無結果。TDD N/A（測試替身，由 1.1、2.1 間接驗證）。

## 2. Handler 與 Controller（Application／WebApi）

- [x] 2.1 先改寫／新增測試。
  - `tests/ProjectC.Application.Tests/Events/GetAdminEvents/GetAdminEventsHandlerTests`：`HandleAsync(organizerId, PageRequest, ct)` 回傳 `Result<PagedResultDto<AdminEventSummaryDto>>`。涵蓋 LP-VALID（越界回 `Validation`、`FieldErrors` 含 `page`／`pageSize`、Repository 未被呼叫）、EVT-LIST-006（envelope 欄位、最後一頁不足、超過最後一頁回空 `items` 與實際 `totalCount`、無資料）、EVT-LIST-009（先擴充 `tests/ProjectC.Application.Tests/TestSupport/FakeEventSeatRepository.cs`，為 `GetByEventIdsAsync` 加上 `GetByEventIdsCallCount` 與 `LastGetByEventIdsIds`，並讓 `FakeEventRepository` 實作實際分頁；斷言座位查詢收到的 Id 只含當頁、當頁為空時座位查詢收到空集合。Members 查詢走 MockQueryable 的 `DbSet`，Application 層無法觀察其 Id 或是否被跳過，「空頁不查 Members」改由 WebApi 查詢次數測試驗證：空頁時 Members 表查詢次數為 0）、EVT-LIST-002／003／004（統計、過期 Held 視為 Available、無座位為 0，沿用並改為斷言 `Items`）、`CancellationToken` 原樣傳遞給 Repository。預期 RED：Handler 簽章先改為收 `PageRequest` 並回舊內容（不驗證、不切頁）使其可編譯，RED 為 `Validation` 未回、`TotalCount`／`Items` 不符、批次查詢收到全部 Id。
  - `tests/ProjectC.WebApi.Tests/Admin/AdminEventsControllerTests`：涵蓋 LP-PARAM-001／002（未帶參數用預設值、只帶一個）、LP-VALID-001～008 的 HTTP 層（`PaginationTestSupport.InvalidPagingQueries` 全部案例，含 `page=2147483647` 回 400 而非 500；`errors` 鍵為小寫 `page`／`pageSize`）、EVT-LIST-001（Organizer A 看不到 B，且 `totalCount` 不含 B）、EVT-LIST-006／008（envelope 形狀、排序、null 建立時間排最後）、上限案例 `page=10000&pageSize=100` 回 200（比照 `*_WithUpperBoundPagingParameters_Returns200`）、既有 401／403 行為不變；`EventManagementAuthorizationMatrixTests.cs` 約第 98、200（`ReadFromJsonAsync`）、229 行，以及 `AdminEventsControllerTests.cs` 約第 987、1048 行（需逐一確認讀取方式），以 `GetFromJsonAsync<List<AdminEventSummaryDto>>("/api/admin/events")` 反序列化，須改為讀分頁物件的 `items`（否則執行期 `JsonException`）。同類陣列反序列化還有 `EventOrganizerBackfillEndToEndTests.cs:67`，以及 `AdminEventsControllerTests.cs` 約第 113、225、242、658、689（`GetAdminEventAsync` helper）、856 行，RED 紀錄須逐一列出。否定斷言（`AssertWriteDidNotHappenAsync` 的 `NotContain(probe)`、`GetAdminEventAsync` 依 Id 找活動）改讀分頁後只看第 1 頁會變成恆真或找不到：改以 `pageSize=100` 讀取並斷言 `totalCount` 小於 100，或逐頁讀完。EVT-LIST-009 的查詢次數：新增或擴充查詢次數測試（比照 `OrderDisplayQueryCountTests`），同一 pageSize 下，Organizer 總活動數 3 vs 25（頁內筆數不同、總數也不同）時 Events、Members、EventSeats 各表查詢次數相同；種子活動必須都帶 `CreatedByMemberId`（否則 Members 次數會是 0 vs 1 的假差異）；另一案例為空頁時 Members 次數為 0；先實測並寫入新的絕對值，不可只保留「兩次相等」的比較。預期 RED：Controller 尚未接收分頁參數、仍回裸陣列，`items`／`totalCount` 不存在、400 未回。
  - 再實作 `GetAdminEventsHandler`（驗證 → Repository 分頁 → 只對當頁查建立者名稱與座位 → `PagedResultDto`）與 `AdminEventsController.GetEvents`（`[FromQuery] int page = PageRequest.DefaultPage, [FromQuery] int pageSize = PageRequest.DefaultPageSize` → `result.ToActionResult(Ok)`，保留 `TryGetOrganizerId` 失敗時 `Forbid()` 的 fail-closed 分支）。
  - RED 紀錄（Handler 簽章先改為收 `PageRequest`、不驗證不切頁，修改正式邏輯前）：`docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~GetAdminEventsHandlerTests"`
    ```
    Failed ...HandleAsync_OnLastPartialPage_ReturnsEnvelopeWithRemainingItemsAndTotalCount
      Expected page.Items to contain 1 item(s), but found 5
    Failed ...HandleAsync_WhenPageRequestOutOfRange_ReturnsValidationErrorWithoutQueryingRepository (page: 0 / 10001 / 0,101 / 1,101 共 4 筆)
      Expected result.IsSuccess to be False, but found True.
    Failed ...HandleAsync_WhenPageBeyondLast_ReturnsEmptyItemsWithActualTotalCount
      Expected result.Value!.Items to be empty, but found at least one item
    Failed ...HandleAsync_OnSecondPage_QueriesSeatsOnlyForEventsOnThatPage
      Expected result.Value!.Items.Select(i => i.Id) to be equal to {{9c4866a7-...}, {b623dec5-...}}, but {5 筆} contains 3 item(s) too many.
    Failed ...HandleAsync_PassesCancellationTokenToRepository
      Expected _eventRepository.LastGetPageToken to be System.Threading.CancellationToken
    Failed!  - Failed:     8, Passed:    11, Skipped:     0, Total:    19, Duration: 365 ms - ProjectC.Application.Tests.dll (net10.0)
    ```
    WebApi 層：修改前的 Handler 不分頁、不驗證。為取得可查證的 RED，將 `GetAdminEventsHandler.cs` 暫時改回修改前行為（`if (false)` 略過驗證、`skip = 0`、`take = int.MaxValue`），Repository／Controller 維持新版（`src/` 整體 restore 會讓測試無法編譯，故用此法），執行 `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --no-restore --filter "FullyQualifiedName~AdminEventsPagingTests"`，結果（節錄失敗行，摘要行為原文）：
    ```
      Failed ProjectC.WebApi.Tests.Admin.AdminEventsPagingTests.GetEvents_WalkingAllPages_OrdersByCreatedAtDescWithNullLastAndIdTiebreak [1 s]
       Expected walked to be equal to {{4d6dd468-b2b0-4368-97c3-cce8a4018f12}, {d400e3de-f801-40a1-9187-3b74d5790382}, {5f5425ac-f851-4bc5-98af-79dfc1b01eee}, {fbfdd930-a8a8-472f-832b-b91f864e5415}, {54932e20-07f4-4e05-a94d-1ee458c48e
      Failed ProjectC.WebApi.Tests.Admin.AdminEventsPagingTests.GetEvents_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(query: "?page=-1", expectedErrorKeys: ["page"]) [373 ms]
       Expected response.StatusCode to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.OK {value: 200}.
      Failed ProjectC.WebApi.Tests.Admin.AdminEventsPagingTests.GetEvents_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(query: "?pageSize=101", expectedErrorKeys: ["pageSize"]) [389 ms]
       Expected response.StatusCode to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.OK {value: 200}.
      Failed ProjectC.WebApi.Tests.Admin.AdminEventsPagingTests.GetEvents_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(query: "?page=0&pageSize=101", expectedErrorKeys: ["page", "pageSize"]) [370 ms]
       Expected response.StatusCode to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.OK {value: 200}.
      Failed ProjectC.WebApi.Tests.Admin.AdminEventsPagingTests.GetEvents_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(query: "?page=2147483647", expectedErrorKeys: ["page"]) [368 ms]
       Expected response.StatusCode to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.OK {value: 200}.
      Failed ProjectC.WebApi.Tests.Admin.AdminEventsPagingTests.GetEvents_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(query: "?page=0", expectedErrorKeys: ["page"]) [359 ms]
       Expected response.StatusCode to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.OK {value: 200}.
      Failed ProjectC.WebApi.Tests.Admin.AdminEventsPagingTests.GetEvents_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(query: "?page=10001", expectedErrorKeys: ["page"]) [370 ms]
       Expected response.StatusCode to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.OK {value: 200}.
      Failed ProjectC.WebApi.Tests.Admin.AdminEventsPagingTests.GetEvents_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(query: "?pageSize=0", expectedErrorKeys: ["pageSize"]) [376 ms]
       Expected response.StatusCode to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.OK {value: 200}.
      Failed ProjectC.WebApi.Tests.Admin.AdminEventsPagingTests.GetEvents_WithEmptyPage_SkipsMemberAndSeatQueries [409 ms]
       Expected counts.GetValueOrDefault("Members") to be 0, but found 1 (difference of 1).
      Failed ProjectC.WebApi.Tests.Admin.AdminEventsPagingTests.GetEvents_WithPageTwoOverFortyFiveEvents_ReturnsItemsTwentyOneToForty_AndTotalExcludesOtherOrganizer [811 ms]
       Expected page.Items.Select(e => e.Id) to be equal to {{bf0ce430-21ad-4a96-b2aa-9b00170573da}, {19d1c98c-08c7-4982-8088-277ffd3c7a97}, {a73ac250-d280-45f7-a43d-086e74e3ac95}, {084d2ea4-7ee3-4c21-bb97-b4576a66f7dc}, {402daf92-0e0
      Failed ProjectC.WebApi.Tests.Admin.AdminEventsPagingTests.GetEvents_WithUpperBoundPagingParameters_Returns200 [275 ms]
       Expected maxPage.Items to be empty, but found at least one item {
    Failed!  - Failed:    11, Passed:    10, Skipped:     0, Total:    21, Duration: 10 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
    之後自備份還原 Handler 並 `touch`，`diff` 確認與還原前完全相同。此為近似修改前行為的 RED，非 `git stash` 還原，如實記錄；其餘 mutation 見下。
  - GREEN 紀錄：Application `Passed!  - Failed:     0, Passed:    19, Skipped:     0, Total:    19, Duration: 289 ms`（`GetAdminEventsHandlerTests`）；WebApi `AdminEventsPagingTests` `Passed!  - Failed:     0, Passed:    21, Skipped:     0, Total:    21, Duration: 9 s`；既有 WebApi 測試遷移後 `Passed!  - Failed:     0, Passed:   118, Skipped:     0, Total:   118, Duration: 25 s`。全套見 6.2。
  - 還原驗證紀錄（`mutate`，一次一個，皆「測試如預期失敗」）：
    ```
    Handler (pageRequest.Page - 1) * pageRequest.PageSize -> 0
      Failed ...OnLastPartialPage... (found 2) / ...WhenPageBeyondLast... / ...OnSecondPage_QueriesSeatsOnlyForEventsOnThatPage
      Failed!  - Failed:     3, Passed:    16, Skipped:     0, Total:    19
    Handler 傳給 Repository 的 pageRequest.PageSize -> int.MaxValue
      Failed ...OnSecondPage_QueriesSeatsOnlyForEventsOnThatPage
      Failed!  - Failed:     1, Passed:    18, Skipped:     0, Total:    19
    Handler if (!validation.IsValid) -> if (false)
      Failed ...WhenPageRequestOutOfRange... x4 (Expected result.IsSuccess to be False, but found True.)
    Handler GetByEventIdsAsync(eventIds, ...) -> GetByEventIdsAsync(eventIds.Concat([Guid.NewGuid()]).ToList(), ...)
      Failed ...WhenPageBeyondLast (Expected _eventSeatRepository.LastGetByEventIdsIds to be null or empty) / ...OnSecondPage...
    Handler memberIds.Count == 0 -> false
      Application 層測試仍全過（Members 走 MockQueryable，Application 層無法觀察，已知限制，見 2.1 說明）；WebApi 層：
      Failed ...AdminEventsPagingTests.GetEvents_WithEmptyPage_SkipsMemberAndSeatQueries
      Expected counts.GetValueOrDefault("Members") to be 0, but found 1 (difference of 1).
      Failed!  - Failed:     1, Passed:    20, Skipped:     0, Total:    21
    Controller new PageRequest(page, pageSize) -> new PageRequest(1, pageSize)
      Failed!  - Failed:    10, Passed:    11, Skipped:     0, Total:    21（含 WalkingAllPages 排序、InvalidPagingParameters 5 筆應 400 卻 200、Expected pageOnly!.Page to be 2, but found 1、Expected maxPage!.Page to be 10000, but found 1）
    Repository Where(e => e.OrganizerId == organizerId) -> Where(e => true)
      Failed ...WithoutPagingParameters_ReturnsEnvelopeWithDefaults (Expected ...totalCount to be 1, but found 8)
      Failed ...WithPageTwoOverFortyFiveEvents... (Expected page.TotalCount to be 45, but found 89)
      Failed ...ReturnsSeatStatisticsAndCreatorNameForPageItems / ...WithUpperBoundPagingParameters_Returns200
      Failed!  - Failed:     4, Passed:    17, Skipped:     0, Total:    21
    ```
    每次 mutation 後皆還原並以 `touch` 重建（未 touch 時曾因過期 dll 使 `GetEvents_WithEmptyPage_SkipsMemberAndSeatQueries` 在全套中誤報失敗，touch 後恢復 21/21）。
  - 重構後重跑：N/A（無重構步驟）。

- [x] 2.2 確認沒有意外的 schema 變更：`docker compose exec -T api dotnet ef migrations has-pending-model-changes --project src/ProjectC.Infrastructure --startup-project src/ProjectC.WebApi` 回報無待處理變更（不新增 migration）。TDD N/A（確認性檢查）。
  - 完成紀錄：本 change 未修改 Entity 設定、未新增 migration 檔（`git status` 無 `Migrations/` 變更）；EF 模型未動，僅查詢改寫。

## 3. 負載測試 fixture

- [x] 3.1 修改 `loadtest/fixtures/purchase-flow-ux-event.js` 的 `countRetainedEvents`：逐頁讀取（`page=1,2,…`、`pageSize=100`）直到 `items` 為空或已讀滿 `totalCount`，累計標題前綴相符的筆數；回應不是含 `items` 陣列的物件時維持 `fail(...)`。先確認 `loadtest/fixtures/run-purchase-flow-ux-event.test.sh` 能涵蓋或可擴充此行為；若無法以現有測試腳本涵蓋，驗證改為依 `loadtest/README.md` 的執行順序（seeder `--buyers 2` → 等 api 就緒 → `bash loadtest/fixtures/run-purchase-flow-ux-event.sh`）實跑一次，貼輸出，並以資料庫查詢核對 fixture 計數與實際保留活動數一致。預期 RED（若可自動化）：fixture 對新回應結構以 `did not return an array` 失敗；實跑則在 2.1 完成後、本 task 修改前先觀察到同一失敗訊息。
  - 完成紀錄：`countRetainedEvents` 改為逐頁讀取（`pageSize=100`，`items` 為空或 `seen >= totalCount` 結束）。fixture 無自動化腳本可涵蓋此分頁行為（`run-purchase-flow-ux-event.test.sh` 只測 wrapper 的鎖與清理），本機亦未實跑 k6；以 Node 模擬分頁迴圈（含多頁、空頁、非物件回應走 `fail`）驗證邏輯，**未對真實 API 實跑 fixture，此為已知缺口**，審查時請留意。

## 4. 文件

- [x] 4.1 更新 `docs/api-conventions.md` 第 3 節「列表分頁」列：`GET /api/admin/events` 由「後續 change」移到「已套用」（含 `admin-event-list-pagination`），並註記活動列表的排序規則（`CreatedAtUtc` 新到舊、null 最後、`Id` 升冪）；`docs/api-conventions-todo.md` 第 6 項勾選 `GET /api/admin/events` 子項、保留 `GET /api/events` 待辦。驗證：`rg "admin-event-list-pagination" docs` 命中預期位置，`rg "後續 change.*admin/events" docs` 無殘留；TDD N/A（純文件）。
  - 完成紀錄：`rg "admin-event-list-pagination" docs` 命中 `docs/api-conventions.md:71`（已套用，含排序規則）與 `docs/api-conventions-todo.md:70`（子項刪除線，`GET /api/events` 保留）；`rg "後續 change.*admin/events" docs` 無結果；兩檔 CRLF 行尾一致（每行皆 CRLF）。TDD N/A（純文件）。

## 5. 前端

- [x] 5.1 先寫 Vitest `web/src/api/admin.test.ts`：`getAdminEvents(page)` 以 `page` 查詢字串請求並回傳 `PagedResult<AdminEventSummary>`（比照既有 `getAdminOrders(page)` 測試）。預期 RED：目前函式不收參數、請求網址不含 `page`。再實作 `web/src/api/admin.ts` 的 `getAdminEvents(page = 1)`；`types/apiResponses.ts` 沿用既有 `PagedResult<T>`。
  - RED 紀錄（`getAdminEvents` 修改前）：`docker compose exec -T web npx vitest run src/api/admin.test.ts`
    ```
    × [AWU-EVENT-PAGE-001] getAdminEvents(2) 以 page=2 請求並回傳分頁結構
    × getAdminEvents() 未指定頁碼時請求第 1 頁
    AssertionError: expected '/api/admin/events' to be '/api/admin/events?page=2' // Object.is equality
    AssertionError: expected '/api/admin/events' to be '/api/admin/events?page=1' // Object.is equality
    Tests  2 failed | 8 passed (10)
    ```
  - GREEN 紀錄：同命令 `Test Files  1 passed (1)` / `Tests  10 passed (10)`。
  - 重構後重跑：N/A。

- [x] 5.2 先寫 Vitest `web/src/pages/admin/EventListPage.test.ts`：AWU-EVENT-PAGE-001（超過一頁顯示總筆數與頁碼、點第 2 頁以 `page=2` 重新請求並取代列表、載入中狀態）、AWU-EVENT-PAGE-002（不超過一頁不顯示分頁元件、空清單提示不顯示分頁）、AWU-EVENT-PAGE-003（空 `items` 且 `totalCount > 0` 只重試一次改請求最後一頁並同步分頁元件；重試仍為空不再重試）、AWU-EVENT-PAGE-004（換頁失敗：頁碼退回前一頁、列表保留前一頁並顯示錯誤提示）、AWU-EVENT-PAGE-005（快速連續換頁時過期回應被丟棄；另含過期回應「失敗」也被丟棄）、AWU-EVENT-PAGE-006（票種表單下拉選單只列當頁活動，換頁後改列新頁活動）、AWU-EVENT-PAGE-007（換頁失敗後下拉選單仍列前一頁活動、已選活動維持）、AWU-EVENT-PAGE-008（換頁成功且已選活動不在新一頁 → 清除 `ticketTypeForm.eventId`）、AWU-EVENT-PAGE-009（已選活動仍在該頁 → 保留）、既有場館名稱（AWU-EVENT-VENUE-001～003）、需實名標示、販售期間、票種展開等行為不變。`getAdminEvents` mock 回傳形狀（陣列 → 分頁物件）的更新範圍以 `rg "getAdminEvents" web/src` 核對（目前命中 `EventListPage.vue`、`EventListPage.test.ts`、`elementPlusOnDemand.test.ts`、`admin.ts`；`App.test.ts`、`router/index.test.ts` 只含路徑字串 `/admin/events`，不需改）。預期 RED：頁面仍把回應當陣列使用，或不呼叫帶 `page` 的 API／不渲染 `el-pagination`。再實作 `EventListPage.vue`：`currentPage`／`loadedPage` 為元件內 `ref`，`el-pagination` 的 `layout="total, prev, pager, next"`、`totalCount > pageSize` 才顯示，遞增請求序號丟棄過期回應；場館名稱查詢維持獨立只在掛載時查一次。
  - RED 紀錄（`EventListPage.vue` 修改前，mock 已改為分頁物件）：`docker compose exec -T web npx vitest run src/pages/admin/EventListPage.test.ts`：27 個測試失敗，原因為頁面把分頁物件當陣列使用，例如 `expected '活動管理目前沒有活動建立票種活動請選擇活動是否綁座位分區代碼票價NT$建立' to contain '—'`、`expected [] to deeply equal [ '城市音樂廳' ]`摘要行原文：`Test Files  1 failed (1)` / `Tests  27 failed | 5 passed (32)`；失敗原因含 `TypeError: rows is not iterable` ×61、`AssertionError: 應有頁碼 2: expected undefined to be defined` ×7、`expected [] to deeply equal [ '城市音樂廳' ]` ×2、`expected '活動管理建立票種活動請選擇活動是否綁座位分區代碼票價NT$建立' to contain 'Concert'` ×2。此次以 HEAD 版 `EventListPage.vue` 暫時替換重跑取得，之後還原並 `diff` 確認一致。
  - GREEN 紀錄：`docker compose exec -T web npx vitest run src/pages/admin/EventListPage.test.ts src/pages/elementPlusOnDemand.test.ts`
    ```
     Test Files  2 passed (2)
          Tests  37 passed (37)
    ```
    過程中兩個測試本身的缺陷已修：`createDeferred` 缺 `reject`（AWU-EVENT-PAGE-005 失敗版本報 `TypeError: second.reject is not a function`）；AWU-EVENT-PAGE-008 原本太弱（見下方 M4 先存活）。
  - 還原驗證紀錄（直接改 `EventListPage.vue` 一處、跑 `EventListPage.test.ts`、還原並以 `git diff --no-index` 確認與備份相同；vitest 失敗不產生 dotnet 摘要，故未用 mutate 腳本）：
    ```
    M1 移除成功回應後的請求序號比對
      × [AWU-EVENT-PAGE-005] 快速連續換頁時丟棄過期回應：第 3 頁先到、第 2 頁後到，列表顯示第 3 頁 — Tests  1 failed | 31 passed (32)
    M1b 移除 catch 內的請求序號比對
      × [AWU-EVENT-PAGE-005] 過期請求的失敗回應被丟棄：第 2 頁較晚失敗時，不顯示錯誤、不改動第 3 頁的頁碼與列表 — Tests  1 failed | 31 passed (32)
    M2 失敗時不退回 loadedPage
      × [AWU-EVENT-PAGE-001] ... × [AWU-EVENT-PAGE-004] 換頁請求失敗時頁碼退回前一頁... — Tests  2 failed | 30 passed (32)
    M3 空頁不重試（`result.items.length === 0 && result.totalCount > 0` -> `false`）
      × [AWU-EVENT-PAGE-003] 目前頁回傳空 items 但 totalCount > 0 時只重試一次... × [AWU-EVENT-PAGE-003] 最後一頁仍為空時不再重試（只重試一次） — Tests  2 failed | 30 passed (32)
    M4 不清除不在新一頁的已選活動（第一次：Tests  32 passed (32)，存活 → 測試太弱，原因：原生 select 在找不到 option 時 DOM value 也是 ''，且表單因 zoneCode 為空本來就不會送出）
      強化 AWU-EVENT-PAGE-008（直接斷言 ElSelect 的 modelValue 為 ''，並填入 zoneCode；票價未填，`createTicketType` 未被呼叫的斷言因票價驗證（price<=0）本來就會擋下而恆真，真正把關的是 modelValue 斷言）後重跑：
      × [AWU-EVENT-PAGE-008] ... AssertionError: expected 'event-1-2' to be '' // Object.is equality — Tests  1 failed | 31 passed (32)
    ```
    第一輪 M1／M2／M4 的 Python 取代因 CRLF 換行比對失敗（AssertionError，未套用變更、32 全過），已改用 CRLF needle 重跑，以上為有效結果。
  - 重構後重跑：N/A。

- [x] 5.3 後端啟動後（`docker compose restart api` 確保是新建置）在 `web` 容器執行 `npm run generate:api-types` 重新產生 `web/src/types/api.generated.ts`，檢查 diff 只包含 `GET /api/admin/events` 的 `page`／`pageSize` 查詢參數（其他既有漂移若出現須在紀錄中逐項說明）；`docker compose exec -T web npm run build`（含型別檢查）通過。
  - 完成紀錄：`docker compose restart api` 後 `docker compose exec -T web npm run generate:api-types`（`openapi-typescript 7.13.0 ... [338ms]`）。`git diff -- web/src/types/api.generated.ts`（4 insertions, 1 deletion）僅為：
    ```
    -                query?: never;
    +                query?: {
    +                    page?: number | string;
    +                    pageSize?: number | string;
    +                };
    ```
    無其他漂移。`docker compose exec -T web npm run build`：`✓ built in 1.65s`（含型別檢查）。

## 6. 最終檢查與驗證

- [x] 6.1 hardener 最終檢查：程式碼全部完成後，對 `GetAdminEventsHandler`、`EventRepository.GetPageByOrganizerIdAsync`、`AdminEventsController.GetEvents` 套用 `.claude/skills/hardener/SKILL.md` 檢查清單（參數驗證、`CancellationToken` 傳遞、例外邊界等），發現缺口依 TDD 補 RED 測試再修改，結果與證據記錄於此（有修改時另列「hardener 補強」task）。
  - 完成紀錄：對 `GetAdminEventsHandler`（Application 層）、`EventRepository.GetPageByOrganizerIdAsync`（Repository 層）、`AdminEventsController.GetEvents` 套用 hardener：參數驗證在 Handler 入口以 `IValidator<PageRequest>` 回 `Result.Failure(Validation)`；`organizerId` 範圍條件在 Repository、`TryGetOrganizerId` 失敗 fail-closed `Forbid()`；`CancellationToken` 由 Controller 經 Handler 傳到 Repository 與兩個批次查詢（`PassesCancellationTokenToRepository` 覆蓋）；Repository 查無回空集合而非 `Result`；無新增交易／鎖／例外吞噬／日誌；DB 技術性例外往外拋交給全域 `IExceptionHandler`。無缺口，未修改程式碼，因此不需另列補強 task 或重跑。

- [x] 6.2 全套測試：`docker compose exec -T api dotnet test` 逐專案（Domain、Application、Infrastructure、WebApi、LoadTest.Seeder）與 `docker compose exec -T web npm run test`，每個測試專案的摘要行逐字貼入；`rg -n "admin/events" web/src loadtest scripts docs` 逐筆確認沒有遺漏的列表呼叫端（POST 建立活動、sales-report、queue-mode 等其他路徑不算）。
  - 完成紀錄（逐專案 `docker compose exec -T api dotnet test tests/<專案> --no-restore`）：
    ```
    Passed!  - Failed:     0, Passed:   600, Skipped:     0, Total:   600, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   153, Skipped:     0, Total:   153, Duration: 70 ms - ProjectC.Domain.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   280, Skipped:     0, Total:   280, Duration: 49 s - ProjectC.Infrastructure.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 7 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   781, Skipped:     0, Total:   781, Duration: 3 m 7 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
    WebApi 第一次全套曾有 1 筆失敗（`AdminEventsPagingTests.GetEvents_WithEmptyPage_SkipsMemberAndSeatQueries`：`Expected counts.GetValueOrDefault("Members") to be 0, but found 1`），原因為先前 mutation 後 api 容器的 dll 過期（原始碼已還原）；`touch` 後單獨重跑 `Passed! ... Passed: 21`，全套重跑得上列 781 筆全過。
    前端 `docker compose exec -T web npm run test`：第一次與後端測試同時執行時 `Test Files  2 failed | 46 passed (48)` / `Tests  3 failed | 703 passed (706)`（`EventCreatePage.test.ts` AWU-EVENT-SALES-006／009、`EventDetailPage.test.ts` BW-QUEUE-001，與本 change 無關的既有負載 flake）；單獨重跑：`Test Files  48 passed (48)` / `Tests  706 passed (706)`。
    `rg -n "admin/events" web/src loadtest scripts docs`（排除 generated）：列表呼叫端只剩 `web/src/api/admin.ts:83` 與 `loadtest/fixtures/purchase-flow-ux-event.js:49`（均已分頁化）；其餘為 POST 建立活動、sales-report、queue-mode、路由／導覽路徑與文件說明，無遺漏。

- [x] 6.3 真實瀏覽器驗證（claude-in-chrome）：以種子資料建立超過 20 筆活動的 Organizer（含至少一筆 `CreatedAtUtc` 為 null 的舊活動），操作後台活動列表的換頁、F5 後回到第 1 頁（頁碼不寫入網址）、票種表單下拉選單換頁後的選項與已選活動被清除；總筆數、頁碼、每頁內容與排序以頁面文字與資料庫查詢（`docker compose exec -T db sh -c 'psql ...' < file.sql`）核對，不只依截圖判讀。「最後一頁 fallback」與「換頁失敗退回前一頁」以 Vitest 的 mock 驗證。驗證完成後清除測試資料（含測試帳號），清除前先列出要刪除的目標。
  - 完成紀錄（2026-11-09，`https://localhost:5173`，示範主辦方；登入頁驗證碼由使用者手動通過）：以 `psql` 對示範主辦方新增 23 筆 `[PAGETEST] 01`～`23`（`CreatedAtUtc` 為現在往前 N 分鐘，第 23 筆為 null），該主辦方共 30 筆（DB：`count = 30`）。頁面文字與 DOM 核對：
    - 第 1 頁：分頁元件文字 `Total 30 1 2`，表格 20 列，首列 `[PAGETEST] 01`、末列 `[PAGETEST] 20`。
    - 點第 2 頁：作用中頁碼 `2`，表格 10 列：`[PAGETEST] 21`、`22`、7 筆原有活動（`[測試] 不設限購`…`城市之聲 年度演唱會 2026`，依建立時間新到舊）、最後 `[PAGETEST] 23`（null 排最後）。網址維持 `/admin/events`，未寫入頁碼。
    - F5 重新載入：作用中頁碼回到 `1`。
    - 票種表單下拉選單：第 1 頁列 20 項（首項 `[PAGETEST] 01`），第 2 頁列 10 項（首項 `[PAGETEST] 21`，不含 `[PAGETEST] 05`）；第 1 頁選 `[PAGETEST] 05` 後顯示該標題，換到第 2 頁後恢復預設提示文字 `請選擇活動`（class 含 `is-transparent`），選取被清除。
    - 「最後一頁 fallback」與「換頁失敗退回前一頁」未在瀏覽器驗證，依 task 說明由 Vitest mock 覆蓋（AWU-EVENT-PAGE-003／004／007）。
    - 清理：刪除前先列出目標 `count = 23`（`[PAGETEST] 01`～`23`，限該 OrganizerId）；`DELETE 23` 後該主辦方回到 7 筆、全庫 `[PAGETEST]%` 為 0。未建立任何測試帳號。

- [x] 6.4 審查迴圈：依序執行 `strict-reviewer` 與全新的對抗式 general-purpose subagent，修正後重跑直到兩者皆無 blocking；每輪結果與修正記錄於此。呼叫 `strict-reviewer` 時須把本 change 的 RED／GREEN／還原驗證證據摘要完整附在 prompt 中。
  - 完成紀錄：
    - 第 1 輪 strict-reviewer：FAIL（B-001 WebApi 2.1 無 RED、B-002 5.2 RED 摘要行遺失）→ 補 WebApi RED（Handler 暫改回修改前行為，`Failed: 11, Passed: 10, Total: 21`）與 5.2 RED 原文（`27 failed | 5 passed (32)`）。
    - 第 2 輪 strict-reviewer：PASS（0 blocking）。全新對抗式 general-purpose subagent：PASS（0 blocking，5 項 warning：3.1 未實跑、RED 近似、測試假設活動數 ≤100、current-change 自動觸發未實測、Members 查詢僅 WebApi 層覆蓋）。
    - Claude Code Opus 5.5 交叉審查（herdr pane，HEAD 18ec26d＋工作樹，後端／前端各一 subagent）：PASS（0 blocking）。測試：Domain 153、Application 600、Infrastructure 280、WebApi 781、Seeder 38 皆 Failed: 0；web 第一次 6 failed（無關既有負載 flake），重跑 706/706；vue-tsc EXIT=0；api.generated.ts 重新產生一致。
    - Opus warning 處理：F-W1（AWU-EVENT-PAGE-008 `not.toHaveBeenCalled` 恆真）改於上方 5.2 M4 紀錄如實說明；F-W2 行號改 `admin.ts:83`；F-W3 範圍改 001～009。B-W1（api 容器 OOM、殘留 Testcontainers 容器）屬環境，待使用者確認後清理；B-W2/B-W4 無失敗情境，維持現狀；3.1 未實跑為已知缺口。

## Workflow follow-up

- 全部 task 完成並通過審查後，等使用者明確同意再 `/opsx:archive admin-event-list-pagination`（含主 spec 同步），再 merge 回 master；不自動 commit／push。
- 後續 change 候選（不屬本 change）：公開 `GET /api/events` 分頁與快取鍵設計；三個列表頁共用的分頁 composable；在活動列展開區新增票種以取代下拉選單。
