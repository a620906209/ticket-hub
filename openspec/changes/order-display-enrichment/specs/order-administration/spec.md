## MODIFIED Requirements

### Requirement: 查詢所有訂單列表
系統 SHALL 提供已切換至一個 Approved Organizer 的使用者查詢訂單的端點，回傳每筆訂單的基本資訊、買家顯示名稱（`BuyerDisplayName`，取自買家 `Member.DisplayName`）與即時狀態（依 `GetStatus(now)` 推導，可能為 Expired）。回傳範圍 SHALL 僅限於呼叫端目前 Organizer 名下活動（依 `Order.EventId` 對應的 `Event.OrganizerId` 判斷）的訂單，不含其他 Organizer 名下活動的訂單。此過濾 MUST 在資料庫端執行，MUST NOT 先載入其他 Organizer 的訂單再於應用程式記憶體中過濾。買家顯示名稱 SHALL 以單次批次查詢取得列表中所有不重複買家，查詢次數 MUST NOT 隨訂單筆數成長；除顯示名稱外 MUST NOT 回傳買家 Email 或其他個人資料。訂單的 `BuyerId` 對應的會員查不到時，視為資料不一致，系統 SHALL 以非預期錯誤失敗（由全域例外處理轉為 500），MUST NOT 回傳部分資料或以空值代替。

#### Scenario: ORD-LIST-001 查詢訂單列表僅回傳目前所屬 Organizer 名下活動的訂單
- **WHEN** 已切換至 Organizer A 的使用者呼叫訂單列表端點，資料庫中同時存在 Organizer A 與 Organizer B 名下活動各自的訂單
- **THEN** 系統只回傳 Organizer A 名下活動的訂單（含每筆的基本資訊與即時狀態），不包含 Organizer B 名下活動的訂單

#### Scenario: ORD-LIST-002 訂單列表包含買家顯示名稱且不含 Email
- **WHEN** 已切換至 Organizer A 的使用者呼叫訂單列表端點，列表中有兩位不同買家的訂單
- **THEN** 每筆訂單的 `BuyerDisplayName` 為其買家的顯示名稱，回應中不包含任何買家 Email 欄位

#### Scenario: ORD-LIST-004 買家顯示名稱的查詢次數不隨訂單筆數成長
- **WHEN** 已切換至 Organizer A 的使用者分別在「1 筆訂單、1 位買家」與「3 筆訂單、3 位不同買家」的資料下呼叫訂單列表端點
- **THEN** 兩次請求對資料庫發出的查詢次數相同

#### Scenario: ORD-LIST-003 訂單買家查不到（資料不一致）
- **WHEN** 已切換至 Organizer A 的使用者呼叫訂單列表端點，其中一筆訂單的 `BuyerId` 對應的會員在資料庫中不存在
- **THEN** 系統以非預期錯誤失敗（500），不回傳訂單列表
