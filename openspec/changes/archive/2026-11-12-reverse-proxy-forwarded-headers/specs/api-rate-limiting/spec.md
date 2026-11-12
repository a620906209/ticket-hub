# Spec Delta

## MODIFIED Requirements

### Requirement: 登入端點的請求頻率限制
系統 SHALL 針對 `POST /api/auth/login` 套用以來源 IP 位址為分區鍵的固定時間窗（Fixed Window）請求頻率限制，與既有下單相關端點的限流（`place-order`／`confirm-order` policy）各自獨立累計，不共用同一組計數。分區鍵使用 `HttpContext.Connection.RemoteIpAddress`（無法取得時退回固定字串 `"unknown"` 分區，不得因此拋出未預期例外）——登入端點呼叫當下使用者尚未通過驗證，不具備會員 Id 可用，故不採用既有下單端點「以已登入會員 Id 分區」的做法。同一來源 IP 在單一時間窗內的登入請求次數超過設定上限時，系統 MUST 拒絕該次請求，不得進入登入 Controller action（依賴 ASP.NET Core `RateLimiter` middleware 的既定短路管線行為——`[EnableRateLimiting]` 攔截發生在 endpoint routing 解析出 endpoint metadata 之後、model binding 與 controller action 執行之前，比照既有下單相關端點限流依賴的同一個框架保證，不重新驗證此框架行為本身），因此不會查詢會員是否存在、不會驗證密碼、不會核發 Access Token 或 Refresh Token；可觀察的 HTTP 結果為：拒絕回應 MUST 包含 `429` 狀態碼、`Retry-After` 標頭、`ProblemDetails` body（`status`、`title`、`traceId`），且回應內容 MUST 不含任何 Token 欄位。視窗起算與重置規則、拒絕回應格式比照既有 `api-rate-limiting` 能力「下單相關端點的請求頻率限制」與「限流拒絕回應格式統一為 ProblemDetails，並附帶 Retry-After」兩條 Requirement 的既定規則，共用同一個回應格式機制。頻率限制的次數上限與時間窗長度 SHALL 透過獨立的設定值調整（與下單端點的設定值分開，不共用同一組數值），不寫死於程式碼。

**部署前提**：`RemoteIpAddress` 須反映真實用戶端位址。部署在反向代理之後時，由 `client-address-resolution` 能力還原（代理須轉送 `X-Forwarded-For`，且代理位於受信任網段）；代理未轉送標頭或不在信任網段時，所有請求會共用代理的位址而落入同一個分區。

#### Scenario: LRL-001 登入請求次數未超過限制
- **WHEN** 同一來源 IP 在目前時間窗內對 `POST /api/auth/login` 的呼叫次數尚未達到設定上限
- **THEN** 系統正常處理該次請求，依既有登入邏輯驗證帳密並回應

#### Scenario: LRL-002 恰好第 PermitLimit 次請求仍允許
- **WHEN** 同一來源 IP 在目前時間窗內對 `POST /api/auth/login` 的呼叫次數恰好是第 `PermitLimit` 次
- **THEN** 系統正常處理該次請求，不視為超過限制

#### Scenario: LRL-003 第 PermitLimit+1 次請求起拒絕
- **WHEN** 同一來源 IP 在目前時間窗內對 `POST /api/auth/login` 的呼叫次數已達到設定上限，再送出下一次請求（即使該次請求攜帶的帳密正確）
- **THEN** 系統 MUST 一律拒絕該次請求，回傳 `429 Too Many Requests`，不因帳密正確而放行；回應內容 MUST 不含任何 Token 欄位（可觀察的 HTTP 結果；「不查詢會員、不驗證密碼」是 middleware 短路管線帶來的框架保證，不重新驗證框架行為本身，見上方 Requirement 說明）

#### Scenario: LRL-004 不同來源 IP 的限流各自獨立
- **WHEN** 來源 IP A 在目前時間窗內對 `POST /api/auth/login` 已達到請求次數上限，來源 IP B 對同一端點送出登入請求
- **THEN** 來源 IP B 的請求不受來源 IP A 的用量影響，依正常流程處理

#### Scenario: LRL-005 時間窗重置後恢復可請求
- **WHEN** 同一來源 IP 在前一個時間窗內已達到請求次數上限，新的時間窗開始後再次送出登入請求
- **THEN** 系統視為新時間窗的第一次請求，正常處理

#### Scenario: LRL-006 登入端點的限流與下單端點的限流互不影響
- **WHEN** 已登入會員對應的來源 IP 對 `POST /api/orders` 的請求次數已達到該端點的限流上限（`place-order` policy 以會員 Id 為分區鍵），該會員（同一來源 IP）接著對 `POST /api/auth/login` 送出請求（`login` policy 以來源 IP 為分區鍵，例如重新登入）
- **THEN** `POST /api/auth/login` 的請求不受 `POST /api/orders` 用量已達上限影響，依其自身額度正常處理——兩個 policy 不僅計數獨立，分區鍵語意本身也不同（會員 Id vs 來源 IP），不存在共用計數的可能

#### Scenario: LRL-010 登入限流拒絕回應遵循統一格式
- **WHEN** 同一來源 IP 對 `POST /api/auth/login` 的請求被限流拒絕
- **THEN** 回應 HTTP 狀態碼為 `429`，包含 `Retry-After` 標頭，body 為 `ProblemDetails` 格式且 `status = 429`、`title = "TooManyRequests"`、包含 `traceId` 欄位——與既有下單端點限流拒絕回應格式一致（共用同一個 `OnRejected` callback）

#### Scenario: LRL-011 反向代理後不同用戶端各自計數
- **WHEN** 請求經受信任代理送達，用戶端 A（`X-Forwarded-For: 203.0.113.1`）已達登入上限，用戶端 B（`X-Forwarded-For: 203.0.113.2`）送出登入請求
- **THEN** 用戶端 B 的請求不受影響，依正常流程處理；用戶端 A 的下一次請求回傳 `429`

#### Scenario: LRL-012 非信任來源無法以偽造標頭繞過限流
- **WHEN** 來源不在信任網段的用戶端每次請求都帶不同的 `X-Forwarded-For`，連續送出超過上限的登入請求
- **THEN** 標頭被忽略，所有請求落入同一個分區，超過上限後回傳 `429`

## ADDED Requirements

### Requirement: 公開活動讀取端點的請求頻率限制
系統 SHALL 對 `EventsController` 的匿名讀取端點（`GET /api/events`、`GET /api/events/{id}`、`GET /api/events/{id}/seats`、`GET /api/events/{id}/ticket-types`）套用獨立命名的 `public-read` Fixed Window 限流，以來源 IP 分區；四個端點共用同一個 IP 的額度，不與其他 policy 共用計數。不排隊，超過額度的請求 MUST 立即回傳 `429`，格式與既有限流拒絕相同（`ProblemDetails`，含 `status`、`title`、`traceId`，附 `Retry-After`），且 MUST NOT 執行查詢或讀取快取。健康檢查探針與已登入端點不受此 policy 影響。

#### Scenario: PRL-001 超過額度回 429
- **WHEN** 同一來源 IP 在同一時間窗內對公開活動讀取端點的請求總數超過額度
- **THEN** 超過額度的請求回傳 `429`，本文為 `ProblemDetails` 並附 `Retry-After`

#### Scenario: PRL-002 不同來源 IP 各自計數
- **WHEN** 來源 IP A 已用完額度，來源 IP B（經受信任代理，`X-Forwarded-For` 不同）請求 `GET /api/events`
- **THEN** 來源 IP B 的請求正常處理

#### Scenario: PRL-003 與其他 policy 計數獨立
- **WHEN** 同一來源 IP 已用完 `public-read` 額度，接著呼叫 `GET /api/captcha` 或 `POST /api/auth/login`
- **THEN** 這些請求依各自 policy 的額度處理，不受 `public-read` 影響

#### Scenario: PRL-004 探針不受影響
- **WHEN** 同一來源 IP 已用完 `public-read` 額度，請求 `/health/live`
- **THEN** 回傳探針結果，不是 `429`

### Requirement: 公開讀取限流設定值須為正數，缺漏時採用預設值
系統 SHALL 以 `PublicReadRateLimiting:PermitLimit` 與 `PublicReadRateLimiting:WindowSeconds` 設定額度，缺漏時採用預設值 120 次／60 秒；設定為 0 或負數時 MUST 在應用程式啟動時擋下。

#### Scenario: PRL-005 設定缺漏時採用預設值
- **WHEN** 設定未提供 `PublicReadRateLimiting` 區段
- **THEN** 系統以 PermitLimit 120、WindowSeconds 60 運作，不視為錯誤

#### Scenario: PRL-006 設定值為 0 或負數時擋下
- **WHEN** `PublicReadRateLimiting:PermitLimit` 或 `WindowSeconds` 被設為 0 或負數
- **THEN** 應用程式啟動失敗，不以此設定值運作
