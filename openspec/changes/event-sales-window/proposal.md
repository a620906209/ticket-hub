## Why

`docs/project-scope.md` 第 1 節的主辦方需求「設定開賣時間」從未實作：`Event` 沒有任何販售時間欄位，建立訂單與加入排隊也完全不檢查時間，已開始甚至已結束的活動仍可購票。核心商業目標是「開賣尖峰不當機、不超賣」，但系統裡沒有「開賣」這個時間點，後續的壓測（第 8 節補強項目 ②）也無法模擬開賣瞬間的搶票情境。本 change 是第 8 節「Phase 3 完成後盤點的補強項目 ①」的後端部分。

## What Changes

- `Event` 新增 `SalesStartAtUtc`（開賣時間）與 `SalesEndAtUtc`（停售時間），兩者皆可為 null，建立後不可變更：
  - `SalesStartAtUtc = null` 代表不設開賣下界，也就是建立後立即可售（仍受停售時間限制）
  - `SalesEndAtUtc = null` 代表停售時間沿用活動開始時間 `StartAtUtc`
- 建立活動的管理 API 接受兩個**選填**欄位 `SalesStartAtUtc`、`SalesEndAtUtc`，並驗證「開賣時間早於實際停售時間」與「停售時間不晚於活動開始時間」。**API 相容**：未提供時請求格式與建立行為和本次變更前相同，既有客戶端不需修改。但**業務行為有變更**：未設定 `SalesEndAtUtc` 的活動（不論新舊）自 `StartAtUtc` 起即視為停售，不再能建立訂單或加入排隊，這是本 change 明確引入的行為。前端建立表單在後續 change 才把開賣時間設為必填。
- 建立訂單（`POST /api/orders`）與加入排隊（`POST /api/events/{id}/queue/entries`）時檢查販售期間：
  - 尚未開賣時回 409，`Title = "SalesNotOpen"`
  - 已停售時回 409，`Title = "SalesClosed"`
  - 時間取自既有 `IDateTimeProvider`
- 確認付款與取消訂單**不**檢查販售期間：停售前建立的 Pending 訂單，仍可在原本的持有期限內付款，或逾時後自動取消。
- 公開活動列表與後台活動列表附帶 `SalesStartAtUtc`、`SalesEndAtUtc`（原始值，不含依當下時間推導的販售狀態，避免快取內容隨時間過期），供後續前端 change 使用。
- 資料庫遷移新增兩個可為 null 的欄位，既有活動不回填：`SalesStartAtUtc = null` 不設開賣下界；`SalesEndAtUtc = null` 以 `StartAtUtc` 作為停售時間。
  - 使用者原先選擇的是「以建立時間或極早時間回填」，改用 null 在行為上等價，而且免去回填。
  - 停售時間沿用 `StartAtUtc`，因此**已開始的既有活動遷移後會立即變成已停售**；之後任何未設定 `SalesEndAtUtc` 的活動，在 `StartAtUtc` 起也一律停售。這是預期中的行為修正。

**不在本次範圍**：
- 前端：建立表單欄位、活動頁「尚未開賣／倒數／已停售」狀態，拆到後續 change `event-sales-window-web-ui`
- 開賣前的預先等候室
- 停售後主動清理排隊紀錄
- 活動建立後修改販售期間：活動編輯已列入 Won't

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `event-management`：建立活動可指定販售期間並驗證，建立後不可變更；後台活動列表附帶販售期間；遷移後既有活動 `SalesStartAtUtc = null`（不設開賣下界），`SalesEndAtUtc = null` 時仍以 `StartAtUtc` 作為停售時間
- `ticket-purchase`：建立訂單須在販售期間內，確認付款與取消不受影響；公開活動列表附帶販售期間
- `purchase-queue`：加入排隊須在販售期間內，沒有預先等候室
- `query-caching`：活動列表快取 key 隨回應形狀版本化，改為 `query-cache:events:list:v2`

**拆分評估**：本 change 修改 4 個能力、37 項 tasks，皆超過 CLAUDE.md 門檻（3 個能力／約 30 項），評估後不拆分：
- `query-caching` 的修改只是活動列表快取 key 版本化，是 `EventDto` 新增欄位的直接後果，單獨成 change 無法獨立交付（沒有新欄位就不需要換 key），也不能晚於本 change 部署（否則重疊部署時舊 DTO 會寫入新版本讀取的 key）
- 超出的 tasks 來自把每條情境拆成獨立測試任務，實作任務本身約 15 項；若把測試與實作拆開，任一個 change 都無法獨立驗證
- 前端已拆為後續 change `event-sales-window-web-ui`

## Impact

- **Domain**：`Event` 新增兩個屬性、建構子驗證，以及單一 Entity 可自行判斷的販售狀態方法
- **Application**：
  - `CreateEventRequest`／Validator／Handler
  - `OrderService.PlaceOrderAsync`、`JoinPurchaseQueueHandler`
  - `EventDto`、`AdminEventSummaryDto` 與對應 Handler
  - `ErrorType`／`Error` 新增 `SalesNotOpen`、`SalesClosed`
- **Infrastructure**：`EventConfiguration` 明確對映兩個屬性；新增 EF migration，`Down` 比照 `AddRealNameVerification` 以 DO-block 防護
- **WebApi**：`ResultExtensions` 將兩個新 ErrorType 對映為 409
- **快取**：活動列表的快取內容多兩個欄位，快取 key 改為 `query-cache:events:list:v2`，新版本不會讀到舊形狀的快取內容；後端閘門一律讀資料庫，不受快取影響
- **API 相容性**：新增的是選填欄位與回應欄位，既有客戶端不受影響；新增的 409 `Title` 前端目前會當成一般錯誤顯示，直到後續前端 change 補上專屬處理
