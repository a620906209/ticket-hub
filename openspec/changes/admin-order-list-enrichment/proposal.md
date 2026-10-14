## Why

2026-10-13 走查時發現後台訂單列表只有「狀態、持有到期時間、買家、操作」四欄，主辦方無法從列表分辨訂單屬於哪個活動、買了幾張、金額多少，也無法依時間找訂單；不是待付款的訂單「持有到期時間」欄是空白，看起來像資料壞掉（見 `docs/todo-2026-10-13.md` 第 2 項）。

## What Changes

- 訂單列表 API（`GET /api/admin/orders`）每筆訂單新增：活動名稱（`EventTitle`）、張數（`TicketCount`）、總金額（`TotalAmount`）、建立時間（`CreatedAtUtc`）；列表依建立時間由新到舊排序。
- `Order` 新增必填、不可變更的建立時間 `CreatedAtUtc`，建立訂單時記錄當下時間；migration 以 `HeldUntilUtc − 10 分鐘` 回填既有訂單（持有時間自第一版起固定 10 分鐘，回填值精確）。
- 後台訂單列表頁新增「訂單編號（Id 前 8 碼）」「活動」「張數」「總金額」「建立時間」欄；非 Pending 訂單的「持有到期時間」欄顯示「—」，不再留白。
- 不改：買家端「我的訂單」、後台訂單詳情頁、保留期限規則（只對 Pending 顯示、10 分鐘，屬設計行為）。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `order-administration`：「查詢所有訂單列表」回傳欄位增加活動名稱、張數、總金額、建立時間，並定義排序與活動查不到時的處理。
- `admin-web-ui`：「主辦方成員可查看目前 Organizer 名下的訂單列表與明細」列表欄位增加，非 Pending 訂單持有到期時間顯示「—」。
- `ticket-ordering`：新增「建立訂單須記錄建立時間」Requirement。

## Change 顆粒度

維持單一 change，不拆分，理由如下：
- 只影響 3 個 capability（`order-administration`、`admin-web-ui`、`ticket-ordering`），tasks 共 26 項，未達 CLAUDE.md「超過 3 個 capability 或約 30 項 tasks」的拆分門檻。
- `CreatedAtUtc` 是 API 排序與列表「建立時間」欄的共同前置。Domain 欄位、migration 回填、API DTO 與列表 UI 單獨交付都無法提供完整的使用者價值：只有欄位沒有畫面，或有畫面卻缺排序依據。
- 唯一可行的拆法是「Domain＋migration」與「API＋UI」兩個 change，但前者單獨上線沒有任何可觀察的行為，只多一輪審查與合併，不降低風險。
- 變更集中在訂單列表這一條路徑，不新增授權邏輯；Organizer 資料隔離與個資不外洩由既有測試守門，並在 tasks 4.0 逐一列出。

## Impact

- Domain：`Order` 建構子新增 `createdAtUtc` 參數（約 28 處呼叫端，多數在測試）。
- Infrastructure：`OrderConfiguration`、新 migration（含回填；Down 僅刪欄）；測試中 raw SQL `INSERT INTO "Orders"` 需補 `CreatedAtUtc` 欄（`ForeignKeyConstraintsTests`、`GetOrderByIdLegacyDataTests`、`GetPaidItemSalesByEventIdAsyncTests`，共 5 處）。
- Application：`CreateOrderHandler`、`GetOrdersHandler`、`OrderSummaryDto`。
- Web：`AdminOrderListPage.vue`、`OrderSummary` 型別。
- API 回應為新增欄位，向後相容；資料庫 schema 變更需先 migrate 再部署新版。
