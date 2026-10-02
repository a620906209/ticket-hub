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

核銷請求的檢查順序 MUST 固定為：(1) QR 簽章驗證（僅以路徑 ticketId 與簽章金鑰計算，不查詢資料庫）→ (2) 載入 Ticket，不存在時回 404 → (3) 歸屬核對，不屬於呼叫端目前 Organizer 時回 404 → (4) 狀態檢查，非 `Issued` 時依「拒絕非 Issued 狀態、不存在或路徑格式不合法的核銷請求」需求回 409 → (5) 持票人確認檢查，票券所屬活動需實名而請求未帶持票人已確認旗標時，依「需實名活動的核銷須確認持票人身分」需求回 409（`HolderVerificationRequired`）。歸屬核對 MUST 先於狀態檢查：屬於其他 Organizer 的票券不論目前狀態為何（含已被其所屬 Organizer 核銷過的 `Redeemed`），一律回傳與「查無此票」相同的 404，不得回傳 409，避免洩漏「這張票存在且已被核銷」。

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

### Requirement: 需實名活動的核銷須確認持票人身分
核銷請求 body SHALL 接受可選欄位 `isHolderVerified`（布林，未提供視為 false）。票券所屬活動 `IsRealNameRequired = true` 時，只有 `isHolderVerified = true` 的核銷請求才能完成核銷；否則系統 MUST 回傳 409，ProblemDetails `Title` 為 `HolderVerificationRequired`，Ticket 狀態與核銷時間維持不變。

- **檢查順序**：此檢查 MUST 在既有的簽章驗證、載入 Ticket、歸屬核對、狀態檢查全部通過之後才執行（「核銷 API 需要已切換至一個 Approved Organizer」需求的第 (5) 步）。因此：
  - 屬於其他 Organizer 的票券仍回 404，不會因為是實名活動而回 409，不洩漏存在性。
  - 已核銷的票券仍回既有的 409 衝突，不回 `HolderVerificationRequired`。
  - 簽章無效仍回既有的簽章錯誤。
- **不需實名的活動**：`isHolderVerified` 的值不影響結果，核銷行為與本次變更前完全一致。
- **旗標的意義**：`isHolderVerified` 由操作人員在介面上確認後送出，後端不驗證「是否真的比對過」。這個旗標的作用是讓「未經確認步驟就核銷」在 API 層無法發生，包括舊版客戶端與手動輸入路徑。

#### Scenario: RDM-RN-001 需實名活動未帶確認旗標的核銷被拒
- **WHEN** 已切換至 Organizer A 的操作人員，對 Organizer A 名下需實名活動、狀態為 `Issued` 的票券呼叫核銷端點，簽章正確，body 未帶 `isHolderVerified`
- **THEN** 系統回傳 409、`Title = "HolderVerificationRequired"`，Ticket 維持 `Issued`，核銷時間仍為空

#### Scenario: RDM-RN-002 需實名活動帶確認旗標的核銷成功
- **WHEN** 同上條件，但 body 帶 `isHolderVerified = true`
- **THEN** 系統核銷成功，Ticket 轉為 `Redeemed` 並記錄核銷時間

#### Scenario: RDM-RN-003 確認旗標為 false 視同未確認
- **WHEN** 同 RDM-RN-001 條件，body 帶 `isHolderVerified = false`
- **THEN** 系統回傳 409、`Title = "HolderVerificationRequired"`，Ticket 維持 `Issued`

#### Scenario: RDM-RN-004 不需實名的活動不受旗標影響
- **WHEN** 操作人員對不需實名活動、狀態為 `Issued` 的自家票券呼叫核銷端點，body 未帶 `isHolderVerified`
- **THEN** 系統核銷成功

#### Scenario: RDM-RN-005 其他 Organizer 的實名活動票券仍回 404
- **WHEN** 已切換至 Organizer A 的操作人員，對 Organizer B 名下需實名活動、狀態為 `Issued` 的票券呼叫核銷端點，body 未帶 `isHolderVerified`
- **THEN** 系統回傳與「查無此票」逐字相同的 404，不回傳 409

#### Scenario: RDM-RN-006 已核銷的實名活動票券回既有衝突
- **WHEN** 操作人員對自家需實名活動、狀態已是 `Redeemed` 的票券呼叫核銷端點，body 未帶 `isHolderVerified`
- **THEN** 系統回傳既有的非 Issued 衝突 409，`Title` 不是 `HolderVerificationRequired`

#### Scenario: RDM-RN-007 實名活動票券簽章無效回既有簽章錯誤
- **WHEN** 操作人員對自家需實名活動的票券呼叫核銷端點，簽章被竄改，body 帶 `isHolderVerified = true`
- **THEN** 系統回傳既有的簽章無效錯誤，不核銷

#### Scenario: RDM-RN-008 並發的兩次確認核銷只有一次成功
- **WHEN** 兩個帶 `isHolderVerified = true` 的核銷請求同時對同一張自家需實名活動的 `Issued` 票券送出
- **THEN** 恰好一個成功，另一個回傳既有的非 Issued 衝突 409（沿用「核銷併發防重複」需求）

#### Scenario: RDM-RN-009 確認旗標型別不符
- **WHEN** 呼叫核銷端點時 request body 的 `isHolderVerified` 為非布林型別（例如字串 `"yes"` 或數字 `1`）
- **THEN** 系統在框架層級的請求反序列化階段即回 400，不進入核銷邏輯，不查詢或變更任何 Ticket 的狀態（比照既有 TICKET-REDEEM-SIG-TYPE-MISMATCH）

### Requirement: 已切換 Organizer 的操作人員可查詢票券持票人資料
系統 SHALL 提供 `GET /api/admin/tickets/{id}/holder`，回傳 `ticketId`、`ticketStatus`、`isRealNameRequired`、`holderRealName`、`holderNationalIdLast4`。這是唯讀端點，不變更任何資料、不加鎖。

- **授權**：MUST 與核銷端點相同。未登入回 401；未帶 `OrganizerId` claim 回 403；票券不存在或不屬於呼叫端目前 Organizer 時，回應 MUST 與票券不存在時逐字相同的 404。
- **持票人定義**：持票人為票券所屬訂單的買家（`Order.BuyerId` 對應的會員）。
- **需實名的活動**：`holderRealName` 為完整姓名，`holderNationalIdLast4` 為完整末四碼。這是全系統唯一回傳完整末四碼的端點（見 `real-name-verification` 能力遮蔽規則）。
- **不需實名的活動**：兩個持票人欄位 MUST 為 null，不回傳買家的任何個人資料。
- **資料損毀**：需實名活動的買家查無實名資料（實名不可撤銷且下單時已檢查，正常情況不可達），或歸屬關聯查不到時，系統 MUST 以非預期錯誤失敗（500），不得回傳空值或視同查無此票。
- **路徑格式**：路徑中的 Id 非 GUID 時回 404（路由約束）。
- **可讀取完整個資的對象（最小權限）**：只有 Access Token 中 `OrganizerId` 等於票券所屬活動 Organizer 的成員。目前 `OrganizerMemberRole` 只有 `Owner`，等同該 Organizer 的 Owner。平台 `Admin` 角色本身不具讀取權，未切換到該 Organizer 時一律 403。未來新增其他 Organizer 成員角色時，MUST 重新評估哪些角色可讀。
- **票券狀態限制**：只有票券狀態為 `Issued` 時才回傳持票人資料。狀態不是 `Issued`（例如已核銷 `Redeemed`）時，MUST 回傳 409（與核銷端點的非 Issued 衝突相同語意），回應不得包含任何持票人欄位。核銷完成後，這張票的持票人個資即無法再經由本端點讀取。
- **時間限制**：系統沒有活動結束時間欄位，本端點不以時間限制讀取。只要票券仍為 `Issued`，活動開始後仍可讀取，以支援遲到入場。
- **用途限制**：僅供核銷前的現場比對。系統 MUST NOT 提供依活動或依會員列出多位持票人個資的端點；回應帶 `Cache-Control: no-store`；個資不寫入任何日誌或快取。
- **撤銷**：Organizer 被停權時，沿用 RDM-AUTHZ-006 的既知延遲視窗：停權前核發、尚未過期的 Access Token 在過期前仍可讀取，上限為 `AccessTokenExpirationMinutes`；換發與切換由 `organizer-management` 能力立即阻擋。
- **稽核**：每次成功或被拒（404／409）的查詢，以 Information 等級記錄 Ticket Id、呼叫者 Member Id、呼叫者 Organizer Id、結果，成功時另記持票人 Member Id；MUST NOT 記錄姓名或末四碼。

#### Scenario: RDM-HOLDER-001 查詢自家需實名活動票券的持票人
- **WHEN** 已切換至 Organizer A 的操作人員，查詢 Organizer A 名下需實名活動的票券持票人，該訂單買家已登記姓名「王小明」、末四碼「1234」
- **THEN** 系統回傳 `isRealNameRequired = true`、`holderRealName = "王小明"`、`holderNationalIdLast4 = "1234"`，以及票券目前狀態

#### Scenario: RDM-HOLDER-002 不需實名活動不回傳持票人資料
- **WHEN** 操作人員查詢自家不需實名活動的票券持票人，該訂單買家剛好已登記實名
- **THEN** 系統回傳 `isRealNameRequired = false`，`holderRealName` 與 `holderNationalIdLast4` 皆為 null

#### Scenario: RDM-HOLDER-003 查詢其他 Organizer 的票券回 404
- **WHEN** 已切換至 Organizer A 的操作人員，查詢 Organizer B 名下需實名活動的票券持票人
- **THEN** 系統回傳與票券不存在時逐字相同的 404，不回傳任何持票人資料

#### Scenario: RDM-HOLDER-004 未帶 OrganizerId claim 被拒
- **WHEN** 未切換 Organizer 的使用者（含角色為 `Admin` 者）呼叫查詢持票人端點
- **THEN** 系統回傳 403

#### Scenario: RDM-HOLDER-005 未登入被拒
- **WHEN** 未提供有效 Authorization Header 呼叫查詢持票人端點
- **THEN** 系統回傳 401

#### Scenario: RDM-HOLDER-006 查詢不變更票券
- **WHEN** 操作人員查詢一張 `Issued` 票券的持票人
- **THEN** 票券狀態維持 `Issued`，核銷時間仍為空

#### Scenario: RDM-HOLDER-007 需實名活動的買家查無實名資料（資料損毀）
- **WHEN** 需實名活動的票券，其訂單買家在資料庫中沒有實名資料
- **THEN** 系統以非預期錯誤失敗（500），回應不包含任何持票人欄位

#### Scenario: RDM-HOLDER-008 已核銷票券不再回傳持票人資料
- **WHEN** 操作人員查詢自家需實名活動、狀態已是 `Redeemed` 的票券持票人
- **THEN** 系統回傳 409，回應不包含 `holderRealName`、`holderNationalIdLast4` 或任何姓名、末四碼

#### Scenario: RDM-HOLDER-009 活動開始後仍可查詢未核銷票券
- **WHEN** 操作人員查詢自家需實名活動、開始時間已過、狀態仍為 `Issued` 的票券持票人
- **THEN** 系統回傳持票人姓名與末四碼

#### Scenario: RDM-HOLDER-010 平台 Admin 未切換至該 Organizer 不可讀取
- **WHEN** 角色為 `Admin`、目前 Access Token 的 `OrganizerId` 是另一個 Organizer B 的使用者，查詢 Organizer A 名下需實名活動的票券持票人
- **THEN** 系統回傳與票券不存在時逐字相同的 404，不回傳任何持票人資料

#### Scenario: RDM-HOLDER-011 查詢留下不含個資的稽核日誌
- **WHEN** 操作人員依序對自家 `Issued` 票券查詢成功、對自家 `Redeemed` 票券查詢得到 409、對其他 Organizer 的票券查詢得到 404
- **THEN** 三次各有一筆 Information 稽核日誌，結構化屬性含 Ticket Id、呼叫者 Member Id、Organizer Id 與結果，成功那筆另含持票人 Member Id；三筆的訊息與結構化屬性都不含持票人姓名或末四碼

