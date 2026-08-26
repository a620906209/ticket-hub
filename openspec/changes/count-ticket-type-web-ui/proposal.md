## Why

`TicketType.RequiresSeat` 開關已在 `ticket-type-requires-seat`（2026-08-20 歸檔）完成純後端實作，`event-management`／`ticket-ordering`／`ticket-purchase` 三個能力都已支援純計數（不綁座位）票種的建立、瀏覽與下單。但前端目前完全沒有對應入口：Admin 建立票種表單（`EventListPage.vue` → `createTicketType`）只送出 `eventId`／`zoneCode`／`price`，沒有 `RequiresSeat`／`AvailableQuantity` 欄位；買家端活動詳情頁（`EventDetailPage.vue`）的票種只用來對照座位分區，沒有任何「指定數量購買」的操作路徑。這是 `docs/project-scope.md` 第 9 節 Phase 1 Must 盤點中前端 RWD 唯一剩下的缺口——純計數票種（例如演唱會站立區）現況完全無法在前端展示 end-to-end 下單流程。

## What Changes

- Admin 建立票種表單新增「是否綁座位」開關（對應 `RequiresSeat`）：開啟時維持現有分區代碼驗證流程；關閉時改顯示「可售總量」欄位（對應 `AvailableQuantity`，必填正整數），隱藏／不驗證分區代碼是否存在於座位圖
- 買家端活動詳情頁對 `RequiresSeat = false` 的票種，改顯示票種名稱、價格與當下可售總量，並提供「指定購買數量」的輸入與送出操作，取代座位圖選位動作
- 買家下單時，純計數購買動作 SHALL 組成計數項目（`TicketTypeId` + `Quantity`，不含 `EventSeatId`）呼叫既有建立訂單 API；同一次下單如混合座位選購與計數購買，兩者 SHALL 可以合併送出（後端 API 已支援混合項目）
- 每筆訂單限購張數（`Event.MaxTicketsPerOrder`）的前端即時提示邏輯 SHALL 涵蓋計數購買的數量，與既有座位選購的限購提示共用同一套剩餘額度計算
- 區域隨選快速下單（既有 `buyer-web-ui` 能力）維持僅適用於座位選購，不擴及純計數票種——避免與新的計數購買輸入操作互相干擾，此範圍不在本次規劃內

## Capabilities

### New Capabilities
（無）本次僅在既有前端能力上擴充，不新增後端能力

### Modified Capabilities
- `admin-web-ui`：「Admin 可透過介面管理活動與票種」需求新增 `RequiresSeat`／`AvailableQuantity` 欄位與對應驗證情境
- `buyer-web-ui`：「買家可選位並送出訂單」需求擴充為同時支援座位選購與純計數購買兩種操作路徑；「每筆訂單限購張數」相關情境需涵蓋計數購買

## Impact

- `web/src/pages/admin/EventListPage.vue`、`web/src/api/admin.ts`（`createTicketType` 簽章擴充）
- `web/src/pages/buyer/EventDetailPage.vue`、`web/src/api/orders.ts`（若需擴充送出訂單的請求組裝邏輯）
- `web/src/types/apiResponses.ts`（`TicketType` 型別目前只有 `id`／`zoneCode`／`price`，需新增 `requiresSeat`／`availableQuantity` 欄位以對應既有後端回應）
- 對應的 Vitest 測試檔（`EventListPage.test.ts`、`EventDetailPage.test.ts` 或新增）
- 不涉及後端程式碼或資料庫變更（`event-management`／`ticket-ordering`／`ticket-purchase` API 已就緒）
