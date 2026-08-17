# event-management Specification

## Purpose
TBD - created by archiving change ticketing-event-management. Update Purpose after archive.
## Requirements
### Requirement: 後台管理 API 需要 Admin 角色
系統 SHALL 要求呼叫後台管理端點（建立 Venue、SeatMap、Event、TicketType）者持有效 JWT 且角色為 Admin；未提供有效 Token 或角色非 Admin MUST 被拒絕。

#### Scenario: Admin 成功呼叫管理端點
- **WHEN** 持有效 JWT 且角色為 Admin 的使用者呼叫任一後台管理端點
- **THEN** 系統受理該請求並依端點邏輯處理

#### Scenario: 非 Admin 會員呼叫管理端點
- **WHEN** 持有效 JWT 但角色非 Admin 的使用者呼叫任一後台管理端點
- **THEN** 系統回傳 403 拒絕存取，不執行任何變更

#### Scenario: 未帶 Token 呼叫管理端點
- **WHEN** 未提供 Authorization Header 或 Token 無效，呼叫任一後台管理端點
- **THEN** 系統回傳 401 未授權，不執行任何變更

### Requirement: 透過管理 API 建立場地與座位圖
系統 SHALL 提供 Admin 建立 `Venue` 與其下 `SeatMap`（含座位樣板 `Seat`）的端點，座位樣板的唯一性規則遵循既有 `event-catalog` 能力的規範。建立 `SeatMap` 前 MUST 先確認所屬 `Venue` 存在，不存在時 MUST 拒絕並回報找不到場地。

#### Scenario: 建立場地與座位圖成功
- **WHEN** Admin 提供場地資訊與座位圖內容（含不重複的分區代碼與座位編號組合）建立
- **THEN** 系統成功建立，回傳可用於後續查詢的識別碼

#### Scenario: 座位圖內座位樣板重複
- **WHEN** Admin 建立座位圖時，其中兩個座位的分區代碼與座位編號組合相同
- **THEN** 系統 MUST 拒絕建立並回報座位重複錯誤，不建立任何座位

#### Scenario: 建立座位圖時場地不存在
- **WHEN** Admin 對不存在的 `Venue` 建立座位圖
- **THEN** 系統 MUST 拒絕建立並回報找不到場地，不建立任何座位

### Requirement: 透過管理 API 建立活動與票種
系統 SHALL 提供 Admin 建立 `Event`（指定場地與座位圖）與 `TicketType`（指定分區代碼與票價）的端點，建立規則遵循既有 `event-catalog` 能力的規範（包含活動建立時自動產生對應 `EventSeat`、票種須核對座位圖歸屬與分區存在性等）。建立前 MUST 先確認引用的場地／座位圖／活動存在，不存在時 MUST 拒絕並回報找不到對應資源。

#### Scenario: 建立活動成功並自動產生座位庫存
- **WHEN** Admin 提供標題、開始時間、場地、座位圖建立活動
- **THEN** 系統成功建立活動，並依座位圖為每個座位樣板建立對應的 `EventSeat`（狀態皆為 Available）

#### Scenario: 建立活動缺少必要欄位
- **WHEN** Admin 建立活動時未提供標題或開始時間
- **THEN** 系統 MUST 拒絕建立並回報錯誤

#### Scenario: 建立活動時場地或座位圖不存在
- **WHEN** Admin 建立活動時指定不存在的場地或座位圖
- **THEN** 系統 MUST 拒絕建立並回報找不到對應資源，不建立活動也不產生 EventSeat

#### Scenario: 建立票種時票價無效
- **WHEN** Admin 為活動建立票種並指定票價為 0 或負數
- **THEN** 系統 MUST 拒絕建立並回報票價無效錯誤

#### Scenario: 建立票種時對應不存在的分區
- **WHEN** Admin 為活動建立票種，指定的分區代碼不存在於該活動的座位圖中
- **THEN** 系統 MUST 拒絕建立並回報分區不存在錯誤

#### Scenario: 建立票種時活動不存在
- **WHEN** Admin 對不存在的活動建立票種
- **THEN** 系統 MUST 拒絕建立並回報找不到活動
