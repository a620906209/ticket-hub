# Proposal

## Why

`GET /api/orders`（買家自己的訂單）與 `GET /api/admin/orders`（主辦方範圍的訂單）目前整表載入、無上限，資料量隨時間成長後，單次請求的記憶體與回應大小無界，也能被當成資料匯出端點。`docs/api-conventions-todo.md` 第 6 項（`list-pagination-convention`）已查證這兩個端點加上 `GET /api/admin/events` 風險最高；本 change 先定下跨模組分頁慣例並套用在訂單兩端點，活動端點由後續 change 引用同一慣例。

## What Changes

- 新增跨列表分頁慣例（offset）：查詢參數 `page`（從 1 起，預設 1、上限 10000）、`pageSize`（預設 20、上限 100）；回應由裸陣列改為 `{ items, page, pageSize, totalCount }`；`page`／`pageSize` 越界回 400（逐欄位 `errors`），不靜默截斷；`page` 超過最後一頁回 200 與空 `items`。
- `GET /api/orders` 與 `GET /api/admin/orders` 套用該慣例；分頁、排序、總數都在資料庫端完成，之後才對該頁的訂單批次查活動名稱（與買家顯示名稱），查詢次數維持不隨筆數成長。
- 買家訂單列表補上確定性排序：建立時間由新到舊、建立時間相同時依訂單 Id 升冪（與後台訂單列表既有排序一致）。
- 前端「我的訂單」與後台訂單列表加分頁元件，頁碼只存元件內狀態；API 層與型別同步改為分頁回應。
- `docs/api-conventions.md` 第 3 節「列表分頁」改為本慣例；記錄 `venues`、`organizers/mine`、`admin/organizers` 判定不需要分頁的理由；`/api/admin/events`、`/api/events` 標為後續 change。
- **BREAKING**：上述兩個端點的回應結構由陣列改為物件。前後端同一個 change 切換，不保留舊格式（本專案無外部 API 消費者；負載測試腳本不呼叫這兩個列表端點）。

## Capabilities

### New Capabilities
- `list-pagination`: 列表端點共用的分頁請求參數、驗證、回應結構與防止整表匯出的上限規則。

### Modified Capabilities
- `buyer-order-query`: 買家訂單列表改為分頁回應，並定義確定性排序。
- `order-administration`: 後台訂單列表改為分頁回應（MODIFIED 既有「查詢所有訂單列表」Requirement）；既有排序、Organizer 範圍、批次查詢與資料不一致規則改為對「該頁」成立。
- `buyer-web-ui`: 「我的訂單」列表頁加分頁互動。
- `admin-web-ui`: 後台訂單列表頁加分頁互動。

## Impact

- 後端：`IOrderRepository`（新增分頁查詢，取代 `GetByBuyerIdAsync`／`GetByOrganizerIdAsync` 的列表用途）、`OrderRepository`、`GetMyOrdersHandler`、`GetOrdersHandler`、`OrdersController`、`AdminOrdersController`，新增共用分頁請求驗證與回應型別（Application/Common）。
- 前端：`web/src/api/orders.ts`、`admin.ts`、`types/apiResponses.ts`、`MyOrdersPage.vue`、`AdminOrderListPage.vue` 及對應 Vitest；`api.generated.ts` 重新產生。
- 文件：`docs/api-conventions.md`、`docs/api-conventions-todo.md`。
- 既有測試需配合調整：`FakeOrderRepository`、`GetMyOrdersHandlerTests`、`GetOrdersHandlerTests`、`OrderRepositoryOrganizerScopingTests`、`OrdersControllerTests`、`AdminOrdersControllerTests`、`OrganizerScopingDataCorruptionTests`、`OrderDisplayQueryCountTests`。
- 不影響：資料庫 schema（不需 migration；排序用既有 `CreatedAtUtc` 欄位，是否需要索引見 design.md）、認證、限流。

## 範圍與切分

- 本 change 涉及 5 個 capability（超過 CLAUDE.md 評估門檻 3 個），但其中 `list-pagination` 是慣例本身、兩個 web-ui 各只加一段分頁互動，且與兩個端點在同一條垂直切面上；若再拆開，前後端中間態會出現前端讀不懂新回應的版本。預估 task 約 25 項，低於 30 項門檻，故不再拆。
- 排除（各自獨立 change，依序引用本 change 的慣例）：`GET /api/admin/events`（`admin-event-list-pagination`）、`GET /api/events`（公開、整份快取，需獨立設計快取鍵）。
- 不做：cursor 分頁、呼叫端自選排序、頁碼同步到網址 query、`pageSize` 選擇器。
