# Design

## Context

- 動機與範圍見 proposal.md。分頁慣例與共用型別（`PageRequest`／`PageRequestValidator`／`PagedResultDto<T>`，`Application/Common/Paging`）、前端換頁行為已由 `order-list-pagination`、`admin-event-list-pagination` 建立並歸檔，本 change 只引用，不重述。
- 現況：`GetEventsHandler.HandleAsync` 先讀 `query-cache:events:list:v2`；未命中時 `IEventRepository.GetAllAsync`（`ToListAsync`，**沒有 `OrderBy`**）→ 轉 `EventDto` → 寫入快取。`EventDto` 沒有 `CreatedAtUtc`。`GetEventByIdHandler`（上一個 change）包著 `GetEventsHandler` 做 `FirstOrDefault`。`CreateEventHandler`／`SetEventQueueModeHandler` 以 `GetEventsHandler.CacheKey` 失效快取。
- `Event.CreatedAtUtc` 為 nullable（欄位上線前的舊活動為 null）。
- `IEventRepository.GetAllAsync` 還被 `PurchaseQueueAdmissionService` 使用，不可移除或改變語意。
- 買家 `EventListPage.vue` 是 `GET /api/events` 唯一的資料消費者；`loadtest` 與 README 的 `curl -f /api/events` 只當就緒探測（只看 HTTP 200）。

## Goals / Non-Goals

**Goals:**
- 公開列表單次回應大小有上界（`pageSize ≤ 100`），順序確定、逐頁讀取不重複不遺漏。
- 單一活動端點與快取失效行為完全不變。

**Non-Goals:**
- 不減少 Redis 讀取與反序列化「整份列表」的成本（見風險）。
- 不新增索引、migration、Entity 欄位。
- 不抽共用的前端分頁 composable。

## Decisions

### 決策 1：`GetEventsHandler` 分成「取整份有序列表」與「切頁」兩層
- 新增 `GetEventsHandler.GetAllOrderedAsync(ct)`（回傳 `IReadOnlyList<EventDto>`，即現行的快取 cache-aside 邏輯，加上排序）。`HandleAsync(PageRequest, ct)` 改為：`IValidator<PageRequest>` 驗證（失敗回 `Result.Failure(Validation)`，**不碰快取與資料庫**）→ `GetAllOrderedAsync` → 在記憶體以 `Skip/Take` 切頁 → `PagedResultDto<EventDto>`（`totalCount` 為整份列表筆數）。
- `GetEventByIdHandler` 改呼叫 `GetAllOrderedAsync`，不經分頁與驗證，因此活動不論排在第幾頁都找得到（EVT-LIST-006）。
- 介面歸類：沿用 Handler 既有位置，不新增介面。
- 替代方案：另建 `GetEventPageHandler`。不採用——兩個 Handler 都要處理同一個快取 key，會把快取讀寫邏輯重複一份。

### 決策 2：在轉 DTO 之前對 `Event` 排序，不擴充 `EventDto`
未命中時 `events.OrderBy(e => e.CreatedAtUtc == null).ThenByDescending(e => e.CreatedAtUtc).ThenBy(e => e.Id)`（記憶體 LINQ），排序後才轉 `EventDto` 並寫入快取，因此快取內容本身有序，命中路徑不需要排序、也不需要 `CreatedAtUtc`。`false < true` 讓 null 排最後，不依賴任何資料庫的 NULL 預設。`IEventRepository.GetAllAsync` 不改。
- 替代方案 A：`EventDto` 加 `CreatedAtUtc`、讀取時排序。不採用——會擴充公開 DTO 並改變 `GET /api/events/{id}` 回應。
- 替代方案 B：在 Repository 新增有序的 `GetAllOrderedAsync` 用資料庫排序。不採用——`GetAllAsync` 的另一個呼叫端不需要排序；列表整份載入後記憶體排序的成本與既有的整份載入同量級。

### 決策 3：快取 key 升為 `v3`，版本化規則擴充為「欄位或順序語意改變」
排序是新行為：新版本若讀 `v2`，在滾動部署重疊期間會讀到舊版本實例寫入的未排序列表，分頁會因此重複或遺漏。升版後各版本只讀寫自身 key（`query-caching`「活動列表快取 key 隨回應形狀版本化」）。`CacheKey` 常數升版即可，`CreateEventHandler`／`SetEventQueueModeHandler` 因引用常數而自動清除新 key；舊版本 key 由 TTL 自然過期（回滾與重疊期間的陳舊性分析見該 Requirement）。
- 同步：`real-name-verification`「部署切換與 migration 回滾…」Requirement 內提到 `v2` 的句子一併改為 `v3`（delta 已 MODIFIED）。
- 規格命名備註：QC-EVT-VER-004 的 Scenario 名稱沿用「v2 key 寫入完整形狀內容」，因為 OpenSpec 的 MODIFIED 不允許更動既有 Scenario 名稱；內容已改為描述目前版本（v3）。
- 驗證：既有 `QueryCache*` 整合測試（含 `QueryCacheTtlSafetyNetTests`、`QueryCacheEventInvalidationOrderingTests`）寫死的 `v2` 字串改為引用常數或改為 `v3`，QC-EVT-VER-001～005 對應測試。

### 決策 4：Controller 參數繫結沿用後台列表的寫法
`GetEvents(CancellationToken, [FromQuery] int page = PageRequest.DefaultPage, [FromQuery] int pageSize = PageRequest.DefaultPageSize)`，非整數／空值／超出 32 位元範圍由既有模型繫結與全域錯誤處理轉成 400 `Validation`（`list-pagination` 的 LP-VALID-004、008 已定義、`GET /api/admin/events` 已驗證過）；範圍驗證由 `PageRequestValidator`。端點維持匿名，不加 `[Authorize]`。

### 決策 5：不做逐頁快取，整份列表單一 key
分頁後仍以單一 key 存整份列表、在記憶體切頁。替代方案：每個 `(page, pageSize)` 一個 key。不採用——失效時要清除不確定數量的 key，且與 `CreateEventHandler`／`SetEventQueueModeHandler`「清除單一常數 key」的現行機制不相容。代價：每次請求（含命中）仍需從 Redis 讀取並反序列化整份列表（見風險）。

### 決策 6：買家前端沿用後台活動列表頁的換頁行為，自行持有一份
`getEvents(page)` 回傳 `PagedResult<EventSummary>`（沿用既有 `PagedResult<T>`）；`EventListPage.vue` 持有 `currentPage`／`loadedPage`／`pageSize`／`totalCount` 與遞增請求序號，行為（過期回應丟棄、空頁且 `totalCount > 0` 重試一次最後一頁、失敗退回上一個成功頁）與 `admin-event-list-pagination` 決策 6 相同。`loadedAtMs`（販售狀態計算基準）每次成功載入重新取值；訂單建立提示（`placedOrderId`）邏輯不變。分頁元件放在卡片格線下方、`totalCount > pageSize` 才顯示。
- 替代方案：抽 `usePagedList` composable。不採用，理由同 `admin-event-list-pagination` 決策 6，重複份數在 proposal 記錄。

### 決策 7：`api.generated.ts` 與文件
後端完成後在 `web` 容器執行 `npm run generate:api-types`，檢查 diff 只涉及此端點的 `page`／`pageSize` 查詢參數與回應結構。`docs/api-conventions.md` 第 3 節與 `docs/api-conventions-todo.md` 第 6 項更新；刪除 `docs/public-event-list-pagination-todo.md`。

## 安全確認（CLAUDE.md 強制問題）

- **輸入驗證**：`page`／`pageSize` 先經模型繫結（非整數、空值、超出 int 範圍 → 400），再經既有 `PageRequestValidator`（Application 層，FluentValidation）；未拼接進 SQL 或 shell。驗證失敗時不讀快取、不查資料庫。
- **資料庫**：全程沿用 `GetAllAsync`（EF Core）；本 change 不新增查詢，無 raw SQL。N+1：不適用（單一整表查詢）。
- **權限**：端點維持匿名（既有行為，QC-ACCESS-001）；回應內容不依呼叫者身分而異，可安全共用單一快取 key（QC-ACCESS-002）。`pageSize ≤ 100`、`page ≤ 10000` 使回應大小有上界。
- **前端**：活動欄位以 Vue 文字插值顯示，不使用 `v-html`；此為匿名公開頁，請求經既有統一請求層，查詢字串只由整數頁碼組成。

## Risks / Trade-offs

- **[每次請求都讀取並反序列化整份列表（含快取命中時）]** → 快取 payload 隨活動數線性成長；現階段活動數有限、TTL 30 秒、回應已有界。活動數達數千以上再另案（例如把快取拆成有序索引＋單筆）。
- **[破壞性回應變更：重疊期間舊前端讀到物件]** → 本專案前後端同一個 compose 部署，API 與 Web 必須同版本上線。
- **[新舊版本重疊期間 `v3` 與 `v2` 各自陳舊]** → 見決策 3，上限為 `EventListTtlSeconds`。
- **[買家 `loadPage` 邏輯與其他三頁重複]** → proposal 已記錄，另開 change。
- **[分頁中途資料異動可能使列表跨頁重複或遺漏一筆]** → 與其他列表端點相同，列表情境可接受；快取 30 秒內一致。

## Migration Plan

無資料庫變更。部署：API 與 Web 同版本一起上線；回滾即還原整個 change（`v2`／`v3` key 各自由 TTL 過期，無需手動清除）。
