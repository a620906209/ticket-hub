## ADDED Requirements

### Requirement: 後台訂單列表支援分頁
系統 SHALL 在後台訂單列表頁以分頁元件顯示訂單，每頁筆數固定為預設值（不提供每頁筆數選擇器），分頁元件 SHALL 顯示 `totalCount` 與頁碼；換頁時系統 SHALL 以新的 `page` 重新呼叫 `order-administration` 訂單列表 API，並以回應的 `items` 取代列表內容，前端不重新排序。頁碼只保存在頁面元件內，MUST NOT 寫入網址。訂單總筆數不超過一頁時 MUST NOT 顯示分頁元件。

#### Scenario: AWU-ORDER-PAGE-001 訂單超過一頁時可換頁
- **WHEN** 已切換至一個 Approved Organizer 的使用者開啟訂單列表頁，Organizer 名下訂單超過一頁，並點選第 2 頁
- **THEN** 分頁元件顯示訂單總筆數，系統以 `page` 為 2 重新呼叫 API，列表改為顯示第 2 頁的訂單，載入期間列表顯示載入中狀態

#### Scenario: AWU-ORDER-PAGE-002 訂單未超過一頁時不顯示分頁元件
- **WHEN** 已切換至一個 Approved Organizer 的使用者開啟訂單列表頁，訂單筆數不超過一頁
- **THEN** 頁面只顯示訂單列表，不顯示分頁元件

#### Scenario: AWU-ORDER-PAGE-003 重新整理停留在目前頁
- **WHEN** 使用者停在第 2 頁並點選「重新整理」
- **THEN** 系統重新請求第 2 頁；若回傳空 `items` 但 `totalCount` 大於 0，系統只重試一次改請求最後一頁，並同步分頁元件顯示該頁

#### Scenario: AWU-ORDER-PAGE-004 換頁請求失敗時退回前一頁
- **WHEN** 使用者停在第 1 頁並點選第 2 頁，但第 2 頁的請求回應錯誤
- **THEN** 分頁元件回到第 1 頁，列表保留第 1 頁的訂單並顯示錯誤提示，不顯示與頁碼不符的內容

#### Scenario: AWU-ORDER-PAGE-005 快速連續換頁時丟棄過期回應
- **WHEN** 使用者快速依序點選第 2 頁與第 3 頁，第 3 頁的回應先到、第 2 頁的回應後到
- **THEN** 列表顯示第 3 頁的訂單，第 2 頁的回應被丟棄
