## ADDED Requirements

### Requirement: 系統提供圖形驗證碼產生端點
系統 SHALL 提供不需登入即可呼叫的端點 `GET /api/captcha`，每次呼叫產生一組獨立的驗證碼：4 碼英數字內容（排除 `0`/`O`、`1`/`I`/`l` 等易混淆字元，且答案字元 MUST 使用密碼學安全亂數產生，不得使用 `System.Random`／`Random.Shared`）、對應圖片（PNG，以 SixLabors.ImageSharp 純受管理程式碼繪製，不依賴 `System.Drawing`），以及代表此次驗證碼的隨機 token（GUID）。回應 SHALL 包含 token 與圖片內容（base64 編碼的 PNG），不回傳驗證碼答案明文。

#### Scenario: CAPTCHA-GEN-001 成功產生驗證碼
- **WHEN** 任何呼叫者（不論是否登入）呼叫 `GET /api/captcha`
- **THEN** 系統回傳一組新的 token 與對應的 base64 PNG 圖片，不回傳答案明文

#### Scenario: CAPTCHA-GEN-002 每次呼叫產生彼此獨立的驗證碼
- **WHEN** 同一呼叫者連續兩次呼叫 `GET /api/captcha`
- **THEN** 系統回傳兩組不同的 token，先前產生的 token 不因後一次呼叫而失效（各自依自身 TTL 獨立計算）

### Requirement: 驗證碼答案以雜湊暫存於 Redis，具備明確時效
系統 SHALL 將產生的驗證碼答案正規化為大寫後以 SHA-256 雜湊，寫入 Redis（key 為 `captcha:{token}`），TTL 為 120 秒，不儲存明碼答案。

#### Scenario: CAPTCHA-STORE-001 驗證碼寫入時帶有 TTL
- **WHEN** 系統產生新的驗證碼並寫入 Redis
- **THEN** 寫入的 key 帶有 120 秒的 TTL，且儲存的值為答案正規化後的 SHA-256 雜湊，不是明碼

#### Scenario: CAPTCHA-STORE-002 TTL 到期後驗證碼自動失效
- **WHEN** 驗證碼產生後超過 120 秒未被驗證
- **THEN** 對應的 Redis key 已因 TTL 到期而不存在，後續以該 token 驗證 MUST 視為失敗

### Requirement: 驗證碼時效設定值須為正數，缺漏時採用明確預設值
系統 SHALL 驗證 `CaptchaOptions` 的時效秒數（`TtlSeconds`）為正整數（`> 0`），比照既有 `CaptchaRateLimitingOptions`／`LoginRateLimitingOptions` 的既定驗證模式；設定缺漏時採用明確預設值（`120` 秒，與本能力其餘 Requirement 描述的 TTL 一致），不因缺漏而導致驗證碼功能無法啟用；若設定值存在但為 0 或負數，視為明顯誤設定，MUST 於設定驗證階段擋下，不得以無效設定值靜默套用（例如 `TtlSeconds = 0` 會讓 Redis 寫入立即過期，等同驗證碼從未存在，卻不會有任何啟動期的錯誤徵兆）。

#### Scenario: CAPTCHA-STORE-003 TtlSeconds 設定缺漏時採用預設值
- **WHEN** `appsettings` 未提供 `Captcha` 的 `TtlSeconds` 設定
- **THEN** 系統採用明確定義的預設值（`TtlSeconds = 120`）正常運作，不視為錯誤

#### Scenario: CAPTCHA-STORE-004 TtlSeconds 設定值為 0 或負數時擋下
- **WHEN** `Captcha:TtlSeconds` 被設定為 0 或負數
- **THEN** 系統 MUST 在設定驗證階段（Host 建立階段）擋下此設定，拋出 `OptionsValidationException`，不得以此設定值繼續啟動或運作

### Requirement: 驗證碼核對為一次性，成功或失敗皆立即失效，讀取與刪除須為單一原子操作
系統 SHALL 提供供其他能力呼叫的驗證邏輯：以單一原子 Redis 操作（`GETDEL`）讀取並刪除 `captcha:{token}`，取得的值與使用者填寫的驗證碼文字正規化為大寫後計算的 SHA-256 雜湊比對。**讀取與刪除 MUST 在同一次 Redis 命令內完成，不得先讀取、再另外呼叫刪除**——分成兩步會在兩個並發驗證請求之間留下時間視窗，讓同一 token 被兩者都讀到仍然有效的值而雙雙驗證成功，違反一次性保證。Token 不存在（未產生過、已過期，或已被使用過而遭原子刪除）時，MUST 視為驗證失敗。

#### Scenario: CAPTCHA-VERIFY-001 驗證碼正確時驗證成功
- **WHEN** 呼叫端提供的 token 存在於 Redis，且填寫的文字正規化後的雜湊與儲存值相符
- **THEN** 系統回報驗證成功；該 token 已透過同一次 `GETDEL` 操作被原子性地讀取並刪除

#### Scenario: CAPTCHA-VERIFY-002 驗證碼錯誤時驗證失敗且 token 失效
- **WHEN** 呼叫端提供的 token 存在於 Redis，但填寫的文字正規化後的雜湊與儲存值不符
- **THEN** 系統回報驗證失敗；該 token 已透過同一次 `GETDEL` 操作被原子性地讀取並刪除，之後不可再用於任何驗證嘗試

#### Scenario: CAPTCHA-VERIFY-006 同一 token 被並發提交驗證時，僅一方可能成功
- **WHEN** 兩個請求以完全相同的 token 與相同的正確答案，幾乎同時提交驗證
- **THEN** 由於讀取與刪除是單一原子 `GETDEL` 操作，Redis MUST 保證兩次操作依序執行、不重疊——僅先執行的一方能讀到有效值並驗證成功，後執行的一方讀到的值已被前者取走而視為 token 不存在，驗證失敗，不得兩者皆驗證成功

#### Scenario: CAPTCHA-VERIFY-003 使用不存在或已過期的 token 驗證
- **WHEN** 呼叫端提供的 token 在 Redis 中不存在（從未產生、已過期，或先前已被使用過一次）
- **THEN** 系統回報驗證失敗，不因 token 不存在而拋出未預期例外

#### Scenario: CAPTCHA-VERIFY-004 同一 token 不可重複驗證
- **WHEN** 呼叫端先前已對某個 token 完成一次驗證（不論成功或失敗），再次以同一 token 提交驗證
- **THEN** 系統回報驗證失敗（等同 token 不存在的情形），不得因第一次驗證成功而讓第二次也視為成功

#### Scenario: CAPTCHA-VERIFY-005 驗證時忽略大小寫
- **WHEN** 產生的驗證碼答案為大寫英數字組合，呼叫端填寫時使用小寫字母
- **THEN** 系統將呼叫端輸入正規化為大寫後比對，視為與正確答案相符，驗證成功

### Requirement: Redis 無法連線時，驗證碼產生與核對皆 fail-closed
系統 SHALL 在 `GET /api/captcha` 產生驗證碼或驗證碼核對邏輯偵測到 Redis 無法連線時，回傳明確的技術性錯誤，MUST NOT 視為驗證通過、MUST NOT 略過驗證直接放行——驗證碼是防機器人的唯一防線，Redis 故障時放行等同該次故障期間完全撤除防護，性質與 `query-caching`／`purchase-queue-leader-election` 既有 Redis 用途（純效能或並發優化）的 fail-open 慣例不同，不得沿用。

#### Scenario: CAPTCHA-FAIL-001 Redis 無法連線時無法產生驗證碼
- **WHEN** 呼叫 `GET /api/captcha`，此時 Redis 無法連線
- **THEN** 系統回傳明確的技術性錯誤，不回傳任何 token 或圖片，不得以未寫入 TTL 或降級方式繼續運作

#### Scenario: CAPTCHA-FAIL-002 Redis 無法連線時驗證邏輯回報明確的技術性錯誤，不包裝成一般驗證失敗
- **WHEN** 呼叫驗證碼核對邏輯，此時 Redis 無法連線
- **THEN** 系統 MUST 讓技術性例外原樣往外拋，回傳明確的技術性錯誤（如既有全域 `IExceptionHandler` 轉換的 `5xx`），MUST NOT 包裝成與「驗證碼答案錯誤」外觀相同的一般業務驗證失敗、MUST NOT 因無法讀取 Redis 而預設視為驗證通過——呼叫端須能區分「答案錯誤」與「系統故障」兩種不同情況，不得混為一談

### Requirement: Redis 連線／命令逾時上限須明確設定，逾時比照無法連線處理，同樣 fail-closed
系統 SHALL 對 Redis 連線與命令逾時設定明確的上限（不得依賴 StackExchange.Redis 函式庫版本的預設值），逾時本身視為與「Redis 無法連線」同一類故障，同樣套用 fail-closed：MUST 在此上限內拋出技術性例外、回報明確的技術性錯誤，MUST NOT 因等待逾時而讓請求無限期掛起，MUST NOT 因逾時而預設視為驗證通過。

#### Scenario: CAPTCHA-FAIL-003 Redis 命令逾時（連線存在但無回應）比照無法連線處理
- **WHEN** 呼叫 `GET /api/captcha` 或驗證碼核對邏輯，此時 Redis 連線已建立但命令逾時未回應（非 TCP 連線直接被拒絕）
- **THEN** 系統 MUST 在明確設定的逾時上限內拋出技術性例外（`RedisTimeoutException`），後續行為與 CAPTCHA-FAIL-001／002 相同——回傳明確的技術性錯誤、不視為驗證通過，此上限值為專案顯式設定的結果，不依賴函式庫預設值

### Requirement: 呼叫端提供的 Token 與驗證碼文字須符合長度上限，比對前忽略前後空白
呼叫任何受驗證碼保護的端點（登入、註冊、加入排隊）時，`CaptchaToken` 與 `CaptchaAnswer` 兩欄位的驗證順序 MUST 固定為：**先 trim 前後空白 → 再檢查 trim 後是否為空白 → 再檢查 trim 後的長度是否超過上限**，長度上限的判斷基準是 trim 之後的字串，不是原始輸入字串。長度上限：`CaptchaToken` 64 字元（實際產生值為 GUID 字串表示法，固定 36 字元）、`CaptchaAnswer` 16 字元（實際答案固定 4 碼）。trim 後為空白或超過長度上限時 MUST 視為同一類 `Validation` 錯誤，不呼叫驗證邏輯。`CaptchaAnswer` 比對前 MUST trim 前後空白，避免使用者輸入時偶發的空白造成非預期的驗證失敗；此 trim 與長度驗證使用的 trim 是同一份正規化結果，不是兩套各自獨立、可能得出不同結果的邏輯。

#### Scenario: CAPTCHA-INPUT-001 CaptchaToken 超過長度上限
- **WHEN** 呼叫端提供的 `CaptchaToken`（trim 前後空白後）長度超過 64 字元
- **THEN** 系統回傳 400 `Validation` 錯誤，不呼叫驗證邏輯

#### Scenario: CAPTCHA-INPUT-002 CaptchaAnswer 超過長度上限
- **WHEN** 呼叫端提供的 `CaptchaAnswer`（trim 前後空白後）長度超過 16 字元
- **THEN** 系統回傳 400 `Validation` 錯誤，不呼叫驗證邏輯

#### Scenario: CAPTCHA-INPUT-003 CaptchaAnswer 前後空白不影響驗證
- **WHEN** 使用者輸入的驗證碼文字與正確答案相符，但前後夾帶空白字元
- **THEN** 系統 trim 前後空白後再正規化比對，視為與正確答案相符，驗證成功

#### Scenario: CAPTCHA-INPUT-004 原始輸入含大量前後空白但 trim 後仍在長度上限內
- **WHEN** 呼叫端提供的 `CaptchaAnswer` 原始字串長度超過 16 字元，但前後夾帶的空白字元 trim 後剩餘內容不超過 16 字元（例如 13 個空白字元加上 4 碼答案，原始長度 17 字元）
- **THEN** 系統 MUST 依 trim 後的長度判斷，不得因原始（未 trim）字串長度超過上限而回傳 400，長度驗證通過後續走一般驗證流程（答案正確則成功、錯誤則失敗，與長度限制無關）

### Requirement: 驗證碼操作尊重請求取消
系統 SHALL 將呼叫端提供的 `CancellationToken` 傳遞至驗證碼產生與核對流程。若呼叫已被取消，系統 MUST 在執行 Redis 操作前停止處理並拋出 `OperationCanceledException`，不得吞掉例外、不得回報驗證通過或驗證失敗等任何看似正常完成的結果。呼叫 `ICaptchaService` 的 Handler（`LoginHandler`／`RegisterMemberHandler`／`JoinPurchaseQueueHandler`／`GetCaptchaHandler`）MUST 把自己收到的 `CancellationToken` 原樣轉傳給 `ICaptchaService`，不得改用 `CancellationToken.None`。

#### Scenario: CAPTCHA-CANCEL-001 GenerateAsync 已取消
- **WHEN** 呼叫 `ICaptchaService.GenerateAsync` 時傳入的 `CancellationToken` 已處於取消狀態
- **THEN** 系統 MUST 在執行 Redis 寫入前拋出 `OperationCanceledException`，不回傳任何 token 或圖片

#### Scenario: CAPTCHA-CANCEL-004 GenerateAsync 在圖片產生完成後、Redis 寫入前才被取消
- **WHEN** 呼叫 `ICaptchaService.GenerateAsync` 時傳入的 `CancellationToken` 在呼叫當下尚未取消，但在圖片產生完成之後、實際呼叫 Redis 寫入之前才轉為已取消狀態
- **THEN** 系統 MUST 在執行 Redis 寫入前偵測到取消並拋出 `OperationCanceledException`，MUST NOT 已經呼叫 Redis 寫入命令——「MUST 在執行 Redis 操作前停止處理」這個保證不只適用於「呼叫當下已取消」，也適用於處理過程中才發生取消的情況

#### Scenario: CAPTCHA-CANCEL-002 VerifyAsync 已取消
- **WHEN** 呼叫 `ICaptchaService.VerifyAsync` 時傳入的 `CancellationToken` 已處於取消狀態
- **THEN** 系統 MUST 在執行 Redis 讀取／刪除前拋出 `OperationCanceledException`，不回報驗證成功或失敗

#### Scenario: CAPTCHA-CANCEL-003 Handler 將收到的 CancellationToken 轉傳給 ICaptchaService
- **WHEN** 呼叫端呼叫 `LoginHandler`／`RegisterMemberHandler`／`JoinPurchaseQueueHandler`／`GetCaptchaHandler` 時傳入已取消的 `CancellationToken`
- **THEN** 該 Handler MUST 把此已取消的 `CancellationToken` 原樣轉傳給 `ICaptchaService.VerifyAsync`／`GenerateAsync`，不得改用 `CancellationToken.None`——可觀察行為為該次呼叫拋出 `OperationCanceledException`，而非略過驗證碼檢查繼續執行後續業務邏輯

### Requirement: 驗證碼產生端點套用專屬的請求頻率限制 policy
`GET /api/captcha` SHALL 套用一個新增的、獨立命名的 `captcha` rate limiting policy（以來源 IP 為分區鍵），限制同一來源短時間內可產生的驗證碼數量，避免呼叫端藉由無限次換取新 token 來規避「驗證失敗需取新圖」的一次性設計。**`captcha` policy MUST 是獨立的具名 policy，MUST NOT 沿用既有 `login` policy 的名稱**——ASP.NET Core `RateLimiter` middleware 的具名 policy 對應同一個 `PartitionedRateLimiter` 實例，若 `GET /api/captcha` 與 `POST /api/auth/login` 共用同一個 policy 名稱，會讓兩個語意完全不同的端點共用同一組計數（同一 IP 呼叫驗證碼端點會消耗登入端點的額度，反之亦然），違反本專案既有 `api-rate-limiting` 能力已明確建立的原則（`api-rate-limiting` spec「登入端點的請求頻率限制」Scenario LRL-006：「不同 policy 不共用計數」，`place-order`／`confirm-order` 即使共用同一個 `RateLimitingOptions` 設定類別，也刻意各自獨立計數）。分區鍵的**推導邏輯**（來源 IP、無法取得時退回 `"unknown"`）MAY 重用既有 `LoginRateLimiterPartitioning.GetPartitionKey` 這個 static method，這只是共用「怎麼從 `HttpContext` 算出 IP 分區鍵」的程式碼，不代表共用 `login` policy 本身或其計數器。額度與時間窗設定值由新增的 `CaptchaRateLimitingOptions` 獨立管理，不與 `RateLimitingOptions`／`LoginRateLimitingOptions` 共用同一組數值。超過限制時的回應行為 SHALL 沿用 `api-rate-limiting` 既有的 `429`、`Retry-After`、`ProblemDetails` 格式與共用的 `OnRejected` callback，不另建第二套限流「機制」（沿用既有的中介軟體與回應格式基礎設施），但 policy 本身與其設定值是本次改動新增、獨立的。

#### Scenario: CAPTCHA-RATE-001 短時間內大量產生驗證碼被限流
- **WHEN** 同一來源 IP 在短時間內呼叫 `GET /api/captcha` 的次數超過 `captcha` policy 設定的限流門檻
- **THEN** 系統回傳 `429 Too Many Requests`（含 `Retry-After`），不再產生新的驗證碼

#### Scenario: CAPTCHA-RATE-004 驗證碼限流與登入限流互不影響
- **WHEN** 同一來源 IP 已達到 `captcha` policy 的限流上限，該 IP 接著呼叫 `POST /api/auth/login`
- **THEN** `POST /api/auth/login` 的請求不受 `GET /api/captcha` 用量已達上限影響，依 `login` policy 自身額度正常處理；反之，同一 IP 已達到 `login` policy 上限時，`GET /api/captcha` 依 `captcha` policy 自身額度正常處理——兩個 policy 計數完全獨立，即使分區鍵推導邏輯共用同一段程式碼

### Requirement: 驗證碼限流設定值須為正數，缺漏時採用明確預設值
系統 SHALL 驗證 `CaptchaRateLimitingOptions` 的次數上限（`PermitLimit`）為正整數（`> 0`）、時間窗長度（`WindowSeconds`）為正整數秒數（`> 0`），比照既有 `RateLimitingOptions`／`LoginRateLimitingOptions` 的既定驗證模式；設定缺漏時採用明確預設值（次數上限 `10`、時間窗 `60` 秒），不因缺漏而導致限流機制無法啟用；若設定值存在但為 0 或負數，視為明顯誤設定，MUST 於設定驗證階段擋下，不得以無效設定值靜默套用。

#### Scenario: CAPTCHA-RATE-002 設定缺漏時採用預設值
- **WHEN** `appsettings` 未提供 `CaptchaRateLimiting` 的次數上限／時間窗設定
- **THEN** 系統採用明確定義的預設值（`PermitLimit = 10`、`WindowSeconds = 60`）正常運作，不視為錯誤

#### Scenario: CAPTCHA-RATE-003 設定值為 0 或負數時擋下
- **WHEN** `CaptchaRateLimiting` 的次數上限被設定為 0 或負數，或時間窗被設定為 0 或負的時間長度
- **THEN** 系統 MUST 在設定驗證階段（Host 建立階段）擋下此設定，拋出 `OptionsValidationException`，不得以此設定值繼續啟動或運作
