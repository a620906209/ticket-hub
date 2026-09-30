## MODIFIED Requirements

### Requirement: 買家可查詢自己的訂單列表
系統 SHALL 提供已登入會員查詢自己所有訂單的端點，只回傳呼叫者身份為買家（`BuyerId`）的訂單，MUST NOT 回傳其他會員的訂單。每筆訂單摘要 SHALL 包含訂單 Id、所屬活動 Id、所屬活動名稱（`EventTitle`）、訂單狀態、持有到期時間；時間欄位一律為 ISO 8601 UTC 格式（比照既有 `ticket-purchase`／`order-administration` 能力的既定慣例）。持有到期時間（`HeldUntilUtc`）SHALL 一律回傳建立訂單當下設定的原始值，不因訂單狀態轉為 Paid、Cancelled 或查詢時推導為 Expired 而清空或改寫——此欄位是歷史記錄用途的原始時間戳，「訂單目前是否仍在保留中」一律以訂單狀態欄位判斷，呼叫端 MUST NOT 用此欄位是否已過期來判斷訂單目前狀態。活動名稱 SHALL 以單次批次查詢取得列表中所有不重複活動，查詢次數 MUST NOT 隨訂單筆數成長。訂單的 `EventId` 對應的活動查不到時，視為資料不一致，系統 SHALL 以非預期錯誤失敗（由全域例外處理轉為 500），MUST NOT 回傳部分資料或以空值代替活動名稱。

#### Scenario: 查詢自己的訂單列表
- **WHEN** 已登入會員呼叫訂單列表端點
- **THEN** 系統回傳僅屬於該會員的訂單摘要清單，不含任何其他會員的訂單

#### Scenario: 尚未有任何訂單
- **WHEN** 已登入會員從未建立過訂單，呼叫訂單列表端點
- **THEN** 系統回傳空清單，不視為錯誤

#### Scenario: BOQ-LIST-TITLE-001 訂單摘要包含所屬活動名稱
- **WHEN** 已登入會員在兩個不同活動各有訂單，呼叫訂單列表端點
- **THEN** 每筆訂單摘要的 `EventTitle` 為其所屬活動的名稱

#### Scenario: BOQ-LIST-TITLE-002 訂單所屬活動查不到（資料不一致）
- **WHEN** 已登入會員的某筆訂單其 `EventId` 對應的活動在資料庫中不存在
- **THEN** 系統以非預期錯誤失敗（500），不回傳訂單列表

### Requirement: 買家可查詢自己單筆訂單的明細與票券狀態
系統 SHALL 提供已登入會員查詢自己單筆訂單明細的端點，回傳訂單狀態、所屬活動名稱（`EventTitle`）、持有到期時間（ISO 8601 UTC 格式，語意比照上方「買家可查詢自己的訂單列表」Requirement 對 `HeldUntilUtc` 的定義——原始值，不因終態而清空或改寫），以及訂單內每筆項目（`OrderItem`）的顯示資訊與對應的票券清單與各自狀態（`Issued`／`Redeemed`／`Voided`）；訂單尚未出票（例如仍為 Pending）時，對應項目的票券清單 SHALL 為空，不視為錯誤。每筆項目 SHALL 另外包含：
- 座位標示：`SeatZoneCode`、`SeatNumber`，取自該項目 `EventSeatId` 對應座位的分區代碼與座位號碼；項目為純計數選購（`EventSeatId` 為 null）時兩者皆 SHALL 為 null
- 票種名稱：`TicketTypeName`，取自該項目 `TicketTypeId` 對應票種的 `ZoneCode`；`TicketTypeId` 為 null 的舊訂單項目 SHALL 為 null

上述顯示資訊 SHALL 以固定次數的查詢取得，查詢次數 MUST NOT 隨訂單項目數成長。訂單的 `EventId`、活動的 `SeatMapId` 對應的座位圖（訂單含座位項目時）、項目非 null 的 `EventSeatId` 或 `TicketTypeId`、或 `EventSeat.SeatId` 在該座位圖中對應的座位範本查不到時，視為資料不一致，系統 SHALL 以非預期錯誤失敗（由全域例外處理轉為 500），MUST NOT 回傳部分資料或以空值、Id 代替。非訂單買家本人查詢 MUST 被拒絕；訂單不存在 MUST 回報找不到資源；本人與存在檢查 SHALL 先於顯示資訊查詢執行，非本人或不存在時不觸發資料不一致判斷。

#### Scenario: 查詢自己的訂單明細（已出票）
- **WHEN** 訂單買家本人查詢一筆已確認付款、已出票的訂單明細
- **THEN** 系統回傳該訂單狀態、每筆項目的票券清單，票券狀態皆為當下實際狀態

#### Scenario: 查詢自己尚未確認付款的訂單明細
- **WHEN** 訂單買家本人查詢一筆狀態為 Pending、尚未出票的訂單明細
- **THEN** 系統回傳該訂單狀態，每筆項目對應的票券清單為空，不視為錯誤

#### Scenario: 非本人查詢他人訂單明細
- **WHEN** 非訂單買家的已登入會員查詢該訂單明細
- **THEN** 系統 MUST 拒絕此次查詢，回傳 403，不洩漏訂單內容

#### Scenario: 查詢不存在的訂單
- **WHEN** 已登入會員對不存在的訂單 Id 呼叫訂單明細端點
- **THEN** 系統回傳 404

#### Scenario: BOQ-DETAIL-DISPLAY-001 混合座位與計數項目的明細包含活動名稱、座位標示與票種名稱
- **WHEN** 訂單買家本人查詢一筆同時含座位項目（分區 A、座位 12、票種 A）與純計數項目（票種「站票」）的訂單明細
- **THEN** 回應的 `EventTitle` 為所屬活動名稱；座位項目的 `SeatZoneCode` 為 `A`、`SeatNumber` 為 `12`、`TicketTypeName` 為 `A`；計數項目的 `SeatZoneCode` 與 `SeatNumber` 皆為 null、`TicketTypeName` 為「站票」

#### Scenario: BOQ-DETAIL-DISPLAY-002 舊訂單項目沒有票種 Id
- **WHEN** 訂單買家本人查詢一筆項目 `TicketTypeId` 為 null 的舊訂單明細
- **THEN** 該項目的 `TicketTypeName` 為 null，其餘欄位正常回傳，不視為錯誤

#### Scenario: BOQ-DETAIL-DISPLAY-003 項目關聯的座位或票種查不到（資料不一致）
- **WHEN** 訂單買家本人查詢一筆訂單明細，且下列任一情形成立：訂單所屬活動不存在、含座位項目時活動的座位圖不存在、某項目非 null 的 `EventSeatId` 或 `TicketTypeId` 對應資料不存在、`EventSeat.SeatId` 在座位圖中找不到對應座位範本
- **THEN** 系統以非預期錯誤失敗（500），不回傳該訂單明細

#### Scenario: BOQ-DETAIL-DISPLAY-004 顯示資訊的查詢次數不隨項目數成長
- **WHEN** 訂單買家本人分別查詢含 1 個座位項目與含 5 個座位項目的訂單明細
- **THEN** 兩次查詢對資料庫發出的查詢次數相同
