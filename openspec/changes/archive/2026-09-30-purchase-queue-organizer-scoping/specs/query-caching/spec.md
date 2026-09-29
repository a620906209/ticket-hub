## MODIFIED Requirements

### Requirement: 活動列表快取於相關異動時明確失效
系統 SHALL 在下列既有操作的資料庫交易提交成功後，明確清除 `query-cache:events:list`：建立活動（`CreateEventHandler`）、開關活動的熱門搶購模式（`SetEventQueueModeHandler`）。清除快取的呼叫 MUST NOT 影響觸發它的原操作本身是否成功——原操作的資料庫交易一旦提交成功即視為成功，快取清除是交易提交後的後續動作。

#### Scenario: QC-EVT-INV-001 建立活動後清除活動列表快取
- **WHEN** 已切換至一個 Approved Organizer 的使用者成功建立一場新活動，且 `query-cache:events:list` 快取當時存在
- **THEN** 系統於建立交易提交成功後清除該快取 key，下一次查詢 `GET /api/events` 會重新查詢資料庫並看到新建立的活動

#### Scenario: QC-EVT-INV-002 開關熱門搶購模式後清除活動列表快取
- **WHEN** 已切換至一個 Approved Organizer 的使用者成功開啟或關閉自己名下某活動的熱門搶購模式，且 `query-cache:events:list` 快取當時存在
- **THEN** 系統於該操作的資料庫交易提交成功後清除該快取 key，下一次查詢 `GET /api/events` 會看到更新後的 `IsQueueModeEnabled`

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
- **WHEN** 已切換至一個 Approved Organizer 的使用者建立活動、建立票種或開關熱門搶購模式，或買家的訂單異動觸發庫存扣減/歸還，此時 Redis 無法連線導致快取失效呼叫失敗
- **THEN** 系統記錄 Warning 等級的結構化 log，原操作仍視為成功（依其資料庫交易結果判斷），不因快取失效失敗而回報該操作失敗


### Requirement: 票種列表快取於相關異動時明確失效
系統 SHALL 在下列既有操作的資料庫交易提交成功後，明確清除受影響活動對應的 `query-cache:ticket-types:event:{eventId}`：為活動建立新票種（`CreateTicketTypeHandler`）、純計數票種因訂單建立而扣減庫存（`TicketType.Reserve`）、純計數票種因訂單取消或逾時釋放而歸還庫存（`TicketType.Release`）。只清除實際受影響活動的快取 key，不影響其他活動的票種列表快取。

#### Scenario: QC-TT-INV-001 建立票種後清除該活動的票種列表快取
- **WHEN** 已切換至一個 Approved Organizer 的使用者成功為自己名下活動建立一個新票種，且該活動的票種列表快取當時存在
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

