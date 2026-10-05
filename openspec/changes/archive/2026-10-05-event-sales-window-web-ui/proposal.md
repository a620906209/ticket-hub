## Why

`event-sales-window`（後端，已合併 968a894）讓活動有販售期間 `[SalesStartAtUtc, SalesEndAtUtc ?? StartAtUtc)`，期間外下單與加入排隊回 409 `SalesNotOpen`／`SalesClosed`。前端目前完全不知道這件事：

- 買家看不到開賣／停售時間。
- 按鈕在期間外仍可按。
- 409 會落入下單的泛用錯誤路徑，**清空買家的選位**，並顯示英文的 `detail`（例如 `Event '…' sales have not started.`）。
- 後台建立活動表單也無法設定販售期間，後端功能等於無法使用。

## What Changes

- **買家活動詳情頁**
  - 顯示販售期間與販售狀態文字（尚未開賣／販售中／已停售）。
  - 期間外預先停用選位、快速選位、送出訂單、加入排隊。
  - 開賣時間一到，不需重新整理即自動解鎖。
  - 未開賣時不載入排隊驗證碼，避免買家在頁面等開賣時驗證碼逾時。
- **409 處理**
  - 下單或加入排隊收到 `SalesNotOpen`／`SalesClosed` 時，顯示中文訊息。
  - 下單時**保留已選座位與計數數量**，不重新載入、不清空。
  - 收到 `SalesClosed` 後，該頁視為已停售（停售不可逆，以伺服器判斷為準）。
- **買家活動列表**：卡片顯示販售狀態文字標籤。
- **後台建立活動表單**
  - 新增「開賣時間」「停售時間」兩個選填的日期時間欄位。
  - 送出前依後端規則做前端檢查：停售時間不晚於活動開始時間、開賣時間早於實際停售時間。
  - 後端仍是最終驗證。
- **後台活動列表**：新增販售期間欄，未設定者顯示「未設定」。
- 前端型別 `EventSummary`／`AdminEventSummary` 補上 `salesStartAtUtc`／`salesEndAtUtc`，`createEvent` 補上兩個參數。
- 不改動任何後端 API。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `buyer-web-ui`：新增「活動頁依販售期間顯示狀態並限制購票操作」需求，涵蓋詳情頁、列表卡片、409 處理與自動解鎖；並修改既有「買家可選位並送出訂單」，把 409 `SalesNotOpen`／`SalesClosed` 列為「下單失敗一律清空」的例外。
- `admin-web-ui`：新增「建立活動表單可設定販售期間，活動列表顯示販售期間」需求。

## 顆粒度評估

依 CLAUDE.md「Change 顆粒度」檢查：影響 2 個 capability（未超過 3 個），tasks 36 項（超過約 30 項警戒線）。評估後**不拆分**，理由：

- 買家端與後台端共用同一組前置變更：`EventSummary`／`AdminEventSummary` 型別、`createEvent` 改為物件參數、`api.generated.ts` 重新產生。拆成兩個 change 時，後到的一方必須等前者合併，或重複修改同一批檔案。
- 後台部分只有表單兩個欄位加列表一欄（tasks 第 5、6 章共 8 項），單獨成一個 change 的審查成本高於收益。
- 超過警戒線的主因是測試任務按 Scenario 逐條拆開（第 3 章 17 項中有 11 項是測試）。審查時 diff 集中在 `EventDetailPage.vue` 與 `EventCreatePage.vue`，不會出現 real-name-verification 那種跨 8 個 capability、需反覆切換帳號驗證的情況。

## Impact

- **前端**：
  - `web/src/types/apiResponses.ts`、`web/src/api/admin.ts`
  - 新增 `web/src/utils/salesWindow.ts`（純函式，與後端判斷規則一致）
  - 新增 `web/src/components/SalesStatusTag.vue`（詳情頁與列表共用）
  - `web/src/pages/buyer/EventDetailPage.vue`、`EventListPage.vue`
  - `web/src/pages/admin/EventCreatePage.vue`、`EventListPage.vue`
  - 上述頁面的 vitest 測試
- **後端**：無變更。依賴 `event-sales-window` 已提供的 API 欄位與 409 title。
- **已知限制**：預先停用使用買家本機時鐘判斷。時鐘偏慢的買家，開賣後最多晚「偏差秒數」才解鎖；時鐘偏快的買家提前送出時，會收到 `SalesNotOpen` 中文訊息。兩者都由後端 409 把關，不會造成錯誤購票。
