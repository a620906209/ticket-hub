# Spec Delta

## Purpose

限制 API 每個請求本文的大小，讓超大本文在被完整讀取與反序列化之前就被拒絕，並以一致的錯誤格式回應，保護 Web API 的 CPU、記憶體與請求處理能力。

## ADDED Requirements

### Requirement: 所有端點套用全域請求本文上限
系統 SHALL 對所有接受請求本文的端點套用 1,048,576 bytes（1MB）的本文上限，未另行指定個別上限的端點都適用。本文超過上限時 MUST 回傳 413，且 MUST 在讀到超過上限的位元組時就停止讀取，不得先完整讀取或反序列化整個本文。本文大小未超過上限時，系統 MUST 依端點既有規則處理，不得因本文大小拒絕。上限依實際讀到的本文位元組計算，不論請求以 `Content-Length` 或 chunked 傳送。

#### Scenario: RBL-001 匿名端點本文超過全域上限
- **WHEN** 未登入的使用者對登入端點送出 1,048,577 bytes 的 JSON 本文
- **THEN** 系統回傳 413，不回傳 500

#### Scenario: RBL-002 本文恰為全域上限時不因大小被拒
- **WHEN** 未登入的使用者對登入端點送出恰好 1,048,576 bytes、內容不是有效登入資料的 JSON 本文
- **THEN** 系統依登入端點既有規則回應 4xx，不回傳 413 或 500

### Requirement: 建立訂單端點套用較嚴格的本文上限
系統 SHALL 對建立訂單端點（`POST /api/orders`）套用 16,384 bytes（16KB）的本文上限，取代全域上限。本文超過上限時 MUST 回傳 413，不建立訂單、不鎖定座位、不扣減庫存。身分驗證仍先於本文讀取：未登入的請求依既有規則回傳 401，不回傳 413。

#### Scenario: RBL-003 建立訂單本文超過端點上限
- **WHEN** 已登入會員對建立訂單端點送出 16,385 bytes 的 JSON 本文
- **THEN** 系統回傳 413，不建立訂單，該票種可售量不變

#### Scenario: RBL-004 建立訂單本文恰為端點上限時不因大小被拒
- **WHEN** 符合既有下單條件的已登入會員，對可售且庫存足夠的活動，送出恰好 16,384 bytes 的 JSON 本文（一筆符合既有規則的計數項目，在 JSON 結尾 `}` 之後以空白補足長度，不改變 JSON 語義）
- **THEN** 系統成功建立訂單，不回傳 413

#### Scenario: RBL-014 未登入時本文超過端點上限仍回 401
- **WHEN** 未登入的使用者對建立訂單端點送出 16,385 bytes 的 JSON 本文
- **THEN** 系統回傳 401，不回傳 413

### Requirement: 建立座位圖端點放寬本文上限
系統 SHALL 對建立座位圖端點（`POST /api/admin/venues/{venueId}/seat-maps`）套用 10,485,760 bytes（10MB）的本文上限，取代全域上限，讓大型場館能在單一請求建立座位圖。本文超過此上限時 MUST 回傳 413，不建立座位圖。權限要求不變。

#### Scenario: RBL-005 座位圖本文超過全域上限但未超過端點上限
- **WHEN** 已切換至 Approved Organizer 的使用者送出本文大於 1,048,576 bytes、小於 10,485,760 bytes 的有效座位圖
- **THEN** 系統成功建立座位圖，回傳 201

#### Scenario: RBL-006 座位圖本文超過端點上限
- **WHEN** 已切換至 Approved Organizer 的使用者送出 10,485,761 bytes 的座位圖本文
- **THEN** 系統回傳 413，不建立座位圖

### Requirement: 宣告長度與實際傳送不一致時不得繞過上限
系統 MUST 依下列規則處理本文長度宣告，任何情況都不得因此接受超過上限的本文或回傳 500：`Content-Length` 宣告超過上限時，MUST 不等待本文送完即回傳 413；chunked 本文在讀到超過上限的位元組時 MUST 回傳 413；`Content-Length` 宣告值小於實際送出的位元組時，只把宣告長度內的位元組當作本文；`Content-Length` 宣告值大於實際送出、系統仍在讀取本文而用戶端停止傳送時，MUST 在有限時間內以 408 結束該請求，不得無限期等待。

#### Scenario: RBL-007 Content-Length 宣告超過上限
- **WHEN** 用戶端以 `Content-Length: 2097152` 對登入端點送出請求標頭，尚未送出任何本文
- **THEN** 系統不等待本文即回傳 413

#### Scenario: RBL-008 chunked 本文超過上限
- **WHEN** 用戶端以 `Transfer-Encoding: chunked`、不帶 `Content-Length`，對登入端點分段送出合計 1,048,577 bytes 的本文
- **THEN** 系統回傳 413

#### Scenario: RBL-009 Content-Length 宣告小於實際本文
- **WHEN** 用戶端以 `Content-Length: 2` 對登入端點送出 `{}` 後，在同一連線緊接著送出 1,048,577 bytes 不構成合法 HTTP 請求行的額外資料
- **THEN** 系統只以 `{}` 作為該請求本文並依登入端點既有規則回應（不回傳 413 或 500）；只有這一個請求進入應用程式，額外資料不會被當作該請求本文、也不會成為另一個進入應用程式的請求，伺服器對額外資料可回 400 或關閉連線，但 MUST NOT 回 500

#### Scenario: RBL-010 Content-Length 宣告大於實際本文且用戶端停止傳送
- **WHEN** 用戶端以 `Content-Length: 1000` 對登入端點送出 10 bytes 有效 JSON 前綴（`{"email":"`）後停止傳送，但不關閉連線
- **THEN** 系統在 30 秒內回傳 408，回應為含 `traceId` 的 `ProblemDetails`，不回傳 500；記錄一筆 Warning 等級、含 traceId 的日誌，不記錄 Error 等級日誌，日誌不含本文內容

### Requirement: 本文讀取錯誤以一致的 ProblemDetails 回應
伺服器在讀取本文階段判定的請求錯誤（本文超過上限、本文傳輸編碼錯誤、本文讀取逾時），系統 MUST 以該錯誤對應的 4xx 狀態碼回應，不得回傳 500。回應 MUST 為與既有錯誤回應一致的 `ProblemDetails`，包含 `status`、`title` 與 `traceId`；413 的 `title` MUST 為 `Request body too large.`。這類錯誤 MUST 以 Warning 等級記錄（含 traceId），不得以 Error 等級記錄，且日誌不得包含本文內容。本文已完整讀取後的 JSON 內容語法錯誤不屬於本 Requirement，維持既有模型繫結的 400 回應。

#### Scenario: RBL-011 413 回應格式
- **WHEN** 已登入會員對建立訂單端點送出 16,385 bytes 的 JSON 本文
- **THEN** 回應狀態碼為 413，本文為 `ProblemDetails`，`status` 為 413、`title` 為 `Request body too large.`、含 `traceId`

#### Scenario: RBL-012 本文讀取錯誤的日誌等級
- **WHEN** 任一請求因本文超過上限被拒
- **THEN** 系統記錄一筆 Warning 等級、含 traceId 的日誌，不記錄 Error 等級日誌，日誌不含本文內容

#### Scenario: RBL-015 chunked 傳輸編碼錯誤
- **WHEN** 用戶端以 `Transfer-Encoding: chunked` 對登入端點送出格式錯誤的 chunk（chunk size 不是十六進位數字，或 chunk 資料後缺少 CRLF 結尾）
- **THEN** 系統回傳 400，回應為含 `status`、`title`、`traceId` 的 `ProblemDetails`；記錄一筆 Warning 等級、含 traceId 的日誌，不記錄 Error 等級日誌，日誌不含本文內容

#### Scenario: RBL-013 其他非本文讀取錯誤的例外維持 500
- **WHEN** 請求處理過程中發生非本文讀取造成的未預期例外
- **THEN** 系統維持既有行為回傳 500 的 `ProblemDetails`，並以 Error 等級記錄
