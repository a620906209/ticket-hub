## Why

2026-09-30 前端盤點（見 `docs/project-scope.md` 第 8 節「已知前端缺口」）發現：買家訂單頁面只看得到訂單與項目的 GUID，沒有活動名稱、座位、票種；後台訂單列表以買家 GUID 辨識買家。原因是買家與後台的訂單查詢 API 只回傳 Id。本 change 擴充後端回應欄位並在前端顯示。原與 `order-pending-actions` 同屬 `order-ui-gaps`，因本項需要後端變更與較重的查詢設計，拆為獨立 change，**於 `order-pending-actions` 歸檔後進行**。

## What Changes

- 買家訂單查詢 API 擴充回應欄位（**非 breaking**，只新增欄位）：訂單列表摘要與明細新增活動名稱；明細的每筆項目新增座位標示（分區＋座位號碼，純計數項目為 null）與票種名稱（舊訂單 `TicketTypeId` 為 null 時為 null）
- 後台訂單列表 API 擴充回應欄位（**非 breaking**）：新增買家顯示名稱（`DisplayName`），不回傳 Email 等其他個資
- 關聯資料查不到時視為資料不一致，回 500（比照既有 `ORD-DETAIL-004`）
- 前端：「我的訂單」列表、訂單明細、訂單結果頁顯示活動名稱；明細每筆項目顯示票種名稱與座位標示；後台訂單列表以買家顯示名稱取代 GUID

## Capabilities

### New Capabilities
（無）

### Modified Capabilities
- `buyer-order-query`：訂單列表摘要新增活動名稱；訂單明細新增活動名稱，每筆項目新增座位標示與票種名稱；新增查不到關聯資料時的資料不一致處理規則；顯示資訊查詢次數不隨筆數成長
- `order-administration`：訂單列表每筆新增買家顯示名稱；查詢次數不隨筆數成長；買家查不到時的資料不一致處理
- `buyer-web-ui`：結果頁、列表、明細顯示活動名稱；明細項目顯示票種名稱與座位標示
- `admin-web-ui`：訂單列表以買家顯示名稱辨識買家，不顯示 GUID

## Impact

- **後端**：`GetMyOrdersHandler`、`GetMyOrderDetailHandler`、`GetOrdersHandler` 與對應 DTO（`MyOrderSummaryDto`、`MyOrderDetailDto`、`MyOrderItemDto`、`OrderSummaryDto`）；`IEventRepository`／`EventRepository` 新增 `GetByIdsAsync`；`ISeatMapRepository`／`SeatMapRepository` 新增 `GetSeatsByIdsAsync`（明細只查訂單用到的座位，不載入整張座位圖）；新增 `IMemberDisplayNameReader`（Domain）與其 Infrastructure 實作，供後台列表批次取買家顯示名稱；須避免 N+1
- **前端**：`OrderResultPage.vue`、`OrderDetailPage.vue`、`MyOrdersPage.vue`、`AdminOrderListPage.vue`、`types/apiResponses.ts`
- **測試**：後端 Application 單元測試、WebApi 整合測試（Testcontainers），新增查詢計數 interceptor；前端 vitest
- **不影響**：確認／取消訂單 API、`order-administration` 租戶過濾規則、路由守衛規則
