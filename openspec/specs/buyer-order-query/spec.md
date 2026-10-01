# buyer-order-query Specification

## Purpose
TBD - created by archiving change buyer-order-query. Update Purpose after archive.
## Requirements
### Requirement: 買家需登入才能查詢訂單或票券
系統 SHALL 要求呼叫買家訂單列表、訂單明細、票券 QR Code 端點者持有效已登入會員 JWT；未登入或 Token 無效 MUST 被拒絕。此登入要求不限制呼叫者角色（比照既有 `OrdersController` 下單/確認/取消端點的既定慣例，僅要求已登入、不額外做角色檢查）；資料隔離一律透過比對資源的 `BuyerId` 與呼叫者身份達成（見下方各 Requirement 的非本人拒絕規則），不透過角色限制呼叫者。

#### Scenario: 未登入查詢訂單列表
- **WHEN** 未提供 Authorization Header 或 Token 無效，呼叫買家訂單列表端點
- **THEN** 系統回傳 401 未授權

#### Scenario: 未登入查詢票券 QR Code
- **WHEN** 未提供 Authorization Header 或 Token 無效，呼叫票券 QR Code 端點
- **THEN** 系統回傳 401 未授權

### Requirement: 買家可查詢自己的訂單列表
系統 SHALL 提供已登入會員查詢自己所有訂單的端點，只回傳呼叫者身份為買家（`BuyerId`）的訂單，MUST NOT 回傳其他會員的訂單。每筆訂單摘要 SHALL 包含訂單 Id、所屬活動 Id、所屬活動名稱（`EventTitle`）、訂單狀態、持有到期時間；時間欄位一律為 ISO 8601 UTC 格式（比照既有 `ticket-purchase`／`order-administration` 能力的既定慣例）。持有到期時間（`HeldUntilUtc`）SHALL 一律回傳建立訂單當下設定的原始值，不因訂單狀態轉為 Paid、Cancelled 或查詢時推導為 Expired 而清空或改寫——此欄位是歷史記錄用途的原始時間戳，「訂單目前是否仍在保留中」一律以訂單狀態欄位判斷，呼叫端 MUST NOT 用此欄位是否已過期來判斷訂單目前狀態。活動名稱 SHALL 以單次批次查詢取得列表中所有不重複活動，查詢次數 MUST NOT 隨訂單筆數成長。訂單的 `EventId` 對應的活動查不到時，視為資料不一致，系統 SHALL 以非預期錯誤失敗（由全域例外處理轉為 500），MUST NOT 回傳部分資料或以空值代替活動名稱。

#### Scenario: BOQ-LIST-001 查詢自己的訂單列表
- **WHEN** 已登入會員呼叫訂單列表端點
- **THEN** 系統回傳僅屬於該會員的訂單摘要清單，不含任何其他會員的訂單

#### Scenario: BOQ-LIST-002 尚未有任何訂單
- **WHEN** 已登入會員從未建立過訂單，呼叫訂單列表端點
- **THEN** 系統回傳空清單，不視為錯誤

#### Scenario: BOQ-LIST-TITLE-001 訂單摘要包含所屬活動名稱
- **WHEN** 已登入會員在兩個不同活動各有訂單，呼叫訂單列表端點
- **THEN** 每筆訂單摘要的 `EventTitle` 為其所屬活動的名稱

#### Scenario: BOQ-LIST-TITLE-003 活動名稱的查詢次數不隨訂單筆數成長
- **WHEN** 已登入會員分別在「1 筆訂單、1 個活動」與「3 筆訂單、3 個不同活動」的資料下呼叫訂單列表端點
- **THEN** 兩次請求對資料庫發出的查詢次數相同

#### Scenario: BOQ-LIST-TITLE-002 訂單所屬活動查不到（資料不一致）
- **WHEN** 已登入會員的某筆訂單其 `EventId` 對應的活動在資料庫中不存在
- **THEN** 系統以非預期錯誤失敗（500），不回傳訂單列表

### Requirement: 買家可查詢自己單筆訂單的明細與票券狀態
系統 SHALL 提供已登入會員查詢自己單筆訂單明細的端點，回傳訂單狀態、所屬活動名稱（`EventTitle`）、持有到期時間（ISO 8601 UTC 格式，語意比照上方「買家可查詢自己的訂單列表」Requirement 對 `HeldUntilUtc` 的定義——原始值，不因終態而清空或改寫），以及訂單內每筆項目（`OrderItem`）的顯示資訊與對應的票券清單與各自狀態（`Issued`／`Redeemed`／`Voided`）；訂單尚未出票（例如仍為 Pending）時，對應項目的票券清單 SHALL 為空，不視為錯誤。每筆項目 SHALL 另外包含：
- 座位標示：`SeatZoneCode`、`SeatNumber`，取自該項目 `EventSeatId` 對應座位的分區代碼與座位號碼；項目為純計數選購（`EventSeatId` 為 null）時兩者皆 SHALL 為 null；`EventSeatId` 非 null 時兩者皆 SHALL 有值，不存在只有其中一欄為 null 的回應
- 票種名稱：`TicketTypeName`，取自該項目 `TicketTypeId` 對應票種的 `ZoneCode`；`TicketTypeId` 為 null 的舊訂單項目 SHALL 為 null

上述顯示資訊 SHALL 以固定次數的查詢取得，查詢次數 MUST NOT 隨訂單項目數成長。明細請求的資料庫查詢 SHALL 只涉及下列資料表，且各表查詢次數不超過所列上限：`Orders`（含以 join 帶出的 `OrderItems`）1 次、`Tickets` 1 次（訂單無項目時 0 次）、`Events` 1 次、`TicketTypes` 1 次、`EventSeats` 與 `Seats` 在訂單含座位項目時各 1 次、只有計數項目時 0 次；與訂單資料無關、每個請求固定發生的查詢（例如身分驗證相關）不在此限，但其次數同樣 MUST NOT 隨項目數改變。座位標示 SHALL 只查詢訂單項目實際用到的座位範本，MUST NOT 為此載入活動座位圖的全部座位；訂單不含座位項目時不查詢座位資料。訂單的 `EventId`、項目非 null 的 `EventSeatId` 或 `TicketTypeId`、或 `EventSeat.SeatId` 對應的座位範本查不到時，視為資料不一致，系統 SHALL 以非預期錯誤失敗（由全域例外處理轉為 500），MUST NOT 回傳部分資料或以空值、Id 代替。非訂單買家本人查詢 MUST 被拒絕；訂單不存在 MUST 回報找不到資源；本人與存在檢查 SHALL 先於顯示資訊查詢執行，非本人或不存在時不觸發資料不一致判斷。

#### Scenario: BOQ-DETAIL-001 查詢自己的訂單明細（已出票）
- **WHEN** 訂單買家本人查詢一筆已確認付款、已出票的訂單明細
- **THEN** 系統回傳該訂單狀態、每筆項目的票券清單，票券狀態皆為當下實際狀態

#### Scenario: BOQ-DETAIL-002 查詢自己尚未確認付款的訂單明細
- **WHEN** 訂單買家本人查詢一筆狀態為 Pending、尚未出票的訂單明細
- **THEN** 系統回傳該訂單狀態，每筆項目對應的票券清單為空，不視為錯誤

#### Scenario: BOQ-DETAIL-003 非本人查詢他人訂單明細
- **WHEN** 非訂單買家的已登入會員查詢該訂單明細
- **THEN** 系統 MUST 拒絕此次查詢，回傳 403，不洩漏訂單內容

#### Scenario: BOQ-DETAIL-004 查詢不存在的訂單
- **WHEN** 已登入會員對不存在的訂單 Id 呼叫訂單明細端點
- **THEN** 系統回傳 404

#### Scenario: BOQ-DETAIL-DISPLAY-001 混合座位與計數項目的明細包含活動名稱、座位標示與票種名稱
- **WHEN** 訂單買家本人查詢一筆同時含座位項目（分區 A、座位 12、票種 A）與純計數項目（票種「站票」）的訂單明細
- **THEN** 回應的 `EventTitle` 為所屬活動名稱；座位項目的 `SeatZoneCode` 為 `A`、`SeatNumber` 為 `12`、`TicketTypeName` 為 `A`；計數項目的 `SeatZoneCode` 與 `SeatNumber` 皆為 null、`TicketTypeName` 為「站票」

#### Scenario: BOQ-DETAIL-DISPLAY-002 舊訂單項目沒有票種 Id
- **WHEN** 訂單買家本人查詢一筆項目 `TicketTypeId` 為 null 的舊訂單明細
- **THEN** 該項目的 `TicketTypeName` 為 null，其餘欄位正常回傳，不視為錯誤

#### Scenario: BOQ-DETAIL-DISPLAY-003 項目關聯的座位或票種查不到（資料不一致）
- **WHEN** 訂單買家本人查詢一筆訂單明細，且下列任一情形成立：訂單所屬活動不存在、某項目非 null 的 `EventSeatId` 或 `TicketTypeId` 對應資料不存在、`EventSeat.SeatId` 對應的座位範本不存在
- **THEN** 系統以非預期錯誤失敗（500），不回傳該訂單明細

#### Scenario: BOQ-DETAIL-DISPLAY-004 顯示資訊的查詢次數不隨項目數成長
- **WHEN** 訂單買家本人分別查詢項目數較少（純座位、純計數各 1 個項目；座位與計數混合至少須 2 個項目，即 1 座位＋1 計數）與含 5 個項目的訂單明細，項目組成分別為純座位、純計數、座位與計數混合三種
- **THEN** 每種組成下，兩次請求對每一張資料表發出的查詢次數皆相同，且各表次數不超過本 Requirement 列出的上限；純計數組成的兩次請求皆不查詢 `EventSeats` 與 `Seats`

### Requirement: 買家可取得自己已出票票券的 QR Code 圖檔
系統 SHALL 提供已登入會員依票券 Id 取得 QR Code PNG 圖檔的端點；成功回應 MUST 直接以 `image/png` Content-Type 回傳圖檔二進位內容，不得包裝成 JSON（例如 URL 或 Base64 字串）。圖檔內容為既有 `ticket-issuance` 能力產生的 HMAC 簽章 QR 內容所繪製。系統 MUST 驗證呼叫者為該票券所屬訂單的買家本人，非本人 MUST 被拒絕；票券不存在 MUST 回報找不到資源。此端點對 `Issued` 與 `Redeemed` 狀態的票券 MUST NOT 因狀態而限制存取，皆可查看 QR Code（供買家核對核銷結果）。`Voided` 為 `Ticket` 狀態列舉的第三值，但依既有 `ticket-issuance` 能力規格，本次系統範疇內 MUST NOT 提供任何使 Ticket 進入 `Voided` 的觸發路徑，故現況下不存在任何 `Voided` 票券可供查詢；本 Requirement 對 `Voided` 狀態的行為不另外定義，待未來新增 `Voided` 觸發路徑（例如退款流程）的提案時一併決定是否開放查看 QR Code。

#### Scenario: 買家取得自己已出票票券的 QR Code
- **WHEN** 票券所屬訂單的買家本人，對狀態為 `Issued` 的票券呼叫 QR Code 端點
- **THEN** 系統回傳該票券的 QR Code PNG 圖檔

#### Scenario: 買家取得自己已核銷票券的 QR Code
- **WHEN** 票券所屬訂單的買家本人，對狀態為 `Redeemed` 的票券呼叫 QR Code 端點
- **THEN** 系統仍回傳該票券的 QR Code PNG 圖檔，不因已核銷而拒絕

#### Scenario: 非本人取得他人票券的 QR Code
- **WHEN** 非票券所屬訂單買家的已登入會員，呼叫該票券的 QR Code 端點
- **THEN** 系統 MUST 拒絕此次查詢，回傳 403，不回傳圖檔

#### Scenario: 查詢不存在的票券
- **WHEN** 已登入會員對不存在的票券 Id 呼叫 QR Code 端點
- **THEN** 系統回傳 404

