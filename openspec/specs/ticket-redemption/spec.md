# ticket-redemption Specification

## Purpose
TBD - created by archiving change ticket-issuance-and-redemption. Update Purpose after archive.

## Requirements

### Requirement: 核銷 API 需要 Admin 權限
系統 SHALL 提供 `PATCH /api/admin/tickets/{id}/redeem` 端點，僅允許已登入且角色為 `Admin` 的使用者呼叫；非 `Admin` 或未登入呼叫 MUST 被拒絕，不變更任何 Ticket 狀態。

#### Scenario: Admin 核銷成功
- **WHEN** 已登入且角色為 `Admin` 的使用者，對狀態為 `Issued` 的 Ticket 呼叫核銷端點
- **THEN** 系統成功核銷

#### Scenario: 一般會員呼叫被拒絕
- **WHEN** 已登入但角色為 `Member` 的使用者呼叫核銷端點
- **THEN** 系統回傳 403，不變更 Ticket 狀態

#### Scenario: 未登入呼叫被拒絕
- **WHEN** 未提供有效 Authorization Header，呼叫核銷端點
- **THEN** 系統回傳 401，不變更 Ticket 狀態

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
