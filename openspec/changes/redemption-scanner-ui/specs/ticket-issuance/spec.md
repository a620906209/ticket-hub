## MODIFIED Requirements

### Requirement: 每張票券可依 Ticket ID 按需產生 QR Code 內容以 HMAC 簽章防偽
系統 SHALL 提供依 Ticket ID **按需（on-demand）**產生 QR Code 的能力，內容為該 Ticket ID 附加 HMAC-SHA256 簽章（簽章金鑰透過設定注入，MUST NOT 寫死於程式碼或進版控設定檔），使驗票端（例如現場掃碼前端，見 `admin-web-ui` 與 `ticket-redemption` 能力）能判斷票券內容未被竄改；系統 SHALL 提供對應的驗章方法，輸入內容被竄改或簽章不符時 MUST 驗證失敗。此產生能力為可隨時依 Ticket ID 重新推導，出票交易（見「訂單確認付款成功時自動出票」需求）MUST NOT 呼叫此能力，不預先產生或持久化圖檔。

**QR 內容格式契約（精確定義，供出票端、前端 parser、後端驗證三方對齊）**：內容為 `{TicketId}.{Signature}`，恰好一個 `.` 分隔符；`{TicketId}` 為 Ticket ID 的 `"D"` 格式字串（32 位小寫十六進位、含連字號，例如 `3fa85f64-5717-4562-b3fc-2c963f66afa6`，不含大括號）；`{Signature}` 為對 `{TicketId}` 文字（`"D"` 格式字串本身，非其他表示法）計算 HMAC-SHA256 後以 Base64Url 編碼（無 padding）的結果。驗章時 MUST 以此精確格式重新計算並比對，任何額外字元、缺少分隔符、分隔符數量不為一、或 `{TicketId}` 不是合法 `"D"` 格式，MUST 導致驗證失敗，不得嘗試容錯解析。此格式契約為系統內部驗證用途，前端對此格式的任何檢查（例如避免明顯無效內容浪費 API 呼叫）僅為便利性檢查，不構成安全邊界——後端驗章方法才是唯一可信的驗證依據，即使收到不符合此格式的任意輸入也 MUST 安全回傳驗證失敗，不得拋出例外。

系統 SHALL NOT 將 QR 內容或簽章值本身輸出至一般應用程式日誌（結構化 log 僅可記錄 Ticket ID 等非機敏識別資訊，比照 CLAUDE.md 機敏資訊管理規則）。

#### Scenario: TICKET-ISSUANCE-QR-VERIFY-VALID 依 Ticket ID 產生的 QR 內容可驗章通過
- **WHEN** 對依 Ticket ID 產生的 QR 內容執行驗章
- **THEN** 驗證結果為通過，並還原出正確的 Ticket ID

#### Scenario: TICKET-ISSUANCE-QR-VERIFY-TAMPERED 竄改後的內容驗章失敗
- **WHEN** 對依 Ticket ID 產生的 QR 內容任意竄改一個字元後執行驗章
- **THEN** 驗證結果為失敗

#### Scenario: TICKET-ISSUANCE-QR-VERIFY-MALFORMED 格式不符的任意輸入安全回傳失敗
- **WHEN** 對不含分隔符、分隔符數量不為一、或前段不是合法 `"D"` 格式 GUID 的任意字串（含 `null`／空字串）執行驗章
- **THEN** 驗證結果為失敗，不拋出例外，也不還原出任何 Ticket ID
