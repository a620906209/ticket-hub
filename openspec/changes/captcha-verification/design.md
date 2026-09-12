## Context

本專案既有防搶票機制分兩層：`api-rate-limiting`（分區限流，擋「量」）與 `purchase-queue`（排隊機制，擋「搶購瞬間的公平性」）。兩者皆無法區分請求來源是真人還是自動化腳本——只要腳本控制請求頻率在限流門檻內，仍可大量佔用註冊、登入、排隊名額。本次改動在這些端點前補上驗證碼，作為「真人驗證」層，與既有機制並存、互不取代。

既有 Redis 使用場景（`purchase-queue-leader-election` 的分散式鎖、`query-caching` 的查詢快取）皆採 fail-open：Redis 故障時照常運作、只犧牲效能或並發正確性優化，因為這兩者都不是安全邊界。驗證碼是安全邊界（防機器人），若沿用 fail-open，Redis 故障時等同全面撤除防護，性質完全不同，本設計必須另作決策（見決策 5）。

既有 `ticket-issuance` 的 QR Code 產生已建立「Linux 容器內不可用 `System.Drawing`」的慣例（`TicketQrCodeGenerator.cs` 明確採用純受管理程式碼的 `QRCoder.PngByteQRCode`），本次驗證碼圖片產生延續同一慣例。

## Goals / Non-Goals

**Goals:**
- 為註冊、登入、加入排隊三個端點補上驗證碼檢查，防止自動化腳本繞過既有限流機制大量操作
- 驗證碼產生、暫存、核對邏輯獨立為 `captcha-verification` 能力，供其他能力呼叫，不與任何單一呼叫端耦合
- 驗證碼具備明確時效與一次性保證，避免重放攻擊

**Non-Goals:**
- 不整合第三方驗證碼服務（reCAPTCHA、hCaptcha）——本專案既有整合決策一貫排除依賴外部第三方服務（Mock Payment Gateway、本地 HMAC 簽章而非第三方簽章服務），驗證碼延續同一決策
- 不處理進階機器人辨識（行為分析、裝置指紋）——本次僅止於「輸入圖形驗證碼文字」這種基礎驗證
- 不涉及查詢排隊狀態端點（輪詢用，非建立操作，加驗證碼會破壞既有輪詢體驗）
- 不涉及下單（`PlaceOrder`）端點——依規劃階段決策，驗證碼只套用在註冊/登入/加入排隊三處，避免與排隊機制的验证產生重複驗證的使用者體驗問題
- 不引入斷路器（circuit breaker）等進階 Redis 韌性機制——決策 11 已將 Redis 逾時上限明確設為 2 秒並搭配既有 rate limiting 間接壓低高併發下的等待請求量，本專案定位為技術展示、非高流量正式服務，更進階的韌性機制（例如偵測連續逾時後讓後續請求快速失敗）留給未來若有實際需求時再評估

## Decisions

### 決策 1：驗證碼圖片以 SixLabors.ImageSharp（+ ImageSharp.Drawing、SixLabors.Fonts）產生，版本鎖定於 Apache 2.0 系列；文字繪製字型以 embedded resource 內嵌，不依賴容器 OS 字型

延續 `TicketQrCodeGenerator` 已建立的慣例：容器化部署於 Linux，`System.Drawing.Common` 在 .NET 6+ 於非 Windows 平台會拋出 `PlatformNotSupportedException`，MUST NOT 使用。`SixLabors.ImageSharp.Drawing` 是純受管理程式碼實作，可繪製文字與簡單雜訊線干擾，跨平台可用。

**版本與授權鎖定**：`Directory.Packages.props` 既有 `SixLabors.ImageSharp` 鎖定於 `2.1.11`（Apache 2.0，免授權金鑰），但目前僅供 `Infrastructure.Tests` 解碼 QRCode PNG 這種測試用途引用。本次改動把 ImageSharp 系列首次帶入**正式 Runtime 程式碼**（`ProjectC.Infrastructure` 的驗證碼圖片產生），授權風險層級與既有測試用途不同，MUST 明確決策版本：`SixLabors.ImageSharp.Drawing` 鎖定 `1.0.0`（依賴 `SixLabors.ImageSharp >= 2.1.5`，與既有 `2.1.11` 相容）、`SixLabors.Fonts` 鎖定 `1.0.0`，兩者皆為 Apache 2.0、不觸發 Split License 授權金鑰要求（該授權模式自 `SixLabors.ImageSharp.Drawing`／`SixLabors.ImageSharp` 3.x 系列起才引入）。三個套件版本 MUST 新增至 `Directory.Packages.props` 第一個（正式）`ItemGroup`（而非既有測試專用的第二個 `ItemGroup`），並比照既有 `SixLabors.ImageSharp` 條目加上授權說明註解。MUST NOT 在未重新評估授權情境（含是否需要註冊授權金鑰、是否符合免費條件）前升級至 3.x 系列。**此版本與授權宣稱已於 design-hardener 審查階段透過官方管道查證，但本機 repo 無法核對第三方套件原始碼本身，實作階段 MUST 以 `docker compose exec api dotnet restore`／`dotnet build` 的實際還原結果與官方 NuGet 頁面複核套件相依限制是否與此處描述一致，若不一致以實際結果為準並回頭更新本節。**

**容器內文字繪製字型來源**：本專案唯一的 `Dockerfile`（`mcr.microsoft.com/dotnet/sdk:10.0`）與 .NET 官方 base image 慣例一致，不內建任何系統字型；`SixLabors.Fonts` 的 `SystemFonts` 集合依賴 OS 已安裝字型，在無字型容器內呼叫會拋出 `FontException: No fonts found installed on the machine`。既有 `TicketQrCodeGenerator` 使用 `QRCoder.PngByteQRCode` 純粹是像素運算、不涉及文字繪製，不能類比涵蓋「繪製 4 碼驗證碼文字需要字型」這個全新前提。因此 MUST 將授權相容的開源字型檔案以 embedded resource 形式內嵌於 `ProjectC.Infrastructure`，執行時透過自訂 `FontCollection.Add(stream)` 載入取得 `FontFamily`，MUST NOT 依賴 `SystemFonts`，確保產圖行為不受容器是否安裝系統字型影響。

**選用字型與授權條款**：採用 `DejaVu Sans`（`DejaVu Fonts License`，衍生自 `Bitstream Vera License`，DejaVu 本身的修改部分為公眾領域）。此授權明確允許重製、散布、嵌入應用程式（含隨更大型的商業軟體包一併散布），唯二限制：(1) 若修改字型檔本身，重新散布時檔名 MUST NOT 含 `Bitstream`／`Vera` 字樣——本次改動僅嵌入原始字型檔、不修改，不受此限制；(2) MUST NOT 將字型檔案本身單獨拆封出來販售，僅能隨應用程式整體散布——本次為內部嵌入應用程式的一部分，非單獨販售，符合授權條件。embedded resource 內須一併附上字型的授權條款檔案（`LICENSE`／`copyright` 純文字檔，隨 DejaVu 官方發行版本一起取得），供日後稽核來源與條款。

**考慮過的替代方案**：手刻像素運算（不依賴任何繪圖套件）——複雜度過高、維護成本不成比例，排除。修改 Dockerfile 安裝 OS 字型套件（如 `fonts-dejavu-core`）並執行 `fc-cache`——可行，但額外耦合容器 image 內容、且需要在多個環境（本機開發、CI、正式部署）重複維護同一份字型安裝步驟；embedded resource 讓字型隨應用程式一起部署、不依賴執行環境設定，更可攜，優先採用。

### 決策 2：驗證碼內容為 4 碼英數字，排除易混淆字元，比對時忽略大小寫

字元集排除 `0`/`O`、`1`/`I`/`l` 等易混淆字元（避免真人使用者因字型辨識困難而誤判）。比對時將使用者輸入 trim 前後空白後正規化為大寫，與正確答案比對，不要求大小寫完全一致——這是可用性考量，不降低防機器人強度（機器人窮舉不因忽略大小寫、忽略前後空白而變得更容易，字元集大小才是決定因素）。

### 決策 3：驗證碼答案以 Redis 暫存，key 為隨機 token，值為答案的雜湊（不存明碼）

`GET /api/captcha` 產生一組隨機 token（`Guid.NewGuid()`，其底層實作為密碼學安全亂數來源——這是對 .NET 執行環境行為的既知事實，非本專案程式碼，本機 repo 無從核對，若未來 .NET 版本變更此保證，MUST 重新評估此依據）與對應圖片，將 `captcha:{token}` → 答案的 SHA-256 雜湊（正規化大寫後雜湊）寫入 Redis，TTL 為 120 秒。驗證時對使用者輸入做相同的 trim＋正規化＋雜湊，與 Redis 內容比對。不存明碼答案：比照 CLAUDE.md 機敏資訊管理原則，即使驗證碼答案本身時效極短、不是傳統意義的密碼，仍以雜湊儲存，避免任何能讀取 Redis 的人員或工具直接看到明碼答案。

**考慮過的替代方案**：答案明碼存 Redis——比對邏輯更簡單，但沒有理由讓明碼落地，即使時效短，排除。

### 決策 4：Token 為一次性，讀取與刪除 MUST 是單一原子操作（`GETDEL`），不得分兩步執行

驗證流程：以單一 Redis 命令 `GETDEL captcha:{token}`（對應 `StackExchange.Redis 3.1.31` 的 `IDatabaseAsync.StringGetDeleteAsync`，已確認此版本與既有 `redis:7-alpine`（`docker-compose.yml`）皆支援此命令，`GETDEL` 為 Redis 6.2 引入）**在同一次 Redis 呼叫內原子地讀取雜湊值並刪除該 key**，接著在應用程式端比對讀到的雜湊值。若該次 `GETDEL` 回傳空值（不存在或已過期），直接視為驗證失敗。

**為什麼不能用「先 `StringGetAsync` 讀取、比對後再 `KeyDeleteAsync`」這種兩步寫法**：兩個呼叫之間存在可觀察的時間視窗，若兩個並發請求以同一 token、同一正確答案幾乎同時呼叫驗證，都可能在對方執行刪除之前讀到同一份仍然存在的有效雜湊，導致兩者都比對成功——這會違反「同一 token MUST NOT 被用於第二次驗證嘗試」的一次性保證（CAPTCHA-VERIFY-004）。`GETDEL` 是 Redis 伺服器端保證的單一原子命令，同一 key 的讀取與刪除不可能被另一個並發命令插入中間，從根本上排除這個競態，不需要額外的分散式鎖或 Lua 腳本。

失敗後（含答案錯誤、token 不存在／已過期、或已被使用過）呼叫端 MUST 重新呼叫 `GET /api/captcha` 取得新圖片與新 token 才能重試，不可用同一 token 反覆嘗試——這是防止暴力猜測 4 碼驗證碼的關鍵設計（token 本身也綁定唯一嘗試次數為 1 次）。

**考慮過的替代方案**：先 `StringGetAsync` 再 `KeyDeleteAsync`——實作直覺、但如上述有並發競態，排除。用 Lua 腳本手刻 compare-and-delete（比照 `RedisDistributedLock` 的既有慣例）——`GETDEL` 是更簡單、伺服器原生支援的等價方案,不需要额外维护腳本字串，優先採用；若之後需要「讀取與刪除」以外更複雜的原子邏輯（例如同時遞增嘗試次數），才需要升級為 Lua 腳本。

**版本核對**：`StackExchange.Redis 3.1.31` 由 `Directory.Packages.props`（Central Package Management）集中鎖定（非僅本文件宣稱），本機 NuGet 還原快取（`~/.nuget/packages/stackexchange.redis/3.1.31`）已確認該版本組件內存在 `IDatabaseAsync.StringGetDeleteAsync`，可安全依賴此 API。

### 決策 5：Redis 無法連線時，驗證碼相關端點 fail-closed（拒絕該次操作），不沿用既有 Redis 用途的 fail-open 慣例

**這是本次設計最關鍵、也最容易照抄既有慣例出錯的決策點。** `query-caching`／`purchase-queue-leader-election` 的 fail-open 合理，是因為 Redis 只是效能或並發優化的手段，資料正確性完全由資料庫交易保證，Redis 故障最壞情況是效能降級或短暫的並發優化失效。驗證碼不同：驗證碼本身就是防護機制的全部，Redis 故障若 fail-open（略過驗證直接放行），等同該次故障期間對這三個端點的機器人防護完全失效，防護強度歸零；而 fail-closed（Redis 故障時一律拒絕，回傳明確錯誤要求稍後再試）只是讓這三個非交易關鍵路徑的端點短暫不可用。

兩害相權：機器人防護在攻擊面上"完全消失"的風險，遠高於"註冊/登入/加入排隊短暫不可用"的可用性成本——尤其後兩者本來就已经有既有 rate limiting 的 429 拒絕情境，使用者與前端已有「稍後再試」的既定處理模式可以沿用。

因此：`GET /api/captcha`（產生）與驗證步驟，在偵測到 Redis 無法連線時，MUST 回傳明確的技術性錯誤（透過既有全域 `IExceptionHandler`，不特別攔截包裝成業務錯誤），不得視為驗證通過。

**考慮過的替代方案**：fail-open（照抄既有慣例）——排除，理由如上，性質不同不能照搬。降級為記憶體內暫存（不用 Redis）——會讓多實例部署下的驗證碼狀態無法跨實例共享，且需要另外處理清理過期項目的背景邏輯，增加不成比例的複雜度，排除；本專案既有 Redis 基礎設施（`purchase-queue-leader-election` 已建立），無須新增技術依賴。

### 決策 6：`GET /api/captcha` 新增獨立命名的 `captcha` policy，不沿用既有 `login` policy

若不限制驗證碼圖片本身的產生頻率，機器人可無限制地重複呼叫 `GET /api/captcha` 換取新 token，等同繞過決策 4「一次性、失敗需取新圖」的防暴力猜測設計（用時間換取無限次嘗試機會）。此端點以來源 IP 為分區鍵套用限流。

**核對既有 `Program.cs` 後發現的問題**：目前已註冊的 rate limiting policy 只有 `place-order`／`confirm-order`（會員 Id 分區）與 `login`（IP 分區）三個，沒有任何一個可以直接套用在匿名的 `GET /api/captcha` 上。若照字面「沿用既有 `api-rate-limiting` 分區限流機制」直接套用 `[EnableRateLimiting("login")]`，會讓 `GET /api/captcha` 與 `POST /api/auth/login` **共用同一個具名 policy 的計數器**（ASP.NET Core `RateLimiter` middleware 對同一個 policy 名稱只建立一個 `PartitionedRateLimiter` 實例）——同一 IP 呼叫驗證碼端點會消耗登入端點的額度，反之亦然，這違反本專案既有 `api-rate-limiting` 能力自己建立的原則（`api-rate-limiting` spec LRL-006：「不同 policy 不共用計數」，`place-order`／`confirm-order` 即使共用同一個 `RateLimitingOptions` 設定類別也刻意各自獨立計數）。

**決策**：新增一個獨立命名的 `captcha` policy 與獨立的 `CaptchaRateLimitingOptions` 設定類別（`PermitLimit` 預設 `10`、`WindowSeconds` 預設 `60`，比照 `LoginRateLimitingOptions` 的既定驗證模式：`[Range(1, int.MaxValue)]`、沒有 `ValidateOnStart()`、靠 `Program.cs` 啟動時強制解析一次 `IOptions<T>.Value` 觸發 fail-fast）。分區鍵**推導邏輯**重用既有 `LoginRateLimiterPartitioning.GetPartitionKey`（同一段程式碼、同一個部署前提，見決策 6 原文中對可信代理的說明），但這只是共用「怎麼從 `HttpContext` 算出 IP 字串」的靜態方法，不代表共用 `login` policy 本身或其計數狀態。回應格式（`429`／`Retry-After`／`ProblemDetails`／共用的 `OnRejected` callback）沿用既有基礎設施，不重新實作。

**考慮過的替代方案**：
- 沿用 `"login"` policy 名稱——排除，如上述會造成計數污染，且違反本專案既有原則。
- 修改既有 `api-rate-limiting` 能力，新增一個「給任何匿名敏感端點共用」的抽象 policy——排除，`api-rate-limiting` 目前三個 Requirement 都是針對已上線的具體端點寫的具體契約，沒有既有先例支援這種抽象共用 policy；驗證碼取碼頻率與登入頻率的防護目的不同（防止 token 農場 vs 防止密碼窮舉），額度與時間窗也沒有理由綁在一起，回頭修改一個已封存、跟本次改動不直接相關的能力屬於不必要的範圍擴張。
- 讓 `CaptchaController.Get` 完全不做任何限流、只靠決策 4 的一次性 token 設計防護——排除，決策 4 本身已明確指出「不限制取碼頻率就等於用時間換取無限次嘗試機會」，這是本決策存在的理由。

**部署前提（沿用既有限制，非本次新決策）**：來源 IP 的取得方式沿用既有 `LoginRateLimiterPartitioning.GetPartitionKey`（`httpContext.Connection.RemoteIpAddress`）同一套推導邏輯，MUST NOT 另外實作一套判斷方式。此推導方式在反向代理／CDN 之後部署時，若未設定可信任的 `ForwardedHeadersOptions`，會取得代理節點位址而非真實用戶端 IP——這是 `login-rate-limiting` 已明確記錄的既有部署前提（見 `openspec/specs/api-rate-limiting/spec.md`「登入端點的請求頻率限制」小節），本次改動沿用同一套分區鍵邏輯即沿用同一個已知限制，不在本次範圍內重新處理或引入信任 `X-Forwarded-For` 的新邏輯。

### 決策 7：前端以共用 composable（`useCaptcha`）封裝驗證碼圖片載入、刷新、與 token 狀態管理，三處呼叫端共用同一元件

註冊頁、登入頁、加入排隊操作三處皆需要「顯示圖片、輸入文字、送出時附帶 token」的相同互動模式，抽成共用 composable 避免三處各自重複實作；圖片以 `GET /api/captcha` 回應中的 base64 PNG 直接嵌入 `<img>`，不另外開圖片串流端點（避免額外處理跨端點的 token-圖片配對複雜度）。

**`GET /api/captcha` 呼叫本身失敗時的行為**（不論是頁面初次載入、使用者手動刷新、或答錯後系統自動換發）：`useCaptcha` composable MUST 額外暴露一個錯誤狀態（例如 `loadError`），呼叫失敗（429 或 5xx）時設定該狀態、不拋出未捕捉例外中斷頁面；三處呼叫端一律顯示同一句提示「系統暫時無法取得驗證碼，請稍後再試」（不特別區分 429／5xx 的文字，比照決策 6 分區限流本身已是防護機制的一部分、不需要讓使用者分辨底層原因），並且在沒有成功取得目前有效 token 之前 MUST 停用送出按鈕（沒有 token 送出必然被後端拒絕，前端提前擋下比讓使用者送出後才收到後端錯誤更清楚），畫面上提供可再次觸發 `refresh()` 的操作（例如提示旁的重試按鈕，與既有手動刷新按鈕共用同一個處理函式）。初次載入失敗與刷新失敗共用同一套處理邏輯，不需要為兩者分別設計不同的 UI 狀態機。

**考慮過的替代方案**：初次載入失敗與刷新失敗分別設計不同提示文字／處理流程——排除，兩者對使用者而言都是「暫時拿不到驗證碼」，用同一套邏輯處理可用性一致，不增加不必要的狀態分支。

### 決策 8：CLAUDE.md 安全強制規則確認

**本次改動觸發輸入驗證、資料庫（Redis）讀寫、身份驗證/授權、前端四項觸發條件，依規則須逐項回答：**

- **外部輸入有沒有經過 Validation？在哪一層？**：`GET /api/captcha` 不接受任何外部輸入（無參數）。三個受保護端點（登入、註冊、加入排隊）新增的 `CaptchaToken`／`CaptchaAnswer` 兩欄位由 FluentValidation Validator 在 **Application 層**驗證，**驗證順序 MUST 固定為：先 trim 前後空白 → 再檢查非空白 → 再檢查長度上限，長度上限的判斷基準是 trim 之後的字串，不是原始輸入字串**（見下方「必須先 trim 再驗證長度」段落，這是本次改動明確選定的方案，避免與可用性設計互相矛盾）。長度上限：`CaptchaToken` 64 字元（實際產生值為 GUID 字串表示法固定 36 字元，上限留有餘裕但非無限制）、`CaptchaAnswer` 16 字元（實際答案固定 4 碼，上限容許使用者輸入時偶發的前後空白，同時避免不受限制的超長輸入直接送進雜湊運算與 log）。trim 後為空白或超過長度上限 MUST 視為同一類 `Validation` 錯誤，不呼叫 `ICaptchaService.VerifyAsync`（缺漏／空白對應 CAPTCHA-AUTH-002／CAPTCHA-REG-002／CAPTCHA-QUEUE-002，超長對應 CAPTCHA-INPUT-001／002）。格式與內容本身（是否命中正確答案）由 `ICaptchaService.VerifyAsync` 於同一層做語意驗證，不另外限制字元集（`CaptchaToken`／`CaptchaAnswer` 皆用相同雜湊比對邏輯處理，比對失敗即驗證失敗，不因格式不符 GUID／4 碼而有額外的例外路徑）

  **必須先 trim 再驗證長度，不能反過來（否則兩套規則互相矛盾）**：若對「未 trim 的原始字串」做 `MaximumLength(16)` 檢查，`"             TEST"`（前綴 13 個空白＋`TEST`，共 17 字元）這種輸入會先被長度規則擋下回 400，但同一份輸入 trim 後其實是合法的 4 碼答案——這與「比對前 trim、允許前後空白」的可用性設計（CAPTCHA-INPUT-003）直接衝突：使用者会先被「輸入太長」擋下，永遠走不到「trim 後比對」那一步。

  **版本核對**：本專案 `Directory.Packages.props` 鎖定 `FluentValidation 12.1.1`；`.Transform()`／`.TransformForEach()` 已於官方 11.x 標記 deprecated，並於 12.0 正式移除（design-hardener 審查階段已透過官方 FluentValidation 12.0 Upgrade Guide 查證此為列名的 breaking change，非僅憑訓練資料推測；此查證未實際在容器內執行 build 驗證，本機 repo 亦無此第三方套件原始碼可核對，屬於審查方法本身的已知限制，若實作階段編譯結果與此宣稱不符，MUST 以實際編譯結果為準並回頭更新本節），在此版本下不存在、無法編譯。因此 Validator MUST 改用官方遷移建議的寫法：`RuleFor` 直接以 lambda 取 trim 後的值建立規則鏈，並用 `.OverridePropertyName()` 維持錯誤訊息對應到正確欄位名稱，例如：

  ```csharp
  RuleFor(x => x.CaptchaAnswer == null ? null : x.CaptchaAnswer.Trim())
      .NotEmpty()
      .MaximumLength(16)
      .OverridePropertyName(nameof(RegisterMemberRequest.CaptchaAnswer));
  ```

  `CaptchaToken` 比照同一寫法、上限改為 64。這讓長度判斷基準與後續 `RedisCaptchaService.VerifyAsync` 內部 `Trim()` 後才雜湊比對的行為一致（CAPTCHA-INPUT-004）。**此寫法只影響該規則鏈內部用於驗證的值，不會反過來改寫 `CaptchaToken`／`CaptchaAnswer` 屬性本身**——`RedisCaptchaService.VerifyAsync` 仍必須自己對傳入的 `answer` 呼叫 `Trim()` 才能比對到正確結果，不能假設 Validator 已經把值改寫成 trim 後的版本

  **考慮過的替代方案**：在 `LoginRequest`／`RegisterMemberRequest`／`JoinPurchaseQueueRequest` 上新增額外的 computed property（例如 `TrimmedCaptchaAnswer`）供 Validator 專用——排除，這些 DTO 目前皆為簡單 record，新增一個僅供驗證用、與正式屬性並存的衍生屬性會讓呼叫端困惑該用哪一個，`RuleFor` lambda 內直接轉換值是官方文件明確列出的等價替代方案，不需要更動 DTO 形狀
- **有沒有直接拼接進 SQL 或 shell 指令？**：無。本次改動不新增任何 SQL 查詢或 shell 呼叫，`RedisCaptchaService` 僅呼叫 `StackExchange.Redis` 的型別化 API（`StringSetAsync`／`StringGetDeleteAsync`），key／value 皆透過該套件的 `RedisKey`／`RedisValue` 型別傳遞，不經過字串拼接組出命令
- **是否使用 EF Core／Dapper 參數化查詢？有沒有 N+1 查詢風險？**：不適用——本次改動不新增、不修改任何 PostgreSQL／EF Core 查詢，驗證碼狀態完全存於 Redis，與既有資料庫層無關
- **這個操作需要什麼權限？權限檢查在哪一層執行？**：`GET /api/captcha` 刻意匿名可存取（未登入使用者也需要在註冊頁看到驗證碼），不做任何權限檢查，這是設計本身要求（若要求登入才能取得驗證碼，會讓「未登入者無法通過驗證碼防護」變成邏輯矛盾）。三個受保護端點的既有權限規則不變：登入、註冊本身即為未登入狀態下呼叫，不需要（也不能要求）身份；加入排隊沿用既有 `[Authorize]`（只要求已登入、不限角色，`EventQueueController` 既有規則），驗證碼檢查與既有身份驗證彼此獨立，不取代也不影響對方
- **有沒有可能被未授權使用者觸發？**：`GET /api/captcha` 設計上任何人皆可呼叫（含未登入），這是刻意行為，不是漏洞；防止被濫用（大量產生驗證碼消耗 Redis 或繞過一次性設計）的手段是決策 6 的分區限流，不是身份驗證
- **前端：有沒有直接將使用者輸入渲染進 DOM？**：驗證碼圖片以 `<img>` 標籤載入 `data:image/png;base64,...`，不使用 `v-html`，不涉及使用者輸入渲染；驗證碼輸入欄位本身是使用者對自己輸入的文字框，比照既有表單欄位處理，不特別渲染為 HTML
- **前端：API 呼叫有沒有帶正確的 Auth Header？**：`GET /api/captcha` 不需要（匿名端點）；登入、註冊、加入排隊三個端點的既有 Auth Header 規則不變（加入排隊沿用既有攔截器帶入的 Bearer Token，登入/註冊本身不需要）

### 決策 9：測試共用基底 `CustomWebApplicationFactory` MUST 預設把 `ICaptchaService` 替換為 `FakeCaptchaService`

`tests/ProjectC.WebApi.Tests/TestSupport/CustomWebApplicationFactory.cs` 是目前 16 個既有測試檔案直接、另外 4 個透過子類別間接（合計 20 個，已逐一列出見下方核對範圍段落）共用的基底類別，這些測試透過 `AuthTestHelper.RegisterAsync`／`LoginAsync`／`RegisterAndLoginAsync` 或直接建構 `LoginRequest`／`RegisterMemberRequest` 呼叫真實的 `POST /api/auth/register`／`POST /api/auth/login` 端點準備測試前置資料，本身完全不是在測驗證碼行為。

登入、註冊、加入排隊三個端點新增驗證碼必填檢查後，若不處理，這些既有測試會因為：
1. `LoginRequest`／`RegisterMemberRequest` 建構子簽章改變（新增必填欄位）而編譯失敗
2. 即使補上欄位，也沒有任何方式能程式化解出 `RedisCaptchaService` 產生的隨機圖形驗證碼內容（不是像限流那樣「調高額度」就能繞過的問題——答案本身是隨機圖片內容，測試端無從得知）

而全數壞掉。這與 `CustomWebApplicationFactory.ConfigureWebHost` 已有的先例是同一類問題：該檔案已因為「一旦 Program.cs 加上全域 `login` policy，所有共用此基底類別的既有測試都會被誤傷」而把 `LoginRateLimiting:PermitLimit` 全域放寬到 1000（見該檔案既有註解）。CAPTCHA 的情況更嚴重——限流可以「放寬額度」繞過，驗證碼的答案無法用任何設定值繞過。

因此 `CustomWebApplicationFactory` 的 `ConfigureWebHost`／`ConfigureTestServices` MUST 新增：預設把 DI 容器內的 `ICaptchaService` 替換為 `FakeCaptchaService`（tasks.md 5.1，固定 token／答案）。`AuthTestHelper.RegisterAsync`／`LoginAsync` 內建構 `RegisterMemberRequest`／`LoginRequest` 時一律帶入 `FakeCaptchaService` 的固定有效 token／答案；`EventQueueControllerTests.cs` 既有呼叫 `/api/events/{id}/queue/entries` 的案例同理補上帶正確驗證碼的 `JoinPurchaseQueueRequest` body。

需要驗證**真實** `RedisCaptchaService` 行為的測試（CAPTCHA-GEN-*、CAPTCHA-RATE-001、CAPTCHA-FAIL-*，見 tasks.md 6.1／6.2／6.4）反過來才是例外：這些測試類別的 `WebApplicationFactory` MUST 個別在自己的 `ConfigureTestServices` 覆寫把 `ICaptchaService` 換回真實的 `RedisCaptchaService`，比照 `NeedsWorkingRedis` 這種既有透過子類別覆寫改變基底行為的既定慣例，不影響其他繼承 `CustomWebApplicationFactory` 的既有測試類別。

**核對範圍的重要澄清**：`tests/ProjectC.WebApi.Tests` 內實際存在三個彼此獨立、互不繼承的 `WebApplicationFactory<Program>` 階層，MUST 逐一處理，不能只改 `CustomWebApplicationFactory` 就當作涵蓋全部：
1. `CustomWebApplicationFactory` 及其子類別（`RedisUnavailableWebApplicationFactory`／`LogCapturingWebApplicationFactory`／`CachingComponentTestWebApplicationFactory`／`RateLimitedWebApplicationFactory`）——共 20 個既有測試檔案受影響（見 tasks.md 6.2a 的完整清單），本決策的替換套用在這裡
2. `LoginRateLimitedWebApplicationFactory`——刻意不繼承 `CustomWebApplicationFactory`（既有註解：避免連帶繼承 Testcontainers 容器建構行為），供 `Auth/LoginRateLimitingTests.cs` 使用；這是本次改動風險最高的既有測試（連續送出大量登入請求觸發限流），MUST 單獨為它新增同樣的 `ICaptchaService` → Fake 替換
3. `ObservabilityWebApplicationFactory`——同樣獨立繼承 `WebApplicationFactory<Program>`，供 `Observability/RequestTraceIdTests.cs`／`BackgroundServiceTraceIdTests.cs`／`SensitiveDataMaskingInStructuredPropertiesTests.cs` 使用，MUST 單獨為它新增同樣的替換

**考慮過的替代方案**：在這 20 個既有測試檔案／`AuthTestHelper` 各自組出正確驗證碼——排除，這些測試分散在完全不相關的能力（Orders、Tickets、Admin、Observability 等），要求它們各自知道驗證碼機制的存在是不合理的耦合，且找不到任何方式能讓它們程式化解出真實隨機圖片內容。

### 決策 10：`CancellationToken` 傳遞是外部可觀察契約，MUST 用既有 `RedisQueryCache` 的既定手法測試，不能只當作簽章慣例

`ICaptchaService.GenerateAsync`／`VerifyAsync` 的 `CancellationToken` 參數不是裝飾性簽章——`CaptchaController`／`GetCaptchaHandler`／`LoginHandler`／`RegisterMemberHandler`／`JoinPurchaseQueueHandler` 這條呼叫鏈上任何一處誤用 `CancellationToken.None` 而不是往下傳遞收到的參數，都會讓「HTTP request 取消後停止執行」這個保證在該處失效，且沒有編譯期或執行期的立即徵兆能發現。

`StackExchange.Redis` 的 `IDatabaseAsync` 非同步方法本身不接受 `CancellationToken` 參數（既有 `RedisQueryCache.cs` 已是如此），因此既有慣例是在方法最前面手動呼叫 `cancellationToken.ThrowIfCancellationRequested()`，讓已取消的呼叫在真正送出 Redis 命令之前就拋出——`RedisCaptchaService` MUST 延續同一手法，不得省略。既有 `RedisQueryCacheTests` 已建立驗證手法：對 `GetAsync`／`SetAsync`／`RemoveAsync` 各自傳入已取消的 `CancellationTokenSource.Token`，斷言 `OperationCanceledException` 往外拋、不被 fail-open 的 catch 吞掉——`RedisCaptchaServiceTests` MUST 對 `GenerateAsync`／`VerifyAsync` 比照辦理（對應 spec 新增的 CAPTCHA-CANCEL-001／002）。

**`GenerateAsync` 需要在兩個時間點檢查，不能只在方法最前面檢查一次**：既有 `RedisQueryCache.cs` 的 `GetAsync`／`SetAsync`／`RemoveAsync` 在方法最前面檢查之後緊接著就是 Redis 呼叫本身，中間沒有其他耗時工作，檢查一次已足夠涵蓋「MUST 在執行 Redis 操作前停止處理」這個保證。`RedisCaptchaService.GenerateAsync` 不同：方法最前面的檢查之後，還要經過 `ICaptchaImageGenerator` 產生 PNG（CPU-bound 的繪圖運算，非零耗時，尤其含雜訊線繪製）才會呼叫 `StringSetAsync`——若取消發生在這段耗時期間，方法最前面那一次檢查已經通過、不會再被攔截，`StringSetAsync` 仍會被呼叫，違反 spec「MUST 在執行 Redis 操作前停止處理」這個一般性保證（不只是「呼叫當下已取消」這種狹義情境）。因此 `GenerateAsync` MUST 在兩個時間點各檢查一次：方法最前面（避免呼叫端傳入已取消的 token 時還去做不必要的圖片產生），以及**緊接在 `StringSetAsync` 呼叫之前**（攔截圖片產生期間才發生的取消，對應 spec 新增的 CAPTCHA-CANCEL-004）。`VerifyAsync` 檢查之後到 `StringGetDeleteAsync` 之間只有輕量的字串 trim／正規化／雜湊運算，理論上不需要額外檢查，但為了與 `GenerateAsync` 維持一致的程式碼模式、明確符合 spec 的一般性 MUST 陳述、避免日後這段邏輯變得更複雜時被遺忘，MUST 同樣在 `StringGetDeleteAsync` 呼叫前再檢查一次。

**可測試性**：若 `RedisCaptchaService` 直接建構 `CaptchaImageGenerator` 這個具體類別，測試將無法控制「圖片產生完成的那一刻讓 token 被取消」這個精確時間點。因此 MUST 新增 `ICaptchaImageGenerator` 介面（`ProjectC.Infrastructure/Captcha/`），由 `RedisCaptchaService` 建構子注入。**此介面與其實作皆位於 `ProjectC.Infrastructure` 同一專案內，MUST NOT 被誤讀為違反 CLAUDE.md「不得將介面與實作放在同一專案」這條規則**——該規則鎖定的是 `Domain` 定義的 Repository／外部服務介面（決策 12 已論證的那個層級的抽象），`ICaptchaImageGenerator` 純粹是 Infrastructure 內部的技術性測試替身邊界，用來讓副作用（圖片產生的時序）在測試中可控，不對外跨越到 Domain／Application、不被任何 Handler 直接依賴，性質與該條規則規範的範疇不同，與決策 12 的介面放置規則亦無關。測試時以假的 `ICaptchaImageGenerator` 實作，在其產生圖片的方法內部呼叫測試自己持有的 `CancellationTokenSource.Cancel()` 再回傳圖片位元組，讓 `RedisCaptchaService.GenerateAsync` 恰好在「圖片已產生、尚未呼叫 Redis」的時間點收到已取消的 token，斷言：`OperationCanceledException` 往外拋，且**真實 Redis（`RedisFixture`，比照既有 `RedisQueryCacheTests` 手法）中該 token 對應的 key 確實不存在**，證明取消確實發生在 `StringSetAsync` 呼叫之前而非之後（tasks.md 5.2a；不使用 mock `IDatabase`／`IConnectionMultiplexer`——與既有 `RedisCaptchaServiceTests`／`RedisQueryCacheTests` 對「正常 Redis 行為」一律使用真實 Redis 而非 mock 的既定分工一致，mock 手法只用於決策 11／CAPTCHA-FAIL-* 這種需要模擬 Redis 拋例外的情境）。

Handler 層級同理（對應 spec 新增的 CAPTCHA-CANCEL-003）：`LoginHandler`／`RegisterMemberHandler`／`JoinPurchaseQueueHandler`／`GetCaptchaHandler` MUST 把自己收到的 `cancellationToken` 參數原樣傳給 `ICaptchaService`，不得改傳 `CancellationToken.None`。驗證方式：`FakeCaptchaService`（tasks.md 5.1）內部同樣加上 `cancellationToken.ThrowIfCancellationRequested()`，測試時對 Handler 傳入已取消的 token，若 Handler 確實把它轉傳給 `VerifyAsync`，測試會觀察到 `OperationCanceledException`；若 Handler 誤用 `CancellationToken.None`，測試會觀察不到例外而失敗，藉此不需要引入新的「斷言確切傳入哪個 token 實例」的 Mock/Spy 手法，沿用既有專案裡「pre-cancelled token 應該拋例外」這一種既定測試風格即可。

`CaptchaController`／Controller 層到 Handler 這一段的轉傳，屬於 ASP.NET Core 標準 Model Binding（action 方法的 `CancellationToken` 參數由框架綁定 `HttpContext.RequestAborted`）與一行直接傳遞的呼叫，不是自訂邏輯——比照既有 `LRL-003` 情境「framework 短路管線帶來的保證，不重新驗證框架行為本身」的既定原則，這一段不需要額外的自動化測試，靠程式碼審查確認呼叫時確實傳遞該參數即可。

**考慮過的替代方案**：用 Mock 斷言「Handler 呼叫 `VerifyAsync` 時傳入的 token 實例與收到的參數完全相同」——排除，這個專案目前沒有這種斷言手法的既有先例，且「pre-cancelled token 拋例外」已經是功能等價、更簡單、且有既有先例可循的驗證方式，不需要引入新的測試風格。

### 決策 11：Redis 連線／命令逾時上限須明確設定，不得依賴 StackExchange.Redis 預設值

**問題**：既有 `Program.cs`（195-214 行）註冊共用 `IConnectionMultiplexer` 時只明確設定 `AbortOnConnectFail = false`，未設定 `ConnectTimeout`／`SyncTimeout`／`AsyncTimeout`／`ConnectRetry`，因此套用 `StackExchange.Redis 3.1.31` 的函式庫預設值：`ConnectTimeout` 與 `SyncTimeout` 皆為 5000ms、`AsyncTimeout` 預設等於 `SyncTimeout`（同為 5000ms）、`ConnectRetry` 為 3。這對 `query-caching`／`purchase-queue-leader-election` 的 fail-open 用途影響有限（最壞情況只是稍微拖慢一次快取讀寫或鎖操作，仍會被各自的 try-catch 吸收後降級，不影響正確性），但對 CAPTCHA 的 fail-closed 邊界是不同性質的問題：

- 決策 4 的 `GETDEL`（`VerifyAsync`）與 `GenerateAsync` 的 `StringSetAsync` 都是單一 Redis 命令呼叫，若 Redis 連線已建立但網路退化、命令無回應（不同於「TCP 連線直接被拒絕」），該次呼叫會等到 `AsyncTimeout`（預設 5000ms）才拋出 `RedisTimeoutException`，期間 HTTP 請求持續掛起等待
- 既有 `RedisUnavailableWebApplicationFactory` 指向 `127.0.0.1:1`（無人監聽的 port）這種測試手法，驗證的是「TCP 連線被拒絕」這種快速失敗路徑（`RedisConnectionException` 幾乎立即拋出），並不涵蓋「連線正常、命令逾時無回應」這種更貼近真實網路退化情境的故障模式——後者才會真正吃滿預設的逾時秒數
- 高併發下（例如短時間內大量登入/註冊/加入排隊請求恰好遇上 Redis 逾時期間），即使是非阻塞的 async I/O，大量同時掛起等待中的 Task 仍會累積佔用連線池與記憶體資源，逾時秒數越長，這個累積效應的時間視窗就越大

**決策**：`Program.cs` 註冊共用 `IConnectionMultiplexer` 時，MUST 明確設定 `ConnectTimeout = 2000`、`SyncTimeout = 2000`、`AsyncTimeout = 2000`（毫秒，不依賴函式庫預設值）、`ConnectRetry = 1`（減少初始 `ConnectionMultiplexer.Connect()` 內部重試次數，避免 Redis 於應用程式啟動當下不可用時，啟動流程被迫等待「重試次數 × ConnectTimeout」——既有預設 3 次重試 × 5000ms 上限，最壞情況長達 15 秒）。

這是**共用**設定，同時套用於 `query-caching`／`purchase-queue-leader-election`／本次 CAPTCHA 三種用途——對 fail-open 的前兩者而言，更短的逾時只會讓它們更快偵測到故障並降級，它們的例外處理路徑本來就與逾時長短無關，只在乎「最終有沒有拋出例外」；對 fail-closed 的 CAPTCHA 而言，這讓「Redis 故障時多快回報技術性錯誤」有了明確、可稽核、可測試的上限，不再隱含依賴函式庫版本演進可能改變的預設值。

2000ms 的選擇：本機開發／容器內部網路（compose 服務間通訊）延遲通常在個位數毫秒等級，2 秒已遠高於正常操作所需時間，不會在正常情況下誤判逾時；同時遠低於預設的 5 秒，能讓使用者在合理時間內看到「稍後再試」的提示。

**高併發等待期間的資源保護**：本次不引入額外的斷路器（circuit breaker）或請求佇列限制機制（見 Non-Goals「不引入斷路器等進階 Redis 韌性機制」）——CAPTCHA 三個受保護端點與 `GET /api/captcha` 本身已有既有 rate limiting（決策 6 的 `captcha` policy、既有 `login` policy）將單一來源 IP 的請求頻率限制在低額度（`captcha` policy 預設 10 次/60 秒），即使 Redis 故障期間所有請求都需要等到 2 秒逾時才失敗，單一來源能製造的併發等待請求數量已被限流機制間接壓低；本專案定位為技術展示而非高流量正式服務，若未來有更高併發保護需求（例如全域斷路器，一旦偵測到 Redis 連續逾時就讓後續請求直接快速失敗，不必每個都各自等滿 2 秒），屬於另一個獨立的 change，不在本次範圍內。

**考慮過的替代方案**：只為 CAPTCHA 建立獨立、擁有自己專屬逾時設定的第二個 `IConnectionMultiplexer`——排除，`IConnectionMultiplexer` 官方建議整個應用程式共用單一實例（既有 214 行註解已說明），新增第二個實例會產生第二組 TCP 連線與重連邏輯，增加不成比例的複雜度；且如上述，調整共用設定對既有 fail-open 用途只有正面影響，沒有理由為了 CAPTCHA 一個能力就分裂連線池。維持函式庫預設值、不顯式設定——排除，這正是本決策要解決的問題：預設值分散在函式庫版本演進中、不透明，MUST 顯式宣告讓故障回應時間成為可稽核、可測試的契約。

### 決策 12：`ICaptchaService` 介面放置於 `Application/Common/Interfaces`，不放 `Domain`——比照 `IDistributedLock`／`IDateTimeProvider` 的既有例外

CLAUDE.md 架構規則原文：「Repository／外部服務的 interface 定義」一律放 `Domain`。但本專案已有經過審查、已合併的既有先例明確建立例外：`purchase-queue-leader-election`（`IDistributedLock`）design.md「介面放置位置」段落已建立判準——**純技術性、不承載業務語意、不被任何 Domain Entity 或 Application 業務規則直接使用、只作為執行協調機制的技術抽象**，放 `Application/Common/Interfaces`，比照 `IDateTimeProvider`；相對地，`IPaymentGateway`／`IEmailNotificationService` 這類承載業務語意（付款是否成功、通知是否送出，這些狀態會被 Domain／Application 業務規則直接使用與判斷）的外部服務介面，仍留在 `Domain`，該決策明確寫明「此例外僅適用於本次技術性分散式鎖介面，不修改其他外部服務介面的既定規則」——但既然 `IQueryCache`（`query-caching`）已比照同一判準沿用至 `Application/Common/Interfaces`，可見這個判準本身（而非僅限單一介面）已是本專案reviewed 過、重複套用兩次的既定慣例。

`ICaptchaService` 符合前者的判準：驗證碼答案、token 本身不是任何 Domain Entity（`Member`／`Event`／`Order`／`PurchaseQueueEntry`）建模的一部分，沒有任何 Domain 業務規則會查詢或依賴驗證碼狀態，`ICaptchaService` 只被 Application 層的 Handler 在執行業務邏輯**之前**呼叫一次、作為請求層級的技術性關卡（與 `IDistributedLock` 被背景服務在執行業務邏輯前呼叫的用法性質相同），驗證結果本身不落地成任何 Domain 狀態、不被序列化進任何 Entity。因此比照 `IDistributedLock`／`IDateTimeProvider`／`IQueryCache`（同樣是技術性基礎設施關注點，皆放 `Application/Common/Interfaces`）的既有慣例，`ICaptchaService` MUST 定義於 `ProjectC.Application/Common/Interfaces`（tasks.md 1.1 現有路徑已正確），`RedisCaptchaService` 實作放 `Infrastructure`，與既有三個介面採一致的放置規則，不額外為 CAPTCHA 立特例。`CaptchaChallenge`（`GenerateAsync` 的回傳型別）同理，僅供 Application 層的 `GetCaptchaHandler` 使用、不被 Domain 引用，MUST 與 `ICaptchaService` 同樣定義於 `Application/Common/Interfaces`（或同目錄下的 `Captcha` 子資料夾），不需要放 `Domain`。

**考慮過的替代方案**：依 CLAUDE.md 字面規則移至 `Domain`——排除，若只把 `ICaptchaService` 移到 Domain，會讓四個性質相同（純技術性、無業務語意、由 Application Handler 直接消費）的介面（`IDateTimeProvider`／`IDistributedLock`／`IQueryCache`／`ICaptchaService`）中，前三個留 `Application`、只有 CAPTCHA 放 `Domain`，製造沒有原則依據的不一致，比字面遵守 CLAUDE.md 而製造內部矛盾的代價更高（CLAUDE.md Rule 7：發現衝突要選一個、說明理由、標記另一個待清理，不要各改各的）。若未來這四個介面的放置慣例整體需要重新檢討（例如認為這個例外判準本身有害，應收斂回 Domain），應在獨立的技術債 change 中一次處理全部四個介面，不是本次 CAPTCHA change 的範圍。

### 決策 13：`ICaptchaService`／`ICaptchaImageGenerator` 的 DI Lifetime 明確為 Scoped，不與 `IConnectionMultiplexer` 的 Singleton 生命週期混為一談

**問題**：原 tasks.md 2.6 把 lifetime 寫成「Scoped 或 Singleton，依 `IConnectionMultiplexer` 既有註冊生命週期決定」——這不是可執行的判準：`IConnectionMultiplexer` 是 Singleton 且官方保證 thread-safe，但這不代表**依賴**它的服務也必須或可以是 Singleton；lifetime 判準取決於該服務自身是否持有可變狀態、其依賴是否皆 thread-safe，與它剛好用到哪個既有 Singleton 無關（CLAUDE.md DI Lifetime 判準表：Scoped 用於「狀態綁定單一 HTTP request」，Singleton 用於「全域共用、無狀態、必須 thread-safe」）。

**決策**：`RedisCaptchaService`（`ICaptchaService`）與 `CaptchaImageGenerator`（`ICaptchaImageGenerator`）MUST 註冊為 **Scoped**。理由：

- `RedisCaptchaService` 本身除了建構子注入的依賴（`IConnectionMultiplexer`、`ICaptchaImageGenerator`、`CaptchaOptions`）外不持有其他可變狀態，Scoped／Singleton 對它自身而言皆無資料競爭風險；但它依賴的 `ICaptchaImageGenerator` 是否能安全跨執行緒共用，才是關鍵。
- `CaptchaImageGenerator` 使用 `SixLabors.Fonts` 的 `FontCollection`／`FontFamily` 繪製文字。查證後未能取得官方文件對 `FontFamily`／`FontCollection` 多執行緒並行讀取的明確 thread-safety 保證，且沒有既有先例可比照（`TicketQrCodeGenerator` 用 `QRCoder` 純像素運算，不涉及字型）。在無法取得第三方套件明確 thread-safety 保證的情況下，MUST NOT 假設安全並宣告為 Singleton。
- 選擇 Scoped 讓每個 HTTP request 各自持有獨立的 `CaptchaImageGenerator` 執行個體（含各自的 `FontCollection`），從根本排除跨執行緒共用同一份字型物件的疑慮，不需要仰賴未經證實的第三方 thread-safety 保證，比照 CLAUDE.md DI Lifetime 判準表「若不確定該選哪個 lifetime，預設用 Scoped」的既定原則。
- 代價：每個 request scope 各自重新解析內嵌字型檔（parse TTF），有一定 CPU 成本，但驗證碼圖片產生本身頻率已受決策 6 的 `captcha` policy 限流（預設 10 次/60 秒/IP）節制，且本專案定位為技術展示、非高流量正式服務，這個成本可接受；若未來效能量測顯示這是瓶頸，應在獨立的效能最佳化 change 中，針對字型解析本身（而非整個 `CaptchaImageGenerator`）評估是否能安全快取為 Singleton（需要屆時補上明確的並發正確性測試或找到官方 thread-safety 保證）。

`IConnectionMultiplexer` 維持既有 Singleton 不變（官方明確保證 thread-safe，本次改動不變更其生命週期，僅決策 11 調整其逾時設定）。`CaptchaOptions` 沿用既有 Options Pattern（見決策 14），不受本決策影響。

**考慮過的替代方案**：Singleton（含證明 thread-safe 後採用）——排除，需要能明確引用 `SixLabors.Fonts` 官方 thread-safety 保證或新增並發產圖測試佐證，本次查證未能取得肯定證據，不符合「不確定時預設 Scoped」的專案既定判準，且沒有急迫的效能需求需要承擔這個舉證成本。

### 決策 14：`CaptchaOptions.TtlSeconds` 比照 `CaptchaRateLimitingOptions`（有預設值＋擋無效值），不是比照 `QueryCacheOptions`（無預設值）

**問題**：原 tasks.md 1.2 已決定 `TtlSeconds` 具備預設值 `120`，但 tasks.md 2.5 卻寫「比照 `QueryCacheOptions` 既有驗證模式」——這兩者實際上是本專案兩種不同、互斥的既有驗證模式：

- `QueryCacheOptions`（`EventListTtlSeconds`／`TicketTypesTtlSeconds`）：屬性**沒有**預設值（C# 預設為 `0`），靠 `[Range(1, int.MaxValue)]` ＋ `.ValidateOnStart()`，讓「設定缺漏」與「設定為 0」得到**同樣的 fail-fast 效果**——這個模式**沒有**「缺漏時採用預設值」這個行為，缺漏本身就是錯誤。
- `CaptchaRateLimitingOptions`／`LoginRateLimitingOptions`：屬性**具備**明確預設值（如 `PermitLimit = 10`），靠 `[Range(1, int.MaxValue)]`（擋顯式 0／負數），MUST NOT 加 `.ValidateOnStart()`（這兩者的既有模式是刻意在 `app.Build()` 之後另外手動呼叫 `app.Services.GetRequiredService<T>()` 強制解析一次以觸發 fail-fast，而非用 `.ValidateOnStart()`，讓「缺漏時使用預設值」與「顯式設為無效值時 fail-fast」兩者並存不衝突）。

`TtlSeconds` 若沿用 `QueryCacheOptions` 模式（無預設值），會直接與 tasks.md 1.2 已經寫明的「預設 120」互相矛盾——缺漏時到底是要 fail-fast 還是用 120，兩份任務描述給了互斥的答案。

**決策**：`CaptchaOptions.TtlSeconds` MUST 比照 `CaptchaRateLimitingOptions`／`LoginRateLimitingOptions` 的模式，不是 `QueryCacheOptions`：

- `public int TtlSeconds { get; set; } = 120;`（有明確預設值，與 spec 其餘 Requirement 描述的 120 秒一致）
- `[Range(1, int.MaxValue)]` 擋下顯式設定為 0 或負數的情況
- `Program.cs` 註冊時 MUST NOT 加 `.ValidateOnStart()`，MUST 比照 `CaptchaRateLimitingOptions` 在 `app.Build()` 之後另外呼叫 `app.Services.GetRequiredService<CaptchaOptions>();` 強制解析一次以觸發 fail-fast
- 對應 spec 新增的 Requirement「驗證碼時效設定值須為正數，缺漏時採用明確預設值」與 Scenario CAPTCHA-STORE-003（缺漏採預設值）／CAPTCHA-STORE-004（0 或負數擋下）

**考慮過的替代方案**：沿用 `QueryCacheOptions` 模式（無預設值，缺漏視為錯誤）——排除，這會讓「未設定 `Captcha:TtlSeconds`」這種最常見的省事設定方式（比照 `CaptchaRateLimitingOptions` 已經採用的「缺漏可接受、用預設值」慣例）在 CAPTCHA 這裡變成啟動失敗，體驗不一致且與 tasks.md 1.2 已經寫明的預設值直接矛盾。

## Risks / Trade-offs

- **[風險] Redis 故障時三個端點（註冊/登入/加入排隊）完全不可用** → 緩解：這三者皆非訂單交易的關鍵路徑（下單、付款、核銷不受影響），且 Redis 已是既有排隊機制的必要依賴（`purchase-queue-leader-election` 故障時排隊本身的並發正確性已依賴 Redis），本次改動不新增「引入 Redis 才產生的可用性風險」這一類別的新風險，只是把既有風險延伸到另外三個端點；決策 11 已將 Redis 逾時上限明確設為 2 秒，故障期間單一請求的最壞等待時間有明確上限，不會無限期掛起
- **[風險] 自建驗證碼圖片辨識強度低於第三方服務（reCAPTCHA 等有持續對抗機器學習辨識的能力）** → 緩解：此為 Non-Goal 已明確排除的範疇（本專案定位為技術展示、非正式營運系統），且與既有 rate limiting、排隊機制疊加後已能展示「多層防護」的設計思路，可接受
- **[風險] 4 碼英數字元空間有限，理論上仍可被窮舉（扣除混淆字元後約 32 個字元，4 碼約 100 萬種組合）** → 緩解：決策 4 的一次性 token 設計讓每次嘗試都需要重新取得新圖片與新 token，決策 6 的限流讓取得新圖片本身也受頻率限制，兩者疊加後窮舉所需時間大幅提高，足以達成本專案的展示目的
- **[取捨] 前端三處皆需要修改既有頁面加入驗證碼元件，增加前端改動範圍** → 接受：與 Goals 直接相關，且透過決策 7 的共用 composable 已將重複工作降到最低
- **[風險，誠實揭露] 決策 6 的限流只限制「產生新驗證碼」的頻率，不限制「持有多組已解出答案的有效 token 後、在各自 TTL 到期前批次送出多次註冊/登入/加入排隊嘗試」**——攻擊者理論上可在限流門檻內慢慢囤積多組有效 token（每組仍只能用一次），之後短時間內批次送出，使「一次性 token＋限流」疊加後的實際防護強度略低於單看兩者字面描述的直覺 → 接受：囤積本身仍受限流機制的時間窗口限制（同一時間窗口內能拿到的 token 數量有上限），且解出驗證碼本身仍需要真人或有效的 OCR 能力，不是免費的；此殘餘風險與 Non-Goals 已排除的「進階機器人辨識」屬於同一類別，本次不處理，留給未來若有需要時再評估（例如把 token 的簽發速率與消費速率綁定在同一個滑動視窗）
