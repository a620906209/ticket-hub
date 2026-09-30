## MODIFIED Requirements

### Requirement: 主辦方成員可查看目前 Organizer 名下的訂單列表與明細
系統 SHALL 提供訂單列表頁與訂單詳情頁，呼叫 `order-administration` API 顯示呼叫端目前切換所在 Organizer 名下活動的訂單狀態，與單筆訂單內的座位項目明細；不顯示其他 Organizer 名下活動的訂單（過濾由後端 `order-administration` 能力執行，前端不另行過濾）。此狀態為頁面載入或手動重新整理當下查詢 API 取得的結果，非伺服器推播的即時更新。訂單列表與詳情頁的訂單狀態 SHALL 以中文標籤顯示，標籤呈現方式、顏色、對照規則與未知值處理比照 `buyer-web-ui` 能力「「我的訂單」列表與明細頁串接查詢 API，顯示訂單、票券狀態與 QR Code」Requirement；持有到期時間 SHALL 僅在訂單狀態為 Pending 時顯示，其他狀態 MUST NOT 顯示，理由同該 Requirement（`HeldUntilUtc` 為不因終態改寫的原始值）。

#### Scenario: AWU-ORDER-LIST-001 查看目前 Organizer 名下的訂單列表
- **WHEN** 已切換至一個 Approved Organizer 的使用者開啟後台訂單列表頁
- **THEN** 系統顯示該 Organizer 名下活動目前的訂單與其中文狀態標籤

#### Scenario: AWU-ORDER-DETAIL-001 查看訂單明細
- **WHEN** 已切換至一個 Approved Organizer 的使用者點選某筆訂單進入詳情頁
- **THEN** 系統顯示該訂單內的每一筆座位項目明細

#### Scenario: AWU-ORDER-HOLD-001 持有到期時間僅於 Pending 訂單顯示
- **WHEN** 已切換至一個 Approved Organizer 的使用者開啟訂單列表，列表中同時有 Pending 與 Paid 訂單，並分別進入兩筆的詳情頁
- **THEN** 列表與詳情頁皆只對 Pending 訂單顯示持有到期時間，Paid 訂單不顯示
