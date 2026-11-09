## ADDED Requirements

### Requirement: 公開活動列表分頁
`GET /api/events` SHALL 允許匿名存取，並套用 `list-pagination` 能力的分頁慣例（參數 `page`／`pageSize`、預設值、越界回 400 `Validation`、回應 `{ items, page, pageSize, totalCount }`、超過最後一頁回 200 與空 `items`）。`items` 的每個元素 MUST 維持既有的活動欄位（與 `GET /api/events/{id}` 相同），`totalCount` 為全部活動的總筆數。端點 MUST NOT 提供繞過分頁取得全部活動的參數。

#### Scenario: EVT-LIST-001 未帶分頁參數
- **WHEN** 匿名呼叫端呼叫 `GET /api/events`，未帶 `page` 與 `pageSize`，資料庫共有 45 場活動
- **THEN** 系統回應 200，`page` 為 1、`pageSize` 為 20、`totalCount` 為 45，`items` 為排序後的前 20 場活動，每個元素包含既有的活動欄位

#### Scenario: EVT-LIST-002 取得指定頁
- **WHEN** 同上資料，以 `page=3`、`pageSize=20` 呼叫
- **THEN** `items` 為排序後的第 41–45 場活動共 5 筆，`totalCount` 為 45

#### Scenario: EVT-LIST-003 頁碼超過最後一頁
- **WHEN** 同上資料，以 `page=4`、`pageSize=20` 呼叫
- **THEN** 系統回應 200，`items` 為空陣列，`page` 為 4，`totalCount` 為 45

#### Scenario: EVT-LIST-004 沒有任何活動
- **WHEN** 資料庫沒有任何活動，呼叫 `GET /api/events`
- **THEN** 系統回應 200，`items` 為空陣列，`totalCount` 為 0

#### Scenario: EVT-LIST-005 分頁參數越界或格式錯誤
- **WHEN** 呼叫端帶 `pageSize=101`、`page=0`、`page=10001` 或 `page=abc`
- **THEN** 系統回應 400，`title` 為 `Validation`，`errors` 指出對應的 `page` 或 `pageSize` 欄位，不回傳任何活動，也不查詢資料庫與快取

#### Scenario: EVT-LIST-006 分頁不影響單一活動查詢
- **WHEN** 某活動依排序位於第 2 頁之後，匿名呼叫端呼叫 `GET /api/events/{id}`
- **THEN** 系統回應 200 與該活動，與列表分頁無關

### Requirement: 公開活動列表排序
`GET /api/events` 的活動順序 SHALL 為確定性排序：建立時間由新到舊、建立時間為 null 者（建立時間欄位上線前的舊活動）排在最後、建立時間相同或同為 null 者再依活動 Id 升冪。呼叫端 MUST NOT 能自行指定排序欄位。此順序同時是分頁切頁的依據：以相同 `pageSize` 由第 1 頁逐頁讀到最後一頁，期間資料沒有變動時，每場活動恰好出現一次。

#### Scenario: EVT-LIST-ORDER-001 建立時間新到舊
- **WHEN** 資料庫有建立時間分別為 T1 < T2 < T3 的三場活動，呼叫 `GET /api/events`
- **THEN** `items` 的順序為 T3、T2、T1 的活動

#### Scenario: EVT-LIST-ORDER-002 建立時間為 null 者排最後
- **WHEN** 資料庫有一場建立時間為 null 的活動與兩場有建立時間的活動，呼叫 `GET /api/events`
- **THEN** 建立時間為 null 的活動排在所有有建立時間的活動之後

#### Scenario: EVT-LIST-ORDER-003 次要鍵依 Id 升冪
- **WHEN** 資料庫有兩場建立時間相同（或同為 null）的活動，呼叫 `GET /api/events`
- **THEN** 兩場活動依活動 Id 升冪排列，重複呼叫結果順序不變

#### Scenario: EVT-LIST-ORDER-004 逐頁讀取不重複不遺漏
- **WHEN** 呼叫端以 `pageSize=2` 由第 1 頁讀到最後一頁，期間活動沒有異動
- **THEN** 所有頁的 `items` 合併後，每場活動恰好出現一次

## MODIFIED Requirements

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
- **WHEN** 整份活動列表快取（`query-cache:events:list:v3`）存在且未過期，呼叫 `GET /api/events/{id}`
- **THEN** 系統從快取內容取出該活動回傳，不查詢資料庫

#### Scenario: EVT-DETAIL-005 快取未命中時沿用列表路徑
- **WHEN** 整份活動列表快取不存在，呼叫 `GET /api/events/{id}`
- **THEN** 系統查詢資料庫取得完整活動列表（單次查詢），將完整列表寫入 `query-cache:events:list:v3`，回傳該活動；之後的 `GET /api/events` 命中該快取

#### Scenario: EVT-DETAIL-006 列表快取失效後反映最新值
- **WHEN** 活動的熱門搶購模式被切換並使 `query-cache:events:list:v3` 被明確失效後，呼叫 `GET /api/events/{id}`
- **THEN** 回傳的 `IsQueueModeEnabled` 為切換後的值

#### Scenario: EVT-DETAIL-007 匿名可存取且回應不依身份而異
- **WHEN** 未登入與已登入（不同角色）的呼叫者分別查詢同一活動
- **THEN** 皆回傳 `200`，內容完全相同

#### Scenario: EVT-DETAIL-008 Redis 無法連線時仍可查詢
- **WHEN** Redis 無法連線，呼叫 `GET /api/events/{id}`
- **THEN** 系統依 `query-caching` 既有 fail-open 行為查詢資料庫並回傳該活動（活動不存在仍為 `404`）
