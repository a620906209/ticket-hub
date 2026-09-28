## RENAMED Requirements

- FROM: `### Requirement: 核銷 API 需要 Admin 權限`
- TO: `### Requirement: 核銷 API 需要已切換至一個 Approved Organizer`

## MODIFIED Requirements

### Requirement: 核銷 API 需要已切換至一個 Approved Organizer
系統 SHALL 提供 `PATCH /api/admin/tickets/{id}/redeem` 端點，僅允許 Access Token 帶有效 `OrganizerId` claim（即已切換至一個 Approved Organizer）的使用者呼叫；未帶該 claim 或未登入呼叫 MUST 被拒絕，不變更任何 Ticket 狀態。核銷前，系統 SHALL 依 `Ticket.OrderItemId → Order.EventId → Event.OrganizerId` 核對該 Ticket 所屬活動是否屬於呼叫端目前 Organizer；不屬於時 MUST 視同查無此票（見下方「拒絕非 Issued 狀態、不存在或路徑格式不合法的核銷請求」需求的 404 規則），不得回傳 403，且此 404 的回應 body MUST 與票券不存在時逐字相同（含錯誤訊息），避免讓核銷端點被用來刺探其他主辦方的票券是否存在。歸屬核對所需的 `OrderItem → Order → Event` 關聯在資料庫中查不到（FK 約束下不可達，代表資料毀損）時，系統 MUST 以非預期錯誤失敗（500），不變更 Ticket 狀態，MUST NOT 視同查無此票或視同屬於呼叫端而放行核銷。

核銷請求的檢查順序 MUST 固定為：(1) QR 簽章驗證（僅以路徑 ticketId 與簽章金鑰計算，不查詢資料庫）→ (2) 載入 Ticket，不存在時回 404 → (3) 歸屬核對，不屬於呼叫端目前 Organizer 時回 404 → (4) 狀態檢查，非 `Issued` 時依「拒絕非 Issued 狀態、不存在或路徑格式不合法的核銷請求」需求回 409。歸屬核對 MUST 先於狀態檢查：屬於其他 Organizer 的票券不論目前狀態為何（含已被其所屬 Organizer 核銷過的 `Redeemed`），一律回傳與「查無此票」相同的 404，不得回傳 409，避免洩漏「這張票存在且已被核銷」。

此授權同樣適用 `RequireOrganizerContext` Policy 不即時查表的既知取捨（見 `organizer-management` 能力與 `event-management` 能力 `EVT-AUTHZ-004`）：Organizer 被停權後，其成員手上停權前已核發、尚未過期的 Access Token，在自然到期前仍可通過本端點並執行核銷；延遲上限為 `AccessTokenExpirationMinutes`，停權後的換發與切換由 `organizer-management` 能力立即阻擋。

#### Scenario: RDM-AUTHZ-001 已切換至 Approved Organizer 的成員核銷自己活動的票券成功
- **WHEN** Access Token 帶有效 `OrganizerId` claim 的使用者，對狀態為 `Issued`、且所屬活動屬於自己目前 Organizer 的 Ticket 呼叫核銷端點
- **THEN** 系統成功核銷

#### Scenario: RDM-AUTHZ-002 未帶 OrganizerId claim 呼叫被拒絕
- **WHEN** Access Token 未帶 `OrganizerId` claim 的使用者（含單純角色為 `Admin` 但尚未切換 Organizer 者）呼叫核銷端點
- **THEN** 系統回傳 403，不變更 Ticket 狀態

#### Scenario: RDM-AUTHZ-003 未登入呼叫被拒絕
- **WHEN** 未提供有效 Authorization Header，呼叫核銷端點
- **THEN** 系統回傳 401，不變更 Ticket 狀態

#### Scenario: RDM-AUTHZ-004 核銷屬於其他 Organizer 的票券
- **WHEN** 已切換至 Organizer A 的使用者，對狀態為 `Issued`、但所屬活動屬於 Organizer B 的 Ticket 呼叫核銷端點
- **THEN** 系統 MUST 回傳與「查無此票」相同的 404 結果（狀態碼與回應 body 皆逐字相同），不變更 Ticket 狀態，不得回傳 403

#### Scenario: RDM-AUTHZ-005 核銷屬於其他 Organizer、且已被核銷過的票券
- **WHEN** 已切換至 Organizer A 的使用者，對狀態為 `Redeemed`（已由其所屬 Organizer B 核銷）的 Ticket 呼叫核銷端點
- **THEN** 系統 MUST 回傳與「查無此票」相同的 404（狀態碼與回應 body 皆逐字相同），不得回傳 409，不變更 Ticket 狀態與核銷時間

#### Scenario: RDM-AUTHZ-006 停權前已核發、尚未過期的 Access Token 於過期前仍可核銷（已知延遲視窗，非缺陷）
- **WHEN** 某 Member 持有一組停權前核發、尚未過期、帶有 Organizer 的 `OrganizerId` claim 的 Access Token，該 Organizer 隨後被停權，該 Member 在未重新換發、未重新切換的情況下，對屬於該 Organizer 活動、狀態為 `Issued` 的 Ticket 呼叫核銷端點
- **THEN** 系統 SHALL 依 `RequireOrganizerContext` Policy 的既定行為受理並完成核銷；延遲上限為 `AccessTokenExpirationMinutes`，換發或切換即被拒絕的負向路徑由 `organizer-management` 能力 `ORG-REFRESH-003`／`ORG-SUSPEND-001` 負責

#### Scenario: RDM-AUTHZ-007 票券的歸屬關聯查不到（資料不一致）
- **WHEN** Ticket 存在，但依 `Ticket.OrderItemId → Order.EventId → Event.OrganizerId` 查不到對應的 Organizer
- **THEN** 系統以非預期錯誤失敗（由全域例外處理轉為 500），不變更 Ticket 狀態，不回傳 404、不完成核銷
