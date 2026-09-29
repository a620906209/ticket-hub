## MODIFIED Requirements

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

