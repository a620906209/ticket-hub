## Context

`GET /api/events`（`GetEventsHandler`）與 `GET /api/events/{id}/ticket-types`（`GetTicketTypesHandler`）目前每次請求都直接查 PostgreSQL。專案已有 Redis 基礎設施（`purchase-queue-leader-election` 引入）：`docker-compose.yml` 的 `redis` 服務、`Program.cs` 註冊的 `IConnectionMultiplexer` Singleton，以及 `RedisDistributedLock` 示範的 fail-open 錯誤處理慣例（Redis 無法連線時記錄 Warning、不中斷主流程）。本設計延續既有慣例，不引入新的基礎設施或抽象風格。

寫入面目前有五個會改變這兩個查詢回應內容的既有進入點（其中取消訂單的兩個外部呼叫端共用同一段失效邏輯，實作上是一個觸發點），皆已對照實際程式碼確認呼叫位置與交易邊界：
- `CreateEventHandler`（新增活動）、`SetEventQueueModeHandler`（開關熱門搶購模式）——影響 `EventsController.GetEvents`（`GetEventsHandler`，買家可匿名存取的公開查詢）的回應。**注意**：`AdminEventsController` 也有一個同名的 `GetEvents` 方法（對應 `GetAdminEventsHandler`，`GET /api/admin/events`），是完全不同的查詢端點——本次改動不快取它，`query-cache:events:list` 這個 key 只服務 `EventsController.GetEvents`，兩者不得混淆。兩個交易提交點皆在 `OrderService` 以外的對應 Handler 呼叫流程中（Admin 端點各自獨立的交易）
- `CreateTicketTypeHandler`（新增票種）——影響對應活動的 `GetTicketTypes` 回應
- `TicketType.Reserve`（`CreateOrderHandler.cs:67`，訂單建立成功時扣減純計數票種庫存）與 `TicketType.Release`（`CancelOrderHandler.cs:48`，訂單取消/逾時歸還庫存）——影響對應活動的 `GetTicketTypes` 回應中的 `AvailableQuantity`。實際交易提交點：
  - 建立訂單：`OrderService.PlaceOrderAsync` 在 `transaction.CommitAsync` 之後（程式碼行號約 307）
  - 取消訂單：`OrderService.ChangeOrderStatusAsync` 私有方法在 `transaction.CommitAsync` 之後（程式碼行號約 441）——此方法由 `CancelOrderAsync`（買家主動取消）與 `CancelExpiredOrderAsync`（`ExpiredOrderCleanupService` 背景清理）**共用同一個呼叫路徑**，兩者都委派給同一個 `CancelOrderHandler.Handle`，因此快取失效只需要在 `ChangeOrderStatusAsync` 這一個共用交易骨架的提交後加一次，不需要在兩個呼叫端各自重複
  - 補充：`CreateOrderHandler.cs:77` 也有一次 `ticketType.Release(quantity)` 呼叫，但那是**尚未提交交易前的記憶體內補償回滾**（同一次建立訂單請求中，後面的計數項目扣減失敗時，把前面已扣減的項目補回去，整筆請求最終回傳 Failure、不會呼叫 `transaction.CommitAsync`）——這個呼叫點 MUST NOT 觸發快取失效，因為資料庫從未寫入任何變更，沒有東西需要失效

目前 Admin 端沒有「更新活動」「更新票種」端點，因此不需要處理那兩種情境的失效。

`ChangeOrderStatusAsync` 這個共用交易骨架有三個呼叫端：`ConfirmOrderAsync`（確認付款，程式碼行號約 314）、`CancelOrderAsync`（買家主動取消，約 353）、`CancelExpiredOrderAsync`（背景逾時清理，約 360）。**確認付款不會呼叫 `TicketType.Reserve`／`Release`，`AvailableQuantity` 在付款這一步完全不變**（純計數票種的庫存扣減發生在更早的「建立訂單」時；付款只是把 `OrderStatus` 從 `Pending` 轉為 `Paid`，不重新處理庫存）。因此票種列表快取失效 MUST NOT 在 `ConfirmOrderAsync` 觸發——若連確認付款都清除快取，會讓「哪些操作真的改變了票種庫存」這件事在 spec 與程式碼之間出現落差（spec 只要求 Reserve／Release 對應的失效，不包含 Confirm），對日後追查快取失效邏輯的人造成誤導。

實作上，`ChangeOrderStatusAsync` 新增一個 `bool invalidatesTicketTypeCache` 參數，由呼叫端明確指定：`CancelOrderAsync`／`CancelExpiredOrderAsync` 傳入 `true`，`ConfirmOrderAsync` 傳入 `false`。這是顯式參數，不是隱藏的型別判斷或字串比對，維持共用骨架的簡潔性，同時精確對應 spec 實際要求的失效範圍。

## Goals / Non-Goals

**Goals:**
- 為上述兩個查詢端點導入 Redis cache-aside 快取，降低對 PostgreSQL 的重複查詢
- 在既有寫入進入點（活動建立/隊列開關、票種建立、訂單建立/取消庫存異動），於資料庫交易提交成功後明確觸發對應快取失效
- TTL 作為被動安全網，防止任何遺漏的失效呼叫造成無限期的資料不一致
- Redis 無法連線時 fail-open：查詢直接落回資料庫，不影響功能正確性，只影響效能

**Non-Goals:**
- 不快取 Admin 專用的活動列表查詢（`AdminEventsController.GetEvents`／`GetAdminEventsHandler`，`GET /api/admin/events`）——本次僅涵蓋買家可匿名存取的公開查詢端點（`EventsController.GetEvents`），兩者方法名稱相同但是完全不同的 Controller/Handler，不共用快取 key
- 不快取 `GET /api/events/{id}/seats`（座位即時鎖定狀態，變動頻繁且需要即時性，不屬於「一般查詢」）
- 不引入通用快取框架或對所有查詢端點做無差別快取——僅限本次盤點出的兩個端點
- 不處理跨請求的快取預熱（warm-up）或主動背景刷新
- 不新增跨實例的快取失效廣播機制（Redis 本身是所有實例共用的單一邏輯儲存，一個實例執行 `DEL` 後，其他實例下次讀取自然看到失效結果，不需要 pub/sub）

## Decisions

### 1. 快取抽象放在 Application 層，實作放 Infrastructure 層，比照 `IDistributedLock` 既有慣例
新增 `IQueryCache`（`Application/Common/Interfaces`），提供 `GetAsync<T>(key, cancellationToken)` / `SetAsync<T>(key, value, ttl, cancellationToken)` / `RemoveAsync(key, cancellationToken)`——每個方法皆接受並宣告 `CancellationToken cancellationToken`，比照 `IDistributedLock` 兩個方法的既有簽章（`IDistributedLock.cs:8,10`），符合 CLAUDE.md「非同步程式設計」節的強制規則；Infrastructure 層以既有的 `IConnectionMultiplexer` Singleton + `System.Text.Json` 序列化實作，錯誤處理沿用 `RedisDistributedLock` 的 try/catch fail-open 模式（`RedisConnectionException`/`RedisTimeoutException` 時記錄 Warning、視為快取未命中，不拋出例外中斷呼叫端）。

`GetAsync` 額外多一種失敗來源需要 fail-open：快取內容存在、Redis 連線正常，但反序列化失敗（`System.Text.Json.JsonException`）。**訂正觸發條件的描述**：`System.Text.Json` 預設對 DTO 演進相當寬容——新增欄位、缺少非必要欄位、多出未知欄位，預設都不會拋出例外（不會因為單純加減欄位就失敗，這點不能誇大描述成「DTO 欄位異動」都會觸發）；只有「JSON 語法本身不合法」或「JSON 值與目標型別確實不相容」（例如某欄位期望是數字，快取裡卻是無法轉換的字串；或期望是 `Guid`，卻是格式不對的字串）才會可靠地拋出 `JsonException`。這不屬於 `RedisConnectionException`/`RedisTimeoutException`，MUST 另外用一個 catch 分支處理：捕捉 `JsonException`，記錄 Warning（訊息含快取 key），視為未命中（回傳未命中並落回查詢資料庫），不對外拋出例外——原則與 Redis 連線失敗的 fail-open 完全一致，只是失敗來源不同。**這個 catch 分支不是、也不試圖成為 DTO 版本升級策略**：若未來 DTO 型別異動屬於會讓 `System.Text.Json` 真正拋出例外的那一類（型別不相容），這個分支會讓它安全地退化為快取未命中；若是預設會被寬容處理、不拋例外的異動（單純加減欄位），部署後可能讀到欄位值為預設值的舊快取，直到該筆快取因 TTL 或既有失效機制被清除——這不是本次改動要解決的問題，不在範圍內設計版本化 cache key 或部署時清除快取的機制，僅記錄為已知限制。

**考慮過的替代方案**：`Microsoft.Extensions.Caching.StackExchangeRedis` 提供的 `IDistributedCache`。放棄理由——`IDistributedCache` 的底層方法一樣會拋 `RedisConnectionException`，仍須自己包一層 try/catch 才能做到 fail-open，並無節省程式碼；而專案已有 `IConnectionMultiplexer` 直接操作的既定慣例（`RedisDistributedLock`），沿用同一種操作方式比引入第二套 Redis 存取風格更符合「一致性優先」（CLAUDE.md Rule 11）。

### 2. 快取邏輯直接寫在既有 Handler 內，不引入 pipeline/decorator 抽象
專案沒有 MediatR 或既有的 handler pipeline 機制，只有 2 個 Handler 需要快取。直接在 `GetEventsHandler`／`GetTicketTypesHandler` 內加上「讀快取 → 未命中查 DB → 寫回快取」三步邏輯，符合 Rule 2（不為單一用途導入抽象）。若未來有第三個查詢端點需要相同模式，屆時再抽出共用邏輯。

### 3. 快取 Key 設計
- 活動列表：固定 key `query-cache:events:list`（無參數查詢，全站共用一份）
- 票種列表：`query-cache:ticket-types:event:{eventId}`（依活動切分，只影響異動的那場活動）

### 4. 失效時機：資料庫交易提交成功後，於原呼叫流程內同步觸發
五個實際失效觸發點（皆在其資料庫交易 `CommitAsync` 之後、同一次呼叫的回傳前，同步呼叫 `IQueryCache.RemoveAsync`）：
- `CreateEventHandler` 的交易提交後 → 清除 `query-cache:events:list`
- `SetEventQueueModeHandler` 的交易提交後 → 清除 `query-cache:events:list`
- `CreateTicketTypeHandler` 的交易提交後 → 清除該活動的 `query-cache:ticket-types:event:{eventId}`
- `OrderService` 建立訂單方法的 `transaction.CommitAsync` 之後（程式碼行號約 307）→ **只有當本次下單的 `quantitySelections`（純計數票種項目）非空時**才清除該次下單 `eventId` 對應的 `query-cache:ticket-types:event:{eventId}`；純座位制訂單（`quantitySelections` 為空）不會呼叫 `TicketType.Reserve`、不變更 `AvailableQuantity`，比照 `ConfirmOrderAsync` 的同一個原則（不變更庫存的操作不觸發失效），不做無條件的全面失效
- `OrderService.ChangeOrderStatusAsync`（`transaction.CommitAsync` 之後，程式碼行號約 441）在 `invalidatesTicketTypeCache = true` 時 → 清除 `order.EventId` 對應的 `query-cache:ticket-types:event:{eventId}`——只有 `CancelOrderAsync`／`CancelExpiredOrderAsync` 這兩個外部呼叫端會傳入 `true`（兩者共用同一個觸發點），`ConfirmOrderAsync` 傳入 `false`、不觸發失效

失效呼叫失敗（Redis 無法連線）時記錄 Warning，MUST NOT 讓寫入操作本身失敗——寫入的正確性完全在資料庫交易內已確定，快取只是效能優化層，不應該讓快取層的故障反過來影響核心業務操作。

### 5. TTL 作為安全網，具體數值與理由
- `QueryCache:EventListTtlSeconds` 預設 `30`：活動列表變動頻率低（僅建立活動、開關熱門搶購模式兩種操作），30 秒的最大陳舊時間上限在此頻率下影響輕微
- `QueryCache:TicketTypesTtlSeconds` 預設 `10`：`AvailableQuantity` 直接影響買家的購買決策，採較短 TTL 降低任一失效呼叫遺漏時對買家造成誤導的最長時間
- 兩者皆透過設定檔覆寫（沿用 `PurchaseQueue`／`OrderCleanup` 設定區塊的慣例），不寫死於程式碼；這兩個值即為「假設所有明確失效呼叫都正常運作」前提下的最大允許陳舊時間上限——正常路徑下失效呼叫在資料異動當下即觸發，實際延遲遠低於 TTL

### 6a. TTL 設定值採 fail-fast 驗證，不當作有安全預設值的設定
`QueryCache:EventListTtlSeconds`／`QueryCache:TicketTypesTtlSeconds` 若被覆寫為 `0`、負數或無法解析為整數，後果不是單純「快取失效」這種可接受的效能降級：`0` 或負數的 `TimeSpan` 傳給 `IConnectionMultiplexer` 的 `StringSetAsync` expiry 參數，StackExchange.Redis 會在呼叫當下同步拋出 `ArgumentException`——這不屬於 `RedisQueryCache` 現有 catch 分支（`RedisConnectionException`／`RedisTimeoutException`）捕捉的例外型別，會直接未捕捉地往外拋，導致**每一次**快取寫入呼叫（也就是每一次快取未命中的查詢）都會讓 `GetEventsHandler`／`GetTicketTypesHandler` 拋出未預期例外、回應變成 `500`。這與 `RateLimitingOptions`／`DistributedLockOptions`「有安全預設值，設定錯誤只是效能/行為降級」的類別不同，性質上更接近 `PurchaseQueueOptions`／`JwtOptions`「缺值就無法運作」的類別。

新增 `QueryCacheOptions`（`Application/Common`），仿照 `PurchaseQueueOptions.cs` 的寫法，兩個屬性皆加 `[Range(1, int.MaxValue)]`，不給 C# 層級預設值（缺漏時維持 `0`，會被 `[Range]` 擋下，與「設定但為 0 或負數」得到相同的 fail-fast 效果）；`Program.cs` 比照 `PurchaseQueueOptions` 的註冊寫法：`AddOptions<QueryCacheOptions>().Bind(...).ValidateDataAnnotations().ValidateOnStart()`。設定值無法解析為整數時，`.Bind(...)` 本身就會在啟動時拋出例外，效果等同 fail-fast，不需要額外處理。

### 6. 匿名存取範圍不變，快取內容在所有呼叫者間安全共享
確認 `EventsController` 目前沒有 `[Authorize]`，`GET /api/events`／`GET /api/events/{id}/ticket-types` 本來就是匿名可存取端點，本次改動不新增任何權限檢查。兩端點的回應（`EventDto`、`TicketTypeDto`）內容不依呼叫者身份而異，因此固定 key（活動列表）與依活動 Id 切分的 key（票種列表）可以安全地讓所有呼叫者共用同一份快取內容，不需要依身份或角色再切分快取 key。若未來這兩個回應需要個人化（例如依主辦方身份顯示不同管理欄位），MUST 在那次改動重新設計 key，本次不處理。

`eventId` 若不是合法 GUID，會被路由層的 `{id:guid}` 限制直接擋下（既有行為），永遠不會進入 `GetTicketTypesHandler`，因此不會查詢或寫入任何快取 key——本次改動不需要額外處理這個情境，只需要在測試中確認既有行為未被破壞。

**CLAUDE.md 安全強制規則確認**（本次改動涉及外部輸入、資料庫讀取、匿名存取範圍，依規則須在實作前逐項回答，已對照實際程式碼確認）：
- **外部輸入驗證由哪一層負責**：`eventId` 的格式驗證（是否為合法 GUID）由 ASP.NET Core **路由層**的 `{id:guid}` 限制負責，不合法格式的請求在路由階段就被拒絕（`404`），永遠不會進入 Controller／Handler；活動**是否存在**的驗證則由 **Handler 層**負責（`GetTicketTypesHandler` 呼叫 `IEventRepository.GetByIdAsync` 查無資料時回傳 `NotFound`）——這是兩層不同性質的驗證，格式驗證在路由層、存在性驗證在 Handler 層
- **資料庫查詢是否使用 EF Core／參數化查詢**：是。本次快取包裝的兩個既有查詢方法——`IEventRepository.GetAllAsync()`（`EventRepository.cs:21-22`）與 `ITicketTypeRepository.GetByEventIdAsync()`（`TicketTypeRepository.cs:21-22`）——皆為純 EF Core LINQ（`ToListAsync()`／`Where()`），由 EF Core 自動產生參數化 SQL，不涉及字串拼接；這兩個 repository 內另外用 `FromSqlInterpolated` 寫原生 SQL 的方法（`GetForUpdateAsync`）不在本次快取範圍內，且該寫法本身也是參數化插值，無 SQL Injection 風險
- **是否存在 N+1 查詢風險**：無。`GetAllAsync()`／`GetByEventIdAsync()` 回傳的 `EventDto`／`TicketTypeDto` 全部欄位皆為 `Event`／`TicketType` 自身的純量屬性（見 `GetEventsHandler.cs`／`GetTicketTypesHandler.cs` 的 `Select` 對應），不需要載入任何 Navigation Property，每次查詢固定是 1 條 SQL，不隨結果筆數增加額外查詢；本次改動只是在這兩條既有查詢外包一層快取，未新增、未修改任何 repository 查詢方法，查詢本身的形狀不變
- **受影響寫入路徑各自的權限主體與檢查層**（本次改動只新增快取失效呼叫，不變更任一路徑既有的權限規則，以下為對照原始碼確認的既有行為）：
  - `CreateEventHandler`（建立活動）、`SetEventQueueModeHandler`（開關熱門搶購模式）、`CreateTicketTypeHandler`（建立票種）：三者皆掛在 `AdminEventsController`，該 Controller 類別層級有 `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`（`AdminEventsController.cs:12`），權限檢查在 **Middleware／Authorization Policy 層**（ASP.NET Core 授權管線，早於 Action 執行），只有 `Admin` 角色能觸發，非 Admin 或未登入呼叫在進入 Handler 前就被拒絕（`403`／`401`），不可能觸發快取失效
  - 訂單建立（`OrderService.PlaceOrderAsync`）、買家主動取消（`CancelOrderAsync`）：掛在 `OrdersController`，該 Controller 類別層級有 `[Authorize]`（`OrdersController.cs:12`，只要求已登入，不限角色），權限檢查同樣在 **Middleware／Authorization Policy 層**；取消還有第二層 **Handler 層**的資源擁有權檢查（`ChangeOrderStatusAsync` 比對 `order.BuyerId != requestingBuyerId`，非本人取消回傳 `403 Forbidden`，見 `OrderService.cs`），兩層驗證通過才會走到觸發快取失效的程式碼
  - 背景逾時清理（`CancelExpiredOrderAsync`）：由 `ExpiredOrderCleanupService` 背景服務呼叫，沒有 HTTP 請求、沒有使用者身份可驗證，**系統層授權條件**是「以 `requestingBuyerId = null` 呼叫、`ChangeOrderStatusAsync` 改以『訂單的 `HeldUntilUtc` 確實已逾時』作為授權依據取代本人驗證」（`ticketing-order-management design.md` 決策 1 既有規則，本次未變更）——這代表該路徑的『未授權使用者能否觸發』防線不是身份驗證，而是「只有真的已逾時的訂單才會被清理」這個業務條件本身，背景服務不對外暴露 HTTP 端點，外部無法直接呼叫
- **測試對應**：格式驗證（路由層）→ QC-ACCESS-003（spec.md）→ tasks.md 8.3；存在性驗證（Handler 層）→ QC-TT-004（spec.md）→ tasks.md 3.5；EF Core／N+1 兩項是對既有查詢實作的確認，查詢邏輯本身不在本次改動範圍內，已由 `event-catalog`／`ticket-ordering` 既有 spec 與其原始實作的測試覆蓋，不需要為此新增測試任務；上述五個寫入路徑的權限規則皆為既有行為、本次未變更，也已由各自既有能力（`event-management`／`ticket-ordering`）的既有 spec 與測試覆蓋，本次改動不需要為權限本身重新測試，只需要確保新增的快取失效呼叫不會繞過或提前於既有的授權檢查（見上方各路徑的呼叫順序說明）

## Risks / Trade-offs

- **[Risk]** `TicketType.Reserve`／`Release` 有多個呼叫點（訂單建立、取消、逾時釋放），若失效呼叫遺漏其中一處，買家會看到與實際庫存不符的 `AvailableQuantity`。
  → **Mitigation**：這只影響顯示的新鮮度，不影響下單正確性（下單本身仍會透過 `GetForUpdateAsync` 悲觀鎖重新確認即時庫存，見既有 `ticket-ordering`／`ticket-purchase` 規格）；TTL 頂住最大 staleness 上限；tasks 階段須為每個扣減/歸還呼叫點各自驗證有對應的失效呼叫。

- **[Risk]** `EventDto.IsQueueModeEnabled` 同樣會被快取（`EventListTtlSeconds`，預設 30 秒），買家瀏覽活動列表時看到的「是否處於熱門搶購模式」可能落後 Admin 剛切換的真實設定值，最長 30 秒。這跟 `AvailableQuantity` 的風險是同一類（顯示新鮮度，不是正確性），但影響的是完全不同的 UX 流程，先前遺漏在風險盤點裡，這裡補上：
  → **Mitigation**：下單時的正確性不受影響——買家真正送出訂單時，系統仍依 `ticket-purchase`「透過 API 建立訂單並鎖定座位或扣減票種庫存」Requirement 的既有規則，以「系統實際執行建立邏輯當下重新讀取到的值」判斷是否需要排隊資格，不採信瀏覽時查到的快取值（該 Requirement 原文本來就已經要求這樣做，是為了防範 Admin 在買家送出請求過程中切換設定的情境，本次快取只是讓這個既有防線更常被用到）。**唯一會被快取新鮮度影響的是買家瀏覽活動列表時，前端據以判斷「是否要先導向排隊入口」的那個初始判斷**——若 Admin 剛把某活動切換成熱門搶購模式，最長 30 秒內，仍看到快取的舊列表的買家會被導向一般選位流程而非排隊入口；不過買家送出訂單時後端仍會依當下真值擋下（回傳 `403 QueueAdmissionRequired`），前端既有的既定錯誤處理（見 `buyer-web-ui` BW-QUEUE-004）會把買家導回排隊等待畫面——買家體驗上是「先進了選位頁，送出時才被要求去排隊」，比「進頁面就先看到排隊入口」多一次來回，但不會讓任何人繞過排隊直接搶到票。這個 UX 落差可接受，不額外處理（若要更即時，可考慮把 `EventListTtlSeconds` 調得更短，但這是設定值調整，不是本次需要解決的架構問題）。

- **[Risk]** 交易提交與快取失效呼叫之間的極短視窗內，另一個並發查詢可能讀到失效前的舊資料並重新寫回快取，短暫「復活」已過期的快取值。
  → **Mitigation**：這是 cache-aside 模式的既知限制，不是本次改動特有的風險，不為此引入版本戳記等額外複雜機制（Rule 2：不為理論邊界情況過度設計）。**訂正**：這個復活的快取值嚴格來說不是「從交易提交那一刻起算、絕對不超過 TTL」，而是「從它自己被重新寫入的那一刻起算 TTL」——兩者的差距就是一次「Handler 查詢資料庫→寫入快取」的完整耗時，**本次改動沒有為這個差距另外設計或實作任何強制上限**（不新增查詢逾時、不新增鎖、不新增版本戳記檢查），這個差距的實際大小完全取決於既有、與本次改動無關的基礎設施行為。唯一做出的保證是「有限」（一定會過期，不會無限期陳舊），不是某個具體數值（不宣稱「毫秒級」，那是本次未實作也未測試強制施加的數字）。已誠實揭露為 spec.md「快取項目具備 TTL 安全網」Requirement 的「已知邊界例外」段落，並新增 QC-TTL-004 Scenario 明確定義這個「有限但不承諾具體數值」的保證，不再含糊地說有一個明確界限；對應測試見 tasks.md 6.2e（以可控制的同步機制決定性地構造這個時序，驗證的是「最終會過期」而非「多快過期」）。

- **[Risk]** Redis 快取讀寫失敗時 fail-open 落回資料庫查詢，若 Redis 大量請求逾時（而非直接連線失敗），可能讓每次查詢都多付出一次逾時等待成本，反而拖慢查詢。
  → **Mitigation**：沿用 `RedisDistributedLock` 既有的連線設定（`AbortOnConnectFail = false`），逾時行為由 StackExchange.Redis 的既有連線設定控制，不在本次改動額外調整；如觀察到此問題屬於 Should 之後的效能調校範疵。

## Migration Plan

不需資料遷移。**訂正**：`QueryCacheOptions` 的兩個 TTL 設定值刻意不給 C# 層級預設值（見決策 6a），缺漏或無效時 fail-fast 啟動失敗，這不是「有安全預設值、向後相容」的類別——但因為本次改動同時在 `appsettings.json` 出廠設定裡就寫入了 `EventListTtlSeconds=30`／`TicketTypesTtlSeconds=10`（tasks.md 1.4），只要沿用出廠設定檔案部署，既有部署流程不會被中斷；只有在部署時**額外覆寫**這兩個值為缺漏/無效時，才會照設計刻意啟動失敗，這是有意的保護行為，不是相容性問題。Redis 服務已存在於 `docker-compose.yml`，不需新增基礎設施。

## Open Questions

（無——TTL 具體數值已於決策 5 訂定，匿名存取與快取共享安全性已於決策 6 確認）
