## MODIFIED Requirements

### Requirement: 「我的訂單」列表與明細頁串接查詢 API，顯示訂單、票券狀態與 QR Code
系統 SHALL 提供「我的訂單」列表頁與訂單明細頁，呼叫 `buyer-order-query` 能力查詢自己的訂單列表與明細。列表頁 SHALL 顯示每筆訂單的活動名稱與狀態；明細頁 SHALL 顯示活動名稱與訂單狀態。訂單狀態與票券狀態 SHALL 以中文標籤顯示（訂單：Pending「待付款」、Paid「已付款」、Cancelled「已取消」、Expired「已逾時」；票券：Issued「已出票」、Redeemed「已核銷」），MUST NOT 直接顯示英文列舉值；狀態 SHALL 以標籤元件呈現並依狀態區分顏色（訂單 Pending 為警示色、Paid 為成功色、Cancelled／Expired 為中性色；票券 Issued 為成功色、Redeemed 為中性色；未知值為中性色），標籤內 MUST 顯示文字，顏色不得為唯一辨識方式；收到上述以外的未知狀態值時 SHALL 原樣顯示該值，不得顯示空白或造成頁面錯誤。持有到期時間（`HeldUntilUtc`）僅在訂單狀態為 Pending 時 SHALL 顯示為「保留至 {時間}」提示，訂單狀態為 Paid、Cancelled 或 Expired（終態或已逾時）時 MUST NOT 顯示此欄位——`HeldUntilUtc` 是建立訂單當下設定的原始值，不因訂單轉為終態而清空或重新賦值（比照既有 `buyer-order-query` 能力對此欄位的定義），對終態訂單顯示原值會誤導買家以為訂單仍在保留中。明細頁每筆項目 SHALL 顯示票種名稱與座位標示（「{分區}-{座位號碼}」），純計數項目的座位標示顯示「—」，票種名稱為 null 的舊訂單項目顯示「—」。明細頁 SHALL 顯示每筆項目對應的票券清單與各自狀態（`Issued`／`Redeemed`），每張票券 SHALL 以文字顯示完整 Ticket ID，供現場相機不可用時由工作人員於核銷頁手動輸入（見 `admin-web-ui`「掃描期間與相機不可用時皆可切換到手動輸入 Ticket ID 完成核銷」）。狀態為 `Issued` 或 `Redeemed` 的票券，明細頁 SHALL 提供「查看 QR Code」操作，點選後顯示該票券的 QR Code 圖檔；圖檔 MUST 透過既有 API 攔截器（帶 Authorization Header）取得後以 Blob／Object URL 顯示，MUST NOT 直接將 `<img src>` 指向需要驗證的端點網址。訂單內項目尚未出票（例如訂單仍為 Pending）時，該項目 SHALL 顯示「尚未出票」，不顯示 QR Code 操作。明細頁網址中的訂單 Id 非 GUID 格式時，前端不另做格式檢查，由後端路由約束 `{id:guid}` 回應 404，依「找不到這筆訂單」處理。列表頁 SHALL 以分頁元件顯示訂單，每頁筆數固定為預設值（不提供每頁筆數選擇器），分頁元件顯示 `totalCount` 與頁碼；換頁時以新的 `page` 重新呼叫列表 API 並以回應的 `items` 取代列表內容，前端 MUST NOT 重新排序；頁碼只保存在頁面元件內，MUST NOT 寫入網址；訂單總筆數不超過一頁時 MUST NOT 顯示分頁元件。`Ticket.Voided` 狀態依既有 `ticket-issuance` 能力規格現況無任何觸發路徑，本次前端 MUST NOT 為 `Voided` 實作任何顯示邏輯（不在本次 UI 行為範圍內）；目前若 API 回傳 `Voided`，僅依本 Requirement 的通用規則以未知狀態原樣顯示（中性色、無「查看 QR Code」），不構成 `Voided` 專屬的顯示邏輯；待未來新增 `Voided` 觸發路徑的提案時，須一併規劃對應的前端顯示行為。

#### Scenario: BW-MYORDER-LIST-001 開啟我的訂單列表頁
- **WHEN** 已登入買家開啟「我的訂單」頁面
- **THEN** 系統呼叫訂單列表 API 取得第 1 頁，顯示該頁訂單的活動名稱與中文狀態標籤；狀態為 Pending 的訂單額外顯示「保留至 {時間}」，其他狀態不顯示持有到期時間；尚無任何訂單時顯示空清單提示且不顯示分頁元件，不視為錯誤

#### Scenario: BW-MYORDER-DETAIL-001 開啟訂單明細頁查看已出票訂單
- **WHEN** 已登入買家從列表點入一筆已確認付款、已出票的訂單明細
- **THEN** 系統呼叫訂單明細 API，顯示活動名稱、訂單狀態「已付款」（不顯示持有到期時間，因訂單已為 Paid 終態）、每筆項目的票種名稱與座位標示，以及對應的票券清單，狀態為 `Issued` 的票券顯示「已出票」與「查看 QR Code」操作

#### Scenario: BW-MYORDER-TICKET-ID-001 明細頁顯示 Ticket ID 供手動核銷
- **WHEN** 已登入買家開啟一筆已出票的訂單明細頁
- **THEN** 每張票券以文字顯示完整的 Ticket ID，工作人員可在相機不可用時於核銷頁手動輸入此值完成核銷

#### Scenario: BW-MYORDER-QR-001 點選查看 QR Code
- **WHEN** 買家在訂單明細頁對一張 `Issued` 或 `Redeemed` 狀態的票券點選「查看 QR Code」
- **THEN** 系統透過帶 Authorization Header 的請求取得 QR Code 圖檔，以 Blob／Object URL 顯示在畫面上

#### Scenario: BW-MYORDER-DETAIL-002 開啟尚未出票訂單的明細頁
- **WHEN** 已登入買家開啟一筆狀態為 Pending、尚未出票的訂單明細頁
- **THEN** 系統顯示訂單狀態「待付款」與「保留至 {時間}」，訂單內每筆項目顯示「尚未出票」，不提供「查看 QR Code」操作

#### Scenario: BW-MYORDER-NOTFOUND-001 直接以網址開啟不存在的訂單明細頁
- **WHEN** 使用者直接開啟一個不存在的訂單 Id 的明細頁網址
- **THEN** 訂單明細 API 回應 404，系統顯示「找不到這筆訂單」提示與返回「我的訂單」列表的操作，不顯示任何訂單資料

#### Scenario: BW-MYORDER-FORBIDDEN-001 直接以網址開啟非本人的訂單明細頁
- **WHEN** 已登入買家直接開啟非自己所屬訂單的明細頁網址
- **THEN** 訂單明細 API 回應 403，系統顯示「你沒有權限查看這筆訂單」提示與返回「我的訂單」列表的操作，不顯示任何訂單資料

#### Scenario: BW-MYORDER-INVALID-ID-001 以非 GUID 的訂單 Id 開啟明細頁
- **WHEN** 使用者直接開啟訂單 Id 非 GUID 格式的明細頁網址（例如 `/orders/not-a-guid`）
- **THEN** 訂單明細 API 回應 404（後端路由約束），系統顯示「找不到這筆訂單」提示與返回「我的訂單」列表的操作，不顯示任何訂單資料

#### Scenario: BW-MYORDER-DISPLAY-001 明細頁項目顯示座位標示與票種名稱
- **WHEN** 已登入買家開啟一筆含座位項目（分區 A、座位 12、票種 A）與純計數項目（票種「站票」）的訂單明細頁
- **THEN** 座位項目顯示票種「A」與座位「A-12」，計數項目顯示票種「站票」與座位「—」

#### Scenario: BW-MYORDER-DISPLAY-002 收到未知的訂單狀態值
- **WHEN** 訂單列表 API 回傳的某筆訂單狀態為前端對照表未定義的值
- **THEN** 該筆訂單的狀態欄位原樣顯示該值，頁面其餘內容正常顯示

#### Scenario: BW-MYORDER-PAGE-001 訂單超過一頁時顯示分頁元件並可換頁
- **WHEN** 已登入買家有超過一頁的訂單，開啟「我的訂單」頁面後點選第 2 頁
- **THEN** 分頁元件顯示訂單總筆數與頁碼，系統以 `page` 為 2 重新呼叫訂單列表 API，並以第 2 頁的訂單取代列表內容；載入期間列表顯示載入中狀態

#### Scenario: BW-MYORDER-PAGE-002 訂單未超過一頁時不顯示分頁元件
- **WHEN** 已登入買家的訂單筆數不超過一頁
- **THEN** 頁面只顯示訂單列表，不顯示分頁元件

#### Scenario: BW-MYORDER-PAGE-003 重新整理停留在目前頁
- **WHEN** 已登入買家停在第 2 頁並點選「重新整理」
- **THEN** 系統重新請求第 2 頁；若回傳空 `items` 但 `totalCount` 大於 0，系統只重試一次改請求最後一頁，並同步分頁元件顯示該頁

#### Scenario: BW-MYORDER-PAGE-004 換頁請求失敗時退回前一頁
- **WHEN** 已登入買家停在第 1 頁並點選第 2 頁，但第 2 頁的請求回應錯誤
- **THEN** 分頁元件回到第 1 頁，列表保留第 1 頁的訂單並顯示錯誤提示，不顯示與頁碼不符的內容

#### Scenario: BW-MYORDER-PAGE-005 快速連續換頁時丟棄過期回應
- **WHEN** 已登入買家快速依序點選第 2 頁與第 3 頁，第 3 頁的回應先到、第 2 頁的回應後到
- **THEN** 列表顯示第 3 頁的訂單，第 2 頁的回應被丟棄
