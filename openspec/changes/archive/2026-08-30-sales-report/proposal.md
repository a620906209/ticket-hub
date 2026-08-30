## Why

主辦方（Admin）目前只能在活動列表看到座位 Available／Held／Sold 的數量統計（`admin-event-audit-and-sales-status`），無法得知單一活動實際「賣了多少錢」——訂單建立與結帳流程、電子票券、核銷都已完成，唯獨「錢賣了多少」這個最基本的營運資訊還要自己拿資料庫湊。`docs/project-scope.md` 第 2、5 節已將「主辦方銷售報表」列為 Phase 2（Should）項目，Phase 1（Must）已於 2026-08-26 全數完成，現在依開發階段順序進入 Phase 2。

## What Changes

- 新增 Admin 專用的單一活動銷售報表查詢端點：回傳該活動目前的總營收、已售出票券張數（含座位制與計數制票種），以及依票種（`TicketType`）拆分的營收與售出張數明細
- 報表僅統計 `Order.Status = Paid` 的訂單內容（`Pending`／`Cancelled` 不計入銷售額，逾時未付款訂單依既有 `order-administration` 清理規則轉為 `Cancelled` 後自然不再計入）
- 報表為查詢當下的即時彙總快照，不做時間序列／歷史趨勢資料（對應 project-scope「不含歷史趨勢分析」），也不含分頁載入用的圖表資料點
- Admin 活動列表新增「銷售報表」連結，導向該活動的報表頁面；報表頁面顯示總營收、總售出張數，以及依票種列出的營收/售出張數表格
- 查詢對象限定「單一活動」，不提供跨活動彙總（例如「全平台總營收」）——project-scope 定義的角色需求是「主辦方查看自己活動的銷售報表」，跨活動彙總不在本次範疇，且目前系統僅需支援單一主辦方
- 權限沿用既有 Admin-only 模式（比照 `event-management`／`order-administration`），**不**額外檢查活動是否為呼叫者建立——目前系統只有單一 `Admin` 角色、無每個 Admin 對應一個主辦方的擁有權概念，任一 Admin 皆可查詢任一活動的報表；「主辦方查看自己活動」在多租戶架構落地前，於本次範圍內等同「Admin 查看任一活動」，細節與未來擴充方向見 design.md 決策 6

## Capabilities

### New Capabilities
- `sales-report`：Admin 查詢單一活動的銷售彙總報表（總營收、總售出票券數、依票種拆分明細），僅統計已付款訂單，即時查詢無歷史趨勢

### Modified Capabilities
（無——本次為全新查詢能力，不變更既有 `event-management`／`order-administration`／`ticket-ordering` 等既有 Requirement 的行為）

## Impact

- 新增後端：`IOrderRepository` 新增 `GetPaidItemSalesByEventIdAsync` 方法（不新開 Repository 介面，理由與具體查詢設計見 design.md 決策 1）+ EF Core 實作（Infrastructure，從 `Order` 出發 `SelectMany(o => o.Items)` 後 `GroupBy(TicketTypeId)`，單一查詢於資料庫端完成依票種明細＋無法歸類分組＋總營收/總張數的彙總，不經過完整 `Order` 聚合根水合）、`GetEventSalesReportHandler`（Application）、`AdminEventsController` 新增 `GET /api/admin/events/{eventId}/sales-report` action
- 前端：新增 `AdminSalesReportPage.vue`、對應路由（比照既有 Admin 路由命名慣例）、`EventListPage.vue` 新增報表連結、`web/src/api/admin.ts` 新增對應 API 呼叫
- 不影響既有訂單、票種、活動、核銷相關的行為與既有測試；不需要 EF Core migration（純查詢，不新增欄位）
