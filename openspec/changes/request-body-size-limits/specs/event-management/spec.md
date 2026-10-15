# Spec Delta

## MODIFIED Requirements

### Requirement: 透過管理 API 建立場地與座位圖
系統 SHALL 提供已切換至一個 Approved Organizer 的使用者建立 `Venue` 與其下 `SeatMap`（含座位樣板 `Seat`）的端點，座位樣板的唯一性規則遵循既有 `event-catalog` 能力的規範。建立 `SeatMap` 前 MUST 先確認所屬 `Venue` 存在，不存在時 MUST 拒絕並回報找不到場地。`Venue`／`SeatMap` 為平台共用資源，不歸屬特定 Organizer，任何已切換至一個 Approved Organizer 的使用者皆可建立與查詢，不因所屬 Organizer 不同而受限。單一建立座位圖請求的座位數 MUST 不超過 50,000 席；超過時 MUST 回報驗證錯誤（400），只回報座位數超過上限，不逐筆驗證座位內容，也不查詢場地、不檢查座位重複、不建立任何座位。座位數恰為 50,000 席時不因座位數被拒。

#### Scenario: EVT-VENUE-001 建立場地與座位圖成功
- **WHEN** 已切換至一個 Approved Organizer 的使用者提供場地資訊與座位圖內容（含不重複的分區代碼與座位編號組合）建立
- **THEN** 系統成功建立，回傳可用於後續查詢的識別碼

#### Scenario: EVT-VENUE-002 座位圖內座位樣板重複
- **WHEN** 已切換至一個 Approved Organizer 的使用者建立座位圖時，其中兩個座位的分區代碼與座位編號組合相同
- **THEN** 系統 MUST 拒絕建立並回報座位重複錯誤，不建立任何座位

#### Scenario: EVT-VENUE-003 建立座位圖時場地不存在
- **WHEN** 已切換至一個 Approved Organizer 的使用者對不存在的 `Venue` 建立座位圖
- **THEN** 系統 MUST 拒絕建立並回報找不到場地，不建立任何座位

#### Scenario: EVT-VENUE-004 座位數超過上限
- **WHEN** 已切換至一個 Approved Organizer 的使用者對存在的場地建立含 50,001 席座位的座位圖（其中含重複座位與空白分區代碼）
- **THEN** 系統回傳 400 驗證錯誤，錯誤只有座位數超過上限一項（不含逐筆座位或重複座位的錯誤），不建立座位圖與任何座位

#### Scenario: EVT-VENUE-005 座位數恰為上限
- **WHEN** 建立座位圖請求含恰好 50,000 席欄位合法、不重複的座位
- **THEN** 驗證通過，不因座位數被拒
