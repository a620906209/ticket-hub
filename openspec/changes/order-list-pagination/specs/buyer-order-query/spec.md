## MODIFIED Requirements

### Requirement: 買家可查詢自己的訂單列表
系統 SHALL 提供已登入會員分頁查詢自己訂單的端點（分頁參數、驗證與回應結構見 `list-pagination` 能力），只回傳呼叫者身份為買家（`BuyerId`）的訂單，MUST NOT 回傳其他會員的訂單。每筆訂單摘要 SHALL 包含訂單 Id、所屬活動 Id、所屬活動名稱（`EventTitle`）、訂單狀態、持有到期時間；列表 SHALL 依建立時間由新到舊排序，建立時間相同時依訂單 Id 升冪排序，使同一份資料每次查詢的順序相同；排序、分頁與總筆數 MUST 在資料庫端完成，MUST NOT 先載入該會員全部訂單再於記憶體中切頁。時間欄位一律為 ISO 8601 UTC 格式（比照既有 `ticket-purchase`／`order-administration` 能力的既定慣例）。持有到期時間（`HeldUntilUtc`）SHALL 一律回傳建立訂單當下設定的原始值，不因訂單狀態轉為 Paid、Cancelled 或查詢時推導為 Expired 而清空或改寫——此欄位是歷史記錄用途的原始時間戳，「訂單目前是否仍在保留中」一律以訂單狀態欄位判斷，呼叫端 MUST NOT 用此欄位是否已過期來判斷訂單目前狀態。活動名稱 SHALL 以單次批次查詢取得當頁列表中所有不重複活動，查詢次數 MUST NOT 隨當頁訂單筆數成長。當頁訂單的 `EventId` 對應的活動查不到時，視為資料不一致，系統 SHALL 以非預期錯誤失敗（由全域例外處理轉為 500），MUST NOT 回傳部分資料或以空值代替活動名稱。

#### Scenario: BOQ-LIST-001 查詢自己的訂單列表
- **WHEN** 已登入會員呼叫訂單列表端點
- **THEN** 系統回傳僅屬於該會員的訂單摘要分頁（`items`），`totalCount` 僅計入該會員的訂單，不含任何其他會員的訂單

#### Scenario: BOQ-LIST-002 尚未有任何訂單
- **WHEN** 已登入會員從未建立過訂單，呼叫訂單列表端點
- **THEN** 系統回傳空的 `items` 與 `totalCount` 為 0，不視為錯誤

#### Scenario: BOQ-LIST-TITLE-001 訂單摘要包含所屬活動名稱
- **WHEN** 已登入會員在兩個不同活動各有訂單，呼叫訂單列表端點
- **THEN** 每筆訂單摘要的 `EventTitle` 為其所屬活動的名稱

#### Scenario: BOQ-LIST-TITLE-003 活動名稱的查詢次數不隨訂單筆數成長
- **WHEN** 已登入會員分別在「1 筆訂單、1 個活動」與「3 筆訂單、3 個不同活動」的資料下呼叫訂單列表端點
- **THEN** 兩次請求對資料庫發出的查詢次數相同（活動名稱仍只以 1 次批次查詢取得）

#### Scenario: BOQ-LIST-TITLE-002 訂單所屬活動查不到（資料不一致）
- **WHEN** 已登入會員的某筆訂單其 `EventId` 對應的活動在資料庫中不存在
- **THEN** 該筆訂單落在被請求的頁面時，系統以非預期錯誤失敗（500），不回傳該頁；落在其他頁面時不影響本頁

#### Scenario: BOQ-LIST-ORDER-001 買家訂單列表依建立時間由新到舊排序
- **WHEN** 已登入會員有三筆訂單，建立時間依序為 T1 < T2 < T3，且在資料庫中不是依建立時間順序存放，呼叫訂單列表端點
- **THEN** `items` 順序為 T3、T2、T1 的訂單

#### Scenario: BOQ-LIST-ORDER-002 建立時間相同的訂單依 Id 排序且分頁不重複不遺漏
- **WHEN** 已登入會員有 3 筆建立時間相同的訂單，以 `pageSize` 為 2 依序請求第 1、2 頁
- **THEN** 兩頁合併後恰為這 3 筆訂單各出現一次，且順序依訂單 Id 升冪

#### Scenario: BOQ-LIST-PAGE-001 買家訂單列表依 page 與 pageSize 切頁
- **WHEN** 已登入會員有 5 筆訂單，以 `page` 為 2、`pageSize` 為 2 呼叫訂單列表端點
- **THEN** `items` 為排序後的第 3、4 筆，`page` 為 2、`pageSize` 為 2、`totalCount` 為 5
