## ADDED Requirements

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
