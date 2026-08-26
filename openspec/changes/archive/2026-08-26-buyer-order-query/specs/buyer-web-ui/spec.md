## MODIFIED Requirements

### Requirement: 「我的訂單」列表與明細頁串接查詢 API，顯示訂單、票券狀態與 QR Code
系統 SHALL 提供「我的訂單」列表頁與訂單明細頁，呼叫新增的 `buyer-order-query` 能力查詢自己的訂單列表與明細。列表頁與明細頁 SHALL 顯示每筆訂單的狀態；持有到期時間（`HeldUntilUtc`）僅在訂單狀態為 Pending 時 SHALL 顯示為「保留至 {時間}」提示，訂單狀態為 Paid、Cancelled 或 Expired（終態或已逾時）時 MUST NOT 顯示此欄位——`HeldUntilUtc` 是建立訂單當下設定的原始值，不因訂單轉為終態而清空或重新賦值（比照既有 `buyer-order-query` 能力對此欄位的定義），對終態訂單顯示原值會誤導買家以為訂單仍在保留中。明細頁 SHALL 顯示訂單狀態、每筆項目對應的票券清單與各自狀態（`Issued`／`Redeemed`）。狀態為 `Issued` 或 `Redeemed` 的票券，明細頁 SHALL 提供「查看 QR Code」操作，點選後顯示該票券的 QR Code 圖檔；圖檔 MUST 透過既有 API 攔截器（帶 Authorization Header）取得後以 Blob／Object URL 顯示，MUST NOT 直接將 `<img src>` 指向需要驗證的端點網址。訂單內項目尚未出票（例如訂單仍為 Pending）時，該項目 SHALL 顯示「尚未出票」，不顯示 QR Code 操作。`Ticket.Voided` 狀態依既有 `ticket-issuance` 能力規格現況無任何觸發路徑，本次前端 MUST NOT 為 `Voided` 實作任何顯示邏輯（不在本次 UI 行為範圍內）；待未來新增 `Voided` 觸發路徑的提案時，須一併規劃對應的前端顯示行為。

#### Scenario: 開啟我的訂單列表頁
- **WHEN** 已登入買家開啟「我的訂單」頁面
- **THEN** 系統呼叫訂單列表 API，顯示自己所有訂單的狀態；狀態為 Pending 的訂單額外顯示「保留至 {時間}」，其他狀態不顯示持有到期時間；尚無任何訂單時顯示空清單提示，不視為錯誤

#### Scenario: 開啟訂單明細頁查看已出票訂單
- **WHEN** 已登入買家從列表點入一筆已確認付款、已出票的訂單明細
- **THEN** 系統呼叫訂單明細 API，顯示訂單狀態（不顯示持有到期時間，因訂單已為 Paid 終態）與每筆項目對應的票券清單，狀態為 `Issued` 的票券顯示「查看 QR Code」操作

#### Scenario: 點選查看 QR Code
- **WHEN** 買家在訂單明細頁對一張 `Issued` 或 `Redeemed` 狀態的票券點選「查看 QR Code」
- **THEN** 系統透過帶 Authorization Header 的請求取得 QR Code 圖檔，以 Blob／Object URL 顯示在畫面上

#### Scenario: 開啟尚未出票訂單的明細頁
- **WHEN** 已登入買家開啟一筆狀態為 Pending、尚未出票的訂單明細頁
- **THEN** 系統顯示訂單狀態與「保留至 {時間}」，訂單內每筆項目顯示「尚未出票」，不提供「查看 QR Code」操作

#### Scenario: 直接以網址開啟不存在的訂單明細頁
- **WHEN** 使用者直接開啟一個不存在的訂單 Id 的明細頁網址
- **THEN** 訂單明細 API 回應 404，系統顯示「找不到這筆訂單」提示與返回「我的訂單」列表的操作，不顯示任何訂單資料

#### Scenario: 直接以網址開啟非本人的訂單明細頁
- **WHEN** 已登入買家直接開啟非自己所屬訂單的明細頁網址
- **THEN** 訂單明細 API 回應 403，系統顯示「你沒有權限查看這筆訂單」提示與返回「我的訂單」列表的操作，不顯示任何訂單資料
