## ADDED Requirements

### Requirement: 核銷 API 可選驗證 QR 簽章內容
系統 SHALL 允許核銷端點（`PATCH /api/admin/tickets/{id}/redeem`）的請求 body 附帶可選欄位 `signature`（字串型別）。當 `signature` 為 `null` 或整個請求 body 未提供時，系統 SHALL 維持既有行為，直接以資料庫狀態為權威來源核銷，不驗證任何簽章。當 `signature` 為非 `null` 的字串時（含空字串或僅空白字元），系統 SHALL 在查詢或鎖定 Ticket 之前，先以路徑參數 `id` 與 `signature` 依 `ticket-issuance` 能力定義的精確格式重組，呼叫既有 `ITicketSigningService.TryVerify` 驗證其未被竄改；驗證失敗（含空字串／空白字元必然驗證失敗的情況）MUST 回傳與「查無此票」（404）、「狀態衝突」（409）可明確區分的錯誤，MUST NOT 查詢或變更任何 Ticket 的狀態；此錯誤 SHALL 具備專屬且穩定的判別依據（比照既有 `ErrorType.QueueAdmissionRequired` 的既定慣例），不得與其他驗證錯誤共用同一個判別依據。`signature` 欄位型別不符（例如數字、物件）時，MUST 在進入此驗證邏輯前即被回絕（框架層級的 request body 反序列化失敗），同樣 MUST NOT 查詢或變更任何 Ticket 的狀態。驗證通過後才進入既有的核銷流程（鎖定、狀態檢查、轉態）。系統 SHALL NOT 將 `signature` 欄位值或完整請求 body 內容輸出至一般應用程式日誌。

#### Scenario: TICKET-REDEEM-SIG-BACKWARD-COMPAT 未提供簽章時維持既有行為
- **WHEN** 呼叫核銷端點時 request body 未附帶 `signature`，或整個 body 省略
- **THEN** 系統直接以資料庫狀態核銷（或依既有規則回報 404／409），行為與新增此需求前完全相同

#### Scenario: TICKET-REDEEM-SIG-VALID 提供正確簽章時驗證通過並核銷
- **WHEN** 呼叫核銷端點時附帶與路徑參數 `id` 相符的正確簽章，且該 Ticket 狀態為 `Issued`
- **THEN** 系統驗證簽章通過，成功核銷

#### Scenario: TICKET-REDEEM-SIG-INVALID 提供不符的簽章
- **WHEN** 呼叫核銷端點時附帶的 `signature` 與路徑參數 `id` 重組後驗證不通過（內容被竄改或簽章錯誤）
- **THEN** 系統回傳可與「查無此票」「已核銷過」明確區分的錯誤，不查詢或變更任何 Ticket 的狀態

#### Scenario: TICKET-REDEEM-SIG-EMPTY 提供空字串或空白字元的簽章
- **WHEN** 呼叫核銷端點時附帶的 `signature` 為空字串或僅含空白字元
- **THEN** 系統視為驗證失敗，回傳與 TICKET-REDEEM-SIG-INVALID 相同的可區分錯誤，不查詢或變更任何 Ticket 的狀態

#### Scenario: TICKET-REDEEM-SIG-TYPE-MISMATCH 簽章欄位型別不符
- **WHEN** 呼叫核銷端點時 request body 的 `signature` 欄位為非字串型別（例如數字）
- **THEN** 系統在框架層級的請求反序列化階段即回絕請求，不進入核銷邏輯，不查詢或變更任何 Ticket 的狀態
