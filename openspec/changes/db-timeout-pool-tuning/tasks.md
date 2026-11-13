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

- [x] 1.1 新增 `DatabaseOptions`（`Application/Common`）、註冊與啟動時強制解析（對應 DBL-005、006、007）
  - 測試先行（`tests/ProjectC.WebApi.Tests/Startup/DatabaseOptionsFailFastTests.cs`，比照 `CaptchaOptionsFailFastTests`：`new WebApplicationFactory<Program>()` ＋ `AddInMemoryCollection`，不需容器）：
    - `CreatingHost_WithOutOfRangeDatabaseSetting_ThrowsOptionsValidationMentioningSettingName`（Theory：`CommandTimeoutSeconds` 0／3601、`ConnectionTimeoutSeconds` 0／301、`MaxPoolSize` 0／1001；斷言例外鏈含 `OptionsValidationException` 且文字含對應設定項名稱）
    - `CreatingHost_WithBoundaryDatabaseSettings_StartsNormally`（Theory：各項 1 與上限值）
    - `CreatingHost_WithInvalidDatabaseSetting_DoesNotLeakConnectionStringPassword`（DBL-007，連線字串含 `Password=SECRET-SENTINEL`）
  - 預期 RED：先加空殼——`DatabaseOptions` 有預設值但**無** `[Range]`，且已綁定、未驗證——使測試可編譯；RED 為斷言失敗：`Expected a <Exception> to be thrown, but no exception was thrown`（範圍外值未被擋下）；編譯失敗不算 RED
  - 最小實作：`DatabaseOptions` 加 `[Range]`（1–3600／1–300／1–1000，預設 30／15／100）；註冊比照 `PublicReadRateLimitingOptions`（`AddOptions<T>().Bind(...).ValidateDataAnnotations()` ＋ Singleton 解包）；`EagerOptionsValidationExtensions.ResolveEagerlyValidatedOptions` 加一行強制解析並附設計引用註解
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~DatabaseOptionsFailFast"` 全數通過
  - 重構：N/A 或重跑同命令
  - 紀錄（原文）：
    - RED（正式程式碼修改前，標明命令、失敗測試名稱、Expected／Actual 原文）：先加空殼（`DatabaseOptions` 無 `[Range]`、已 Bind＋`ValidateDataAnnotations`、已在 `ResolveEagerlyValidatedOptions` 解析）後執行 `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~DatabaseOptionsFailFast"`：
      ```
      （以下為補錄的逐測試原文：暫時移除 `DatabaseOptions` 的三個 `[Range]` 後重跑同命令取得，已 `git checkout` 還原；原紀錄以 `...` 縮寫測試名稱且只貼一次 Error Message）
      Failed ProjectC.WebApi.Tests.Startup.DatabaseOptionsFailFastTests.CreatingHost_WithOutOfRangeDatabaseSetting_ThrowsOptionsValidationMentioningSettingName(settingName: "MaxPoolSize", value: 1001) [738 ms]
        Error Message:
        Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.DatabaseOptionsFailFastTests.CreatingHost_WithOutOfRangeDatabaseSetting_ThrowsOptionsValidationMentioningSettingName(settingName: "MaxPoolSize", value: 0) [163 ms]
        Error Message:
        Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.DatabaseOptionsFailFastTests.CreatingHost_WithOutOfRangeDatabaseSetting_ThrowsOptionsValidationMentioningSettingName(settingName: "CommandTimeoutSeconds", value: 3601) [83 ms]
        Error Message:
        Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.DatabaseOptionsFailFastTests.CreatingHost_WithOutOfRangeDatabaseSetting_ThrowsOptionsValidationMentioningSettingName(settingName: "ConnectionTimeoutSeconds", value: 301) [49 ms]
        Error Message:
        Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.DatabaseOptionsFailFastTests.CreatingHost_WithOutOfRangeDatabaseSetting_ThrowsOptionsValidationMentioningSettingName(settingName: "ConnectionTimeoutSeconds", value: 0) [68 ms]
        Error Message:
        Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.DatabaseOptionsFailFastTests.CreatingHost_WithOutOfRangeDatabaseSetting_ThrowsOptionsValidationMentioningSettingName(settingName: "CommandTimeoutSeconds", value: 0) [117 ms]
        Error Message:
        Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.DatabaseOptionsFailFastTests.CreatingHost_WithInvalidDatabaseSetting_DoesNotLeakConnectionStringPassword [117 ms]
        Error Message:
        Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed!  - Failed:     7, Passed:     6, Skipped:     0, Total:    13, Duration: 1 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
      （6 個邊界值測試現況即通過，為既有行為固定。）
    - GREEN（加 `[Range]` 後同命令）：`Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13, Duration: 1 s - ProjectC.WebApi.Tests.dll (net10.0)`
    - 重構後重跑（或 N/A）：N/A（無重構）
    - 註：選項註冊放在 `AddPersistence`（它已持有 `IConfiguration`），與 2.1 的 DbContext 註冊同處。

## 2. DbContext 套用設定（TDD）

- [x] 2.1 `AddPersistence` 改為 `(sp, options)` 多載，執行期以 `NpgsqlConnectionStringBuilder` 套用 `Timeout`／`Maximum Pool Size`，並以 `o.CommandTimeout(n)` 套用命令逾時（對應 DBL-001、002、003、004）
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
    - RED（先抽出 `AddApplicationDbContext()`［行為未變、未套用任何設定］與 `DatabaseSettingsWebApplicationFactory` 後，命令 `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~DatabasePersistenceConfiguration"`）：
      ```
      Failed ProjectC.WebApi.Tests.Database.DatabasePersistenceConfigurationTests.ApplicationDbContext_WithDatabaseSection_AppliesCommandTimeoutAndConnectionStringValues [102 ms]
        Expected dbContext.Database.GetCommandTimeout() to be 7, but found <null>.
      Failed ProjectC.WebApi.Tests.Database.DatabasePersistenceConfigurationTests.ApplicationDbContext_WhenConnectionStringHasSameParameters_DatabaseSettingsWin [93 ms]
        Expected builder.MaxPoolSize to be 20, but found 500 (difference of 480).
      Failed ProjectC.WebApi.Tests.Database.DatabasePersistenceConfigurationTests.ApplicationDbContext_WithoutDatabaseSection_UsesDefaultTimeoutsAndPoolSize [43 ms]
        Expected dbContext.Database.GetCommandTimeout() to be 30, but found <null>.
      Failed!  - Failed:     3, Passed:     1, Skipped:     0, Total:     4, Duration: 619 ms - ProjectC.WebApi.Tests.dll (net10.0)
      ```
      DBL-004（readiness）現況即通過＝既有行為固定，以 mutation (c) 證明能失敗。
    - GREEN：`Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 578 ms - ProjectC.WebApi.Tests.dll (net10.0)`
    - 回歸（既有 `CustomWebApplicationFactory` 測試類別 `AdminEventsControllerTests` ＋本類別）：`Passed!  - Failed:     0, Passed:    65, Skipped:     0, Total:    65, Duration: 21 s - ProjectC.WebApi.Tests.dll (net10.0)`
    - mutation (a)／(b)／(c)（`bash scripts/verify-test-evidence.sh mutate --file <檔> --from <原> --to <新> -- docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~DatabasePersistenceConfiguration`，每次還原後 `touch`）：
      - (a) 移除 `MaxPoolSize = databaseOptions.MaxPoolSize,`：
        ```
        Failed ProjectC.WebApi.Tests.Database.DatabasePersistenceConfigurationTests.ApplicationDbContext_WithDatabaseSection_AppliesCommandTimeoutAndConnectionStringValues [105 ms]
          Expected builder.MaxPoolSize to be 20, but found 100 (difference of 80).
        Failed ProjectC.WebApi.Tests.Database.DatabasePersistenceConfigurationTests.ApplicationDbContext_WhenConnectionStringHasSameParameters_DatabaseSettingsWin [89 ms]
          Expected builder.MaxPoolSize to be 20, but found 500 (difference of 480).
        Failed!  - Failed:     2, Passed:     2, Skipped:     0, Total:     4, Duration: 589 ms - ProjectC.WebApi.Tests.dll (net10.0)
        結果：測試如預期失敗
        ```
      - (b) `npgsqlOptions => npgsqlOptions.CommandTimeout(...)` → `_ => { }`：
        ```
        Failed ProjectC.WebApi.Tests.Database.DatabasePersistenceConfigurationTests.ApplicationDbContext_WithDatabaseSection_AppliesCommandTimeoutAndConnectionStringValues [105 ms]
          Expected dbContext.Database.GetCommandTimeout() to be 7, but found <null>.
        Failed ProjectC.WebApi.Tests.Database.DatabasePersistenceConfigurationTests.ApplicationDbContext_WithoutDatabaseSection_UsesDefaultTimeoutsAndPoolSize [50 ms]
          Expected dbContext.Database.GetCommandTimeout() to be 30, but found <null>.
        Failed!  - Failed:     2, Passed:     2, Skipped:     0, Total:     4, Duration: 627 ms - ProjectC.WebApi.Tests.dll (net10.0)
        結果：測試如預期失敗
        ```
      - (c) `DatabaseReadinessHealthCheck` 的 `Timeout = TimeoutSeconds,` → `Timeout = 30,`（兩行 `--from` 因行尾為 CRLF 比對 0 次，改單行）：
        ```
        Failed ProjectC.WebApi.Tests.Database.DatabasePersistenceConfigurationTests.DatabaseReadinessHealthCheck_WithLargeDatabaseSettings_StillUsesTwoSecondTimeoutWithoutPooling [376 ms]
          Expected builder.Timeout to be 2, but found 30 (difference of 28).
        Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4, Duration: 555 ms - ProjectC.WebApi.Tests.dll (net10.0)
        結果：測試如預期失敗
        ```
    - 實作備註：`DatabaseSettingsWebApplicationFactory` 做了與 `TestHostConfiguration` 相同的三個 `RemoveAll`（`DbContextOptions<>`、`IDbContextOptionsConfiguration<>`、`ApplicationDbContext`）後呼叫 `AddApplicationDbContext()`；`DatabaseOptions` 以 `serviceProvider.GetRequiredService<DatabaseOptions>()` 取得。

## 3. 逾時確實生效（整合測試，真實 PostgreSQL）

- [x] 3.1 DBL-008／009：命令逾時與連線池耗盡等待逾時（對應 DBL-008、009）
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
    - mutation／RED（2.1 最小實作已先存在，依規格以 `mutate` 暫時移除 2.1 的賦值取得 RED，還原後 `touch`；命令 `bash scripts/verify-test-evidence.sh mutate --file src/ProjectC.WebApi/Extensions/PersistenceServiceCollectionExtensions.cs --from <原> --to <新> -- docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~DatabaseTimeoutBehavior`）：
      - 移除 `npgsqlOptions => npgsqlOptions.CommandTimeout(databaseOptions.CommandTimeoutSeconds)`（→ `_ => { }`）：
        ```
        Failed ProjectC.WebApi.Tests.Database.DatabaseTimeoutBehaviorTests.ExecuteSql_WhenCommandExceedsCommandTimeout_FailsWithinBoundAndPoolRemainsUsable [6 s]
          Expected a <System.Exception> to be thrown, but no exception was thrown.
        Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2, Duration: 8 s - ProjectC.WebApi.Tests.dll (net10.0)
        結果：測試如預期失敗
        ```
      - 移除 `MaxPoolSize = databaseOptions.MaxPoolSize,`：
        ```
        Failed ProjectC.WebApi.Tests.Database.DatabaseTimeoutBehaviorTests.OpenConnection_WhenPoolExhausted_FailsWithinConnectionTimeoutAndSucceedsAfterRelease [357 ms]
          Expected a <System.Exception> to be thrown, but no exception was thrown.
        Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2, Duration: 2 s - ProjectC.WebApi.Tests.dll (net10.0)
        結果：測試如預期失敗
        ```
    - 兩種逾時的實際例外型態（測試以 `ITestOutputHelper` 印出例外鏈，`--logger "console;verbosity=detailed"`）：
      ```
      pool-exhausted: System.InvalidOperationException | TargetSite=Npgsql.EntityFrameworkCore.PostgreSQL
      pool-exhausted: Npgsql.NpgsqlException | TargetSite=Npgsql
      pool-exhausted: System.TimeoutException | TargetSite=
      command-timeout: Npgsql.NpgsqlException | TargetSite=Npgsql
      command-timeout: System.TimeoutException | TargetSite=
      ```
      結論：連線池耗盡**不是**裸 `TimeoutException`——外層為 EF Core Npgsql 丟出的 `InvalidOperationException`，內層為 `NpgsqlException` → `TimeoutException`（`TargetSite` 為 null）；命令逾時為 `NpgsqlException` → `TimeoutException`。故決策 3 條件 (b)（以 `TargetSite` 判定裸 `TimeoutException`）刪除，只留「鏈上有 `NpgsqlException` 且其內層鏈含 `TimeoutException`」；已依本 task 規定更新 design.md 決策 3、Open Questions 與本檔 4.1。
    - 背景服務結論：Testing 環境不註冊 `ExpiredOrderCleanupService`／`PurchaseQueueAdmissionService`（`BackgroundServiceCollectionExtensions.cs`：`if (!environment.IsEnvironment("Testing"))`），`MaxPoolSize = 1` 測試不需停用背景服務；host 啟動亦未於測試前自行占用連線池（test 結果穩定通過）。
    - GREEN（還原後）：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~DatabaseTimeoutBehavior"` → `Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 4 s - ProjectC.WebApi.Tests.dll (net10.0)`（還原後重跑原文；`Test Run Successful` 行未收錄）

## 4. 503 ServiceUnavailable（TDD）

- [x] 4.1 `GlobalExceptionHandler` 資料庫逾時分支與 `DatabaseTimeoutDetector`（對應 AER-DB-001、002、003、004、005）
  - 測試先行（`tests/ProjectC.WebApi.Tests/ExceptionHandling/GlobalExceptionHandlerTests.cs` 增列，比照既有直接建構 handler 與 `DefaultHttpContext` 的寫法，用 `RecordingLogger`）：
    - `TryHandleAsync_WhenCommandTimeout_Writes503ServiceUnavailableWithTraceId`（`new NpgsqlException("msg", new TimeoutException())`）
    - `TryHandleAsync_WhenPoolWaitTimeout_Writes503ServiceUnavailable`（依 3.1 實測型態建構：`new InvalidOperationException("msg", new NpgsqlException("msg", new TimeoutException()))`；決策 3(b) 已因連線池耗盡被包成 `NpgsqlException` 而刪除，無 `TargetSite` 問題）
    - `TryHandleAsync_WhenTimeoutWrappedInDbUpdateException_Writes503ServiceUnavailable`（AER-DB-003）
    - `TryHandleAsync_WhenNpgsqlExceptionWithoutTimeout_Writes500InternalServerError`、`TryHandleAsync_WhenPlainTimeoutExceptionNotFromNpgsql_Writes500InternalServerError`（避免誤判：未被 `NpgsqlException` 包住的裸 `TimeoutException` 維持 500）、`TryHandleAsync_WhenInvalidOperationException_Writes500InternalServerError`（AER-DB-004，AER-001 回歸）
    - `TryHandleAsync_WhenDatabaseTimeout_LogsWarningWithoutSqlOrConnectionString`（AER-DB-005：例外 `Message`／`Data` 含 SQL 哨兵與連線字串哨兵，斷言 `RecordingLogger` 有 Warning、帶 TraceId、全部輸出不含哨兵）
  - 預期 RED：先加空殼 `DatabaseTimeoutDetector.IsDatabaseTimeout` 恆回 false（使其可編譯）；RED 為斷言失敗：`Expected httpContext.Response.StatusCode to be 503, but found 500`；其餘 500／日誌的「回歸」測試現況即通過，記為既有行為固定
  - 最小實作：實作 `DatabaseTimeoutDetector`（design 決策 3）；`TryHandleAsync` 在 `BadHttpRequestException` 分支之後加入資料庫逾時分支，產生 503／`ServiceUnavailable`／`traceId`、無 `Detail`；日誌不傳入例外物件（決策 5）
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~GlobalExceptionHandlerTests"`
  - mutation：(a) 偵測器恆回 false，503 測試須失敗；(b) 偵測器把所有 `TimeoutException` 視為資料庫逾時，「未被 `NpgsqlException` 包住的裸 `TimeoutException`」測試須失敗
  - 重構：N/A 或重跑
  - 紀錄（原文）：
    - RED（先加空殼 `DatabaseTimeoutDetector.IsDatabaseTimeout` 恆回 false，命令 `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~GlobalExceptionHandlerTests"`）：
      ```
      Failed ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenPoolWaitTimeout_Writes503ServiceUnavailable [24 ms]
        Expected httpContext.Response.StatusCode to be 503, but found 500 (difference of -3).
      Failed ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenTimeoutWrappedInDbUpdateException_Writes503ServiceUnavailable [4 ms]
        Expected httpContext.Response.StatusCode to be 503, but found 500 (difference of -3).
      Failed ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenDatabaseTimeout_LogsWarningWithoutSqlOrConnectionString [5 ms]
        Expected entry.Level[0] to be LogLevel.Warning {value: 3}, but found LogLevel.Error {value: 4}.
      Failed ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenCommandTimeout_Writes503ServiceUnavailableWithTraceId [3 ms]
        Expected httpContext.Response.StatusCode to be 503, but found 500 (difference of -3).
      Failed!  - Failed:     4, Passed:    11, Skipped:     0, Total:    15, Duration: 150 ms - ProjectC.WebApi.Tests.dll (net10.0)
      ```
      （`TryHandleAsync_WhenNotDatabaseTimeout_Writes500InternalServerError` 三個 theory 案例與既有測試現況即通過＝既有行為固定；偵測器以 (b) mutation 證明能失敗。）
    - GREEN：`Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15, Duration: 87 ms - ProjectC.WebApi.Tests.dll (net10.0)`
    - mutation (a)／(b)（`bash scripts/verify-test-evidence.sh mutate --file src/ProjectC.WebApi/ExceptionHandling/DatabaseTimeoutDetector.cs ...`，還原後 `touch`）：
      - (a) 偵測器開頭改為 `return false;`：
        ```
        Failed ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenPoolWaitTimeout_Writes503ServiceUnavailable [22 ms]
          Expected httpContext.Response.StatusCode to be 503, but found 500 (difference of -3).
        Failed ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenTimeoutWrappedInDbUpdateException_Writes503ServiceUnavailable [6 ms]
          Expected httpContext.Response.StatusCode to be 503, but found 500 (difference of -3).
        Failed ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenDatabaseTimeout_LogsWarningWithoutSqlOrConnectionString [6 ms]
          Expected entry.Level[0] to be LogLevel.Warning {value: 3}, but found LogLevel.Error {value: 4}.
        Failed ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenCommandTimeout_Writes503ServiceUnavailableWithTraceId [3 ms]
          Expected httpContext.Response.StatusCode to be 503, but found 500 (difference of -3).
        Failed!  - Failed:     4, Passed:    11, Skipped:     0, Total:    15, Duration: 119 ms - ProjectC.WebApi.Tests.dll (net10.0)
        結果：測試如預期失敗
        ```
      - (b) 條件改為 `candidate is TimeoutException`（任何 `TimeoutException` 都算）：
        ```
        Failed ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenNotDatabaseTimeout_Writes500InternalServerError(exception: System.TimeoutException: http client timeout) [23 ms]
          Expected httpContext.Response.StatusCode to be 500, but found 503 (difference of 3).
        Failed!  - Failed:     1, Passed:    14, Skipped:     0, Total:    15, Duration: 96 ms - ProjectC.WebApi.Tests.dll (net10.0)
        結果：測試如預期失敗
        ```
    - 備註：偵測器的 null／循環／`AggregateException` 防護屬 6.1 hardener 範圍，於該處補測試。

- [x] 4.2 端對端：DB 逾時經 HTTP 回標準 503（對應 AER-DB-001、002 的 HTTP 層）
  - 測試先行（`tests/ProjectC.WebApi.Tests/Database/DatabaseTimeoutHttpResponseTests.cs`，`DatabaseSettingsWebApplicationFactory`（2.1 建立，非 `CustomWebApplicationFactory`）＋`Database:CommandTimeoutSeconds = 1`）：
    - `GetEventById_WhenDatabaseCommandTimesOut_Returns503ProblemDetails`：測試先以獨立連線開交易並 `LOCK TABLE "Events" IN ACCESS EXCLUSIVE MODE`（固定字串，加 EF1002 理由註解）使目標查詢卡住，呼叫會查 DB 且未被查詢快取擋掉的公開端點（`GET /api/events/{id:guid}`，實作時先確認該路徑不命中 Redis 快取，必要時改選不經快取的端點並在紀錄說明），斷言 `503`、`application/problem+json`、`title = ServiceUnavailable`、`traceId` 非空且等於回應中的值、無 `detail`；測試結尾回滾鎖
    - `GetEventById_WhenConnectionPoolExhausted_Returns503ProblemDetails`：`MaxPoolSize = 1`、`ConnectionTimeoutSeconds = 1`，測試先占用唯一連線，再呼叫同一端點，斷言同上
  - 預期 RED：在 4.1 的最小實作前，回應為 500 `InternalServerError`（`Expected ... to be 503, but found 500`）；RED 取得方式為 4.1 實作前先寫此測試，或對 4.1 實作以 `restore`／mutation 暫時移除分支
  - 最小實作：無新增（4.1 已涵蓋）
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~DatabaseTimeoutHttpResponse"`
  - 重構：N/A
  - 紀錄（原文）：
    - RED（4.1 已先實作，依規格以 `mutate` 暫時讓偵測器恆回 false：`bash scripts/verify-test-evidence.sh mutate --file src/ProjectC.WebApi/ExceptionHandling/DatabaseTimeoutDetector.cs --from "        ArgumentNullException.ThrowIfNull(exception);" --to "        return false;" -- docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~DatabaseTimeoutHttpResponse`，還原後 `touch`）：
      ```
      Failed ProjectC.WebApi.Tests.Database.DatabaseTimeoutHttpResponseTests.GetEventById_WhenConnectionPoolExhausted_Returns503ProblemDetails [1 s]
        Expected response.StatusCode to be HttpStatusCode.ServiceUnavailable {value: 503}, but found HttpStatusCode.InternalServerError {value: 500}.
      Failed ProjectC.WebApi.Tests.Database.DatabaseTimeoutHttpResponseTests.GetEventById_WhenDatabaseCommandTimesOut_Returns503ProblemDetails [1 s]
        Expected response.StatusCode to be HttpStatusCode.ServiceUnavailable {value: 503}, but found HttpStatusCode.InternalServerError {value: 500}.
      Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 2 s - ProjectC.WebApi.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
    - GREEN（還原後）：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~DatabaseTimeoutHttpResponse"` → `Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 2 s - ProjectC.WebApi.Tests.dll (net10.0)`
    - 端點確認：`GET /api/events/{id:guid}` 由 `GetEventByIdHandler` 處理，未經 `IQueryCache`（只有 `GetEventsHandler` 使用），且查詢目標為 `Events`，不存在的 id 同樣會被排他鎖卡住，故不需預建活動。
    - **實作期發現（4.2 首次執行）**：兩個測試首次執行即只在 `Content-Type` 斷言失敗（狀態碼已是 503）：`GlobalExceptionHandler` 以 `WriteAsJsonAsync(problemDetails, ct)` 輸出，Content-Type 為 `application/json; charset=utf-8`，不是規格要求的 `application/problem+json`。處理：只針對 503 分支改以 `contentType: "application/problem+json"` 輸出；並於 4.1 的 `AssertServiceUnavailable` 補 `ContentType` 斷言。補強前 RED（以 `verify-test-evidence.sh mutate` 暫時還原 503 分支的 `contentType` 為 `string? contentType = null;`，即補強前行為，重跑 `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~GlobalExceptionHandlerTests|FullyQualifiedName~DatabaseTimeoutHttpResponse"`；此為補強後重現的完整未過濾輸出，已還原且 `touch`，僅略去編譯 warning 行；原先實作期的第一次 RED 只留下單一測試的紀錄，已由本重現取代）：
      ```
      Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
      A total of 1 test files matched the specified pattern.
      [xUnit.net 00:00:00.50]     ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenPoolWaitTimeout_Writes503ServiceUnavailable [FAIL]
      [xUnit.net 00:00:00.51]     ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenTimeoutWrappedInDbUpdateException_Writes503ServiceUnavailable [FAIL]
        Failed ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenPoolWaitTimeout_Writes503ServiceUnavailable [30 ms]
        Error Message:
         Expected httpContext.Response.ContentType to start with "application/problem+json", but "application/json; charset=utf-8" differs near "jso" (index 12).
        Stack Trace:
           at FluentAssertions.Primitives.StringAssertions`1.StartWith(String expected, String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.AssertServiceUnavailable(DefaultHttpContext httpContext) in /src/tests/ProjectC.WebApi.Tests/ExceptionHandling/GlobalExceptionHandlerTests.cs:line 174
         at ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenPoolWaitTimeout_Writes503ServiceUnavailable() in /src/tests/ProjectC.WebApi.Tests/ExceptionHandling/GlobalExceptionHandlerTests.cs:line 116
      --- End of stack trace from previous location ---
        Failed ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenTimeoutWrappedInDbUpdateException_Writes503ServiceUnavailable [6 ms]
        Error Message:
         Expected httpContext.Response.ContentType to start with "application/problem+json", but "application/json; charset=utf-8" differs near "jso" (index 12).
        Stack Trace:
           at FluentAssertions.Primitives.StringAssertions`1.StartWith(String expected, String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.AssertServiceUnavailable(DefaultHttpContext httpContext) in /src/tests/ProjectC.WebApi.Tests/ExceptionHandling/GlobalExceptionHandlerTests.cs:line 174
         at ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenTimeoutWrappedInDbUpdateException_Writes503ServiceUnavailable() in /src/tests/ProjectC.WebApi.Tests/ExceptionHandling/GlobalExceptionHandlerTests.cs:line 128
      --- End of stack trace from previous location ---
      [xUnit.net 00:00:00.55]     ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenCommandTimeout_Writes503ServiceUnavailableWithTraceId [FAIL]
        Failed ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenCommandTimeout_Writes503ServiceUnavailableWithTraceId [5 ms]
        Error Message:
         Expected httpContext.Response.ContentType to start with "application/problem+json", but "application/json; charset=utf-8" differs near "jso" (index 12).
        Stack Trace:
           at FluentAssertions.Primitives.StringAssertions`1.StartWith(String expected, String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.AssertServiceUnavailable(DefaultHttpContext httpContext) in /src/tests/ProjectC.WebApi.Tests/ExceptionHandling/GlobalExceptionHandlerTests.cs:line 174
         at ProjectC.WebApi.Tests.ExceptionHandling.GlobalExceptionHandlerTests.TryHandleAsync_WhenCommandTimeout_Writes503ServiceUnavailableWithTraceId() in /src/tests/ProjectC.WebApi.Tests/ExceptionHandling/GlobalExceptionHandlerTests.cs:line 104
      --- End of stack trace from previous location ---
      [xUnit.net 00:00:06.32]     ProjectC.WebApi.Tests.Database.DatabaseTimeoutHttpResponseTests.GetEventById_WhenConnectionPoolExhausted_Returns503ProblemDetails [FAIL]
      [xUnit.net 00:00:07.53]     ProjectC.WebApi.Tests.Database.DatabaseTimeoutHttpResponseTests.GetEventById_WhenDatabaseCommandTimesOut_Returns503ProblemDetails [FAIL]
        Failed ProjectC.WebApi.Tests.Database.DatabaseTimeoutHttpResponseTests.GetEventById_WhenConnectionPoolExhausted_Returns503ProblemDetails [1 s]
        Error Message:
         Expected response.Content.Headers.ContentType?.MediaType to be a match with the expectation, but it differs at index 12:
                    ↓ (actual)
        "…plication/json"
        "…plication/problem+json"
                    ↑ (expected)
        Stack Trace:
           at FluentAssertions.Primitives.StringAssertions`1.Be(String expected, String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.Database.DatabaseTimeoutHttpResponseTests.AssertServiceUnavailableAsync(HttpResponseMessage response) in /src/tests/ProjectC.WebApi.Tests/Database/DatabaseTimeoutHttpResponseTests.cs:line 86
         at ProjectC.WebApi.Tests.Database.DatabaseTimeoutHttpResponseTests.GetEventById_WhenConnectionPoolExhausted_Returns503ProblemDetails() in /src/tests/ProjectC.WebApi.Tests/Database/DatabaseTimeoutHttpResponseTests.cs:line 75
         at ProjectC.WebApi.Tests.Database.DatabaseTimeoutHttpResponseTests.GetEventById_WhenConnectionPoolExhausted_Returns503ProblemDetails() in /src/tests/ProjectC.WebApi.Tests/Database/DatabaseTimeoutHttpResponseTests.cs:line 80
      --- End of stack trace from previous location ---
        Failed ProjectC.WebApi.Tests.Database.DatabaseTimeoutHttpResponseTests.GetEventById_WhenDatabaseCommandTimesOut_Returns503ProblemDetails [1 s]
        Error Message:
         Expected response.Content.Headers.ContentType?.MediaType to be a match with the expectation, but it differs at index 12:
                    ↓ (actual)
        "…plication/json"
        "…plication/problem+json"
                    ↑ (expected)
        Stack Trace:
           at FluentAssertions.Primitives.StringAssertions`1.Be(String expected, String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.Database.DatabaseTimeoutHttpResponseTests.AssertServiceUnavailableAsync(HttpResponseMessage response) in /src/tests/ProjectC.WebApi.Tests/Database/DatabaseTimeoutHttpResponseTests.cs:line 86
         at ProjectC.WebApi.Tests.Database.DatabaseTimeoutHttpResponseTests.GetEventById_WhenDatabaseCommandTimesOut_Returns503ProblemDetails() in /src/tests/ProjectC.WebApi.Tests/Database/DatabaseTimeoutHttpResponseTests.cs:line 49
         at ProjectC.WebApi.Tests.Database.DatabaseTimeoutHttpResponseTests.GetEventById_WhenDatabaseCommandTimesOut_Returns503ProblemDetails() in /src/tests/ProjectC.WebApi.Tests/Database/DatabaseTimeoutHttpResponseTests.cs:line 54
         at ProjectC.WebApi.Tests.Database.DatabaseTimeoutHttpResponseTests.GetEventById_WhenDatabaseCommandTimesOut_Returns503ProblemDetails() in /src/tests/ProjectC.WebApi.Tests/Database/DatabaseTimeoutHttpResponseTests.cs:line 54
         at ProjectC.WebApi.Tests.Database.DatabaseTimeoutHttpResponseTests.GetEventById_WhenDatabaseCommandTimesOut_Returns503ProblemDetails() in /src/tests/ProjectC.WebApi.Tests/Database/DatabaseTimeoutHttpResponseTests.cs:line 54
      --- End of stack trace from previous location ---

      Failed!  - Failed:     5, Passed:    12, Skipped:     0, Total:    17, Duration: 6 s - ProjectC.WebApi.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
      補強後 `FullyQualifiedName~GlobalExceptionHandlerTests|FullyQualifiedName~DatabaseTimeoutHttpResponse`：`Passed!  - Failed:     0, Passed:    17, Skipped:     0, Total:    17, Duration: 4 s - ProjectC.WebApi.Tests.dll (net10.0)`。
      既有 500／BadHttpRequest 分支同樣是 `application/json`（與 `docs/api-conventions.md`「所有錯誤回應為 `application/problem+json`」不符），本 change 不更動（見最終回報待決事項）。

## 5. 設定與文件

- [x] 5.1 k8s ConfigMap 與文件同步（TDD：N/A——純設定與文件，無自動化行為測試；以下列檢查取代，且此 N/A 只豁免 TDD 步驟）
  - `k8s/10-configmap.yaml` 新增 `Database__MaxPoolSize: "25"` 與註解（公式與預留見 design 決策 6）
  - `docs/todo/2026-11-02-deployment-prerequisites.md` 新增連線池容量小節：公式、目前 k8s（`(2 + 1) × 25 + 3 = 78 ≤ 100`）與 compose（單副本 100 = 100，沿用 k6 基準、未調整）數值、readiness 探測不經連線池但占 `max_connections`、`Database` 設定覆蓋連線字串同名參數
  - `docs/api-conventions.md` 第 2 節「框架層錯誤」補一條：資料庫逾時 → 503 `ServiceUnavailable`（含 `traceId`、無 `Detail`）
  - `docs/review/TODO.md` ⑧ 與 M-1 改為實際範圍（逾時＋池上限；retry 拆出為 `db-retry-execution-strategy`）並標示已實作／待歸檔
  - 驗證：`kubectl apply --dry-run=client -f k8s/10-configmap.yaml`（若本機無 kubectl 則改用 `docker run --rm -v ... bitnami/kubectl` 或記錄無法執行並以 YAML 語法檢查替代）；`bash scripts/check-doc-paths.sh`
  - 紀錄（原文）：
    - 已改：`k8s/10-configmap.yaml`（`Database__MaxPoolSize: "25"`＋公式註解）、`docs/todo/2026-11-02-deployment-prerequisites.md`（新增「5. 資料庫連線池容量」）、`docs/api-conventions.md`（框架層錯誤新增 503 條目）、`docs/review/TODO.md`（⑧ 列與 M-1 標題）。docs／k8s 檔保留 CRLF。
    - `kubectl apply --dry-run=client -f k8s/10-configmap.yaml` 輸出：`configmap/projectc-config created (dry run)`
    - `bash scripts/check-doc-paths.sh` 輸出：`OK: 所有 docs 路徑引用都存在`
    - 註：k8s 的 25 為估算值，未經 k6 實測（design Open Question，留待使用者確認）。

## 6. hardener 最終檢查與全套測試

- [x] 6.1 hardener 最終檢查（程式碼變更全部完成後套用 `.claude/skills/hardener/SKILL.md`，範圍：`AddPersistence` 的連線字串組裝、`GlobalExceptionHandler`／`DatabaseTimeoutDetector`；重點：連線字串缺漏或格式錯誤時的行為、偵測器的 null／循環例外鏈保護、日誌遮蔽、`CancellationToken`）
  - 發現缺少的防禦分支依 TDD 先補 RED 測試再修改，證據記於本 task；若無修改則記錄「已符合清單」
  - 紀錄：
    - 套用前必答：輸入驗證＝`Database` 設定以 DataAnnotations（`[Range]`）在啟動時 fail-fast；連線字串沿用既有來源、不新增外部輸入面。權限＝無（內部設定與例外轉換）。N+1＝不適用。悲觀鎖＝否（未新增並發操作）。
    - 連線字串缺漏（null／空）：`ApplyDatabaseOptions` 原樣回傳、不新增自訂例外；格式錯誤：`NpgsqlConnectionStringBuilder` 原生丟例外。新增 `DatabaseConnectionStringHardeningTests`（3 個）：缺漏不在組裝階段丟例外、格式錯誤的例外訊息不含密碼 sentinel。Npgsql 的例外訊息為函式庫行為，無法以本專案改動製造 RED，故這 3 個為 GREEN-only 的特徵測試（誠實標註，非 RED 驗證）：`Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 116 ms - ProjectC.WebApi.Tests.dll (net10.0)`
    - 偵測器補強：新增 `DatabaseTimeoutDetectorTests`（4 個：null→ArgumentNullException、AggregateException 第二個內層含逾時→true、循環例外鏈→終止且 false、裸 TimeoutException→false）。GREEN：`Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 22 ms - ProjectC.WebApi.Tests.dll (net10.0)`
    - 還原驗證（`verify-test-evidence.sh mutate`，一次一個改動，每次後 `touch`）：
      - 移除 `ArgumentNullException.ThrowIfNull(exception);` → `Failed ProjectC.WebApi.Tests.ExceptionHandling.DatabaseTimeoutDetectorTests.IsDatabaseTimeout_WhenExceptionIsNull_ThrowsArgumentNullException [25 ms]`：`Expected a <System.ArgumentNullException> to be thrown, but no exception was thrown.`；`Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4, Duration: 48 ms - ProjectC.WebApi.Tests.dll (net10.0)`；`結果：測試如預期失敗`
      - AggregateException 分支加 `&& false` → `Failed ProjectC.WebApi.Tests.ExceptionHandling.DatabaseTimeoutDetectorTests.IsDatabaseTimeout_WhenTimeoutIsInSecondInnerOfAggregate_ReturnsTrue [21 ms]`：`Expected DatabaseTimeoutDetector.IsDatabaseTimeout(aggregate) to be True, but found False.`；`Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4, Duration: 42 ms - ProjectC.WebApi.Tests.dll (net10.0)`；`結果：測試如預期失敗`
      - 循環例外鏈：初版測試未做 mutate 還原驗證（移除 visited 保護會使測試行程掛住）；strict-reviewer warning 1 後已補強，見下方「strict-reviewer warning 1、2 補強」。
    - 日誌遮蔽：503 路徑的 `LogWarning` 只記 `exception.GetType().Name`、Method、RouteTemplate、TraceId，不傳例外物件（AER-DB-005 以 sentinel 驗證不含連線字串／訊息）。已符合清單、無須修改。
    - `CancellationToken`：本次變更的程式碼沒有新增 async 方法簽章；`TryHandleAsync` 沿用既有 `cancellationToken` 傳給 `WriteAsJsonAsync`。已符合清單。
    - 例外邊界：偵測器為純函式、不吞例外；`GlobalExceptionHandler` 是最外層邊界。已符合清單。
    - 套用後有修改的檔案：僅新增測試檔，正式程式碼未因 hardener 修改。
- [x] 6.2 全套測試（各測試專案摘要行貼原文，不得以「全數通過」代替）
  - `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests`
  - `docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests`
  - 其餘測試專案（`ProjectC.Application.Tests`、`ProjectC.Domain.Tests`、`ProjectC.LoadTest.Seeder.Tests` 等，以 `ls tests` 為準）
  - 確認 `ServiceRegistrationSnapshot.baseline.txt` 是否需更新（`DatabaseOptions` 新增註冊）；若更新須在此記錄差異
  - 紀錄（原文）：
    - `Passed!  - Failed:     0, Passed:   985, Skipped:     0, Total:   985, Duration: 3 m 26 s - ProjectC.WebApi.Tests.dll (net10.0)`（首次全套為 `Failed!  - Failed:     1, Passed:   984, Skipped:     0, Total:   985, Duration: 3 m 44 s - ProjectC.WebApi.Tests.dll (net10.0)`，唯一失敗為 `ServiceRegistrationSnapshotTests.ServiceRegistrations_MatchCommittedBaseline`，見下）
    - `Passed!  - Failed:     0, Passed:   308, Skipped:     0, Total:   308, Duration: 1 m 7 s - ProjectC.Infrastructure.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   632, Skipped:     0, Total:   632, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   153, Skipped:     0, Total:   153, Duration: 87 ms - ProjectC.Domain.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 7 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)`
    - 基準檔：`ServiceRegistrationSnapshot.baseline.txt` 需更新。失敗（`Failed ProjectC.WebApi.Tests.Startup.ServiceRegistrationSnapshotTests.ServiceRegistrations_MatchCommittedBaseline [51 ms]`）訊息：`Expected (missing.Count + unexpected.Count) to be 0 because DI 圖與基準檔不一致。缺少的項目：；多出的項目：Singleton | Microsoft.Extensions.Options.IConfigureOptions`1[ProjectC.Application.Common.DatabaseOptions] | instance || Singleton | Microsoft.Extensions.Options.IOptionsChangeTokenSource`1[ProjectC.Application.Common.DatabaseOptions] | instance || Singleton | Microsoft.Extensions.Options.IValidateOptions`1[ProjectC.Application.Common.DatabaseOptions] | instance || Singleton | ProjectC.Application.Common.DatabaseOptions | factory, but found 4 (difference of 4).`；僅此 4 項（皆為 `DatabaseOptions` 的 Options 註冊，與其他 Options 同型），已手動依序插入基準檔（+4 行、無刪除），單檔重跑 `Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 374 ms - ProjectC.WebApi.Tests.dll (net10.0)`，再跑全套得上列 985 通過。
- [x] 6.3 Task 完成後告知：spec 同步狀態（`database-connection-limits` 新增、`api-error-responses` 補 503；`docs/api-conventions.md` 已同步）、AC 涵蓋、維護性提醒（重複邏輯、命名一致性）。k6／k8s 重跑列為建議，非驗收

- [x] 6.4 strict-reviewer warning 1、2 補強（TDD：測試強化，RED 以 mutate 取得；正式程式碼未改）
  - warning 1：`IsDatabaseTimeout_WhenExceptionChainIsCyclic_TerminatesAndReturnsFalse` 改為 `Task.Run` 執行偵測並 `Wait(TimeSpan.FromSeconds(5))`，保護失效時以逾時失敗而非掛住。
  - warning 2：`OpenConnection_WhenPoolExhausted_...` 補 `ChainContains<TimeoutException>(exception).Should().BeTrue(...)`。
  - 還原驗證（`verify-test-evidence.sh mutate`，一次一個，每次後 `touch`）原文：
    - 改動 `DatabaseTimeoutDetector.cs`：`if (!visited.Add(current))` → `if (!visited.Add(current) && false)`
      - `Failed ProjectC.WebApi.Tests.ExceptionHandling.DatabaseTimeoutDetectorTests.IsDatabaseTimeout_WhenExceptionChainIsCyclic_TerminatesAndReturnsFalse [5 s]`
      - `Expected detection.Wait(TimeSpan.FromSeconds(5)) to be True because 循環例外鏈必須在時限內走完, but found False.`
      - `Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4, Duration: 5 s - ProjectC.WebApi.Tests.dll (net10.0)`
      - `結果：測試如預期失敗`
    - 改動測試（證明新斷言有效；池耗盡實測例外鏈為 InvalidOperationException → NpgsqlException → TimeoutException）：`ChainContains<TimeoutException>` 換成 `ChainContains<ArgumentException>(exception).Should().BeTrue("mutated"`
      - `Failed ProjectC.WebApi.Tests.Database.DatabaseTimeoutBehaviorTests.OpenConnection_WhenPoolExhausted_FailsWithinConnectionTimeoutAndSucceedsAfterRelease [2 s]`
      - `Expected ChainContains<ArgumentException>(exception) to be True because mutated, but found False.`
      - `Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 2 s - ProjectC.WebApi.Tests.dll (net10.0)`
      - `結果：測試如預期失敗`
    - 註：第一次執行 mutation B 時 `--from` 在檔內出現 2 次，腳本輸出 `預期原字串恰好出現 1 次，實際 2 次` 並未改檔；改以唯一字串重做。
  - 還原後（兩個改動均已還原，grep 確認無殘留）相關測試：`Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 6 s - ProjectC.WebApi.Tests.dll (net10.0)`
  - WebApi 全套：`Passed!  - Failed:     0, Passed:   985, Skipped:     0, Total:   985, Duration: 4 m 7 s - ProjectC.WebApi.Tests.dll (net10.0)`
