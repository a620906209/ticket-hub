## MODIFIED Requirements

### Requirement: 本文讀取錯誤以一致的 ProblemDetails 回應
伺服器在讀取本文階段判定的請求錯誤（本文超過上限、本文傳輸編碼錯誤、本文讀取逾時），系統 MUST 以該錯誤對應的 4xx 狀態碼回應，不得回傳 500。回應 MUST 為與既有錯誤回應一致的 `ProblemDetails`，包含 `status`、`title` 與 `traceId`；413 的 `title` MUST 為 `PayloadTooLarge`。這類錯誤 MUST 以 Warning 等級記錄（含 traceId），不得以 Error 等級記錄，且日誌不得包含本文內容。本文已完整讀取後的 JSON 內容語法錯誤不屬於本 Requirement，維持既有模型繫結的 400 回應。非本文來源、但框架同樣以 `BadHttpRequestException` 表示的請求錯誤，系統依其 4xx 狀態碼以相同格式與日誌規則回應（design 決策 4 的刻意一致化）。

#### Scenario: RBL-011 413 回應格式
- **WHEN** 已登入會員對建立訂單端點送出 16,385 bytes 的 JSON 本文
- **THEN** 回應狀態碼為 413，本文為 `ProblemDetails`，`status` 為 413、`title` 為 `PayloadTooLarge`、含 `traceId`

#### Scenario: RBL-012 本文讀取錯誤的日誌等級
- **WHEN** 任一請求因本文超過上限被拒
- **THEN** 系統記錄一筆 Warning 等級、含 traceId 的日誌，不記錄 Error 等級日誌，日誌不含本文內容

#### Scenario: RBL-015 chunked 傳輸編碼錯誤
- **WHEN** 用戶端以 `Transfer-Encoding: chunked` 對登入端點送出格式錯誤的 chunk（chunk size 不是十六進位數字，或 chunk 資料後缺少 CRLF 結尾）
- **THEN** 系統回傳 400，回應為含 `status`、`title`、`traceId` 的 `ProblemDetails`；記錄一筆 Warning 等級、含 traceId 的日誌，不記錄 Error 等級日誌，日誌不含本文內容

#### Scenario: RBL-013 其他未預期例外維持 500
- **WHEN** 請求處理過程中發生不是 `BadHttpRequestException` 的未預期例外
- **THEN** 系統維持既有行為回傳 500 的 `ProblemDetails`，並以 Error 等級記錄
