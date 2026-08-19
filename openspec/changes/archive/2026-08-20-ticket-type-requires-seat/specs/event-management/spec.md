## MODIFIED Requirements

### Requirement: 透過管理 API 建立活動與票種
系統 SHALL 提供 Admin 建立 `Event`（指定場地與座位圖）與 `TicketType`（指定分區代碼與票價，並指定是否綁座位 `RequiresSeat`）的端點，建立規則遵循既有 `event-catalog` 能力的規範（包含活動建立時自動產生對應 `EventSeat` 等）。建立前 MUST 先確認引用的場地／座位圖／活動存在，不存在時 MUST 拒絕並回報找不到對應資源。建立活動時，若指定的座位圖存在但不屬於指定的場地，MUST 視同找不到座位圖，拒絕建立（不得建立場地與座位圖不對應的活動）。建立活動成功時，系統 SHALL 記錄呼叫端當下的登入身份（建立者）與當下時間（建立時間）；這兩項資訊由後端依 JWT 解析取得，不接受前端在建立活動的請求內容中指定或覆寫。

建立票種時，驗證規則依 `RequiresSeat` 分流：
- `RequiresSeat = true`（綁座位）：`ZoneCode` MUST 存在於該活動座位圖的分區中，不接受 `AvailableQuantity`
- `RequiresSeat = false`（純計數）：`ZoneCode` 僅作為票種顯示名稱，MUST NOT 驗證是否存在於座位圖分區中；`AvailableQuantity` MUST 為必填且為正整數

請求未提供 `RequiresSeat` 時，系統 MUST 視為 `true`（綁座位），維持本次變更前既有客戶端（未帶此欄位）的既有建立票種行為不受影響。

#### Scenario: 建立活動成功並自動產生座位庫存
- **WHEN** Admin 提供標題、開始時間、場地、座位圖建立活動
- **THEN** 系統成功建立活動，並依座位圖為每個座位樣板建立對應的 `EventSeat`（狀態皆為 Available）

#### Scenario: 建立活動成功記錄建立者與建立時間
- **WHEN** 已登入的 Admin 成功建立活動
- **THEN** 系統記錄的建立者 MUST 是該次請求實際使用的登入身份，建立時間 MUST 是系統當下時間，不得為空

#### Scenario: 建立活動缺少必要欄位
- **WHEN** Admin 建立活動時未提供標題或開始時間
- **THEN** 系統 MUST 拒絕建立並回報錯誤

#### Scenario: 建立活動時場地或座位圖不存在
- **WHEN** Admin 建立活動時指定不存在的場地或座位圖
- **THEN** 系統 MUST 拒絕建立並回報找不到對應資源，不建立活動也不產生 EventSeat

#### Scenario: 建立活動時場地與座位圖不對應
- **WHEN** Admin 建立活動時指定的場地與座位圖都存在，但座位圖實際屬於另一個場地
- **THEN** 系統 MUST 拒絕建立並回報找不到對應資源，不建立活動也不產生 EventSeat

#### Scenario: 建立票種時票價無效
- **WHEN** Admin 為活動建立票種並指定票價為 0 或負數
- **THEN** 系統 MUST 拒絕建立並回報票價無效錯誤

#### Scenario: 建立綁座位票種時對應不存在的分區
- **WHEN** Admin 建立 `RequiresSeat = true` 的票種，指定的分區代碼不存在於該活動的座位圖中
- **THEN** 系統 MUST 拒絕建立並回報分區不存在錯誤

#### Scenario: 建立票種時活動不存在
- **WHEN** Admin 對不存在的活動建立票種
- **THEN** 系統 MUST 拒絕建立並回報找不到活動

#### Scenario: 建立純計數票種成功
- **WHEN** Admin 建立 `RequiresSeat = false` 的票種，指定顯示名稱、票價與正整數的可售總量 `AvailableQuantity`
- **THEN** 系統成功建立票種，不驗證顯示名稱是否對應座位圖分區，票種初始可售數量為指定的 `AvailableQuantity`

#### Scenario: 建立純計數票種時未提供可售總量
- **WHEN** Admin 建立 `RequiresSeat = false` 的票種但未提供 `AvailableQuantity`，或提供 0 或負數
- **THEN** 系統 MUST 拒絕建立並回報可售總量無效錯誤

#### Scenario: 建立綁座位票種時提供可售總量
- **WHEN** Admin 建立 `RequiresSeat = true` 的票種，卻同時提供 `AvailableQuantity`
- **THEN** 系統 MUST 拒絕建立並回報驗證錯誤，綁座位票種的庫存數量須由座位圖決定，不接受額外指定總量

#### Scenario: 建立票種時未提供 RequiresSeat（既有客戶端相容）
- **WHEN** Admin 呼叫建立票種端點，請求內容比照本次變更前的既有格式，未包含 `RequiresSeat` 欄位
- **THEN** 系統 MUST 視為 `RequiresSeat = true`（綁座位），依既有的分區存在性規則驗證，行為與本次變更前完全一致
