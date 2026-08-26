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
系統 SHALL 提供已登入會員查詢自己所有訂單的端點，只回傳呼叫者身份為買家（`BuyerId`）的訂單，MUST NOT 回傳其他會員的訂單。每筆訂單摘要 SHALL 包含訂單 Id、所屬活動 Id、訂單狀態、持有到期時間；時間欄位一律為 ISO 8601 UTC 格式（比照既有 `ticket-purchase`／`order-administration` 能力的既定慣例）。持有到期時間（`HeldUntilUtc`）SHALL 一律回傳建立訂單當下設定的原始值，不因訂單狀態轉為 Paid、Cancelled 或查詢時推導為 Expired 而清空或改寫——此欄位是歷史記錄用途的原始時間戳，「訂單目前是否仍在保留中」一律以訂單狀態欄位判斷，呼叫端 MUST NOT 用此欄位是否已過期來判斷訂單目前狀態。

#### Scenario: 查詢自己的訂單列表
- **WHEN** 已登入會員呼叫訂單列表端點
- **THEN** 系統回傳僅屬於該會員的訂單摘要清單，不含任何其他會員的訂單

#### Scenario: 尚未有任何訂單
- **WHEN** 已登入會員從未建立過訂單，呼叫訂單列表端點
- **THEN** 系統回傳空清單，不視為錯誤

### Requirement: 買家可查詢自己單筆訂單的明細與票券狀態
系統 SHALL 提供已登入會員查詢自己單筆訂單明細的端點，回傳訂單狀態、持有到期時間（ISO 8601 UTC 格式，語意比照上方「買家可查詢自己的訂單列表」Requirement 對 `HeldUntilUtc` 的定義——原始值，不因終態而清空或改寫），以及訂單內每筆項目（`OrderItem`）對應的票券清單與各自狀態（`Issued`／`Redeemed`／`Voided`）；訂單尚未出票（例如仍為 Pending）時，對應項目的票券清單 SHALL 為空，不視為錯誤。非訂單買家本人查詢 MUST 被拒絕；訂單不存在 MUST 回報找不到資源。

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
