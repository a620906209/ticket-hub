# order-administration Specification

## Purpose
TBD - created by archiving change ticketing-order-management. Update Purpose after archive.
## Requirements
### Requirement: 查詢所有訂單列表
系統 SHALL 提供已切換至一個 Approved Organizer 的使用者查詢訂單的端點，回傳每筆訂單的基本資訊與即時狀態（依 `GetStatus(now)` 推導，可能為 Expired）。回傳範圍 SHALL 僅限於呼叫端目前 Organizer 名下活動（依 `Order.EventId` 對應的 `Event.OrganizerId` 判斷）的訂單，不含其他 Organizer 名下活動的訂單。此過濾 MUST 在資料庫端執行，MUST NOT 先載入其他 Organizer 的訂單再於應用程式記憶體中過濾。

#### Scenario: ORD-LIST-001 查詢訂單列表僅回傳目前所屬 Organizer 名下活動的訂單
- **WHEN** 已切換至 Organizer A 的使用者呼叫訂單列表端點，資料庫中同時存在 Organizer A 與 Organizer B 名下活動各自的訂單
- **THEN** 系統只回傳 Organizer A 名下活動的訂單（含每筆的基本資訊與即時狀態），不包含 Organizer B 名下活動的訂單

### Requirement: 查詢單筆訂單明細
系統 SHALL 提供已切換至一個 Approved Organizer 的使用者依訂單 ID 查詢單筆訂單明細的端點，回傳訂單內每筆座位項目；訂單不存在 MUST 回報找不到資源。查出的訂單若所屬活動的 `OrganizerId` 不等於呼叫端目前 Organizer，MUST 視同找不到（404），不得回傳該訂單資料，也不得回傳 403；此 404 的回應 body MUST 與訂單不存在時逐字相同（含錯誤訊息），避免用不同的錯誤結果讓呼叫端得知「這筆訂單存在、只是不屬於自己」。訂單所屬活動在資料庫中查不到（FK 約束下不可達，代表資料毀損）時，系統 MUST 以非預期錯誤失敗（500），MUST NOT 視同找不到或回傳該訂單資料。

#### Scenario: ORD-DETAIL-001 查詢存在且屬於自己 Organizer 的訂單明細
- **WHEN** 已切換至一個 Approved Organizer 的使用者以存在、且所屬活動屬於自己目前 Organizer 的訂單 ID 查詢訂單明細
- **THEN** 系統回傳該訂單的即時狀態與內部每筆座位項目

#### Scenario: ORD-DETAIL-002 查詢不存在的訂單
- **WHEN** 已切換至一個 Approved Organizer 的使用者以不存在的訂單 ID 查詢訂單明細
- **THEN** 系統回傳 404 找不到資源

#### Scenario: ORD-DETAIL-003 查詢屬於其他 Organizer 的訂單明細
- **WHEN** 已切換至 Organizer A 的使用者以存在、但所屬活動屬於 Organizer B 的訂單 ID 查詢訂單明細
- **THEN** 系統 MUST 回傳 404 找不到資源，回應 body 與以不存在的訂單 ID 查詢時逐字相同，不得回傳該訂單資料，也不得回傳 403

#### Scenario: ORD-DETAIL-004 訂單所屬活動查不到（資料不一致）
- **WHEN** 查出的訂單其 `EventId` 對應的活動在資料庫中不存在
- **THEN** 系統以非預期錯誤失敗（由全域例外處理轉為 500），不回傳 404、不回傳該訂單資料

### Requirement: 背景週期性清理逾時仍為 Pending 的訂單
系統 SHALL 以固定週期背景執行清理程序，找出狀態為 Pending 且已超過到期時間（`HeldUntilUtc`）的訂單，依既有 `ticket-ordering` 能力的取消規則將其轉為 Cancelled，並釋放訂單內仍由該訂單持有的座位；此清理不需要、也不驗證任何買家身份，因為是系統依訂單自身逾時狀態主動觸發，不是任何買家發起的請求。單筆訂單處理失敗（無論是正常的業務規則拒絕，或是可回復的基礎設施例外）MUST NOT 中斷其餘訂單的清理；應用程式關閉（取消）訊號不算「單筆失敗」，MUST 讓清理程序正常停止，不得被當成失敗吞掉後繼續處理下一筆。

#### Scenario: 逾時的 Pending 訂單被背景清理
- **WHEN** 背景清理程序執行，且資料庫中存在一筆狀態為 Pending、已超過到期時間的訂單
- **THEN** 該訂單狀態轉為 Cancelled，訂單內仍由該訂單持有的座位釋放回 Available

#### Scenario: 尚未逾時的 Pending 訂單不受影響
- **WHEN** 背景清理程序執行，且資料庫中存在一筆狀態為 Pending、尚未超過到期時間的訂單
- **THEN** 該訂單狀態與座位鎖定維持不變，不被清理程序處理

#### Scenario: 已是終態的訂單不受影響
- **WHEN** 背景清理程序執行，且資料庫中存在狀態為 Paid 或 Cancelled 的訂單
- **THEN** 這些訂單不被清理程序掃描或處理

#### Scenario: 單筆訂單清理失敗不影響其餘訂單
- **WHEN** 背景清理程序處理多筆逾時訂單，其中一筆處理時被業務規則拒絕（回傳失敗結果，而非拋出例外）
- **THEN** 系統繼續處理其餘逾時訂單，不因單一筆失敗而整批中斷

#### Scenario: 應用程式關閉時清理程序正常停止，不當成單筆失敗處理
- **WHEN** 背景清理程序收到應用程式關閉訊號
- **THEN** 系統停止目前的清理流程、不開始處理後續訂單，且不將這次中斷記錄成某一筆訂單的清理失敗

### Requirement: 查看訂單需要已切換至一個 Approved Organizer
系統 SHALL 要求呼叫訂單列表、訂單明細端點者持有效 JWT 且帶有 `OrganizerId` claim（即已切換至一個 Approved Organizer，見 `organizer-management`／`event-management` 能力的授權規則）；未提供有效 Token 或 Token 未帶 `OrganizerId` claim MUST 被拒絕。此授權同樣適用 `RequireOrganizerContext` Policy 不即時查表的既知取捨（見 `organizer-management` 能力與 `event-management` 能力 `EVT-AUTHZ-004`）：Organizer 被停權後，其成員手上停權前已核發、尚未過期的 Access Token，在自然到期前仍可查詢該 Organizer 名下的訂單（含買家資訊）；延遲上限為 `AccessTokenExpirationMinutes`，停權後的換發與切換由 `organizer-management` 能力立即阻擋。

#### Scenario: ORD-AUTHZ-001 已切換至 Approved Organizer 的成員成功查詢訂單
- **WHEN** 持有效 JWT 且帶有 `OrganizerId` claim 的使用者呼叫訂單列表或訂單明細端點
- **THEN** 系統受理該請求並依端點邏輯處理

#### Scenario: ORD-AUTHZ-002 已登入但尚未切換 Organizer 的使用者查詢訂單
- **WHEN** 持有效 JWT、但 Token 未帶 `OrganizerId` claim 的使用者（含單純角色為 `Admin` 但尚未切換 Organizer 者）呼叫訂單列表或訂單明細端點
- **THEN** 系統回傳 403 拒絕存取

#### Scenario: ORD-AUTHZ-003 未帶 Token 查詢訂單
- **WHEN** 未提供 Authorization Header 或 Token 無效，呼叫訂單列表或訂單明細端點
- **THEN** 系統回傳 401 未授權

#### Scenario: ORD-AUTHZ-004 停權前已核發、尚未過期的 Access Token 於過期前仍可查詢訂單（已知延遲視窗，非缺陷）
- **WHEN** 某 Member 持有一組停權前核發、尚未過期、帶有 Organizer 的 `OrganizerId` claim 的 Access Token，該 Organizer 隨後被停權，該 Member 在未重新換發、未重新切換的情況下呼叫訂單列表端點
- **THEN** 系統 SHALL 依 `RequireOrganizerContext` Policy 的既定行為受理該請求並回傳該 Organizer 名下的訂單；延遲上限為 `AccessTokenExpirationMinutes`，換發或切換即被拒絕的負向路徑由 `organizer-management` 能力 `ORG-REFRESH-003`／`ORG-SUSPEND-001` 負責

