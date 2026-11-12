# Production Readiness TODO — 2026-11-12

> 來源：`production-readiness-report-2026-11-12.md`、`documentation-audit-2026-11-12.md`。
> 排序：🔴 Critical → 🟠 High → 🟡 Medium → 🔵 Low。工時：S（< 半天）／M（1–3 天）／L（> 3 天）。
> `[文件]` = 僅改文件。
> ✅ = 已於 2026-11-12 審查後直接修正（不需 OpenSpec 流程，均為文件／設定／相依性更新，未 commit）。

## ✅ 已修正（2026-11-12）

| 項目 | 修正內容 | 驗證 |
|---|---|---|
| M-5 npm 漏洞（部分） | `npm audit fix`（非 `--force`）：vite 8.2.0 → 8.3.8，undici 升級；24 → 13 個，剩餘 13 個 moderate 皆需 major 升級（eslint 10／minimatch 鏈），未套用 | lint 0 error、Vitest 49 files／725 passed、`vite build` 成功；undici 僅在 `jsdom` 之下（devDependency），不進 runtime bundle |
| M-10 `.env.example` | 補 `WEB_HOST_PORT`；補註「compose 不轉入」的部署用變數指引（compose 的 api 區塊實際只轉入 Jwt／Auth／TicketSigning／DevelopmentSeed／Seq／Redis，故不列為可用變數） | `docker compose config` 未新增變數警告 |
| M-11 `settings.local.json` | 移除 `dotnet build/test/ef/run/sln`、`git commit -m`、`git merge master -m` 允許項（檔案為 git-ignored 本機設定） | 之後這些指令會回到權限詢問 |
| M-13 `web/README.md` | 刪除「我的訂單只有空狀態」過時限制，改為現況（已串接 `GET /api/orders`）＋「無 E2E」 | — |
| M-14 `deployment-prerequisites.md:3` | 開頭改寫為 2026-11-12 現況（k8s/、deploy/ 已存在；VPS 線未實測） | `scripts/check-doc-paths.sh` OK |
| README 已知限制 | 新增「尚未達可上線狀態」條目並連結本審查報告與 TODO | — |
| L-1 架構指南 | `Program.cs:386` → `RateLimitingServiceCollectionExtensions.cs:51`；導覽列加註行號可能漂移 | — |
| L-2 CLAUDE.md graphify 敘述 | 改為「graphify-out/ 已 git-ignore」 | — |
| L-6 `ticket-redemption` Purpose | 由 TBD 改為依 7 條 Requirement 歸納的目的敘述 | `openspec validate --all` 32 passed |

| 階段 0（分支 `chore/stage0-hardening`，未 commit） | M-9：未知 Email 也對固定 dummy hash 驗證一次；M-17：`HostOptions.ShutdownTimeout` 20 秒（K8s 的 grace period 與 preStop 原本就有）；H-11＋M-16：寫入 `docs/2026-11-12-clock-dependencies.md`，Logout 維持匿名 | Application.Tests 626 passed（新增測試 RED：`Expected VerifyPasswordCallCount to be 1, but found 0`）、Infrastructure `BCryptPasswordHasherTests` 2 passed、WebApi Startup 82 passed |

未修正但不需 OpenSpec 的項目與理由：M-12 全域 `~/.claude/CLAUDE.md`（影響其他專案，請使用者自行決定）；L-3 graphify 絕對路徑（hook 執行環境的 PATH 不確定，改錯會讓每次工具呼叫失敗）；L-4 LICENSE（授權選擇屬使用者決策）；M-15 版本號／CHANGELOG（需決定版號策略）；L-5 digest 釘版、L-8 gzip、H-3 nginx 安全標頭雖是設定檔，但會改變 production 映像行為且 CSP 可能破壞前端，建議併入 H-3 的 change 一起驗證。

---

## 建議修正／開發順序（2026-11-12）

原則：(1) 先堵「對外曝露就會出事」的洞，再補「收錢才需要」的鏈路；(2) 每個 OpenSpec change ≤ 3 個 capability、≤ 30 task（CLAUDE.md 顆粒度規則）；(3) 同一處程式碼只動一次，把會互相牽動的項目併成一個 change；(4) 文件類與一行設定類的項目夾在 change 之間當緩衝，不另排期。

| 階段 | 目標 | 建議 change（依序） | 涵蓋項目 | 依賴 | 預估 |
|---|---|---|---|---|---|
| **0. 立即（無 spec 小改）** | 清掉零成本項目 | 無 change；依「無對應 spec」條款直接改 | H-11 文件化時鐘依賴（寫進 runbook 草稿）、M-16 決策（Logout 維持匿名並記錄理由，或改）、M-17 `ShutdownTimeout`＋`terminationGracePeriodSeconds`、M-9 dummy hash（純 Handler 內改，TDD 單元測試） | 無 | 半天 |
| **1. 代理層與認證邊界** | 任何對外曝露前必須完成 | ① `reverse-proxy-forwarded-headers`：nginx 轉送標頭、`UseForwardedHeaders`＋信任代理、限流 key 改用轉送後 IP、`captcha` 同步、公開讀取端點加 IP 限流 | **C-2**、M-3 | 無 | M |
| | | ② `edge-security-headers`：TLS 終結（Ingress／cert-manager 或 nginx）、HSTS、CSP／X-Frame／nosniff、`AllowedHosts`、CORS 決策、gzip、digest 釘版 | H-3、L-5、L-8 | ① 完成（HSTS 需 Proto 正確） | M |
| | | ③ `secrets-and-observability-hardening`：Seq 認證＋API key＋PVC、Redis `requirepass`＋AOF＋PVC、Admin email 移 Secret、migrator 改環境變數、Sealed／External Secrets | H-4、H-8、M-7 | 無（可與②並行，但建議序跑以免審查互擾） | M |
| **2. 金流與通知鏈** | 從「展示」變「能收錢」 | ④ `production-guard-for-mocks`：Production 下 Mock 付款／Email 綁定即啟動失敗（`ValidateOnStart`）；README 同步 | **C-1(b)**、H-2 前置 | 無 | S |
| | | ⑤ `email-delivery`：真實 Email 實作（SMTP／SendGrid），密碼重設改為 Handler 內寄送 | H-2、H-1 | ④ | M |
| | | ⑥ `payment-gateway-integration`：真實金流（授權→扣款→webhook 驗簽→冪等→對帳），含 Declined／逾時／重複 webhook 情境 | **C-1(a)** | ④、⑤（付款結果通知） | L |
| **3. 資料安全與一致性** | 不丟資料、不重複處理 | ⑦ `order-cleanup-leader-lock`：清理服務取 `IDistributedLock`、`ChangeOrderStatusAsync` 改 FOR UPDATE 或 Order 加 xmin、多副本整合測試 | H-6 | 無（建議在⑥之前完成，金流接上後重複取消代價更高） | S–M |
| | | ⑧ `db-resilience`：`EnableRetryOnFailure`（execution strategy 包交易）、`CommandTimeout`、連線池調校＋k6 複測 | M-1 | ⑦（兩者都碰 OrderService 交易邊界） | M |
| | | ⑨ `postgres-backup-restore`：pg_dump CronJob＋外部儲存、RPO/RTO 文件、一次還原演練 | H-5 | 無 | M |
| **4. 可觀測性與交付管線** | 上線後看得見、改得動 | ⑩ `metrics-and-alerts`：OpenTelemetry Metrics＋`/metrics`、4 條告警、Grafana dashboard JSON 進版控 | H-7 | 無 | M |
| | | ⑪ `ci-quality-gates`：ReportGenerator 合併覆蓋率＋門檻、trivy image scan、gitleaks、映像建置與 push | M-4 | 無 | M |
| | | ⑫ `e2e-golden-path`：Playwright 覆蓋註冊→登入→排隊→下單→確認→QR→核銷，進 CI | M-8 | ⑪ | M |
| | | ⑬ `[文件]` runbook＋回滾手冊 | H-10 | ⑨⑩ 完成後內容才完整 | S |
| **5. 合規** | 個資法最低要求 | ⑭ `member-data-rights`：刪除（匿名化）＋匯出端點、token 清理 job、隱私告知頁 | H-9 | ⑤（刪除確認信） | L |
| **6. 收尾** | 低優先 | M-2 refresh token 改 HttpOnly cookie（需 CSRF 設計，獨立 change）、M-6 `Directory.Build.props`＋`TreatWarningsAsErrors`、M-15 版號／CHANGELOG、L-3／L-4／L-7／L-9 | — | — | 各 S–M |

**為什麼這個順序**
- 階段 1 排在金流之前：C-2 與 H-3 是「只要有人連得到就會被打」的問題，不收錢也會出事；而 C-1 在系統不對外時沒有實害。
- ④ 先於 ⑥：一個 S 級的 fail-fast 守門就能把 Critical 降為「已知不可上線」，爭取時間做 L 級的真實金流。
- ⑦ 先於 ⑥：金流接上後，重複取消＝重複退款，修正代價倍增。
- ⑪ 先於 ⑫：e2e 需要 CI 能建映像。
- ⑭ 最後：依賴 Email，且在對外收集個資之前完成即可。

**估計總量**：Critical＋High 全部完成約 14 個 change；以過去 71 個 change／3.5 個月的節奏（含審查），約 6–8 週。若只求「可對外展示（不收錢）」，做完階段 0–1 加 ④、⑦ 即可解除 No-Go 中的 C-2，並把 C-1 轉為明示限制。

---

## 🔴 Critical

### C-1 正式環境付款為 Mock
- **維度**：功能完整度／資安 A04
- **位置**：`src/ProjectC.WebApi/Extensions/TicketingServiceCollectionExtensions.cs:24`、`src/ProjectC.Infrastructure/Payments/MockPaymentGateway.cs:15`、`src/ProjectC.Application/Orders/ConfirmOrderHandler.cs:70`
- **問題**：`IPaymentGateway` 無條件綁 `MockPaymentGateway`，`AlwaysSucceed` 預設 true；確認訂單不收款即成功。
- **修法**：(a) 接真實金流（另開 change，含 webhook 驗簽、冪等、對帳）；或 (b) 短期：`Production` 環境若 `IPaymentGateway` 解析為 Mock 即啟動失敗（`ValidateOnStart` 或 Program.cs 檢查），並在 README 明示「不可收費上線」。
- **驗收**：Production 環境無真實 gateway 設定時 `dotnet ProjectC.WebApi.dll` 啟動失敗並記錄明確錯誤；有設定時 ConfirmOrder 整合測試以 stub gateway 驗證 Declined 路徑回 409。
- **工時**：(b) S；(a) L

### C-2 登入／驗證碼限流在反向代理後失效
- **維度**：資安 A07／API2
- **位置**：`src/ProjectC.WebApi/Common/LoginRateLimiterPartitioning.cs:10`、`src/ProjectC.WebApi/Extensions/RateLimitingServiceCollectionExtensions.cs:55,58`、`deploy/nginx/default.conf:9-13`、`src/ProjectC.WebApi/Program.cs`（無 `UseForwardedHeaders`）
- **問題**：分區 key 為 `Connection.RemoteIpAddress`；nginx／K8s 不送、API 不讀 `X-Forwarded-For` → 全站共用一個 5/60s 計數器：任一人錯 5 次即全站鎖登入；攻擊者亦可藉此 DoS。
- **修法**：nginx 加 `proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for; X-Forwarded-Proto $scheme; X-Real-IP $remote_addr;`；API `UseForwardedHeaders` 並以 `KnownNetworks/KnownProxies` 限定信任來源（放在 `UseHttpsRedirection` 之前）；限流 key 改用轉送後 IP，並評估 login 改為「IP ＋ 帳號」複合分區。
- **驗收**：整合測試以兩個不同 `X-Forwarded-For` 各打 5 次登入，彼此不互相觸發 429；不帶 header 且來源非信任代理時忽略 header；`scripts/verify-k8s-manifests.sh` 既有限流斷言仍通過。
- **工時**：M

---

## 🟠 High

### H-1 密碼重設流程無投遞管道
- **維度**：功能完整度
- **位置**：`src/ProjectC.Application/Authentication/PasswordReset/RequestPasswordResetHandler.cs:28,47`、`src/ProjectC.WebApi/Controllers/AuthController.cs:74-76`
- **問題**：token 產生後被丟棄，使用者永遠無法完成重設。
- **修法**：接 `IEmailNotificationService` 真實實作後，由 Handler 內部寄送（Controller 維持不回 token）。
- **驗收**：整合測試以 fake email sink 驗證已註冊 Email 收到含 token 的訊息、未註冊 Email 不寄且回應相同。
- **工時**：M（依賴 H-2）

### H-2 Email 通知為 Mock
- **維度**：功能完整度
- **位置**：`TicketingServiceCollectionExtensions.cs:30`、`src/ProjectC.Infrastructure/Notifications/MockEmailNotificationService.cs`
- **問題**：訂單確認／票券通知不會送出。
- **修法**：新增 SMTP／SendGrid／SES 實作於 Infrastructure；Production 下 Mock 綁定 fail-fast（同 C-1(b) 模式）。
- **驗收**：Production 無 Email 設定時啟動失敗；有設定時 ConfirmOrder 流程觸發一次寄送（以 fake transport 斷言）。
- **工時**：M

### H-3 無 TLS／HSTS／安全標頭／CORS 政策，`AllowedHosts: "*"`
- **維度**：資安 A02／A05
- **位置**：`deploy/nginx/default.conf:2`、`k8s/50-web.yaml`、`src/ProjectC.WebApi/appsettings.json:18`、`Program.cs`
- **問題**：全程明文；無 CSP／X-Frame-Options／X-Content-Type-Options／Referrer-Policy；Host header 不受限。
- **修法**：K8s 加 Ingress（cert-manager／Let's Encrypt）或 nginx 終結 TLS；nginx 加安全標頭與 `gzip`；API `UseHsts`（非 Development）、`AllowedHosts` 設實際網域；明文 CORS 政策（同源則記錄決策）。
- **驗收**：`curl -I https://<host>/` 回 HSTS、CSP、X-Frame-Options；`curl -H 'Host: evil' http://api:8080/health/live` 回 400。
- **工時**：M

### H-4 Seq 無認證且無 API key
- **維度**：可觀測性／資安 A05
- **位置**：`k8s/22-seq.yaml:28`、`src/ProjectC.WebApi/Logging/SerilogConfigurator.cs:43-46`
- **問題**：日誌含 email／訂單資料；任何可達 5341 的 Pod 可讀寫。
- **修法**：Seq 啟用認證（移除 `SEQ_FIRSTRUN_NOAUTHENTICATION`，以 Secret 設管理員密碼）；sink 加 `apiKey`（Secret 注入）；Seq 加 PVC。
- **驗收**：無 API key 寫入回 401；Seq Pod 重啟後歷史日誌仍在。
- **工時**：S

### H-5 PostgreSQL 無備份／RPO/RTO
- **維度**：資料庫／部署維運
- **位置**：`k8s/20-postgres.yaml:12,22`
- **問題**：單副本、hostPath 1Gi、無 CronJob 備份、無還原演練。
- **修法**：加 `pg_dump` CronJob 到外部儲存（或改用託管 DB）；文件化 RPO/RTO；做一次還原演練並記錄。
- **驗收**：`docs/` 有備份手冊，含最近一次還原演練日期與耗時；CronJob 連續 7 天成功。
- **工時**：M

### H-6 `ExpiredOrderCleanupService` 多副本無 leader lock
- **維度**：韌性
- **位置**：`src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs`（無 `IDistributedLock`）、`k8s/40-api.yaml` replicas 2、`src/ProjectC.Infrastructure/.../OrderService.cs:736+`
- **問題**：兩副本同時掃描取消；`ChangeOrderStatusAsync` 非 FOR UPDATE 載入，且 `OrderConfiguration.cs` 無並發 token → 重複釋放庫存／座位風險 🤔[需多副本動態測試確認]。
- **修法**：比照 `PurchaseQueueAdmissionService` 取 `IDistributedLock`；或 `ChangeOrderStatusAsync` 改 FOR UPDATE＋狀態機冪等。
- **驗收**：整合測試並行兩個 service 實例處理同一批過期訂單，座位釋放次數 = 1；單元測試驗證鎖未取得時跳過本輪。
- **工時**：S–M

### H-7 無 metrics／告警
- **維度**：可觀測性
- **位置**：`git grep OpenTelemetry src` → 空
- **問題**：無法量化 5xx 率、下單失敗、限流命中、refresh 重用。
- **修法**：加 OpenTelemetry Metrics＋`/metrics`（Prometheus exporter），至少 4 個告警（5xx 率、readiness 失敗、登入失敗率、refresh reuse 偵測）。
- **驗收**：`curl /metrics` 含 `http_server_request_duration_seconds` 與自訂 `projectc_order_place_total{result}`；告警規則檔進版控。
- **工時**：M

### H-8 Secret 管理與 Admin 信箱寫死
- **維度**：部署維運
- **位置**：`k8s/examples/secret.example.yaml`、`k8s/10-configmap.yaml`（`AdminBootstrap__Email`）、`k8s/30-migrator-job.yaml`
- **問題**：CHANGE-ME 範本無管理機制；Admin email 在 ConfigMap；migrator 連線字串走 args。
- **修法**：導入 Sealed Secrets 或 External Secrets；Admin email 改 Secret；migrator 改讀環境變數（efbundle 支援 `--connection` 外亦可用 `ConnectionStrings__DefaultConnection` 由程式讀取，需確認 bundle 行為 🤔）。
- **驗收**：`kubectl describe pod <migrator>` 不含密碼；`git grep CHANGE-ME k8s` 只剩 examples/。
- **工時**：M

### H-9 個資法最低合規缺口
- **維度**：資料與隱私
- **位置**：`git grep -nE 'DeleteMember|Anonymi|Export' src` → 空；`docs/` 無隱私文件
- **問題**：無刪除／匯出／保存期限；無蒐集告知。
- **修法**：新增「刪除帳號（匿名化）」與「匯出我的資料」端點（Admin 可代執行）；`RefreshToken`／`PasswordResetToken` 清理 job；撰寫隱私告知頁。
- **驗收**：刪除後 `Member` 的 email／RealName 被匿名化、訂單保留但去識別；清理 job 整合測試刪除過期 token；前端註冊頁連結隱私告知。
- **工時**：L

### H-10 Runbook／回滾手冊缺失 `[文件]`
- **維度**：部署維運／文件
- **位置**：`docs/` 無 runbook
- **修法**：於 `docs/` 新增帶日期前綴的 runbook 文件：啟動順序、migration 失敗處置、回滾（映像＋migration Down）、Redis 失效行為、Seq 查詢範例、聯絡人。
- **驗收**：依 runbook 由非作者完成一次 kind 部署＋回滾演練。
- **工時**：M

### H-11 時鐘依賴無監控
- **維度**：其他風險
- **位置**：JWT ClockSkew、`HeldUntilUtc`、Redis TTL
- **修法**：節點 NTP 強制＋告警（偏移 > 1s）；文件化依賴。
- **驗收**：告警規則存在；runbook 列出時鐘漂移症狀。
- **工時**：S

---

## 🟡 Medium

### M-1 DB 連線無 retry／timeout／池調校
- **位置**：`src/ProjectC.WebApi/Extensions/PersistenceServiceCollectionExtensions.cs:18-19`
- **修法**：`EnableRetryOnFailure`（注意與 `FOR UPDATE` 交易的相容性，需使用 execution strategy 包交易）、`CommandTimeout`、`MaxPoolSize` 依 k6 結果調整。
- **驗收**：整合測試模擬瞬斷後成功；k6 冷啟動 P95 複測。
- **工時**：M

### M-2 前端 refresh token 存 `localStorage`
- **位置**：`web/src/stores/auth.ts:12-20`
- **修法**：改 HttpOnly Secure SameSite=Strict cookie（需 CSRF 對策與 CORS 決策）；或縮短效期＋加 CSP 降低影響。
- **驗收**：XSS 測試頁無法讀取 refresh token；refresh 流程 e2e 通過。
- **工時**：M

### M-3 公開 `GET /api/events*` 無限流
- **位置**：`src/ProjectC.WebApi/Controllers/EventsController.cs`
- **修法**：加 `public-read` IP 分區限流（依賴 C-2 修正），或 GlobalLimiter。
- **驗收**：超過門檻回 429 帶 `Retry-After`。
- **工時**：S

### M-4 CI 無覆蓋率門檻／映像掃描／secret 掃描
- **位置**：`.github/workflows/ci.yml`
- **修法**：ReportGenerator 合併 cobertura＋門檻（line ≥ 85%、branch ≥ 70%）；加 trivy image scan、gitleaks。
- **驗收**：PR 覆蓋率低於門檻失敗；trivy High 以上失敗（allowlist 沿用 `.security/`）。
- **工時**：M

### M-5 npm 漏洞（✅ 部分修正，剩 13 moderate）
- **位置**：`web/package-lock.json`
- **現況**：已 `npm audit fix`，剩餘 13 個 moderate 全在 `eslint`／`editorconfig` → `minimatch`、`@eslint/config-array` 鏈，需 `--force` 升 eslint major。
- **修法**：另行評估升級 eslint 10 與相關 plugin，跑 lint 確認規則無破壞。
- **驗收**：`npm audit --audit-level=moderate` 0。
- **工時**：S

### M-6 分析器僅 WebApi、`TreatWarningsAsErrors=false`
- **位置**：`src/ProjectC.WebApi/ProjectC.WebApi.csproj:9-12`
- **修法**：新增 `Directory.Build.props` 統一分析器；分階段開 `TreatWarningsAsErrors`。
- **驗收**：全解決方案 0 warning 後開啟。
- **工時**：M

### M-7 Redis 無持久化／無密碼
- **位置**：`k8s/21-redis.yaml`
- **修法**：AOF＋PVC；`requirepass` 由 Secret 注入。
- **驗收**：Redis 重啟後排隊名單保留；無密碼連線被拒。
- **工時**：S

### M-8 無 e2e 測試
- **位置**：`web/package.json`
- **修法**：Playwright 覆蓋註冊→登入→排隊→下單→確認→QR 一條線。
- **驗收**：CI 執行通過。
- **工時**：M

### M-9 登入帳號枚舉時間差 🤔
- **位置**：`src/ProjectC.Application/Authentication/Login/LoginHandler.cs:58`
- **修法**：member 為 null 時仍對固定 dummy hash 做一次 `VerifyPassword`。
- **驗收**：單元測試驗證兩路徑皆呼叫 hasher 一次。
- **工時**：S

### M-10 `.env.example` 不完整 `[文件]`（✅ 已修正）
- 見上方「已修正」表。原建議「補 Captcha／Mock／限流選項」有誤：compose 並未轉入這些變數，列出會誤導；改為註解指引。

### M-11 `settings.local.json` 允許清單與規則矛盾（✅ 已修正）
- 見上方「已修正」表。

### M-12 全域 `~/.claude/CLAUDE.md` 殘留規則 `[文件]`
- **修法**：移除 Laravel 字樣、修正 token 預算為可遵守值或刪除。
- **工時**：S

### M-13 `web/README.md:51` 過時 `[文件]`（✅ 已修正）

### M-14 `deployment-prerequisites.md:3` 過時 `[文件]`（✅ 已修正）
- 仍留在 `docs/todo/`：VPS（compose）線尚未實測，待辦未全部完成。

### M-15 CHANGELOG／版本號缺失 `[文件]`
- **修法**：建 `CHANGELOG.md` 並打第一個 tag。
- **工時**：S

### M-16 Logout `[AllowAnonymous]` 騷擾向量 🤔
- **位置**：`src/ProjectC.WebApi/Controllers/AuthController.cs` Logout
- **修法**：要求同時帶有效 access token 且 `sub` 與 refresh token 擁有者一致；或接受並文件化。
- **工時**：S

### M-17 Graceful shutdown 未設定
- **修法**：`HostOptions.ShutdownTimeout`、K8s `terminationGracePeriodSeconds`、preStop sleep。
- **工時**:S

---

## 🔵 Low

### L-1 `docs/architecture-guide/index.html:419` 行號過時 `[文件]`（✅ 已修正）
- 只修了唯一明確失效的 `Program.cs:386`；其餘 `vscode://` 連結行號未逐一重對，導覽列已加註。

### L-2 CLAUDE.md graphify 「committed」敘述與 `.gitignore:500` 不符 `[文件]`（✅ 已修正）

### L-3 `.claude/settings.json` graphify 絕對路徑
- **修法**：改用 PATH 查找或相對 `~`。工時 S。

### L-4 LICENSE／SECURITY.md 缺失 `[文件]`
- 工時 S。

### L-5 映像未釘 digest
- **位置**：`deploy/api.Dockerfile:5`、`deploy/web.Dockerfile:4`
- 工時 S。

### L-6 `ticket-redemption` spec Purpose 佔位 `[文件]`（✅ 已修正）

### L-7 API 版本化策略 `[文件]`
- 記錄「單版本、破壞性變更即新路徑」決策即可。工時 S。

### L-8 nginx 無 gzip
- 工時 S。

### L-9 `.agents/`、`.claude/agents/`、`.devin/rules/` 三處並存
- **修法**：以腳本比對或指定單一來源。工時 S。

### L-10 本次審查雜目錄
- **位置**：`C:\AIArea\ProjectC\C:\Users\2110062\AppData\Local\Temp\prr-coverage\`
- **修法**：使用者手動刪除（審查者禁止刪檔）。工時 S。
