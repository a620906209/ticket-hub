## Context

`ticketing-core-domain`（已歸檔）留下三個 pure Handler（`CreateOrderHandler`/`ConfirmOrderHandler`/`CancelOrderHandler`），完全不做 I/O，呼叫端要自己載入 `EventSeat`/`TicketType`/`Order` 物件、自己存檔。`ticketing-infrastructure` 把 Repository、`IUnitOfWork`（寫入必須包在交易裡）、`EventSeat` 悲觀鎖（`GetForUpdateAsync`，必須在交易內呼叫）都準備好了；`ticketing-event-management` 已經在用 Repository + `IUnitOfWork`（見下段），**但 `GetForUpdateAsync` 悲觀鎖從沒被真正呼叫過**——這是第一次真的把鎖定機制跟訂單流程兜起來。

`ticketing-event-management` 的 Handler 都是「先驗證/讀取（不開交易）→ 全部通過才開交易 → 只把 Add+Commit 包在交易裡」，因為那些是單純的建立操作，不涉及修改既有資料的併發風險，也不需要悲觀鎖。這次的訂單操作不一樣：建立訂單需要鎖座位（跟其他訂單搶）、確認/取消訂單需要修改「已存在」的訂單與座位狀態，會遇到 `ticketing-event-management` 沒遇過的並發問題（見決策 3）。

## Goals / Non-Goals

**Goals:**
- 任何人（不需登入）都能瀏覽活動、座位可售狀態、票種價格。
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
比照 `ticketing-event-management` 的「先驗證再開交易」精神，但這次「鎖座位」本身就是「讀取」的一部分（`GetForUpdateAsync` 沒有交易就無法呼叫，也沒有分開的「先查再鎖」兩步）。

**Request DTO**（Controller 收到的請求 body，跟 Domain 的 `SeatSelection` 是兩回事，不可混用）：
```csharp
public sealed record PlaceOrderRequest(IReadOnlyList<PlaceOrderSelectionRequest> Selections);
public sealed record PlaceOrderSelectionRequest(Guid EventSeatId, Guid TicketTypeId);
```

**`PlaceOrderAsync(buyerId, request, ct)`**：
1. `PlaceOrderRequestValidator`（FluentValidation，比照既有 `CreateSeatMapRequestValidator` 慣例，透過 `Program.cs` 既有的組件掃描自動註冊）驗證：`Selections` 非空；每筆的 `EventSeatId`/`TicketTypeId` 皆不可為 `Guid.Empty`；**`EventSeatId` 不可重複**（這是核心規則——同一個 `EventSeatId` 配兩個不同 `TicketTypeId` 對 `CreateOrderHandler` 來說一樣是「同一座位被選兩次」，見 `src/ProjectC.Application/Orders/CreateOrderHandler.cs:29-33`；「配對不重複」不夠，會漏掉這個情況）。
2. 逐一用 `ITicketTypeRepository.GetByIdAsync` 載入請求內每個不重複的 `TicketTypeId`（不需要鎖，`TicketType` 建立後不會變），任一個查無回 `Error.NotFound`。
3. 開交易 → `IEventSeatRepository.GetForUpdateAsync(distinctEventSeatIds)` 鎖定並載入座位（`distinctEventSeatIds` 直接用第 1 步已經驗證過的 `Selections` 取值即可，因為 Validator 已保證 `EventSeatId` 唯一，這裡的「distinct」只是強調呼叫端不能假設 `GetForUpdateAsync` 會幫忙去重比對數量——**回傳數量若少於請求數量，代表某個 `EventSeatId` 不存在，回 `Error.NotFound`**（`ticketing-infrastructure` 的 `GetForUpdateAsync` 契約是「找不到的不補、由呼叫端自己比對數量」，見 `IEventSeatRepository.cs:17-22`）。這一步為了確認座位存在，可能會對這些座位短暫取得資料庫 row lock，但**還沒有呼叫 `EventSeat.Hold`、也還沒有 Commit**——spec「不對任何座位執行鎖定」指的是不建立業務上的 Held 狀態、不落地，不是完全不碰資料庫；交易失敗 Rollback 後這個短暫的 row lock 會立刻釋放。
4. **分區比對（防止用便宜分區的票種配貴分區的座位）**，對每個 `Selections` 項目依序：
   1. **先檢查 `selection.TicketType.EventId == selection.EventSeat.EventId`，不相等直接回 `Error.Validation`**（跟 `CreateOrderHandler.Handle` 之後會做的跨活動檢查用同一個判斷條件、同一種錯誤類型，只是提前到這裡做，見 `src/ProjectC.Application/Orders/CreateOrderHandler.cs:25-33`）。這一步 MUST 先做，才進到分區比對，避免「座位屬於 A 活動、票種屬於 B 活動、剛好兩邊 ZoneCode 字串不同」時，錯誤地回報「分區不一致」而不是「票種與座位不屬於同一活動」——也避免為了根本不該配對的資料多查一次座位圖。
   2. 通過後才用鎖到的 `EventSeat.SeatId` 對照座位圖找出座位實際所屬 `ZoneCode`（跟決策 5 的 Join 邏輯一樣：`EventSeat.EventId` → `IEventRepository.GetByIdAsync` 拿 `SeatMapId` → `ISeatMapRepository.GetByIdAsync`，已 `Include(Seats)`，用 `SeatId` 找到座位樣板的 `ZoneCode`；因為上一步已經確保同一個 `Selections` 項目的座位與票種同屬一個活動，正常情況下所有項目屬於同一個 `EventId`，可以只查一次並快取結果，不需要每個項目各查一次）。**`IEventRepository.GetByIdAsync`／`ISeatMapRepository.GetByIdAsync` 回傳 `null`，或座位圖內找不到對應 `SeatId` 的座位樣板，理論上不應該發生（`EventSeat`/`Event`/`SeatMap`/`Seat` 之間皆有 FK 或建立時的驗證保證一致），但 MUST 防禦性地回 `Error.Conflict`（標示為活動座位資料不一致），不可讓 null 直接往下傳導致 NullReferenceException**——跟決策 2 稍後 Confirm/Cancel 對「訂單內部資料不一致」的處理是同一個防禦原則。
   3. 跟該項目的 `TicketType.ZoneCode` 比對，**不相等回 `Error.Validation`**（例如：座位屬於 A 區，卻配了 B 區的票種）。這是新增的驗證規則，`CreateOrderHandler` 本身不做這個檢查（它只驗證 `TicketType.EventId == EventSeat.EventId`，不驗證分區），所以只能在 `OrderService` 組裝 `SeatSelection` 之前擋下來。
5. 組出 `SeatSelection` 清單 → 呼叫既有 `CreateOrderHandler.Handle(buyerId, selections)` → 成功則 `IOrderRepository.Add` + Commit，失敗則不 Commit（交由 `await using` 自動 Rollback）。
6. 回傳 `Result<Guid>`（新訂單 Id），比照 `CreateEventHandler` 的慣例。

**`ConfirmOrderAsync(orderId, requestingBuyerId, ct)`** / **`CancelOrderAsync(orderId, requestingBuyerId, ct)`**（兩者流程一致，只差最後呼叫的是 `ConfirmOrderHandler.Handle` 還是 `CancelOrderHandler.Handle`，回傳皆為 `Result`）：
1. `IOrderRepository.GetByIdAsync(orderId, ct)`（既有方法 MUST 一併載入 `Items`）→ 回傳 `null` 時回 `Error.NotFound`，**不開交易、不鎖任何座位**。
2. 驗證本人：`order.BuyerId != requestingBuyerId` → 回 `Error.Forbidden`（用第一次讀到的值即可，見下方「本人驗證」段落；同樣不開交易）。
3. 開交易 → `IEventSeatRepository.GetForUpdateAsync(order.Items.Select(i => i.EventSeatId).Distinct().ToList())` 鎖定 `order.Items` 對應的座位。
4. **鎖到的數量若少於 `order.Items` 內不重複的 `EventSeatId` 數量，回 `Error.Conflict`**（標示為「訂單內部資料不一致」）。理論上不應該發生——目前系統沒有任何刪除 `EventSeat` 的路徑，`order.Items` 引用的座位建立後就一直存在——但 `Cancel` 這條路徑如果不在 Service 層先比對數量、直接把不完整的 `eventSeatsById` 丟給 `CancelOrderHandler.Handle`，會因為它對「查不到座位」的處理是靜默 `continue`（見 `src/ProjectC.Application/Orders/CancelOrderHandler.cs:33-34`）而讓訂單被成功取消，卻有一個查不到的座位沒被釋放；`Confirm` 這條路徑則是 `ConfirmOrderHandler` 自己會查不到就回 `Error.NotFound`（見 `ConfirmOrderHandler.cs:30-31`），不會有這個風險，但 Service 層統一先比對數量可以避免兩個方法各自處理不一致、也能在鎖後立刻發現問題不必等 Handler 內部才發現。
5. **`IOrderRepository.ReloadAsync(order, ct)`（見決策 3，不可省略）**。
6. 呼叫 `ConfirmOrderHandler.Handle` / `CancelOrderHandler.Handle`。
7. 成功則 Commit，失敗則不 Commit（交由 `await using` 自動 Rollback）。

**其他兩個小情境**：
- **兩人搶同一座位**：`GetForUpdateAsync` 讓兩筆交易對同一批座位序列化執行，後拿到鎖的那筆會看到座位已經是 Held/Sold，`CreateOrderHandler.Handle` 或 `ConfirmOrderHandler.Handle` 依既有規則回 `Error.Conflict`，不需要額外處理。
- **`BuyerId` 不在 `Members` 表**：正常情況下不會發生（`buyerId` 來自已驗證的 JWT，簽發時對應的會員必然存在），`OrderConfiguration` 的 FK 只是最後一道防線（跟 `event-management` 的 Admin 角色檢查一樣，是「理論上不該發生但資料庫層仍要擋」的防禦），不需要在 Application 層額外檢查。

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
- `IOrderRepository.ReloadAsync(Order order, CancellationToken ct) : Task`（見決策 3）。**契約**：傳入的 `order` MUST 是同一個請求的 `DbContext`（同一筆交易）內、剛透過 `GetByIdAsync` 查出來、目前仍被追蹤（tracked）的同一個實體實例，等同直接包裝 `DbContext.Entry(order).ReloadAsync()`；呼叫端不需要（也不應該）對其他來源的 `Order` 實例（例如已經 `Detach`、或來自不同 `DbContext`）呼叫這個方法，實作不處理這種情況。

都是新增方法，不改動既有介面成員的簽章（除了決策 1 提到的 `CreateOrderHandler.Handle`，那是 Application 層 Handler 不是 Repository）。

**`GetEventsHandler` 回傳 DTO 欄位**：`{ Id, Title, StartAtUtc, VenueId, SeatMapId }`，直接對應 `Event` 的公開屬性，不需要額外 Join（決策 5、決策 6 的座位/票種端點回應欄位已個別定義，這裡補上活動列表端點的）。

### 5. 座位瀏覽端點要能對應到票種，所以要 Join 座位樣板拿 ZoneCode
`EventSeat` 本身只有 `SeatId`（指向物理座位樣板 `Seat`），沒有 `ZoneCode`。買家要下單，必須知道「這個座位屬於哪個分區」才能對到 `GET /api/events/{id}/ticket-types` 回傳的票種價格。座位列表的 Query Handler 流程：`IEventSeatRepository.GetByEventIdAsync(eventId)` 拿座位列表 → `IEventRepository.GetByIdAsync(eventId)` 拿 `SeatMapId` → `ISeatMapRepository.GetByIdAsync(seatMapId)`（已經會 `Include(Seats)`）拿座位樣板 → 用 `SeatId` 對應，組出 `{ EventSeatId, ZoneCode, SeatNumber, Status }`。`Status` 用 `EventSeat.GetStatus(now)`（`now` 來自既有 `IDateTimeProvider`）轉成字串，不對外暴露原始私有欄位。

**`IEventRepository.GetByIdAsync(eventId)` 找不到活動時回傳 `null`，這一步 MUST 先做 null 檢查並回 `Error.NotFound`，不可直接存取 `event.SeatMapId`**（否則對不存在的活動 ID 查座位會丟出未處理的 NullReferenceException，而非乾淨的 404）。

`GetTicketTypesHandler` 雖然不需要 Join 座位圖，但 spec「查詢不存在的活動」要求座位可售狀態與票種列表兩個端點對不存在的活動都回 404（見 `specs/ticket-purchase/spec.md`），所以 `GetTicketTypesHandler` 也 MUST 先呼叫 `IEventRepository.GetByIdAsync(eventId)` 確認活動存在、`null` 時回 `Error.NotFound`，才呼叫 `ITicketTypeRepository.GetByEventIdAsync`；活動存在但還沒建立任何票種，才回傳空列表。

### 6. Controller 結構、授權、模擬付款、回應碼
- `EventsController`（`api/events`，公開）：`GET /`（活動列表）、`GET /{id}/seats`、`GET /{id}/ticket-types`，皆回 200 + 內容，找不到活動回 404（見決策 5）。
- `OrdersController`（`api/orders`，需登入，`[Authorize]` 不需要角色限制，任何已登入會員皆可）：
  - `POST /`：Request Body 為決策 2 的 `PlaceOrderRequest`（`buyerId` 從 `User.GetMemberId()` 取得，不接受請求 Body 傳入，避免冒用他人身份），成功回 **201 + `{ id }`**（`OrderService.PlaceOrderAsync` 回傳 `Result<Guid>`，比照 `AdminEventsController.CreateEvent` 的 `result.ToActionResult(id => StatusCode(201, new { id }))` 慣例）。
  - `POST /{id}/confirm`、`POST /{id}/cancel`：不接受任何請求 Body，成功回 **204**（`OrderService` 這兩個方法回傳 `Result`，直接用既有 `ResultExtensions.ToActionResult(Result)` 即可，它對 `Result.Success()` 回 `NoContentResult`）。
- 非本人操作回 403（`Error.Forbidden`，`ResultExtensions` 既有分類已支援，不新增）；訂單不存在回 404（見決策 2）。
- `POST /api/orders/{id}/confirm` 沿用原設計：不接受任何付款資訊，呼叫即視為付款成功，之後金流增量才會取代。

## Risks / Trade-offs

- **[Risk]** `ReloadAsync` 是這次新增、之前完全沒用過的模式，如果 Cancel 的實作忘記呼叫它，兩個並發 Cancel 中的輸家會誤報操作成功（見決策 3）。這個問題只在「兩個並發同類操作（尤其是兩個並發 Cancel）」時才會出現——如果整合測試只測「Confirm 跟 Cancel 互搶」，即使 `ReloadAsync` 沒有實作或寫錯，測試依然會通過（因為這個組合本來就已經被既有的座位狀態檢查攔住），抓不到問題。→ **Mitigation**：整合測試 MUST 包含「兩個並發 Cancel 操作同一筆訂單」的情境（比照 `ticketing-infrastructure` 對悲觀鎖的測試方式，用兩個並發交易），驗證輸家確實回 Conflict 而非誤報 Success；「Confirm 跟 Cancel 互搶」「兩個並發 Confirm」可以另外測以涵蓋 spec Scenario 或作為補充，**但兩者都已被既有座位狀態檢查攔住、不依賴 `ReloadAsync`，不能拿來取代「兩個並發 Cancel」這個測試**。
- **[Risk]** 座位瀏覽端點需要三次 Repository 查詢（座位、活動、座位圖）才能組出回應，沒有做任何快取或合併查詢。→ **Mitigation**：這是讀取路徑，資料量小（一場活動的座位數是有限的），先求正確、不先做效能優化；如果之後真的是瓶頸，再評估合併查詢或快取。
- **[Risk]** `Orders.BuyerId` 加 FK 指向 `Members` 是跨兩個原本獨立規劃的領域（售票／會員）第一次在資料庫層級產生直接關聯。→ **Mitigation**：這是必然的（訂單本來就需要知道買家是誰），FK 只是把這個既有的邏輯關聯明確化成資料庫約束，沒有新增業務耦合。

## Migration Plan

- 新增 EF Core Migration：`docker compose exec api dotnet ef migrations add AddOrderBuyerId`，只新增 `Orders.BuyerId` 欄位與其 FK，不影響其他既有表。
- 因為 `BuyerId` 是 `NOT NULL` 且目前資料庫裡不會有任何既有的 `Orders` 資料（`ticketing-event-management` 之前完全沒有下單 API，不可能有人下過單），不需要處理既有資料的欄位遷移／預設值問題。這個假設是推論而非既定事實（例如開發過程中可能有人手動塞過測試資料），套用 migration 前 MUST 先執行 `SELECT COUNT(*) FROM "Orders"` 確認資料表確實是空的；如果不是空的，`AddOrderBuyerId` migration 需要改成先補一個預設值（或先清空測試資料）再改成 `NOT NULL`，不能直接套用。
- Rollback：`docker compose exec api dotnet ef database update <上一個 migration>`。

## Open Questions

（無——範圍與併發策略都已確認清楚。）
