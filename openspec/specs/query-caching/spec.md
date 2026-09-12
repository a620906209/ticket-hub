## ADDED Requirements

### Requirement: 活動列表查詢採 Redis cache-aside 快取
系統 SHALL 為買家可匿名存取的公開活動列表查詢端點 `GET /api/events`（`EventsController.GetEvents`／`GetEventsHandler`）導入 cache-aside 快取：查詢時先讀取固定 key `query-cache:events:list` 的快取內容，命中時直接回傳快取內容、不查詢資料庫；未命中時查詢資料庫取得完整活動列表，並在回傳前將結果寫入該快取 key。快取內容 MUST 與未快取時資料庫查詢的回應內容一致（欄位、順序皆相同），不因導入快取而改變既有回應格式。本 Requirement MUST NOT 適用於 `AdminEventsController.GetEvents`（`GetAdminEventsHandler`，`GET /api/admin/events`）——這是給 Admin 用的另一個獨立查詢端點，方法名稱雖然相同，但不在本次快取範圍內，不讀寫 `query-cache:events:list`。

#### Scenario: QC-EVT-001 快取未命中時查詢資料庫並寫回快取
- **WHEN** 呼叫 `GET /api/events`，`query-cache:events:list` 快取不存在
- **THEN** 系統查詢資料庫取得活動列表、回傳結果，並將結果寫入該快取 key

#### Scenario: QC-EVT-002 快取命中時直接回傳快取內容
- **WHEN** 呼叫 `GET /api/events`，`query-cache:events:list` 快取存在且未過期
- **THEN** 系統直接回傳快取內容，不查詢資料庫

#### Scenario: QC-EVT-003 活動列表為空時仍寫入快取
- **WHEN** 資料庫目前沒有任何活動，呼叫 `GET /api/events`，`query-cache:events:list` 快取不存在
- **THEN** 系統查詢資料庫得到空列表、回傳空列表，並將空列表寫入該快取 key（不因結果為空而略過寫入或視為錯誤），下一次查詢命中快取直接回傳空列表、不再查詢資料庫

### Requirement: 票種列表查詢採 Redis cache-aside 快取，依活動切分
系統 SHALL 為 `GET /api/events/{id}/ticket-types`（`GetTicketTypesHandler`）導入 cache-aside 快取，快取 key 為 `query-cache:ticket-types:event:{eventId}`，依活動 Id 切分，不同活動的快取彼此獨立。查詢時先讀取對應活動的快取內容，命中時直接回傳、不查詢資料庫；未命中時查詢資料庫取得該活動的完整票種列表（含 `AvailableQuantity`），並在回傳前寫入對應快取 key。活動不存在時 MUST 仍回傳既有的 `404 Not Found`，不因快取層而改變此行為，且不快取該次查詢結果（不存在的活動沒有可快取的內容）。

#### Scenario: QC-TT-001 快取未命中時查詢資料庫並寫回快取
- **WHEN** 呼叫 `GET /api/events/{id}/ticket-types`，該活動的快取 key 不存在
- **THEN** 系統查詢資料庫取得該活動的票種列表、回傳結果，並將結果寫入對應快取 key

#### Scenario: QC-TT-002 快取命中時直接回傳快取內容
- **WHEN** 呼叫 `GET /api/events/{id}/ticket-types`，該活動的快取 key 存在且未過期
- **THEN** 系統直接回傳快取內容，不查詢資料庫

#### Scenario: QC-TT-003 不同活動的快取互不影響
- **WHEN** 活動 A 的票種列表快取已存在，活動 B 首次查詢票種列表
- **THEN** 系統仍查詢資料庫取得活動 B 的票種列表，不受活動 A 快取內容影響，也不影響活動 A 既有的快取

#### Scenario: QC-TT-004 查詢不存在的活動不受快取層影響
- **WHEN** 呼叫 `GET /api/events/{id}/ticket-types`，指定的活動 Id 不存在
- **THEN** 系統回傳 `404 Not Found`，不查詢或寫入任何快取 key

#### Scenario: QC-TT-005 活動存在但票種列表為空時仍寫入快取
- **WHEN** 活動存在但尚未建立任何票種，呼叫 `GET /api/events/{id}/ticket-types`，該活動的快取 key 不存在
- **THEN** 系統查詢資料庫得到空列表、回傳空列表，並將空列表寫入對應快取 key（不因結果為空而略過寫入或視為錯誤），下一次查詢命中快取直接回傳空列表、不再查詢資料庫

### Requirement: 活動列表快取於相關異動時明確失效
系統 SHALL 在下列既有操作的資料庫交易提交成功後，明確清除 `query-cache:events:list`：建立活動（`CreateEventHandler`）、開關活動的熱門搶購模式（`SetEventQueueModeHandler`）。清除快取的呼叫 MUST NOT 影響觸發它的原操作本身是否成功——原操作的資料庫交易一旦提交成功即視為成功，快取清除是交易提交後的後續動作。

#### Scenario: QC-EVT-INV-001 建立活動後清除活動列表快取
- **WHEN** Admin 成功建立一場新活動，且 `query-cache:events:list` 快取當時存在
- **THEN** 系統於建立交易提交成功後清除該快取 key，下一次查詢 `GET /api/events` 會重新查詢資料庫並看到新建立的活動

#### Scenario: QC-EVT-INV-002 開關熱門搶購模式後清除活動列表快取
- **WHEN** Admin 成功開啟或關閉某活動的熱門搶購模式，且 `query-cache:events:list` 快取當時存在
- **THEN** 系統於該操作的資料庫交易提交成功後清除該快取 key，下一次查詢 `GET /api/events` 會看到更新後的 `IsQueueModeEnabled`

### Requirement: 票種列表快取於相關異動時明確失效
系統 SHALL 在下列既有操作的資料庫交易提交成功後，明確清除受影響活動對應的 `query-cache:ticket-types:event:{eventId}`：為活動建立新票種（`CreateTicketTypeHandler`）、純計數票種因訂單建立而扣減庫存（`TicketType.Reserve`）、純計數票種因訂單取消或逾時釋放而歸還庫存（`TicketType.Release`）。只清除實際受影響活動的快取 key，不影響其他活動的票種列表快取。

#### Scenario: QC-TT-INV-001 建立票種後清除該活動的票種列表快取
- **WHEN** Admin 成功為活動建立一個新票種，且該活動的票種列表快取當時存在
- **THEN** 系統於建立交易提交成功後清除該活動對應的快取 key

#### Scenario: QC-TT-INV-002 訂單成功建立扣減庫存後清除該活動的票種列表快取
- **WHEN** 買家成功建立一筆包含純計數票種的訂單，該票種的 `AvailableQuantity` 因此被扣減，且該活動的票種列表快取當時存在
- **THEN** 系統於扣減庫存的資料庫交易提交成功後清除該活動對應的快取 key

#### Scenario: QC-TT-INV-003 訂單取消或逾時歸還庫存後清除該活動的票種列表快取
- **WHEN** 純計數票種的訂單因取消或逾時未付款而歸還 `AvailableQuantity`，且該活動的票種列表快取當時存在
- **THEN** 系統於歸還庫存的資料庫交易提交成功後清除該活動對應的快取 key

#### Scenario: QC-TT-INV-004 只清除受影響活動的快取
- **WHEN** 活動 A 的票種發生扣減庫存的異動，活動 B 的票種列表快取當時也存在
- **THEN** 系統只清除活動 A 對應的快取 key，活動 B 的快取 key 不受影響、維持原有內容直到自身 TTL 到期或自身的異動觸發失效

### Requirement: 快取項目具備 TTL 安全網，且訂有明確的最大陳舊時間上限
系統 SHALL 為 `query-cache:events:list` 與 `query-cache:ticket-types:event:{eventId}` 兩類快取 key 各自設定正數 TTL（透過設定檔 `QueryCache:EventListTtlSeconds`／`QueryCache:TicketTypesTtlSeconds`，非寫死於程式碼），確保即使明確失效的呼叫因程式邏輯疏漏而遺漏，快取內容仍會在 TTL 到期後自動失效，不會無限期地與資料庫實際內容不一致。

預設值：`EventListTtlSeconds = 30`、`TicketTypesTtlSeconds = 10`。活動列表變動頻率低（僅建立活動、開關熱門搶購模式兩種操作），採較長的 30 秒；票種列表含 `AvailableQuantity`，其新鮮度直接影響買家的購買決策判斷，採較短的 10 秒，降低任一失效呼叫遺漏時的最大影響時間。此 TTL 值即為「在明確失效機制正常運作的前提下，快取內容落後資料庫實際內容的最大允許時間上限」——正常情況下失效呼叫會在資料異動當下立即觸發，TTL 只是理論上限，不是預期的一般延遲。

**已知的邊界例外（誠實揭露，非隱藏在設計文件裡的但書）**：資料庫交易提交與快取失效呼叫之間存在一個極短的時間視窗；若另一個並發查詢恰好在這個視窗內把交易提交前讀到的舊資料重新寫入快取（帶著全新的 TTL），該筆快取的實際陳舊時間上限是「原訂 TTL ＋ 這次重新寫入發生的時間點與交易提交時間點之間的差距」，而不是嚴格從交易提交那一刻起算的 TTL。**這個差距沒有本次改動另外設計或強制施加的上限**——它實際上等於一次「Handler 查詢資料庫→寫入快取」的完整耗時，其上限完全取決於既有、與本次改動無關的基礎設施層級限制（例如資料庫查詢本身的逾時設定，本次未新增或調整任何這類限制）；不得宣稱一個本次改動並未實作或測試強制施加的具體數值（例如「毫秒級」）。系統 SHALL 確保這個例外情況下的陳舊時間仍然是**有限**的（不會無限期陳舊、最終一定會過期），MUST NOT 因這個競態而導致快取內容永久停留在舊值——這是本 Requirement 唯一做出的保證，不承諾具體的數值上限。

#### Scenario: QC-TTL-004 交易提交與快取失效之間的競態不會造成無界陳舊
- **WHEN** 一個查詢請求在資料庫交易提交前已讀取到舊資料，且該請求在交易提交後、快取失效呼叫執行之前才把讀到的舊資料寫入快取
- **THEN** 該筆被重新寫入的快取項目仍帶有完整的新 TTL，會在寫入後的 TTL 時間內自然過期，系統不會因為這個時序而讓該 key 無限期地不過期或無限期地陳舊

#### Scenario: QC-TTL-001 快取寫入時帶有設定的 TTL
- **WHEN** 系統因快取未命中而查詢資料庫並寫入快取
- **THEN** 寫入的快取項目 MUST 帶有設定檔中對應的正數 TTL（活動列表為 `EventListTtlSeconds`，票種列表為 `TicketTypesTtlSeconds`）

#### Scenario: QC-TTL-002 TTL 到期後快取自動失效
- **WHEN** 某快取 key 自**最後一次寫入**起經過的時間已超過其設定的 TTL，且期間未被任何明確失效操作清除，**且未發生 QC-TTL-004 所述的競態情況**（本 Scenario 限定在沒有競態的一般情況；競態情況下的陳舊時間保證見 QC-TTL-004，兩者的保證範圍不同，不可互相替代）
- **THEN** 系統視為快取未命中，重新查詢資料庫並寫回新的快取內容，該筆快取自最後一次寫入起的實際陳舊時間不超過其設定的 TTL 上限

#### Scenario: QC-TTL-003 TTL 尚未到期時持續命中
- **WHEN** 某快取 key 自寫入後經過的時間尚未超過其設定的 TTL，且期間未被任何明確失效操作清除
- **THEN** 系統在整個尚未到期的時間範圍內，每次查詢皆視為快取命中，直接回傳快取內容、不重新查詢資料庫，不因時間流逝但尚未達到 TTL 上限而提前視為未命中

### Requirement: TTL 設定值採啟動時 fail-fast 驗證
系統 SHALL 在應用程式啟動時驗證 `QueryCache:EventListTtlSeconds` 與 `QueryCache:TicketTypesTtlSeconds` 皆為正整數；任一值缺漏、無法解析為整數、為 `0` 或負數時，MUST 在應用程式啟動時失敗，不允許以無效設定值繼續運作。此規則的理由：`0` 或負數的 TTL 傳給 Redis 用戶端會在每次快取寫入時同步拋出未被既有 fail-open 機制捕捉的例外（見「Redis 無法連線時，查詢與快取失效皆 fail-open」Requirement——該 Requirement 只處理 `RedisConnectionException`／`RedisTimeoutException`，不包含設定錯誤導致的 `ArgumentException`），會讓每一次快取未命中的查詢都回應失敗，而非單純的效能降級，因此不能沿用「設定錯誤只是效能降級、可安全忽略」的處理方式。

#### Scenario: QC-TTL-CONFIG-001 設定值為正整數時正常啟動
- **WHEN** `EventListTtlSeconds` 與 `TicketTypesTtlSeconds` 皆設定為正整數
- **THEN** 應用程式正常啟動

#### Scenario: QC-TTL-CONFIG-002 設定值為 0、負數或無法解析時啟動失敗
- **WHEN** `EventListTtlSeconds` 或 `TicketTypesTtlSeconds` 任一被設定為 `0`、負數，或設定值無法解析為整數
- **THEN** 應用程式啟動時 MUST 失敗，不得以此設定值繼續運作

#### Scenario: QC-TTL-CONFIG-003 設定值缺漏時啟動失敗
- **WHEN** `EventListTtlSeconds` 或 `TicketTypesTtlSeconds` 任一完全未在設定檔中提供（缺漏，不落回任何預設值）
- **THEN** 應用程式啟動時 MUST 失敗，等同視為 `0`（不給予 C# 層級預設值，缺漏與明確設為 `0` 得到相同的 fail-fast 效果）

### Requirement: Redis 無法連線時，查詢與快取失效皆 fail-open
系統 SHALL 在快取讀取、寫入或失效操作因 Redis 無法連線而失敗時，記錄 Warning 等級的結構化 log，並視為快取未命中或失效已完成（不重試、不拋出例外中斷呼叫端）：查詢操作 MUST 照常查詢資料庫並回傳結果；異動操作（建立活動、建立票種、開關熱門搶購模式、訂單扣減/歸還庫存）MUST NOT 因快取失效呼叫失敗而回報操作本身失敗——這些操作的正確性完全由其資料庫交易保證，快取層故障只降低查詢效能與新鮮度，不影響任何操作的成功與否。

此 fail-open 原則同樣適用於快取內容本身無法反序列化的情況——具體是「不合法 JSON」或「JSON 值與目標型別不相容」導致 `System.Text.Json` 拋出 `JsonException`（`System.Text.Json` 預設對單純新增/缺少/多出欄位這類 DTO 演進是寬容的，不會因此拋出例外，此處不涵蓋那種情況）：查詢操作 SHALL 將此視為快取未命中，記錄 Warning 等級的結構化 log，照常查詢資料庫並回傳結果，不得讓反序列化例外往外拋出。

#### Scenario: QC-FAIL-003 快取內容無法反序列化時查詢照常回傳資料庫結果
- **WHEN** 呼叫 `GET /api/events` 或 `GET /api/events/{id}/ticket-types`，此時對應快取 key 存在，但內容無法反序列化為預期的型別
- **THEN** 系統記錄 Warning 等級的結構化 log，視為快取未命中，照常查詢資料庫並回傳結果，不回報查詢失敗

#### Scenario: QC-FAIL-001 Redis 無法連線時查詢照常回傳資料庫結果
- **WHEN** 呼叫 `GET /api/events` 或 `GET /api/events/{id}/ticket-types`，此時 Redis 無法連線
- **THEN** 系統記錄 Warning 等級的結構化 log，照常查詢資料庫並回傳結果，不回報查詢失敗

#### Scenario: QC-FAIL-002 Redis 無法連線時異動操作不受影響
- **WHEN** Admin 建立活動、建立票種或開關熱門搶購模式，或買家的訂單異動觸發庫存扣減/歸還，此時 Redis 無法連線導致快取失效呼叫失敗
- **THEN** 系統記錄 Warning 等級的結構化 log，原操作仍視為成功（依其資料庫交易結果判斷），不因快取失效失敗而回報該操作失敗

### Requirement: 查詢端點的匿名存取範圍與快取共享安全性維持既有行為
`GET /api/events` 與 `GET /api/events/{id}/ticket-types` 本次改動前後皆維持既有的匿名可存取行為，MUST NOT 因導入快取而新增、收緊或放寬任何身份驗證或授權檢查。兩端點的回應內容（`EventDto`／`TicketTypeDto` 所含欄位）不依呼叫者身份、角色或所屬主辦方而異——回應對所有呼叫者皆相同，因此可安全地在所有呼叫者之間共享同一份快取內容：活動列表使用全站共用的單一 key `query-cache:events:list`；票種列表依活動 Id 切分（`query-cache:ticket-types:event:{eventId}`），同一活動的快取內容對所有呼叫者共用同一份。若未來這兩個端點的回應需要依身份或權限個人化（例如依主辦方顯示不同的管理資訊），MUST 在該次改動中重新設計快取 key（納入身份或權限維度），不得沿用本次的全站/單一活動共用 key 設計；本次改動不處理個人化情境。

`{id:guid}` 路由參數限制（既有行為，非本次改動新增）：`eventId` 不是合法 GUID 格式時，請求在路由層即被拒絕，MUST NOT 進入 `GetTicketTypesHandler`、不觸碰快取或資料庫。

#### Scenario: QC-ACCESS-001 兩端點維持既有的匿名可存取行為
- **WHEN** 未帶任何身份驗證資訊呼叫 `GET /api/events` 或 `GET /api/events/{id}/ticket-types`
- **THEN** 系統正常回應（快取命中或未命中皆同），不因本次導入快取而要求身份驗證

#### Scenario: QC-ACCESS-002 快取內容在所有呼叫者間安全共享
- **WHEN** 兩個不同呼叫者（不論是否登入、角色為何）分別查詢 `GET /api/events`（活動列表）與 `GET /api/events/{id}/ticket-types`（同一活動的票種列表）——**此保證分別適用於這兩個端點,不是驗證其中一個就代表另一個也成立**
- **THEN** 系統回傳的快取內容對兩者完全相同,不因呼叫者不同而回傳不同內容，也不需要為不同呼叫者建立各自獨立的快取；兩個端點各自都要能觀察到這個保證成立

#### Scenario: QC-ACCESS-003 eventId 格式錯誤時請求在路由層被拒絕
- **WHEN** 呼叫 `GET /api/events/{id}/ticket-types`，路徑中的 `id` 不是合法的 GUID 格式
- **THEN** 系統回傳 `404 Not Found`（既有 `{id:guid}` 路由限制行為）——這是 ASP.NET Core 路由層的框架保證：不匹配路由樣板的請求不會解析出 Controller/Action，`GetTicketTypesHandler` 與其依賴的 Repository／`IQueryCache` 因此不可能被呼叫，此為框架機制本身提供的保證，不需要額外的執行期監測手段才能確認
