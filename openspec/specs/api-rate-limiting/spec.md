# api-rate-limiting Specification

## Purpose
TBD - created by change rate-limiting-queue. Update Purpose after archive.

## Requirements

### Requirement: 下單相關端點的請求頻率限制
系統 SHALL 針對 `POST /api/orders`（建立訂單）與 `POST /api/orders/{id}/confirm`（確認訂單）套用以會員 Id 為分區鍵的固定時間窗（Fixed Window）請求頻率限制；分區鍵僅使用已登入會員的 Id，不含 IP fallback（此二端點皆已要求登入，未登入請求在進入限流檢查前即被拒絕）。**兩個端點的請求次數各自獨立累計，不共用同一組計數**——會員對 `POST /api/orders` 的呼叫次數不影響其對 `POST /api/orders/{id}/confirm` 的可用額度，反之亦然；兩者的上限與時間窗長度數值可相同，但計數狀態彼此獨立。同一會員在單一端點的單一時間窗內請求次數超過設定上限時，系統 MUST 拒絕該次請求，不執行任何訂單建立、座位鎖定、庫存扣減或訂單確認邏輯。視窗起算點為該分區鍵（會員 Id + 端點對應的 policy）第一次被觀察到請求的時間點，視窗內累計請求數達上限前（含第 `PermitLimit` 次）皆允許，第 `PermitLimit + 1` 次起拒絕；視窗到期後立即重置為新視窗、計數歸零。頻率限制的次數上限（`PermitLimit`）與時間窗長度（`Window`）SHALL 透過設定值調整，不寫死於程式碼。

#### Scenario: RL-001 請求次數未超過限制
- **WHEN** 已登入會員在目前時間窗內對 `POST /api/orders` 的呼叫次數尚未達到設定上限
- **THEN** 系統正常處理該次請求，依既有建立訂單邏輯回應

#### Scenario: RL-002 恰好第 PermitLimit 次請求仍允許
- **WHEN** 已登入會員在目前時間窗內對 `POST /api/orders` 的呼叫次數恰好是第 `PermitLimit` 次
- **THEN** 系統正常處理該次請求，不視為超過限制

#### Scenario: RL-003 第 PermitLimit+1 次請求起拒絕
- **WHEN** 已登入會員在目前時間窗內對 `POST /api/orders` 或 `POST /api/orders/{id}/confirm` 的呼叫次數已達到設定上限，再送出下一次請求
- **THEN** 系統 MUST 拒絕該次請求，回傳 `429 Too Many Requests`，不執行任何訂單建立、座位鎖定、庫存扣減或訂單確認邏輯

#### Scenario: RL-004 兩個端點的用量互不影響
- **WHEN** 已登入會員在目前時間窗內對 `POST /api/orders` 的呼叫次數已達到設定上限，同一會員接著呼叫 `POST /api/orders/{id}/confirm`
- **THEN** `POST /api/orders/{id}/confirm` 的請求不受 `POST /api/orders` 用量已達上限影響，依其自身額度正常處理

#### Scenario: RL-005 不同會員的限流各自獨立
- **WHEN** 會員 A 在目前時間窗內已達到請求次數上限，會員 B 對同一端點送出請求
- **THEN** 會員 B 的請求不受會員 A 的用量影響，依正常流程處理

#### Scenario: RL-006 時間窗重置後恢復可請求
- **WHEN** 已登入會員在前一個時間窗內已達到請求次數上限，新的時間窗開始後再次送出請求
- **THEN** 系統視為新時間窗的第一次請求，正常處理

### Requirement: 限流拒絕回應格式統一為 ProblemDetails，並附帶 Retry-After
系統 SHALL 將請求頻率限制拒絕的回應，統一輸出為與既有全域例外處理（`GlobalExceptionHandler`）一致的 `ProblemDetails` 格式，包含 `Status`（429）、`Title` 與 `traceId` extension，維持全站錯誤回應格式一致，不因限流機制屬於 middleware 層而使用不同的錯誤格式。回應 SHALL 額外附帶 `Retry-After` 標頭（單位秒），標示距離該分區鍵下次可再次請求（視窗重置）的預估秒數，供前端提示更精確的等待時間。

#### Scenario: RL-007 限流拒絕回應格式
- **WHEN** 系統因超過請求頻率限制而拒絕一次請求
- **THEN** 回應 Body SHALL 為 `ProblemDetails` 格式的 JSON，`Status` 為 429，且包含 `traceId`；回應 Header SHALL 包含 `Retry-After`

### Requirement: 限流設定值須為正數，缺漏時採用明確預設值
系統 SHALL 驗證 `RateLimitingOptions` 的 `PermitLimit` 為正整數（`> 0`）、`Window` 為正時間長度（`> TimeSpan.Zero`）；設定缺漏時採用明確預設值（`PermitLimit = 20`、`Window = 1 分鐘`），不因缺漏而導致限流機制無法啟用；若設定值存在但為 0 或負數，視為明顯誤設定，MUST 於設定驗證階段擋下，不得以無效設定值靜默套用（例如變成「形同無限流」或「所有請求皆被拒絕」的非預期行為）。

#### Scenario: RL-008 設定缺漏時採用預設值
- **WHEN** `appsettings` 未提供 `RateLimitingOptions` 的 `PermitLimit`／`Window` 設定
- **THEN** 系統採用明確定義的預設值正常運作，不視為錯誤

#### Scenario: RL-009 設定值為 0 或負數時擋下
- **WHEN** `PermitLimit` 被設定為 0 或負數，或 `Window` 被設定為 0 或負的時間長度
- **THEN** 系統 MUST 在設定驗證階段擋下此設定，不得以此設定值繼續運作

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

### Requirement: 登入端點限流設定值須為正數，缺漏時採用明確預設值
系統 SHALL 驗證登入端點限流設定的次數上限（`PermitLimit`）為正整數（`> 0`）、時間窗長度（`WindowSeconds`）為正整數秒數（`> 0`）；設定缺漏時採用明確預設值（次數上限 `5`、時間窗 `60` 秒），不因缺漏而導致限流機制無法啟用；若設定值存在但為 0 或負數，視為明顯誤設定，MUST 於設定驗證階段擋下，不得以無效設定值靜默套用。

#### Scenario: LRL-007 設定缺漏時採用預設值
- **WHEN** `appsettings` 未提供登入端點限流的次數上限／時間窗設定
- **THEN** 系統採用明確定義的預設值正常運作，不視為錯誤

#### Scenario: LRL-008 設定值為 0 或負數時擋下
- **WHEN** 登入端點限流的次數上限被設定為 0 或負數，或時間窗被設定為 0 或負的時間長度
- **THEN** 系統 MUST 在設定驗證階段擋下此設定，不得以此設定值繼續運作

### Requirement: 建立座位圖端點的請求頻率限制
系統 SHALL 對建立座位圖端點（`POST /api/admin/venues/{venueId}/seat-maps`）套用獨立命名的 Fixed Window 請求頻率限制，以呼叫端 Access Token 中的 `OrganizerId` 分區：同一 Organizer 的所有成員共用一個額度，不同 Organizer 各自計數，且不與其他限流 policy 共用計數。不排隊（QueueLimit 為 0），超過額度的請求 MUST 立即回傳 429，回應格式與既有限流拒絕相同（`ProblemDetails`，含 `status` 429、`title`、`traceId`，並附 `Retry-After` 標頭），且 MUST NOT 讀取本文、不建立座位圖。限流在身分驗證與授權之後執行，未登入或未切換至 Approved Organizer 的請求依既有規則回傳 401／403，不消耗額度。

#### Scenario: SRL-001 超過額度時回 429 且不建立座位圖
- **WHEN** 同一 Organizer 在同一時間窗內送出超過額度的建立座位圖請求
- **THEN** 超過額度的請求回傳 429，本文為含 `status` 429、`title`、`traceId` 的 `ProblemDetails`，並附 `Retry-After` 標頭；該請求不建立座位圖。額度用完後即使送出 JSON 語法錯誤的本文，仍回傳 429 而不是模型繫結的 400，證明限流早於本文讀取

#### Scenario: SRL-002 不同 Organizer 各自計數
- **WHEN** Organizer A 已用完額度後，Organizer B 的成員在同一時間窗內建立座位圖
- **THEN** Organizer B 的請求成功建立座位圖，不受 A 的額度影響

#### Scenario: SRL-005 同一 Organizer 的不同成員共用額度
- **WHEN** 同一 Organizer 的成員甲在同一時間窗內用完額度後，同一 Organizer 的成員乙建立座位圖
- **THEN** 成員乙的請求回傳 429，不建立座位圖

### Requirement: 建立座位圖限流設定值須為正數，缺漏時採用預設值
系統 SHALL 以 `SeatMapRateLimiting:PermitLimit` 與 `SeatMapRateLimiting:WindowSeconds` 設定建立座位圖的限流額度，缺漏時採用預設值 10 次／60 秒；設定為 0 或負數時 MUST 在應用程式啟動時擋下。

#### Scenario: SRL-003 設定缺漏時採用預設值
- **WHEN** 設定未提供 `SeatMapRateLimiting` 區段
- **THEN** 系統以 PermitLimit 10、WindowSeconds 60 運作，不視為錯誤

#### Scenario: SRL-004 設定值為 0 或負數時擋下
- **WHEN** `SeatMapRateLimiting:PermitLimit` 或 `SeatMapRateLimiting:WindowSeconds` 被設為 0 或負數
- **THEN** 應用程式啟動失敗，不以此設定值運作

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
