# Tasks

> 對應 spec：`event-catalog`（ADDED：EVT-LIST-001～006、EVT-LIST-ORDER-001～004）、`event-catalog`（MODIFIED：EVT-DETAIL-004～006 key 同步）、`ticket-purchase`（MODIFIED：TP-BROWSE-RN-001 key 同步）、`real-name-verification`（MODIFIED：部署切換 Requirement 的 key 名同步，無 Scenario 行為變更）、`query-caching`（MODIFIED：QC-EVT-001～003、QC-EVT-INV-001／002、QC-TTL-001～004、QC-ACCESS-001～003、QC-EVT-VER-001～005）、`buyer-web-ui`（MODIFIED：「瀏覽活動列表」Scenario；ADDED：BW-LIST-PAGE-001～008）。
> 所有後端命令在容器內執行：`docker compose exec -T api dotnet test <專案> --filter "..."`；前端：`docker compose exec -T web npm run test -- <檔案>`。
> 每個 task 的「完成紀錄」於實作時依 CLAUDE.md「證據貼原文」填寫（RED：命令、失敗測試名、Expected／Actual 原文，需在修改正式程式碼前；GREEN：命令與摘要行；重構後重跑或 N/A）。本 change 非 bug fix、也非下單／排隊／座位／庫存核心邏輯，「RED 還原驗證」以 mutate 輔助（排序與切頁各至少一個改動）；其餘 N/A。
> 前提：`IEventRepository.GetAllAsync` 不改（`PurchaseQueueAdmissionService` 也使用）；`GetEventByIdHandler`、`CreateEventHandler`、`SetEventQueueModeHandler` 行為不變。

## 1. 後端：排序、切頁與 v3 快取（Application）

- [ ] 1.1 在 `tests/ProjectC.Application.Tests/Events/GetEvents/GetEventsHandlerTests.cs`（既有檔）先寫測試並更新既有測試的建構（Handler 建構子新增 `IValidator<PageRequest>`、`HandleAsync` 改收 `PageRequest`）：
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
  - 完成紀錄（RED／GREEN／mutate／重構後重跑）：待實作時填寫。
- [ ] 1.2 `GetEventByIdHandler` 改呼叫 `GetEventsHandler.GetAllOrderedAsync`，在 `tests/ProjectC.Application.Tests/Events/GetEventById/GetEventByIdHandlerTests.cs` 更新既有測試的建構並新增：活動排在第 2 頁之後（共 25 筆、目標為最舊）仍回 `Success`（EVT-LIST-006）；既有 EVT-DETAIL-001～008 的 handler 層測試維持通過。
  - 預期 RED：1.1 的過渡寫法下（`GetEventByIdHandler` 只看第一頁），「目標在第 2 頁之後」測試因找不到而 `NotFound`（Expected IsSuccess True／Actual False）。GREEN：改呼叫 `GetAllOrderedAsync`。
  - 完成紀錄：待實作時填寫。

## 2. 後端：Controller 與整合測試（WebApi／Infrastructure）

- [ ] 2.1 在 `tests/ProjectC.WebApi.Tests/Events/EventsControllerTests.cs`（既有檔，`CustomWebApplicationFactory`）更新既有 `GET /api/events` 測試為新回應結構，並新增：EVT-LIST-001（匿名、未帶參數：`page=1,pageSize=20`、`totalCount`、`items` 欄位與 `GET /api/events/{id}` 一致）、EVT-LIST-002／003／004、EVT-LIST-005（`pageSize=101`、`page=0`、`page=10001`、`page=abc`、`page=` 皆 400 `Validation`，`errors` 鍵為小寫 `page`／`pageSize`）、EVT-LIST-ORDER-001～004（以 HTTP 建立時間不同、null 的活動驗證順序與逐頁不重複）、EVT-LIST-006（排在第 2 頁之後的活動 `GET /api/events/{id}` 回 200）。
  - 隔離策略：`EventsControllerTests` 的 `IClassFixture<CustomWebApplicationFactory>` 共用 DB 與 Redis，公開列表是全域資料。依賴精確總數或絕對位置的情境（EVT-LIST-001～003、ORDER）放進**獨立的測試類別**（各自獨立 factory／全新容器）；EVT-LIST-004（`totalCount=0`）只在 handler 層驗證，HTTP 層不測。其餘共用 fixture 的測試只斷言「基準 `totalCount`＋自己建立的筆數」與自己建立之活動間的相對順序。
  - 測試資料操作：以 `ExecuteUpdate` 或 SQL 改 `CreatedAtUtc` 不會失效快取，更新後必須先刪除 `query-cache:events:list:v3`（或確保更新前沒有任何 `GET /api/events`）。
  - 邊界：EVT-LIST-005 沿用 `PaginationTestSupport.InvalidPagingQueries` 的 Theory，另補 LP-VALID 邊界值（`pageSize=100`、`page=10000` 回 200）與 `2147483647`。
  - 連帶測試：`AdminEventsControllerTests` 的 `QueryEvents_WhenStoredStartIsUtc_AllQueryEndpointsReturn200`（`ReadStartAtUtcJsonAsync`）與 `QueryEvents_WhenStoredMaxTicketsPerOrderExceedsSystemLimit_ListsStillLoad`（`ReadMaxTicketsPerOrderJsonAsync`）把公開列表根節點當陣列解析，改傳 `isPaged: true`；實作時另以 `rg '"/api/events"' tests --glob '*.cs'` 逐檔判斷是否需更新 body 解析。
  - 預期 RED：action 仍回整份陣列時，結構與 400 斷言失敗（Expected／Actual 取自實際輸出）。
  - 實作：`EventsController.GetEvents` 改為 `(CancellationToken, [FromQuery] int page = PageRequest.DefaultPage, [FromQuery] int pageSize = PageRequest.DefaultPageSize)`，呼叫 `HandleAsync(new PageRequest(page, pageSize), ct)` 並 `result.ToActionResult(Ok)`；維持匿名；更新 `ServiceRegistrationSnapshot.baseline.txt`（若 `GetEventsHandler` 的註冊簽名有變）。
  - 完成紀錄：待實作時填寫。
- [ ] 2.2 快取整合測試與既有測試的 key 同步（`CachingComponentTestWebApplicationFactory`，真實 Redis）。已知含 `v2` 字串或 `EventListCacheKeyV2` 的檔案：WebApi.Tests 的 `RealNameEventListCacheTests`（TP-BROWSE-RN-001）、`QueryCachingAccessScopeTests`（QC-ACCESS-001～003）、`QueryCachingComponentTests`、`QueryCachingFailOpenTests`、`QueryCachingEventDetailTests`（EVT-DETAIL-004～006）；Infrastructure.Tests 的 `QueryCacheEventInvalidationOrderingTests`、`QueryCacheTtlSafetyNetTests`（QC-TTL-001～004，第 31 行常數 `EventListCacheKeyV2`）；Application.Tests 的 `CreateEventHandlerTests`（約第 216 行，QC-EVT-INV）。實作時以 `rg "events:list:v2|EventListCacheKeyV2" tests --glob '*.cs'` 重新列出全部，不得遺漏：
  - QC-TTL-003：TTL 內持續命中，改觀察 v3 key；QC-ACCESS-003：eventId 格式錯誤在路由層被拒絕，行為不變，僅確認既有測試仍通過（key 同步後）；EVT-DETAIL-004～006、TP-BROWSE-RN-001：預置／斷言的 key 改為 v3，行為斷言不變。
  - 既有測試改為引用 `GetEventsHandler.CacheKey` 或改為 `v3`，並以回應 `items` 取代原本整份陣列斷言。
  - 新增 QC-EVT-VER-001（`list` 與 `list:v2` 存有未排序內容、v3 不存在 → 回傳排序後的頁面、v3 寫入完整有序列表、兩個舊 key 內容不變）、QC-EVT-VER-002（建立活動／開關熱門搶購模式只清除 v3，另外兩個 key 不變）、QC-EVT-VER-003（整個過程中 `list` 與 `list:v2` 不存在）、QC-EVT-VER-004（v3 原始內容兩個銷售期間屬性存在且 null 值不被省略）、QC-EVT-VER-005（請求 `page=2,pageSize=1` 後 v3 仍含全部活動且已排序）；另新增「不同 `page`／`pageSize` 共用同一個 v3 key，第二次請求命中（`CountingEventRepository` 的 `GetAllAsync` 計數不增加）」。
  - QC-EVT-INV-001／002、QC-TTL-001／002／004、QC-ACCESS-001／002 既有測試只需 key 與結構同步，行為斷言不變；Redis 不可用的 fail-open 測試（`QueryCachingFailOpenTests`）維持：Redis 連線失敗時 `GET /api/events` 仍回 200 分頁結構並有 Warning log。
  - 預期 RED：key 與結構更新後、尚未升版前的測試失敗；新增的 VER／共用 key 測試先因 key 仍為 `v2` 或內容未排序而失敗。
  - 完成紀錄：待實作時填寫。
- [ ] 2.3 hardener 最終檢查（`GetEventsHandler`、`GetEventByIdHandler`、`EventsController` 全部完成後，套用 `.claude/skills/hardener/SKILL.md` 一次）：檢查參數驗證（`PageRequest` 驗證在碰快取前）、`CancellationToken` 傳遞、快取不可用的 fail-open、例外邊界；發現缺口依 TDD 補測試。無修改時記錄「已確認符合、無修改」。
  - 完成紀錄：待實作時填寫。

## 3. 前端：API 層、型別與買家活動列表頁

- [ ] 3.1 `web/src/api/events.ts` 與 `web/src/types/apiResponses.ts`：`getEvents(page)` 以 `?page=` 呼叫並回傳既有的 `PagedResult<EventSummary>`；在 `web` 容器執行 `npm run generate:api-types`，檢查 `api.generated.ts` diff 只含 `GET /api/events` 的 `page`／`pageSize` 查詢參數與回應結構，確認 `vue-tsc -b` 通過。同時把其他以 `getEvents` 陣列 mock 的前端測試改為分頁結構：`web/src/layouts/BuyerLayout.test.ts`、`web/src/pages/elementPlusOnDemand.test.ts`、`web/src/pages/buyer/orderConfirmationFlow.test.ts`、`web/src/pages/buyer/EventDetailPage.test.ts`（實作時以 `rg "getEvents" web/src --glob '*.test.ts'` 列出全部；`PlatformLayout.test.ts` 等若亦有，一併處理）。
  - 預期 RED：更新 `web/src/api/events` 對應測試（若有）與 `EventListPage.test.ts` 的 mock 為分頁結構後，頁面尚未改，測試失敗。
  - 完成紀錄：待實作時填寫。
- [ ] 3.2 `web/src/pages/buyer/EventListPage.test.ts`（既有檔）先寫測試並更新既有測試的 mock 為 `{ items, page, pageSize, totalCount }`：BW-LIST-PAGE-001（換頁以 `page=2` 重新呼叫、載入中狀態、列表換為第 2 頁）、002（總數不超過一頁不顯示 `el-pagination`）、003（`totalCount=0` 顯示「目前沒有活動」且不顯示分頁）、004（空頁且 `totalCount>0` 只重試一次最後一頁，重試仍空不再重試）、005（換頁失敗退回前一頁、列表保留、顯示錯誤）、006（首次載入失敗顯示錯誤、不顯示分頁與空狀態）、007（快速換頁丟棄過期回應）、008（頁碼不進網址、離頁返回回第 1 頁），並維持既有的販售狀態標籤與訂單建立提示測試。
  - 預期 RED：頁面仍呼叫舊的 `getEvents()` 且把回應當陣列時，分頁相關測試失敗（取實際輸出原文）。
  - 模板注意：現行為 `el-alert v-if=errorMessage` → `el-empty v-else-if` → 格線 `v-else`，錯誤時會隱藏列表；BW-LIST-PAGE-005 要求換頁失敗時保留前一頁列表並顯示錯誤，v-if 鏈須改為「錯誤提示與列表可並存」，首次載入失敗（BW-LIST-PAGE-006）仍不顯示列表、分頁與空狀態。
  - 實作：`EventListPage.vue` 沿用 `admin/EventListPage.vue` 的換頁邏輯（`currentPage`／`loadedPage`／`pageSize`／`totalCount`、請求序號、空頁重試、失敗還原）；`el-pagination` 於 `totalCount > pageSize` 時顯示；每次成功載入更新 `loadedAtMs`；`router-link` 卡片、`SalesStatusTag`、`placedOrderId` 提示不變。
  - 完成紀錄：待實作時填寫。

## 4. 文件

- [ ] 4.1 `docs/api-conventions.md` 第 3 節「列表分頁」把 `GET /api/events` 由「後續 change」移到「已套用」（註明排序規則、單一整份快取 `v3`、單一活動端點不受影響）；`docs/api-conventions-todo.md` 第 6 項勾選；刪除 `docs/public-event-list-pagination-todo.md`。TDD：N/A（純文件）。
  - 完成紀錄：待實作時填寫。

## 5. 全套驗證與收尾

- [ ] 5.1 全套測試：`docker compose exec -T api dotnet test`（各專案）與 `docker compose exec -T web npm run test`，逐專案貼摘要行原文；`vue-tsc -b`、eslint 變更檔通過。
  - 完成紀錄：待實作時填寫。
- [ ] 5.2 真實瀏覽器驗證（claude-in-chrome，https://localhost:5173）：以 `docker compose exec db psql` 暫時新增至 45 場活動（插入後 DEL `query-cache:events:list:v3` 或等 TTL 過期）（含建立時間 null 與同時間者；記錄新增前後筆數，結束後還原），驗證匿名開啟買家列表：第 1 頁 20 張卡片、分頁元件顯示總數 45、換到第 3 頁顯示 5 張、頁碼不在網址、點進活動詳情頁（單一活動端點）後返回回到第 1 頁；`GET /api/events?pageSize=101` 回 400；數字以頁面文字與 DB 核對，不只看截圖。
  - 完成紀錄：待實作時填寫。
- [ ] 5.3 實作完成後執行 `graphify update .`，並在交接摘要列出 spec 同步狀態（`event-catalog`、`query-caching`、`buyer-web-ui`）。
  - 完成紀錄：待實作時填寫。
