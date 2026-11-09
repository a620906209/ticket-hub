# Proposal

## Why

`GET /api/admin/events`（主辦方範圍的活動列表）目前整表載入、沒有排序、無上限：Repository 取回該 Organizer 的全部活動，Handler 再對全部活動查座位與建立者名稱。活動與座位數隨時間成長後，單次請求的記憶體與回應大小無界。`docs/api-conventions-todo.md` 第 6 項已查證此端點風險次高，且 `order-list-pagination` 已定下跨列表分頁慣例（`list-pagination` 能力），本 change 直接引用該慣例。

## What Changes

- `GET /api/admin/events` 套用 `list-pagination` 慣例：查詢參數 `page`／`pageSize`（預設 1／20，上限 10000／100）、回應改為 `{ items, page, pageSize, totalCount }`、越界與非整數回 400 `Validation`、超過最後一頁回 200 與空 `items`。
- 補上確定性排序：建立時間由新到舊、建立時間為 null（建立者／建立時間欄位上線前的舊活動）排最後、再依活動 Id 升冪。
- 分頁、排序、總數都在資料庫端完成；之後只對「當頁」的活動批次查座位統計與建立者顯示名稱，查詢次數不隨活動數成長。主辦方範圍過濾與授權不變，`totalCount` 與 `items` 使用同一個範圍條件。
- 前端後台活動列表頁加分頁元件（行為與兩個訂單列表頁一致）；API 層與型別改為分頁回應。
- 前端「建立票種」表單的活動下拉選單沿用列表頁目前載入的活動，分頁後只列出當頁活動；此限制寫入 spec。
- 負載測試 fixture `loadtest/fixtures/purchase-flow-ux-event.js` 的 `countRetainedEvents` 會呼叫此端點並要求回應為陣列，改為逐頁讀取（`pageSize=100`）後再計數。
- `docs/api-conventions.md` 第 3 節「列表分頁」把 `GET /api/admin/events` 由「後續 change」移到「已套用」；`docs/api-conventions-todo.md` 第 6 項勾選對應子項。
- **BREAKING**：此端點的回應由陣列改為物件。前端與負載測試 fixture 同一個 change 切換，不保留舊格式；已以 `rg` 核對，這兩處是僅有的呼叫端。

## Capabilities

### New Capabilities

無。分頁慣例本身（`list-pagination`）沿用，不修改。

### Modified Capabilities

- `event-management`: MODIFIED「透過管理 API 查詢活動列表時取得建立者與售票狀況統計」——回應改為分頁結構，並定義排序與「統計只針對當頁」的行為。
- `admin-web-ui`: ADDED「後台活動列表支援分頁」——列表頁分頁互動與票種表單活動下拉選單的限制。

## Impact

- 後端：`IEventRepository`（新增分頁查詢並移除 `GetByOrganizerIdAsync`，其唯一正式程式碼呼叫端就是 `GetAdminEventsHandler`）、`EventRepository`、`GetAdminEventsHandler`、`AdminEventsController.GetEvents`；Domain 新增 `EventPage`。
- 前端：`web/src/api/admin.ts`、`types/apiResponses.ts`、`pages/admin/EventListPage.vue` 與對應 Vitest；`api.generated.ts` 重新產生。
- 負載測試：`loadtest/fixtures/purchase-flow-ux-event.js`。
- 文件：`docs/api-conventions.md`、`docs/api-conventions-todo.md`。
- 既有測試需配合調整：`GetAdminEventsHandlerTests`、`AdminEventsControllerTests`、實作 `IEventRepository` 的測試替身（各測試專案的 wrapper／fake）。
- 不影響：資料庫 schema（不需 migration）、認證、限流、公開 `GET /api/events` 及其快取。

## 範圍與切分

- 涉及 2 個 capability、預估 task 約 15 項，低於 CLAUDE.md 評估拆分的門檻（3 個 capability／約 30 項），不拆。
- 排除：公開 `GET /api/events`（整份快取，需獨立設計快取鍵，另開 change）；其餘列表端點；cursor 分頁、自選排序、頁碼同步到網址、`pageSize` 選擇器。
- 不做、但已知：`EventListPage` 的 `loadPage` 邏輯與 `MyOrdersPage`、`AdminOrderListPage` 已重複，本 change 完成後共三份。抽共用 composable 另開 change，避免本次動到已上線的兩個頁面。
- 不做、但已知：建立票種表單的活動下拉選單分頁後只列當頁活動；改為在活動列展開區新增票種（消除此限制）另案處理。
