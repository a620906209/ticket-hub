# event-catalog Specification

## Purpose
TBD - created by archiving change ticketing-core-domain. Update Purpose after archive.
## Requirements
### Requirement: 建立活動
系統 SHALL 允許建立一場需要選位的表演活動，活動必須包含標題、開始時間、所屬場地，並指定使用場地下的一份座位圖（`SeatMap`）；缺少任一必要欄位時 MUST 拒絕建立。

#### Scenario: 建立活動時提供完整資訊
- **WHEN** 使用者提供標題、開始時間、場地、座位圖並建立活動
- **THEN** 系統成功建立 Event，並可查詢到剛建立的活動

#### Scenario: 建立活動時缺少必要欄位
- **WHEN** 使用者建立活動但未提供標題或開始時間
- **THEN** 系統 MUST 拒絕建立並回報錯誤，不產生 Event

### Requirement: 場地座位圖與座位樣板唯一性
系統 SHALL 允許為場地建立座位圖（`SeatMap`），座位圖由多個座位樣板（`Seat`）組成，每個座位須包含分區代碼與座位編號；同一場地座位圖內，分區代碼加座位編號的組合 MUST 唯一。座位樣板描述場地的物理座位配置，可被多場活動的座位圖重複使用，本身不記錄任何鎖定或售出狀態。

#### Scenario: 建立座位圖時座位編號不重複
- **WHEN** 為場地座位圖新增多個分區代碼與座位編號皆不同的座位
- **THEN** 系統成功建立所有座位

#### Scenario: 建立座位圖時出現重複座位
- **WHEN** 為同一場地座位圖新增分區代碼與座位編號皆相同的座位
- **THEN** 系統 MUST 拒絕該筆座位的建立，並回報座位重複錯誤

### Requirement: 建立活動時建立專屬座位庫存（EventSeat）
系統 SHALL 在建立活動時，依其指定的座位圖，為圖中每一個座位樣板建立一筆專屬於該活動的座位庫存記錄（`EventSeat`），初始狀態皆為 Available。不同活動的 `EventSeat` MUST 彼此獨立，即使兩場活動使用同一份場地座位圖，其中一場活動的座位售出或鎖定 MUST NOT 影響另一場活動的座位可售性。

#### Scenario: 建立活動時產生對應數量的座位庫存
- **WHEN** 建立一場活動並指定一份包含 N 個座位的座位圖
- **THEN** 系統為該活動建立 N 筆 `EventSeat`，狀態皆為 Available

#### Scenario: 同一座位圖被兩場活動使用時庫存互不影響
- **WHEN** 兩場不同活動皆使用同一份場地座位圖建立，其中一場活動的某個 `EventSeat` 被標記為 Sold
- **THEN** 另一場活動對應相同座位樣板的 `EventSeat` 狀態仍為 Available，不受影響

### Requirement: EventSeat 唯一性
系統 SHALL 保證同一活動內，每個座位樣板最多只對應一筆 `EventSeat`（鍵為活動與座位樣板的組合）；此唯一性由建立活動時的座位庫存建立流程本身保證，不依賴事後去重。`EventSeat` MUST 為獨立於 `Event` 的實體，不作為 `Event` 內部集合被整包載入操作；訂單項目（`OrderItem`）MUST 直接關聯 `EventSeat`，而非直接關聯共用的座位樣板 `Seat`。

#### Scenario: 建立活動的座位庫存流程不產生重複記錄
- **WHEN** 依座位圖建立一場活動的 `EventSeat` 庫存
- **THEN** 每個座位樣板在該活動下只對應唯一一筆 `EventSeat`，不存在重複記錄

### Requirement: 票種與票價
系統 SHALL 只允許透過活動本身建立票種（`TicketType`），建立時 MUST 先核對傳入的座位圖確實是該活動使用的座位圖（而非另一場活動的座位圖），再核對票種必須對應該座位圖中的一個分區代碼，且票價必須為大於零的金額。`TicketType` MUST NOT 能繞過活動直接建構，避免用其他活動的座位圖建立出歸屬錯誤的票種。

#### Scenario: 建立票種並設定合法票價
- **WHEN** 為活動建立票種，指定已存在的分區代碼與票價 500
- **THEN** 系統成功建立票種，票價為 500

#### Scenario: 建立票種時票價為零或負數
- **WHEN** 為活動建立票種並指定票價為 0 或負數
- **THEN** 系統 MUST 拒絕建立並回報票價無效錯誤

#### Scenario: 建立票種時對應不存在的分區
- **WHEN** 為活動建立票種，指定的分區代碼不存在於該活動的座位圖中
- **THEN** 系統 MUST 拒絕建立並回報分區不存在錯誤

#### Scenario: 建立票種時傳入其他活動的座位圖
- **WHEN** 為活動 A 建立票種，卻傳入活動 B 使用的座位圖
- **THEN** 系統 MUST 拒絕建立並回報座位圖不屬於此活動的錯誤
