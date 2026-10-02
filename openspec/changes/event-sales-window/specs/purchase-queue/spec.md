## ADDED Requirements

### Requirement: 加入排隊須在活動販售期間內
加入排隊端點 `POST /api/events/{id}/queue/entries` 處理時，若當下時間不在活動的實際販售期間 `[SalesStartAtUtc, SalesEndAtUtc ?? StartAtUtc)` 內（語意見 `event-management` 能力），系統 MUST 拒絕加入：
- 早於開賣時間：回 409，ProblemDetails `Title = "SalesNotOpen"`
- 已到或超過停售時間：回 409，`Title = "SalesClosed"`

拒絕時 MUST NOT 建立、回傳或修改任何排隊紀錄，包含不將逾時的 `Admitted` 紀錄轉為 `Expired`。沒有開賣前的預先等候室。

- **檢查時機**：
  - **交易外**：在驗證碼驗證通過、確認活動存在之後，熱門搶購模式檢查與實名檢查之前先檢查一次。驗證碼仍是第一道檢查。
  - **交易內**：以鎖定重讀的活動、交易內重新取得的當下時間，再檢查一次。這次為最終判斷，在熱門搶購模式重驗與任何排隊紀錄查詢或寫入之前執行。
  - 兩次結果不同是合法情形，以交易內為準，系統 MUST NOT 因此以非預期錯誤失敗。
- **與 PQ-JOIN-002 的關係**：已有進行中紀錄的會員，在販售期間外呼叫同樣被拒，不回傳既有紀錄。
- **不受影響**：查詢排隊狀態端點（`GET`）不檢查販售期間；入場推進服務不因停售而停止，被放行者建立訂單時由 `ticket-purchase` 能力的販售期間檢查把關。

#### Scenario: PQ-SALES-JOIN-001 開賣前加入排隊被拒
- **WHEN** 已登入會員對已開啟熱門搶購模式、`SalesStartAtUtc` 在當下時間之後的活動呼叫加入排隊，並提供正確的驗證碼
- **THEN** 系統回傳 409、`Title = "SalesNotOpen"`，不建立排隊紀錄

#### Scenario: PQ-SALES-JOIN-002 停售後加入排隊被拒
- **WHEN** 已登入會員對已開啟熱門搶購模式、已停售的活動呼叫加入排隊，並提供正確的驗證碼
- **THEN** 系統回傳 409、`Title = "SalesClosed"`，不建立排隊紀錄

#### Scenario: PQ-SALES-JOIN-003 販售期間內可加入排隊
- **WHEN** 已登入會員在販售期間內，對已開啟熱門搶購模式的活動呼叫加入排隊，並提供正確的驗證碼
- **THEN** 系統建立一筆 `Waiting` 排隊紀錄

#### Scenario: PQ-SALES-JOIN-004 驗證碼錯誤時先回報驗證碼錯誤
- **WHEN** 會員對尚未開賣的活動呼叫加入排隊，但驗證碼錯誤
- **THEN** 系統回報既有的 `CaptchaInvalid`，不回報 `SalesNotOpen`

#### Scenario: PQ-SALES-JOIN-005 尚未開賣且未開啟熱門搶購模式時回報尚未開賣
- **WHEN** 會員對尚未開賣、`IsQueueModeEnabled = false` 的活動呼叫加入排隊，並提供正確的驗證碼
- **THEN** 系統回傳 409、`Title = "SalesNotOpen"`，不回報「未開啟熱門搶購模式」

#### Scenario: PQ-SALES-JOIN-006 尚未開賣且未登記實名時回報尚未開賣
- **WHEN** 未登記實名的會員，對尚未開賣、需實名且已開啟熱門搶購模式的活動呼叫加入排隊，並提供正確的驗證碼
- **THEN** 系統回傳 409、`Title = "SalesNotOpen"`，不回報 `RealNameRequired`

#### Scenario: PQ-SALES-JOIN-007 停售後已有進行中紀錄者再次呼叫被拒，紀錄不變
- **WHEN** 會員在販售期間內已有一筆已逾時的 `Admitted` 紀錄，停售後再次呼叫加入排隊，並提供正確的驗證碼
- **THEN** 系統回傳 409、`Title = "SalesClosed"`，該紀錄仍為 `Admitted`（未被轉為 `Expired`），不建立新紀錄

#### Scenario: PQ-SALES-JOIN-008 交易外可售、交易內已停售時以交易內為準
- **WHEN** 會員加入排隊；交易外檢查時在販售期間內，交易內重新取得的當下時間已到停售時間（以測試替身模擬）
- **THEN** 系統回傳 409、`Title = "SalesClosed"`，不建立排隊紀錄，不以 500 失敗

#### Scenario: PQ-SALES-JOIN-009 停售後仍可查詢排隊狀態
- **WHEN** 會員在販售期間內加入排隊後，停售時間已過，查詢自己的排隊狀態
- **THEN** 系統照常回傳該會員的排隊紀錄狀態
