# Tasks

> 對應 spec：`event-management`（MODIFIED：EVT-LIST-001～009）、`admin-web-ui`（ADDED：AWU-EVENT-PAGE-001～007）；`list-pagination` 沿用不修改（LP-* 場景由 2.1 的 HTTP 層測試覆蓋）。
> 既有 Scenario 覆蓋：EVT-LIST-001～004 語意未變，由更新後的既有測試（改為斷言分頁 envelope）覆蓋——`GetAdminEventsHandlerTests`、`AdminEventsControllerTests`；EVT-LIST-005（公開端點不回傳後台欄位）由不在本次修改範圍的 `EventsControllerTests.cs` 既有測試覆蓋，於 6.2 全套測試確認持續通過；各 task 的 RED 證據須逐一列出受影響的既有測試名稱。
> 所有後端命令在容器內執行：`docker compose exec -T api dotnet test <專案> --filter "..."`；前端：`docker compose exec -T web npm run test -- <檔案>`。
> 每個 task 的「完成紀錄」於實作時依 CLAUDE.md「證據貼原文」填寫（RED：命令、失敗測試名、Expected／Actual 原文、需在修改正式程式碼前；GREEN：命令與摘要行；重構後重跑或 N/A）。核心列表查詢的 task（1.1、2.1）另預留「還原驗證」紀錄，使用 `scripts/verify-test-evidence.sh`（mutate／restore 規則見 CLAUDE.md；mutate 後以 `touch` 被改檔案再重建，避免容器沿用舊 dll）。

## 1. Repository 分頁查詢（Domain／Infrastructure）

- [ ] 1.1 在 `tests/ProjectC.Infrastructure.Tests/Events/` 新增 Testcontainers 整合測試 `EventRepositoryPagingTests`，測 `GetPageByOrganizerIdAsync(organizerId, skip, take, ct)`：先寫測試。涵蓋 EVT-LIST-001／006（`skip`／`take` 切頁；`TotalCount` 只計該 Organizer 名下活動，不含他人）、EVT-LIST-008（`CreatedAtUtc` 各不相同且寫入順序打亂 → 新到舊；建立時間相同 → `Id` 升冪；`CreatedAtUtc` 為 null 的活動排在所有有建立時間的活動之後，且 null 之間依 `Id` 升冪；逐頁合併後每筆恰好一次）、LP-RESP-003／004（skip 超過總數回空 `Items` 但 `TotalCount` 為實際總數；無資料回空與 0）。預期 RED：先在 `IEventRepository`／`EventRepository` 加入方法簽章並回 `new EventPage([], 0)` 使其可編譯（同步新增 Domain `EventPage`），測試因 Expected 筆數／順序不符失敗（不得以編譯失敗當 RED）。再實作：同一個範圍 query 供 `CountAsync` 與取頁，`AsNoTracking().OrderBy(e => e.CreatedAtUtc == null).ThenByDescending(e => e.CreatedAtUtc).ThenBy(e => e.Id).Skip().Take()`。並從 `IEventRepository` 與 `EventRepository` 移除 `GetByOrganizerIdAsync`。
  - RED 紀錄：（實作時填入：命令、失敗測試名、Expected／Actual 原文、標明在修改正式程式碼前）
  - GREEN 紀錄：（實作時填入）
  - 還原驗證紀錄：（簽章被 stash 後測試專案無法編譯，改用 `mutate`，一次一個改動，至少含：移除 `OrderBy(e => e.CreatedAtUtc == null)`、`ThenByDescending(CreatedAtUtc)` 改升冪、`ThenBy(Id)` 改降冪、`Skip(skip)` 改 `Skip(0)`；貼原文輸出）
  - 重構後重跑：（實作時填入或標示 N/A）

- [ ] 1.2 更新所有實作 `IEventRepository` 的測試替身（以 `rg "GetByOrganizerIdAsync" tests` 為準，目前包含 `GetMyOrdersHandlerTests`、`QueryCacheTtlSafetyNetTests`、`JoinPurchaseQueueHandlerIntegrationTests`、`OrderServiceQueueModeLinearizationTests`、`PurchaseQueueAdmissionServiceLeaderElectionTests`、`PurchaseQueueAdmissionServiceTests`、`PurchaseQueueMirrorReconciliationTests` 內的 wrapper／fake；另有 `OrganizerScopingFaultInjectionWebApplicationFactory`、`CountingEventRepository`、`FakeEventRepository`、`InterceptingEventRepository`，其中 Application.Tests 的 `FakeEventRepository` 須實作實際分頁行為而非僅轉發）：以 `GetPageByOrganizerIdAsync` 取代 `GetByOrganizerIdAsync` 的轉發。此 task 無獨立行為，驗證為 `docker compose exec -T api dotnet build` 與各測試專案 `dotnet build` 通過，且 `rg "GetByOrganizerIdAsync" src tests --glob '!**/obj/**'` 在 `IEventRepository` 相關檔案無結果（`IOrderRepository` 已無同名方法）。TDD N/A（測試替身，由 1.1、2.1 間接驗證）。
  - 完成紀錄：（實作時填入 build 摘要行與 `rg` 輸出）

## 2. Handler 與 Controller（Application／WebApi）

- [ ] 2.1 先改寫／新增測試。
  - `tests/ProjectC.Application.Tests/Events/GetAdminEvents/GetAdminEventsHandlerTests`：`HandleAsync(organizerId, PageRequest, ct)` 回傳 `Result<PagedResultDto<AdminEventSummaryDto>>`。涵蓋 LP-VALID（越界回 `Validation`、`FieldErrors` 含 `page`／`pageSize`、Repository 未被呼叫）、EVT-LIST-006（envelope 欄位、最後一頁不足、超過最後一頁回空 `items` 與實際 `totalCount`、無資料）、EVT-LIST-009（先擴充 `tests/ProjectC.Application.Tests/TestSupport/FakeEventSeatRepository.cs`，為 `GetByEventIdsAsync` 加上 `GetByEventIdsCallCount` 與 `LastGetByEventIdsIds`，並讓 `FakeEventRepository` 實作實際分頁；斷言座位查詢收到的 Id 只含當頁、當頁為空時座位查詢收到空集合。Members 查詢走 MockQueryable 的 `DbSet`，Application 層無法觀察其 Id 或是否被跳過，「空頁不查 Members」改由 WebApi 查詢次數測試驗證：空頁時 Members 表查詢次數為 0）、EVT-LIST-002／003／004（統計、過期 Held 視為 Available、無座位為 0，沿用並改為斷言 `Items`）、`CancellationToken` 原樣傳遞給 Repository。預期 RED：Handler 簽章先改為收 `PageRequest` 並回舊內容（不驗證、不切頁）使其可編譯，RED 為 `Validation` 未回、`TotalCount`／`Items` 不符、批次查詢收到全部 Id。
  - `tests/ProjectC.WebApi.Tests/Admin/AdminEventsControllerTests`：涵蓋 LP-PARAM-001／002（未帶參數用預設值、只帶一個）、LP-VALID-001～008 的 HTTP 層（`PaginationTestSupport.InvalidPagingQueries` 全部案例，含 `page=2147483647` 回 400 而非 500；`errors` 鍵為小寫 `page`／`pageSize`）、EVT-LIST-001（Organizer A 看不到 B，且 `totalCount` 不含 B）、EVT-LIST-006／008（envelope 形狀、排序、null 建立時間排最後）、上限案例 `page=10000&pageSize=100` 回 200（比照 `*_WithUpperBoundPagingParameters_Returns200`）、既有 401／403 行為不變；`EventManagementAuthorizationMatrixTests.cs` 約第 98、200（`ReadFromJsonAsync`）、229 行，以及 `AdminEventsControllerTests.cs` 約第 987、1048 行（需逐一確認讀取方式），以 `GetFromJsonAsync<List<AdminEventSummaryDto>>("/api/admin/events")` 反序列化，須改為讀分頁物件的 `items`（否則執行期 `JsonException`）。同類陣列反序列化還有 `EventOrganizerBackfillEndToEndTests.cs:67`，以及 `AdminEventsControllerTests.cs` 約第 113、225、242、658、689（`GetAdminEventAsync` helper）、856 行，RED 紀錄須逐一列出。否定斷言（`AssertWriteDidNotHappenAsync` 的 `NotContain(probe)`、`GetAdminEventAsync` 依 Id 找活動）改讀分頁後只看第 1 頁會變成恆真或找不到：改以 `pageSize=100` 讀取並斷言 `totalCount` 小於 100，或逐頁讀完。EVT-LIST-009 的查詢次數：新增或擴充查詢次數測試（比照 `OrderDisplayQueryCountTests`），同一 pageSize 下，Organizer 總活動數 3 vs 25（頁內筆數不同、總數也不同）時 Events、Members、EventSeats 各表查詢次數相同；種子活動必須都帶 `CreatedByMemberId`（否則 Members 次數會是 0 vs 1 的假差異）；另一案例為空頁時 Members 次數為 0；先實測並寫入新的絕對值，不可只保留「兩次相等」的比較。預期 RED：Controller 尚未接收分頁參數、仍回裸陣列，`items`／`totalCount` 不存在、400 未回。
  - 再實作 `GetAdminEventsHandler`（驗證 → Repository 分頁 → 只對當頁查建立者名稱與座位 → `PagedResultDto`）與 `AdminEventsController.GetEvents`（`[FromQuery] int page = PageRequest.DefaultPage, [FromQuery] int pageSize = PageRequest.DefaultPageSize` → `result.ToActionResult(Ok)`，保留 `TryGetOrganizerId` 失敗時 `Forbid()` 的 fail-closed 分支）。
  - RED 紀錄：（實作時填入：每個失敗測試名與 Expected／Actual 原文，標明在修改正式程式碼前）
  - GREEN 紀錄：（實作時填入：每個測試專案摘要行）
  - 還原驗證紀錄：（以 `mutate` 一次一個改動：Handler 傳 `skip=0`／`take=int.MaxValue`（忽略分頁）、移除 validator 呼叫、批次查詢改用與當頁無關的 Id 集合；貼原文輸出）
  - 重構後重跑：（實作時填入或標示 N/A）

- [ ] 2.2 確認沒有意外的 schema 變更：`docker compose exec -T api dotnet ef migrations has-pending-model-changes --project src/ProjectC.Infrastructure --startup-project src/ProjectC.WebApi` 回報無待處理變更（不新增 migration）。TDD N/A（確認性檢查）。
  - 完成紀錄：（實作時填入輸出最後一行與 `git status` 無 migration 檔變更）

## 3. 負載測試 fixture

- [ ] 3.1 修改 `loadtest/fixtures/purchase-flow-ux-event.js` 的 `countRetainedEvents`：逐頁讀取（`page=1,2,…`、`pageSize=100`）直到 `items` 為空或已讀滿 `totalCount`，累計標題前綴相符的筆數；回應不是含 `items` 陣列的物件時維持 `fail(...)`。先確認 `loadtest/fixtures/run-purchase-flow-ux-event.test.sh` 能涵蓋或可擴充此行為；若無法以現有測試腳本涵蓋，驗證改為依 `loadtest/README.md` 的執行順序（seeder `--buyers 2` → 等 api 就緒 → `bash loadtest/fixtures/run-purchase-flow-ux-event.sh`）實跑一次，貼輸出，並以資料庫查詢核對 fixture 計數與實際保留活動數一致。預期 RED（若可自動化）：fixture 對新回應結構以 `did not return an array` 失敗；實跑則在 2.1 完成後、本 task 修改前先觀察到同一失敗訊息。
  - 完成紀錄：（實作時填入 RED 原文、修改後輸出、計數核對結果；實跑須遵守 README 的清理規則）

## 4. 文件

- [ ] 4.1 更新 `docs/api-conventions.md` 第 3 節「列表分頁」列：`GET /api/admin/events` 由「後續 change」移到「已套用」（含 `admin-event-list-pagination`），並註記活動列表的排序規則（`CreatedAtUtc` 新到舊、null 最後、`Id` 升冪）；`docs/api-conventions-todo.md` 第 6 項勾選 `GET /api/admin/events` 子項、保留 `GET /api/events` 待辦。驗證：`rg "admin-event-list-pagination" docs` 命中預期位置，`rg "後續 change.*admin/events" docs` 無殘留；TDD N/A（純文件）。
  - 完成紀錄：（實作時填入）

## 5. 前端

- [ ] 5.1 先寫 Vitest `web/src/api/admin.test.ts`：`getAdminEvents(page)` 以 `page` 查詢字串請求並回傳 `PagedResult<AdminEventSummary>`（比照既有 `getAdminOrders(page)` 測試）。預期 RED：目前函式不收參數、請求網址不含 `page`。再實作 `web/src/api/admin.ts` 的 `getAdminEvents(page = 1)`；`types/apiResponses.ts` 沿用既有 `PagedResult<T>`。
  - RED 紀錄：（實作時填入）
  - GREEN 紀錄：（實作時填入）
  - 重構後重跑：（實作時填入或標示 N/A）

- [ ] 5.2 先寫 Vitest `web/src/pages/admin/EventListPage.test.ts`：AWU-EVENT-PAGE-001（超過一頁顯示總筆數與頁碼、點第 2 頁以 `page=2` 重新請求並取代列表、載入中狀態）、AWU-EVENT-PAGE-002（不超過一頁不顯示分頁元件、空清單提示不顯示分頁）、AWU-EVENT-PAGE-003（空 `items` 且 `totalCount > 0` 只重試一次改請求最後一頁並同步分頁元件；重試仍為空不再重試）、AWU-EVENT-PAGE-004（換頁失敗：頁碼退回前一頁、列表保留前一頁並顯示錯誤提示）、AWU-EVENT-PAGE-005（快速連續換頁時過期回應被丟棄；另含過期回應「失敗」也被丟棄）、AWU-EVENT-PAGE-006（票種表單下拉選單只列當頁活動，換頁後改列新頁活動）、AWU-EVENT-PAGE-007（換頁失敗後下拉選單仍列前一頁活動、已選活動維持）、AWU-EVENT-PAGE-008（換頁成功且已選活動不在新一頁 → 清除 `ticketTypeForm.eventId`）、AWU-EVENT-PAGE-009（已選活動仍在該頁 → 保留）、既有場館名稱（AWU-EVENT-VENUE-001～003）、需實名標示、販售期間、票種展開等行為不變。`getAdminEvents` mock 回傳形狀（陣列 → 分頁物件）的更新範圍以 `rg "getAdminEvents" web/src` 核對（目前命中 `EventListPage.vue`、`EventListPage.test.ts`、`elementPlusOnDemand.test.ts`、`admin.ts`；`App.test.ts`、`router/index.test.ts` 只含路徑字串 `/admin/events`，不需改）。預期 RED：頁面仍把回應當陣列使用，或不呼叫帶 `page` 的 API／不渲染 `el-pagination`。再實作 `EventListPage.vue`：`currentPage`／`loadedPage` 為元件內 `ref`，`el-pagination` 的 `layout="total, prev, pager, next"`、`totalCount > pageSize` 才顯示，遞增請求序號丟棄過期回應；場館名稱查詢維持獨立只在掛載時查一次。
  - RED 紀錄：（實作時填入：vitest 失敗測試名與 Expected／Actual 原文，標明在修改正式程式碼前）
  - GREEN 紀錄：（實作時填入）
  - 還原驗證紀錄：（`mutate` 一次一個，至少含：移除請求序號比對、失敗時不退回 `loadedPage`、空頁不重試、換頁後不清除不在新一頁的已選活動；vitest 失敗時腳本會回報「沒有測試失敗摘要」exit 2，以 vitest 原文輸出為證據）
  - 重構後重跑：（實作時填入或標示 N/A）

- [ ] 5.3 後端啟動後（`docker compose restart api` 確保是新建置）在 `web` 容器執行 `npm run generate:api-types` 重新產生 `web/src/types/api.generated.ts`，檢查 diff 只包含 `GET /api/admin/events` 的 `page`／`pageSize` 查詢參數（其他既有漂移若出現須在紀錄中逐項說明）；`docker compose exec -T web npm run build`（含型別檢查）通過。
  - 完成紀錄：（實作時填入）

## 6. 最終檢查與驗證

- [ ] 6.1 hardener 最終檢查：程式碼全部完成後，對 `GetAdminEventsHandler`、`EventRepository.GetPageByOrganizerIdAsync`、`AdminEventsController.GetEvents` 套用 `.claude/skills/hardener/SKILL.md` 檢查清單（參數驗證、`CancellationToken` 傳遞、例外邊界等），發現缺口依 TDD 補 RED 測試再修改，結果與證據記錄於此（有修改時另列「hardener 補強」task）。
  - 完成紀錄：（實作時填入）

- [ ] 6.2 全套測試：`docker compose exec -T api dotnet test` 逐專案（Domain、Application、Infrastructure、WebApi、LoadTest.Seeder）與 `docker compose exec -T web npm run test`，每個測試專案的摘要行逐字貼入；`rg -n "admin/events" web/src loadtest scripts docs` 逐筆確認沒有遺漏的列表呼叫端（POST 建立活動、sales-report、queue-mode 等其他路徑不算）。
  - 完成紀錄：（實作時填入逐字摘要行與 `rg` 輸出）

- [ ] 6.3 真實瀏覽器驗證（claude-in-chrome）：以種子資料建立超過 20 筆活動的 Organizer（含至少一筆 `CreatedAtUtc` 為 null 的舊活動），操作後台活動列表的換頁、F5 後回到第 1 頁（頁碼不寫入網址）、票種表單下拉選單換頁後的選項與已選活動被清除；總筆數、頁碼、每頁內容與排序以頁面文字與資料庫查詢（`docker compose exec -T db sh -c 'psql ...' < file.sql`）核對，不只依截圖判讀。「最後一頁 fallback」與「換頁失敗退回前一頁」以 Vitest 的 mock 驗證。驗證完成後清除測試資料（含測試帳號），清除前先列出要刪除的目標。
  - 完成紀錄：（實作時填入）

- [ ] 6.4 審查迴圈：依序執行 `strict-reviewer` 與全新的對抗式 general-purpose subagent，修正後重跑直到兩者皆無 blocking；每輪結果與修正記錄於此。呼叫 `strict-reviewer` 時須把本 change 的 RED／GREEN／還原驗證證據摘要完整附在 prompt 中。
  - 完成紀錄：（實作時填入每輪結果）

## Workflow follow-up

- 全部 task 完成並通過審查後，等使用者明確同意再 `/opsx:archive admin-event-list-pagination`（含主 spec 同步），再 merge 回 master；不自動 commit／push。
- 後續 change 候選（不屬本 change）：公開 `GET /api/events` 分頁與快取鍵設計；三個列表頁共用的分頁 composable；在活動列展開區新增票種以取代下拉選單。
