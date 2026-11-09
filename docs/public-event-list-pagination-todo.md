# 公開活動列表分頁（拆為兩個 change）待辦

> 建立日期：2026-11-09。來源：`docs/api-conventions-todo.md` 第 6 項最後一個待辦（公開 `GET /api/events` 分頁）。完成後移除本文件，並更新 `api-conventions.md` 與 `api-conventions-todo.md`。

## 為什麼拆開

公開列表分頁後，詳情頁（`EventDetailPage.vue`）與 k6 的 `assertEventOnSale` 目前「抓整份列表再 `find`」會漏掉第一頁以外的活動而壞掉。因此先補單一活動端點並讓兩處改用，再做列表分頁。兩者合併會跨 3 個 capability（`event-catalog`、`buyer-web-ui`、`query-caching`），依 change 顆粒度規則拆開，依賴單向：Change 1 → Change 2。

## Change 1：`public-event-detail-endpoint`（進行中）

- 分支：`feature/public-event-detail-endpoint`；提案已寫（`openspec/changes/public-event-detail-endpoint/`），`openspec validate` 通過。
- [ ] `spec-reviewer` 修到 PASS，暫停等使用者交叉審查
- [ ] `/opsx:apply`（TDD、hardener、strict-reviewer + 對抗式 subagent、Opus 交叉審查、真實瀏覽器驗證）
- [ ] `/opsx:archive` → merge 回 master
- 內容：新增匿名 `GET /api/events/{id}`（取自既有整份列表快取，404 ProblemDetails）；詳情頁與 `loadtest/lib/admin-api.js` 改用。

## Change 2：`public-event-list-pagination`（Change 1 merge 後才開）

- 分支：`feature/public-event-list-pagination`（從更新後的 master 開）。
- [ ] 以 `branch-and-propose` 開分支並 `/opsx:propose`
- [ ] `GET /api/events` 套用分頁慣例：`page`／`pageSize` 預設 1／20、上限 10000／100，回應 `{items,page,pageSize,totalCount}`，超出範圍回 400 `Validation`
- [ ] 排序：`CreatedAtUtc` 新到舊、null 排最後、`Id` 升冪；維持單一整份快取 key，於記憶體切頁；失效邏輯不變
- [ ] 買家 `EventListPage.vue` 加 `el-pagination`（總數大於 pageSize 才顯示）；頁碼只存元件狀態、不進 URL；沿用後台列表的防護（捨棄過期回應、末頁空頁重試、失敗還原）
- [ ] 更新 `docs/api-conventions.md` 與 `api-conventions-todo.md` 第 6 項
- 涉及 capability：`event-catalog`、`buyer-web-ui`、`query-caching`。
- 注意：`IEventRepository.GetAllAsync` 也被 `PurchaseQueueAdmissionService` 使用，不可移除。

## 排除項目

篩選已結束活動、依開始時間排序、依 Organizer 篩選公開列表、頁碼進 URL、單一活動專屬快取、新增 Entity 欄位或 migration、後台 `GET /api/admin/events/{id}`。
