## AC → 測試對照

| Scenario | 對應 task／測試 |
|---|---|
| DBL-001 未設定採預設值 | 2.1 `DatabasePersistenceConfigurationTests` |
| DBL-002 設定值套用到 DbContext | 2.1 |
| DBL-003 設定值優先於連線字串 | 2.1 |
| DBL-004 readiness 檢查連線設定不變 | 2.1（`DatabaseReadinessHealthCheck` 實際連線字串斷言） |
| DBL-005 範圍外的值啟動失敗 | 1.1 `DatabaseOptionsFailFastTests` |
| DBL-006 邊界值可啟動 | 1.1 |
| DBL-007 失敗訊息不含連線字串 | 1.1 |
| DBL-008 命令超過逾時即失敗 | 3.1 `DatabaseTimeoutBehaviorTests` |
| DBL-009 連線池耗盡等待受限 | 3.1 |
| AER-DB-001 命令逾時回 503 | 4.1 單元（`GlobalExceptionHandlerTests`）＋ 4.2 端對端 |
| AER-DB-002 取得連線逾時回 503 | 4.1 單元＋ 4.2 端對端 |
| AER-DB-003 包裝例外內的逾時 | 4.1 |
| AER-DB-004 非逾時例外維持 500 | 4.1 |
| AER-DB-005 日誌不含 SQL／連線字串 | 4.1 |

## 1. DatabaseOptions 與啟動驗證（TDD）

- [ ] 1.1 新增 `DatabaseOptions`（`Application/Common`）、註冊與啟動時強制解析（對應 DBL-005、006、007）
  - 測試先行（`tests/ProjectC.WebApi.Tests/Startup/DatabaseOptionsFailFastTests.cs`，比照 `CaptchaOptionsFailFastTests`：`new WebApplicationFactory<Program>()` ＋ `AddInMemoryCollection`，不需容器）：
    - `CreatingHost_WithOutOfRangeDatabaseSetting_ThrowsOptionsValidationMentioningSettingName`（Theory：`CommandTimeoutSeconds` 0／3601、`ConnectionTimeoutSeconds` 0／301、`MaxPoolSize` 0／1001；斷言例外鏈含 `OptionsValidationException` 且文字含對應設定項名稱）
    - `CreatingHost_WithBoundaryDatabaseSettings_StartsNormally`（Theory：各項 1 與上限值）
    - `CreatingHost_WithInvalidDatabaseSetting_DoesNotLeakConnectionStringPassword`（DBL-007，連線字串含 `Password=SECRET-SENTINEL`）
  - 預期 RED：先加空殼——`DatabaseOptions` 有預設值但**無** `[Range]`，且已綁定、未驗證——使測試可編譯；RED 為斷言失敗：`Expected a <Exception> to be thrown, but no exception was thrown`（範圍外值未被擋下）；編譯失敗不算 RED
  - 最小實作：`DatabaseOptions` 加 `[Range]`（1–3600／1–300／1–1000，預設 30／15／100）；註冊比照 `PublicReadRateLimitingOptions`（`AddOptions<T>().Bind(...).ValidateDataAnnotations()` ＋ Singleton 解包）；`EagerOptionsValidationExtensions.ResolveEagerlyValidatedOptions` 加一行強制解析並附設計引用註解
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~DatabaseOptionsFailFast"` 全數通過
  - 重構：N/A 或重跑同命令
  - 紀錄（原文）：
    - RED（正式程式碼修改前，標明命令、失敗測試名稱、Expected／Actual 原文）：
    - GREEN：
    - 重構後重跑（或 N/A）：

## 2. DbContext 套用設定（TDD）

- [ ] 2.1 `AddPersistence` 改為 `(sp, options)` 多載，執行期以 `NpgsqlConnectionStringBuilder` 套用 `Timeout`／`Maximum Pool Size`，並以 `o.CommandTimeout(n)` 套用命令逾時（對應 DBL-001、002、003、004）
  - 測試先行（`tests/ProjectC.WebApi.Tests/Database/DatabasePersistenceConfigurationTests.cs`，用 `DatabaseSettingsWebApplicationFactory.WithWebHostBuilder` ＋ `AddInMemoryCollection` 設定 `Database:*`（**不可**用 `CustomWebApplicationFactory`：其測試 helper 會移除正式 DbContext 註冊，`Database:*` 不生效，見 design Context），由 host 取 scope 內的 `ApplicationDbContext`，不連開發用 `db`）：
    - `ApplicationDbContext_WithoutDatabaseSection_UsesDefaultTimeoutsAndPoolSize`（DBL-001：`Database.GetCommandTimeout()` 為 30，連線字串 `Timeout` 15、`MaxPoolSize` 100）
    - `ApplicationDbContext_WithDatabaseSection_AppliesCommandTimeoutAndConnectionStringValues`（DBL-002：7／5／20）
    - `ApplicationDbContext_WhenConnectionStringHasSameParameters_DatabaseSettingsWin`（DBL-003：以 `ConnectionStrings:DefaultConnection` 內含 `Maximum Pool Size=500;Timeout=60` 的設定，斷言覆蓋為 20／5；解析用 `NpgsqlConnectionStringBuilder`，不做字串比對）
    - `DatabaseReadinessHealthCheck_WithLargeDatabaseSettings_StillUsesTwoSecondTimeoutWithoutPooling`（DBL-004：`Database:ConnectionTimeoutSeconds = 30`、`MaxPoolSize = 20` 下跑 `CheckHealthAsync` 後，該 scope DbContext 的連線字串 `Timeout` 為 2、`Pooling` 為 false）
  - 預期 RED：測試可編譯（僅用既有 API）；RED 為斷言失敗：`Expected ... to be 7, but found 30`（命令逾時未套用）、`Expected builder.MaxPoolSize to be 20, but found 100`；DBL-001 的命令逾時斷言現況會失敗（目前未呼叫 `CommandTimeout`，`GetCommandTimeout()` 為 null，`Expected 30 but found <null>`），連線字串 `Timeout`／`MaxPoolSize` 部分現況即為預設值；DBL-004 現況即通過，記為「既有行為固定」並以 mutation（見下）證明能失敗；RED 紀錄以實際結果為準
  - 最小實作：見標題；連線字串缺漏（null／空）維持現行行為。另（測試支援，不屬正式行為）：把 DbContext 註冊抽成 `AddApplicationDbContext()` 並由 `AddPersistence` 呼叫；新增 `DatabaseSettingsWebApplicationFactory`（design 決策 2）。RED 階段 factory 與抽出的方法須先存在且可編譯，RED 才是斷言失敗而非編譯錯誤
  - 回歸：新增 factory 不改動共用 `TestHostConfiguration`；GREEN 後另跑既有一個使用 `CustomWebApplicationFactory` 的測試類別確認不受影響
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~DatabasePersistenceConfiguration"` 全數通過
  - mutation（`bash scripts/verify-test-evidence.sh mutate`，一次一個，還原後 `touch`）：(a) 移除 `MaxPoolSize` 賦值，DBL-002／003 須失敗；(b) 移除 `o.CommandTimeout(...)`，DBL-002 須失敗；(c) 把 `DatabaseReadinessHealthCheck` 的 `Timeout = TimeoutSeconds` 改成沿用池設定，DBL-004 須失敗
  - 重構：N/A 或重跑同命令
  - 紀錄（原文，含 mutation 命令與失敗輸出）：
    - RED：
    - GREEN：
    - mutation (a)／(b)／(c)：

## 3. 逾時確實生效（整合測試，真實 PostgreSQL）

- [ ] 3.1 DBL-008／009：命令逾時與連線池耗盡等待逾時（對應 DBL-008、009）
  - 測試先行（`tests/ProjectC.WebApi.Tests/Database/DatabaseTimeoutBehaviorTests.cs`，`DatabaseSettingsWebApplicationFactory`（2.1 建立，非 `CustomWebApplicationFactory`）＋`Database:*` 設定；用 `Stopwatch` 斷言上界，不斷言下界以外的精確時間）：
    - `ExecuteSql_WhenCommandExceedsCommandTimeout_FailsWithinBoundAndPoolRemainsUsable`（`CommandTimeoutSeconds = 2`，`pg_sleep(6)`；斷言 5 秒內失敗、例外鏈含 `TimeoutException`、之後 `SELECT 1` 成功；`pg_sleep` 用 `ExecuteSqlRawAsync` 並加 `#pragma warning disable EF1002` 與固定字串理由註解，比照 `ProductionAdminBootstrapStartupTests`）
    - `OpenConnection_WhenPoolExhausted_FailsWithinConnectionTimeoutAndSucceedsAfterRelease`（`MaxPoolSize = 1`、`ConnectionTimeoutSeconds = 2`；先以一個 scope 的 DbContext 開連線占用池，另一 scope 取連線須在 5 秒內失敗，釋放後可成功）
  - 測試前提：`MaxPoolSize = 1` 的測試跑在完整 host 上，背景服務可能同時取同一個池的連線；斷言只看上界（失敗須在 5 秒內），占用連線前須容許等待，並於紀錄說明是否需停用背景服務（4.2 同）
  - **本 task 另須記錄兩種逾時實際的例外型態**（型別全名與內層鏈）於紀錄欄位，供 4.1 的 `DatabaseTimeoutDetector` 決策 3 的假設核對；若連線池耗盡不是裸 `TimeoutException`，須先回頭更新 design.md 決策 3 與 4.1 再繼續
  - 預期 RED：在 2.1 完成前這兩個測試即會失敗（設定未套用，6 秒命令在預設 30 秒下成功、池上限 100 不耗盡）；故 3.1 排在 2.1 之後時 RED 以「2.1 的最小實作暫時還原（`restore`，或 mutate 移除 `CommandTimeout`／`MaxPoolSize` 賦值）」取得，RED 失敗訊息須為 `Expected ... to throw ... but no exception was thrown` 類，不得是編譯或容器錯誤
  - 最小實作：無新增正式程式碼（2.1 已涵蓋）
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~DatabaseTimeoutBehavior"`
  - 重構：N/A
  - 紀錄（原文，含例外型態）：
    - mutation／RED：
    - 兩種逾時的實際例外型態：
    - GREEN：

## 4. 503 ServiceUnavailable（TDD）

- [ ] 4.1 `GlobalExceptionHandler` 資料庫逾時分支與 `DatabaseTimeoutDetector`（對應 AER-DB-001、002、003、004、005）
  - 測試先行（`tests/ProjectC.WebApi.Tests/ExceptionHandling/GlobalExceptionHandlerTests.cs` 增列，比照既有直接建構 handler 與 `DefaultHttpContext` 的寫法，用 `RecordingLogger`）：
    - `TryHandleAsync_WhenCommandTimeout_Writes503ServiceUnavailableWithTraceId`（`new NpgsqlException("msg", new TimeoutException())`）
    - `TryHandleAsync_WhenPoolWaitTimeout_Writes503ServiceUnavailable`（依 3.1 觀察到的實際型態建構；若偵測條件含「`TargetSite` 屬 Npgsql 組件」，`new TimeoutException()` 未經拋出時 `TargetSite` 為 null，須沿用 3.1 實際捕捉到的例外物件，或改由 4.2 端對端涵蓋；若決策 3(b) 因連線池耗盡被包成 `NpgsqlException` 而刪除，則無此問題）
    - `TryHandleAsync_WhenTimeoutWrappedInDbUpdateException_Writes503ServiceUnavailable`（AER-DB-003）
    - `TryHandleAsync_WhenNpgsqlExceptionWithoutTimeout_Writes500InternalServerError`、`TryHandleAsync_WhenPlainTimeoutExceptionNotFromNpgsql_Writes500InternalServerError`（避免誤判：非 Npgsql 來源的 `TimeoutException` 維持 500）、`TryHandleAsync_WhenInvalidOperationException_Writes500InternalServerError`（AER-DB-004，AER-001 回歸）
    - `TryHandleAsync_WhenDatabaseTimeout_LogsWarningWithoutSqlOrConnectionString`（AER-DB-005：例外 `Message`／`Data` 含 SQL 哨兵與連線字串哨兵，斷言 `RecordingLogger` 有 Warning、帶 TraceId、全部輸出不含哨兵）
  - 預期 RED：先加空殼 `DatabaseTimeoutDetector.IsDatabaseTimeout` 恆回 false（使其可編譯）；RED 為斷言失敗：`Expected httpContext.Response.StatusCode to be 503, but found 500`；其餘 500／日誌的「回歸」測試現況即通過，記為既有行為固定
  - 最小實作：實作 `DatabaseTimeoutDetector`（design 決策 3）；`TryHandleAsync` 在 `BadHttpRequestException` 分支之後加入資料庫逾時分支，產生 503／`ServiceUnavailable`／`traceId`、無 `Detail`；日誌不傳入例外物件（決策 5）
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~GlobalExceptionHandlerTests"`
  - mutation：(a) 偵測器恆回 false，503 測試須失敗；(b) 偵測器把所有 `TimeoutException` 視為資料庫逾時，「非 Npgsql 來源」測試須失敗
  - 重構：N/A 或重跑
  - 紀錄（原文）：
    - RED：
    - GREEN：
    - mutation (a)／(b)：

- [ ] 4.2 端對端：DB 逾時經 HTTP 回標準 503（對應 AER-DB-001、002 的 HTTP 層）
  - 測試先行（`tests/ProjectC.WebApi.Tests/Database/DatabaseTimeoutHttpResponseTests.cs`，`DatabaseSettingsWebApplicationFactory`（2.1 建立，非 `CustomWebApplicationFactory`）＋`Database:CommandTimeoutSeconds = 1`）：
    - `GetEventById_WhenDatabaseCommandTimesOut_Returns503ProblemDetails`：測試先以獨立連線開交易並 `LOCK TABLE "Events" IN ACCESS EXCLUSIVE MODE`（固定字串，加 EF1002 理由註解）使目標查詢卡住，呼叫會查 DB 且未被查詢快取擋掉的公開端點（`GET /api/events/{id:guid}`，實作時先確認該路徑不命中 Redis 快取，必要時改選不經快取的端點並在紀錄說明），斷言 `503`、`application/problem+json`、`title = ServiceUnavailable`、`traceId` 非空且等於回應中的值、無 `detail`；測試結尾回滾鎖
    - `GetEventById_WhenConnectionPoolExhausted_Returns503ProblemDetails`：`MaxPoolSize = 1`、`ConnectionTimeoutSeconds = 1`，測試先占用唯一連線，再呼叫同一端點，斷言同上
  - 預期 RED：在 4.1 的最小實作前，回應為 500 `InternalServerError`（`Expected ... to be 503, but found 500`）；RED 取得方式為 4.1 實作前先寫此測試，或對 4.1 實作以 `restore`／mutation 暫時移除分支
  - 最小實作：無新增（4.1 已涵蓋）
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~DatabaseTimeoutHttpResponse"`
  - 重構：N/A
  - 紀錄（原文）：
    - RED：
    - GREEN：

## 5. 設定與文件

- [ ] 5.1 k8s ConfigMap 與文件同步（TDD：N/A——純設定與文件，無自動化行為測試；以下列檢查取代，且此 N/A 只豁免 TDD 步驟）
  - `k8s/10-configmap.yaml` 新增 `Database__MaxPoolSize: "25"` 與註解（公式與預留見 design 決策 6）
  - `docs/todo/2026-11-02-deployment-prerequisites.md` 新增連線池容量小節：公式、目前 k8s（`(2 + 1) × 25 + 3 = 78 ≤ 100`）與 compose（單副本 100 = 100，沿用 k6 基準、未調整）數值、readiness 探測不經連線池但占 `max_connections`、`Database` 設定覆蓋連線字串同名參數
  - `docs/api-conventions.md` 第 2 節「框架層錯誤」補一條：資料庫逾時 → 503 `ServiceUnavailable`（含 `traceId`、無 `Detail`）
  - `docs/review/TODO.md` ⑧ 與 M-1 改為實際範圍（逾時＋池上限；retry 拆出為 `db-retry-execution-strategy`）並標示已實作／待歸檔
  - 驗證：`kubectl apply --dry-run=client -f k8s/10-configmap.yaml`（若本機無 kubectl 則改用 `docker run --rm -v ... bitnami/kubectl` 或記錄無法執行並以 YAML 語法檢查替代）；`bash scripts/check-doc-paths.sh`
  - 紀錄（原文）：

## 6. hardener 最終檢查與全套測試

- [ ] 6.1 hardener 最終檢查（程式碼變更全部完成後套用 `.claude/skills/hardener/SKILL.md`，範圍：`AddPersistence` 的連線字串組裝、`GlobalExceptionHandler`／`DatabaseTimeoutDetector`；重點：連線字串缺漏或格式錯誤時的行為、偵測器的 null／循環例外鏈保護、日誌遮蔽、`CancellationToken`）
  - 發現缺少的防禦分支依 TDD 先補 RED 測試再修改，證據記於本 task；若無修改則記錄「已符合清單」
  - 紀錄：
- [ ] 6.2 全套測試（各測試專案摘要行貼原文，不得以「全數通過」代替）
  - `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests`
  - `docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests`
  - 其餘測試專案（`ProjectC.Application.Tests`、`ProjectC.Domain.Tests`、`ProjectC.LoadTest.Seeder.Tests` 等，以 `ls tests` 為準）
  - 確認 `ServiceRegistrationSnapshot.baseline.txt` 是否需更新（`DatabaseOptions` 新增註冊）；若更新須在此記錄差異
  - 紀錄：
- [ ] 6.3 Task 完成後告知：spec 同步狀態（`database-connection-limits` 新增、`api-error-responses` 補 503；`docs/api-conventions.md` 已同步）、AC 涵蓋、維護性提醒（重複邏輯、命名一致性）。k6／k8s 重跑列為建議，非驗收
