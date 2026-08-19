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

### Requirement: 透過管理 API 查詢場地與座位圖
系統 SHALL 提供查詢場地（`Venue`）列表、單一場地明細（含其下座位圖摘要）、單一座位圖明細（含完整座位清單）的端點，沿用既有「後台管理 API 需要 Admin 角色」的權限規則。場地列表 SHALL 依場地名稱排序，名稱相同時 SHALL 依場地 Id 排序，確保重複查詢時順序完全穩定（場地名稱目前沒有唯一性約束，只依名稱排序不足以保證順序）。場地明細回傳的座位圖摘要、座位圖明細回傳的座位清單皆不保證順序，呼叫端不得依賴其回傳順序。場地明細回傳的座位圖摘要 SHALL 僅含座位圖 Id 與座位總數，不含每個座位的明細；場地下有多張座位圖時，每張座位圖摘要的座位總數 MUST 各自對應該座位圖實際的座位數。座位圖明細 SHALL 回傳該座位圖下每個座位的分區代碼與座位編號；座位圖目前沒有任何座位時（`Seats` 為空集合）MUST 視為成功，回傳空的座位清單，不得視同找不到。查詢不存在的場地或座位圖 MUST 回報找不到，不得回傳空物件或造成例外。查詢座位圖明細時，若指定的座位圖存在但不屬於指定的場地，MUST 視同找不到，不得回傳該座位圖的資料。

#### Scenario: 查詢場地列表
- **WHEN** Admin 呼叫場地列表查詢端點
- **THEN** 系統回傳目前所有場地的基本資訊，依名稱排序

#### Scenario: 場地列表中有多個同名場地
- **WHEN** Admin 呼叫場地列表查詢端點，資料庫中有兩個以上名稱相同的場地
- **THEN** 系統 SHALL 依場地 Id 排序這些同名場地，確保重複查詢時順序完全一致，不因資料庫查詢的不確定順序而變動

#### Scenario: 查詢場地明細
- **WHEN** Admin 對存在的場地呼叫場地明細查詢端點
- **THEN** 系統回傳該場地的基本資訊，以及其下每張座位圖的 Id 與座位總數

#### Scenario: 場地下有多張座位圖
- **WHEN** Admin 對底下有多張座位圖、且各自座位數不同的場地呼叫場地明細查詢端點
- **THEN** 系統回傳的座位圖摘要清單中，每張座位圖的座位總數 MUST 各自對應其實際座位數，不得混淆或加總錯誤

#### Scenario: 場地明細中某張座位圖目前沒有任何座位
- **WHEN** Admin 對底下有一張座位圖、但該座位圖目前沒有任何座位的場地呼叫場地明細查詢端點
- **THEN** 系統回傳的座位圖摘要中，該座位圖的座位總數 MUST 為 0，不得省略該筆座位圖或視同找不到

#### Scenario: 查詢不存在的場地明細
- **WHEN** Admin 對不存在的場地 Id 呼叫場地明細查詢端點
- **THEN** 系統 MUST 回報找不到場地

#### Scenario: 查詢座位圖明細
- **WHEN** Admin 對存在且屬於指定場地的座位圖呼叫座位圖明細查詢端點
- **THEN** 系統回傳該座位圖下每個座位的分區代碼與座位編號

#### Scenario: 查詢不屬於指定場地的座位圖明細
- **WHEN** Admin 呼叫座位圖明細查詢端點，指定的座位圖存在但實際屬於另一個場地
- **THEN** 系統 MUST 回報找不到，不回傳該座位圖的資料

#### Scenario: 座位圖目前沒有任何座位
- **WHEN** Admin 對存在、但目前沒有任何座位的座位圖呼叫座位圖明細查詢端點
- **THEN** 系統 MUST 回傳成功，座位清單為空陣列，不得視同找不到

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

### Requirement: 透過管理 API 查詢活動列表時取得建立者與售票狀況統計
系統 SHALL 提供一個獨立於既有公開活動列表查詢端點（`event-catalog` 能力既有的公開端點，供買家瀏覽用）的 Admin 專用活動列表查詢端點，沿用既有「後台管理 API 需要 Admin 角色」的權限規則。這個端點 SHALL 為每筆活動附帶：建立者（Admin 的 MemberId 與可辨識的顯示名稱，查無對應會員或活動未記錄建立者時顯示名稱為 null）、建立時間（未記錄時為 null）、座位依 Available／Held／Sold 分類的數量統計。統計 SHALL 反映查詢當下的即時狀態（依既有座位狀態計算邏輯，Held 若已過期 MUST 視為 Available，不得沿用過期前的分類）。查詢活動列表不需要另外呼叫其他端點才能取得這份統計。既有公開的活動列表查詢端點 MUST NOT 回傳建立者、建立時間或售票狀況統計這幾項——這些是 Admin 專用資訊，不對未登入的公開查詢揭露。

#### Scenario: Admin 查詢活動列表取得建立者與售票狀況統計
- **WHEN** Admin 呼叫 Admin 專用的活動列表查詢端點
- **THEN** 系統回傳的每筆活動 SHALL 附帶建立者、建立時間，以及 Available／Held／Sold 各自的座位數量

#### Scenario: 活動座位有已過期的持有中狀態
- **WHEN** 活動的某些座位曾被持有（Held）但持有期限已過、尚未被查詢清理程序處理
- **THEN** 統計 MUST 把這些座位算入 Available，不得算入 Held

#### Scenario: 活動沒有任何座位
- **WHEN** Admin 查詢一筆理論上不應存在但座位數為零的活動（例如資料異常）
- **THEN** 系統 MUST 回傳三個統計數字皆為 0，不得因此拒絕整筆查詢或造成例外

#### Scenario: 既有公開活動列表查詢端點不回傳 Admin 專用欄位
- **WHEN** 任何呼叫端（不論是否登入）呼叫既有的公開活動列表查詢端點
- **THEN** 回傳內容 MUST NOT 包含建立者、建立時間或售票狀況統計
