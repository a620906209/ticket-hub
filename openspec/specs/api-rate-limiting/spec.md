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

**部署前提**：本機制正確性依賴 `RemoteIpAddress` 反映真實用戶端位址，僅支援應用程式直接接收用戶端連線的部署拓樸；部署在反向代理／CDN 之後時，須先設定可信任的 `ForwardedHeadersOptions`，否則本機制對真實攻擊者實質失效（見 `login-rate-limiting` change 的 design.md「Deployment Prerequisite」）。

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

### Requirement: 登入端點限流設定值須為正數，缺漏時採用明確預設值
系統 SHALL 驗證登入端點限流設定的次數上限（`PermitLimit`）為正整數（`> 0`）、時間窗長度（`WindowSeconds`）為正整數秒數（`> 0`）；設定缺漏時採用明確預設值（次數上限 `5`、時間窗 `60` 秒），不因缺漏而導致限流機制無法啟用；若設定值存在但為 0 或負數，視為明顯誤設定，MUST 於設定驗證階段擋下，不得以無效設定值靜默套用。

#### Scenario: LRL-007 設定缺漏時採用預設值
- **WHEN** `appsettings` 未提供登入端點限流的次數上限／時間窗設定
- **THEN** 系統採用明確定義的預設值正常運作，不視為錯誤

#### Scenario: LRL-008 設定值為 0 或負數時擋下
- **WHEN** 登入端點限流的次數上限被設定為 0 或負數，或時間窗被設定為 0 或負的時間長度
- **THEN** 系統 MUST 在設定驗證階段擋下此設定，不得以此設定值繼續運作
