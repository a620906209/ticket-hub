# event-management Specification

## Purpose
TBD - created by archiving change ticketing-event-management. Update Purpose after archive.
## Requirements
### Requirement: 後台管理 API 需要已切換至一個 Approved Organizer
系統 SHALL 要求呼叫後台管理端點者持有效 JWT，且該 JWT 帶有格式合法（可解析為非 `Guid.Empty` 的 `Guid`，見 `organizer-management` 能力「`RequireOrganizerContext` Authorization Policy MUST 驗證 claim 格式並 fail-closed」需求）的 `OrganizerId` claim（即已透過 `organizer-management` 能力的切換操作情境端點，切換至一個自己所屬且狀態為 `Approved` 的 Organizer）；未提供有效 Token 或 Token 未帶合法 `OrganizerId` claim MUST 被拒絕。單純持有 `MemberRole.Admin` 角色、但尚未切換至任何 Organizer 的請求，MUST 被視同未帶 `OrganizerId` claim，同樣被拒絕。

本 Requirement 涵蓋的「後台管理端點」，本次交付範圍內明確為以下 8 個既有端點（不含 `AdminEventsController` 上屬於其他能力的端點：銷售報表查詢屬 `sales-report` 能力、熱門搶購模式開關屬 `purchase-queue` 能力；兩者的授權規則、Scenario 與測試皆由各自能力定義，不屬於本 Requirement 與 `EVT-AUTHZ-001`～`EVT-AUTHZ-005` 的涵蓋範圍）：
1. 建立 Venue（`AdminVenuesController`）
2. 建立 SeatMap（`AdminVenuesController`）
3. 查詢場地列表（`AdminVenuesController`）
4. 查詢場地明細（`AdminVenuesController`）
5. 查詢座位圖明細（`AdminVenuesController`）
6. 建立 Event（`AdminEventsController`）
7. 建立 TicketType（`AdminEventsController`）
8. 後台專用活動列表查詢（`AdminEventsController`）

此 Policy（`RequireOrganizerContext`）驗證 claim 格式合法後即判定通過，不對每次請求即時查詢該 Organizer 目前的資料庫狀態，因此某個 Organizer 被停權後，其成員手上停權前已核發、尚未過期的 Access Token，在自然到期前仍可能通過本 Policy（見 `EVT-AUTHZ-004`；這是與既有 Role 變更延遲曝險同等級的既知取捨，非缺陷，詳見 design.md Decision 4）。

#### Scenario: EVT-AUTHZ-001 已切換至 Approved Organizer 的成員成功呼叫管理端點
- **WHEN** 持有效 JWT 且帶有 `OrganizerId` claim 的使用者呼叫任一後台管理端點
- **THEN** 系統受理該請求並依端點邏輯處理

#### Scenario: EVT-AUTHZ-002 已登入但尚未切換 Organizer 的使用者呼叫管理端點
- **WHEN** 持有效 JWT、但 Token 未帶 `OrganizerId` claim 的使用者（含單純角色為 `Admin` 但尚未切換 Organizer 者）呼叫任一後台管理端點
- **THEN** 系統回傳 403 拒絕存取，不執行任何變更

#### Scenario: EVT-AUTHZ-003 未帶 Token 呼叫管理端點
- **WHEN** 未提供 Authorization Header 或 Token 無效，呼叫任一後台管理端點
- **THEN** 系統回傳 401 未授權，不執行任何變更

#### Scenario: EVT-AUTHZ-004 停權前已核發、尚未過期的 Access Token 於過期前仍可通過既有請求（已知延遲視窗，非缺陷）
- **WHEN** 某 Member 持有一組停權前核發、尚未過期的 Access Token（帶有該 Organizer 的 `OrganizerId` claim），該 Organizer 隨後被停權（見 `organizer-management` 能力），該 Member 在 Access Token 過期前，於未重新換發、未重新切換的情況下呼叫本能力任一後台管理端點
- **THEN** 系統 SHALL 依既有 `RequireOrganizerContext` Policy 的既定行為受理該請求（不因為 Organizer 已被停權而在這個時間點被攔截）；該延遲曝險上限為 `AccessTokenExpirationMinutes`，一旦該 Access Token 過期或呼叫端嘗試換發／切換，MUST 依 `organizer-management` 能力的停權／換發規則被拒絕——**這條「一旦換發或切換即被拒絕」的負向路徑，由 `organizer-management` 能力自己的 Requirement 與測試完整負責，不在本能力範圍內重複驗證**：換發後 claim 被清空見該能力 `ORG-REFRESH-003` Scenario（對應測試 `organizer-management` tasks.md 7.24）；重新呼叫切換被拒絕見該能力 `ORG-SUSPEND-001` Scenario（對應測試 `organizer-management` tasks.md 7.11）；「過期後」本身則見本 spec `EVT-AUTHZ-005`。本 Scenario／`EVT-AUTHZ-004` 只負責驗證「過期前、未換發、未切換」這個延遲視窗內仍可通過的那一半行為，三條 Scenario（本篇 + `ORG-REFRESH-003`／`ORG-SUSPEND-001` + `EVT-AUTHZ-005`）合起來才是完整的正負向覆蓋

#### Scenario: EVT-AUTHZ-005 Access Token 過期後無法呼叫本能力後台管理端點
- **WHEN** 使用者持一組已過期的 Access Token（不論其是否曾帶有 `OrganizerId` claim）呼叫本能力任一後台管理端點
- **THEN** 系統 MUST 回傳 401 未授權，不執行該端點的查詢或寫入邏輯——此為既有 JWT Bearer 驗證的既定行為（見 `authentication` 能力「未攜帶或攜帶無效 Access Token」Scenario），本次是第一次針對本能力自己的端點明確驗證這條路徑，呼應 `EVT-AUTHZ-004` 對「過期後」的保證

### Requirement: 透過管理 API 建立場地與座位圖
系統 SHALL 提供已切換至一個 Approved Organizer 的使用者建立 `Venue` 與其下 `SeatMap`（含座位樣板 `Seat`）的端點，座位樣板的唯一性規則遵循既有 `event-catalog` 能力的規範。建立 `SeatMap` 前 MUST 先確認所屬 `Venue` 存在，不存在時 MUST 拒絕並回報找不到場地。`Venue`／`SeatMap` 為平台共用資源，不歸屬特定 Organizer，任何已切換至一個 Approved Organizer 的使用者皆可建立與查詢，不因所屬 Organizer 不同而受限。

#### Scenario: EVT-VENUE-001 建立場地與座位圖成功
- **WHEN** 已切換至一個 Approved Organizer 的使用者提供場地資訊與座位圖內容（含不重複的分區代碼與座位編號組合）建立
- **THEN** 系統成功建立，回傳可用於後續查詢的識別碼

#### Scenario: EVT-VENUE-002 座位圖內座位樣板重複
- **WHEN** 已切換至一個 Approved Organizer 的使用者建立座位圖時，其中兩個座位的分區代碼與座位編號組合相同
- **THEN** 系統 MUST 拒絕建立並回報座位重複錯誤，不建立任何座位

#### Scenario: EVT-VENUE-003 建立座位圖時場地不存在
- **WHEN** 已切換至一個 Approved Organizer 的使用者對不存在的 `Venue` 建立座位圖
- **THEN** 系統 MUST 拒絕建立並回報找不到場地，不建立任何座位

### Requirement: 透過管理 API 查詢場地與座位圖
系統 SHALL 提供查詢場地（`Venue`）列表、單一場地明細（含其下座位圖摘要）、單一座位圖明細（含完整座位清單）的端點，沿用既有「後台管理 API 需要已切換至一個 Approved Organizer」的權限規則。場地列表 SHALL 依場地名稱排序，名稱相同時 SHALL 依場地 Id 排序，確保重複查詢時順序完全穩定（場地名稱目前沒有唯一性約束，只依名稱排序不足以保證順序）。場地明細回傳的座位圖摘要、座位圖明細回傳的座位清單皆不保證順序，呼叫端不得依賴其回傳順序。場地明細回傳的座位圖摘要 SHALL 僅含座位圖 Id 與座位總數，不含每個座位的明細；場地下有多張座位圖時，每張座位圖摘要的座位總數 MUST 各自對應該座位圖實際的座位數。座位圖明細 SHALL 回傳該座位圖下每個座位的分區代碼與座位編號；座位圖目前沒有任何座位時（`Seats` 為空集合）MUST 視為成功，回傳空的座位清單，不得視同找不到。查詢不存在的場地或座位圖 MUST 回報找不到，不得回傳空物件或造成例外。查詢座位圖明細時，若指定的座位圖存在但不屬於指定的場地，MUST 視同找不到，不得回傳該座位圖的資料。

#### Scenario: EVT-VENUE-QUERY-001 查詢場地列表
- **WHEN** 已切換至一個 Approved Organizer 的使用者呼叫場地列表查詢端點
- **THEN** 系統回傳目前所有場地的基本資訊，依名稱排序

#### Scenario: EVT-VENUE-QUERY-002 場地列表中有多個同名場地
- **WHEN** 已切換至一個 Approved Organizer 的使用者呼叫場地列表查詢端點，資料庫中有兩個以上名稱相同的場地
- **THEN** 系統 SHALL 依場地 Id 排序這些同名場地，確保重複查詢時順序完全一致，不因資料庫查詢的不確定順序而變動

#### Scenario: EVT-VENUE-QUERY-003 查詢場地明細
- **WHEN** 已切換至一個 Approved Organizer 的使用者對存在的場地呼叫場地明細查詢端點
- **THEN** 系統回傳該場地的基本資訊，以及其下每張座位圖的 Id 與座位總數

#### Scenario: EVT-VENUE-QUERY-004 場地下有多張座位圖
- **WHEN** 已切換至一個 Approved Organizer 的使用者對底下有多張座位圖、且各自座位數不同的場地呼叫場地明細查詢端點
- **THEN** 系統回傳的座位圖摘要清單中，每張座位圖的座位總數 MUST 各自對應其實際座位數，不得混淆或加總錯誤

#### Scenario: EVT-VENUE-QUERY-005 場地明細中某張座位圖目前沒有任何座位
- **WHEN** 已切換至一個 Approved Organizer 的使用者對底下有一張座位圖、但該座位圖目前沒有任何座位的場地呼叫場地明細查詢端點
- **THEN** 系統回傳的座位圖摘要中，該座位圖的座位總數 MUST 為 0，不得省略該筆座位圖或視同找不到

#### Scenario: EVT-VENUE-QUERY-006 查詢不存在的場地明細
- **WHEN** 已切換至一個 Approved Organizer 的使用者對不存在的場地 Id 呼叫場地明細查詢端點
- **THEN** 系統 MUST 回報找不到場地

#### Scenario: EVT-VENUE-QUERY-007 查詢座位圖明細
- **WHEN** 已切換至一個 Approved Organizer 的使用者對存在且屬於指定場地的座位圖呼叫座位圖明細查詢端點
- **THEN** 系統回傳該座位圖下每個座位的分區代碼與座位編號

#### Scenario: EVT-VENUE-QUERY-008 查詢不屬於指定場地的座位圖明細
- **WHEN** 已切換至一個 Approved Organizer 的使用者呼叫座位圖明細查詢端點，指定的座位圖存在但實際屬於另一個場地
- **THEN** 系統 MUST 回報找不到，不回傳該座位圖的資料

#### Scenario: EVT-VENUE-QUERY-009 座位圖目前沒有任何座位
- **WHEN** 已切換至一個 Approved Organizer 的使用者對存在、但目前沒有任何座位的座位圖呼叫座位圖明細查詢端點
- **THEN** 系統 MUST 回傳成功，座位清單為空陣列，不得視同找不到

### Requirement: 透過管理 API 建立活動與票種
系統 SHALL 提供已切換至一個 Approved Organizer 的使用者建立 `Event`（指定場地與座位圖）與 `TicketType`（指定分區代碼與票價，並指定是否綁座位 `RequiresSeat`）的端點，建立規則遵循既有 `event-catalog` 能力的規範（包含活動建立時自動產生對應 `EventSeat` 等）。建立前 MUST 先確認引用的場地／座位圖／活動存在，不存在時 MUST 拒絕並回報找不到對應資源。建立活動時，若指定的座位圖存在但不屬於指定的場地，MUST 視同找不到座位圖，拒絕建立（不得建立場地與座位圖不對應的活動）。建立活動成功時，系統 SHALL 記錄呼叫端當下的登入身份（建立者）、當下時間（建立時間），以及呼叫端 Access Token 中的 `OrganizerId`（該活動所屬的主辦方）；建立者、建立時間、`OrganizerId` 三者皆由後端依 JWT 解析取得，不接受前端在建立活動的請求內容中指定或覆寫。建立票種時，該票種歸屬的 Organizer MUST 與其所屬 Event 的 `OrganizerId` 一致（票種透過 Event 間接歸戶，不需另外指定）；系統 SHALL 在確認活動存在後，額外核對該活動的 `OrganizerId` 是否等於呼叫端 Access Token 中的 `OrganizerId`，不一致時 MUST 視同活動不存在（找不到），不得回傳 403、不得建立任何票種——避免已核准的 Organizer 成員透過猜測或枚舉活動 Id，對其他 Organizer 名下的既有活動新增票種。

建立票種時，驗證規則依 `RequiresSeat` 分流：
- `RequiresSeat = true`（綁座位）：`ZoneCode` MUST 存在於該活動座位圖的分區中，不接受 `AvailableQuantity`
- `RequiresSeat = false`（純計數）：`ZoneCode` 僅作為票種顯示名稱，MUST NOT 驗證是否存在於座位圖分區中；`AvailableQuantity` MUST 為必填且為正整數

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

### Requirement: 透過管理 API 查詢活動列表時取得建立者與售票狀況統計
系統 SHALL 提供一個獨立於既有公開活動列表查詢端點（`event-catalog` 能力既有的公開端點，供買家瀏覽用）的後台專用活動列表查詢端點，沿用既有「後台管理 API 需要已切換至一個 Approved Organizer」的權限規則。這個端點 SHALL 只回傳呼叫端目前切換所在 Organizer 名下的活動，不回傳其他 Organizer 的活動。每筆活動 SHALL 附帶：建立者（Member 的 MemberId 與可辨識的顯示名稱，查無對應會員或活動未記錄建立者時顯示名稱為 null）、建立時間（未記錄時為 null）、座位依 Available／Held／Sold 分類的數量統計。統計 SHALL 反映查詢當下的即時狀態（依既有座位狀態計算邏輯，Held 若已過期 MUST 視為 Available，不得沿用過期前的分類）。查詢活動列表不需要另外呼叫其他端點才能取得這份統計。既有公開的活動列表查詢端點 MUST NOT 回傳建立者、建立時間、售票狀況統計或 `OrganizerId` 這幾項——這些是後台專用資訊，不對未登入的公開查詢揭露。

#### Scenario: EVT-LIST-001 查詢活動列表僅回傳目前所屬 Organizer 名下的活動
- **WHEN** 已切換至 Organizer A 的使用者呼叫後台專用的活動列表查詢端點，資料庫中同時存在 Organizer A 與 Organizer B 名下的活動
- **THEN** 系統只回傳 Organizer A 名下的活動，不包含 Organizer B 的活動

#### Scenario: EVT-LIST-002 查詢活動列表取得建立者與售票狀況統計
- **WHEN** 已切換至一個 Approved Organizer 的使用者呼叫後台專用的活動列表查詢端點
- **THEN** 系統回傳的每筆活動 SHALL 附帶建立者、建立時間，以及 Available／Held／Sold 各自的座位數量

#### Scenario: EVT-LIST-003 活動座位有已過期的持有中狀態
- **WHEN** 活動的某些座位曾被持有（Held）但持有期限已過、尚未被查詢清理程序處理
- **THEN** 統計 MUST 把這些座位算入 Available，不得算入 Held

#### Scenario: EVT-LIST-004 活動沒有任何座位
- **WHEN** 已切換至一個 Approved Organizer 的使用者查詢一筆理論上不應存在但座位數為零的活動（例如資料異常）
- **THEN** 系統 MUST 回傳三個統計數字皆為 0，不得因此拒絕整筆查詢或造成例外

#### Scenario: EVT-LIST-005 既有公開活動列表查詢端點不回傳後台專用欄位
- **WHEN** 任何呼叫端（不論是否登入）呼叫既有的公開活動列表查詢端點
- **THEN** 回傳內容 MUST NOT 包含建立者、建立時間、售票狀況統計或 `OrganizerId`

### Requirement: 既有資料遷移時 SHALL 回填 Organizer 歸屬，加上完整性約束前 MUST 確認無殘留資料
本次為 `Event` 新增 `OrganizerId` 外鍵欄位時，系統 SHALL 透過資料庫遷移對既有資料執行下列回填（依賴 `organizer-management` 變更已存在的 `Organizer`／`OrganizerMember` 資料表），且加上 `NOT NULL` 約束的遷移 MUST 在確認回填完整後才可執行（見 design.md Migration Plan）：
- 以寫死在 migration 內的固定 GUID（而非查詢 `Name` 比對——`organizer-management` 不保證 `Organizer.Name` 全系統唯一）識別並建立一筆「既有資料轉入」用 `Organizer`（`Status = Approved`）
- 為**所有**既有 `MemberRole.Admin` 角色的 Member（不限於曾建立過活動者）各自建立一筆 `OrganizerMember`（`Role = Owner`），掛在該轉入用 Organizer 下
- 所有既有 `Event.OrganizerId` 設為該轉入用 Organizer 的 Id

上述三個步驟 SHALL 皆為冪等操作（資料庫層級的衝突處理，而非應用層「先查詢再寫入」）：轉入用 Organizer 依固定 GUID 衝突即略過插入；`OrganizerMember` 依 `organizer-management` 已建立的 `(OrganizerId, MemberId)` 複合唯一索引衝突即略過插入；`Event` 回填只處理 `OrganizerId IS NULL` 的既有列。回填腳本因故中途失敗後重新完整執行，MUST 能安全地只補完尚未完成的部分，不得因重跑而產生重複的 `Organizer`／`OrganizerMember` 記錄，也不得改寫已回填過的 `Event.OrganizerId`。

在為 `Events.OrganizerId` 加上 `NOT NULL` 約束的後續遷移執行前，該遷移本身 MUST 內建明確、可辨識的檢查，主動查詢確認資料庫中沒有 `OrganizerId IS NULL` 的殘留列；若有殘留列，該遷移 MUST 主動中止失敗（錯誤訊息可辨識為此檢查本身，而非仰賴資料庫對 `NOT NULL` 約束的底層檢查碰巧失敗），且 MUST 不產生部分套用的 schema 變更（約束與外鍵要嘛完全套用，要嘛完全不套用），不得略過此檢查直接套用約束、也不得僅由部署人員手動查詢代替（見 design.md「部署窗口風險」，可能因部署順序疏漏，在回填完成、新版後端尚未上線的窗口期由舊版後端建立不帶 `OrganizerId` 的活動）。

**Rollback 為部署運維程序，非本次自動化測試範圍**：若需回滾本次遷移，還原前 SHALL 先由執行者確認沒有新建立的 Organizer 資料依賴（例如新申請的 Organizer 下已有 Event），必要時先手動處理資料再降版；這是部署當下由人工核對的運維步驟，本次不強制要求對應的自動化測試。

#### Scenario: EVT-MIGRATE-001 遷移執行後所有既有 Admin 皆為轉入用 Organizer 的成員
- **WHEN** 針對已有既有 `MemberRole.Admin` 角色 Member（含從未建立過活動者）的資料庫執行本次遷移的回填邏輯
- **THEN** 每一位既有 Admin 角色 Member 皆各自擁有一筆掛在轉入用 Organizer 下、`Role = Owner` 的 `OrganizerMember` 記錄

#### Scenario: EVT-MIGRATE-002 遷移執行後所有既有 Event 皆歸屬轉入用 Organizer
- **WHEN** 針對已有既有 `Event` 資料的資料庫執行本次遷移的回填邏輯
- **THEN** 所有既有 `Event.OrganizerId` 皆等於轉入用 Organizer 的 Id，不遺漏任何一筆

#### Scenario: EVT-MIGRATE-003 加上 NOT NULL 約束前偵測到殘留 null 列時遷移中止
- **WHEN** 加上 `Events.OrganizerId` 的 `NOT NULL` 約束遷移執行，資料庫中仍存在至少一筆 `OrganizerId IS NULL` 的 `Event`（模擬部署窗口期殘留資料）
- **THEN** 該遷移內建的明確檢查 MUST 主動偵測並中止失敗（錯誤訊息可辨識為此檢查本身），不得略過檢查直接套用約束、也不得靜默忽略殘留列、也不得僅依賴資料庫底層 `NOT NULL` 違反錯誤碰巧失敗；該次遷移的 `NOT NULL` 約束與外鍵 MUST 完全未套用，不產生部分套用的 schema 變更

#### Scenario: EVT-MIGRATE-004 端到端部署驗證：既有 Admin 回填後只需切換一次即可恢復後台操作
- **WHEN** 對一個帶有既有 `MemberRole.Admin` Member 與既有 `Event` 資料、且尚未套用本次遷移的資料庫，依序套用：(1) EVT-MIGRATE-001／002 的回填遷移，(2) EVT-MIGRATE-003 的 `NOT NULL` 約束遷移；接著以該既有 Admin 的身份登入取得 Refresh Token，呼叫 `organizer-management` 能力的切換操作情境端點切換到回填產生的「既有資料轉入」Organizer，再以換發出的 Access Token 呼叫本能力的後台專用活動列表查詢端點查詢該回填 Event
- **THEN** 遷移全程成功，該 Admin SHALL 成功換發帶有轉入用 Organizer `OrganizerId` claim 的 Access Token（不需要任何額外的手動資料處理），並 SHALL 成功以該 Access Token 查詢到該筆回填的既有 Event——驗證 Migration Plan「回填完成後，既有 Admin 只需呼叫一次切換即可恢復操作」這項部署保證，不僅是資料列本身正確，而是整條「回填 → 切換 → 實際操作既有資料」的路徑皆可行

#### Scenario: EVT-MIGRATE-005 回填腳本重跑不產生重複資料
- **WHEN** 對已成功執行過一次回填的資料庫，完整重新執行整支回填腳本（模擬 migration 中途失敗後的人工重跑）
- **THEN** 資料庫中仍只有一筆固定 GUID 的轉入用 `Organizer`；每一位既有 Admin 角色 Member 仍各自只有一筆掛在該 Organizer 下的 `OrganizerMember`，不產生重複記錄；所有既有 `Event.OrganizerId` 維持不變（第一次回填的結果不被覆寫或改動）

### Requirement: 建立活動時可指定是否需實名，建立後不可變更
建立活動的管理 API SHALL 接受可選欄位 `IsRealNameRequired`（是否需實名）。未提供時 MUST 視為 false，既有客戶端（未帶此欄位）的建立行為不受影響。此設定 MUST 隨活動建立一併儲存；系統 MUST NOT 提供任何在活動建立後變更此設定的途徑。本次變更前已存在的活動一律視為不需實名。後台專用的活動列表查詢端點 SHALL 在每筆活動附帶 `IsRealNameRequired`。

#### Scenario: EVT-REALNAME-001 建立需實名的活動
- **WHEN** 已切換至 Approved Organizer 的使用者建立活動，指定 `IsRealNameRequired = true`
- **THEN** 系統成功建立活動，該活動的 `IsRealNameRequired` 為 true

#### Scenario: EVT-REALNAME-002 未提供此欄位時視為不需實名
- **WHEN** 已切換至 Approved Organizer 的使用者以本次變更前的既有格式建立活動，請求內容不含 `IsRealNameRequired`
- **THEN** 系統成功建立活動，該活動的 `IsRealNameRequired` 為 false

#### Scenario: EVT-REALNAME-003 後台活動列表顯示是否需實名
- **WHEN** 已切換至 Organizer A 的使用者查詢後台專用的活動列表，Organizer A 名下同時有需實名與不需實名的活動
- **THEN** 每筆活動附帶與建立時一致的 `IsRealNameRequired`

#### Scenario: EVT-REALNAME-004 既有活動遷移後為不需實名
- **WHEN** 套用本次資料庫遷移前已存在的活動，在遷移後被查詢
- **THEN** 該活動的 `IsRealNameRequired` 為 false

