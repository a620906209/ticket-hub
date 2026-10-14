## MODIFIED Requirements

### Requirement: 主辦方成員可查看目前 Organizer 名下的訂單列表與明細
系統 SHALL 提供訂單列表頁與訂單詳情頁，呼叫 `order-administration` API 顯示呼叫端目前切換所在 Organizer 名下活動的訂單狀態，與單筆訂單內的座位項目明細；不顯示其他 Organizer 名下活動的訂單（過濾由後端 `order-administration` 能力執行，前端不另行過濾）。此狀態為頁面載入或手動重新整理當下查詢 API 取得的結果，非伺服器推播的即時更新。訂單列表 SHALL 以買家顯示名稱（`BuyerDisplayName`）辨識買家，MUST NOT 顯示買家 GUID。訂單列表 SHALL 依 API 回傳順序（建立時間由新到舊）顯示下列欄位：訂單編號（訂單 Id 的前 8 個字元，僅供辨識）、活動名稱、買家、張數、總金額（標示「NT$」貨幣單位）、狀態、建立時間、持有到期時間、操作（進入詳情頁，連結使用完整訂單 Id）。訂單列表與詳情頁的訂單狀態 SHALL 以中文標籤顯示，標籤呈現方式、顏色、對照規則與未知值處理比照 `buyer-web-ui` 能力「「我的訂單」列表與明細頁串接查詢 API，顯示訂單、票券狀態與 QR Code」Requirement；持有到期時間 SHALL 僅在訂單狀態為 Pending 時顯示，其他狀態 MUST NOT 顯示，理由同該 Requirement（`HeldUntilUtc` 為不因終態改寫的原始值）；在訂單列表中，非 Pending 訂單的持有到期時間欄 SHALL 顯示「—」，MUST NOT 留白。

#### Scenario: AWU-ORDER-LIST-001 查看目前 Organizer 名下的訂單列表
- **WHEN** 已切換至一個 Approved Organizer 的使用者開啟後台訂單列表頁
- **THEN** 系統顯示該 Organizer 名下活動目前的訂單與其中文狀態標籤，每筆以買家顯示名稱辨識買家，不顯示買家 GUID

#### Scenario: AWU-ORDER-DETAIL-001 查看訂單明細
- **WHEN** 已切換至一個 Approved Organizer 的使用者點選某筆訂單進入詳情頁
- **THEN** 系統顯示該訂單內的每一筆座位項目明細

#### Scenario: AWU-ORDER-HOLD-001 持有到期時間僅於 Pending 訂單顯示
- **WHEN** 已切換至一個 Approved Organizer 的使用者開啟訂單列表，列表中同時有 Pending 與 Paid 訂單，並分別進入兩筆的詳情頁
- **THEN** 列表與詳情頁皆只對 Pending 訂單顯示持有到期時間，Paid 訂單不顯示時間；列表中 Paid 訂單的持有到期時間欄顯示「—」

#### Scenario: AWU-ORDER-LIST-002 訂單列表顯示訂單編號、活動、張數、總金額與建立時間
- **WHEN** 已切換至一個 Approved Organizer 的使用者開啟訂單列表，API 回傳一筆 Id 為 `3f2a9c1e-…`、活動名稱「春季音樂會」、張數 2、總金額 2500 的訂單
- **THEN** 該列顯示訂單編號「3f2a9c1e」、活動「春季音樂會」、張數「2」、總金額「NT$2,500」與該訂單的建立時間，「查看明細」連結指向完整訂單 Id 的詳情頁

#### Scenario: AWU-ORDER-LIST-003 訂單列表依 API 回傳順序顯示
- **WHEN** API 依建立時間由新到舊回傳訂單 A、B、C
- **THEN** 列表由上到下依序顯示 A、B、C，前端不重新排序
