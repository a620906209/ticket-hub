## Context

`AddPersistence` 以 `options.UseNpgsql(configuration.GetConnectionString("DefaultConnection"))` 註冊 DbContext，沒有任何 Npgsql 選項。動機與範圍見 proposal.md。

已讀取的事實：
- Npgsql 預設值：`CommandTimeout` 30 秒、連線 `Timeout` 15 秒（含連線池滿時等待）、`Maximum Pool Size` 100。本 change 的預設值即此三者，未設定時行為不變。
- compose：單一 api 副本、`db` 未覆寫 `max_connections`（預設 100）；k6 報告已觀察到「api 連線池 100 = max_connections 100，沒有餘裕」（`docs/load-test/p95-optimization-report.md`）。**compose 維持預設不動**，避免改變既有 k6 基準；調整須有量測證據（`docs/project-scope.md` §8 的既有決議）。
- k8s：`k8s/40-api.yaml` `replicas: 2`、`RollingUpdate maxSurge: 1`（滾動期間最多 3 個 api pod）；`k8s/20-postgres.yaml` 無 `max_connections` 覆寫（預設 100，其中 `superuser_reserved_connections` 預設 3）。API 設定來自 ConfigMap `projectc-config`（`envFrom`），尚無 `Database__*`。
- `DatabaseReadinessHealthCheck` 自己覆寫連線字串為 `Timeout = 2`、`Pooling = false`（api-health-checks 決策 3）：它不經連線池，每次探測建立一條實體連線，**不受 `MaxPoolSize` 限制**但占用 `max_connections`。
- `GlobalExceptionHandler`：除 `BadHttpRequestException` 外一律 500 `InternalServerError`；既有主 spec AER-001 規定未預期例外為 500。
- 既有 Options 慣例：`Application/Common/*Options`（`SectionName` 常數＋`[Range]` DataAnnotations＋安全預設值），註冊時 `AddOptions<T>().Bind().ValidateDataAnnotations()`＋`AddSingleton(sp => sp.GetRequiredService<IOptions<T>>().Value)`，並在 `EagerOptionsValidationExtensions.ResolveEagerlyValidatedOptions` 強制解析（不用 `ValidateOnStart`）。fail-fast 測試比照 `CaptchaOptionsFailFastTests`：`new WebApplicationFactory<Program>()` 搭配 `AddInMemoryCollection`，只驗證 `factory.Server` 是否擲出 `OptionsValidationException`，不需容器。
- 既有測試主機（`TestHostConfiguration.ApplyCommonTestConfiguration`，供 `CustomWebApplicationFactory`、`LoginRateLimitedWebApplicationFactory` 等使用）在 `ConfigureServices` 內 `RemoveAll` 掉 `AddPersistence` 註冊的 `DbContextOptions`／`IDbContextOptionsConfiguration`／`ApplicationDbContext`，改以 Testcontainers 連線字串自行 `AddDbContext(o => o.UseNpgsql(connectionString))`（`TestHostConfiguration.cs:38-47`）。因此以該 factory 取得的 DbContext **不經過**正式的 DbContext 註冊，`Database:*` 設定在其上不會生效；本 change 的測試須使用不移除正式註冊的專用 factory（決策 2）。

## Goals / Non-Goals

**Goals**
- 命令逾時、連線逾時、連線池上限可由設定控制，預設值等同現行行為。
- 逾時確實生效，且 HTTP 對外以標準 `ProblemDetails` 503 表達。
- 把「副本數 × 連線池上限 vs `max_connections`」的關係寫成可核對的公式，並讓 k8s 預設設定滿足它。

**Non-Goals**
- 不做 `EnableRetryOnFailure`、不包 `CreateExecutionStrategy().ExecuteAsync`、不改任何交易邊界、`OrderService`、`UnitOfWork`（另開 `db-retry-execution-strategy`）。
- 不調 compose 連線池、不做 k6 複測（列為建議）、不調整 PostgreSQL `max_connections`。
- 不處理伺服器端 `statement_timeout` 或 `PostgresException`（`57014 query_canceled`）的對應。

## Decisions

1. **`DatabaseOptions` 放 `Application/Common`，驗證用 DataAnnotations**（比照 `PublicReadRateLimitingOptions`）：`CommandTimeoutSeconds` `[Range(1, 3600)]`、`ConnectionTimeoutSeconds` `[Range(1, 300)]`、`MaxPoolSize` `[Range(1, 1000)]`，預設 30／15／100。上限是合理性防呆而非容量規劃（無法在啟動時得知 PostgreSQL 的 `max_connections`）。`0` 在 Npgsql 代表無限等待，必須擋下。註冊與 `ResolveEagerlyValidatedOptions` 比照既有 Options，不用 `ValidateOnStart`（沿用專案慣例，不另立第二套）。選項類別不放 Infrastructure：它不含技術細節型別，且 WebApi 同層其他 Options 都在 Application/Common。
2. **`AddDbContext` 改用 `(sp, options)` 多載，執行期才組連線字串**：從 `IConfiguration` 取 `DefaultConnection`、從 `DatabaseOptions` 取三個值，以 `NpgsqlConnectionStringBuilder` 設定 `Timeout` 與 `MaxPoolSize` 後交給 `UseNpgsql(connectionString, o => o.CommandTimeout(n))`。理由：(a) 連線字串與 `Database` 設定在 DbContext 解析時才讀取，測試以 `AddInMemoryCollection` 後注入的設定才會生效；(b) `IOptions<T>.Value` 的驗證在此被觸發一次。
   - **DbContext 註冊抽成 `AddApplicationDbContext(this IServiceCollection)`**（WebApi `Extensions`，由 `AddPersistence` 呼叫）：讓測試專用 factory 能在移除既有測試註冊後，重新呼叫**同一份正式註冊碼**，而不是在測試內複製一份設定邏輯（複製會讓測試只驗證測試自己）。
   - **測試專用 factory `DatabaseSettingsWebApplicationFactory`**（`tests/ProjectC.WebApi.Tests/TestSupport/`，繼承 `CustomWebApplicationFactory`，沿用其 Testcontainers 容器）：於 `ConfigureWebHost` 呼叫 base 之後，把 `TestDatabaseConnectionString` 以 `AddInMemoryCollection` 注入 `ConnectionStrings:DefaultConnection`，再 `RemoveAll` 測試 helper 的 DbContext 註冊並呼叫 `AddApplicationDbContext()`。不修改共用的 `TestHostConfiguration`，既有 factory 與測試行為不變（避免波及所有整合測試）。連線字串缺漏（null／空）時維持現行行為（不丟自訂例外，交給 Npgsql 原有錯誤），不在本 change 新增行為。
   - **連線字串內已有的同名參數以 `DatabaseOptions` 為準**：`NpgsqlConnectionStringBuilder` 對同名參數賦值即覆蓋。理由：單一調整入口，避免環境變數、ConfigMap、連線字串三處打架；缺點是「連線字串裡寫的值被靜默覆蓋」，以 DBL-003 固定並在部署文件寫明。
   - **同一連線字串不同池大小會形成不同的連線池**（Npgsql 以連線字串為池 key）：因所有存取都經同一個設定產生的連線字串，不會分裂成多個池；health check 因覆寫連線字串另成一個（不啟用連線池的）路徑，維持現狀。
3. **逾時判定輔助 `DatabaseTimeoutDetector.IsDatabaseTimeout(Exception)` 放 WebApi `ExceptionHandling/`**：沿例外鏈（`InnerException`，含 `AggregateException` 的內層）走一遍，鏈上任一例外為 `NpgsqlException` 且其自身內層鏈含 `TimeoutException`，即為資料庫逾時。**實測（3.1，DBL-008／009，Npgsql 真實逾時）**：命令逾時為 `NpgsqlException` → `TimeoutException`；連線池耗盡為 `InvalidOperationException`（EF Core Npgsql 外包）→ `NpgsqlException` → `TimeoutException`（內層 `TimeoutException` 為 Npgsql 建立、`TargetSite` 為 null，不是裸的 `TimeoutException`）。兩者共用同一條件，因此原先「`TimeoutException` 且丟出位置在 Npgsql 組件」的條件 (b) 已刪除（本決策原就預先規定：連線池耗盡若被包成 `NpgsqlException` 則刪 (b)）。沒有被 `NpgsqlException` 包住的裸 `TimeoutException`（例如 HttpClient 逾時）不視為資料庫逾時。`OperationCanceledException`（用戶端中斷）不屬於逾時，不納入。
4. **503 `ServiceUnavailable`，以 ADDED Requirement 補充 `api-error-responses`**：逾時是 DB 暫時無法即時服務，與程式缺陷的 500 語意不同，前端可據 `Title` 區分並提示稍後重試。不用 504（本服務不是 gateway）、不加 `Retry-After`（沒有可靠的重試時間）。`Title` 沿用主 spec「機器代碼、無空白標點」規則。不 MODIFIED 既有「框架層錯誤的 Title 為機器代碼」Requirement（其代碼對照是列舉式長文，改寫需整段複製，易與主 spec 脫鉤），改以新 Requirement 補充，並明寫「不適用 AER-001 的 500 對照」避免矛盾；歸檔時由 `docs/api-conventions.md` 的框架層錯誤清單一併列出。
5. **日誌刻意不傳入例外物件**：`LogWarning("Database timeout {Method} {RouteTemplate}. TraceId: {TraceId}", ...)`，只記例外型別名稱（`ExceptionType`）。理由：Npgsql 例外與其 `Data`／訊息可能夾帶 SQL 或連線資訊，比照 `BadHttpRequestException` 分支的既有做法（request-body-size-limits 決策 4）。代價是少了 stack trace，逾時診斷改靠 TraceId 對應 Seq 內同請求的其他紀錄與 PostgreSQL 端日誌；此取捨寫在這裡以免日後誤判為遺漏。
6. **k8s `Database__MaxPoolSize: "25"`（ConfigMap）**：公式 `(replicas + maxSurge) × MaxPoolSize + superuser_reserved(3) + migrator / health-check / 維運預留 ≤ max_connections`。`(2 + 1) × 25 = 75`，加 3 = 78，剩 22 條給 readiness／liveness 探測（每次一條不經連線池的實體連線）、migrator Job 與臨時維運連線。選 25 而非 30：30 時只剩 7 條，滾動更新期間探測與 migrator 重疊就可能貼邊。**此值未經 k8s 環境負載量測**（k6 只在 compose 做過）；僅 `ConnectionTimeoutSeconds` 預設 15 秒的保護下，池滿會回 503 而非無限等待。compose 維持預設 100，不在本 change 變更。`CommandTimeoutSeconds`、`ConnectionTimeoutSeconds` 在 k8s 沿用預設，不另寫入 ConfigMap（減少與程式預設值雙處維護）。
7. **文件**：`docs/api-conventions.md` 第 2 節框架層錯誤補 503；`docs/todo/2026-11-02-deployment-prerequisites.md` 補「副本數 × MaxPoolSize」公式與目前 k8s／compose 數值；`docs/review/TODO.md` ⑧／M-1 改為實際範圍並註明 `db-retry-execution-strategy` 為另案。

## 跨模組慣例對照

已讀 `docs/api-conventions.md`：

- **第 2 節「錯誤格式／框架層錯誤」**：沿用 `application/problem+json`、`Title` 為機器代碼、帶 `traceId`（值為 `HttpContext.TraceIdentifier`）、框架層錯誤不含 `Detail`。**新增**一條框架層對照：資料庫逾時 → 503 `ServiceUnavailable`（決策 4），須同步更新該節；沒有新增 `ErrorType`（此錯誤不經 `Result`／`ResultExtensions`，直接由 `GlobalExceptionHandler` 產生），故不影響 `ResultExtensions` 對照表。
- **第 1 節認證與授權**：不適用，無端點或權限變更。
- **第 3 節資料命名**：不適用；新設定節 `Database` 與設定項名稱（`CommandTimeoutSeconds`、`ConnectionTimeoutSeconds`、`MaxPoolSize`）遵循既有 `*Seconds`／`PermitLimit` 風格，環境變數形式為 `Database__MaxPoolSize`。
- 前端：未新增 API 端點或欄位；前端既有 `ApiError` 以 `detail` 為訊息來源，503 無 `detail` 時前端行為由既有「前端錯誤訊息不得以 Title 作為備援」規範涵蓋，不在本 change 修改。

## 安全確認（CLAUDE.md）

- 輸入驗證：設定值由 DataAnnotations `[Range]` 在啟動時驗證（`ResolveEagerlyValidatedOptions`）；連線字串以 `NpgsqlConnectionStringBuilder` 組裝，無字串拼接、無 shell。
- 資料庫：不新增任何 EF 查詢（無 N+1 風險）；測試中的 `ExecuteSqlRaw`（`pg_sleep`、`LOCK TABLE`）為固定字串、無外部輸入，加 EF1002 理由註解。
- 權限：不新增端點或權限；503 回應不含 `Detail`，日誌不含 SQL／連線字串（決策 5）。
- 前端：不適用。

## Risks / Trade-offs

- **未量測的預設值風險**：k8s `MaxPoolSize = 25` 未經負載量測，高併發時可能比 100 更早出現連線池等待與 503。緩解：預設值可由 ConfigMap 調整、不需重建映像；建議（非驗收）在 k8s 重跑 k6 驗證。
- **逾時判定誤判／漏判**：判定規則依 Npgsql 例外型態，升級 Npgsql 大版本時型態可能變動。緩解：整合測試以真實 Npgsql 逾時（DBL-008／009）驗證，升級時測試會失敗。
- **連線字串被 `DatabaseOptions` 靜默覆蓋**：見決策 2；以 DBL-003 與部署文件固定。
- **逾時後無自動重試**：暫時性失敗仍直接回 503，需用戶端重試；自動重試由 `db-retry-execution-strategy` 處理。
- **503 取代 500 可能影響告警／前端判斷**：目前專案沒有以 500 比率為依據的告警（metrics 為另案 ⑩），前端未針對 500／503 區分處理，風險低。
- **交易內逾時**：命令逾時發生在交易中途時，該交易由例外往外傳而回滾（沿用 `UnitOfWork`／`OrderService` 既有例外路徑，本 change 不改），不會留下半完成狀態；DBL-008 只驗證連線池未損壞。

## Migration Plan

無資料遷移。`Database` 設定節可選；未設定時行為不變，可直接部署。k8s 套用順序不變（ConfigMap 新增鍵後 api Deployment 需重啟才會讀到新環境變數，滾動更新即可）。回滾：移除 ConfigMap 鍵即回到預設 100。

## Open Questions

- k8s `MaxPoolSize = 25` 是否合適？決策 6 的公式與預留是本 change 的判斷，請使用者在交叉審查時確認；若要求更保守或更寬鬆，只改 ConfigMap 與公式表，不影響程式。
- （已解決）連線池耗盡的實際例外型態已於 3.1 觀察確認，見決策 3。
