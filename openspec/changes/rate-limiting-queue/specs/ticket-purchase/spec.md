## MODIFIED Requirements

### Requirement: 瀏覽活動與座位可售狀態
系統 SHALL 提供不需登入即可查詢的端點，讓使用者查詢活動列表、活動的座位可售狀態（含分區代碼，供對應票種價格）、活動的票種與價格。查詢票種列表時，每筆票種 SHALL 附帶 `RequiresSeat`，供呼叫端判斷該票種是否需要另外指定座位；`RequiresSeat = false` 的票種 SHALL 額外附帶當下的可售總量（`AvailableQuantity`）。查詢活動列表時，每筆活動 SHALL 額外附帶 `IsQueueModeEnabled`（是否處於熱門搶購模式），供前端判斷買家進入該活動詳情頁時是否需先加入排隊；此欄位為活動的公開屬性，不需登入即可取得，與需要登入的排隊加入/查詢端點不同。

#### Scenario: TP-BROWSE-001 查詢活動列表
- **WHEN** 使用者查詢活動列表
- **THEN** 系統回傳目前已建立的活動基本資訊，每筆活動附帶 `IsQueueModeEnabled`

#### Scenario: TP-BROWSE-002 查詢活動座位可售狀態
- **WHEN** 使用者查詢某活動的座位狀態
- **THEN** 系統回傳該活動每個座位當下的可售狀態與所屬分區代碼

#### Scenario: TP-BROWSE-003 查詢活動票種與價格
- **WHEN** 使用者查詢某活動的票種列表
- **THEN** 系統回傳該活動已建立的票種、對應價格，以及每個票種的 `RequiresSeat`；`RequiresSeat = false` 的票種另附帶當下可售總量

#### Scenario: TP-BROWSE-004 查詢不存在的活動
- **WHEN** 使用者以不存在的活動 ID 查詢座位可售狀態或票種列表
- **THEN** 系統回傳 404 找不到資源，不得擲出未預期例外

### Requirement: 透過 API 建立訂單並鎖定座位或扣減票種庫存
系統 SHALL 提供已登入會員建立訂單的端點，可在同一次請求中混合兩種選購項目：座位項目（指定 `EventSeatId` 與對應 `TicketTypeId`，票種須為 `RequiresSeat = true`）、計數項目（指定 `TicketTypeId` 與購買數量，票種須為 `RequiresSeat = false`，不指定 `EventSeatId`）。鎖定/扣減與驗證規則遵循既有 `ticket-ordering` 能力的規範（原子性鎖定或扣減、票價快照、跨活動與重複選位驗證等），並將發起建立的會員身份記錄為訂單的買家身份。選定的座位或票種若不存在，MUST 拒絕建立並回報找不到對應資源。座位項目所選座位實際所屬的分區 MUST 與所選票種的分區一致，不一致 MUST 拒絕建立。項目所指定的票種與其 `RequiresSeat` 屬性不一致時（例如對 `RequiresSeat = false` 的票種指定 `EventSeatId`，或對 `RequiresSeat = true` 的票種只指定數量、不指定座位，或對 `RequiresSeat = true` 的票種指定 `EventSeatId` 卻同時指定非 1 的購買數量）MUST 拒絕建立並回報驗證錯誤。同一次請求中，`EventSeatId` 為空的計數項目之間 `TicketTypeId` MUST 互不重複——買家想購買多張同一計數票種，MUST 將數量加總為單一選購項目的購買數量送出，不接受拆成多筆重複的計數項目。選購項目未提供購買數量時，系統 MUST 視為 1，維持本次變更前既有客戶端（未帶此欄位)的既有座位選購行為不受影響。若訂單所屬活動的 `IsQueueModeEnabled = true`（熱門搶購模式），系統 MUST 以「建立訂單當下」重新查得的排隊資格為準，確認發起建立的會員在該活動有狀態為 `Admitted` 且未逾時的排隊紀錄，沒有則 MUST 拒絕建立訂單、不執行任何座位鎖定或庫存扣減，並以 `403 Forbidden`（`ProblemDetails`，`title` 為穩定字串 `"QueueAdmissionRequired"`，與座位/庫存競爭失敗使用的 `409 Conflict`、以及既有其他業務語意也可能回傳的 403（例如非本人操作）明確區隔，前端 MUST 同時檢查 `status === 403` 且 `title === "QueueAdmissionRequired"` 兩者，不得只依 HTTP 狀態碼分流，據以導向排隊等待畫面）回報需先加入排隊；即使該會員在送出請求當下確實持有有效資格，只要在系統實際檢查的當下該資格已變為逾時或不存在，仍 MUST 視為不合格並拒絕——不得沿用請求開始前、或前端輪詢當下查到的舊有資格結果。`IsQueueModeEnabled = false` 的活動不受此限制，行為與本次變更前一致。同理，「活動是否處於熱門搶購模式」本身也 MUST 以系統實際執行建立邏輯當下重新讀取到的值為準，不得沿用建立訂單流程開始前（尚未進入實際處理）查詢到的舊值——若 Admin 在買家的建立訂單請求處理過程中變更了該活動的熱門搶購模式，系統 MUST 依實際處理當下的設定值決定是否套用排隊資格檢查，不得因為讀到變更前的舊值而錯誤地略過或誤套用排隊檢查。此請求另受 `api-rate-limiting` 能力定義的請求頻率限制規範，超過限制時依該能力規則拒絕，不進入本需求的建立邏輯。

#### Scenario: TP-ORDER-001 成功建立訂單
- **WHEN** 已登入會員選定多個皆可售的座位與對應票種建立訂單
- **THEN** 系統成功建立 Pending 訂單，所選座位轉為 Held，訂單的買家身份為該會員

#### Scenario: TP-ORDER-002 座位已被鎖定時建立訂單失敗
- **WHEN** 已登入會員選定的座位中，有一個已處於未逾時的 Held 狀態
- **THEN** 系統回傳衝突錯誤，不建立訂單，本次已鎖定成功的其餘座位復原為 Available

#### Scenario: TP-ORDER-003 選定不存在的座位或票種
- **WHEN** 已登入會員選定的座位或票種當中，有一個實際不存在
- **THEN** 系統 MUST 拒絕建立訂單並回報找不到對應資源，不對任何座位執行鎖定（不建立業務上的 Held 狀態、不落地寫入；查詢過程中可能對座位短暫取得資料庫層級的 row lock，交易結束即釋放）

#### Scenario: TP-ORDER-004 座位分區與票種分區不一致
- **WHEN** 已登入會員選定的座位當中，有一個實際所屬分區與配對的票種分區不同（例如座位屬於 A 區，卻配了 B 區票種）
- **THEN** 系統 MUST 拒絕建立訂單並回報驗證錯誤，不建立訂單也不對任何座位執行鎖定（不建立業務上的 Held 狀態、不落地寫入；查詢過程中可能對座位短暫取得資料庫層級的 row lock，交易結束即釋放）

#### Scenario: TP-ORDER-005 成功建立純計數選購的訂單
- **WHEN** 已登入會員對 `RequiresSeat = false` 的票種指定購買數量（不指定 `EventSeatId`）建立訂單，且指定數量不超過當下可售總量
- **THEN** 系統成功建立 Pending 訂單，該票種的可售總量相應扣減，訂單的買家身份為該會員

#### Scenario: TP-ORDER-006 純計數票種指定了座位
- **WHEN** 已登入會員對 `RequiresSeat = false` 的票種在請求中指定了 `EventSeatId`
- **THEN** 系統 MUST 拒絕建立訂單並回報驗證錯誤，不對任何座位或庫存執行變更

#### Scenario: TP-ORDER-007 綁座位票種未指定座位
- **WHEN** 已登入會員對 `RequiresSeat = true` 的票種在請求中只指定數量、未指定 `EventSeatId`
- **THEN** 系統 MUST 拒絕建立訂單並回報驗證錯誤，不對任何座位或庫存執行變更

#### Scenario: TP-ORDER-008 座位項目指定非 1 的購買數量
- **WHEN** 已登入會員對 `RequiresSeat = true` 的票種在請求中指定了 `EventSeatId`，同時指定購買數量不等於 1
- **THEN** 系統 MUST 拒絕建立訂單並回報驗證錯誤，不對任何座位或庫存執行變更

#### Scenario: TP-ORDER-009 同一計數票種在同一次請求中重複出現
- **WHEN** 已登入會員在同一次建立訂單請求中，對同一個 `RequiresSeat = false` 的票種送出兩筆以上不指定 `EventSeatId` 的選購項目（例如分開指定購買數量 2 與 3）
- **THEN** 系統 MUST 拒絕建立訂單並回報驗證錯誤，不對任何座位或庫存執行變更；買家應改為送出單一選購項目、購買數量為加總後的值

#### Scenario: TP-ORDER-010 座位選購項目未提供購買數量（既有客戶端相容）
- **WHEN** 已登入會員呼叫建立訂單端點，選購項目比照本次變更前的既有格式，只提供 `EventSeatId` 與 `TicketTypeId`、未包含購買數量欄位
- **THEN** 系統 MUST 視為購買數量 1，依既有座位選購規則處理，行為與本次變更前完全一致

#### Scenario: TP-ORDER-011 熱門搶購模式下已入場的會員成功建立訂單
- **WHEN** 已登入會員在 `IsQueueModeEnabled = true` 的活動中，擁有狀態為 `Admitted` 且未逾時的排隊紀錄，送出建立訂單請求
- **THEN** 系統依既有規則正常處理建立訂單，成功後將該筆排隊紀錄標記為 `Completed`

#### Scenario: TP-ORDER-012 熱門搶購模式下未入場即嘗試建立訂單
- **WHEN** 已登入會員在 `IsQueueModeEnabled = true` 的活動中，沒有狀態為 `Admitted` 且未逾時的排隊紀錄，送出建立訂單請求
- **THEN** 系統 MUST 拒絕建立訂單，回傳 `403 Forbidden`（`title = "QueueAdmissionRequired"`），不執行任何座位鎖定或庫存扣減，回報需先加入排隊

#### Scenario: TP-ORDER-013 一般活動不受熱門搶購模式影響
- **WHEN** 已登入會員對 `IsQueueModeEnabled = false` 的活動建立訂單
- **THEN** 系統依既有規則正常處理，不檢查排隊紀錄

#### Scenario: TP-ORDER-014 排隊資格於建立訂單處理過程中才變為逾時
- **WHEN** 已登入會員送出建立訂單請求時排隊資格仍為 `Admitted` 且未逾時，但系統實際執行資格確認的當下，該資格已超過 `AdmissionExpiresAtUtc`
- **THEN** 系統 MUST 拒絕建立訂單，回傳 `403 Forbidden`（`title = "QueueAdmissionRequired"`），不執行任何座位鎖定或庫存扣減——即使請求送出當下資格仍有效，仍以系統檢查當下的最新狀態為準

#### Scenario: TP-ORDER-015 建立訂單處理過程中熱門搶購模式才被開啟
- **WHEN** 買家送出建立訂單請求時活動的 `IsQueueModeEnabled` 仍為 `false`，但在系統實際執行建立邏輯之前，Admin 已將該活動切換為 `true`，且買家不具備已入場的排隊資格
- **THEN** 系統 MUST 以切換後的最新設定值為準，拒絕建立訂單並回傳 `403 Forbidden`（`title = "QueueAdmissionRequired"`），不得因請求送出當下讀到的是切換前的 `false` 而略過排隊檢查、繼續完成建立

#### Scenario: TP-ORDER-016 建立訂單處理過程中熱門搶購模式才被關閉
- **WHEN** 買家送出建立訂單請求時活動的 `IsQueueModeEnabled` 仍為 `true` 且買家不具備已入場的排隊資格，但在系統實際執行建立邏輯之前，Admin 已將該活動切換為 `false`
- **THEN** 系統 MUST 以切換後的最新設定值為準，不再檢查排隊資格，依一般活動的既有規則正常處理建立訂單
