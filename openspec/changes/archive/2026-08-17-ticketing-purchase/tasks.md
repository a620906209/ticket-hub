## 1. Domain：`Order.BuyerId`（BREAKING）

- [x] 1.1 `Order` 公開建構子新增 `Guid buyerId` 參數（驗證 `!= Guid.Empty`，比照既有 `venueId`/`seatMapId` 風格），`BuyerId` 新增為公開唯讀屬性
- [x] 1.2 `Order` 的 EF-only `private` 建構子同步新增 `buyerId` 參數
- [x] 1.3 `ProjectC.Application.Orders.CreateOrderHandler.Handle` 簽章改成 `Handle(Guid buyerId, IReadOnlyList<SeatSelection> selections)`，建構 `Order` 時傳入 `buyerId`；`ProjectC.Domain.Tests`/`ProjectC.Application.Tests` 既有呼叫端同步更新參數

## 2. Infrastructure：持久化與 Repository 擴充

- [x] 2.1 `OrderConfiguration` 新增 `builder.Property(o => o.BuyerId).IsRequired()` 與 `builder.HasOne<Member>().WithMany().HasForeignKey(o => o.BuyerId).OnDelete(DeleteBehavior.Restrict)`（見 design.md 決策 1）
- [x] 2.2 套用前先執行 `SELECT COUNT(*) FROM "Orders"` 確認 dev 資料庫目前沒有既有訂單資料（見 design.md Migration Plan）；產生 Migration：`docker compose exec api dotnet ef migrations add AddOrderBuyerId`，確認 Up/Down 皆正確，套用到 dev 資料庫
- [x] 2.3 `IEventRepository` 新增 `GetAllAsync(CancellationToken cancellationToken)`，`EventRepository` 實作
- [x] 2.4 `IEventSeatRepository` 新增 `GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)`（唯讀，不鎖），`EventSeatRepository` 實作
- [x] 2.5 `ITicketTypeRepository` 新增 `GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)`，`TicketTypeRepository` 實作
- [x] 2.6 `IOrderRepository` 新增 `ReloadAsync(Order order, CancellationToken cancellationToken)`，`OrderRepository` 實作（包裝 `DbContext.Entry(order).ReloadAsync`，見 design.md 決策 3）

## 3. Application：協調服務與查詢 Handler

- [x] 3.1 新增 `PlaceOrderRequest(IReadOnlyList<PlaceOrderSelectionRequest> Selections)` / `PlaceOrderSelectionRequest(Guid EventSeatId, Guid TicketTypeId)` 這兩個 Request DTO（見 design.md 決策 2），與 `PlaceOrderRequestValidator`（`FluentValidation`，比照既有 `CreateSeatMapRequestValidator` 慣例）：`Selections` 非空、每筆的 `EventSeatId`/`TicketTypeId` 皆不可為 `Guid.Empty`、**`EventSeatId` 不可重複**（不是「配對不重複」，同一座位配兩個不同票種仍須被擋下，見 design.md 決策 2 的說明）；透過 `Program.cs` 既有的 `AddValidatorsFromAssemblyContaining` 組件掃描自動註冊，不需額外 DI 註冊
- [x] 3.2 新增 `OrderService`（依賴新增 `ISeatMapRepository`）：
  - `PlaceOrderAsync(buyerId, request, cancellationToken)`：`PlaceOrderRequestValidator` 驗證請求 → 逐一 `ITicketTypeRepository.GetByIdAsync` 載入票種（找不到回 `Error.NotFound`）→ 開交易 → `IEventSeatRepository.GetForUpdateAsync` 鎖座位（回傳數量不足回 `Error.NotFound`）→ **分區比對（見 design.md 決策 2 第 4 點）**：對每個項目先檢查 `TicketType.EventId == EventSeat.EventId`（不符直接回 `Error.Validation`，MUST 在查座位圖之前做）→ 用 `EventSeat.SeatId` 對照 `IEventRepository.GetByIdAsync`/`ISeatMapRepository.GetByIdAsync` 查出的座位圖找出實際 `ZoneCode`（這兩個查詢或座位圖內找不到對應座位樣板，MUST 防禦性回 `Error.NotFound`，不可 null 解參考；比照 `CreateTicketTypeHandler` 對同類情境的既有分類）→ 跟 `TicketType.ZoneCode` 不同則回 `Error.Validation` → 組 `SeatSelection` → 呼叫 `CreateOrderHandler.Handle(buyerId, selections)` → 成功則 `IOrderRepository.Add` + Commit → 回傳 `Result<Guid>`（訂單 Id）
  - `ConfirmOrderAsync(orderId, requestingBuyerId, cancellationToken)`：`IOrderRepository.GetByIdAsync`（`null` 回 `Error.NotFound`，不開交易）→ 驗證本人（非本人回 `Error.Forbidden`，不開交易）→ 開交易 → `GetForUpdateAsync` 鎖訂單內不重複的 `EventSeatId` → **鎖到數量不足 `order.Items` 內不重複 `EventSeatId` 數量，回 `Error.NotFound`（見 design.md 決策 2 第 4 點，理論上不應發生但 Service 層仍須防禦；跟 `ConfirmOrderHandler` 自己對「查不到座位」的既有分類一致）** → **`IOrderRepository.ReloadAsync(order)`（見 design.md 決策 3，不可省略）** → 呼叫 `ConfirmOrderHandler.Handle` → 成功 Commit，回傳 `Result`
  - `CancelOrderAsync(orderId, requestingBuyerId, cancellationToken)`：流程同上，最後呼叫 `CancelOrderHandler.Handle`
- [x] 3.3 新增 `GetEventsHandler`（或等效查詢服務）：`IEventRepository.GetAllAsync` → 回傳活動列表 DTO，欄位 `{ Id, Title, StartAtUtc, VenueId, SeatMapId }`（見 design.md 決策 4）
- [x] 3.4 新增 `GetEventSeatsHandler`：依 design.md 決策 5 的流程（`GetByEventIdAsync` 座位 + `GetByIdAsync` 活動拿 `SeatMapId` + `GetByIdAsync` 座位圖）組出 `{ EventSeatId, ZoneCode, SeatNumber, Status }`，`Status` 用 `EventSeat.GetStatus(now)`（`now` 來自 `IDateTimeProvider`）；`IEventRepository.GetByIdAsync` 回傳 `null`（活動不存在）時 MUST 回 `Error.NotFound`，不可直接存取 `SeatMapId`（見 design.md 決策 5）
- [x] 3.5 新增 `GetTicketTypesHandler`：先 `IEventRepository.GetByIdAsync(eventId)` 確認活動存在（`null` 回 `Error.NotFound`，見 design.md 決策 5），再 `ITicketTypeRepository.GetByEventIdAsync` → 回傳 `{ Id, ZoneCode, Price }` 列表（活動存在但無票種則回空列表）

## 4. WebApi：Controller

- [x] 4.1 新增 `EventsController`（`api/events`，公開端點，不套 `[Authorize]`）：`GET /`、`GET /{id:guid}/seats`、`GET /{id:guid}/ticket-types`
- [x] 4.2 新增 `OrdersController`（`api/orders`，`[Authorize]`）：`POST /`（Body 為 `PlaceOrderRequest`，`buyerId` 從 `User.GetMemberId()` 取得，不接受請求 Body 傳入；成功回 201 + `{ id }`，比照 `AdminEventsController.CreateEvent` 慣例）、`POST /{id:guid}/confirm`、`POST /{id:guid}/cancel`（皆不接受 Body，成功回 204，用既有 `ResultExtensions.ToActionResult(Result)`），皆呼叫 `OrderService`（見 design.md 決策 6）
- [x] 4.3 於 `Program.cs` 註冊 `OrderService`、三個查詢 Handler 為 `AddScoped`（另外也補上先前從未在 DI 註冊過的 `CreateOrderHandler`/`ConfirmOrderHandler`/`CancelOrderHandler`，`OrderService` 依賴它們）

## 5. 測試

- [x] 5.1 `ProjectC.Domain.Tests`：`Order` 建構子缺少 `BuyerId`（`Guid.Empty`）時拒絕建立，對應 spec `ticket-ordering`「建立訂單須記錄買家身份」
- [x] 5.2 `ProjectC.Application.Tests`：`CreateOrderHandler.Handle` 新簽章，驗證建立的 `Order.BuyerId` 正確（既有測試同步更新，不算新增）
- [x] 5.3 `ProjectC.Application.Tests`：`PlaceOrderRequestValidator`（空清單、`EventSeatId`/`TicketTypeId` 為 `Guid.Empty`、**同一個 `EventSeatId` 配兩個不同 `TicketTypeId`**皆應驗證失敗，最後一項對應 design.md 決策 2 特別強調的規則）
- [x] 5.4 `ProjectC.Application.Tests`（用 Fake Repository/UnitOfWork，比照 `ticketing-event-management` 慣例）：`OrderService.PlaceOrderAsync`（成功、座位/票種不存在回 NotFound、**座位分區與票種分區不一致回 Validation**，對應 spec「座位分區與票種分區不一致」Scenario）
- [x] 5.5 `ProjectC.Application.Tests`：`OrderService.ConfirmOrderAsync`/`CancelOrderAsync`（成功、非本人回 Forbidden、**訂單不存在回 NotFound**，對應 spec「確認/取消不存在的訂單」兩個 Scenario；**訂單項目引用的 `EventSeatId` 鎖不到（`GetForUpdateAsync` 回傳數量不足）回 `Error.NotFound`**——實作時發現這跟 `ConfirmOrderHandler` 自己對「查不到座位」的既有分類一致，比 design.md 原寫的 `Error.Conflict` 更能維持 Confirm/Cancel 行為對稱，已改用 `NotFound`，見下方實作偏差說明）
- [x] 5.6 `ProjectC.Infrastructure.Tests`（Testcontainers 整合測試，比照 `GetForUpdateAsyncTests` 的並發測試手法）：驗證 `ConfirmOrderAsync`/`CancelOrderAsync` 的「鎖後重讀」——
  - **主要情境（MUST）**：用兩個並發交易模擬**同時取消同一筆 Pending 訂單兩次**（兩個並發 Cancel），驗證只有一個回傳 Success，另一個依重讀後的最新狀態回 Conflict 而非誤報 Success。這是唯一真正依賴 `ReloadAsync` 才會正確的情境（見 design.md 決策 3）——若 `ReloadAsync` 漏掉或寫錯，這個測試 MUST 失敗
  - **次要情境**：兩個並發交易模擬同時確認與取消同一筆訂單，驗證只有一個成功、另一個被拒絕（這個組合即使沒有 `ReloadAsync` 也已被既有座位狀態檢查攔住，保留這個測試是為了涵蓋 spec Scenario，但不能取代上面的主要情境）
  對應 spec「確認與取消訂單的並發一致性」的兩個 Scenario 與 design.md 決策 3
- [x] 5.7 `ProjectC.WebApi.Tests`（Testcontainers 整合測試）：`EventsControllerTests` 涵蓋 spec「瀏覽活動與座位可售狀態」全部 Scenario（含查詢不存在活動回 404，`/seats` 與 `/ticket-types` 兩個端點都要測）
- [x] 5.8 `ProjectC.WebApi.Tests`：`OrdersControllerTests` 涵蓋 spec「買家需登入」「建立訂單」「確認訂單」「取消訂單」全部 Scenario（含未登入 401、非本人 403、座位/票種不存在、座位衝突、座位分區與票種分區不一致（同活動跨分區、跨活動兩種情況都測）、訂單不存在 404、成功回應碼分別為 201+`{id}`／204）

## 6. 收尾檢查

- [x] 6.1 確認 `ProjectC.Domain.csproj` 未新增任何 `<ProjectReference>`
- [x] 6.2 確認既有的 `ConfirmOrderHandler`/`CancelOrderHandler`（pure Handler 本身）簽章未被改動，只有 `CreateOrderHandler` 因為 `BuyerId` 而改
- [x] 6.3 確認 `OrderService` 的 `ConfirmOrderAsync`/`CancelOrderAsync` 都有呼叫 `ReloadAsync`，沒有漏掉任何一個（見 design.md 決策 3 的風險提示）——兩者共用同一個 `ChangeOrderStatusAsync` 私有方法，`ReloadAsync` 只需要寫一次
- [x] 6.4 執行全部測試（`docker compose exec api dotnet test`），確認通過——全部 211 個測試通過（Domain 54、Application 76、Infrastructure 28、WebApi 53），0 失敗、0 警告
- [x] 6.5 比對 tasks 完成狀況與 `ticket-purchase`、`ticket-ordering`（新增部分）兩份 spec 的全部 19 個 Scenario，確認皆有對應測試——逐條核對完成，19 個 Scenario 皆至少有一個對應測試
- [x] 6.6 主動告知 spec 同步狀態：`ticket-purchase` 是全新能力，archive 時需要建成新的 `openspec/specs/ticket-purchase/spec.md`；`ticket-ordering` 的新增需求要合併進既有的 `openspec/specs/ticket-ordering/spec.md`（已在完成報告中告知，尚未實際執行 archive）
