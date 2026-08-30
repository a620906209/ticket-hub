## ADDED Requirements

### Requirement: 查詢銷售報表需要 Admin 角色
系統 SHALL 要求呼叫單一活動銷售報表查詢端點者持有效 JWT 且角色為 Admin；未提供有效 Token 或角色非 Admin MUST 被拒絕。

#### Scenario: Admin 成功查詢銷售報表
- **WHEN** 持有效 JWT 且角色為 Admin 的使用者呼叫指定活動的銷售報表查詢端點
- **THEN** 系統受理該請求並依端點邏輯處理

#### Scenario: 非 Admin 會員查詢銷售報表
- **WHEN** 持有效 JWT 但角色非 Admin 的使用者呼叫銷售報表查詢端點
- **THEN** 系統回傳 403 拒絕存取

#### Scenario: 未帶 Token 查詢銷售報表
- **WHEN** 未提供 Authorization Header 或 Token 無效，呼叫銷售報表查詢端點
- **THEN** 系統回傳 401 未授權

### Requirement: 查詢單一活動的銷售彙總報表
系統 SHALL 提供 Admin 依活動 Id 查詢該活動即時銷售彙總報表的端點，回傳查詢當下的總營收（僅統計 `Order.Status = Paid` 的訂單，`Pending`／`Cancelled` 訂單 MUST NOT 計入）、總售出票券張數，以及依票種（`TicketType`）拆分的售出張數與營收明細。報表 SHALL 涵蓋座位制（`RequiresSeat = true`）與計數制（`RequiresSeat = false`）兩種票種，統計方式一致（依 `OrderItem.Quantity` 加總張數、`Quantity × UnitPrice` 加總營收）。所有金額 SHALL 以 `OrderItem.UnitPrice`（下單當下寫入的成交單價快照）計算，MUST NOT 重新查詢或套用 `TicketType.Price` 目前的設定值——票種價格在活動中途調整不影響已付款訂單的報表金額。查詢的活動 MUST 存在，不存在時 MUST 回報找不到資源。報表為查詢當下的即時快照，不提供任何時間序列或歷史趨勢資料，不做任何快取（每次查詢皆反映呼叫當下的資料庫實際內容）。查詢時機不受活動開始時間限制，活動建立後即可查詢（即便尚未開賣或尚未有任何訂單）。總營收、總售出票券張數這兩個金額類數字 SHALL 來自同一次資料庫聚合查詢，確保「總數 = 依票種明細加總 + 無法歸類分組加總」這個等式恆成立，不因查詢時序產生落差；票種目錄（用於補上 0 銷售的票種、以及判斷某個分組是否屬於本活動，見下一條 Requirement）是另一次獨立查詢，MUST NOT 要求與前述聚合查詢具備交易級快照一致性——票種目錄不提供刪除功能，這個獨立性在實務上不影響任何金額數字的正確性。

#### Scenario: 查詢有銷售記錄的活動
- **WHEN** Admin 對存在且已有已付款訂單的活動查詢銷售報表
- **THEN** 系統回傳該活動的總營收、總售出票券張數，以及依票種拆分的售出張數與營收，數字僅反映 `Status = Paid` 的訂單內容

#### Scenario: 查詢混合座位制與計數制票種的活動
- **WHEN** 活動同時有座位制與計數制兩種票種，兩者皆有已付款訂單
- **THEN** 依票種明細 MUST 分別列出兩種票種各自的售出張數與營收，總營收與總售出張數 MUST 為兩者加總

#### Scenario: 尚未有任何銷售的活動
- **WHEN** Admin 對存在、但目前沒有任何已付款訂單的活動查詢銷售報表（不論是否已有票種、是否已有 Pending/Cancelled 訂單）
- **THEN** 系統 MUST 回傳成功，總營收與總售出票券張數皆為 0，不得視同找不到或回傳錯誤

#### Scenario: 活動存在但沒有任何票種也沒有任何訂單
- **WHEN** Admin 對存在、但目前尚未建立任何票種、也沒有任何訂單的活動查詢銷售報表
- **THEN** 系統 MUST 回傳成功，`TotalRevenue`／`TotalTicketsSold`／`UnclassifiedItemCount`／`UnclassifiedTicketsSold`／`UnclassifiedRevenue` 皆為 0，`ByTicketType` MUST 為空陣列，不得視同找不到或回傳錯誤

#### Scenario: 活動的票種尚未賣出任何張數
- **WHEN** 活動底下某個票種目前沒有任何已付款訂單項目，但其他票種有
- **THEN** 依票種明細 MUST 包含該票種，售出張數與營收為 0，不得從明細清單中省略

#### Scenario: Pending 與 Cancelled 訂單不計入報表
- **WHEN** 活動有狀態為 `Pending`（含已逾時但尚未被背景清理程序處理）或 `Cancelled` 的訂單
- **THEN** 這些訂單的項目 MUST NOT 計入總營收、總售出張數或依票種明細

#### Scenario: 查詢不存在的活動
- **WHEN** Admin 對不存在的活動 Id 查詢銷售報表
- **THEN** 系統 MUST 回報找不到資源

### Requirement: 依票種明細排除無法歸類票種的已付款項目，並以明確欄位回報筆數
系統 SHALL 將「無法歸類」的已付款項目定義為：`OrderItem.TicketTypeId` 為 null（僅可能存在於本能力上線前建立的舊資料），或 `TicketTypeId` 有值但不在該活動當下的票種目錄清單中（正常下單流程 MUST 保證票種與訂單屬於同一活動，這類項目理論上不應存在；但資料庫層級沒有約束保證 `TicketType.EventId` 恆等於 `Order.EventId`，故報表 SHALL 視同資料異常處理，不假設它不會發生）。系統 SHALL 將無法歸類的項目排除於依票種明細（`ByTicketType`）之外，但 MUST NOT 將其排除於總營收與總售出票券張數之外，也 MUST NOT 靜默捨棄（不計入任何數字）——總數 MUST 反映所有已付款項目的實際金額與張數，不因缺少票種分類而低估或憑空消失。回應內容 SHALL 額外提供 `UnclassifiedItemCount`（無法歸類的項目筆數）、`UnclassifiedTicketsSold`（無法歸類項目的售出張數）、`UnclassifiedRevenue`（無法歸類項目的營收）三個明確欄位，兩種成因統一計入這三個欄位；前端頁面 SHALL 依 `UnclassifiedItemCount > 0` 判斷是否顯示提示文字（例如「含 N 筆無法歸類的項目」，N 取自 `UnclassifiedItemCount`；措辭 MUST NOT 使用「舊資料」等字眼暗示單一成因——無法歸類的成因不只一種，見本 Requirement 開頭定義），MUST NOT 用「總數減依票種明細加總」反推筆數——金額或張數的差額無法反推出實際筆數。

#### Scenario: 已付款訂單存在 TicketTypeId 為 null 的舊資料項目
- **WHEN** 活動的已付款訂單中，有項目的 `TicketTypeId` 為 null
- **THEN** 該項目的金額與張數 MUST 計入總營收與總售出票券張數，但 MUST NOT 出現在依票種明細中；回應的 `UnclassifiedItemCount`／`UnclassifiedTicketsSold`／`UnclassifiedRevenue` MUST 正確反映這類項目的筆數、張數與金額，前端 SHALL 依 `UnclassifiedItemCount > 0` 顯示提示

#### Scenario: 已付款訂單存在指向其他活動票種的資料異常項目
- **WHEN** 活動的已付款訂單中，有項目的 `TicketTypeId` 有值，但該 `TicketType` 實際屬於另一個活動（不在本活動票種目錄中）
- **THEN** 該項目 MUST NOT 被靜默捨棄——其金額與張數 MUST 計入總營收與總售出票券張數與 `UnclassifiedItemCount`／`UnclassifiedTicketsSold`／`UnclassifiedRevenue`，且 MUST NOT 出現在依票種明細中

#### Scenario: 沒有無法歸類的項目
- **WHEN** 活動的已付款訂單中所有項目皆有對應且屬於本活動的 `TicketTypeId`
- **THEN** 回應的 `UnclassifiedItemCount`／`UnclassifiedTicketsSold`／`UnclassifiedRevenue` MUST 皆為 0，前端 MUST NOT 顯示無法歸類提示
