## Context

`ticketing-core-domain`（已歸檔）留下三個 pure Handler（`CreateOrderHandler`/`ConfirmOrderHandler`/`CancelOrderHandler`），完全不做 I/O，呼叫端要自己載入 `EventSeat`/`TicketType`/`Order` 物件、自己存檔。`ticketing-infrastructure` 把 Repository（目前只有 `GetByIdAsync`/`Add` 這類最小操作）、`IUnitOfWork`（寫入必須包在交易裡）、`EventSeat` 悲觀鎖（`GetForUpdateAsync`，必須在交易內呼叫）都準備好了，但完全沒被使用過——這是第一次真的把這些兜起來。

`ticketing-event-management` 的 Handler 都是「先驗證/讀取（不開交易）→ 全部通過才開交易 → 只把 Add+Commit 包在交易裡」，因為那些是單純的建立操作，不涉及修改既有資料的併發風險。這次的訂單操作不一樣：建立訂單需要鎖座位（跟其他訂單搶）、確認/取消訂單需要修改「已存在」的訂單與座位狀態，會遇到 `ticketing-event-management` 沒遇過的並發問題（見決策 3）。

## Goals / Non-Goals

**Goals:**
- 已登入會員能瀏覽活動、座位可售狀態、票種價格（皆為公開端點，不需登入）。
- 已登入會員能建立訂單（鎖座位）、確認訂單（模擬付款、座位轉售出）、取消訂單（釋放座位），操作者必須是訂單本人。
- `Order.BuyerId` 從 JWT 取得並記錄，`CreateOrderHandler`/`Order` 建構子相應調整。
- 確認/取消訂單在取得座位鎖之後，重新讀取訂單最新狀態，避免並發操作互相覆蓋（見決策 3）。

**Non-Goals:**
- 後台訂單查看、逾時訂單背景清理——留給下一個 change。
- Confirm 端點的真正金流整合——延續原設計的 Non-Goal，呼叫即視為付款成功。
- 瀏覽端點的分頁/篩選/搜尋——先做最簡單的列表。
- 訪客（未登入）購票——這次確定要登入，`Order.BuyerId` 必填。
- 修改/刪除既有訂單以外的操作（例如訂單內容變更）——這次只有建立/確認/取消。

## Decisions

### 1. `Order.BuyerId` 必填，`CreateOrderHandler` 簽章調整（BREAKING）
`Order` 的兩個建構子（公開的與 EF-only 的）都新增 `Guid buyerId` 參數，公開建構子驗證 `buyerId != Guid.Empty`（比照既有 `venueId`/`seatMapId` 的驗證風格）。`CreateOrderHandler.Handle` 簽章改成 `Handle(Guid buyerId, IReadOnlyList<SeatSelection> selections)`，建構 `Order` 時一併傳入。這是明確的 Breaking Change：訂單從「匿名」變成「一定綁定會員」。

`OrderConfiguration`（`ticketing-infrastructure`）新增 `builder.Property(o => o.BuyerId).IsRequired()` 與 `builder.HasOne<Member>().WithMany().HasForeignKey(o => o.BuyerId).OnDelete(DeleteBehavior.Restrict)`——跟其他跨聚合參照一樣沒有 navigation，用 `HasOne<T>().WithMany()` 建立 FK（呼應 `ticketing-infrastructure` 驗收時補的那批 FK，這次新增的欄位從一開始就要有）。需要新的 Migration。

### 2. `OrderService` 協調服務：先驗證/讀取不需鎖的部分，鎖只包住真正需要鎖的操作
比照 `ticketing-event-management` 的「先驗證再開交易」精神，但這次「鎖座位」本身就是「讀取」的一部分（`GetForUpdateAsync` 沒有交易就無法呼叫，也沒有分開的「先查再鎖」兩步）。三個方法的順序：

- **`PlaceOrderAsync(buyerId, selections, ct)`**：`PlaceOrderRequestValidator`（FluentValidation，比照既有 `CreateSeatMapRequestValidator` 慣例，透過 `Program.cs` 既有的組件掃描自動註冊）驗證請求格式（非空、無重複配對）→ 逐一用 `ITicketTypeRepository.GetByIdAsync` 載入請求內每個不重複的 `TicketTypeId`（不需要鎖，`TicketType` 建立後不會變），任一個查無回 `Error.NotFound` → 開交易 → `IEventSeatRepository.GetForUpdateAsync(eventSeatIds)` 鎖定並載入座位，**回傳數量若少於請求數量，代表某個 `EventSeatId` 不存在，回 `Error.NotFound`**（這裡跟票種不同：`ticketing-infrastructure` 的 `GetForUpdateAsync` 契約本來就是「找不到的不補、由呼叫端自己比對數量」）→ 組出 `SeatSelection` 清單 → 呼叫既有 `CreateOrderHandler.Handle(buyerId, selections)` → 成功則 `IOrderRepository.Add` + Commit，失敗則不 Commit（交由 `await using` 自動 Rollback）。
- **`ConfirmOrderAsync(orderId, requestingBuyerId, ct)`** / **`CancelOrderAsync(orderId, requestingBuyerId, ct)`**：見決策 3，兩者流程一致，只差最後呼叫的是 `ConfirmOrderHandler.Handle` 還是 `CancelOrderHandler.Handle`。

### 3. Confirm/Cancel 必須「鎖後重讀」，否則兩個並發的同類操作會有一個被誤判成功
`ConfirmOrderAsync`/`CancelOrderAsync` 都需要先讀 `Order`（拿 `BuyerId` 做本人驗證、拿 `Items` 才知道要鎖哪些 `EventSeat`），再對這些座位呼叫 `GetForUpdateAsync`。問題是：**EF Core 的同一個 `DbContext` 對同一個實體只會查一次、之後都回傳記憶體裡已追蹤的舊物件（identity map），不會自動重新整理**。

**先釐清哪些併發組合已經安全、哪些不安全**：`ConfirmOrderHandler`/`CancelOrderHandler`（`ticketing-core-domain` 既有邏輯）在操作前都會用「鎖後拿到的新鮮 `EventSeat` 狀態」跟 `order.Id` 比對（`seat.IsHeldBy(order.Id, now)`／`seat.IsSoldBy(order.Id)`），而 `Order.Status` 的轉換永遠跟它持有座位的 held/sold 狀態在同一筆交易內綁定變更。所以：

- **Confirm 跟 Cancel 互搶（例如 A 確認、B 取消同一筆訂單）**：不管誰先誰後，輸家鎖到座位後看到的必定是贏家改過的新鮮座位狀態（已變成 Sold 或已釋放），`ConfirmOrderHandler` 的 `IsHeldBy` 檢查、或 `CancelOrderHandler` 對「座位已被同一張訂單賣出」的檢查都會直接攔下並回 `Error.Conflict`——**這個組合即使不做 `ReloadAsync` 也已經安全**，`Order.Status` 是否重讀不影響結果。
- **兩個並發的 Confirm**：同理，輸家鎖到的座位已經是 Sold（不是 Held），`IsHeldBy` 檢查會失敗並回 Conflict——同樣不需要 `ReloadAsync`。
- **兩個並發的 Cancel（真正需要 `ReloadAsync` 的情境）**：`CancelOrderHandler.Handle` 對「座位已經不是自己持有」的處理是**靜默略過**（`seat.ReleaseHold` 內部發現 `_heldByOrderId` 已經不是自己就直接 `return`，不報錯），只有座位「已售出」才會回 Conflict。如果輸家沒有重讀 `order.Status`，它手上的 `Order` 物件仍是查詢當下讀到的 Pending（記憶體內不會自動刷新），會直接呼叫 `order.Cancel()` 成功並回傳 `Result.Success()`——**即使贏家已經先把訂單改成 Cancelled，輸家也會誤報操作成功**，違反 spec「兩個並發同類操作只能一個成功、另一個 MUST 被拒絕」的要求（這不會讓資料庫狀態不一致，兩邊最終都是 Cancelled，但會讓其中一個呼叫端拿到錯誤的「成功」回應）。

**修法：拿到座位鎖之後，MUST 呼叫新增的 `IOrderRepository.ReloadAsync(order, ct)`（包裝 EF Core 的 `Entry(order).ReloadAsync()`），強制用資料庫目前的值覆寫這個追蹤中物件的欄位，才把 `order` 交給 `ConfirmOrderHandler`/`CancelOrderHandler`。** 這樣上例中後拿到鎖的 Cancel，鎖後重讀會看到贏家已經 Commit 的 Cancelled 狀態，`CancelOrderHandler.Handle` 的第一個檢查（`order.Status != Pending`）就會直接回 `Error.Conflict`，不會誤報成功。雖然 Confirm 跟 Cancel 互搶、或兩個並發 Confirm 這兩種組合本來就已經安全，但 `ReloadAsync` 統一套用在 `ConfirmOrderAsync`/`CancelOrderAsync`，不對併發組合另外判斷分支，避免邏輯分岔、也對其他兩種情境多一層防禦（defense-in-depth）。

`ReloadAsync` 只重讀 `Order` 自己的純量欄位（`Status`/`HeldUntilUtc`），不需要（也不會）重新載入 `Items` 集合——`Items` 建立後不會變，沒有這個問題。

本人驗證（`order.BuyerId == requestingBuyerId`）用第一次讀到的值就夠，不需要等鎖後重讀——`BuyerId` 建立後不可變更，沒有過期的風險，及早檢查也能在不需要的情況下省掉一次鎖等待。

### 4. Repository 介面新增唯讀查詢方法
- `IEventRepository.GetAllAsync(CancellationToken ct) : Task<IReadOnlyList<Event>>`
- `IEventSeatRepository.GetByEventIdAsync(Guid eventId, CancellationToken ct) : Task<IReadOnlyList<EventSeat>>`（唯讀，不鎖，供瀏覽端點用；跟既有的 `GetForUpdateAsync` 是兩個不同用途的方法，不要混用）
- `ITicketTypeRepository.GetByEventIdAsync(Guid eventId, CancellationToken ct) : Task<IReadOnlyList<TicketType>>`
- `IOrderRepository.ReloadAsync(Order order, CancellationToken ct) : Task`（見決策 3）

都是新增方法，不改動既有介面成員的簽章（除了決策 1 提到的 `CreateOrderHandler.Handle`，那是 Application 層 Handler 不是 Repository）。

### 5. 座位瀏覽端點要能對應到票種，所以要 Join 座位樣板拿 ZoneCode
`EventSeat` 本身只有 `SeatId`（指向物理座位樣板 `Seat`），沒有 `ZoneCode`。買家要下單，必須知道「這個座位屬於哪個分區」才能對到 `GET /api/events/{id}/ticket-types` 回傳的票種價格。座位列表的 Query Handler 流程：`IEventSeatRepository.GetByEventIdAsync(eventId)` 拿座位列表 → `IEventRepository.GetByIdAsync(eventId)` 拿 `SeatMapId` → `ISeatMapRepository.GetByIdAsync(seatMapId)`（已經會 `Include(Seats)`）拿座位樣板 → 用 `SeatId` 對應，組出 `{ EventSeatId, ZoneCode, SeatNumber, Status }`。`Status` 用 `EventSeat.GetStatus(now)`（`now` 來自既有 `IDateTimeProvider`）轉成字串，不對外暴露原始私有欄位。

**`IEventRepository.GetByIdAsync(eventId)` 找不到活動時回傳 `null`，這一步 MUST 先做 null 檢查並回 `Error.NotFound`，不可直接存取 `event.SeatMapId`**（否則對不存在的活動 ID 查座位會丟出未處理的 NullReferenceException，而非乾淨的 404）。`GetTicketTypesHandler`（決策 6）沒有這個 Join，活動不存在時 `ITicketTypeRepository.GetByEventIdAsync` 單純回傳空列表即可，不需要額外檢查——這是刻意的不對稱，因為它沒有解參考不存在物件的風險。

### 6. Controller 結構、授權、模擬付款
- `EventsController`（`api/events`，公開）：`GET /`（活動列表）、`GET /{id}/seats`、`GET /{id}/ticket-types`。
- `OrdersController`（`api/orders`，需登入，`[Authorize]` 不需要角色限制，任何已登入會員皆可）：`POST /`（`buyerId` 從 `User.GetMemberId()` 取得，不接受請求 Body 傳入，避免冒用他人身份）、`POST /{id}/confirm`、`POST /{id}/cancel`。
- 非本人操作回 403（`Error.Forbidden`，`ResultExtensions` 既有分類已支援，不新增）。
- `POST /api/orders/{id}/confirm` 沿用原設計：不接受任何付款資訊，呼叫即視為付款成功，之後金流增量才會取代。

## Risks / Trade-offs

- **[Risk]** `ReloadAsync` 是這次新增、之前完全沒用過的模式，如果 Cancel 的實作忘記呼叫它，兩個並發 Cancel 中的輸家會誤報操作成功（見決策 3）。這個問題只在「兩個並發同類操作（尤其是兩個並發 Cancel）」時才會出現——如果整合測試只測「Confirm 跟 Cancel 互搶」，即使 `ReloadAsync` 沒有實作或寫錯，測試依然會通過（因為這個組合本來就已經被既有的座位狀態檢查攔住），抓不到問題。→ **Mitigation**：整合測試 MUST 包含「兩個並發 Cancel（或兩個並發 Confirm）操作同一筆訂單」的情境（比照 `ticketing-infrastructure` 對悲觀鎖的測試方式，用兩個並發交易），驗證輸家確實回 Conflict 而非誤報 Success；「Confirm 跟 Cancel 互搶」可以另外測以涵蓋 spec Scenario，但不能取代前者。
- **[Risk]** 座位瀏覽端點需要三次 Repository 查詢（座位、活動、座位圖）才能組出回應，沒有做任何快取或合併查詢。→ **Mitigation**：這是讀取路徑，資料量小（一場活動的座位數是有限的），先求正確、不先做效能優化；如果之後真的是瓶頸，再評估合併查詢或快取。
- **[Risk]** `Orders.BuyerId` 加 FK 指向 `Members` 是跨兩個原本獨立規劃的領域（售票／會員）第一次在資料庫層級產生直接關聯。→ **Mitigation**：這是必然的（訂單本來就需要知道買家是誰），FK 只是把這個既有的邏輯關聯明確化成資料庫約束，沒有新增業務耦合。

## Migration Plan

- 新增 EF Core Migration：`docker compose exec api dotnet ef migrations add AddOrderBuyerId`，只新增 `Orders.BuyerId` 欄位與其 FK，不影響其他既有表。
- 因為 `BuyerId` 是 `NOT NULL` 且目前資料庫裡不會有任何既有的 `Orders` 資料（`ticketing-event-management` 之前完全沒有下單 API，不可能有人下過單），不需要處理既有資料的欄位遷移／預設值問題。這個假設是推論而非既定事實（例如開發過程中可能有人手動塞過測試資料），套用 migration 前 MUST 先執行 `SELECT COUNT(*) FROM "Orders"` 確認資料表確實是空的；如果不是空的，`AddOrderBuyerId` migration 需要改成先補一個預設值（或先清空測試資料）再改成 `NOT NULL`，不能直接套用。
- Rollback：`docker compose exec api dotnet ef database update <上一個 migration>`。

## Open Questions

（無——範圍與併發策略都已確認清楚。）
