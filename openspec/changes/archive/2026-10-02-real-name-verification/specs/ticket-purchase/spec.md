## ADDED Requirements

### Requirement: 公開活動列表附帶是否需實名
不需登入的公開活動列表查詢端點 SHALL 在每筆活動附帶 `IsRealNameRequired`，供前端顯示「本活動需實名」標示。此欄位可能來自 `query-caching` 能力的快取內容。由於此設定建立後不可變更，快取值與資料庫值不會因設定變更而不一致。部署前寫入、不含此欄位的舊快取項目 MUST 被解讀為 false；這些項目只可能是部署前建立的活動，而那些活動一律不需實名。

#### Scenario: TP-BROWSE-RN-001 公開活動列表附帶是否需實名
- **WHEN** 使用者（不論是否登入）查詢公開活動列表，資料庫中有需實名與不需實名的活動
- **THEN** 每筆活動附帶與建立時一致的 `IsRealNameRequired`

#### Scenario: TP-BROWSE-RN-002 不含新欄位的舊快取項目解讀為不需實名
- **WHEN** 活動列表快取中存在本次變更前格式（不含 `isRealNameRequired` 欄位）的項目，使用者查詢公開活動列表命中該快取
- **THEN** 回應中這些活動的 `IsRealNameRequired` 為 false，查詢不因欄位缺少而失敗

### Requirement: 需實名的活動建立訂單前買家須已登記實名
建立訂單時，若訂單所屬活動的 `IsRealNameRequired = true`，而買家（JWT 中的會員）尚未登記實名，系統 MUST 拒絕建立訂單，不鎖定任何座位、不扣減任何庫存、不建立訂單。拒絕回應為 403，ProblemDetails `Title` 為 `RealNameRequired`，供前端與其他 403 區分並引導買家登記。

- **檢查時機與讀取來源**：
  - 交易外讀到的活動存在時，MUST 以它的 `IsRealNameRequired` 判斷，並在確認所有項目屬於同一場活動之後、每筆訂單限購張數檢查之前、開啟任何資料庫交易或取得任何鎖之前執行。限購超過且未登記實名時，回應 `RealNameRequired`。
  - 交易外讀不到活動、但交易內鎖定重讀時讀得到，MUST 改以交易內讀到的 `IsRealNameRequired` 判斷，並在排隊資格檢查與任何座位或庫存鎖定之前執行；失敗時回滾交易。系統 MUST NOT 因交易外讀不到活動而跳過實名檢查。
  - 兩次讀取都讀得到、但 `IsRealNameRequired` 不同時（代表「設定建立後不可變」的不變量被破壞），系統 MUST 以非預期錯誤失敗（500），不建立訂單、不變更任何座位或庫存。
- **適用範圍**：不論活動是否開啟熱門搶購模式都適用；排隊入場後建立訂單同樣檢查。
- **不需實名的活動**：建立訂單行為與本次變更前完全一致，不查詢買家實名資料。

#### Scenario: TP-RN-ORDER-001 未登記實名的買家對需實名活動下單被拒
- **WHEN** 尚未登記實名的已登入會員對 `IsRealNameRequired = true` 的活動送出合法的建立訂單請求
- **THEN** 系統回傳 403、`Title = "RealNameRequired"`，座位狀態與票種庫存維持不變，不建立訂單

#### Scenario: TP-RN-ORDER-002 已登記實名的買家對需實名活動下單成功
- **WHEN** 已登記實名的會員對 `IsRealNameRequired = true` 的活動送出合法的建立訂單請求
- **THEN** 系統成功建立訂單

#### Scenario: TP-RN-ORDER-003 不需實名的活動不受影響
- **WHEN** 尚未登記實名的會員對 `IsRealNameRequired = false` 的活動送出合法的建立訂單請求
- **THEN** 系統成功建立訂單

#### Scenario: TP-RN-ORDER-004 同時違反實名與限購時回報實名
- **WHEN** 尚未登記實名的會員對設定每筆限購 2 張、且需實名的活動送出 3 張的建立訂單請求
- **THEN** 系統回傳 403、`Title = "RealNameRequired"`，不回報限購錯誤

#### Scenario: TP-RN-ORDER-005 排隊入場後建立訂單仍檢查實名
- **WHEN** 需實名且開啟熱門搶購模式的活動中，一位已是 `Admitted` 狀態、但未登記實名的會員建立訂單。正常流程下排隊閘門會擋住未登記者，這個狀態不可達；本情境用測試資料直接建立排隊紀錄，驗證建立訂單不依賴排隊閘門
- **THEN** 系統回傳 403、`Title = "RealNameRequired"`，不建立訂單，排隊紀錄狀態不變

#### Scenario: TP-RN-ORDER-006 項目跨活動時先回報跨活動錯誤
- **WHEN** 尚未登記實名的會員送出建立訂單請求，項目同時包含需實名活動 A 的座位與活動 B 的票種
- **THEN** 系統回報既有的「所有項目須屬於同一場活動」驗證錯誤（400），不回報 `RealNameRequired`，不鎖定任何座位或庫存

#### Scenario: TP-RN-ORDER-007 交易外讀不到活動時仍在交易內檢查實名
- **WHEN** 未登記實名的會員對需實名活動下單；交易外的活動讀取回傳 null，交易內的鎖定重讀回傳該需實名活動（以測試替身模擬）
- **THEN** 系統回傳 403、`Title = "RealNameRequired"`，未檢查排隊資格，未鎖定任何座位或扣減庫存，交易回滾、不建立訂單

#### Scenario: TP-RN-ORDER-008 兩次讀取的實名設定不一致時失敗
- **WHEN** 已登記或未登記實名的會員下單；交易外讀到的活動 `IsRealNameRequired = false`，交易內鎖定重讀的同一活動為 true（以測試替身模擬不變量被破壞）
- **THEN** 系統以非預期錯誤失敗（500），不建立訂單，座位與庫存不變

#### Scenario: TP-RN-ORDER-009 兩次讀取不一致（交易外為需實名、交易內為不需實名）時同樣失敗
- **WHEN** 已登記實名的會員下單（通過交易外的主要檢查）；交易外讀到的活動 `IsRealNameRequired = true`，交易內鎖定重讀的同一活動為 false（以測試替身模擬不變量被破壞）
- **THEN** 系統以非預期錯誤失敗（500），不建立訂單，座位與庫存不變
