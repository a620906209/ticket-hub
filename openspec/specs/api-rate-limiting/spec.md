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
