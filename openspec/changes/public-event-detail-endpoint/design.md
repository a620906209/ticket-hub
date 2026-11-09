## Context

公開 API 只有整份列表 `GET /api/events`（`GetEventsHandler`，快取 key `query-cache:events:list:v2`，TTL `EventListTtlSeconds`＝30）。`EventDetailPage.vue` 的 `loadData` 與 `loadtest/lib/admin-api.js` 的 `assertEventOnSale` 都靠「抓整份、`find` 一筆」。後續 change `public-event-list-pagination` 要把列表改為分頁，這兩處必須先脫離整份列表。`IEventRepository.GetByIdAsync` 已存在（`GetEventSeatsHandler` 使用）。

## Goals / Non-Goals

**Goals**
- 新增匿名 `GET /api/events/{id}`，回應與列表單筆相同。
- 兩個消費者改用，且行為不變（找不到活動的畫面、販售中核對的失敗訊息語意）。

**Non-Goals**
- 不動 `GET /api/events` 的形狀、排序、快取 key。
- 不新增單一活動快取、Entity 欄位、migration。
- 不處理後台 `GET /api/admin/events/{id}`。

## Decisions

### 決策 1：新增 `GetEventByIdHandler`，注入 `GetEventsHandler`，從整份列表取單筆
- 為何不用 `IEventRepository.GetByIdAsync` 查單筆：會繞過列表快取，高流量詳情頁每次直打資料庫，且與列表快取的陳舊性不一致（同一活動在兩端點看到不同值）。從同一份列表取出可保證「列表與單筆同源、失效同步」，並重用既有映射，不重複 `EventDto` 建構邏輯。
- 代價：單筆查詢需反序列化整份列表（目前活動數量小，30 秒 TTL 下可接受）。Change 2 之後列表仍以整份單一 key 快取，此代價不變；若活動數量成長到值得切單筆 key，另開 change。
- 找不到回 `Result.Failure(Error.NotFound(...))`，由既有 `ToActionResult` 轉為 `404` ProblemDetails，比照 `GetEventSeatsHandler`。
- 介面歸類：未新增介面。Handler 為具體類別；Handler 注入另一個具體 Handler 在本專案只有 `OrderService` 注入 `CreateOrderHandler`／`ConfirmOrderHandler`／`CancelOrderHandler` 一個先例（`OrderService.cs:32-34`），本 change 沿用同樣的具體類別注入，不為單一用途新增抽象（CLAUDE.md Rule 2）。註冊位置為 `ApplicationHandlerServiceCollectionExtensions.cs`，lifetime 為 Scoped，與 `GetEventsHandler`（`AddScoped`，第 63 行）一致，且 `GetEventsHandler` 依賴 Scoped 的 Repository，不可更長。
- 規格歸類：新端點的 Requirement 放 `event-catalog`（`EventDto` 與活動資料的擁有者），而既有公開瀏覽端點的 Requirement 在 `ticket-purchase`「瀏覽活動與座位可售狀態」（TP-BROWSE-001～004）。不放入 `ticket-purchase` 是因為該 Requirement 以「座位可售狀態」為主題，且本 change 不改變其任何既有行為；放 `event-catalog` 避免對 `ticket-purchase` 做 MODIFIED（需整段複製）。代價是公開端點分散在兩個 capability，後續 `public-event-list-pagination` 若修改列表端點，須同時留意兩處。

### 決策 2：`query-caching` 能力不修改
單一活動查詢不新增 key、不改失效與 TTL，行為完全由既有列表快取 Requirement 涵蓋；新行為只在 `event-catalog` 以 EVT-DETAIL-004～008 場景明文化。`QC-ACCESS-001` 所指「`GET /api/events` 與 `ticket-types` 維持匿名」不受影響。`query-caching` 的 QC-ACCESS／QC-FAIL 條文只列舉這兩個端點，新端點的對應保證（匿名、不依身份而異、Redis 失效仍可查）由 EVT-DETAIL-007／008 承擔；QC-ACCESS 的「未來個人化必須重新設計 key」條款對新端點同樣適用，若日後 `EventDto` 加入依呼叫者而異的欄位，必須同時重新設計列表快取 key。

### 決策 3：前端以 `ApiError.status === 404` 區分「不存在」與「載入失敗」
`loadData` 目前以 `find ?? null` 把不存在視為 `event=null`，頁面顯示「找不到這個活動」。改呼叫單筆 API 後，不存在變成 `404` 例外：捕捉 `ApiError` 且 `status === 404` 時設 `event=null`（不設 `errorMessage`），座位與票種仍以 `Promise.all` 並行載入；但單筆活動 `404` 時座位／票種端點同樣回 `404`，因此 `Promise.all` 會以最先拒絕的錯誤結束——實作須用 `Promise.allSettled` 或個別 catch，使活動 `404` 判定不受座位／票種錯誤影響，且活動非 `404` 的失敗仍走「載入活動資訊失敗」。細節由 BW-DETAIL-002／003 測試鎖定。

改用 `allSettled` 後須保留既有「任一請求失敗即顯示載入失敗」的行為：活動成功但座位或票種非 `404` 失敗時，不得顯示活動內容卻無錯誤訊息（回歸風險），由 BW-DETAIL-005 鎖定。

詳情頁會重新呼叫 `loadData` 的路徑只有頁面掛載、下單成功後、下單一般失敗後、下單成功後離頁導覽失敗而留在本頁時的重新同步四處（頁面沒有重新整理按鈕；「手動重新整理」即瀏覽器重新載入頁面），BW-DETAIL-004 以這些路徑為準。分頁後這些路徑若仍走整份列表會壞，因此全部須改用單筆端點。

主 spec `buyer-web-ui` 有三處既有條文明寫詳情頁讀取 `GET /api/events`（「買家可瀏覽活動列表與座位可售狀態」、「買家可選位並送出訂單」的熱門搶購模式判斷、「活動頁依販售期間顯示狀態並限制購票操作」），delta 以 MODIFIED 改寫這三條（只改詳情頁來源，列表頁不變），避免歸檔後主 spec 自相矛盾；同一 Requirement 輪詢段落的「不需另外呼叫活動列表 API 確認」一併改為「活動查詢 API」，因其語意即「不需再呼叫活動資訊端點」。

### 決策 4：負載測試核對改用單筆端點
`assertEventOnSale` 以 `getOk('/api/events/{id}')`；找不到（`404`）時 `getOk` 已以 `fail()` 結束並帶出 `describeFailure`，不再需要 `Array.isArray`／`find`。販售中判斷邏輯不變；失敗訊息格式由「not found in GET /api/events」改為 `describeFailure` 格式（語意相同、文字不同）。另一個呼叫端 `loadtest/fixtures/purchase-flow-ux-event.js` 也透過 `assertEventOnSale` 受影響，一併驗證。

### 已知描述性殘留
主 spec 另有非 SHALL 的描述性文字仍把詳情頁連到列表 API：`real-name-verification/spec.md:160`（「公開活動列表 API 的 isRealNameRequired 可能落後」）、`purchase-queue/spec.md:72`（「不需另外呼叫活動列表 API 確認」）。不構成矛盾，本 change 不改，由 Change 2（列表分頁）一併修正以免歸檔後描述過時。

## 安全確認（CLAUDE.md 觸發：外部輸入、資料庫／快取讀取、前端）

- **輸入驗證**：`id` 由路由 `{id:guid}` 約束；非 GUID 在路由層回 `404`，不進入 Handler（EVT-DETAIL-003，框架保證，比照 `QC-ACCESS-003`）。無字串拼接進 SQL／shell。
- **資料庫**：不新增查詢；仍走既有 `GetAllAsync`（EF Core、參數化）。無 N+1：單次查詢取整份列表。
- **權限**：公開匿名端點，與 `GET /api/events` 相同；回應不含後台欄位、不依身份而異（EVT-DETAIL-007），可共用同一快取。無法被用來取得未授權資料（`EventDto` 本就公開）。
- **前端**：以 Vue 文字插值渲染，不引入 `v-html`；API 呼叫沿用統一攔截器（`authorizedRequest`）。

## Risks / Trade-offs

- 詳情頁與列表同源，單筆內容最長落後 30 秒（與列表一致）；販售期間狀態判斷本來就以此為準，行為不變。
- 決策 3 的 `404` 判定若漏處理，會把「活動不存在」誤顯示為載入失敗；由 BW-DETAIL-002／003 測試防堵。

## Migration Plan

無資料遷移。部署順序無相依：新端點為新增，舊前端仍呼叫列表。
