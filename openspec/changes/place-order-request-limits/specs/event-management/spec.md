## ADDED Requirements

### Requirement: 建立活動時每筆訂單限購張數不得超過系統上限
建立活動時若提供每筆訂單限購張數（`MaxTicketsPerOrder`），其值 MUST ≤ 50（與 `ticket-purchase` 能力的建立訂單系統上限相同），避免買家頁顯示的限購值大於實際可下單的張數。超過時系統 MUST 回傳 400，ProblemDetails 的 `detail` MUST 包含 `MaxTicketsPerOrder must not exceed 50.`，不建立活動與任何 `EventSeat`。未提供時維持不設活動限購。本規則生效前已建立、限購值大於 50 的活動，讀取 MUST 不受影響。

#### Scenario: EVT-LIMIT-001 限購張數超過系統上限被拒
- **WHEN** 已切換至 Approved Organizer 的使用者建立活動，`MaxTicketsPerOrder` 為 51，其餘欄位合法
- **THEN** 系統回傳 400，`detail` 包含 `MaxTicketsPerOrder must not exceed 50.`，不建立活動與任何 `EventSeat`

#### Scenario: EVT-LIMIT-002 限購張數恰為系統上限時成功
- **WHEN** 已切換至 Approved Organizer 的使用者建立活動，`MaxTicketsPerOrder` 為 50，其餘欄位合法
- **THEN** 系統成功建立活動，查詢後台活動列表時該活動的 `MaxTicketsPerOrder` 為 50

#### Scenario: EVT-LIMIT-003 既有限購高於系統上限的活動仍可讀取
- **WHEN** 資料庫中已有一筆 `MaxTicketsPerOrder` 為 100 的活動（本規則生效前寫入），匿名查詢活動列表，以及已切換至該活動所屬 Organizer 的使用者查詢後台活動列表
- **THEN** 兩者皆成功，回傳的 `MaxTicketsPerOrder` 為 100
