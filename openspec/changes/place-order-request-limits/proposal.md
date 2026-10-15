# Proposal

## Why

建立訂單 API 的請求沒有系統層級的上限（`docs/project-scope.md` §8「P95 優化」待辦 ②「下單選購筆數無上限、票種逐筆查詢」，2026-10-13 收尾時暫緩，2026-10-16 重新啟動）：

- `PlaceOrderRequestValidator` 只要求 `Selections` 非空、每筆 `Quantity` ≥ 1，沒有筆數與總張數上限。
- 活動的 `MaxTicketsPerOrder` 可以不設定；沒設定時後端完全不限，買家頁（`EventDetailPage.vue`）也視為無限。同一場活動送幾千個座位，會在一筆交易內鎖住幾千個座位。
- 交易前的票種存在性檢查是 `foreach` 逐筆 `GetByIdAsync`，查詢次數隨請求中不同票種數增加；座位已是批次查詢。
- 請求中大量不合格的選購項目，每一項都會產生一則驗證訊息並串進回應的 `detail`，回應大小隨請求放大。

下單是 k6 壓測中最吃緊的路徑，單一請求不應能占住大量鎖或連線時間。

## What Changes

- 建立訂單：請求的選購筆數 MUST ≤ 50，且所有項目購買數量加總 MUST ≤ 50（系統上限，不論活動是否設定 `MaxTicketsPerOrder`），違反回 400，在開啟資料庫連線之前拒絕。未提供購買數量的項目依既有行為視為 1 計入。
- 選購筆數超過上限時只回報筆數錯誤，不再逐項驗證，避免回應隨請求放大。
- `Selections` 為 null 或含 null 元素時回 400，不再因 Validator 存取 null 而回 500。
- 活動設定的 `MaxTicketsPerOrder` 仍照舊檢查；系統上限與活動限購同時適用，實際上限取兩者較小值。
- 建立活動：`MaxTicketsPerOrder` MUST ≤ 50，否則回 400，避免買家頁顯示的限購值大於後端實際可下單的張數。
- 交易前的票種存在性檢查改為一次批次查詢；找不到時的回應（404 與訊息）不變。
- **BREAKING（僅限直接呼叫 API 的客戶端）**：原本可成功的 > 50 張訂單、`MaxTicketsPerOrder` > 50 的建立活動請求，改回 400。前端畫面不改（見 design Non-Goals）。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `ticket-purchase`：「建立訂單時每筆訂單限購張數以購買數量加總計算」加入系統上限 50（筆數與總張數），以及對應 Scenario。
- `event-management`：新增「建立活動時每筆訂單限購張數不得超過系統上限」Requirement。

## Impact

- 後端：`PlaceOrderRequestValidator`、`CreateEventRequestValidator`、新增共用上限常數、`OrderService` 票種存在性檢查、`ITicketTypeRepository`（`GetByIdAsync` 改為 `GetByIdsAsync`）與 `TicketTypeRepository`。
- 測試替身：`ITicketTypeRepository` 的 7 個測試實作（Application `FakeTicketTypeRepository`、Infrastructure／WebApi 的 decorator 與 fault-injection 實作）須跟著改介面。
- 既有資料：其他環境若已有 `MaxTicketsPerOrder` > 50 的活動，讀取不受影響（不加 Domain 守衛），實際下單上限為 50。本機開發 DB 2026-10-16 查過，現有活動限購最大值為 6。
- 前端、API 回應格式、資料庫 schema：不變。
- 前置依賴：請求本文大小限制由前置 change `request-body-size-limits` 處理，須先 merge；本 change 單獨不保護模型繫結前的反序列化成本（見 design「前置依賴」）。
