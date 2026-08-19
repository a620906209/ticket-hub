## 1. 後端：Event 稽核欄位（Domain／Infra／Migration）

- [x] 1.1 `Event.cs` 新增兩個唯讀屬性 `CreatedByMemberId`（`Guid?`）、`CreatedAtUtc`（`DateTime?`），建構子新增對應的兩個**選填**尾端參數（預設 `null`，比照既有 `description`／`posterUrl`／`maxTicketsPerOrder` 的作法，不強迫既有測試裡所有 `new Event(...)` 呼叫都要跟著改）；不加額外驗證規則（design.md 決策 4：兩個欄位獨立 nullable，不做交叉驗證）
- [x] 1.2 `EventConfiguration.cs` 新增 `CreatedByMemberId`／`CreatedAtUtc` 屬性設定（皆不加 `IsRequired()`，維持 nullable）；比照既有 `Order.BuyerId` 的寫法（`OrderConfiguration.cs`），用 `HasOne<Member>().WithMany().HasForeignKey(e => e.CreatedByMemberId).OnDelete(DeleteBehavior.Restrict)` 建立外鍵約束，需要 `using ProjectC.Domain.Members;`
- [x] 1.3 產生 EF Core migration（`docker compose exec api dotnet ef migrations add AddEventAuditFields --project src/ProjectC.Infrastructure --startup-project src/ProjectC.WebApi`），確認產生的 migration 只新增兩個 nullable 欄位與一個 FK、不對既有資料列填值、不帶任何 `DEFAULT` 值；套用到本機開發資料庫確認成功時**同樣要透過容器執行**（`docker compose exec api dotnet ef database update --project src/ProjectC.Infrastructure --startup-project src/ProjectC.WebApi`，本機沒裝 dotnet SDK，禁止直接下 `dotnet ef database update`，見 CLAUDE.md 執行環境強制規則）；套用後檢查產生的欄位型別跟既有其他 `DateTime`／`DateTime?` 欄位一致（實際查證：本專案既有的 `Event.StartAtUtc` 用的是 `timestamp with time zone`，不是 `timestamp without time zone`——這是 Npgsql 對 `DateTime` 的既有慣例，`CreatedAtUtc` 應該產生同樣的型別，不要跟既有欄位不一致）、既有活動資料列的這兩個新欄位確實是 `NULL`（不是被填了 migration 套用當下的時間）

## 2. 後端：建立活動時記錄建立者與建立時間

- [x] 2.1 `CreateEventHandler` 新增建構子注入 `IDateTimeProvider`；`HandleAsync` 簽章新增 `Guid createdByMemberId` 作為**獨立的第一個參數**（`HandleAsync(Guid createdByMemberId, CreateEventRequest request, CancellationToken cancellationToken)`，design.md 決策 2：不塞進 `CreateEventRequest`，避免前端可以偽造建立者），建構 `Event` 時傳入 `createdByMemberId` 與 `_dateTimeProvider.UtcNow`
- [x] 2.2 `AdminEventsController.CreateEvent` 呼叫 `User.GetMemberId()`（既有的 `ClaimsPrincipalExtensions`，比照 `MembersController` 的既有用法）取得目前登入的 Admin Id，傳入 `_createEventHandler.HandleAsync(User.GetMemberId(), request, cancellationToken)`
- [x] 2.3 補測試：`CreateEventHandlerTests` 新增案例驗證建立成功後 `Event.CreatedByMemberId` 等於傳入的 `createdByMemberId`、`CreatedAtUtc` 等於 `IDateTimeProvider` 回傳的當下時間（用既有的 Fake `IDateTimeProvider` 測試替身固定時間點），對應 spec `透過管理 API 建立活動與票種`（MODIFIED）的「建立活動成功記錄建立者與建立時間」Scenario；`AdminEventsControllerTests` 新增一個整合測試：Admin 建立活動後，透過**第 4 節新增的** `GET /api/admin/events` 查詢，確認回傳的該筆活動 `createdByMemberId` 等於這個 Admin 帳號的 Id（因為 `POST /api/admin/events` 的成功回應只有 `{ id }`，要驗證稽核欄位得回頭查列表；**不要**查詢公開的 `GET /api/events`，那個端點依 design.md 決策 8 不會有這個欄位）

## 3. 後端：`IEventSeatRepository` 新增跨活動批次查詢

- [x] 3.1 `IEventSeatRepository` 新增 `Task<IReadOnlyList<EventSeat>> GetByEventIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken)`（參數型別是 `IReadOnlyList<Guid>`，比照既有 `GetForUpdateAsync` 的既有慣例，不要寫成 `IEnumerable<Guid>`），XML 註解需包含三點：① 用途（供活動列表的售票狀況統計使用，唯讀不鎖定，跟 `GetForUpdateAsync` 是不同用途）② **MUST 一併載入計算 `GetStatus` 所需的完整持久化欄位**（`_heldByOrderId`／`_heldUntilUtc`／`_soldByOrderId` 這幾個私有欄位對應的資料庫欄位都要有），不可用只包含公開欄位的投影查詢（比照既有 `GetByIdAsync` 的 XML 註解約定）③ `eventIds` 為空清單時 MUST 直接回傳空清單，不得執行任何資料庫查詢。`EventSeatRepository`（EF Core 實作）用 `Where(es => eventIds.Contains(es.EventId))` 查詢，並在查詢前加上 `if (eventIds.Count == 0) return [];` 這個提早回傳
- [x] 3.2 更新 `tests/ProjectC.Application.Tests/TestSupport/FakeEventSeatRepository.cs`，補上這個新方法的 in-memory 實作

## 4. 後端：新增 Admin 專用的活動列表查詢端點（不擴充公開的 `GetEventsHandler`／`EventDto`）

> design.md 決策 8：查證後發現既有 `GetEventsHandler`／`EventDto` 是公開、沒有 `[Authorize]` 的 `GET /api/events` 在用，買家端活動列表也共用這個端點。建立者身份／建立時間／即時售票統計不能加進這個公開端點，否則會外洩給未登入的任何人；本節改成新增一個完全獨立的 Admin 專用端點與 Handler。

- [x] 4.1 新增 `Application/Events/GetAdminEvents/AdminEventSummaryDto.cs`：`record AdminEventSummaryDto(Guid Id, string Title, DateTime StartAtUtc, Guid VenueId, Guid SeatMapId, string? Description, string? PosterUrl, int? MaxTicketsPerOrder, Guid? CreatedByMemberId, string? CreatedByDisplayName, DateTime? CreatedAtUtc, int AvailableSeatCount, int HeldSeatCount, int SoldSeatCount)`——前八個欄位對齊既有 `EventDto` 的內容（活動列表本來就要顯示的基本資訊），後六個是本次新增的稽核／統計欄位
- [x] 4.2 新增 `Application/Events/GetAdminEvents/GetAdminEventsHandler.cs`：建構子注入 `IEventRepository`、`IApplicationDbContext`（比照 `GetMyProfileHandler` 直接查 `Members` 的既有作法，design.md 決策 1）、`IEventSeatRepository`、`IDateTimeProvider`。`HandleAsync(CancellationToken)` 邏輯：① 查出所有活動 ② 收集所有活動的 `CreatedByMemberId`（排除 null）批次查詢 `Members` 表，組出 `MemberId → DisplayName` 字典 ③ 呼叫 `IEventSeatRepository.GetByEventIdsAsync` 一次查出所有活動的座位，依 `EventId` 分組、組內再依 `GetStatus(now)`（`now` 來自 `IDateTimeProvider`）分類計數 ④ 組出 `AdminEventSummaryDto` 清單——`CreatedByDisplayName` 查無對應 Member 或 `CreatedByMemberId` 為 null 時為 `null`；某活動沒有任何座位時三個計數皆為 0（不拋例外、不跳過該筆活動）
- [x] 4.3 `AdminEventsController` 新增 `[HttpGet]` `GetEvents` action（沿用 Controller 既有的 `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`），呼叫 `GetAdminEventsHandler`，回傳 `Ok(events)`；`Program.cs` 註冊 `builder.Services.AddScoped<GetAdminEventsHandler>()`（比照既有每個 Handler 都要手動註冊的慣例，遺漏會導致 DI 解析失敗）
- [x] 4.4 補測試：`GetAdminEventsHandlerTests`（目前不存在，本次新增）：
  - 活動有 `CreatedByMemberId` 且對應會員存在時，`CreatedByDisplayName` 正確回傳該會員的 `DisplayName`
  - 活動 `CreatedByMemberId` 為 null（比照舊資料情境）時，`CreatedByDisplayName` 為 null
  - 活動 `CreatedByMemberId` 有值但查無對應會員（資料異常，正常操作不會發生——本專案會員只能停用不能刪除，見 design.md 決策 1）時，`CreatedByDisplayName` 為 null，不拋例外
  - 活動有多個座位分屬 Available／Held／Sold 時，三個計數 MUST 正確對應各自數量
  - 活動有座位曾被 Hold 但 `HeldUntilUtc` 已早於查詢當下時間時，該座位 MUST 算入 `AvailableSeatCount`，不得算入 `HeldSeatCount`（對應 spec 「活動座位有已過期的持有中狀態」Scenario）
  - 活動沒有任何座位時，三個計數皆為 0
  - 多筆活動時，售票狀況統計 MUST 各自獨立、不互相污染（活動 A 的座位不會被算進活動 B 的統計）
  - 系統裡完全沒有任何活動時，`HandleAsync` 回傳空清單，不因為傳給 `GetByEventIdsAsync` 一個空的 `eventIds` 而拋例外或卡住
  對應 spec `透過管理 API 查詢活動列表時取得建立者與售票狀況統計`（ADDED）的前三個 Scenario
- [x] 4.5 `AdminEventsControllerTests` 補上 `GetEvents` 端點的授權測試（401／403／200，比照既有 `CreateVenue`／`GetVenues` 那組寫法）
- [x] 4.6 **安全回歸測試**：在既有 `EventsController` 的測試（`tests/ProjectC.WebApi.Tests/Events/EventsControllerTests.cs`）新增一個測試：先用 Admin 建立至少兩筆活動（確保回傳陣列不是只有一筆，避免測試剛好只檢查到「湊巧」沒洩漏的那一筆），改用**匿名（無 Token）**的 `HttpClient` 呼叫既有公開的 `GET /api/events`，把回應內容反序列化成 `JsonDocument`（而不是強型別的 `EventDto`——強型別反序列化即使 JSON 裡多了欄位也不會讓測試失敗，測不出「有沒有洩漏」）。**遍歷回傳陣列裡的每一筆活動**（不是只檢查第一筆），對每一筆都斷言其 JSON 物件裡**不存在**以下屬性名稱（用實際 JSON 序列化後的 camelCase 名稱檢查，不是 C# 的 PascalCase）：`createdByMemberId`、`createdByDisplayName`、`createdAtUtc`、`availableSeatCount`、`heldSeatCount`、`soldSeatCount`。對應 spec 「既有公開活動列表查詢端點不回傳 Admin 專用欄位」Scenario，也是 design.md Risk 提到「決策 8 需要有測試守著」的具體落實

## 5. 後端：Infrastructure 層 FK 約束測試

- [x] 5.1 `ForeignKeyConstraintsTests.cs` 新增 `InsertEvent_WithNonExistentCreatedByMemberId_ViolatesForeignKey`，比照既有 `InsertOrder_WithNonExistentBuyerId_ViolatesForeignKey` 的寫法（用 raw SQL insert 一筆 `CreatedByMemberId` 指向不存在會員的 `Events` 資料列，斷言拋出例外）——這個檔案裡的 raw SQL **僅限**這種 Infrastructure 層的 FK 約束整合測試使用，目的是繞過 EF Core change tracking／驗證邏輯、直接測試資料庫層級的 FK 約束本身是否存在，不是本專案一般禁止 raw SQL 規則的例外；正式的 application code（Handler／Repository）一律不寫 raw SQL，這點不變

## 6. 前端：型別與 API service 層

- [x] 6.1 重新產生 `web/src/types/api.generated.ts`（`CreateEventRequest` 的 schema 沒變，本次後端請求格式不變；新的 `GET /api/admin/events` 端點依既有慣例不會有 schema，見 ticketing-web-ui design.md 決策 4）
- [x] 6.2 `web/src/types/apiResponses.ts` **新增**（不是修改）`AdminEventSummary` 型別：`id`／`title`／`startAtUtc`／`venueId`／`seatMapId`／`description`／`posterUrl`／`maxTicketsPerOrder`（跟既有 `EventSummary` 一樣的八個欄位）再加上 `createdByMemberId: string | null`、`createdByDisplayName: string | null`、`createdAtUtc: string | null`、`availableSeatCount: number`、`heldSeatCount: number`、`soldSeatCount: number`。**既有的 `EventSummary` 型別不變**（design.md 決策 8：對應公開端點，不含這六個欄位）
- [x] 6.3 `web/src/api/admin.ts` 新增 `getAdminEvents(): Promise<AdminEventSummary[]>`（`GET /admin/events`，用既有的 `authorizedRequest`）；`web/src/api/events.ts` 的既有 `getEvents()` 不動
- [x] 6.4 新增 `web/src/utils/currency.ts`：`formatCurrency(amount: number): string`，回傳 `'NT$' + amount.toLocaleString('en-US', { maximumFractionDigits: 2 })`（千分位、依實際小數位數顯示、最多兩位，不固定補零；不處理負數／NaN／Infinity，見 design.md 決策 7 的格式定義），供 Admin 建立票種表單與買家端票價顯示共用

## 7. 前端：建立活動改成獨立頁面

- [x] 7.1 `web/src/router/index.ts` 新增 `{ path: 'events/new', name: 'admin-event-create', component: AdminEventCreatePage }`（路由 `name` 比照既有 `admin-<resource>` 命名慣例；`AdminEventCreatePage` 是 import 時的別名，對應到檔案名稱是 `EventCreatePage.vue`，比照既有 `import AdminEventListPage from '../pages/admin/EventListPage.vue'` 的既有寫法——檔名不帶 `Admin` 前綴，只有 import 進 router 時才加，維持既有慣例一致）
- [x] 7.2 新增 `web/src/pages/admin/EventCreatePage.vue`：把 `EventListPage.vue` 現有的「建立活動」表單（含場館/座位圖連動下拉選單、活動說明/海報網址/限購張數欄位與驗證規則）整段搬過來，邏輯與驗證規則不變（design.md 決策 5：純搬遷、不改欄位或規則）；建立成功後 `router.push({ name: 'admin-events' })` 導回列表頁
- [x] 7.3 `EventListPage.vue` 移除「建立活動」表單區塊與其對應的 script 邏輯（`eventForm`／`handleCreateEvent`／`handleVenueChange`／`seatMapOptions` 等只給建立活動表單用的狀態，若「建立票種」表單需要用到場館/座位圖查詢以外的部分保留），新增「建立活動」按鈕（`router-link :to="{ name: 'admin-event-create' }"`）
- [x] 7.4 確認活動列表頁在從建立活動頁導回後，會重新呼叫 `getAdminEvents()`（第 8 節改用的新端點）顯示新活動（既有的 `onMounted(loadEvents)` 在路由切回時需要重新觸發——若元件被 keep-alive 快取住不會重新 mount，需改成在 `EventCreatePage.vue` 建立成功、`router.push` 之前或之後由 `EventListPage.vue` 端用路由層級的重新整理機制確保會重新查詢；本專案目前沒有對 Admin 頁面套用 `keep-alive`，預期路由切換會重新 mount，仍需實測確認）
- [x] 7.5 **搬移既有測試**：`web/src/pages/admin/EventListPage.test.ts`（上一輪 `admin-venue-seatmap-query` change 新增，6 個測試，涵蓋場館/座位圖下拉連動、過期回應防護、失敗顯示錯誤、建立成功後表單 reset）測的是即將搬到 `EventCreatePage.vue` 的邏輯——把整個檔案改名搬到 `web/src/pages/admin/EventCreatePage.test.ts`，測試內容不變，只改掛載的元件從 `EventListPage` 換成 `EventCreatePage`（若 `EventCreatePage.vue` 建立成功後改成 `router.push` 導頁而非原本的 `loadEvents()`，「建立成功後表單 reset」那個測試案例需要相應調整斷言方式，例如改成斷言 `router.push` 被呼叫，而不是斷言表單欄位清空——導頁後元件就卸載了，表單狀態清不清空不再是使用者看得到的行為）；不得讓這 6 個測試留在 `EventListPage.test.ts` 裡對著已經不存在的表單元素跑（會直接測試失敗或變成測試死代碼）

## 8. 前端：活動列表新增建立者/建立時間/售票狀況橫條圖

- [x] 8.1 `EventListPage.vue` 改呼叫 `getAdminEvents()`（取代既有的 `getEvents()`）；`events` 狀態的型別改成 `AdminEventSummary[]`。活動列表的 `el-table` 新增「建立者」「建立時間」兩欄：「建立者」顯示 `createdByDisplayName`（不顯示 `createdByMemberId` 原始值，design.md 決策 1）；「建立時間」比照既有「開始時間」欄的既有寫法 `new Date(row.startAtUtc).toLocaleString()`，用 `new Date(row.createdAtUtc).toLocaleString()` 轉成瀏覽器當地時區顯示（design.md 決策 1）。兩欄為 `null` 時都顯示「—」，不得顯示空白或誤導性預設值（design.md 決策 4／Risk）——`createdAtUtc` 為 `null` 時要先判斷 `null` 再顯示「—」，不能直接把 `null` 丟給 `new Date(null)`（那會變成 `1970/1/1`，是一個會誤導人的假日期）
- [x] 8.2 新增售票狀況橫條圖：一個 table column，內容是三個並排 `<div>`（`flex: v-bind(availableCount)` 等寫法或等效實作），依 `availableSeatCount`／`heldSeatCount`／`soldSeatCount` 佔總座位數比例決定寬度，套用 `morandi.css` 既有色系的三種顏色區分；總座位數為 0 時顯示「尚無座位資料」，不渲染橫條（design.md 決策 6）
- [x] 8.3 補前端單元測試（Vitest）：`createdByDisplayName`／`createdAtUtc` 為 null 時顯示「—」；售票狀況橫條圖依三個數字的比例正確計算寬度／比例（可用簡化的樣式或 data 屬性斷言，不需要真的量測渲染後的像素寬度）；總座位數為 0 時顯示「尚無座位資料」而非除以零造成的錯誤

## 9. 前端：貨幣單位顯示

- [x] 9.1 `EventListPage.vue`（或搬遷後仍在此頁的「建立票種」表單）的票價輸入欄位旁標示「NT$」（例如 `el-form-item` 的 `label` 或欄位旁的 `<span>`，不需要即時把使用者輸入的數字格式化成千分位字串，那是顯示既有價格時才用 `formatCurrency`）
- [x] 9.2 買家端 `web/src/pages/buyer/EventDetailPage.vue` 的「票價」欄位與「已選座位總金額」改用 `formatCurrency()` 顯示
- [x] 9.3 新增 `web/src/utils/currency.test.ts` 單元測試，涵蓋 design.md 決策 7 的格式定義：`formatCurrency(1000)` → `"NT$1,000"`（整數不補零）、`formatCurrency(1000.5)` → `"NT$1,000.5"`（一位小數不補成兩位）、`formatCurrency(1000.55)` → `"NT$1,000.55"`（兩位小數）、`formatCurrency(0)` → `"NT$0"`、`formatCurrency(1000000)` → `"NT$1,000,000"`（千分位跨多組）

## 10. 收尾

- [x] 10.1 `npm run lint`／`vue-tsc --noEmit`／`npm run test`／`npm run build` 皆通過
- [x] 10.2 後端 `dotnet test` 全數通過（含本次新增測試）
- [x] 10.3 用 claude-in-chrome 實際於瀏覽器驗證：開啟活動管理頁，列表顯示既有活動的建立者/建立時間（舊活動應顯示「—」）與售票狀況橫條圖 → 點「建立活動」導向獨立頁面、填寫送出後成功導回列表頁且新活動出現、建立者正確顯示為目前登入的 Admin、建立時間是剛剛的時間點、新活動的售票狀況橫條圖全部是 Available（因為還沒有人下單）→ 建立票種表單的價格欄位有 NT$ 標示 → 切換到買家端開啟該活動詳情頁，確認票價與已選座位總金額都有 NT$ 標示
- [x] 10.4 同步確認 `event-management`／`admin-web-ui` 兩份主 spec 的既有 Requirement 已依 delta 正確更新（歸檔時同步）
