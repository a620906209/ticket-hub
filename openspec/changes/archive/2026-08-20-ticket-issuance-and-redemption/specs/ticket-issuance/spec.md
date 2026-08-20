## ADDED Requirements

### Requirement: 訂單確認付款成功時自動出票
系統 SHALL 在訂單確認流程（見 `ticket-purchase` 能力「透過 API 確認訂單」）付款成功、座位/計數項目確認完成後，於同一交易內為該訂單建立電子票券：座位項目（`EventSeatId` 有值）固定產出 1 張 Ticket；計數項目依 `Quantity` 產出對應張數的 Ticket，每張各自可獨立核銷、互不影響。每張 Ticket 建立時的初始狀態為 `Issued`，並記錄產出時間（`IssuedAtUtc`）。每張 Ticket MUST 正確關聯回其來源的 `OrderItem`（`Ticket.OrderItemId`）：座位項目產出的 Ticket MUST 關聯到該座位項目自己的 `OrderItemId`，計數項目產出的多張 Ticket MUST 全部關聯到該計數項目自己的 `OrderItemId`；訂單內有多筆項目時，MUST NOT 出現 Ticket 關聯到錯誤（非其來源）的 `OrderItem` 的情況。Ticket 狀態列舉包含 `Issued`、`Redeemed`、`Voided` 三值；`Voided` 保留供未來退款流程使用，本次範疇內系統 MUST NOT 提供任何會使 Ticket 進入 `Voided` 的觸發路徑（現有系統無已付款訂單取消/退款機制）。

#### Scenario: 座位訂單出票數量正確
- **WHEN** 訂單內含 3 筆座位項目，確認付款成功
- **THEN** 系統為該訂單建立 3 張 Ticket，狀態皆為 `Issued`，且每張 Ticket 的 `OrderItemId` 各自對應到產生它的座位項目

#### Scenario: 計數項目依購買數量出票
- **WHEN** 訂單內含一筆購買數量 5 的計數項目，確認付款成功
- **THEN** 系統為該項目建立 5 張各自獨立的 Ticket，狀態皆為 `Issued`，且 5 張 Ticket 的 `OrderItemId` 皆對應到該計數項目

#### Scenario: 混合項目出票數量加總正確且各自歸屬正確的項目
- **WHEN** 訂單同時含 2 筆座位項目與一筆購買數量 3 的計數項目，確認付款成功
- **THEN** 系統建立共 5 張 Ticket；其中 2 張 `OrderItemId` 分別對應到各自的座位項目，另 3 張 `OrderItemId` 皆對應到該計數項目，不得有 Ticket 關聯錯誤的 `OrderItem`

#### Scenario: 付款失敗不出票
- **WHEN** 訂單確認因 `IPaymentGateway` 回報付款失敗而被拒絕
- **THEN** 系統不建立任何 Ticket

### Requirement: 每張票券可依 Ticket ID 按需產生 QR Code 內容以 HMAC 簽章防偽
系統 SHALL 提供依 Ticket ID **按需（on-demand）**產生 QR Code 的能力，內容為該 Ticket ID 附加 HMAC-SHA256 簽章（簽章金鑰透過設定注入，MUST NOT 寫死於程式碼或進版控設定檔），使未來的驗票端（例如現場掃碼前端，`docs/project-scope.md` 列為 Could、本次範疇外）能離線驗證票券內容未被竄改；系統 SHALL 提供對應的驗章方法，輸入內容被竄改或簽章不符時 MUST 驗證失敗。此產生能力為可隨時依 Ticket ID 重新推導，出票交易（見「訂單確認付款成功時自動出票」需求）MUST NOT 呼叫此能力，不預先產生或持久化圖檔。本次範疇內的核銷流程（見 `ticket-redemption` 能力）不依賴此驗章方法，直接以資料庫狀態作為核銷依據；本次僅要求其自身邏輯正確（可簽出、可驗證、竄改可偵測），不要求被任何核銷路徑或出票流程呼叫。

#### Scenario: 依 Ticket ID 產生的 QR 內容可驗章通過
- **WHEN** 對依 Ticket ID 產生的 QR 內容執行驗章
- **THEN** 驗證結果為通過，並還原出正確的 Ticket ID

#### Scenario: 竄改後的內容驗章失敗
- **WHEN** 對依 Ticket ID 產生的 QR 內容任意竄改一個字元後執行驗章
- **THEN** 驗證結果為失敗
