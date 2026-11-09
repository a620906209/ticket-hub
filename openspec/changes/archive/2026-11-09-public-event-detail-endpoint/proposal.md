## Why

公開活動 API 目前只有整包列表 `GET /api/events`，沒有「查單一活動」端點。買家活動詳情頁與 k6 負載測試的販售中核對（`assertEventOnSale`）都是抓整份列表再用 `find` 找單筆。`api-conventions-todo` 第 6 項最後一個待辦是把 `GET /api/events` 改為分頁；分頁後這兩處「整包抓、前端找」會漏掉第一頁以外的活動而壞掉。因此先補單一活動端點並讓兩處改用，列表分頁留給後續 change `public-event-list-pagination`。

## What Changes

- 新增公開端點 `GET /api/events/{id}`：匿名可存取，回應欄位與既有 `EventDto` 相同；活動不存在回 `404` ProblemDetails。內容取自既有的整份活動列表快取（`query-cache:events:list:v2`，未命中時沿用既有的查庫並寫回路徑），不新增快取 key、不新增查詢方法。
- 買家活動詳情頁（`EventDetailPage.vue`）改呼叫 `GET /api/events/{id}`，不再抓整份列表；活動不存在時維持既有「找不到這個活動」畫面。
- `loadtest/lib/admin-api.js` 的 `assertEventOnSale` 改用 `GET /api/events/{id}` 核對。
- 不改動 `GET /api/events` 本身的回應形狀與行為。

### 拆分理由

列表分頁（回應改為 `{items,page,pageSize,totalCount}`、排序、`el-pagination`、快取切頁、慣例文件）與本 change 的消費者改造互相獨立，且合併後會超過 3 個 capability（`event-catalog`、`buyer-web-ui`、`query-caching`）。依 change 顆粒度規則拆為兩個 change，依賴單向：本 change → `public-event-list-pagination`。本 change 合併前不動 `GET /api/events`，避免兩者互相卡住。

### 排除項目

- 列表分頁、排序、篩選已結束活動、依 Organizer 篩選公開列表（屬 `public-event-list-pagination` 或不做）。
- 單一活動專屬快取 key、新增 Entity 欄位或 migration。
- 後台 `GET /api/admin/events/{id}`（本 change 只處理公開端點）。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `event-catalog`：新增公開單一活動查詢的需求（`GET /api/events/{id}`）。
- `buyer-web-ui`：新增活動詳情頁以單一活動 API 載入活動資訊的需求；MODIFIED 三條既有 Requirement 中「詳情頁讀取 `GET /api/events`」的敘述（改為 `GET /api/events/{id}`，列表頁不變）：「買家可瀏覽活動列表與座位可售狀態」、「買家可選位並送出訂單」、「活動頁依販售期間顯示狀態並限制購票操作」。

## Impact

- 後端：`Application/Events/GetEventById`（新 Handler，注入 `GetEventsHandler` 重用快取與映射）、`EventsController` 新增 action、`ApplicationHandlerServiceCollectionExtensions.cs` 註冊新 Handler（Scoped）。
- 前端：`web/src/api/events.ts` 新增 `getEventById`；`EventDetailPage.vue` 的 `loadData`；`EventDetailPage.test.ts`、`orderConfirmationFlow.test.ts` 的 mock 由 `getEvents` 改為 `getEventById`。
- 負載測試：`loadtest/lib/admin-api.js`（`assertEventOnSale`，呼叫端含 `loadtest/fixtures/purchase-flow-ux-event.js`）。
- 文件：`docs/api-conventions-todo.md` 第 6 項在 Change 2 才更新，本 change 不動。
- `query-caching` 能力不修改：單一活動查詢透過既有列表快取，其 key、TTL、失效與 fail-open 行為不變（design 決策 2）。
