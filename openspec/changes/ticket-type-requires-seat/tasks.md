## 1. 前置調查

- [ ] 1.1 確認 EF Core 既有 `IEventSeatRepository.GetForUpdateAsync` 的實作方式（`FromSqlInterpolated` + `FOR UPDATE`，或既有慣例），作為新增 `ITicketTypeRepository.GetForUpdateAsync` 的參考範本

## 2. Domain 層：TicketType 支援 RequiresSeat

- [ ] 2.1 `TicketType` 新增 `RequiresSeat`（bool）、`AvailableQuantity`（int?）屬性
- [ ] 2.2 建構邏輯依 `RequiresSeat` 分流驗證（`true`：`ZoneCode` 須存在於座位圖分區、`AvailableQuantity` 須為 null；`false`：`ZoneCode` 免驗證分區存在性、`AvailableQuantity` 須為正整數），對應 design.md 決策 1
- [ ] 2.3 新增 `Reserve(int quantity)` 方法：`AvailableQuantity` 不足時拋 `DomainException`，成功則扣減；新增 `Release(int quantity)` 方法：無條件歸還，對應 design.md 決策 3
- [ ] 2.4 `Event.CreateTicketType` 簽章擴充以支援兩種模式的建立參數（或新增對應的計數模式建立方法），維持既有座位模式呼叫端不需大幅修改
- [ ] 2.5 xUnit 單元測試：`TicketType` 建構子在兩種模式下的驗證規則（對應 event-management spec 新增的「建立純計數票種成功／未提供可售總量／綁座位票種提供可售總量」三個 Scenario）
- [ ] 2.6 xUnit 單元測試：`Reserve`／`Release` 的邊界情況（庫存剛好足夠、不足、歸還後回到原數量）

## 3. Domain 層：OrderItem 支援計數行項

- [ ] 3.1 `OrderItem` 新增 `TicketTypeId`（domain 建構子層要求必填，DB 欄位維持 nullable）、`Quantity`（必填）屬性，`EventSeatId` 改為可為 null
- [ ] 3.2 建構邏輯驗證兩種形狀互斥：座位行項（`EventSeatId` 有值、`Quantity = 1`）、計數行項（`EventSeatId = null`、`Quantity >= 1`），對應 design.md 決策 2
- [ ] 3.3 xUnit 單元測試：兩種合法形狀建構成功、非法組合（例如同時有 `EventSeatId` 又 `Quantity > 1`，或兩者皆空）被拒絕

## 4. Infrastructure：Migration 與 Repository

- [ ] 4.1 EF Core migration：`TicketType` 新增 `RequiresSeat`（`NOT NULL DEFAULT true`）、`AvailableQuantity`（nullable）
- [ ] 4.2 EF Core migration：`OrderItem` 新增 `TicketTypeId`（nullable，既有資料不回填，見 design.md Migration Plan）、`Quantity`（`NOT NULL DEFAULT 1`）、`EventSeatId` 改 nullable
- [ ] 4.3 `TicketTypeConfiguration`／`OrderItemConfiguration`（若無則新增）同步反映上述欄位與 nullable 設定
- [ ] 4.4 `ITicketTypeRepository` 新增 `GetForUpdateAsync(IReadOnlyList<Guid> ticketTypeIds, CancellationToken)`，實作比照 `IEventSeatRepository.GetForUpdateAsync`

## 5. Application 層：建立票種

- [ ] 5.1 `CreateTicketTypeRequest` 新增 `RequiresSeat`（bool）、`AvailableQuantity`（int?）
- [ ] 5.2 `CreateTicketTypeRequestValidator` 依 `RequiresSeat` 分流驗證 `AvailableQuantity`（`true` 時必須為 null，`false` 時必須為正整數），`ZoneCode`／`Price` 規則不變
- [ ] 5.3 `CreateTicketTypeHandler` 依 `RequiresSeat` 分流：`true` 沿用現行「查座位圖、驗證分區存在」邏輯；`false` 跳過座位圖分區驗證，改呼叫新的計數模式建立邏輯
- [ ] 5.4 FluentValidation／Handler 整合測試：對應 event-management spec 新增與既有的全部 Scenario（分區不存在、票價無效、活動不存在、純計數成功、純計數未提供總量、綁座位提供總量）

## 6. Application 層：建立訂單支援計數項目

- [ ] 6.1 `PlaceOrderSelectionRequest` 由 `(Guid EventSeatId, Guid TicketTypeId)` 擴充為 `(Guid? EventSeatId, Guid TicketTypeId, int Quantity)`，對應 design.md 決策 4
- [ ] 6.2 `PlaceOrderRequestValidator` 新增結構層驗證：`Quantity >= 1`、`TicketTypeId` 不可為空
- [ ] 6.3 `SeatSelection` 新增或並存一個計數版 DTO（例如 `QuantitySelection(TicketType TicketType, int Quantity)`），供 `CreateOrderHandler` 處理
- [ ] 6.4 `OrderService.PlaceOrderAsync`：載入 `TicketType` 後，依 `RequiresSeat` 與請求是否帶 `EventSeatId` 交叉驗證一致性（純計數票種指定了座位／綁座位票種未指定座位 MUST 拒絕），對應 ticket-purchase spec 新增的兩個 Scenario
- [ ] 6.5 `OrderService.PlaceOrderAsync`：新增每筆訂單限購張數檢查，依 `Quantity` 加總（座位項目固定 1）與 `Event.MaxTicketsPerOrder` 比較，對應 ticket-purchase spec 新增的「建立訂單時每筆訂單限購張數以購買數量加總計算」Requirement
- [ ] 6.6 `OrderService.PlaceOrderAsync`：計數項目改用 `ITicketTypeRepository.GetForUpdateAsync` 鎖定對應 `TicketType`，交易內完成鎖定
- [ ] 6.7 `CreateOrderHandler.Handle`：擴充處理座位選購與計數選購混合的情況，座位呼叫既有 `EventSeat.Hold`，計數呼叫新增的 `TicketType.Reserve`；任一失敗時，本次已鎖定的座位與已扣減的計數庫存 MUST 全數復原
- [ ] 6.8 xUnit／整合測試：對應 ticket-ordering spec「建立訂單並原子性鎖定座位或扣減票種庫存」新增的全部 Scenario（純計數成功、純計數庫存不足、混合座位與計數項目成功）
- [ ] 6.9 xUnit／整合測試：對應 ticket-purchase spec 新增的限購張數三個情境（純座位超限、純計數超限、混合加總超限、混合加總未超限）

## 7. Application 層：確認訂單與取消訂單支援計數項目

- [ ] 7.1 `OrderService.ChangeOrderStatusAsync`：原本只查、鎖 `EventSeat`，改為依 `order.Items` 分流——座位行項照舊鎖 `EventSeat`，計數行項改鎖對應 `TicketType`（`GetForUpdateAsync`），兩者可能在同一筆訂單內同時發生
- [ ] 7.2 `ConfirmOrderHandler.Handle`：座位項目沿用既有驗證與 `ConfirmSold`；計數項目不重複扣減庫存，僅參與訂單狀態/逾時的整體驗證，對應 ticket-ordering spec「確認含計數項目的訂單不重複扣減庫存」Scenario
- [ ] 7.3 `CancelOrderHandler.Handle`：座位項目沿用既有釋放邏輯（含「已被其他訂單合法售出略過」「本訂單自己售出的不一致狀態拒絕」）；計數項目新增無條件呼叫 `TicketType.Release` 歸還數量，對應 ticket-ordering spec「取消訂單」新增/修改的 Scenario
- [ ] 7.4 xUnit 單元測試：`ConfirmOrderHandler`／`CancelOrderHandler` 針對純計數訂單、混合訂單（座位+計數同時存在）的確認與取消行為
- [ ] 7.5 整合測試：逾時清理背景服務（`OrderService.CancelExpiredOrderAsync` 路徑）對含計數項目的逾時訂單正確歸還庫存，對應 design.md Risks 小節提到的「逾時清理需涵蓋計數行項」

## 8. 收尾

- [ ] 8.1 `dotnet test` 全數通過（含本次新增的 Domain／Application 單元測試與整合測試）
- [ ] 8.2 以 Swagger／`dotnet-httpie` 等方式手動驗證後端 API：建立純計數票種 → 以該票種下單（不指定座位）→ 確認訂單（庫存不再變動）→ 另建一筆訂單後改用取消（庫存歸還）；並驗證同一訂單混合座位與計數項目的建立/確認/取消（本次無前端改動，不透過 claude-in-chrome 瀏覽器驗證）
- [ ] 8.3 同步確認 `event-management`／`ticket-ordering`／`ticket-purchase` 三份主 spec 的既有 Requirement 已依 delta 正確更新（歸檔時同步）
- [ ] 8.4 `docs/project-scope.md` §8／§9 更新：標記「② TicketType.RequiresSeat 開關」已完成，下一步為「③ 電子票券（Ticket entity）+ 核銷 API」
