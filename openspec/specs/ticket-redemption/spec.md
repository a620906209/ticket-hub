# ticket-redemption Specification

## Purpose
TBD - created by archiving change ticket-issuance-and-redemption. Update Purpose after archive.
## Requirements
### Requirement: 核銷成功將 Ticket 狀態轉為 Redeemed 並記錄時間
系統 SHALL 在核銷成功時將 Ticket 狀態由 `Issued` 轉為 `Redeemed`，並記錄核銷時間（`RedeemedAtUtc`）；成功時 HTTP 回應 MUST 為 `204 No Content`（比照既有 `POST /api/orders/{id}/confirm`／`POST /api/orders/{id}/cancel` 的回應慣例，不回傳 body）。

#### Scenario: 核銷成功轉態並記錄時間
- **WHEN** 對狀態為 `Issued` 的 Ticket 成功呼叫核銷端點
- **THEN** Ticket 狀態轉為 `Redeemed`，`RedeemedAtUtc` 記錄為核銷當下時間，HTTP 回應為 `204 No Content`

### Requirement: 拒絕非 Issued 狀態、不存在或路徑格式不合法的核銷請求
系統 SHALL 拒絕對任何非 `Issued` 狀態（含 `Redeemed`；`Voided` 本次無觸發路徑不可達，但規則本身涵蓋此狀態以避免未來 `Voided` 上線後出現規格未定義的行為）的 Ticket 核銷、拒絕對不存在的 Ticket ID 核銷、拒絕路徑參數非合法 GUID 格式的請求；非 `Issued` 狀態 MUST 回傳可判別的衝突錯誤，不存在或格式不合法 MUST 回傳 404（路由採 `{id:guid}` 限制，比照既有 Admin 端點慣例，非 GUID 格式在進入 Controller 前即由路由比對失敗回傳 404），三者皆不得拋出未攔截例外。

#### Scenario: 對已核銷票券再次核銷
- **WHEN** 對狀態已是 `Redeemed` 的 Ticket 呼叫核銷端點
- **THEN** 系統回傳衝突錯誤，Ticket 狀態維持 `Redeemed`

#### Scenario: 對不存在的票券核銷
- **WHEN** 呼叫核銷端點帶入不存在的 Ticket ID
- **THEN** 系統回傳 404，不拋出未攔截例外

#### Scenario: 路徑參數非合法 GUID 格式
- **WHEN** 呼叫核銷端點的路徑參數不是合法 GUID 格式
- **THEN** 系統回傳 404，不拋出未攔截例外

### Requirement: 核銷併發防重複
系統 SHALL 保證同一張 Ticket 被並發呼叫兩次核銷端點時，只有一個操作成功轉為 `Redeemed`，另一個 MUST 依 Ticket 當下最新狀態被拒絕，不得讓兩次呼叫都回報成功。

#### Scenario: 並發核銷同一張票
- **WHEN** 兩個請求幾乎同時對同一張狀態為 `Issued` 的 Ticket 呼叫核銷端點
- **THEN** 系統保證只有一個請求成功轉為 `Redeemed`，另一個依 Ticket 當下已變更的狀態被拒絕，不會發生兩次呼叫都成功的情況

### Requirement: 核銷 API 可選驗證 QR 簽章內容
系統 SHALL 允許核銷端點（`PATCH /api/admin/tickets/{id}/redeem`）的請求 body 附帶可選欄位 `signature`（字串型別）。當 `signature` 為 `null` 或整個請求 body 未提供時，系統 SHALL 維持既有行為，直接以資料庫狀態為權威來源核銷，不驗證任何簽章。當 `signature` 為非 `null` 的字串時（含空字串或僅空白字元），系統 SHALL 在查詢或鎖定 Ticket 之前，先以路徑參數 `id` 與 `signature` 依 `ticket-issuance` 能力定義的精確格式重組，呼叫既有 `ITicketSigningService.TryVerify` 驗證其未被竄改；驗證失敗（含空字串／空白字元必然驗證失敗的情況）MUST 回傳與「查無此票」（404）、「狀態衝突」（409）可明確區分的錯誤，MUST NOT 查詢或變更任何 Ticket 的狀態；此錯誤 SHALL 具備專屬且穩定的判別依據（比照既有 `ErrorType.QueueAdmissionRequired` 的既定慣例），不得與其他驗證錯誤共用同一個判別依據。`signature` 欄位型別不符（例如數字、物件）時，MUST 在進入此驗證邏輯前即被回絕（框架層級的 request body 反序列化失敗），同樣 MUST NOT 查詢或變更任何 Ticket 的狀態。驗證通過後才進入既有的核銷流程（鎖定、狀態檢查、轉態）。系統 SHALL NOT 將 `signature` 欄位值或完整請求 body 內容輸出至一般應用程式日誌。

#### Scenario: TICKET-REDEEM-SIG-BACKWARD-COMPAT 未提供簽章時維持既有行為
- **WHEN** 呼叫核銷端點時 request body 未附帶 `signature`，或整個 body 省略
- **THEN** 系統直接以資料庫狀態核銷（或依既有規則回報 404／409），行為與新增此需求前完全相同

#### Scenario: TICKET-REDEEM-SIG-VALID 提供正確簽章時驗證通過並核銷
- **WHEN** 呼叫核銷端點時附帶與路徑參數 `id` 相符的正確簽章，且該 Ticket 狀態為 `Issued`
- **THEN** 系統驗證簽章通過，成功核銷

#### Scenario: TICKET-REDEEM-SIG-INVALID 提供不符的簽章
- **WHEN** 呼叫核銷端點時附帶的 `signature` 與路徑參數 `id` 重組後驗證不通過（內容被竄改或簽章錯誤）
- **THEN** 系統回傳可與「查無此票」「已核銷過」明確區分的錯誤，不查詢或變更任何 Ticket 的狀態

#### Scenario: TICKET-REDEEM-SIG-EMPTY 提供空字串或空白字元的簽章
- **WHEN** 呼叫核銷端點時附帶的 `signature` 為空字串或僅含空白字元
- **THEN** 系統視為驗證失敗，回傳與 TICKET-REDEEM-SIG-INVALID 相同的可區分錯誤，不查詢或變更任何 Ticket 的狀態

#### Scenario: TICKET-REDEEM-SIG-TYPE-MISMATCH 簽章欄位型別不符
- **WHEN** 呼叫核銷端點時 request body 的 `signature` 欄位為非字串型別（例如數字）
- **THEN** 系統在框架層級的請求反序列化階段即回絕請求，不進入核銷邏輯，不查詢或變更任何 Ticket 的狀態

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

