# Proposal

## Why

公開 `GET /api/events` 每次回傳整份活動列表（命中快取時直接回整份、未命中時 `GetAllAsync` 整表載入），沒有分頁也沒有排序：活動數成長後，單次回應大小無界，且順序由資料庫決定、不確定。`docs/api-conventions-todo.md` 第 6 項把它列為最後一個未套用 `list-pagination` 慣例的列表端點；上一個 change `public-event-detail-endpoint` 已讓詳情頁與負載測試改用單一活動端點，不再依賴「整份列表」，因此現在可以安全分頁。

## What Changes

- `GET /api/events` 套用 `list-pagination` 慣例：查詢參數 `page`／`pageSize`（預設 1／20，上限 10000／100）、回應改為 `{ items, page, pageSize, totalCount }`、越界與非整數回 400 `Validation`、超過最後一頁回 200 與空 `items`。
- 確定性排序：建立時間由新到舊、建立時間為 null（欄位上線前的舊活動）排最後、再依活動 Id 升冪。
- 快取結構不變：仍是單一 key 存「整份有序列表」，在記憶體切頁。排序在快取未命中、轉成 DTO 之前完成，因此快取內容本身就是有序的。快取 key 由 `query-cache:events:list:v2` 升為 `query-cache:events:list:v3`（內容順序改變，避免新版本讀到舊版本或重疊期間舊實例寫入的未排序內容）；失效時機與 TTL 不變。
- `GetEventByIdHandler` 改取「整份有序列表」的內部方法，不經過分頁；單一活動端點行為不變。
- 前端買家活動列表頁加分頁元件（行為與後台活動列表頁一致：固定每頁 20 筆、無選擇器、總數不超過一頁不顯示、頁碼不進網址、丟棄過期回應、空頁重試最後一頁、失敗還原頁碼）；API 層與型別改為分頁回應。
- `api.generated.ts` 在容器內重新產生。
- `docs/api-conventions.md` 第 3 節「列表分頁」把 `GET /api/events` 由「後續 change」移到「已套用」；`docs/api-conventions-todo.md` 第 6 項勾選；刪除已完成的 `docs/public-event-list-pagination-todo.md`。
- **BREAKING**：`GET /api/events` 回應由陣列改為物件。已以 `rg` 核對，前端買家活動列表頁是唯一的資料消費者（負載測試與 README 的 `curl -f /api/events` 只檢查 HTTP 200 作為就緒探測，不解析 body）；前端同一個 change 切換，不保留舊格式。

## Capabilities

### New Capabilities

無。分頁慣例（`list-pagination`）沿用，不修改。

### Modified Capabilities

- `event-catalog`: MODIFIED「公開單一活動查詢端點」（EVT-DETAIL-004～006 的快取 key 由 `v2` 改為 `v3`，其餘不變）；ADDED「公開活動列表分頁」「公開活動列表排序」——`GET /api/events` 的分頁參數、回應結構與排序（現行 spec 沒有任何 `GET /api/events` 列表行為的條文，因此以 ADDED 補上）。
- `query-caching`: MODIFIED 五條 Requirement——快取 key 由 `v2` 升為 `v3`（快取 / 失效 / TTL / 匿名共享 / 版本化各條文與其 Scenario 的 key 名同步）；快取內容改為「整份有序列表」，不再要求與回應內容逐項一致（回應是其中一頁）；版本化規則擴充為「欄位組成或順序語意改變」皆須換 key。
- `real-name-verification`: MODIFIED「部署切換與 migration 回滾不得讓實名資料或實名活動設定靜默失效」——僅快取 key 名由 `v2` 改為 `v3`，行為不變。
- `ticket-purchase`: MODIFIED「公開活動列表附帶是否需實名」——僅快取 key 名由 `v2` 改為 `v3`，行為不變。
- `buyer-web-ui`: MODIFIED「買家可瀏覽活動列表與座位可售狀態」的列表 Scenario（由「顯示目前所有活動」改為「顯示當頁活動」）；ADDED「買家活動列表支援分頁」。

## Impact

- 後端：`GetEventsHandler`（`HandleAsync` 改收 `PageRequest`、回 `Result<PagedResultDto<EventDto>>`；新增回傳整份有序列表的內部方法；`CacheKey` 常數升版）、`GetEventByIdHandler`（改用內部方法）、`EventsController.GetEvents`；`IEventRepository.GetAllAsync` 不改（`PurchaseQueueAdmissionService` 也使用）。
- 前端：`web/src/api/events.ts`、`types/apiResponses.ts`、`pages/buyer/EventListPage.vue` 與對應 Vitest；`api.generated.ts` 重新產生。
- 文件：`docs/api-conventions.md`、`docs/api-conventions-todo.md`；移除 `docs/public-event-list-pagination-todo.md`。
- 既有測試需配合調整：`CreateEventHandlerTests`（寫死 v2 key）、`GetEventsHandlerTests`、`GetEventByIdHandlerTests`、`EventsControllerTests`、`QueryCaching*` 系列測試（含 `QueryCacheTtlSafetyNetTests`、`QueryCacheEventInvalidationOrderingTests`）中的 key 名與回應結構。
- 不影響：資料庫 schema（不需 migration）、認證、限流、後台 `GET /api/admin/events`、`PurchaseQueueAdmissionService`。

## 範圍與切分

- 涉及 5 個 capability（`event-catalog`、`buyer-web-ui`、`query-caching`，另 `ticket-purchase`、`real-name-verification` 只同步 key 名），在 CLAUDE.md 評估拆分的門檻內；預估 task 低於約 30 項，**不拆**。理由：後端回應格式與前端消費是同一個破壞性變更，拆開會有「後端已分頁、前端仍當陣列」的中間狀態使買家列表頁壞掉；快取 key 升版與排序是為了分頁的確定性而存在，不能獨立交付。
- 排除：篩選已結束活動、依開始時間排序、依 Organizer 篩選公開列表、頁碼進網址、`pageSize` 選擇器、單一活動專屬快取、新增 Entity 欄位或 migration、後台 `GET /api/admin/events/{id}`。
- 不做、但已知：買家 `EventListPage` 的 `loadPage` 邏輯與後台活動列表頁、`MyOrdersPage`、`AdminOrderListPage` 重複，本 change 完成後共四份；抽共用 composable 另開 change。
