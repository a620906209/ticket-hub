## ADDED Requirements

### Requirement: 公開單一活動查詢端點
系統 SHALL 提供匿名可存取的 `GET /api/events/{id}`（`id` 為 GUID），回傳該活動的 `EventDto`（欄位與 `GET /api/events` 列表中的每一筆完全相同，不含後台專用欄位）。活動不存在時 MUST 回傳 `404 Not Found` 的 ProblemDetails，不回傳空內容或 `200`。`id` 不是合法 GUID 時請求在路由層即被拒絕（`404`），不進入 Handler。端點 MUST NOT 要求身份驗證，回應不依呼叫者身份而異。

單一活動的內容 SHALL 取自與 `GET /api/events` 相同的整份活動列表（含其快取），不另設單一活動的快取 key，也不另設單筆查庫路徑：快取命中時不查詢資料庫；未命中時依既有列表路徑查庫、寫回列表快取後取出該筆。因此單一活動內容落後資料庫的時間不超過 `GET /api/events` 既有的快取 TTL 上限，且在列表快取被明確失效（建立活動、開關熱門搶購模式）時同步更新。快取內容落後時，剛建立且尚未出現在快取列表的活動，在 TTL 內可能回傳 `404`（與 `GET /api/events` 列表看不到該活動一致）；「存在的活動回傳 `200`」以快取內容已包含該活動為前提。

#### Scenario: EVT-DETAIL-001 查詢存在的活動
- **WHEN** 匿名呼叫 `GET /api/events/{id}`，`id` 為資料庫中存在且已出現在列表快取內容（或快取未命中而由資料庫取得）的活動
- **THEN** 系統回傳 `200` 與該活動的 `EventDto`，每個欄位值與 `GET /api/events` 列表中同一活動的對應欄位相同

#### Scenario: EVT-DETAIL-002 查詢不存在的活動
- **WHEN** 呼叫 `GET /api/events/{id}`，`id` 為合法 GUID 但資料庫中沒有該活動
- **THEN** 系統回傳 `404` ProblemDetails（`Title` 為既有 NotFound 對應代碼），不回傳活動內容

#### Scenario: EVT-DETAIL-003 id 格式錯誤
- **WHEN** 呼叫 `GET /api/events/not-a-guid`
- **THEN** 系統回傳 `404`，`GetEventsHandler` 與其依賴的 Repository／`IQueryCache` 不被呼叫

#### Scenario: EVT-DETAIL-004 快取命中時不查詢資料庫
- **WHEN** 整份活動列表快取（`query-cache:events:list:v2`）存在且未過期，呼叫 `GET /api/events/{id}`
- **THEN** 系統從快取內容取出該活動回傳，不查詢資料庫

#### Scenario: EVT-DETAIL-005 快取未命中時沿用列表路徑
- **WHEN** 整份活動列表快取不存在，呼叫 `GET /api/events/{id}`
- **THEN** 系統查詢資料庫取得完整活動列表（單次查詢），將完整列表寫入 `query-cache:events:list:v2`，回傳該活動；之後的 `GET /api/events` 命中該快取

#### Scenario: EVT-DETAIL-006 列表快取失效後反映最新值
- **WHEN** 活動的熱門搶購模式被切換並使 `query-cache:events:list:v2` 被明確失效後，呼叫 `GET /api/events/{id}`
- **THEN** 回傳的 `IsQueueModeEnabled` 為切換後的值

#### Scenario: EVT-DETAIL-007 匿名可存取且回應不依身份而異
- **WHEN** 未登入與已登入（不同角色）的呼叫者分別查詢同一活動
- **THEN** 皆回傳 `200`，內容完全相同

#### Scenario: EVT-DETAIL-008 Redis 無法連線時仍可查詢
- **WHEN** Redis 無法連線，呼叫 `GET /api/events/{id}`
- **THEN** 系統依 `query-caching` 既有 fail-open 行為查詢資料庫並回傳該活動（活動不存在仍為 `404`）
