## Why

`ticketing-infrastructure`（持久化）與 `ticketing-event-management`（後台管理，皆已合併 master）讓 Admin 能建立活動資料，但完全沒有買家端的路——沒有瀏覽端點，也沒有任何方式能真的下單。這是「Infrastructure → 後台管理 API → 買家端 API」這條規劃順序的最後一塊：補上已登入會員瀏覽活動/座位、建立訂單、確認訂單、取消訂單的 API，讓整個售票流程第一次能端到端跑起來。

## What Changes

- 新增買家端瀏覽 API（不需登入）：`GET /api/events`（活動列表）、`GET /api/events/{id}/seats`（座位可售狀態，含分區代碼供比對票種）、`GET /api/events/{id}/ticket-types`（票種與價格）。
- 新增訂單 API（需登入）：`POST /api/orders`（建立訂單，選定一組座位+票種配對）、`POST /api/orders/{id}/confirm`（確認，模擬付款成功）、`POST /api/orders/{id}/cancel`（取消）。
- **BREAKING**：`Order` 新增必填的 `BuyerId` 欄位，來自已登入會員的 JWT（沿用既有 `ClaimsPrincipalExtensions.GetMemberId()`）。`CreateOrderHandler.Handle` 簽章新增 `buyerId` 參數。新增資料庫欄位與遷移，`Orders.BuyerId` 加上指向 `Members.Id` 的 FK。
- 新增 `OrderService` 協調服務，把 `ticketing-infrastructure` 已就緒的 Repository/`IUnitOfWork`/`EventSeat` 悲觀鎖跟既有三個 pure Handler（`CreateOrderHandler`/`ConfirmOrderHandler`/`CancelOrderHandler`，來自 `ticketing-core-domain`）串起來。**確認/取消訂單在取得座位鎖之後，會重新讀取訂單最新狀態才繼續**，避免兩筆並發操作（例如同時確認與取消同一筆訂單）因為等鎖期間訂單資料已經過期而互相覆蓋。
- Repository 介面新增三個唯讀查詢方法（`IEventRepository.GetAllAsync`、`IEventSeatRepository.GetByEventIdAsync`、`ITicketTypeRepository.GetByEventIdAsync`），供瀏覽端點使用；`IOrderRepository` 新增 `ReloadAsync`，供 `OrderService` 取得鎖後的最新訂單狀態。
- 確認/取消訂單需驗證操作者是否為訂單本人（`order.BuyerId == 呼叫者`），非本人回傳 403。

本次不包含：後台訂單查看、逾時訂單背景清理——這兩個原本跟這次規劃在同一批但被拆開，等這次買家端穩定後再視需要另開 change。

## Capabilities

### New Capabilities
- `ticket-purchase`：買家端 API，已登入會員瀏覽活動與座位可售狀態、建立訂單、確認訂單、取消訂單。

### Modified Capabilities
- `ticket-ordering`：新增「建立訂單時 MUST 記錄發起訂單的買家身份（`Order.BuyerId`，來自已登入會員）」的需求；原本此能力未涉及任何買家身份概念。

## Impact

- `ProjectC.Domain.Orders.Order`：建構子（含 EF-only 建構子）新增 `BuyerId` 參數與驗證。
- `ProjectC.Application.Orders.CreateOrderHandler`：`Handle` 簽章新增 `buyerId` 參數，建構 `Order` 時一併傳入。
- `ProjectC.Infrastructure`：`OrderConfiguration` 新增 `BuyerId` 欄位對應與指向 `Members` 的 FK；新增 Migration；三個 Repository 新增唯讀查詢方法的實作；`OrderRepository` 新增 `ReloadAsync`。
- 新增 `ProjectC.Application.Orders.OrderService`（協調服務）與對應的瀏覽用 Query Handler。
- 新增 `ProjectC.WebApi.Controllers` 內 `EventsController`（公開瀏覽）與 `OrdersController`（需登入）。
- 依 CLAUDE.md 規則，這次涉及外部輸入（訂單建立的座位/票種選擇）、身份驗證（JWT）、跨 Entity 協調（座位鎖定 + 訂單狀態），實作前需先過 CLAUDE.md「安全強制規則」清單。
