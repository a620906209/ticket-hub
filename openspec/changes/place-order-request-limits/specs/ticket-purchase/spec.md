## MODIFIED Requirements

### Requirement: 建立訂單時每筆訂單限購張數以購買數量加總計算
系統 SHALL 在活動設定每筆訂單限購張數（`Event.MaxTicketsPerOrder`）時，於建立訂單前檢查本次請求所有項目的購買數量總和（座位項目每筆固定計為 1，計數項目依指定數量計入）是否超過該上限；超過 MUST 拒絕建立訂單並回報驗證錯誤，不對任何座位或庫存執行變更。活動未設定此上限時不受活動限購限制，但仍受下述系統上限限制。

不論活動是否設定限購，系統 SHALL 對每次建立訂單請求套用系統上限 50：選購項目筆數 MUST ≤ 50，所有項目購買數量（依請求中的指定值）的總和 MUST ≤ 50；總和 MUST 以不會溢位的方式計算，購買數量接近整數最大值時 MUST 回報驗證錯誤而非伺服器錯誤。違反時系統 MUST 回傳 400，ProblemDetails 的 `detail` MUST 包含對應訊息：筆數超過為 `An order cannot contain more than 50 selections.`，總張數超過為 `An order cannot contain more than 50 tickets in total.`；不對任何座位或庫存執行變更。選購項目筆數超過上限時，`detail` MUST 只包含筆數訊息，不包含逐項驗證的訊息，避免回應大小隨請求項目數增加。活動限購與系統上限同時適用，實際可購買張數取兩者較小值。系統上限依請求中的指定值加總，座位項目指定了非 1 的購買數量時，若加總超過 50 會先以總張數訊息拒絕，未超過時才依「座位項目購買數量須為 1」的規則拒絕，兩者皆為 400。`Selections` 缺少或為 null 時 MUST 回報驗證錯誤（400），不得回傳 500。`Selections` 含 null 元素時 MUST 回傳 400，`detail` MUST 包含 `Selections must not contain null items.`，不得回傳 500。

#### Scenario: 純座位訂單超過限購張數
- **WHEN** 活動設定每筆訂單限購 4 張，已登入會員選定 5 個座位建立訂單
- **THEN** 系統 MUST 拒絕建立訂單並回報已達限購張數，不對任何座位執行鎖定

#### Scenario: 純計數訂單超過限購張數
- **WHEN** 活動設定每筆訂單限購 4 張，已登入會員對一個 `RequiresSeat = false` 的票種指定購買數量 5 建立訂單
- **THEN** 系統 MUST 拒絕建立訂單並回報已達限購張數，不扣減任何庫存

#### Scenario: 混合座位與計數項目的數量加總超過限購張數
- **WHEN** 活動設定每筆訂單限購 4 張，已登入會員選定 2 個座位，並對一個計數票種指定購買數量 3（合計 5 張）建立訂單
- **THEN** 系統 MUST 拒絕建立訂單並回報已達限購張數，不對任何座位或庫存執行變更

#### Scenario: 混合項目的數量加總未超過限購張數
- **WHEN** 活動設定每筆訂單限購 4 張，已登入會員選定 2 個座位，並對一個計數票種指定購買數量 2（合計 4 張）建立訂單
- **THEN** 系統成功建立訂單

#### Scenario: TP-LIMIT-001 活動未設定限購時總張數超過系統上限被拒
- **WHEN** 活動未設定每筆訂單限購張數，已登入會員對一個 `RequiresSeat = false` 且可售量足夠的票種指定購買數量 51 建立訂單
- **THEN** 系統回傳 400，`detail` 包含 `An order cannot contain more than 50 tickets in total.`，不建立訂單、該票種可售量不變

#### Scenario: TP-LIMIT-002 總張數恰為系統上限時成功
- **WHEN** 活動未設定每筆訂單限購張數，已登入會員對一個 `RequiresSeat = false` 且可售量足夠的票種指定購買數量 50 建立訂單
- **THEN** 系統成功建立 Pending 訂單，該票種可售量扣減 50

#### Scenario: TP-LIMIT-003 選購筆數超過系統上限時只回報筆數錯誤
- **WHEN** 已登入會員送出 51 筆選購項目的建立訂單請求，且每筆項目本身也不合格（例如 `TicketTypeId` 為空 Guid）
- **THEN** 系統回傳 400，`detail` 等於 `An order cannot contain more than 50 selections.`，不含逐項驗證的訊息，不建立訂單

#### Scenario: TP-LIMIT-004 購買數量加總溢位不造成伺服器錯誤
- **WHEN** 已登入會員送出兩筆計數項目（不同票種），購買數量皆為整數最大值 `2147483647`
- **THEN** 系統回傳 400，`detail` 包含 `An order cannot contain more than 50 tickets in total.`，不回傳 500

#### Scenario: TP-LIMIT-005 活動限購高於系統上限時以系統上限為準
- **WHEN** 活動的每筆訂單限購張數為 100（本規則生效前建立的既有資料），已登入會員對該活動的計數票種指定購買數量 51 建立訂單
- **THEN** 系統回傳 400，`detail` 包含 `An order cannot contain more than 50 tickets in total.`，不扣減任何庫存

#### Scenario: TP-LIMIT-006 選購項目含 null 元素時回報驗證錯誤
- **WHEN** 已登入會員送出 `Selections` 含 null 元素的建立訂單請求（例如 `{"selections":[null]}`，或一筆合格項目加一個 null）
- **THEN** 系統回傳 400，`detail` 包含 `Selections must not contain null items.`，不回傳 500，不建立訂單

#### Scenario: TP-LIMIT-007 Selections 缺少或為 null 時回報驗證錯誤
- **WHEN** 已登入會員送出未帶 `selections` 的建立訂單請求（`{}`），或 `selections` 為 null（`{"selections":null}`）
- **THEN** 系統回傳 400，不回傳 500，不建立訂單
