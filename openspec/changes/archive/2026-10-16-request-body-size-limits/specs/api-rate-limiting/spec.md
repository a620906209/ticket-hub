# Spec Delta

## ADDED Requirements

### Requirement: 建立座位圖端點的請求頻率限制
系統 SHALL 對建立座位圖端點（`POST /api/admin/venues/{venueId}/seat-maps`）套用獨立命名的 Fixed Window 請求頻率限制，以呼叫端 Access Token 中的 `OrganizerId` 分區：同一 Organizer 的所有成員共用一個額度，不同 Organizer 各自計數，且不與其他限流 policy 共用計數。不排隊（QueueLimit 為 0），超過額度的請求 MUST 立即回傳 429，回應格式與既有限流拒絕相同（`ProblemDetails`，含 `status` 429、`title`、`traceId`，並附 `Retry-After` 標頭），且 MUST NOT 讀取本文、不建立座位圖。限流在身分驗證與授權之後執行，未登入或未切換至 Approved Organizer 的請求依既有規則回傳 401／403，不消耗額度。

#### Scenario: SRL-001 超過額度時回 429 且不建立座位圖
- **WHEN** 同一 Organizer 在同一時間窗內送出超過額度的建立座位圖請求
- **THEN** 超過額度的請求回傳 429，本文為含 `status` 429、`title`、`traceId` 的 `ProblemDetails`，並附 `Retry-After` 標頭；該請求不建立座位圖。額度用完後即使送出 JSON 語法錯誤的本文，仍回傳 429 而不是模型繫結的 400，證明限流早於本文讀取

#### Scenario: SRL-002 不同 Organizer 各自計數
- **WHEN** Organizer A 已用完額度後，Organizer B 的成員在同一時間窗內建立座位圖
- **THEN** Organizer B 的請求成功建立座位圖，不受 A 的額度影響

#### Scenario: SRL-005 同一 Organizer 的不同成員共用額度
- **WHEN** 同一 Organizer 的成員甲在同一時間窗內用完額度後，同一 Organizer 的成員乙建立座位圖
- **THEN** 成員乙的請求回傳 429，不建立座位圖

### Requirement: 建立座位圖限流設定值須為正數，缺漏時採用預設值
系統 SHALL 以 `SeatMapRateLimiting:PermitLimit` 與 `SeatMapRateLimiting:WindowSeconds` 設定建立座位圖的限流額度，缺漏時採用預設值 10 次／60 秒；設定為 0 或負數時 MUST 在應用程式啟動時擋下。

#### Scenario: SRL-003 設定缺漏時採用預設值
- **WHEN** 設定未提供 `SeatMapRateLimiting` 區段
- **THEN** 系統以 PermitLimit 10、WindowSeconds 60 運作，不視為錯誤

#### Scenario: SRL-004 設定值為 0 或負數時擋下
- **WHEN** `SeatMapRateLimiting:PermitLimit` 或 `SeatMapRateLimiting:WindowSeconds` 被設為 0 或負數
- **THEN** 應用程式啟動失敗，不以此設定值運作
