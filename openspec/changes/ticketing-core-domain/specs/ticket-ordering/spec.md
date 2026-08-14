## ADDED Requirements

### Requirement: 建立訂單並原子性鎖定座位
系統 SHALL 允許買家選定一組 `EventSeat` 建立訂單；建立過程 MUST 對所選所有座位以同一個到期時間嘗試鎖定，只有全部座位皆鎖定成功時才建立狀態為 Pending 的訂單，任一座位鎖定失敗時 MUST 不建立訂單，且已嘗試鎖定的其他座位 MUST 以訂單編號限定的方式復原為鎖定前的狀態，不得殘留部分鎖定、也不得誤釋放非本次建立流程鎖定的座位。訂單內每筆 `OrderItem` MUST 直接關聯對應的 `EventSeat`（而非共用的座位樣板 `Seat`）。此處的原子性為應用程式流程內的循序保證，非資料庫交易。

在嘗試鎖定任何座位之前，系統 MUST 先驗證：所選座位不得有重複、所選座位須全部屬於同一場活動、每個座位對應的票種須與該座位屬於同一場活動。任一項驗證失敗，MUST 直接拒絕建立訂單且不得對任何座位執行鎖定。

#### Scenario: 所選座位皆可鎖定
- **WHEN** 買家選定多個皆為 Available 狀態的 `EventSeat` 建立訂單
- **THEN** 系統建立狀態為 Pending 的訂單，且所選座位皆轉為 Held 並歸屬此訂單，逾時時間與訂單的到期時間相同

#### Scenario: 所選座位其中一個已被鎖定
- **WHEN** 買家選定的座位中，有一個座位已處於未逾時的 Held 狀態
- **THEN** 系統 MUST 拒絕建立訂單，且本次操作中其餘已鎖定成功的座位 MUST 以本次建立所用的訂單編號限定釋放，復原為 Available，不留下部分鎖定，原本已被其他訂單持有的座位維持不變

#### Scenario: 所選座位重複
- **WHEN** 買家選定的座位清單中，同一個 `EventSeat` 出現兩次
- **THEN** 系統 MUST 拒絕建立訂單，且不對任何座位執行鎖定

#### Scenario: 所選座位橫跨多場活動
- **WHEN** 買家選定的座位分別屬於不同的活動
- **THEN** 系統 MUST 拒絕建立訂單，且不對任何座位執行鎖定

#### Scenario: 票種與座位所屬活動不一致
- **WHEN** 買家選定的某個座位所屬活動，與其對應票種所屬活動不同
- **THEN** 系統 MUST 拒絕建立訂單，且不對任何座位執行鎖定

### Requirement: 訂單暫扣快照票價
系統 SHALL 在建立訂單的每筆 `OrderItem` 時，將所選座位對應票種當下的票價複製為該 `OrderItem` 的單價；建立後即使票種票價變更，MUST NOT 影響已建立訂單的金額。

#### Scenario: 建立訂單時複製當下票價
- **WHEN** 買家以票價 500 的票種建立訂單
- **THEN** 訂單內對應 `OrderItem` 的單價記錄為 500

#### Scenario: 票種事後調整票價不影響既有訂單
- **WHEN** 訂單建立後，該票種的票價被調整為 600
- **THEN** 該筆已建立訂單的 `OrderItem` 單價仍為建立當下記錄的 500

### Requirement: 訂單只有單一到期時間
系統 SHALL 讓每筆訂單只持有一個到期時間；訂單是否逾時 MUST 只依此單一到期時間與目前時間比較判斷，MUST NOT 逐一檢查訂單內每個座位各自的暫扣狀態來判斷訂單是否逾時。

#### Scenario: 訂單到期時間套用至所有座位
- **WHEN** 建立一筆包含多個座位的訂單
- **THEN** 訂單記錄的到期時間，與訂單內每個座位鎖定時使用的逾時時間相同

### Requirement: 確認訂單須驗證訂單與座位歸屬一致
系統 SHALL 在將任何座位標記為 Sold 之前，完整驗證：(1) 訂單狀態為 Pending、(2) 訂單本身尚未逾時、(3) 每一筆 `OrderItem` 對應的座位皆能成功解析到存在的 `EventSeat`、(4) 該 `EventSeat` 所屬活動須與訂單所屬活動一致、(5) 訂單內每一筆 `OrderItem` 對應的 `EventSeat` 經由座位狀態查詢方法確認仍由本訂單合法持有暫扣（未售出、鎖定訂單編號相符、未逾時）。上述驗證一律透過 `EventSeat` 對外暴露的狀態查詢方法進行，不得直接讀取內部欄位判斷。任一驗證失敗，MUST 不變更任何座位或訂單狀態；只有全部驗證通過，才將訂單轉為 Confirmed 並將訂單內所有座位轉為 Sold。此驗證機制用以防止「確認某筆訂單卻誤將另一筆訂單持有的座位標記售出」的錯誤。

#### Scenario: 於到期時間內確認訂單且座位歸屬一致
- **WHEN** 對尚未逾時的 Pending 訂單執行確認，且訂單內每個 `OrderItem` 對應的座位皆仍由該訂單持有暫扣
- **THEN** 訂單狀態轉為 Confirmed，訂單內所有座位轉為 Sold

#### Scenario: 訂單已逾時仍嘗試確認
- **WHEN** 對已逾時的 Pending 訂單執行確認
- **THEN** 系統 MUST 拒絕確認，不將任何座位轉為 Sold，訂單狀態維持 Pending（查詢時依單一到期時間推導為 Expired，見「訂單逾時為查詢時推導」）

#### Scenario: 訂單內座位已不再由本訂單持有
- **WHEN** 對 Pending 訂單執行確認，但訂單內某筆 `OrderItem` 對應的 `EventSeat` 目前已由其他訂單持有暫扣或已售出
- **THEN** 系統 MUST 拒絕確認，不變更任何座位狀態

#### Scenario: 座位不屬於訂單所屬活動
- **WHEN** 對 Pending 訂單執行確認，但訂單內某筆 `OrderItem` 解析到的 `EventSeat` 所屬活動與訂單所屬活動不同
- **THEN** 系統 MUST 拒絕確認，不變更任何座位狀態

### Requirement: 取消訂單，統一處理主動取消與逾時清理
系統 SHALL 允許將內部狀態為 Pending 的訂單取消，無論該訂單依到期時間判斷是否已逾時；取消時 MUST 對訂單內每筆 `OrderItem` 對應的 `EventSeat`，以本訂單編號限定釋放（座位若已被其他訂單於逾時後重新鎖定，則不釋放該座位），並將訂單狀態設為 Cancelled。內部狀態非 Pending 的訂單（包含 Confirmed 與 Cancelled）MUST 被拒絕取消——Cancelled 是終態，不可重複取消。

在對任何座位執行釋放之前，系統 MUST 先檢查訂單內是否有座位已被標記 Sold（可能因座位逾時後被其他訂單搶走並完成售出）；只要有一個座位已售出，MUST 直接拒絕整筆取消、不釋放任何座位、不變更訂單狀態，並以失敗結果回報，不得讓座位層級的例外往外傳播。

#### Scenario: 取消尚未逾時的 Pending 訂單
- **WHEN** 對狀態為 Pending 且尚未逾時的訂單執行取消
- **THEN** 訂單狀態轉為 Cancelled，訂單內所有座位（仍由本訂單持有者）釋放回 Available

#### Scenario: 取消已逾時的 Pending 訂單
- **WHEN** 對狀態為 Pending 但依到期時間判斷已逾時的訂單執行取消
- **THEN** 訂單狀態轉為 Cancelled；訂單內仍由本訂單持有暫扣的座位釋放回 Available，已被其他訂單重新鎖定的座位維持不變

#### Scenario: 嘗試取消已確認的訂單
- **WHEN** 對狀態為 Confirmed 的訂單執行取消
- **THEN** 系統 MUST 拒絕此次操作，訂單狀態維持 Confirmed

#### Scenario: 嘗試取消已取消的訂單
- **WHEN** 對狀態為 Cancelled 的訂單再次執行取消
- **THEN** 系統 MUST 拒絕此次操作，訂單狀態維持 Cancelled

#### Scenario: 訂單座位已被其他訂單售出
- **WHEN** 對 Pending 訂單執行取消，但訂單內某筆 `OrderItem` 對應的 `EventSeat` 目前已因逾時後被其他訂單重新鎖定並完成售出而標記為 Sold
- **THEN** 系統 MUST 以失敗結果拒絕此次取消，不釋放訂單內任何座位，訂單狀態維持 Pending，且不得拋出未攔截的例外

### Requirement: 訂單逾時為查詢時推導，不落地寫入狀態
系統 SHALL 只在查詢訂單狀態時，依單一到期時間與目前時間推導並回報 Expired；訂單內部持久化的狀態欄位 MUST NOT 被直接寫入 Expired 值，該欄位只會是 Pending、Confirmed、Cancelled 三者之一。若要讓一筆已逾時的 Pending 訂單進入終態，須透過取消訂單的操作處理，處理後訂單的持久化狀態為 Cancelled。

#### Scenario: 查詢已逾時的 Pending 訂單
- **WHEN** 查詢一筆內部狀態仍為 Pending、但已超過到期時間的訂單狀態
- **THEN** 系統回報訂單狀態為 Expired，但訂單內部持久化狀態欄位仍維持 Pending
