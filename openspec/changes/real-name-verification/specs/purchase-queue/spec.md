## ADDED Requirements

### Requirement: 需實名的活動加入排隊前買家須已登記實名
加入排隊端點 `POST /api/events/{id}/queue/entries` 處理時，若活動 `IsRealNameRequired = true` 而呼叫者尚未登記實名，系統 MUST 拒絕加入，不建立、不修改任何排隊紀錄（含不將逾時的 `Admitted` 紀錄轉為 `Expired`）。拒絕回應為 403，ProblemDetails `Title` 為 `RealNameRequired`。

- **檢查時機**：MUST 在驗證碼驗證通過、確認活動存在且已開啟熱門搶購模式之後，在開啟加入排隊交易之前。驗證碼仍是第一道檢查（既有規則「驗證碼先於任何查詢」不變）。
- **與 PQ-JOIN-002 的關係**：已有進行中紀錄的會員也同樣先經過此檢查。由於實名登記不可撤銷，已登記者不會因此被擋。
- **讀取來源**：以交易外讀到的活動判斷（活動不存在時既有流程已提前回 404，不會跳過檢查）。交易內鎖定重讀的活動 `IsRealNameRequired` 與交易外不同時，MUST 以非預期錯誤失敗（500），不建立或修改任何排隊紀錄。
- **定位**：此檢查只是提早告知；建立訂單時 `ticket-purchase` 能力的實名檢查才是最終把關。
- **不需實名的活動**：加入排隊行為與本次變更前完全一致。

#### Scenario: PQ-RN-JOIN-001 未登記實名的會員加入需實名活動排隊被拒
- **WHEN** 尚未登記實名的已登入會員，對 `IsRealNameRequired = true` 且已開啟熱門搶購模式的活動呼叫加入排隊，並提供正確的驗證碼
- **THEN** 系統回傳 403、`Title = "RealNameRequired"`，不建立排隊紀錄

#### Scenario: PQ-RN-JOIN-002 已登記實名的會員可加入需實名活動排隊
- **WHEN** 已登記實名的會員，對 `IsRealNameRequired = true` 且已開啟熱門搶購模式的活動呼叫加入排隊，並提供正確的驗證碼
- **THEN** 系統建立一筆 `Waiting` 排隊紀錄

#### Scenario: PQ-RN-JOIN-003 驗證碼錯誤時先回報驗證碼錯誤
- **WHEN** 尚未登記實名的會員，對需實名活動呼叫加入排隊，但驗證碼錯誤
- **THEN** 系統回報驗證碼錯誤（既有 `CaptchaInvalid` 行為），不回報 `RealNameRequired`

#### Scenario: PQ-RN-JOIN-004 不需實名的活動不受影響
- **WHEN** 尚未登記實名的會員，對 `IsRealNameRequired = false` 且已開啟熱門搶購模式的活動呼叫加入排隊，並提供正確的驗證碼
- **THEN** 系統建立一筆 `Waiting` 排隊紀錄

#### Scenario: PQ-RN-JOIN-005 活動不存在時先回報找不到
- **WHEN** 尚未登記實名的會員，對不存在的活動 Id 呼叫加入排隊，並提供正確的驗證碼
- **THEN** 系統回傳既有的 404，不回報 `RealNameRequired`

#### Scenario: PQ-RN-JOIN-006 未開啟熱門搶購模式時先回報衝突
- **WHEN** 尚未登記實名的會員，對 `IsRealNameRequired = true` 但 `IsQueueModeEnabled = false` 的活動呼叫加入排隊，並提供正確的驗證碼
- **THEN** 系統回傳既有的 409（未開啟熱門搶購模式），不回報 `RealNameRequired`

#### Scenario: PQ-RN-JOIN-007 兩次讀取的實名設定不一致時失敗
- **WHEN** 已登記實名的會員加入排隊；交易外讀到的活動 `IsRealNameRequired = false`，交易內鎖定重讀的同一活動為 true（以測試替身模擬不變量被破壞）
- **THEN** 系統以非預期錯誤失敗（500），不建立排隊紀錄

#### Scenario: PQ-RN-JOIN-008 兩次讀取不一致（交易外為需實名、交易內為不需實名）時同樣失敗
- **WHEN** 已登記實名的會員加入排隊；交易外讀到的活動 `IsRealNameRequired = true`，交易內鎖定重讀的同一活動為 false（以測試替身模擬不變量被破壞）
- **THEN** 系統以非預期錯誤失敗（500），不建立排隊紀錄
