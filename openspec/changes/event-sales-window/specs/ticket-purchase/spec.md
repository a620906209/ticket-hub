## ADDED Requirements

### Requirement: 公開活動列表附帶販售期間
不需登入的公開活動列表查詢端點 SHALL 在每筆活動附帶 `SalesStartAtUtc`、`SalesEndAtUtc` 的原始值（可為 null，語意見 `event-management` 能力「建立活動時可指定販售期間」）。回應 MUST NOT 包含依當下時間推導的販售狀態：此列表可能來自 `query-caching` 能力的快取內容，推導狀態會隨時間過期，原始時間則建立後不可變。

部署前寫入、不含這兩個欄位的舊快取項目 MUST 被解讀為 null。這類項目通常屬於部署前建立的活動（兩欄位一律為 null）；滾動部署或回滾期間，舊程式碼也可能在最多一個快取 TTL 內寫入此格式，此時列表欄位暫時為 null，但建立訂單與加入排隊的檢查一律讀資料庫，不受影響。

#### Scenario: TP-BROWSE-SALES-001 公開活動列表附帶販售期間原始值
- **WHEN** 使用者（不論是否登入）查詢公開活動列表，資料庫中有設定販售期間與未設定的活動
- **THEN** 每筆活動附帶與建立時一致的 `SalesStartAtUtc`、`SalesEndAtUtc`，未設定者為 null，且回應不含販售狀態欄位

#### Scenario: TP-BROWSE-SALES-002 不含新欄位的舊快取項目解讀為 null
- **WHEN** 活動列表快取中存在本次變更前格式（不含兩個販售期間欄位）的項目，使用者查詢公開活動列表命中該快取
- **THEN** 回應中這些活動的兩欄位為 null，查詢不因欄位缺少而失敗

### Requirement: 建立訂單須在活動販售期間內
建立訂單時，若當下時間不在訂單所屬活動的實際販售期間 `[SalesStartAtUtc, SalesEndAtUtc ?? StartAtUtc)` 內，系統 MUST 拒絕建立訂單，不鎖定任何座位、不扣減任何庫存、不建立訂單：
- 早於開賣時間：回 409，ProblemDetails `Title = "SalesNotOpen"`
- 已到或超過停售時間：回 409，`Title = "SalesClosed"`

錯誤訊息只含活動 Id。當下時間一律取自伺服器時間，不採信客戶端提供的任何時間。

- **檢查時機**：
  - **交易外**：讀到活動時，在確認所有項目屬於同一場活動之後，實名檢查與每筆訂單限購張數檢查之前、開啟交易之前先檢查一次；讀不到活動時略過此次。
  - **交易內**：以鎖定重讀的活動、交易內重新取得的當下時間，再檢查一次。這次為最終判斷，在實名補位檢查、排隊資格檢查與任何座位或庫存鎖定之前執行，失敗時回滾交易。
  - 兩次檢查結果不同（例如交易外為可售、交易內已停售）是合法情形，以交易內為準，系統 MUST NOT 因此以非預期錯誤失敗。
- **適用範圍**：不論活動是否開啟熱門搶購模式都適用；排隊入場後建立訂單同樣檢查。
- **不受影響的操作**：確認訂單（模擬付款）與取消訂單 MUST NOT 檢查販售期間。停售前建立的 Pending 訂單，仍可在原持有期限內確認付款，或依既有規則取消、逾時取消。
- **未設定販售期間的活動**：只受「停售時間 = 活動開始時間」限制。

#### Scenario: TP-SALES-ORDER-001 開賣前下單被拒
- **WHEN** 買家對 `SalesStartAtUtc` 在當下時間之後的活動送出合法的建立訂單請求
- **THEN** 系統回傳 409、`Title = "SalesNotOpen"`，座位狀態與票種庫存不變，不建立訂單

#### Scenario: TP-SALES-ORDER-002 停售後下單被拒
- **WHEN** 買家對 `SalesEndAtUtc` 在當下時間之前（含相等）的活動送出合法的建立訂單請求
- **THEN** 系統回傳 409、`Title = "SalesClosed"`，座位狀態與票種庫存不變，不建立訂單

#### Scenario: TP-SALES-ORDER-003 販售期間內下單成功
- **WHEN** 買家在 `SalesStartAtUtc` 當下（含相等）至 `SalesEndAtUtc` 之前，對該活動送出合法的建立訂單請求
- **THEN** 系統成功建立訂單

#### Scenario: TP-SALES-ORDER-004 未設定停售時間時，活動開始後下單被拒
- **WHEN** 買家對 `SalesEndAtUtc = null`、`StartAtUtc` 在當下時間之前的活動送出建立訂單請求
- **THEN** 系統回傳 409、`Title = "SalesClosed"`，不建立訂單

#### Scenario: TP-SALES-ORDER-005 未設定販售期間、活動尚未開始時可下單
- **WHEN** 買家對兩欄位皆為 null、`StartAtUtc` 在未來的活動送出合法的建立訂單請求
- **THEN** 系統成功建立訂單（與本次變更前行為一致）

#### Scenario: TP-SALES-ORDER-006 同時違反販售期間與實名、限購時回報販售期間
- **WHEN** 尚未登記實名的買家，對尚未開賣、需實名、每筆限購 2 張的活動送出 3 張的建立訂單請求
- **THEN** 系統回傳 409、`Title = "SalesNotOpen"`，不回報實名或限購錯誤

#### Scenario: TP-SALES-ORDER-007 項目跨活動時先回報跨活動錯誤
- **WHEN** 買家送出的建立訂單請求，項目同時包含尚未開賣的活動 A 與活動 B
- **THEN** 系統回報既有的「所有項目須屬於同一場活動」驗證錯誤（400），不回報 `SalesNotOpen`

#### Scenario: TP-SALES-ORDER-008 交易外可售、交易內已停售時以交易內為準
- **WHEN** 買家下單；交易外檢查時當下時間在販售期間內，交易內重新取得的當下時間已到停售時間（以測試替身模擬時間前進）
- **THEN** 系統回傳 409、`Title = "SalesClosed"`，未鎖定任何座位或扣減庫存，交易回滾、不建立訂單，不以 500 失敗

#### Scenario: TP-SALES-ORDER-009 交易外讀不到活動時仍在交易內檢查
- **WHEN** 買家對已停售的活動下單；交易外的活動讀取回傳 null，交易內的鎖定重讀回傳該活動（以測試替身模擬）
- **THEN** 系統回傳 409、`Title = "SalesClosed"`，未檢查排隊資格、未鎖定任何座位或扣減庫存，不建立訂單

#### Scenario: TP-SALES-ORDER-010 排隊入場後建立訂單仍檢查販售期間
- **WHEN** 開啟熱門搶購模式且已停售的活動中，一位 `Admitted` 且未逾時的會員建立訂單
- **THEN** 系統回傳 409、`Title = "SalesClosed"`，不建立訂單，排隊紀錄狀態不變

#### Scenario: TP-SALES-ORDER-011 停售前建立的 Pending 訂單停售後仍可確認付款
- **WHEN** 買家在販售期間內建立 Pending 訂單，停售時間過後、訂單持有期限內確認付款
- **THEN** 訂單成功轉為 `Paid` 並出票，不回報 `SalesClosed`

#### Scenario: TP-SALES-ORDER-012 停售前建立的 Pending 訂單停售後仍可取消
- **WHEN** 買家在販售期間內建立 Pending 訂單，停售時間過後主動取消
- **THEN** 訂單成功取消，座位或庫存依既有規則釋放
