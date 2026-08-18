## Context

`Event` Domain Entity（`src/ProjectC.Domain/Events/Event.cs`）目前完全沒有稽核欄位（建立者／建立時間），全專案 Domain 層也沒有任何既有的 `CreatedAt`／`CreatedBy` 慣例可以直接沿用（查證後確認：`Member`、`Order` 都沒有時間戳；`Order.BuyerId` 是最接近的先例——單純一個 `Guid` 純量欄位，沒有 navigation，FK 用 `HasOne<Member>().WithMany().HasForeignKey(...)` 建立，`OrderConfiguration.cs:27-31`）。

Admin 端目前呼叫端身份（「誰在操作」）只有買家端／會員端在用：`ClaimsPrincipalExtensions.GetMemberId()`（`src/ProjectC.WebApi/Common/ClaimsPrincipalExtensions.cs:8-12`）從 JWT `sub` claim 取出目前登入者的 `Guid`，`MembersController` 呼叫 `User.GetMemberId()` 後把這個 `Guid` 當作 Handler 的**獨立參數**傳入（不是塞進 Request DTO），例如 `_getMyProfileHandler.HandleAsync(User.GetMemberId(), cancellationToken)`。`AdminEventsController`／`AdminMembersController` 目前都不需要「誰在操作」這個資訊，所以沒有用到這個模式。

售票狀態（`EventSeatStatus`：Available／Held／Sold）是 `EventSeat.GetStatus(DateTime now)` 依私有欄位（`_soldByOrderId`／`_heldByOrderId`／`_heldUntilUtc`）即時算出來的，不是資料庫裡的一個可直接 `GROUP BY` 的欄位；`IEventSeatRepository` 目前只有 `GetByEventIdAsync(單一 eventId)`，沒有跨多個活動一次查詢座位的方法。`GetEventsHandler` 目前只注入 `IEventRepository`，一次回傳所有活動、不分頁。

**重要**：`GetEventsHandler`／`EventDto` 是 `EventsController`（`api/events`，`GET /api/events`）在用的，這個端點**完全公開、沒有 `[Authorize]`**（`EventsController.cs` 類別上沒有任何授權標註）。查證前端後確認：買家端 `web/src/pages/buyer/EventListPage.vue` 與 Admin 端 `web/src/pages/admin/EventListPage.vue` 兩邊**都**呼叫同一個 `web/src/api/events.ts` 的 `getEvents()`，也就是同一個公開端點、同一個 `GetEventsHandler`。這跟訂單查詢的既有架構不一樣——`GetOrdersHandler` 從一開始就是只給 `AdminOrdersController`（`[Authorize(Policy = AdminOnly)]`）用的獨立 Handler，沒有跟任何買家端點共用。這個既有的「Admin 活動列表沿用公開端點」是 `ticketing-web-ui` 上線時圖方便的做法，這次規劃時一開始沒注意到，直接規劃成擴充 `GetEventsHandler`／`EventDto`——這樣做會讓建立者身份、建立時間、售票即時統計全部透過公開、免登入的端點外洩給任何人，是這次規劃階段查證後發現、必須修正的問題，見決策 8。

Admin 前端目前顯示外部參照一律用原始 GUID、不解析成人類看得懂的名稱（`AdminOrderListPage.vue:38` 顯示「買家 Id」、`EventListPage.vue` 顯示「場館 Id」「座位圖 Id」，且列表頁註明「無法顯示名稱」）——這是本次要刻意偏離的既有慣例之一，理由見決策 1。

## Goals / Non-Goals

**Goals:**
- `Event` 新增建立者（`CreatedByMemberId`）與建立時間（`CreatedAtUtc`）稽核欄位，`CreateEventHandler` 建立時記錄
- Admin 活動列表顯示建立者（human-readable 顯示名稱）、建立時間、售票狀況橫條圖（Available／Held／Sold 依比例、顏色區分）
- 「建立活動」表單改成獨立頁面／路由
- 票價相關欄位（Admin 建立票種表單、買家端票價顯示）補上「NT$」貨幣單位標示

**Non-Goals:**
- 不 backfill 既有活動的建立者/建立時間——這兩個欄位對舊資料是不可考的資訊，寧可顯示為空，不假造一個看起來合理但其實是編的值
- 不做售票狀況的即時（WebSocket/SignalR）更新——沿用既有「頁面載入/手動刷新當下的查詢結果」模式，跟活動列表、訂單列表既有的作法一致
- 不改變「建立票種」表單本身（維持在活動列表頁、分區代碼手動輸入）——這部分留給下一個 change（票種依座位圖分區自動帶入）處理，本次只調整票價的貨幣單位顯示
- 不做活動列表分頁——公開端點的 `GetEventsHandler`、新增的 Admin 專用 `GetAdminEventsHandler` 都維持一次全部撈回，跟既有作法一致

## Decisions

### 決策 1：建立者顯示為人類看得懂的名稱，刻意偏離「Admin 列表一律顯示原始 GUID」的既有慣例

新增的 `AdminEventSummaryDto`（見決策 8，不是既有公開的 `EventDto`）比既有 `EventDto` 多三個稽核欄位（`CreatedByMemberId: Guid?`、`CreatedByDisplayName: string?`、`CreatedAtUtc: DateTime?`）與三個統計欄位（見決策 3）。`GetAdminEventsHandler` 額外注入 `IApplicationDbContext`，用事件清單裡出現過的 `CreatedByMemberId` 集合一次查詢 `Members` 表（`Where(m => memberIds.Contains(m.Id))`），建立 `MemberId → DisplayName` 的查找字典；`CreatedByMemberId` 本身是 null（本次功能上線前的舊活動）或查無對應會員（見下方「會員資料異常」說明）時，`CreatedByDisplayName` 為 null，前端顯示「—」。API 回傳的是原始 `CreatedByMemberId` 供除錯／未來擴充使用，Admin 活動列表的表格 UI 只顯示 `CreatedByDisplayName`，不會同時把 GUID 也放進表格欄位。

**「查無對應會員」是什麼情境**：查證後確認本專案的會員管理**沒有硬刪除**——`AdminMembersController`／`ActivateMemberHandler`／`DeactivateMemberHandler` 只有「停用」（`IsActive = false`），沒有任何刪除會員資料的端點。搭配決策 4 的 `OnDelete(DeleteBehavior.Restrict)` FK 約束，只要一個會員被至少一筆 `Event.CreatedByMemberId` 參照，資料庫層面就不可能刪除該會員列——因此「會員被刪除、查無對應會員」在正常操作下不會發生。會員被**停用**（`IsActive = false`）不影響這裡的查詢：`DisplayName` 一樣查得到、一樣顯示，稽核紀錄不因為帳號被停用而消失，這也符合稽核的直覺（本來就該看得到「當時是哪個帳號做的」，即使那個帳號現在已經停用）。「查無對應會員」只會是資料異常情境（例如直接操作資料庫、測試資料留下的孤兒 Id），design.md 與 spec 統一用「查無對應會員（資料異常，正常操作不會發生）」描述，不再用「帳號被停用/刪除」這種暗示會發生在正常流程的措辭。

**建立時間的顯示格式**：後端一律以 UTC 傳輸（`CreatedAtUtc` 序列化成 ISO 8601 字串，`System.Text.Json` 對 `DateTime` 的預設行為）；前端顯示時比照既有慣例——`AdminOrderListPage.vue`／`EventListPage.vue`（既有的「開始時間」欄）都是用 `new Date(xUtc).toLocaleString()` 轉成瀏覽器當地時區顯示，不額外處理時區或自訂格式，`createdAtUtc` 用同一種寫法，不新增格式化工具函式。

**顯示的是查詢當下的名稱，不是建立當下的快照**：`CreatedByDisplayName` 每次都是查詢當下從 `Members.DisplayName` 即時查出來的，不是活動建立當下記錄下來、之後不會變的快照。這代表如果一個 Admin 帳號事後改名，所有這個帳號建立過的活動，列表上顯示的建立者名稱都會一起變成新名字，不會保留「建立當下他叫什麼名字」這個歷史資訊。這是刻意的簡化決定：只存 `CreatedByMemberId`（一個穩定的外鍵），顯示名稱永遠是查詢當下的最新值，不額外存一個 `CreatedByDisplayNameSnapshot` 欄位——如果之後稽核需求變成「必須看得到建立當下的名稱、即使後來改名也不變」，才需要新增這個快照欄位，屬於獨立的 schema 擴充，本次不預先加入（YAGNI）。

**理由**：查證後確認本專案 Admin 列表目前的既有慣例是顯示原始 GUID、不解析名稱（`AdminOrderListPage.vue` 的「買家 Id」、`EventListPage.vue` 的「場館 Id」／「座位圖 Id」都是如此，且後者的 spec 明文寫「顯示原始 Id，不查詢對應名稱」）。但這次的需求本質是「稽核紀錄」——目的就是要讓人事後看得出「這是誰做的」，一個 GUID 對這個目的幾乎沒有用（沒人記得住/認得出一串 GUID 對應哪個 Admin 帳號），解析成顯示名稱才真正達成使用者要的效果。這是刻意的、有明確理由的偏離，不是隨意打破既有風格（見 CLAUDE.md「不強迫遵循有害慣例，但要說明理由，不能默默各行其是」）——其餘既有的原始 GUID 顯示（場館/座位圖/買家 Id）維持不變，不擴大範圍去一併「修正」它們。

**考慮過的替代方案**：只顯示 `CreatedByMemberId` 原始值，維持既有慣例一致性——但這樣等於做了「記錄」卻做不到「看得出是誰」，不符合這次需求的實際目的，故不採用。

### 決策 2：建立者身份透過 `User.GetMemberId()` 當獨立參數傳入 Handler，不塞進 `CreateEventRequest`

`AdminEventsController.CreateEvent` 呼叫 `User.GetMemberId()`，把取得的 `Guid` 當作 `CreateEventHandler.HandleAsync` 的**新增獨立參數**（`HandleAsync(Guid createdByMemberId, CreateEventRequest request, CancellationToken cancellationToken)`），不放進 `CreateEventRequest` 這個 DTO 裡。

**理由**：完全比照既有 `MembersController` 呼叫 `GetMyProfileHandler`／`UpdateMyProfileHandler` 的既有模式（`User.GetMemberId()` 當獨立參數，不進 Request DTO）。這樣做的好處：建立者身份**不可能被前端偽造**——如果放進 `CreateEventRequest`，前端理論上可以在 request body 裡塞一個任意的 `createdByMemberId`；獨立參數的作法讓這個值只能來自後端自己解析的 JWT，這是安全性考量，也是本專案既有模式已經驗證過的作法。

### 決策 3：售票狀況統計在 `GetAdminEventsHandler` 一次算好，不讓前端逐一活動呼叫

`IEventSeatRepository` 新增 `GetByEventIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken)`（參數型別比照既有 `GetForUpdateAsync(IReadOnlyList<Guid> eventSeatIds, ...)` 的既有慣例，不用 `IEnumerable<Guid>`——呼叫端傳進來的一定是已經收集好的活動 Id 清單，不是需要延遲列舉的序列，用 `IReadOnlyList` 明確表達這點、也避免 EF Core 的 `Contains` 查詢意外對一個 lazy sequence 列舉兩次）。`eventIds` 為空清單時 MUST 直接回傳空清單，不執行任何資料庫查詢（不是把空集合交給 EF Core 翻譯成一個永遠為假的 `WHERE` 查詢再送去資料庫跑一趟）；一次查出多個活動底下的所有 `EventSeat`；`GetAdminEventsHandler`（見決策 8）額外注入 `IEventSeatRepository`、`IDateTimeProvider`（用來呼叫既有的 `EventSeat.GetStatus(now)`），查出的座位依 `EventId` 分組、每組再依 `GetStatus(now)` 分類計數，組成 `AdminEventSummaryDto` 新增的三個欄位：`AvailableSeatCount`、`HeldSeatCount`、`SoldSeatCount`。

**理由**：
- 對應前端實際使用情境：售票狀況橫條圖要在「活動列表」這個畫面就看得到，不需要另外點進每個活動才查得到；如果讓前端對列表裡的每個活動各自呼叫一次既有的 `GET /api/events/{id}/seats`，活動數一多就是 N 次額外的 HTTP 請求（N+1），且這個既有端點回傳的是完整座位清單（不是統計），前端還要自己再算一次分組計數，邏輯重複
- 一次撈多個活動的座位、在 Application 層分組計數，複雜度跟既有 `GetEventSeatsHandler`（撈單一活動的座位、逐筆組 DTO）同一個量級，沒有引入新的技術（沒有新的 SQL 聚合查詢，沿用既有 `GetStatus(now)` 的計算邏輯，只是多加一層 `GroupBy`）
- **考慮過的替代方案**：在資料庫層用 SQL 聚合（`GROUP BY`）直接算出各狀態數量，效能更好——但座位狀態是程式碼算出來的（`Held` 會依 `now` 動態變回 `Available`），沒辦法簡單翻譯成一個可以下 `GROUP BY` 的 SQL 欄位，除非額外維護一個背景工作把過期的 Held 狀態同步寫回資料庫（本次不做這種結構性變更，範圍過大，且目前活動/座位規模不構成真實效能問題）

### 決策 4：`Event` 新增的兩個稽核欄位是 nullable，不 backfill 舊資料

`CreatedByMemberId`（`Guid?`）、`CreatedAtUtc`（`DateTime?`）在 schema 上是 nullable；EF migration 不對既有資料列填入任何值（維持 `NULL`）。`CreateEventHandler` 建立**新**活動時一律會把這兩個值填滿（不會建立出兩者為 null 的新活動），null 只會出現在這次 migration 套用之前就存在的舊活動上。

**理由**：這兩個欄位代表的是「當初是誰、什麼時候建立的」，對舊資料這是真的不可考的事實，不是「預設值」的概念——如果為了讓欄位看起來像 NOT NULL 而填入一個假的建立時間（例如 migration 套用當下的時間）或假的建立者，會誤導日後查稽核紀錄的人，這比顯示空白更糟。前端看到 `createdByDisplayName`／`createdAtUtc` 為 null 時顯示「—」，如實反映「這筆資料沒有這項紀錄」。

**FK 約束**：`CreatedByMemberId` 比照 `Order.BuyerId` 的既有作法（`OrderConfiguration.cs:27-31`），用 `HasOne<Member>().WithMany().HasForeignKey(e => e.CreatedByMemberId).OnDelete(DeleteBehavior.Restrict)` 建立外鍵約束，只是允許 null。`Restrict` 代表只要有活動的 `CreatedByMemberId` 參照某個會員，資料庫就不允許刪除那筆會員——這與本專案會員管理「只能停用、沒有刪除」的既有事實一致（見決策 1），選 `Restrict` 而非 `Cascade`／`SetNull` 是刻意的：稽核紀錄不該因為任何操作（包含未來如果真的新增了刪除會員的功能）而憑空消失或被清空，寧可讓那個操作直接失敗。

### 決策 5：「建立活動」獨立頁面採用既有 Admin 路由命名慣例，沿用既有欄位與驗證規則

新路由 `path: 'events/new', name: 'admin-event-create'`，比照既有 `admin-<resource>` 命名慣例（`admin-venues`／`admin-events`／`admin-orders`）；表單欄位、驗證規則（`CreateEventRequestValidator`）完全不變，純粹是把現有的表單從 `EventListPage.vue` 內嵌區塊搬到獨立元件/頁面。列表頁新增「建立活動」按鈕（`router-link` 導向新路由），建立成功後導回列表頁（`admin-events`）。

**理由**：使用者反映的是「表單擠在列表頁下面、細節顯得少」，是版面/呈現問題，不是欄位或規則不夠——搬到獨立頁面本身就解決版面擁擠的問題，不需要也不應該趁機新增欄位或改驗證規則（避免範圍蔓延，且新增欄位不是這次需求提到的項目）。這個獨立頁面也是下一個 change（票種依座位圖分區自動帶入）會直接疊上去改造的地方，這次先把頁面結構分好、行為不變，下次才動實際的票種建立流程。

### 決策 6：售票狀況橫條圖用純 CSS 疊色 `<div>`，不引入圖表函式庫

橫條圖用三個並排的 `<div>`，寬度依 `Available/Held/Sold` 佔總座位數的比例動態設定（`flex: <count>`），各自套用固定的莫蘭迪色系顏色（沿用既有 `web/src/styles/morandi.css` 的色調，不新增一套獨立配色）；總座位數為 0（活動還沒有座位，理論上不會發生，因為建立活動當下就會依座位圖產生 `EventSeat`，但仍防呆處理）時不渲染橫條，顯示「尚無座位資料」。

**理由**：使用者要求「簡單的就好」，三態比例橫條用 flexbox 疊色就能做到，不需要為了這麼簡單的視覺化引入 ECharts/Chart.js 這類圖表函式庫（增加 bundle size、多一個依賴要維護），這也是本專案目前前端唯一的視覺化需求，不構成「之後還會有更多圖表」的預期。

### 決策 7：新增共用的 `formatCurrency`，只調整顯示格式、不改變金額的資料型別或計算邏輯

Admin 建立票種表單的 `el-input-number`、買家端 `EventDetailPage.vue` 的票價欄位與總金額顯示，統一在顯示文字前綴/後綴加上「NT$」；由於全專案目前不存在任何金額格式化工具（查證後確認 `web/src/utils/` 沒有相關檔案），新增一個極簡的 `formatCurrency(amount: number): string` 放在 `web/src/utils/`，供這幾個顯示點共用，避免同樣的字串拼接邏輯散落多處。金額本身的儲存型別（後端 `decimal`）、驗證規則（`positiveNumberRule`，只允許正數）、計算邏輯（座位加總）都不變，`formatCurrency` 純粹是顯示層的格式轉換，不參與任何業務邏輯。

**格式定義**（避免「千分位、要不要固定兩位小數」這種細節留給實作階段自己猜）：`formatCurrency(amount)` 回傳 `'NT$' + amount.toLocaleString('en-US', { maximumFractionDigits: 2 })`——千分位逗號分隔，小數位數依實際值有幾位就顯示幾位（最多兩位，比照既有票價欄位 `:precision="2"` 的既有限制），沒有小數就不顯示 `.00`（例如 `formatCurrency(1000)` → `"NT$1,000"`，`formatCurrency(1000.5)` → `"NT$1,000.5"`，不是固定兩位小數的 `"NT$1,000.50"`）。不特別處理負數／`NaN`／`Infinity`——上游（`positiveNumberRule` 前端驗證、後端 `CreateTicketTypeRequestValidator` 票價需為正數）已經擋掉這些不合法的值，`formatCurrency` 只負責格式化一個已知合法的正數，不是一個要防禦任意輸入的通用函式，比照本專案其他小工具函式（例如 `toErrorMessage`）不過度防禦的既有風格。

**理由**：即使只有兩三個使用點，字串格式（千分位、字首字尾）如果散落重複寫，之後要改格式要多處修改；集中成一個函式成本很低，符合「重複邏輯出現才抽共用」的專案原則（這次一次動了 admin＋買家兩處，已經算重複）。

### 決策 8：新增獨立的 Admin 專用查詢端點，不擴充公開的 `GetEventsHandler`／`EventDto`

新增 `GET /api/admin/events`（`AdminEventsController` 新增的 `[HttpGet]` action，沿用該 Controller 既有的 `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`），由新的 `GetAdminEventsHandler`（`Application/Events/GetAdminEvents/`）處理，回傳新的 `AdminEventSummaryDto`（含建立者、建立時間、售票狀況統計）。既有公開的 `GET /api/events`、`GetEventsHandler`、`EventDto` **完全不動**，不新增任何欄位。前端 Admin 活動列表頁改呼叫 `web/src/api/admin.ts` 新增的 `getAdminEvents()`（打新端點），買家端活動列表頁維持呼叫 `web/src/api/events.ts` 的既有 `getEvents()`（打舊端點），兩邊各自獨立。

**理由**：查證後發現（見 Context）`GetEventsHandler`／`EventDto` 目前是 `EventsController`（`api/events`）在用、完全公開不需要登入的端點，買家端與 Admin 端的活動列表這輪之前剛好都重用同一個 `getEvents()`。如果照原本規劃直接在 `EventDto` 加建立者身份、建立時間、即時售票統計，這些資訊會透過這個公開端點外洩給任何未登入的呼叫端——建立者身份（可用來推測系統有哪些 Admin 帳號、對應顯示名稱）跟即時售票數字（商業敏感度：任何人都能即時看到某活動賣了多少張）都不該公開。這跟既有 `GetOrdersHandler` 的做法其實是一致的——`GetOrdersHandler` 從一開始就是只給 `AdminOrdersController` 用的獨立 Handler，沒有跟任何買家端點共用；本次是修正 `Event` 這邊「不小心共用」的既有狀況，讓它符合這個專案原本就有的「Admin 專用查詢邏輯獨立於公開查詢邏輯」的架構慣例。

**考慮過的替代方案**：
- 在既有 `GetEventsHandler` 內部依呼叫端角色（`ClaimsPrincipal.IsInRole("Admin")`）決定要不要多回傳幾個欄位——不採用，因為 Application 層的 Handler 不該知道「呼叫端是誰、角色是什麼」這種屬於 WebApi／Controller 層的授權概念，混在一起會讓同一個 Handler 身兼兩種不同信任等級的查詢邏輯，職責不單一，也違反本專案 Clean Architecture 的分層原則
- 前端維持呼叫既有公開端點取得基本欄位，另外再呼叫一個只回傳「建立者/建立時間/統計」這幾個補充欄位的端點、前端合併——不採用，多一次 HTTP 請求、前端要自己做合併對齊（用 `eventId` 對兩份資料），比直接一個 Admin 專用端點回傳完整資料複雜，沒有對應的好處

## Risks / Trade-offs

- **[Risk]** `GetAdminEventsHandler` 的售票狀況統計，每次呼叫都要撈出所有相關活動的完整座位列表再於記憶體中分組計數，活動數與座位數變大時會有效能疑慮 → **Mitigation**：目前資料規模小，比照既有「不做推測性效能優化」原則；若之後活動/座位規模明顯變大，可以考慮在 `EventSeat` 增加一個定期同步的「目前狀態」欄位改用資料庫聚合查詢，屬於獨立的效能優化 change，不在本次預先處理
- **[Risk]** 建立者顯示名稱解析（決策 1）刻意偏離既有的「顯示原始 GUID」慣例，日後若有人依照既有慣例新增其他需要顯示會員身份的欄位，可能不確定該用哪種作法 → **Mitigation**：design.md 已明確記錄這是「稽核紀錄」這個特定情境下的刻意選擇，不是新的一般性慣例；其他既有的原始 GUID 顯示（買家 Id、場館/座位圖 Id）本次不動
- **[Risk]** 舊活動的 `createdByDisplayName`／`createdAtUtc` 為 null，列表上會有一部分資料「有稽核紀錄、一部分沒有」的不一致外觀 → **Mitigation**：這是如實反映資料現況，比造假數字更誠實；UI 上明確顯示「—」而不是空白或誤導性的 0/預設日期，讓看的人清楚知道這是「沒有紀錄」而不是「紀錄是這個值」
- **[Risk]** `GetAdminEventsHandler` 依序查詢活動、會員、座位三次，中間沒有包在同一個資料庫交易內；三次查詢之間，座位狀態理論上可能被其他並發的下單/取消交易改變，`GetStatus(now)` 也是用 Handler 開始執行時的 `now` 統一計算，不是每個座位各自當下的絕對即時狀態 → **Mitigation**：這個統計本來就是給活動列表在頁面載入當下呈現一個近似快照，不是要做強一致性的對帳報表，跟活動列表其餘欄位（場館/座位圖 Id 等）一樣是「查詢當下」的結果、不是即時推播（design.md Non-Goals 已經聲明過這點）；不需要額外用交易包起來
- **[Risk]** 舊活動（本次功能上線前建立）雖然沒有稽核欄位，但因為當初建立活動時就會依座位圖產生對應的 `EventSeat`，售票狀況統計本身不受這次 migration 影響、能正常計算；若未來資料出現異常（例如活動沒有對應 `EventSeat`、或 `EventSeat` 資料本身損毀），`GetAdminEventsHandler` 只會把統計算成查到的結果（可能是 0 或不完整的數字），不會主動偵測或修復這類資料異常 → **Mitigation**：這是 Admin 列表用途下可接受的簡化，這個統計功能的目的是給日常維運快速看一眼售票狀況，不是資料完整性檢查工具，資料異常的偵測與修復屬於另一個問題，不在本次範圍
- **[Risk]** 決策 8 修正了「Admin 專用資料透過公開端點外洩」這個問題，但如果實作時漏改前端呼叫點（例如 Admin 列表頁忘記把 `getEvents()` 換成 `getAdminEvents()`），或日後有人不知情又把類似欄位加回公開的 `EventDto`，同樣的外洩問題會再次發生 → **Mitigation**：tasks.md 要求新增一個明確的回歸測試，斷言 `GET /api/events`（公開端點）的回應內容 MUST NOT 包含 `createdByMemberId`／`createdByDisplayName`／`createdAtUtc`／座位狀態統計這幾個欄位，讓這件事有測試守著、不是只靠人記得

## Migration Plan

新增 EF Core migration：`Event` 資料表新增兩個 nullable 欄位（`CreatedByMemberId uuid NULL`、`CreatedAtUtc timestamp NULL`）與一個外鍵約束（`CreatedByMemberId → Members.Id`，`ON DELETE RESTRICT`）。純新增欄位、無需回填、不影響既有查詢與既有測試資料。前後端可視為一次性替換：後端 migration 套用＋新端點欄位上線後，前端同一輪改用新欄位渲染，不需要分階段 rollout 或 feature flag。
