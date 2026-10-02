## ADDED Requirements

### Requirement: 建立活動時可指定是否需實名，建立後不可變更
建立活動的管理 API SHALL 接受可選欄位 `IsRealNameRequired`（是否需實名）。未提供時 MUST 視為 false，既有客戶端（未帶此欄位）的建立行為不受影響。此設定 MUST 隨活動建立一併儲存；系統 MUST NOT 提供任何在活動建立後變更此設定的途徑。本次變更前已存在的活動一律視為不需實名。後台專用的活動列表查詢端點 SHALL 在每筆活動附帶 `IsRealNameRequired`。

#### Scenario: EVT-REALNAME-001 建立需實名的活動
- **WHEN** 已切換至 Approved Organizer 的使用者建立活動，指定 `IsRealNameRequired = true`
- **THEN** 系統成功建立活動，該活動的 `IsRealNameRequired` 為 true

#### Scenario: EVT-REALNAME-002 未提供此欄位時視為不需實名
- **WHEN** 已切換至 Approved Organizer 的使用者以本次變更前的既有格式建立活動，請求內容不含 `IsRealNameRequired`
- **THEN** 系統成功建立活動，該活動的 `IsRealNameRequired` 為 false

#### Scenario: EVT-REALNAME-003 後台活動列表顯示是否需實名
- **WHEN** 已切換至 Organizer A 的使用者查詢後台專用的活動列表，Organizer A 名下同時有需實名與不需實名的活動
- **THEN** 每筆活動附帶與建立時一致的 `IsRealNameRequired`

#### Scenario: EVT-REALNAME-004 既有活動遷移後為不需實名
- **WHEN** 套用本次資料庫遷移前已存在的活動，在遷移後被查詢
- **THEN** 該活動的 `IsRealNameRequired` 為 false
