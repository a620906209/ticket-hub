## MODIFIED Requirements

### Requirement: 透過管理 API 建立活動與票種
系統 SHALL 提供已切換至一個 Approved Organizer 的使用者建立 `Event`（指定場地與座位圖）與 `TicketType`（指定分區代碼與票價，並指定是否綁座位 `RequiresSeat`）的端點，建立規則遵循既有 `event-catalog` 能力的規範（包含活動建立時自動產生對應 `EventSeat` 等）。建立前 MUST 先確認引用的場地／座位圖／活動存在，不存在時 MUST 拒絕並回報找不到對應資源。建立活動時，若指定的座位圖存在但不屬於指定的場地，MUST 視同找不到座位圖，拒絕建立（不得建立場地與座位圖不對應的活動）。建立活動成功時，系統 SHALL 記錄呼叫端當下的登入身份（建立者）、當下時間（建立時間），以及呼叫端 Access Token 中的 `OrganizerId`（該活動所屬的主辦方）；建立者、建立時間、`OrganizerId` 三者皆由後端依 JWT 解析取得，不接受前端在建立活動的請求內容中指定或覆寫。建立票種時，該票種歸屬的 Organizer MUST 與其所屬 Event 的 `OrganizerId` 一致（票種透過 Event 間接歸戶，不需另外指定）；系統 SHALL 在確認活動存在後，額外核對該活動的 `OrganizerId` 是否等於呼叫端 Access Token 中的 `OrganizerId`，不一致時 MUST 視同活動不存在（找不到），不得回傳 403、不得建立任何票種——避免已核准的 Organizer 成員透過猜測或枚舉活動 Id，對其他 Organizer 名下的既有活動新增票種。

建立票種時，驗證規則依 `RequiresSeat` 分流：
- `RequiresSeat = true`（綁座位）：`ZoneCode` MUST 存在於該活動座位圖的分區中，不接受 `AvailableQuantity`
- `RequiresSeat = false`（純計數）：`ZoneCode` 僅作為票種顯示名稱，MUST NOT 驗證是否存在於座位圖分區中；`AvailableQuantity` MUST 為必填且為正整數

建立活動時，`StartAtUtc` 經請求反序列化後的 `DateTime.Kind` MUST 為 `Utc`，不論是否提供販售時間欄位。本系統的請求反序列化只對結尾為 `Z` 的時間字串給 `Utc`；不帶時區的字串（`Unspecified`）與帶任何偏移的字串（`Local`，包含 `+08:00`、`+00:00`）皆不符合。下列 Scenario 以這些字串作為規範性範例，執行環境的反序列化行為若改變，對應測試 MUST 失敗。`StartAtUtc` MUST NOT 為 `9999-12-31T23:59:59.9999999Z`（`DateTime.MaxValue`），理由：資料庫驅動把此值存成 `infinity`，讀回時不再是 UTC，前端會把不帶 `Z` 的時間當成本地時間解讀。違反任一規則時系統 MUST 回傳 400，錯誤說明（ProblemDetails 的 `detail`）MUST 包含對應規則的訊息：非 UTC 為 `StartAtUtc must be a UTC time (with 'Z' designator).`，最大值為 `StartAtUtc must not be the maximum date.`；MUST NOT 回傳 500，且不建立活動與任何 `EventSeat`。本規則生效前已建立、`StartAtUtc` 為一般 UTC 時間的活動，MUST 仍可正常查詢。

請求未提供 `RequiresSeat` 時，系統 MUST 視為 `true`（綁座位），維持本次變更前既有客戶端（未帶此欄位）的既有建立票種行為不受影響。

#### Scenario: EVT-CREATE-001 建立活動成功並自動產生座位庫存
- **WHEN** 已切換至 Approved Organizer 的使用者提供標題、開始時間、場地、座位圖建立活動
- **THEN** 系統成功建立活動，並依座位圖為每個座位樣板建立對應的 `EventSeat`（狀態皆為 Available）

#### Scenario: EVT-CREATE-002 建立活動成功記錄建立者、建立時間與所屬 Organizer
- **WHEN** 已切換至 Approved Organizer 的使用者成功建立活動
- **THEN** 系統記錄的建立者 MUST 是該次請求實際使用的登入身份，建立時間 MUST 是系統當下時間，`OrganizerId` MUST 是該次請求 Access Token 中的 `OrganizerId`，皆不得為空

#### Scenario: EVT-CREATE-003 建立活動缺少必要欄位
- **WHEN** 已切換至 Approved Organizer 的使用者建立活動時未提供標題或開始時間
- **THEN** 系統 MUST 拒絕建立並回報錯誤

#### Scenario: EVT-CREATE-004 建立活動時場地或座位圖不存在
- **WHEN** 已切換至 Approved Organizer 的使用者建立活動時指定不存在的場地或座位圖
- **THEN** 系統 MUST 拒絕建立並回報找不到對應資源，不建立活動也不產生 EventSeat

#### Scenario: EVT-CREATE-005 建立活動時場地與座位圖不對應
- **WHEN** 已切換至 Approved Organizer 的使用者建立活動時指定的場地與座位圖都存在，但座位圖實際屬於另一個場地
- **THEN** 系統 MUST 拒絕建立並回報找不到對應資源，不建立活動也不產生 EventSeat

#### Scenario: EVT-TICKET-001 建立票種時票價無效
- **WHEN** 已切換至 Approved Organizer 的使用者為活動建立票種並指定票價為 0 或負數
- **THEN** 系統 MUST 拒絕建立並回報票價無效錯誤

#### Scenario: EVT-TICKET-002 建立綁座位票種時對應不存在的分區
- **WHEN** 已切換至 Approved Organizer 的使用者建立 `RequiresSeat = true` 的票種，指定的分區代碼不存在於該活動的座位圖中
- **THEN** 系統 MUST 拒絕建立並回報分區不存在錯誤

#### Scenario: EVT-TICKET-003 建立票種時活動不存在
- **WHEN** 已切換至 Approved Organizer 的使用者對不存在的活動建立票種
- **THEN** 系統 MUST 拒絕建立並回報找不到活動

#### Scenario: EVT-TICKET-004 建立票種時活動屬於其他 Organizer
- **WHEN** 已切換至 Organizer A 的使用者對存在、但 `OrganizerId` 屬於 Organizer B 的活動建立票種
- **THEN** 系統 MUST 拒絕建立並回報與「活動不存在」相同的找不到錯誤，不得回傳 403，不得建立任何票種

#### Scenario: EVT-TICKET-005 建立純計數票種成功
- **WHEN** 已切換至 Approved Organizer 的使用者建立 `RequiresSeat = false` 的票種，指定顯示名稱、票價與正整數的可售總量 `AvailableQuantity`
- **THEN** 系統成功建立票種，不驗證顯示名稱是否對應座位圖分區，票種初始可售數量為指定的 `AvailableQuantity`

#### Scenario: EVT-TICKET-006 建立純計數票種時未提供可售總量
- **WHEN** 已切換至 Approved Organizer 的使用者建立 `RequiresSeat = false` 的票種但未提供 `AvailableQuantity`，或提供 0 或負數
- **THEN** 系統 MUST 拒絕建立並回報可售總量無效錯誤

#### Scenario: EVT-TICKET-007 建立綁座位票種時提供可售總量
- **WHEN** 已切換至 Approved Organizer 的使用者建立 `RequiresSeat = true` 的票種，卻同時提供 `AvailableQuantity`
- **THEN** 系統 MUST 拒絕建立並回報驗證錯誤，綁座位票種的庫存數量須由座位圖決定，不接受額外指定總量

#### Scenario: EVT-TICKET-008 建立票種時未提供 RequiresSeat（既有客戶端相容）
- **WHEN** 已切換至 Approved Organizer 的使用者呼叫建立票種端點，請求內容比照本次變更前的既有格式，未包含 `RequiresSeat` 欄位
- **THEN** 系統 MUST 視為 `RequiresSeat = true`（綁座位），依既有的分區存在性規則驗證，行為與本次變更前完全一致

#### Scenario: EVT-CREATE-006 開始時間不帶時區被拒
- **WHEN** 已切換至 Approved Organizer 的使用者建立活動，`StartAtUtc` 為不帶時區的時間字串（例如 `2026-12-01T10:00:00`，反序列化後 Kind 為 `Unspecified`），其餘欄位合法；分別測試不提供販售時間欄位，以及提供帶 `Z` 的合法 `SalesStartAtUtc`／`SalesEndAtUtc` 兩種情況
- **THEN** 系統回傳 400，錯誤說明包含 `StartAtUtc must be a UTC time (with 'Z' designator).`，不建立活動與任何 `EventSeat`；之後匿名查詢活動列表仍成功

#### Scenario: EVT-CREATE-007 開始時間帶偏移被拒
- **WHEN** 已切換至 Approved Organizer 的使用者建立活動，`StartAtUtc` 為帶偏移的時間字串，分別測試 `2026-12-01T10:00:00+08:00` 與 `2026-12-01T10:00:00+00:00`（後者時間值等同 UTC，但反序列化後 Kind 為 `Local`），其餘欄位合法
- **THEN** 系統回傳 400，錯誤說明包含 `StartAtUtc must be a UTC time (with 'Z' designator).`，不建立活動與任何 `EventSeat`

#### Scenario: EVT-CREATE-008 開始時間為最大日期被拒
- **WHEN** 已切換至 Approved Organizer 的使用者建立活動，`StartAtUtc` 為 `9999-12-31T23:59:59.9999999Z`，其餘欄位合法
- **THEN** 系統回傳 400，錯誤說明包含 `StartAtUtc must not be the maximum date.`，不建立活動與任何 `EventSeat`；之後匿名查詢活動列表仍成功

#### Scenario: EVT-CREATE-009 已儲存的一般 UTC 活動在守衛生效後仍可讀取
- **WHEN** 資料庫中已儲存一筆 `StartAtUtc` 為一般 UTC 時間的活動（不論在本規則生效前或後寫入，讀取路徑相同：由資料庫讀回後經 `Event` 建構子具現化），匿名查詢活動列表、匿名查詢該活動座位（`GET /api/events/{id}/seats`，會讀取該活動），以及已切換至該活動所屬 Organizer 的使用者查詢後台活動列表
- **THEN** 三者皆成功；兩個列表回傳的 `StartAtUtc` 與建立時一致且帶 `Z`
