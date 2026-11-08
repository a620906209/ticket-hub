# Tasks

> 對應 spec：`list-pagination`（新）、`buyer-order-query`（MODIFIED）、`order-administration`（MODIFIED）、`buyer-web-ui`（MODIFIED）、`admin-web-ui`（ADDED）。
> 既有 Scenario 覆蓋：MODIFIED 時原樣複製、語意未變的 Scenario——ORD-LIST-002/003/005/007（Organizer 範圍、資料不一致）、BOQ-LIST-002、BW-MYORDER-DETAIL-001/002、TICKET-ID-001、QR-001、NOTFOUND-001、FORBIDDEN-001、INVALID-ID-001、DISPLAY-001/002——由 3.1／4.1／6.2 更新後的既有測試覆蓋（改為斷言分頁 envelope）；ORD-LIST-006／008（後台排序）改由 2.1 的 Repository 整合測試覆蓋，原 `GetOrdersHandlerTests` 的 `HandleAsync_SortsByCreatedAtDescending`、`HandleAsync_WhenCreatedAtTies_SortsByIdAscendingRegardlessOfRepositoryOrder` 因排序移到資料庫端而刪除（3.1），各 task 的 RED 證據須逐一列出受影響的既有測試名稱；LP-VALID-004（`page=abc`／`pageSize=abc`、超出 int 範圍）由 4.1 的 HTTP 層測試覆蓋。
> 所有後端命令在容器內執行：`docker compose exec -T api dotnet test <專案> --filter "..."`；前端：`docker compose exec -T web npm run test -- <檔案>`。
> 每個 task 的「完成紀錄」於實作時依 CLAUDE.md「證據貼原文」填寫（RED：命令、失敗測試名、Expected／Actual 原文、需在修改正式程式碼前；GREEN：命令與摘要行；重構後重跑或 N/A）。核心列表查詢的 task（2.1、3.1）另預留「還原驗證」紀錄。

## 1. 共用分頁型別與驗證（Application／Domain）

- [ ] 1.1 在 `tests/ProjectC.Application.Tests/Common/` 新增 `PageRequestValidatorTests` 與 `PagedResultDtoTests`：先寫測試。涵蓋 LP-VALID-001/002/003/005/006/007（`pageSize` 101 失敗、100 通過；`page` 10001 與 `int.MaxValue` 失敗、10000 通過；`page` 0／-1、`pageSize` 0 失敗；兩欄位同時越界時 `errors` 同時含 `page` 與 `pageSize`）、LP-PARAM-001（`PageRequest.DefaultPage == 1`、`DefaultPageSize == 20`、`MaxPageSize == 100`、`MaxPage == 10000`）、`PagedResultDto` 保存 `Items/Page/PageSize/TotalCount`。預期 RED：以反射或最小空殼使測試可編譯後，驗證器對越界值回報「通過」或型別缺漏的斷言失敗（不得以編譯失敗當 RED；先新增空殼 `PageRequest`／`PageRequestValidator`／`PagedResultDto` 使其可編譯，再執行）。再實作 `Application/Common/Paging/PageRequest`、`PageRequestValidator`、`PagedResultDto<T>` 與 Domain `OrderPage` 使 GREEN。
  - RED 紀錄：（實作時填入）
  - GREEN 紀錄：（實作時填入）
  - 重構後重跑：（實作時填入，或 N/A）

## 2. Repository 分頁查詢（Domain／Infrastructure）

- [ ] 2.1 在 `tests/ProjectC.Infrastructure.Tests/Orders/` 新增（或擴充 `OrderRepositoryOrganizerScopingTests`）Testcontainers 整合測試，測 `GetPageByBuyerIdAsync` 與 `GetPageByOrganizerIdAsync`：先寫測試。涵蓋 BOQ-LIST-PAGE-001／ORD-PAGE-001（`skip`／`take` 切頁、`TotalCount` 僅計入該買家或該 Organizer 名下活動的訂單、不含他人訂單）、BOQ-LIST-ORDER-001 與後台 ORD-LIST-006（兩個方法各自：`CreatedAtUtc` 各不相同且寫入順序打亂，結果新到舊——`GetPageByOrganizerIdAsync` 若誤用升冪必須失敗）、BOQ-LIST-ORDER-002／ORD-PAGE-002／ORD-LIST-008／LP-ORDER-001（建立時間相同時依 `Id` 升冪、逐頁合併後每筆恰好一次；ORD-LIST-008 在真實資料庫無法重現「兩次查詢回傳順序不同」，以此 tie 測試對應）、LP-RESP-003／004（skip 超過總數回空 `Items` 但 `TotalCount` 為實際總數；無資料回空與 0）、`Items` 已載入 `Items`（訂單明細）集合。預期 RED：先在 `IOrderRepository`／`OrderRepository` 加入方法簽章並以 `throw new NotImplementedException()` 或回空結果使其可編譯，測試因 `NotImplementedException`／Expected 筆數不符而失敗。再實作：`CountAsync` 加 `OrderByDescending(CreatedAtUtc).ThenBy(Id).Skip().Take().Include(Items)`，後台版沿用 `Events.Any(OrganizerId)` 範圍過濾；`AsNoTracking`。
  - RED 紀錄：（實作時填入）
  - GREEN 紀錄：（實作時填入）
  - 還原驗證紀錄（`bash scripts/verify-test-evidence.sh restore`，輸出原文）：（實作時填入）
  - 重構後重跑：（實作時填入，或 N/A）
- [ ] 2.2 更新測試替身以實作新介面方法：`FakeOrderRepository`（在記憶體用同一排序與切頁語意，並記錄收到的 `CancellationToken`）。同時更新 `OrganizerScopingFaultInjectionWebApplicationFactory` 內也實作 `IOrderRepository` 的 `UnresolvableOrganizerOrderRepository`（改包裝新方法）。此 task 無獨立行為，驗證為 `docker compose exec -T api dotnet build` 與 `docker compose exec -T api dotnet build tests/ProjectC.WebApi.Tests` 通過；TDD N/A（測試替身，由 3.1 的測試間接驗證）。

## 3. Handler（Application）

- [ ] 3.1 在 `tests/ProjectC.Application.Tests/Orders/GetMyOrders/GetMyOrdersHandlerTests` 與 `GetOrders/GetOrdersHandlerTests` 先改寫／新增測試：`HandleAsync(buyerId 或 organizerId, PageRequest, ct)` 回傳 `Result<PagedResultDto<...>>`。涵蓋 LP-VALID（越界回 `Validation` 且 `FieldErrors` 含 `page`／`pageSize`、Repository 未被呼叫）、LP-RESP-001～004（envelope 欄位、最後一頁不足、超過最後一頁回空 `items` 與實際 `totalCount`、無資料）、BOQ-LIST-PAGE-001／ORD-PAGE-001、BOQ-LIST-TITLE-001（每筆摘要的 `EventTitle` 正確）、BOQ-LIST-TITLE-002／ORD-PAGE-004（資料不一致只在包含該筆的頁面失敗，另一頁正常）、活動名稱／買家名稱只針對當頁 Id 批次查詢（斷言 `GetByIdsAsync` 收到的 Id 只含當頁）、`CancellationToken` 原樣傳遞給 Repository。預期 RED：沿用 1.1、2.2 的空殼使其可編譯；Handler 目前簽章不收 `PageRequest`，先把 Handler 簽章改為接收 `PageRequest` 並回傳舊內容（不驗證、不切頁）使測試編譯，RED 為 `Validation` 未回、`TotalCount`／`Items` 筆數不符、`GetByIdsAsync` 收到全部 Id。再實作：驗證 → Repository 分頁查詢 → 當頁批次查名稱與資料不一致檢查 → 回 `PagedResultDto`（排序已由 Repository 完成，移除 `GetOrdersHandler` 內的記憶體排序，並刪除 `GetOrdersHandlerTests` 中測記憶體排序的兩個測試，排序驗證改由 2.1 負責）。`GetMyOrdersHandlerTests` 內的包裝類別是 `IEventRepository` 的包裝，不需因本 change 修改。
  - RED 紀錄：（實作時填入）
  - GREEN 紀錄：（實作時填入）
  - 還原驗證紀錄：（實作時填入）
  - 重構後重跑：（實作時填入，或 N/A）

## 4. Controller 與 WebApi 整合行為

- [ ] 4.1 在 `tests/ProjectC.WebApi.Tests/Orders/OrdersControllerTests` 與 `Admin/AdminOrdersControllerTests` 先改寫／新增測試：涵蓋 LP-PARAM-001／002（未帶參數用預設值、只帶一個）、LP-VALID-001～007 的 HTTP 層（含 `page=2147483647` 回 400 而非 500；400、`title` 為 `Validation`、`errors` 鍵為小寫 `page`／`pageSize`、`page=abc` 走模型繫結失敗路徑同格式）、LP-RESP-001～004、BOQ-LIST-001／002／ORD-LIST-001 的 envelope 形狀（`items`／`page`／`pageSize`／`totalCount`）與範圍不外洩（買家看不到他人訂單、Organizer A 看不到 B）、ORD-PAGE-002、既有 401／403 行為不變。同步調整 `OrganizerScopingDataCorruptionTests`（ORD-PAGE-004）與 `OrganizerScopingFaultInjectionWebApplicationFactory`（工廠本體，替身類別已於 2.2 處理），以及 `OrderDisplayQueryCountTests` 的列表測試：ORD-LIST-004（後台，當頁 1 筆 vs 3 筆，每張表查詢次數相同）與 BOQ-LIST-TITLE-003（買家，同樣比較 1 筆 vs 3 筆不同活動，`Events` 表查詢次數相同且為 1 次、`Orders` 表為固定次數；若逐筆查活動則此測試必須失敗）。既有測試的絕對次數錨點會改變（後台現為 `Orders`＝1、`Events`＝2，加入帶 `Events.Any` 範圍過濾的 COUNT 查詢後預期變為 `Orders`＝2、`Events`＝3；買家 `Orders` 1→2），須先重新實測並寫入新的絕對值，不可只保留「兩次相等」的比較，否則斷言可空洞通過。預期 RED：Controller 尚未接收分頁參數、仍回裸陣列，`items`／`totalCount` 欄位不存在、400 未回。再實作 `OrdersController.GetMyOrders`、`AdminOrdersController.GetOrders`（`[FromQuery] int page = PageRequest.DefaultPage, [FromQuery] int pageSize = PageRequest.DefaultPageSize` → `result.ToActionResult(Ok)`）。
  - RED 紀錄：（實作時填入）
  - GREEN 紀錄：（實作時填入）
  - 重構後重跑：（實作時填入，或 N/A）
- [ ] 4.2 移除 `IOrderRepository.GetByBuyerIdAsync`／`GetByOrganizerIdAsync` 與其實作、測試替身中的對應成員。驗證：`rg "GetByBuyerIdAsync|GetByOrganizerIdAsync" src tests --glob '!**/obj/**'` 在訂單 Repository 相關檔案無結果（`IEventRepository.GetByOrganizerIdAsync` 為活動的，不得誤刪），`dotnet build` 通過。TDD N/A（刪除無呼叫端的程式碼，由編譯與既有測試驗證）。
- [ ] 4.3 確認沒有意外的 schema 變更：`docker compose exec -T api dotnet ef migrations has-pending-model-changes --project src/ProjectC.Infrastructure --startup-project src/ProjectC.WebApi` 應回報無待處理變更（本 change 不新增 migration）。TDD N/A（確認性檢查）。完成紀錄：（實作時填入命令與輸出）

## 5. 文件

- [ ] 5.1 更新 `docs/api-conventions.md`：§3「列表分頁」列改為本慣例（參數、預設、上限、越界 400、回應結構、確定性排序，並標註適用端點與 `list-pagination` 能力）；§4 加入新增分頁端點的更新時機；記錄 `venues`、`organizers/mine`、`admin/organizers` 目前不做分頁的理由（`venues` 為少量固定資料；`organizers/mine` 受限於單一會員的 Organizer 數；`admin/organizers` 為全平台主辦方清單，會隨申請增加，目前規模可接受並列為觀察項目，規模成長時另案分頁），`/api/admin/events`、`/api/events` 標為後續 change。`docs/api-conventions-todo.md` 第 6 項更新進度（本 change 範圍已完成的項目打勾，剩餘項目保留）。驗證：`rg "未發現統一的分頁" docs` 無結果；TDD N/A（純文件）。

## 6. 前端

- [ ] 6.1 先寫 Vitest：`web/src/api/orders.test.ts`（新增或併入現有）與 `admin.test.ts`：`getMyOrders(page)`／`getAdminOrders(page)` 以 `page` 查詢字串請求並回傳 `PagedResult`。預期 RED：目前函式不收參數、請求網址不含 `page`。再實作 `web/src/types/apiResponses.ts` 的 `PagedResult<T>`、兩個 API 函式。
  - RED 紀錄：（實作時填入）
  - GREEN 紀錄：（實作時填入）
- [ ] 6.2 先寫 Vitest（`MyOrdersPage.test.ts`、`AdminOrderListPage.test.ts`）：BW-MYORDER-LIST-001（第 1 頁）、BW-MYORDER-PAGE-001／AWU-ORDER-PAGE-001（超過一頁顯示總筆數與頁碼、點第 2 頁以 `page=2` 重新請求並取代列表、載入中狀態）、BW-MYORDER-PAGE-002／AWU-ORDER-PAGE-002（不超過一頁不顯示分頁元件、空清單提示不顯示分頁）、BW-MYORDER-PAGE-003／AWU-ORDER-PAGE-003（重新整理停留在目前頁；空 `items` 且 `totalCount > 0` 時只重試一次改請求最後一頁，並同步分頁元件顯示該頁）、BW-MYORDER-PAGE-004／AWU-ORDER-PAGE-004（換頁請求失敗：頁碼退回前一頁、列表保留前一頁資料並顯示錯誤提示）、BW-MYORDER-PAGE-005／AWU-ORDER-PAGE-005（快速連續換頁時過期回應被丟棄）、既有狀態標籤與持有到期時間顯示不變。列表 API 的 mock 回傳形狀（陣列 → 分頁物件）只需更新這兩個測試檔；`orderConfirmationFlow.test.ts`、`BuyerLayout.test.ts`、`elementPlusOnDemand.test.ts` 不引用 `getMyOrders`／`getAdminOrders`，不需修改（實作時以 `rg "getMyOrders|getAdminOrders" web/src` 再核對）。預期 RED：頁面仍把回應當陣列使用，或不呼叫帶 `page` 的 API／不渲染 `el-pagination`。再實作兩個頁面（`currentPage` 為元件內 `ref`，`el-pagination` 的 `layout="total, prev, pager, next"`，請求序號丟棄過期回應）。
  - RED 紀錄：（實作時填入）
  - GREEN 紀錄：（實作時填入）
  - 重構後重跑：（實作時填入，或 N/A）
- [ ] 6.3 後端啟動後在 `web` 容器執行 `npm run generate:api-types` 重新產生 `web/src/types/api.generated.ts`，檢查 diff 只包含兩個端點的 `page`／`pageSize` 查詢參數與回應結構；`docker compose exec -T web npm run build`（含型別檢查）通過。完成紀錄：（實作時填入命令與 diff 摘要）

## 7. 最終檢查與驗證

- [ ] 7.1 hardener 最終檢查：程式碼全部完成後，對 `GetMyOrdersHandler`、`GetOrdersHandler`、`OrderRepository` 新方法與兩個 Controller 套用 `.claude/skills/hardener/SKILL.md` 檢查清單（參數驗證、`CancellationToken` 傳遞、例外邊界等），發現缺口依 TDD 補 RED 測試再修改，結果與證據記錄於此。完成紀錄：（實作時填入）
- [ ] 7.2 全套測試：`docker compose exec -T api dotnet test` 逐專案（Domain、Application、Infrastructure、WebApi、LoadTest.Seeder）與 `docker compose exec -T web npm run test`，每個測試專案的摘要行逐字貼入。`rg -n "api/orders|api/admin/orders" loadtest` 預期只命中 POST `/api/orders` 與訂單 confirm（`baseline.js`、`lib/load-test.js`、`fixtures/purchase-flow-ux-event.js`），逐筆確認沒有任何一筆是 GET 列表呼叫。完成紀錄：（實作時填入）
- [ ] 7.3 真實瀏覽器驗證（claude-in-chrome）：以種子資料建立超過 20 筆訂單的買家與 Organizer，操作「我的訂單」與後台訂單列表的換頁、重新整理停留頁（「最後一頁 fallback」與「換頁失敗退回前一頁」以 Vitest 的 mock 回應驗證，真實瀏覽器不手動刪資料，因專案目前沒有刪除訂單的路徑）；總筆數、頁碼與每頁內容以頁面文字與資料庫查詢（`docker compose exec db psql` 於容器內）核對，不只依截圖判讀。完成紀錄：（實作時填入）
- [ ] 7.4 審查迴圈：依序執行 `strict-reviewer` 與全新的對抗式 general-purpose subagent，修正後重跑直到兩者皆無 blocking；每輪結果與修正記錄於此。完成紀錄：（實作時填入）

## Workflow follow-up

- 審查通過且使用者明確同意後才 `/opsx:archive`（含主 spec 同步）並 merge 回 master；任何步驟不自動 commit／push。
- 後續 change：`admin-event-list-pagination`（`GET /api/admin/events`，引用本 change 的 `list-pagination`）、公開 `GET /api/events` 的分頁與快取設計。
