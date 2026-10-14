## AC 與測試對應

本表列出每條 AC 的測試方法。標「既有」的是已存在、本 change 保留原斷言的測試，資料與成功／失敗斷言詳見 4.0、5.0。全量測試通過只作回歸確認，不作為任何 AC 的唯一證據。

| AC | 測試檔／方法 | 任務 |
| --- | --- | --- |
| TO-CREATED-001 | `CreateOrderHandlerTests`（新增） | 2.2 |
| TO-CREATED-002 | `OrderTests`（新增） | 1.1 |
| TO-CREATED-003 | `OrderCreatedAtBackfillMigrationTests`（新增，Testcontainers） | 3.4 |
| ORD-LIST-001 | 既有：`GetOrdersHandlerTests.HandleAsync_ReturnsCallerOrganizerOrdersWithLiveStatus`、`OrderRepositoryOrganizerScopingTests.GetByOrganizerIdAsync_WhenOrdersExistUnderTwoOrganizers_ReturnsOnlyCallerOrganizerOrdersWithItems`、`OrderRepositoryOrganizerScopingTests.GetByOrganizerIdAsync_DoesNotMaterializeOtherOrganizerOrders`、`AdminOrdersControllerTests.GetOrders_ReturnsOnlyCallerOrganizerOrders` | 4.0 |
| ORD-LIST-002 | 既有：`GetOrdersHandlerTests.HandleAsync_WithTwoBuyers_ReturnsBuyerDisplayNamesWithSingleDistinctBatchQuery`、`AdminOrdersControllerTests.GetOrders_WithTwoBuyers_ReturnsBuyerDisplayNamesWithoutEmail` | 4.0 |
| ORD-LIST-003 | 既有：`GetOrdersHandlerTests.HandleAsync_WhenBuyerMissing_ThrowsWithOrderAndBuyerIds`、`OrganizerScopingDataCorruptionTests.GetAdminOrders_WhenOneBuyerMissing_Returns500AndLogsIdsWithoutLeaking` | 4.0 |
| ORD-LIST-004 | `OrderDisplayQueryCountTests.GetAdminOrders_*`（修改） | 4.4 |
| ORD-LIST-005 | `GetOrdersHandlerTests`（新增）、`AdminOrdersControllerTests`（新增） | 4.1、4.3 |
| ORD-LIST-006 | `GetOrdersHandlerTests`（新增） | 4.1 |
| ORD-LIST-007 | `GetOrdersHandlerTests`（新增）、`OrganizerScopingDataCorruptionTests.GetAdminOrders_WhenOneEventMissing_Returns500AndLogsIdsWithoutLeaking`（新增） | 4.1、4.3a |
| ORD-LIST-008 | `GetOrdersHandlerTests`（新增） | 4.1 |
| AWU-ORDER-LIST-001 | 既有：`AdminOrderListPage.test.ts` 的「訂單狀態以中文標籤顯示…」「買家顯示名稱含 HTML 標籤時原樣以文字顯示…」 | 5.0 |
| AWU-ORDER-DETAIL-001 | 既有：`AdminOrderDetailPage.test.ts` 的「混合座位項目與計數項目…」 | 5.0 |
| AWU-ORDER-HOLD-001 | 列表：`AdminOrderListPage.test.ts`「持有到期時間只對 Pending 訂單顯示」（改寫）；詳情：既有 `AdminOrderDetailPage.test.ts` 的 Pending、Paid 兩則 | 5.1、5.0 |
| AWU-ORDER-LIST-002 | `AdminOrderListPage.test.ts`（新增） | 5.1 |
| AWU-ORDER-LIST-003 | `AdminOrderListPage.test.ts`（新增） | 5.1 |

## 1. Domain：訂單建立時間與加總方法

- [x] 1.1 先寫測試（`OrderTests`）：建構時記錄 `CreatedAtUtc`；`createdAtUtc` 為 `default` 時丟 `ArgumentException`（TO-CREATED-002）；`GetTicketCount()` 與 `GetTotalAmount()` 對「1 張 1000＋1 張 1500」分別回傳 2 與 2500，對「單一項目 Quantity 3、UnitPrice 800」回傳 3 與 2400（防止只算項目筆數或忽略 Quantity）
- [x] 1.2 `Order` 新增 `CreatedAtUtc`（公開建構子參數置於 `heldUntilUtc` 前、EF 物化用 private 建構子同步新增）、`GetTicketCount()`、`GetTotalAmount()`，讓 1.1 通過
- [x] 1.3 修正所有 `new Order(` 呼叫端（`src` 與 `tests`），全量 build 無錯誤；補的 `createdAtUtc` 一律為 `DateTimeKind.Utc`（Npgsql timestamptz 不接受 Unspecified）

## 2. Application：建單記錄建立時間

- [x] 2.1 `CreateOrderHandler` 以同一個 `now` 傳入 `createdAtUtc`
- [x] 2.2 測試（`CreateOrderHandlerTests`）：固定 `IDateTimeProvider` 為 T，建立的訂單 `CreatedAtUtc == T`、`HeldUntilUtc == T + 10 分鐘`（TO-CREATED-001）

## 3. Infrastructure：欄位對應與 migration

- [x] 3.1 `OrderConfiguration` 設定 `CreatedAtUtc` 為必填
- [x] 3.2 `docker compose exec api dotnet ef migrations add AddOrderCreatedAt`，改寫為：新增 nullable 欄位 → `migrationBuilder.Sql` 以 `HeldUntilUtc - INTERVAL '10 minutes'` 回填 → 改為 NOT NULL；`Sql` 上方註解寫明原生 SQL 例外理由（design.md 決策 2）；Down 僅刪欄
- [x] 3.3 補上測試內 raw SQL `INSERT INTO "Orders"` 的 `CreatedAtUtc` 欄（`ForeignKeyConstraintsTests` 136／149／164 行、`GetOrderByIdLegacyDataTests` 37 行、`GetPaidItemSalesByEventIdAsyncTests` 114 行；以 grep 確認沒有遺漏）
- [x] 3.4 Migration 測試（比照 `EventSalesWindowMigrationTests`，Testcontainers）：先 migrate 到前一版，插入兩筆不同 `HeldUntilUtc` 的訂單，再 migrate 到本版；驗證兩筆的 `CreatedAtUtc` 各為自己的 `HeldUntilUtc − 10 分鐘`（兩筆值不同，防止以單一常數回填也能通過），且之後插入 `CreatedAtUtc` 為 NULL 的訂單會失敗（TO-CREATED-003）。另加 Down 後再 Up：Down 後 `CreatedAtUtc` 欄位不存在、訂單列仍在；再 Up 後兩筆的 `CreatedAtUtc` 與第一次 Up 的值相同（驗證 design.md 決策 2「回滾後再 Up 得到相同結果」）。保存測試執行輸出（通過的測試名稱與筆數）於 PR 說明
- [x] 3.5 以開發 DB 依 design.md Migration Plan 的「套用」runbook 步驟 1～9 演練，逐步記錄指令輸出（結果記入 PR 說明）：
  - 成功：步驟 1 與 6 的 migration 版本分別為 `AddEventSalesWindow`、`AddOrderCreatedAt`；步驟 3 停止期間 API 呼叫失敗且 `Orders` 筆數不變；步驟 5 記下耗時；步驟 6 欄位為 `timestamp with time zone`、`is_nullable = 'NO'`，NULL 筆數 0、回填不符筆數 0、總筆數不變；步驟 8 新訂單 201、`HeldUntilUtc - CreatedAtUtc = 10 分鐘`、列表第一筆為該訂單且 `createdAtUtc` 以 `Z` 結尾。
  - 失敗：任一查詢結果不符，或停止期間仍有建單成功（`Orders` 筆數增加）。此時停在該步驟，不啟動新版，先回報。
  - 開發 DB 若沒有既有訂單，先以「舊版」API 建立至少一筆 Pending 訂單與一筆 Paid 訂單再演練，否則回填驗證是空集合，沒有意義。
  - **演練結果（2026-10-14，開發 DB）**：步驟 1 最新 migration `20261003115627_AddEventSalesWindow`、`Orders` 2 筆（Paid 1、Cancelled 1，`HeldUntilUtc` 2026-10-13 11:10:59～12:04:42 UTC）；步驟 2 只有 `api` 寫入（pgadmin 已 exited）；步驟 3 停止後直連 8080 無回應、經 web 502、筆數 2、`pg_stat_activity` 0 列；步驟 4 備份 93 行（本機暫存，不進版控）；步驟 5 `real 0m8.060s`，輸出 `Applying migration '20261014081305_AddOrderCreatedAt'. Done.`（另有 EF 工具結束設計時期 host 的 `HostAbortedException` 訊息，非錯誤）；步驟 6 最新 `20261014081305_AddOrderCreatedAt`、`timestamp with time zone`／`NO`、total 2／nulls 0／mismatched 0；步驟 7 約 20 秒 `GET /api/events` 200；步驟 8 新訂單 `8413313c-93d6-45c3-8f3a-e7a6b7286378` `POST /api/orders` 201，`HeldUntilUtc - CreatedAtUtc = 00:10:00`，`CreatedAtUtc` 10:39:26.458042 落在呼叫前後 10:39:26.140～10:39:30.143，`GET /api/admin/orders` 200 第一筆為該訂單且 `createdAtUtc` 為 `2026-10-14T10:39:26.458042Z`
- [x] 3.6 依 design.md Migration Plan 的「回滾」步驟，在 3.5 之後演練一次回滾與重新上線，Down、舊版建單、重新套用各步驟的完整指令與實際輸出記入 PR 說明：
  - 回滾前：記錄 3.5 步驟 8 那筆新訂單的 `Id`、`CreatedAtUtc`。
  - 成功：Down 後 `__EFMigrationsHistory` 不含 `AddOrderCreatedAt`、`CreatedAtUtc` 欄位不存在、筆數不變；舊版建單 201；重新套用後該筆新訂單的 `CreatedAtUtc` 與回滾前記錄的值相同；舊版回滾期間建立的訂單為 `HeldUntilUtc − 10 分鐘`；全部 NULL 筆數 0。
  - 失敗：Down 拋例外、欄位仍在、舊版建單失敗，或重新套用後新訂單的值改變。
  - **演練結果（2026-10-14）**：回滾前記錄 `8413313c…` `CreatedAtUtc = 2026-10-14 10:39:26.458042+00`；停 api 後筆數 3、其他連線 0；回滾指令 `real 0m8.134s`，`Reverting migration '20261014081305_AddOrderCreatedAt'. Done.`；驗證 history 不含 `AddOrderCreatedAt`、欄位數 0、筆數 3。舊版以 `git worktree`（`1104451`，工作目錄未 commit 的修改不受影響）＋`docker compose -p projectc --env-file <主目錄 .env> -f <worktree>/docker-compose.yml up -d --no-deps api` 啟動（`/src` 掛載確認為 worktree），舊版建單 `33f7f036-c72c-4be1-8a25-e5c1e63628d6` 201。停 api 後在主目錄重新套用（`real 0m11.740s`），驗證：最新 `AddOrderCreatedAt`、`timestamptz`／`NO`、`8413313c` 值與回滾前相同、`33f7f036` 為 `HeldUntilUtc − 10 分鐘`、total 4／nulls 0／mismatched 0；新版重新啟動 200，worktree 已移除

## 4. Application：訂單列表 DTO 與查詢

- [x] 4.0 既有 ORD-LIST-001～003 測試在本 change 後仍須以原斷言通過，只允許調整建構與 Fake 設定，不得放寬斷言。`GetOrdersHandlerTests` 因注入 `IEventRepository`，`SeedOrder` 須同時把活動放進 Fake event repository；否則買家缺失測試會先因活動查不到而丟出錯的例外。各測試的資料與斷言：
  - ORD-LIST-001 `GetOrdersHandlerTests.HandleAsync_ReturnsCallerOrganizerOrdersWithLiveStatus`：資料為 Organizer A 的 Pending 訂單（Alice）、A 的已逾時訂單（Bob）、另一 Organizer 的訂單。成功：恰回傳 2 筆，分別為 `Pending`／Alice 與 `Expired`／Bob。失敗：含其他 Organizer 的訂單 Id、筆數不是 2，或逾時訂單回報 `Pending`。
  - ORD-LIST-001 `OrderRepositoryOrganizerScopingTests.GetByOrganizerIdAsync_WhenOrdersExistUnderTwoOrganizers_ReturnsOnlyCallerOrganizerOrdersWithItems`（Testcontainers）：資料為 Organizer A、B 各 2 筆含項目的訂單。成功：Id 集合等於 A 的訂單，項目數與種子一致。失敗：與 B 的 Id 有交集，或項目數不符。
  - ORD-LIST-001 `OrderRepositoryOrganizerScopingTests.GetByOrganizerIdAsync_DoesNotMaterializeOtherOrganizerOrders`：資料同上，查詢時掛具現化紀錄器。成功：被具現化的 Order／OrderItem 全屬 A，且筆數等於 A 的訂單數（防紀錄器未生效）。失敗：B 的訂單或項目被具現化，代表在記憶體過濾。
  - ORD-LIST-001 `AdminOrdersControllerTests.GetOrders_ReturnsOnlyCallerOrganizerOrders`：資料為 Organizer A、B 各以 API 建一筆 Pending 訂單，以 A 的 token 呼叫 `GET /api/admin/orders`。成功：Id 集合恰為 A 那一筆，狀態 `Pending`、買家名稱「Test User」。失敗：回應含 B 的訂單 Id。
  - ORD-LIST-002 `GetOrdersHandlerTests.HandleAsync_WithTwoBuyers_ReturnsBuyerDisplayNamesWithSingleDistinctBatchQuery`：資料為 Alice 兩筆、Bob 一筆。成功：各筆名稱正確，買家名稱查詢恰 1 次，查詢 Id 集合為 {Alice, Bob}。失敗：查詢超過 1 次，或 Id 重複或缺漏。
  - ORD-LIST-002 `AdminOrdersControllerTests.GetOrders_WithTwoBuyers_ReturnsBuyerDisplayNamesWithoutEmail`：資料為 Alice、Bob 各下一筆訂單。成功：200，訂單 Id 到名稱的對照為 {Alice, Bob}。失敗：回應本文以不分大小寫比對出現「email」，或出現任一買家的 Email 字串。本 change 新增的欄位名稱不得含 email 字樣，此測試即為守門。
  - ORD-LIST-003 `GetOrdersHandlerTests.HandleAsync_WhenBuyerMissing_ThrowsWithOrderAndBuyerIds`：資料為兩筆訂單，其中一筆的買家從 Fake 名稱表移除，活動皆存在。成功：丟 `InvalidOperationException`，訊息含孤兒訂單 Id 與 BuyerId。失敗：回傳列表（部分資料），或丟出的是活動缺失的例外。
  - ORD-LIST-003 `OrganizerScopingDataCorruptionTests.GetAdminOrders_WhenOneBuyerMissing_Returns500AndLogsIdsWithoutLeaking`：資料為一筆完整訂單、一筆被注入買家缺失的訂單。成功：500 ProblemDetails，log 恰一筆帶 traceId，且含訂單 Id 與 BuyerId。失敗：回應本文含 `buyerDisplayName`、`buyerId`、任一訂單 Id、BuyerId 或例外訊息，代表回傳了部分資料或洩漏內部資訊。
- [x] 4.1 先寫測試（`GetOrdersHandlerTests`，以 Fake repository）：
  - ORD-LIST-005：`EventTitle`、`TicketCount`、`TotalAmount`、`CreatedAtUtc` 正確
  - ORD-LIST-006：Fake 以 T2、T1、T3 的順序回傳，結果為 T3、T2、T1
  - ORD-LIST-008：兩筆 `CreatedAtUtc` 相同，Fake 分別以 [X, Y] 與 [Y, X] 順序回傳，兩次結果相對順序相同且為 Id 升冪
  - ORD-LIST-007：兩筆訂單、所有買家都存在，其中一筆的 `EventId` 在 Fake event repository 查不到 → 丟 `InvalidOperationException`，訊息含該訂單 Id 與 EventId；失敗：回傳列表、或訊息含的是 BuyerId（買家缺失的例外）
- [x] 4.2 `OrderSummaryDto` 新增 `EventTitle`、`TicketCount`、`TotalAmount`、`CreatedAtUtc`；`GetOrdersHandler` 注入 `IEventRepository`，以 `GetByIdsAsync` 批次取名稱、查不到丟例外（design.md 決策 3），依決策 5 排序；讓 4.1 通過
- [x] 4.3 整合測試（`AdminOrdersControllerTests`）：真實 API 回應 JSON 含四個新欄位且值正確（ORD-LIST-005，驗證序列化欄位名稱）
- [x] 4.3a 整合測試（`OrganizerScopingDataCorruptionTests`，新增 `GetAdminOrders_WhenOneEventMissing_Returns500AndLogsIdsWithoutLeaking`）：資料為同一 Organizer 下兩個活動各一筆訂單，以 `_factory.MissingEventIds` 讓其中一個活動查不到（先確認 `MissingEventRepository` 會過濾 `GetByIdsAsync`）。成功：500 ProblemDetails，log 恰一筆，且含該訂單 Id 與 EventId。失敗：回應本文含 `eventTitle`、`buyerDisplayName`、任一訂單 Id、EventId 或例外訊息（ORD-LIST-007）
- [x] 4.4 `OrderDisplayQueryCountTests.GetAdminOrders_*` 改為「1 筆／1 買家／1 活動」對「3 筆／3 買家／2 活動」，兩邊每張表查詢次數相同，並加正向錨點：`Events` 的次數以暖機後實測值寫死（比照既有 `Members`／`Orders` 錨點；若授權管線本身也查 `Events`，錨點值會大於 1，須確認多出的那 1 次來自活動名稱查詢——移除該查詢後錨點應下降）（ORD-LIST-004）
- [x] 4.5 `docker compose exec api dotnet test` 全部通過（含 ORD-LIST-001～003 既有測試）

## 5. Web：訂單列表頁

- [x] 5.0 既有前端 AC 測試在本 change 後仍須以原斷言通過。`AdminOrderListPage.test.ts` 的 mock 資料要補上新欄位（`eventTitle`、`ticketCount`、`totalAmount`、`createdAtUtc`），斷言不得放寬：
  - AWU-ORDER-LIST-001「訂單狀態以中文標籤顯示，Pending／Paid 分別為警示色／成功色」：資料為 Pending（Buyer 1）、Paid（Buyer 2）兩筆。成功：表格恰 2 列，標籤分別為「待付款」`el-tag--warning` 與「已付款」`el-tag--success`，各列含買家名稱。失敗：頁面文字出現 `buyer-guid-1`／`buyer-guid-2`（顯示了 GUID）。
  - AWU-ORDER-LIST-001「買家顯示名稱含 HTML 標籤時原樣以文字顯示，表格內不產生元素」：資料為買家名稱 `<img src=x onerror=alert(1)>`。成功：頁面文字含該字串原文。失敗：表格內存在 `img` 元素。本 change 對 `eventTitle` 加上同樣的斷言（活動名稱由主辦方輸入，同樣是使用者輸入）。
  - AWU-ORDER-DETAIL-001（`AdminOrderDetailPage.test.ts`「混合座位項目與計數項目…」）：資料為一筆座位項目（seat-1、NT$1,500）與一筆計數項目（數量 5、NT$800）。成功：恰 2 列，含座位 Id、「計數票」、數量 5 與貨幣格式單價。失敗：列數不是 2，或缺少上述任一文字。本 change 不改詳情頁，此測試檔不應有 diff。
  - AWU-ORDER-HOLD-001 詳情頁（`AdminOrderDetailPage.test.ts` 的 Pending、Paid 兩則）：成功：Pending 顯示「持有到期時間：<時間>」。失敗：Paid 頁面文字含「持有到期時間」。
- [x] 5.1 先寫測試（`AdminOrderListPage.test.ts`）。每列以訂單短碼定位，不假設列索引對應的內容；每個欄位以表頭文字（`.el-table__header th`）找出欄索引，再取該列對應的 cell 比對，不以「整列文字含某字串」斷言（短碼與時間字串可能含相同數字而假通過）；先斷言列數與表頭存在，避免空表格或 selector 失效造成假通過：
  - AWU-ORDER-LIST-002：資料為 Id `3f2a9c1e-0000-4000-8000-000000000001`、活動「春季音樂會」、張數 2、總金額 2500 的訂單。成功：該列「訂單編號」cell 恰為「3f2a9c1e」、「活動」cell 恰為「春季音樂會」、「張數」cell 恰為「2」、「總金額」cell 恰為「NT$2,500」、「建立時間」cell 恰為 `new Date(createdAtUtc).toLocaleString()`；「查看明細」RouterLink 的 `to` 為 `/admin/orders/3f2a9c1e-0000-4000-8000-000000000001`。失敗：以完整 Id 當編號顯示、「張數」欄不存在或不是「2」（例如顯示項目數或空白）、金額缺 NT$ 或千分位，或連結使用短碼。
  - AWU-ORDER-HOLD-001 列表（改寫既有測試）：資料為 Pending、Paid、Cancelled、Expired 各一筆，`heldUntilUtc` 互不相同。成功：以表頭「持有到期時間」定位欄索引，Pending 列該 cell 為該時間，其餘三列該 cell 文字恰為「—」。失敗：非 Pending 列出現自己的 `heldUntilUtc` 時間字串，或該欄空白。
  - AWU-ORDER-LIST-003：API 依序回傳 A（`createdAtUtc` = T2）、B（T3）、C（T1），T1 < T2 < T3，三者短碼互不相同。成功：畫面恰 3 列，依短碼比對為 A、B、C。失敗：順序為 B、A、C（前端依建立時間降冪重排），或 C、A、B（升冪重排）。
- [x] 5.2 `OrderSummary` 型別新增 `eventTitle`、`ticketCount`、`totalAmount`、`createdAtUtc`
- [x] 5.3 `AdminOrderListPage.vue` 新增欄位（順序：訂單編號、活動、買家、張數、總金額、狀態、建立時間、持有到期時間、操作），金額用 `formatCurrency`，非 Pending 顯示「—」；讓 5.1 通過
- [x] 5.4 `docker compose exec web npm run test` 全部通過

## 6. 驗證與審查

- [x] 6.1 瀏覽器實測（claude-in-chrome）：以有 Pending、Paid、Expired 訂單的主辦方開啟 `/admin/orders`，核對各欄與 DB 資料一致（時間欄以瀏覽器 `Intl.DateTimeFormat().resolvedOptions().timeZone` 換算 DB 的 UTC 值後比對，不假設時區）、排序新到舊、非 Pending 顯示「—」；390px 寬時表格可水平捲動、頁面本身不溢出
- [x] 6.2 套用 hardener 檢查清單（`GetOrdersHandler`、`CreateOrderHandler`），並確認：
  - Organizer 過濾仍在資料庫端：`GetByOrganizerIdAsync` 的 `Where` 未被移到記憶體，新增的活動查詢只以已過濾訂單的 `EventId` 批次查詢；4.0 的 `GetByOrganizerIdAsync_DoesNotMaterializeOtherOrganizerOrders` 與 `GetOrders_ReturnsOnlyCallerOrganizerOrders` 仍以原斷言通過
  - `git diff master -- web/src` 中沒有 `v-html`、`innerHTML`；活動名稱與買家名稱只以 `{{ }}` 插值渲染
- [x] 6.3 strict-reviewer＋adversarial 審查直到 PASS
