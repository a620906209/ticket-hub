## MODIFIED Requirements

### Requirement: 查詢所有訂單列表
系統 SHALL 提供已切換至一個 Approved Organizer 的使用者分頁查詢訂單的端點（分頁參數、驗證與回應結構見 `list-pagination` 能力；`totalCount` 僅計入呼叫端目前 Organizer 名下活動的訂單），回傳每筆訂單的基本資訊、買家顯示名稱（`BuyerDisplayName`，取自買家 `Member.DisplayName`）與即時狀態（依 `GetStatus(now)` 推導，可能為 Expired）。每筆訂單 SHALL 另外包含：活動名稱（`EventTitle`，取自訂單所屬 `Event.Title`）、張數（`TicketCount`，訂單內所有項目 `Quantity` 的總和）、總金額（`TotalAmount`，訂單內所有項目 `Quantity × UnitPrice` 的總和）與建立時間（`CreatedAtUtc`，見 `ticket-ordering` 能力「建立訂單須記錄建立時間」Requirement）。列表 SHALL 依建立時間由新到舊排序，建立時間相同時依訂單 Id 升冪排序，使同一份資料每次查詢的順序相同。回傳範圍 SHALL 僅限於呼叫端目前 Organizer 名下活動（依 `Order.EventId` 對應的 `Event.OrganizerId` 判斷）的訂單，不含其他 Organizer 名下活動的訂單。此過濾、排序、切頁與總筆數 MUST 在資料庫端執行，MUST NOT 先載入其他 Organizer 的訂單或全部訂單再於應用程式記憶體中過濾或切頁。買家顯示名稱與活動名稱 SHALL 各以單次批次查詢取得當頁列表中所有不重複的買家與活動，查詢次數 MUST NOT 隨當頁訂單、買家或活動的數量成長；除顯示名稱外 MUST NOT 回傳買家 Email 或其他個人資料。當頁訂單的 `BuyerId` 對應的會員、或 `EventId` 對應的活動查不到時，視為資料不一致，系統 SHALL 以非預期錯誤失敗（由全域例外處理轉為 500），MUST NOT 回傳部分資料或以空值代替；不在被請求頁面的訂單不影響該次請求。

#### Scenario: ORD-LIST-001 查詢訂單列表僅回傳目前所屬 Organizer 名下活動的訂單
- **WHEN** 已切換至 Organizer A 的使用者呼叫訂單列表端點，資料庫中同時存在 Organizer A 與 Organizer B 名下活動各自的訂單
- **THEN** 系統只回傳 Organizer A 名下活動的訂單（含每筆的基本資訊與即時狀態），`totalCount` 僅計入 Organizer A 的訂單，不包含 Organizer B 名下活動的訂單

#### Scenario: ORD-LIST-002 訂單列表包含買家顯示名稱且不含 Email
- **WHEN** 已切換至 Organizer A 的使用者呼叫訂單列表端點，列表中有兩位不同買家的訂單
- **THEN** 每筆訂單的 `BuyerDisplayName` 為其買家的顯示名稱，回應中不包含任何買家 Email 欄位

#### Scenario: ORD-LIST-004 買家與活動名稱的查詢次數不隨訂單筆數成長
- **WHEN** 已切換至 Organizer A 的使用者分別在「1 筆訂單、1 位買家、1 個活動」與「3 筆訂單、3 位不同買家、分屬 2 個活動」的資料下呼叫訂單列表端點
- **THEN** 兩次請求對每張資料表發出的查詢次數相同（`totalCount` 與當頁訂單的查詢次數固定），且買家名稱與活動名稱各只以 1 次查詢取得

#### Scenario: ORD-LIST-003 訂單買家查不到（資料不一致）
- **WHEN** 已切換至 Organizer A 的使用者呼叫訂單列表端點，其中一筆訂單的 `BuyerId` 對應的會員在資料庫中不存在
- **THEN** 該筆訂單落在被請求的頁面時，系統以非預期錯誤失敗（500），不回傳該頁

#### Scenario: ORD-LIST-005 訂單列表包含活動名稱、張數、總金額與建立時間
- **WHEN** 已切換至 Organizer A 的使用者呼叫訂單列表端點，列表中有一筆屬於活動「春季音樂會」、含 2 個項目（1 張 NT$1,000 與 1 張 NT$1,500）、於時間 T 建立的訂單
- **THEN** 該筆訂單的 `EventTitle` 為「春季音樂會」、`TicketCount` 為 2、`TotalAmount` 為 2500、`CreatedAtUtc` 為 T

#### Scenario: ORD-LIST-006 訂單列表依建立時間由新到舊排序
- **WHEN** 已切換至 Organizer A 的使用者呼叫訂單列表端點，Organizer A 名下有三筆訂單，建立時間依序為 T1 < T2 < T3，且在資料庫中不是依建立時間順序存放
- **THEN** 回傳順序為 T3、T2、T1 的訂單

#### Scenario: ORD-LIST-008 建立時間相同的訂單依 Id 排序且順序固定
- **WHEN** 已切換至 Organizer A 的使用者連續兩次呼叫訂單列表端點，Organizer A 名下有兩筆建立時間相同的訂單，兩次查詢時資料庫回傳這兩筆的順序不同
- **THEN** 兩次回應中這兩筆訂單的相對順序相同，且依訂單 Id 升冪排列

#### Scenario: ORD-LIST-007 訂單活動查不到（資料不一致）
- **WHEN** 已切換至 Organizer A 的使用者呼叫訂單列表端點，其中一筆訂單的 `EventId` 對應的活動在查詢活動名稱時查不到
- **THEN** 該筆訂單落在被請求的頁面時，系統以非預期錯誤失敗（500），不回傳該頁

#### Scenario: ORD-PAGE-001 依 page 與 pageSize 切頁並只計入目前 Organizer
- **WHEN** 已切換至 Organizer A 的使用者呼叫訂單列表端點，Organizer A 名下有 5 筆訂單、Organizer B 名下有 3 筆訂單，`page` 為 2、`pageSize` 為 2
- **THEN** `items` 為 Organizer A 排序後的第 3、4 筆，`totalCount` 為 5，不含 Organizer B 的任何訂單

#### Scenario: ORD-PAGE-002 逐頁讀取時排序固定
- **WHEN** 已切換至 Organizer A 的使用者以 `pageSize` 為 2 逐頁讀取，Organizer A 名下有 3 筆建立時間相同的訂單
- **THEN** 各頁合併後這 3 筆訂單各出現一次，且順序依訂單 Id 升冪

#### Scenario: ORD-PAGE-004 資料不一致只影響包含該筆訂單的頁面
- **WHEN** 已切換至 Organizer A 的使用者請求第 1 頁，而買家查不到的訂單落在第 2 頁
- **THEN** 系統正常回應第 1 頁；請求第 2 頁時以非預期錯誤失敗（500）
