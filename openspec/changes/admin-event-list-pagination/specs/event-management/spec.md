## MODIFIED Requirements

### Requirement: 透過管理 API 查詢活動列表時取得建立者與售票狀況統計
系統 SHALL 提供一個獨立於既有公開活動列表查詢端點（`event-catalog` 能力既有的公開端點，供買家瀏覽用）的後台專用活動列表查詢端點，沿用既有「後台管理 API 需要已切換至一個 Approved Organizer」的權限規則。這個端點 SHALL 只回傳呼叫端目前切換所在 Organizer 名下的活動，不回傳其他 Organizer 的活動，且 SHALL 採用 `list-pagination` 能力定義的分頁請求參數、驗證與回應結構（`{ items, page, pageSize, totalCount }`）；`totalCount` 只計入呼叫端目前 Organizer 名下的活動。活動 SHALL 依建立時間由新到舊排序、建立時間為 null 者排在最後、建立時間相同或同為 null 時依活動 Id 升冪。每筆活動 SHALL 附帶：建立者（Member 的 MemberId 與可辨識的顯示名稱，查無對應會員或活動未記錄建立者時顯示名稱為 null）、建立時間（未記錄時為 null）、座位依 Available／Held／Sold 分類的數量統計。統計與建立者資料 SHALL 只針對被回傳的該頁活動計算，其查詢次數 MUST NOT 隨活動總數或頁內活動數成長。統計 SHALL 反映查詢當下的即時狀態（依既有座位狀態計算邏輯，Held 若已過期 MUST 視為 Available，不得沿用過期前的分類）。查詢活動列表不需要另外呼叫其他端點才能取得這份統計。既有公開的活動列表查詢端點 MUST NOT 回傳建立者、建立時間、售票狀況統計或 `OrganizerId` 這幾項——這些是後台專用資訊，不對未登入的公開查詢揭露，且其回應結構與是否分頁不受本 Requirement 影響。

#### Scenario: EVT-LIST-001 查詢活動列表僅回傳目前所屬 Organizer 名下的活動
- **WHEN** 已切換至 Organizer A 的使用者呼叫後台專用的活動列表查詢端點，資料庫中同時存在 Organizer A 與 Organizer B 名下的活動
- **THEN** 系統只回傳 Organizer A 名下的活動，不包含 Organizer B 的活動，`totalCount` 只計 Organizer A 名下的活動筆數

#### Scenario: EVT-LIST-002 查詢活動列表取得建立者與售票狀況統計
- **WHEN** 已切換至一個 Approved Organizer 的使用者呼叫後台專用的活動列表查詢端點
- **THEN** 系統回傳的 `items` 中每筆活動 SHALL 附帶建立者、建立時間，以及 Available／Held／Sold 各自的座位數量

#### Scenario: EVT-LIST-003 活動座位有已過期的持有中狀態
- **WHEN** 活動的某些座位曾被持有（Held）但持有期限已過、尚未被查詢清理程序處理
- **THEN** 統計 MUST 把這些座位算入 Available，不得算入 Held

#### Scenario: EVT-LIST-004 活動沒有任何座位
- **WHEN** 已切換至一個 Approved Organizer 的使用者查詢一筆理論上不應存在但座位數為零的活動（例如資料異常）
- **THEN** 系統 MUST 回傳三個統計數字皆為 0，不得因此拒絕整筆查詢或造成例外

#### Scenario: EVT-LIST-005 既有公開活動列表查詢端點不回傳後台專用欄位
- **WHEN** 任何呼叫端（不論是否登入）呼叫既有的公開活動列表查詢端點
- **THEN** 回傳內容 MUST NOT 包含建立者、建立時間、售票狀況統計或 `OrganizerId`

#### Scenario: EVT-LIST-006 活動列表分頁回應結構
- **WHEN** Organizer A 名下共有 45 筆活動，使用者以 `page=2`、`pageSize=20` 呼叫後台專用的活動列表查詢端點
- **THEN** 回應的 `items` 為排序後第 21–40 筆、`page` 為 2、`pageSize` 為 20、`totalCount` 為 45；未帶分頁參數時 `page` 為 1、`pageSize` 為 20

#### Scenario: EVT-LIST-007 分頁參數越界或非整數
- **WHEN** 使用者帶 `pageSize=101`、`page=0`、`page=10001` 或 `page=abc` 呼叫後台專用的活動列表查詢端點
- **THEN** 系統回應 400 `Validation`，`errors` 含對應的 `page` 或 `pageSize` 欄位，不回傳任何活動

#### Scenario: EVT-LIST-008 排序確定且 null 建立時間排最後
- **WHEN** Organizer A 名下有建立時間不同的活動、建立時間相同的活動，以及建立時間為 null 的舊活動，使用者以相同 `pageSize` 由第 1 頁讀到最後一頁
- **THEN** 活動依建立時間新到舊排列，建立時間相同者依活動 Id 升冪，建立時間為 null 者排在所有有建立時間的活動之後；所有頁的 `items` 合併後每筆活動恰好出現一次

#### Scenario: EVT-LIST-009 統計與建立者只針對當頁計算
- **WHEN** Organizer A 名下有超過一頁的活動，使用者請求其中一頁
- **THEN** 該頁每筆活動的座位統計與建立者顯示名稱正確，且相關資料查詢次數與 Organizer A 名下活動總數、頁內活動數無關
