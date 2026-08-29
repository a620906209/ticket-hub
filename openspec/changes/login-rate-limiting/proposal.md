## Why

`POST /api/auth/login` 目前沒有任何請求頻率限制，攻擊者可以對同一帳號或大量帳號進行暴力破解（brute-force）或撞庫（credential stuffing）攻擊而不受阻擋。`docs/project-scope.md` Phase 2 Should 已明確列出「登入 Rate limiting（防暴力破解）」為待補項目；`rate-limiting-queue` 已建立好 `Microsoft.AspNetCore.RateLimiting` 的分區限流基礎設施與 `api-rate-limiting` 能力（統一的 429 `ProblemDetails` + `Retry-After` 回應格式），現在是低成本延伸這套機制、補上登入端點防護缺口的時機。

## What Changes

- 對 `POST /api/auth/login` 套用獨立命名的 Fixed Window 請求頻率限制 policy，超過限制時回傳 `429 Too Many Requests`，不執行任何登入邏輯（不查詢會員、不驗證密碼、不核發 Token）
- **分區鍵策略需要新設計，不能沿用既有下單端點「以已登入會員 Id 分區」的做法**——登入端點呼叫當下使用者尚未通過驗證，沒有會員 Id 可用；評估 IP 位址、請求中的 Email、兩者並行三種方案後，design.md 決策 1 決定本次僅採用來源 IP 分區（技術限制：ASP.NET Core rate limiter partitioner 執行時機早於 MVC model binding，以 Email 分區需要額外的 sync-over-async body 讀取工程；且單獨以 Email 分區本身防護力有限，攻擊者可送出隨機 Email 繞過）；以 Email 分區列入 design.md Open Questions，留待未來評估是否疊加
- 設定值（次數上限、時間窗）需要重新評估是否可沿用既有 `RateLimitingOptions`，或因登入端點的正常呼叫頻率遠低於下單端點而需要獨立的較嚴格數值——design.md 決定
- 前端登入頁（`LoginPage.vue`）比照既有下單頁面對 429 的既定處理模式，顯示友善提示訊息（而非直接顯示後端 `ProblemDetails.title` 原始字串）

## Capabilities

### New Capabilities
（無）

### Modified Capabilities
- `api-rate-limiting`: 新增一條 Requirement，涵蓋 `POST /api/auth/login` 的請求頻率限制（分區鍵策略、拒絕行為、設定驗證），比照既有下單端點限流的既定慣例（`ProblemDetails` 429 格式、`Retry-After`、Fixed Window、設定值驗證）
- `buyer-web-ui`: 「買家可透過介面註冊與登入」Requirement 新增一個 Scenario——登入因請求頻率限制被拒絕（429）時，系統顯示友善提示訊息（例如「登入嘗試過於頻繁，請稍後再試」），不顯示後端原始 `title` 字串

## Impact

- **WebApi**：`Program.cs` 新增一個獨立命名的 rate limiter policy（比照 `place-order`／`confirm-order` 的既定寫法，但分區鍵函式需要不同的策略，見上）；`AuthController.Login` action 套用該 policy
- **Application**：視 design.md 決定的分區鍵與設定策略，可能沿用既有 `RateLimitingOptions`，或新增獨立的設定類別（例如 `LoginRateLimitingOptions`）
- **不影響** `LoginHandler` 本身的業務邏輯（帳密驗證、Token 核發），也不影響其他 Auth 端點（`register`／`refresh`／`logout`／`password-reset/*`）——這些端點目前皆不在本次範疇內，若未來評估仍有暴力破解/濫用風險，留待後續變更處理
- **前端**：`web/src/pages/buyer/LoginPage.vue` 新增 429 錯誤的友善訊息處理，比照 `EventDetailPage.vue` 對下單 429 的既有處理模式
- **不影響**資料庫結構（沿用 middleware 層的記憶體限流機制，不需要 migration）
