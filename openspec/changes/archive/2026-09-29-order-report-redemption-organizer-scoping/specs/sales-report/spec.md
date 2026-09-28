## RENAMED Requirements

- FROM: `### Requirement: 查詢銷售報表需要 Admin 角色`
- TO: `### Requirement: 查詢銷售報表需要已切換至一個 Approved Organizer`

## MODIFIED Requirements

### Requirement: 查詢銷售報表需要已切換至一個 Approved Organizer
系統 SHALL 要求呼叫單一活動銷售報表查詢端點者持有效 JWT 且帶有 `OrganizerId` claim（即已切換至一個 Approved Organizer）；未提供有效 Token 或 Token 未帶 `OrganizerId` claim MUST 被拒絕。查詢的活動若存在但其 `OrganizerId` 不等於呼叫端目前 Organizer，MUST 視同找不到（404），不得回傳 403，且此 404 的回應 body MUST 與活動不存在時逐字相同（含錯誤訊息），避免揭露「這個活動存在、只是不屬於自己」。此授權同樣適用 `RequireOrganizerContext` Policy 不即時查表的既知取捨（見 `organizer-management` 能力與 `event-management` 能力 `EVT-AUTHZ-004`）：Organizer 被停權後，其成員手上停權前已核發、尚未過期的 Access Token，在自然到期前仍可查詢該 Organizer 活動的銷售報表；延遲上限為 `AccessTokenExpirationMinutes`，停權後的換發與切換由 `organizer-management` 能力立即阻擋。

#### Scenario: RPT-AUTHZ-001 已切換至 Approved Organizer 的成員查詢自己活動的銷售報表成功
- **WHEN** 持有效 JWT 且帶有 `OrganizerId` claim 的使用者，對屬於自己目前 Organizer 的活動呼叫銷售報表查詢端點
- **THEN** 系統受理該請求並依端點邏輯處理

#### Scenario: RPT-AUTHZ-002 未帶 OrganizerId claim 查詢銷售報表
- **WHEN** 持有效 JWT、但 Token 未帶 `OrganizerId` claim 的使用者（含單純角色為 `Admin` 但尚未切換 Organizer 者）呼叫銷售報表查詢端點
- **THEN** 系統回傳 403 拒絕存取

#### Scenario: RPT-AUTHZ-003 未帶 Token 查詢銷售報表
- **WHEN** 未提供 Authorization Header 或 Token 無效，呼叫銷售報表查詢端點
- **THEN** 系統回傳 401 未授權

#### Scenario: RPT-AUTHZ-004 查詢屬於其他 Organizer 的活動銷售報表
- **WHEN** 已切換至 Organizer A 的使用者，對存在、但屬於 Organizer B 的活動呼叫銷售報表查詢端點
- **THEN** 系統 MUST 回報找不到資源（404，回應 body 與查詢不存在的活動時逐字相同），不得回傳 403

#### Scenario: RPT-AUTHZ-005 停權前已核發、尚未過期的 Access Token 於過期前仍可查詢銷售報表（已知延遲視窗，非缺陷）
- **WHEN** 某 Member 持有一組停權前核發、尚未過期、帶有 Organizer 的 `OrganizerId` claim 的 Access Token，該 Organizer 隨後被停權，該 Member 在未重新換發、未重新切換的情況下，對屬於該 Organizer 的活動呼叫銷售報表查詢端點
- **THEN** 系統 SHALL 依 `RequireOrganizerContext` Policy 的既定行為受理該請求並回傳銷售報表；延遲上限為 `AccessTokenExpirationMinutes`，換發或切換即被拒絕的負向路徑由 `organizer-management` 能力 `ORG-REFRESH-003`／`ORG-SUSPEND-001` 負責
