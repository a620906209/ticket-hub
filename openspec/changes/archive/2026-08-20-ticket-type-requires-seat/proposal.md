## Why

目前 `TicketType` 一律綁定座位圖分區（`ZoneCode` 必須存在於場地座位圖），主辦方無法建立「純計數、不指定座位」的票種（例如站票、自由入場票）。`docs/project-scope.md` 已將此列為 Phase 1 Must 範疇，且是電子票券／核銷 API（`docs/project-scope.md` §8 規劃順序 ③）的前置基礎——出票邏輯需要先知道票種是否綁座位，才能決定出票時是否需要座位資訊。

## What Changes

- `TicketType` 新增 `RequiresSeat`（布林）欄位：`true` 沿用現行「綁座位分區」邏輯；`false` 為純計數模式，改用新增的 `AvailableQuantity`（總量，購票時扣減）取代分區存在性驗證
- 建立票種 API（`POST /api/admin/events/{eventId}/ticket-types`）依 `RequiresSeat` 分流驗證：`true` 沿用現行 `ZoneCode` 驗證；`false` 改驗證 `AvailableQuantity` 為正整數，不驗證 `ZoneCode`
- 建立訂單 API（`POST /api/orders`）新增「純計數選購」路徑：買家可直接以 `TicketTypeId + Quantity` 下單，不需先取得 `EventSeatId`
- 純計數票種的庫存扣減沿用既有座位鎖定同一套**悲觀交易鎖**模式（`SELECT ... FOR UPDATE` 鎖 `TicketType` 列 → 記憶體內域方法檢查/扣減 → 交易提交時由 EF Core 寫回），不額外引入新的並發控制風格；因為每次扣減只鎖單一 `TicketType` 資料列，不涉及多列鎖定，沒有既有座位鎖定需要「固定順序取鎖」防死鎖的疑慮
- `OrderItem` 需能表達「單一座位」與「無座位、含購買數量」兩種行項：新增 `TicketTypeId`（兩種模式皆必填，計數模式確認/取消時需要靠它找回對應 `TicketType` 才能歸還/確認庫存）、`EventSeatId` 改為可為 null（僅座位模式填寫）、新增 `Quantity`（座位模式固定為 1，計數模式為實際購買張數，一個票種一張 `OrderItem`、不逐張展開）
- 訂單確認／取消／逾時清理流程，需同步支援計數模式行項的釋放與確認（座位模式釋放 `EventSeat`，計數模式歸還 `AvailableQuantity`）
- 既有「已核准/已測試」的座位模式行為（悲觀鎖、Held/Sold 狀態機、確認/取消規則）**不變更**，本次為新增分流，非取代

## Capabilities

### New Capabilities
（無，本次為既有能力擴充，不新增獨立 capability）

### Modified Capabilities
- `event-management`：「透過管理 API 建立活動與票種」新增 `RequiresSeat` 分流的建立票種 Requirement／Scenario
- `ticket-ordering`：「訂單建立」「確認訂單」「取消訂單，統一處理主動取消與逾時清理」三個 Requirement，需納入純計數行項的鎖定/釋放/確認規則
- `ticket-purchase`：「透過 API 建立訂單並鎖定座位」Requirement 需支援計數模式的請求格式與驗證規則

## Impact

- **Domain**：`TicketType`（新增 `RequiresSeat`/`AvailableQuantity`，建構邏輯分流）、`OrderItem`（`EventSeatId` 改為可為 null，新增 `Quantity`）、`Order`（建立/確認/取消邏輯需同時處理兩種行項）
- **Application**：`CreateTicketTypeHandler`/`Validator`、`CreateOrderHandler`、`ConfirmOrderHandler`、`CancelOrderHandler`、`OrderService`（逾時清理）、`SeatSelection`（需擴充或新增對應的計數選購 DTO）
- **Infrastructure**：EF Core migration（`TicketType` 新增兩欄、`OrderItem` 新增 `TicketTypeId`、`EventSeatId` 改 nullable、新增 `Quantity`）、`ITicketTypeRepository` 新增比照 `IEventSeatRepository.GetForUpdateAsync` 的悲觀鎖查詢方法
- **WebApi**：`AdminEventsController.CreateTicketType`、`OrdersController.PlaceOrder` 的請求/回應格式
- **前端（`web/`）**：不在本次範疇內。建立票種表單依 `RequiresSeat` 切換欄位、買家購票頁面支援計數模式「選數量」UI，待本次後端提案完成並歸檔後，另開新的 OpenSpec 提案處理（決策依據：與 `docs/project-scope.md` §8「先調整地基、後蓋新功能」策略一致，且上一次 `IPaymentGateway` 提案也採純後端範疇）
