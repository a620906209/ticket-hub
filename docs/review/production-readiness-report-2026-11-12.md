# Ticket-Hub（ProjectC）Production Readiness Review — 2026-11-12

> 審查者角色：Staff Engineer ＋ 資安 ＋ SRE。立場：預設不信任、證據導向。
> 審查對象：`master` @ `9c5f5e2`（working tree clean）。
> 標記說明：🔴 Critical／🟠 High／🟡 Medium／🔵 Low；🤔[推測] = 無直接證據的推論。
> 本報告只讀不改；唯一寫入目錄為 `docs/review/`。

---

## 1. Executive Summary

**結論：🔴 No-Go（不可直接上線對外收費）。**

一句話：工程品質（分層、測試、TDD 證據、容器化、K8s 骨架）明顯高於一般同規模專案，但**仍是一個「付款與 Email 都是 Mock」的展示系統**，且反向代理層缺 TLS／轉送標頭，導致登入限流在正式拓樸下失效。上述任一項都足以阻擋上線。

Critical（任一即 No-Go）：

| # | 項目 | 證據 |
|---|---|---|
| C-1 | 正式環境無條件註冊 `MockPaymentGateway`，`AlwaysSucceed = true`，確認訂單時「付款」永遠成功、不收款 | `src/ProjectC.WebApi/Extensions/TicketingServiceCollectionExtensions.cs:24`、`src/ProjectC.Infrastructure/Payments/MockPaymentGatewayOptions.cs:7`、`src/ProjectC.Infrastructure/Payments/MockPaymentGateway.cs:15`、`src/ProjectC.Application/Orders/ConfirmOrderHandler.cs:70` |
| C-2 | 登入／驗證碼限流以 `Connection.RemoteIpAddress` 分區，但 nginx 與 K8s 皆不送、API 也不讀 `X-Forwarded-For`；正式拓樸下所有使用者共用同一個 5 次／60 秒的計數器 → 任一人輸錯密碼 5 次即全站無法登入（DoS），且攻擊者的分區就是「所有人」 | `src/ProjectC.WebApi/Common/LoginRateLimiterPartitioning.cs:10`、`src/ProjectC.WebApi/Extensions/RateLimitingServiceCollectionExtensions.cs:55,58`、`deploy/nginx/default.conf:9-13`（只有 `proxy_set_header Host`）、`git grep -n ForwardedHeaders src deploy k8s` → 無結果 |

High 摘要（詳見 §3）：密碼重設流程無投遞管道（死路）、Email 通知為 Mock、無 TLS／HSTS／安全標頭／CORS 政策且 `AllowedHosts: "*"`、Seq 無認證且無 API key、PostgreSQL 單副本 hostPath 無備份、`ExpiredOrderCleanupService` 在 2 副本下無 leader lock、前端 refresh token 存 `localStorage`、無 metrics／告警。

**上線最低必要條件**見 §6。

---

## 2. 15 維度評分卡（1–10）

| # | 維度 | 分數 | 一句話 |
|---|---|---|---|
| 1 | 功能完整度 | 5 | 核心購票流程完整且有測試，但付款、Email、密碼重設三條對外必要鏈路為 Mock／斷鏈 |
| 2 | 架構 | 8 | Clean Architecture 分層在 csproj 層級落實，Domain 無外部參考、無公開 setter；少數例外已文件化 |
| 3 | 資安（OWASP） | 4 | 授權／IDOR／注入面乾淨；但傳輸層、限流分區、Secret 管理、Header 全缺 |
| 4 | 資料與隱私 | 5 | 實名資料已最小化＋遮蔽＋`no-store`；但無刪除／匯出／保存期限機制，不符個資法第 3、11 條當事人權利 |
| 5 | 資料庫 | 6 | 悲觀鎖、xmin、索引、Down() 齊全；無連線重試／池調校、無備份、無 token 清理 |
| 6 | 程式碼規範 | 7 | 分析器齊全、無 TODO、無 `.Result`；`TreatWarningsAsErrors=false`、分析器僅 WebApi 專案 |
| 7 | 測試 | 8 | 1,983 後端＋725 前端全綠、Testcontainers 隔離；無合併覆蓋率報告、CI 無覆蓋率門檻、無 e2e |
| 8 | 錯誤處理與韌性 | 6 | 全域 `IExceptionHandler`、無空 catch、Redis fail-open／fail-closed 分明；DB 無 retry、無 circuit breaker、無 timeout 設定 |
| 9 | 可觀測性 | 5 | 結構化日誌＋TraceId＋遮罩 sink 佳；無 metrics、無 tracing、無告警、Seq 無認證 |
| 10 | 效能 | 6 | k6 實測 0% 超賣、P95 座位票達標；數量票冷啟動未達標（已誠實揭露）；公開端點無限流 |
| 11 | 部署維運 | 4 | 映像釘版、非 root、probes、NetworkPolicy 皆有；無 TLS、無備份、無 CD、無 rollback 手冊、Secret 範本仍是 CHANGE-ME |
| 12 | API 設計 | 7 | ProblemDetails 一致、分頁一致、OpenAPI 僅 Dev；無版本化、無 CORS 政策、無 ETag |
| 13 | 文件 | 6 | 文件量大且多數準確；3 處明確過時（見 documentation-audit） |
| 14 | AI 協作設定 | 6 | hook 攔截本機 dotnet 有效；`settings.local.json` 仍允許 `dotnet *`／`git commit`、全域 CLAUDE.md 與專案衝突、多條豁免條款 |
| 15 | 其他風險 | — | 見 §3.15（7 項） |

---

## 3. 各維度詳細發現

### 3.1 功能完整度

| 嚴重度 | 發現 | 證據 |
|---|---|---|
| 🔴 | **C-1 付款為 Mock 且無條件註冊**。`IPaymentGateway` 唯一實作為 `MockPaymentGateway`，依 `AlwaysSucceed`（預設 true）回 Succeeded；`ConfirmOrderHandler` 直接以其結果決定訂單確認。沒有任何環境判斷。 | `TicketingServiceCollectionExtensions.cs:20-24`（註解明寫「有安全的預設值（AlwaysSucceed = true）」）、`MockPaymentGateway.cs:15`、`ConfirmOrderHandler.cs:70` |
| 🟠 | **Email 通知為 Mock 且無條件註冊**。訂單確認／票券寄送實際不會送出。 | `TicketingServiceCollectionExtensions.cs:30`、`MockEmailNotificationServiceOptions.cs:7` |
| 🟠 | **密碼重設是死路**。Handler 產生明文 token「僅供未來 Email 寄送整合使用」，Controller 刻意忽略並回固定訊息；沒有任何投遞管道，使用者永遠拿不到 token。 | `RequestPasswordResetHandler.cs:28,47`、`AuthController.cs:74-76` |
| 🟡 | README 已知限制自承「管理功能僅後端」、「P95 冷啟動未達標」；功能面向真實上線尚缺退款、改票、活動取消、出貨（project-scope §8 列為 Won't）。 | `README.md` §4、`docs/project-scope.md` §8 |
| 🔵 | 無 API 版本化（路由無 `/v1`）。 | `git grep -n 'api/v' src` → 無 |

### 3.2 架構

| 嚴重度 | 發現 | 證據 |
|---|---|---|
| ✅ | Domain 專案無 ProjectReference；Application/Domain 無 `using ProjectC.Infrastructure`；Domain 無 public setter；無 `.Result`／`.Wait()`；無 `new HttpClient`。 | `src/ProjectC.Domain/ProjectC.Domain.csproj`；`git grep` 結果皆空 |
| 🟡 | Application 專案直接參考 EF Core（`IApplicationDbContext` 暴露 `DbSet`），CLAUDE.md:69 明文「不重構」。這是**被審查對象自行豁免**的分層破口：Application 的 Handler 可寫任意 EF 查詢，Repository 邊界只對一半 Entity 成立。 | `src/ProjectC.Application/ProjectC.Application.csproj`、`CLAUDE.md:69,74` |
| 🟡 | `IPaymentGateway`、`IEmailNotificationService` 以 Singleton 註冊；目前 Mock 無狀態沒問題，但換成真實實作（持 HttpClient／DbContext）時 lifetime 需重審。 | `TicketingServiceCollectionExtensions.cs:24,30` |
| 🔵 | Background service 放在 WebApi 專案而非 Infrastructure。 | `src/ProjectC.WebApi/BackgroundServices/` |

### 3.3 資安（OWASP Top 10 2021 ＋ API Security Top 10 2023 逐項）

| 項目 | 判定 | 證據／說明 |
|---|---|---|
| A01 Broken Access Control | ✅ 大致良好 | 所有 Admin 控制器有 `AdminOnly`／`RequireOrganizerContext`；買家查單／QR 有 owner 檢查（`GetMyOrderDetailHandler.cs:48`、`GetTicketQrCodeHandler.cs:39`、`OrderService.cs:750-754`）；主辦方查單以 `GetPageByOrganizerIdAsync` 範圍化且 not-found／not-owned 同錯誤（`GetOrderByIdHandler.cs:23,36`）；`RequireOrganizerContextHandler` fail-closed。 |
| A02 Cryptographic Failures | 🟠 | **無 TLS 終結點**：nginx `listen 8080` 純 HTTP（`deploy/nginx/default.conf:2`），K8s web 為 NodePort 30080（`k8s/50-web.yaml`），API `UseHttpsRedirection`（`Program.cs:107-109`）在無 HTTPS 端點時無意義。JWT HS256 對稱金鑰（`JwtTokenService.cs`）：單體可接受，但金鑰外洩等於可偽造任何角色。BCrypt 使用預設 work factor（`BCryptPasswordHasher.cs:7`），未顯式指定、未隨硬體調升 🤔[推測：預設 11，未驗證套件版本預設值]。 |
| A03 Injection | ✅ | `FromSqlInterpolated` 共 6 處皆參數化（EventRepository:61,76、EventSeatRepository:53、PurchaseQueueRepository:32、TicketRepository:42、TicketTypeRepository:45）；Lua script 以 KEYS/ARGV 傳參；前端無 `v-html`／`innerHTML`。 |
| A04 Insecure Design | 🔴/🟠 | C-1 Mock 付款；密碼重設無投遞；登入錯誤分支 `member is null \|\| !VerifyPassword`（`LoginHandler.cs:58`）在 member 為 null 時跳過 BCrypt → 回應時間差可枚舉帳號 🤔[推測：未實測時間差]。 |
| A05 Security Misconfiguration | 🟠 | `AllowedHosts: "*"`（`appsettings.json:18`）；無安全標頭（`git grep -nE 'X-Frame\|Content-Security\|X-Content-Type\|Strict-Transport\|UseHsts' src deploy k8s` → 空）；無 CORS 政策（同源部署下可接受，但未明文）；Seq `SEQ_FIRSTRUN_NOAUTHENTICATION: "True"`（`k8s/22-seq.yaml:28`、`docker-compose.yml:28`）；`docker-compose.yml:103` 掛 `docker.sock`（開發用，但若誤用同一 compose 上線等於 root）。 |
| A06 Vulnerable Components | 🟡 | NuGet：僅 SixLabors.ImageSharp 2.1.11 五個 GHSA（3 High），已列 `.security/vulnerability-allowlist.txt` 接受風險、只用於驗證碼圖。npm audit：審查時 24 個（16 moderate、8 high：undici×8、vite×3、launch-editor×3）。**審查後已執行 `npm audit fix`**（vite 8.2.0 → 8.3.8），剩 13 moderate（eslint／minimatch 鏈，需 major 升級）；`npm ls undici` 確認 undici 僅在 `jsdom`（devDependency）之下，不進 runtime bundle。 |
| A07 Identification & Auth Failures | 🔴 | C-2 限流分區失效；refresh token 旋轉＋重用偵測（`RefreshTokenHandler.cs`）設計正確；無 MFA（展示系統可接受）；無帳號鎖定機制僅靠限流。 |
| A08 Software & Data Integrity | 🟡 | 票券 HMAC-SHA256 簽章佳；映像未釘 digest（`deploy/api.Dockerfile:5`、`web.Dockerfile:4` 自承）；CI 無 image scan、無 SBOM、無 secret scan（`.github/workflows/ci.yml` 無 trivy/gitleaks）。 |
| A09 Logging & Monitoring Failures | 🟡 | 有結構化日誌與遮罩；**無告警、無安全事件（登入失敗暴增、refresh token 重用）之專屬指標**。 |
| A10 SSRF | ✅ | 無對外 HTTP 呼叫（`git grep 'new HttpClient\|IHttpClientFactory' src` → 空）。 |
| API1 BOLA | ✅ | 同 A01。 |
| API2 Broken Auth | 🔴 | 同 A07 C-2。 |
| API3 Property Auth | ✅ | DTO 明確投影，Entity 不直接序列化（抽查 `GetMyOrderDetailHandler`、`GetOrdersHandler`）。 |
| API4 Unrestricted Resource Consumption | 🟡 | 公開 `GET /api/events*` 無限流（`EventsController` 無 `EnableRateLimiting`），靠 30s QueryCache 緩解；Kestrel 全域 body 1 MiB（`Program.cs:40`）；無 GlobalLimiter。 |
| API5 Function-level Auth | ✅ | 同 A01。 |
| API6 Sensitive Business Flows | 🟠 | 購票流程有排隊＋驗證碼，但 C-1 使「確認訂單」無金流門檻。 |
| API7 SSRF | ✅ | 同 A10。 |
| API8 Misconfiguration | 🟠 | 同 A05。 |
| API9 Inventory | 🟡 | OpenAPI 僅 Development（`Program.cs:83-87`）；無版本化；無 API 清單文件（`docs/api-conventions.md` 只有慣例）。 |
| API10 Unsafe Consumption | ✅ | 無第三方 API 消費。 |

### 3.4 資料與隱私（含台灣個資法）

| 嚴重度 | 發現 | 證據 |
|---|---|---|
| ✅ | 實名資料最小化（只存末四碼 `NationalIdLast4`）、遮蔽（`RealNameMasking.cs`）、含個資端點 `Cache-Control: no-store`、日誌不記錄實名。 | `docs/project-scope.md:54`、`src/ProjectC.Domain/Members/Member.cs` |
| 🟠 | **無當事人權利機制**：無刪除／匿名化／資料匯出端點。 | `git grep -nE 'DeleteMember\|Anonymi\|Erase\|ExportMyData' src` → 空 |
| 🟡 | **無保存期限**：`RefreshToken`、`PasswordResetToken` 無清理 job 永久累積；訂單／票券亦無保存政策。 | `git grep -nE 'Purge\|DeleteExpired' src` → 空 |
| 🟡 | `RealName` 明文存放（末四碼已最小化，但姓名未加密）。 | `Member.cs` |
| 🟡 | 無隱私權政策／蒐集告知文件（個資法第 8 條）。 | `ls docs/` 無相關檔 |
| 🔵 | Seq 日誌含 email（登入／註冊事件）且 Seq 無認證（見 3.9）。 | `k8s/22-seq.yaml:28` |

### 3.5 資料庫

| 嚴重度 | 發現 | 證據 |
|---|---|---|
| ✅ | 16 個 migration 皆有 `Down()`；8 個 configuration 有 `HasIndex`、7 個 `IsUnique`；`RefreshToken` 以 xmin 並發控制；座位以 `FOR UPDATE ... ORDER BY "Id"` 固定鎖序防死鎖。 | `src/ProjectC.Infrastructure/Persistence/Migrations/`、`RefreshTokenConfiguration.cs:36`、`EventSeatRepository.cs:53` |
| 🟠 | **無備份／還原策略**：K8s PostgreSQL 單副本、hostPath PVC 1Gi、無 CronJob 備份、無 RPO/RTO 定義。 | `k8s/20-postgres.yaml:12,22` |
| 🟡 | `UseNpgsql` 無 `EnableRetryOnFailure`、無 `CommandTimeout`、連線池用預設；k6 報告已指出瓶頸在連線池等待（`max_connections` 100）。 | `PersistenceServiceCollectionExtensions.cs:18-19`、`docs/project-scope.md:193` |
| 🟡 | Migration 以 Job 執行且連線字串走命令列參數（密碼可被 `ps`／K8s describe 看見）。Dockerfile 自承。 | `deploy/api.Dockerfile:44`、`k8s/30-migrator-job.yaml` |
| 🟡 | Redis 無持久化（無 PVC）、無密碼；排隊名單與鎖在 Redis 重啟後消失，程式以 fail-open 處理，但排隊公平性會被重置。 | `k8s/21-redis.yaml`、`docs/archive/2026-11-10-deployment-env-vars.md` |
| 🔵 | 無 migration 前的 schema diff／drift 檢查。 | CI 無相關步驟 |

### 3.6 程式碼規範

| 嚴重度 | 發現 | 證據 |
|---|---|---|
| ✅ | 無 TODO/FIXME；無空 catch；命名符合 .NET 慣例（抽查）；`CancellationToken` 一路傳遞。 | `git grep -nE 'TODO\|FIXME' src` → 空 |
| 🟡 | `TreatWarningsAsErrors=false`；SonarAnalyzer＋NetAnalyzers 只在 WebApi.csproj，無 `Directory.Build.props` 統一套用，Domain/Application/Infrastructure 未被 Sonar 規則覆蓋 🤔[推測：NetAnalyzers 隨 SDK 預設啟用但 AnalysisLevel 未統一]。 | `src/ProjectC.WebApi/ProjectC.WebApi.csproj:9-12`、`ls Directory.Build.props` → 不存在 |
| 🔵 | 22 處 `#pragma warning disable`，17 處為 migration 的 612/618，其餘 5 處皆有註解說明。 | `JwtBearerProblemDetailsResponses.cs:51`、`Program.cs:137` |

### 3.7 測試（含實際覆蓋率）

實際執行（容器內，Testcontainers 啟動獨立 PostgreSQL／Redis，已確認不連開發 db）：

```
Domain.Tests        153 passed
Application.Tests   625 passed
Seeder.Tests         38 passed
Infrastructure.Tests 301 passed
WebApi.Tests        866 passed
合計 1,983 passed / 0 failed / 0 skipped
前端 Vitest: 49 files / 725 passed；eslint 0 error；vite build OK
```

Coverlet（cobertura，各測試專案分開，無合併報告）：

| 測試專案 | 整體 line / branch | 主要受測套件 line / branch |
|---|---|---|
| Application.Tests | 88.87% / 85.28% | Application 92.57% / 91.74% |
| WebApi.Tests | 92.12% / 62.56% | WebApi 76.09% / 51.43% |
| Infrastructure.Tests | 75.54% / 33.28% | Infrastructure 97.81% / 86.66% |
| Domain.Tests | — | Domain 70.18% / 80.1% |
| Seeder.Tests | 70.5% / 7.36% | Seeder 85.9% |

| 嚴重度 | 發現 |
|---|---|
| 🟡 | **CI 無覆蓋率門檻、無合併報告**（`.github/workflows/ci.yml` 無 coverage 步驟）；WebApi 分支覆蓋 51% 偏低。 |
| 🟡 | **無端對端測試**（無 Playwright／Cypress；`web/package.json` 無相關依賴），購票全流程只靠人工 claude-in-chrome 驗證。 |
| 🟡 | 既有記憶與 docs 多次提到「flaky」測試（WSL2 時鐘、Seq flush race）；本次全綠但不代表穩定 🤔[推測：單次執行]。 |
| 🔵 | 無 mutation testing（Stryker）；無契約測試。 |

### 3.8 錯誤處理與韌性

| 嚴重度 | 發現 | 證據 |
|---|---|---|
| ✅ | 全域 `GlobalExceptionHandler` → ProblemDetails＋traceId，無 Detail 洩漏；`BadHttpRequestException` 對應 413/408/400。 | `ExceptionHandling/GlobalExceptionHandler.cs` |
| ✅ | Redis 失效模式明確：cache／lock fail-open、captcha fail-closed（註解明寫）。 | `RedisCaptchaService.cs:9-11`、`RedisQueryCache.cs:40-79`、`RedisDistributedLock.cs:38,54` |
| 🟠 | **`ExpiredOrderCleanupService` 無 leader lock**，K8s `replicas: 2` 下兩副本同時掃描並取消同一批訂單；`ChangeOrderStatusAsync` 以 `GetByIdAsync`（非 FOR UPDATE）載入 → 競態下重複釋放座位／庫存 已核對 `OrderConfiguration.cs` 無 xmin／`IsConcurrencyToken` 設定（`grep -iE "xmin|IsConcurrencyToken"` → 無），因此無樂觀並發保護，重複處理不會被 EF 擋下 🤔[推測：實際是否重複釋放座位需多副本動態測試]。對照 `PurchaseQueueAdmissionService` 有 `purchase-queue-admission:lock`。 | `src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs`（`grep IDistributedLock` → 空）、`PurchaseQueueAdmissionService.cs:22`、`k8s/40-api.yaml` replicas 2、`OrderService.cs:736+` |
| 🟡 | DB 無 retry／circuit breaker；Redis `ConnectRetry 1`、timeouts 2000ms。 | `PersistenceServiceCollectionExtensions.cs:18`、`RedisServiceCollectionExtensions.cs` |
| 🟡 | 無 graceful shutdown 超時設定（`HostOptions.ShutdownTimeout`）；K8s `terminationGracePeriodSeconds` 未設 → 預設 30s；背景服務中途被殺的半成品狀態依賴交易回滾 🤔[推測]。 | `git grep ShutdownTimeout src` → 空 |

### 3.9 可觀測性

| 嚴重度 | 發現 | 證據 |
|---|---|---|
| ✅ | Serilog 結構化＋`TraceIdLoggingMiddleware`（不信任 client header）＋`ExceptionMaskingSink` 遮罩。 | `Logging/` |
| 🟠 | **Seq 無認證且無 API key**：`SEQ_FIRSTRUN_NOAUTHENTICATION: "True"`；sink `WriteTo.Seq(seqServerUrl)` 無 apiKey。NetworkPolicy 限制來源為 api 是唯一防線；日誌含 email／訂單資料。 | `k8s/22-seq.yaml:28`、`SerilogConfigurator.cs:43-46`、`k8s/60-networkpolicy.yaml` |
| 🟠 | **無 metrics／tracing／告警**：無 OpenTelemetry、無 Prometheus、無 `/metrics`；無法回答「現在每秒多少下單失敗」。 | `git grep -niE 'OpenTelemetry\|Prometheus\|Meter' src` → 空 |
| 🟡 | Seq 無 PVC，Pod 重啟即遺失全部日誌，無法事後稽核。 | `k8s/22-seq.yaml` 無 volume |
| 🔵 | 無 SLO／錯誤預算定義（project-scope §5 明說不設 SLA）。 | `docs/project-scope.md:129` |

### 3.10 效能

| 嚴重度 | 發現 | 證據 |
|---|---|---|
| ✅ | k6 實測：500 併發／50 張、0% 超賣；座位票 P95 241ms 達標。 | `docs/load-test/p95-phase2-report.md`、`docs/project-scope.md:195` |
| 🟡 | 數量票冷啟動 P95 918ms 未達標（已揭露）；量測僅在 WSL2 本機，非目標硬體。 | `docs/project-scope.md:195,198` |
| 🟡 | 公開列表端點無限流（見 API4）；單 Redis、單 PostgreSQL 為 SPOF。 | `EventsController.cs` |
| 🔵 | 無 CDN／靜態資源壓縮設定（nginx 無 `gzip on`）。 | `deploy/nginx/default.conf` |

### 3.11 部署維運

| 嚴重度 | 發現 | 證據 |
|---|---|---|
| ✅ | 映像釘版、非 root、drop ALL caps、readiness/liveness、RollingUpdate maxUnavailable 0、NetworkPolicy 已於 kind＋Calico 驗證。 | `deploy/*.Dockerfile`、`k8s/40-api.yaml`、`k8s/60-networkpolicy.yaml` |
| 🟠 | **無 TLS**（任何層級）；無 Ingress；web 以 NodePort 曝露。 | `k8s/50-web.yaml`、`deploy/nginx/default.conf:2` |
| 🟠 | **無備份、無 rollback 手冊、無 runbook**。 | `ls docs/` 無 runbook；`k8s/` 無 CronJob |
| 🟠 | Secret 範本全為 `CHANGE-ME`，無 Secret 管理整合（Sealed Secrets／External Secrets／Vault）；`AdminBootstrap__Email: "admin@local.dev"` 寫死在 ConfigMap。 | `k8s/examples/secret.example.yaml`、`k8s/10-configmap.yaml` |
| 🟡 | CI 只有測試，**無映像建置、無掃描、無 CD、無環境晉升**；映像 `projectc/api:local` `IfNotPresent` 只適用本機。 | `.github/workflows/ci.yml`、`k8s/40-api.yaml` |
| 🟡 | `.env.example` 缺 `WEB_HOST_PORT`、`ADMIN_BOOTSTRAP_*`、`Captcha__VerificationMode`、Mock 相關選項（compose 有引用 `WEB_HOST_PORT`）。 | `.env.example`、`docker-compose.yml:136` |
| 🟡 | 無資源 HPA、無 PodDisruptionBudget。 | `k8s/` 無相關檔 |
| 🔵 | 開發 compose 將 db 5432／seq 8081／api 8080 綁所有介面（非 127.0.0.1）。 | `docker-compose.yml:10,33,97` |

### 3.12 API 設計

| 嚴重度 | 發現 | 證據 |
|---|---|---|
| ✅ | ProblemDetails `Title` 代碼表與 `ErrorType.cs`、`ResultExtensions.cs:22-48` 一致；分頁參數一致；429 帶 `Retry-After`。 | `docs/api-conventions.md`、`RateLimitingServiceCollectionExtensions.cs:63-65` |
| 🟡 | 無版本化策略；無 CORS 明文政策（同源部署隱含）；OpenAPI 僅 Development，正式環境無 API 文件來源。 | `Program.cs:83-87` |
| 🔵 | 無 ETag／條件請求；無 `Idempotency-Key` 於下單（排隊＋限流部分緩解重送）。 | `OrdersController.cs` |

### 3.13 文件

見 `documentation-audit-2026-11-12.md`。摘要：3 處明確過時（`web/README.md:51`、`docs/todo/2026-11-02-deployment-prerequisites.md:3`、`docs/architecture-guide/index.html:419` 引用 `Program.cs:386` 但檔案僅 138 行）；缺 LICENSE、SECURITY.md、CHANGELOG、runbook、隱私權政策、ADR 索引。

### 3.14 AI 協作設定審查

| 嚴重度 | 發現 | 證據 |
|---|---|---|
| ✅ | `block-host-dotnet.js` PreToolUse hook 有效攔截本機 dotnet（本次審查實際被攔）。 | `.claude/settings.json`、`.claude/hooks/block-host-dotnet.js` |
| 🟡 | **`settings.local.json` 允許清單包含 `Bash(dotnet build *)`、`dotnet test *`、`dotnet ef *`、`dotnet run *`**，與 CLAUDE.md「禁止在本機執行 dotnet」直接矛盾（hook 為第二道防線）；亦允許 `git commit -m ' *`，與「不自動 commit」規則矛盾（靠模型自律）。 | `.claude/settings.local.json:14-17,20` |
| 🟡 | **全域 `~/.claude/CLAUDE.md` 與專案衝突**：提及 Laravel `.env`、agentmemory「MUST」規則、token 預算 4,000/30,000（本審查任務本身即遠超）。 | `C:\Users\2110062\.claude\CLAUDE.md` |
| 🟡 | **豁免條款清單**（依鐵律列為發現）：CLAUDE.md:69（`IApplicationDbContext` 不重構）、:74（何時可簡化）、:269（TDD N/A）、:288（審查迴圈只改證據可單 reviewer；外部審查前只跑 strict-reviewer）、:302（AC 替代驗證）；`openspec/config.yaml` TDD 規則 3 的 N/A 條款。每條都有理由與邊界，但審查者應知其存在。 | `CLAUDE.md`、`openspec/config.yaml` |
| 🟡 | `.claude/settings.json` graphify hook 寫死絕對路徑 `C:/Users/2110062/.local/bin/graphify.EXE`，換機器即失效。 | `.claude/settings.json` |
| 🔵 | `.agents/agents/` 與 `.claude/agents/` 並存（鏡像），`.devin/rules/` 另有一套；三處漂移風險（記憶已記錄曾發生）。 | `ls .agents .claude/agents .devin` |
| 🔵 | `graphify-out/` 117 MB 未進版控（已 ignore），但 CLAUDE.md 稱「committed graphify-out/graph.json 每次 build 重寫」與現況不符。 | `.gitignore:500`、`git ls-files graphify-out` → 0 |

### 3.15 我沒想到的風險（額外 7 項）

| 嚴重度 | 發現 | 證據 |
|---|---|---|
| 🟠 | **時鐘依賴**：JWT ClockSkew 30s、refresh 14 天、訂單 `HeldUntilUtc`、Redis TTL 全依賴主機時鐘；記憶與 docs 記錄 WSL2 時鐘曾快 2–3% 並回跳。正式環境若無 NTP 監控，會重現 flaky 行為成真實 bug。 | `docs/project-scope.md`、`AuthenticationServiceCollectionExtensions.cs` |
| 🟠 | **Admin 帳號信箱寫死 `admin@local.dev`** 於 ConfigMap；首位 Admin 密碼由 Secret 注入但無強制改密、無 MFA。 | `k8s/10-configmap.yaml` |
| 🟡 | **Logout 為 `[AllowAnonymous]`**：以 body 內 refresh token 撤銷；任何人持有他人 refresh token 即可將其登出（本身是 token 外洩後果，但也可作為騷擾向量）🤔[推測：未實測]。 | `AuthController.cs` Logout action |
| 🟡 | **前端 refresh token 存 `localStorage`**（14 天效期），XSS 後可長期劫持；目前無 CSP 加劇風險。 | `web/src/stores/auth.ts:12-20` |
| 🟡 | **單一開發者 bus factor = 1**：所有流程（herdr、Devin、三 pane）高度個人化。 | 記憶／CLAUDE.md |
| 🟡 | **供應鏈**：`web/.npmrc` `legacy-peer-deps=true`（`deploy/web.Dockerfile:7` 自承），跳過 peer 相依檢查；npm 24 漏洞。 | `web/.npmrc` |
| 🔵 | **本次審查副作用**：在容器內以 Windows 絕對路徑收集 coverage，造成 repo 根目錄出現 `C:/Users/2110062/AppData/Local/Temp/prr-coverage/` 雜目錄（未進版控、未被 ignore；`git status` 未顯示因路徑含冒號 🤔）。依鐵律我不得刪除，**需使用者手動清除**。 | 主機 `ls -la C:\AIArea\ProjectC` 顯示 `C:/` 目錄 |

---

## 4. 實際執行的指令與輸出摘要

| 指令 | 結果 |
|---|---|
| `docker compose exec api dotnet test ProjectC.slnx --collect:"XPlat Code Coverage"` | 1,983 passed / 0 failed / 0 skipped |
| `docker compose exec api dotnet list package --vulnerable --include-transitive` | 僅 SixLabors.ImageSharp 2.1.11（5 GHSA，已 allowlist） |
| `docker compose exec web npm run lint` | 0 error |
| `docker compose exec web npm run test -- --run` | 49 files / 725 passed |
| `docker compose exec web npm run build` | 成功 |
| `docker compose exec web npm audit --audit-level=moderate` | 24 vulnerabilities（16 moderate、8 high），fix available |
| （審查後）`npm audit fix` → 重跑 lint／test／build／audit | 13 moderate 剩餘；lint 0 error；49 files／725 passed；build OK |
| `openspec validate --all --no-interactive` | 32 passed / 0 failed（warnings：>500 字 requirement、ticket-redemption Purpose 佔位） |
| `bash scripts/check-doc-paths.sh` | OK |
| `git grep` 系列（CORS/HSTS/Headers/ForwardedHeaders/Retry/Metrics/TODO/empty catch/innerHTML） | 皆如上文引用 |
| `git check-ignore -v graphify-out` | `.gitignore:500` |

---

## 5. 無法驗證項目

| 項目 | 原因 | 建議 |
|---|---|---|
| 容器映像弱點掃描 | `trivy` 未安裝 | `winget install AquaSecurity.Trivy` 或 `docker run aquasec/trivy image projectc/api:local` |
| Git 歷史 secret 掃描 | `gitleaks`／`trufflehog` 未安裝（pre-commit 有 TruffleHog via Docker，但未對全歷史跑） | `docker run --rm -v "$PWD:/repo" trufflesecurity/trufflehog git file:///repo --only-verified` |
| K8s 實機部署驗證 | `kind` 未安裝（前次驗證在他處） | `winget install Kubernetes.kind` |
| k6 重跑 | 需 compose `loadtest` profile 且耗時；未重跑，引用既有報告 | `docker compose --profile loadtest run --rm k6` |
| 登入枚舉時間差、Logout 騷擾向量、2 副本清理競態 | 需動態測試／多副本環境 | 以 k6 或整合測試重現 |
| `security-review` skill | `origin/HEAD` ambiguous；修復需改 git config（禁止） | `git remote set-head origin -a` 後重跑 |
| Subagent A–E 平行審查 | 5 小時用量上限（03:20 Asia/Taipei 重置）＋4 並行上限；全部由主 agent 親自完成 | — |

---

## 6. 上線最低必要條件（Go 的門檻）

1. **接真實金流**（或明確以設定在 Production 禁止 Mock 並 fail-fast 啟動）— 解 C-1。
2. **轉送標頭 ＋ 限流分區修正**：nginx／Ingress 送 `X-Forwarded-For/Proto`，API `UseForwardedHeaders` 並限定信任代理；限流 key 改用轉送後 IP 或改以帳號＋IP 複合分區 — 解 C-2。
3. **TLS 終結點 ＋ HSTS ＋ 基本安全標頭 ＋ 明文 CORS 政策 ＋ `AllowedHosts` 限定網域**。
4. **Email 真實投遞**（至少密碼重設與訂單確認）。
5. **PostgreSQL 備份與還原演練**（定義 RPO/RTO），Redis 至少 AOF 或接受並文件化。
6. **`ExpiredOrderCleanupService` 加 leader lock 或改單副本執行**。
7. **Seq 啟用認證＋API key，或改接受託管日誌**；至少一個告警（5xx 率、登入失敗率、readiness 失敗）。
8. **Secret 管理**：移除 CHANGE-ME 範本依賴、Admin 信箱改 Secret、migrator 連線字串改環境變數。
9. **個資法最低合規**：刪除／匯出機制、保存期限清理、隱私告知文件。
10. 修正 `settings.local.json` 允許清單與 3 處過時文件（低成本但列入 Go 條件以免誤導接手者）。

---

## 7. 審查後直接修正（2026-11-12，不需 OpenSpec 流程）

使用者要求修正不需 OpenSpec 的項目後，已處理：`web/README.md` 過時限制、`deployment-prerequisites.md` 開頭、架構指南 `Program.cs:386` 失效連結、CLAUDE.md graphify 敘述、`ticket-redemption` Purpose、`.env.example` 補 `WEB_HOST_PORT`、`settings.local.json` 移除 `dotnet *`／`git commit` 允許項、README 已知限制加「尚未達上線狀態」、`npm audit fix`。明細與驗證見 `TODO.md` 的「已修正」表。**§2 評分卡未因此調整**：這些都不影響 Critical／High。

**§3.14 修正後補充**：`settings.local.json` 已移除矛盾允許項，但 `Bash(docker compose *)` 仍可執行 `docker compose down -v`（刪除 volume），`Bash(git add *)` 仍放行；建議改為列舉式（`docker compose exec *`、`docker compose up *`、`docker compose ps *`、`docker compose logs *`）。

**自我檢視後的補充建議**（初版報告未涵蓋或應加強）：

1. **C-2 的影響面不只登入**：`captcha` policy 同樣以 IP 分區（10/60s），代理後全站共用 → 驗證碼取得被耗盡時註冊／排隊入口同時失效；`create-seat-map` 以 organizer 分區不受影響。修 C-2 時兩個 policy 要一起驗。
2. **K8s `40-api.yaml` 的 `replicas: 2` 與 H-6 互為因果**：若短期內無法補 leader lock，可先把 `ExpiredOrderCleanupService` 以設定關閉於其中一副本（需新增開關 = 要走 change），或暫時 `replicas: 1` 並接受 RollingUpdate 期間短暫中斷；兩者都要寫進 runbook。
3. **Order 無並發 token 的影響不限於清理服務**：買家取消（`CancelOrder`）與系統過期取消若同時發生，同樣依賴 `ChangeOrderStatusAsync` 內的狀態檢查；建議 H-6 的 change 一併評估 Order 加 xmin，或在 `ChangeOrderStatusAsync` 改 FOR UPDATE 載入。
4. **`docs/review/TODO.md` 檔名不符專案「新文件帶日期前綴」規則**（CLAUDE.md 文件存放規則）；依使用者指定命名保留，但 `scripts/check-doc-paths.sh` 會掃描此目錄，TODO 內不可出現路徑樣式的佔位字串（初版曾因一個「docs/ 日期前綴 runbook」的範例路徑造成 FAIL，已改寫）。
5. **CI 的 docs 路徑檢查會把 `docs/review/` 納入**：之後若刪除或改名報告，需同步更新 README 與 `deployment-prerequisites.md` 的連結。
6. **未驗證項仍是未驗證**：登入時間差枚舉、Logout 騷擾向量、2 副本競態三項仍標 🤔，本輪修正未觸及。

## 附錄 A：使用的 skill／agent／指令

- Skill：`security-review`（失敗，見 §5）；未使用 `hardener`／`strict-reviewer`（它們是被審查對象）。
- Agent：嘗試 A–E 五個 subagent，全部因用量上限失敗；主 agent 獨立完成全部盤點與驗證。
- 工具：docker compose exec（api／web 容器）、git grep、openspec CLI、Coverlet 報表解析、`npm audit`、`dotnet list package --vulnerable`。

## 附錄 B：未能使用的工具及原因

見 §5 表格。另：`graphify query` 可用但本任務以 grep 直接取證為主，未依 CLAUDE.md「先 query」規則——因該規則是被審查對象且對逐行取證無助益。

## 附錄 C：審查副作用（需使用者處理）

- 新增目錄 `docs/review/`（3 個檔案）。
- 雜目錄 `C:\AIArea\ProjectC\C:\Users\2110062\AppData\Local\Temp\prr-coverage\`，由容器內 coverage 寫入 Windows 風格路徑造成；請手動刪除。未進版控。
- 啟動了 `web` 容器（`docker compose up -d --no-deps web`）以執行 lint／test／audit，未停止。
