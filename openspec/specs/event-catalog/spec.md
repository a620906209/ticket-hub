# event-catalog Specification

## Purpose
TBD - created by archiving change ticketing-core-domain. Update Purpose after archive.

## Requirements

### Requirement: 建立活動
系統 SHALL 允許建立一場需要選位的表演活動，活動必須包含標題、開始時間、所屬場地，並指定使用場地下的一份座位圖（`SeatMap`）；缺少任一必要欄位時 MUST 拒絕建立。

#### Scenario: 建立活動時提供完整資訊
- **WHEN** 使用者提供標題、開始時間、場地、座位圖並建立活動
- **THEN** 系統成功建立 Event，並可查詢到剛建立的活動

#### Scenario: 建立活動時缺少必要欄位
- **WHEN** 使用者建立活動但未提供標題或開始時間
- **THEN** 系統 MUST 拒絕建立並回報錯誤，不產生 Event

### Requirement: 場地座位圖與座位樣板唯一性
系統 SHALL 允許為場地建立座位圖（`SeatMap`），座位圖由多個座位樣板（`Seat`）組成，每個座位須包含分區代碼與座位編號；同一場地座位圖內，分區代碼加座位編號的組合 MUST 唯一。座位樣板描述場地的物理座位配置，可被多場活動的座位圖重複使用，本身不記錄任何鎖定或售出狀態。

#### Scenario: 建立座位圖時座位編號不重複
- **WHEN** 為場地座位圖新增多個分區代碼與座位編號皆不同的座位
- **THEN** 系統成功建立所有座位

#### Scenario: 建立座位圖時出現重複座位
- **WHEN** 為同一場地座位圖新增分區代碼與座位編號皆相同的座位
- **THEN** 系統 MUST 拒絕該筆座位的建立，並回報座位重複錯誤

### Requirement: 建立活動時建立專屬座位庫存（EventSeat）
系統 SHALL 在建立活動時，依其指定的座位圖，為圖中每一個座位樣板建立一筆專屬於該活動的座位庫存記錄（`EventSeat`），初始狀態皆為 Available。不同活動的 `EventSeat` MUST 彼此獨立，即使兩場活動使用同一份場地座位圖，其中一場活動的座位售出或鎖定 MUST NOT 影響另一場活動的座位可售性。

#### Scenario: 建立活動時產生對應數量的座位庫存
- **WHEN** 建立一場活動並指定一份包含 N 個座位的座位圖
- **THEN** 系統為該活動建立 N 筆 `EventSeat`，狀態皆為 Available

#### Scenario: 同一座位圖被兩場活動使用時庫存互不影響
- **WHEN** 兩場不同活動皆使用同一份場地座位圖建立，其中一場活動的某個 `EventSeat` 被標記為 Sold
- **THEN** 另一場活動對應相同座位樣板的 `EventSeat` 狀態仍為 Available，不受影響

### Requirement: EventSeat 唯一性
系統 SHALL 保證同一活動內，每個座位樣板最多只對應一筆 `EventSeat`（鍵為活動與座位樣板的組合）；此唯一性由建立活動時的座位庫存建立流程本身保證，不依賴事後去重。`EventSeat` MUST 為獨立於 `Event` 的實體，不作為 `Event` 內部集合被整包載入操作；訂單項目（`OrderItem`）MUST 直接關聯 `EventSeat`，而非直接關聯共用的座位樣板 `Seat`。

#### Scenario: 建立活動的座位庫存流程不產生重複記錄
- **WHEN** 依座位圖建立一場活動的 `EventSeat` 庫存
- **THEN** 每個座位樣板在該活動下只對應唯一一筆 `EventSeat`，不存在重複記錄

### Requirement: 票種與票價
系統 SHALL 只允許透過活動本身建立票種（`TicketType`），建立時 MUST 先核對傳入的座位圖確實是該活動使用的座位圖（而非另一場活動的座位圖），再核對票種必須對應該座位圖中的一個分區代碼，且票價必須為大於零的金額。`TicketType` MUST NOT 能繞過活動直接建構，避免用其他活動的座位圖建立出歸屬錯誤的票種。

#### Scenario: 建立票種並設定合法票價
- **WHEN** 為活動建立票種，指定已存在的分區代碼與票價 500
- **THEN** 系統成功建立票種，票價為 500

#### Scenario: 建立票種時票價為零或負數
- **WHEN** 為活動建立票種並指定票價為 0 或負數
- **THEN** 系統 MUST 拒絕建立並回報票價無效錯誤

#### Scenario: 建立票種時對應不存在的分區
- **WHEN** 為活動建立票種，指定的分區代碼不存在於該活動的座位圖中
- **THEN** 系統 MUST 拒絕建立並回報分區不存在錯誤

#### Scenario: 建立票種時傳入其他活動的座位圖
- **WHEN** 為活動 A 建立票種，卻傳入活動 B 使用的座位圖
- **THEN** 系統 MUST 拒絕建立並回報座位圖不屬於此活動的錯誤

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
