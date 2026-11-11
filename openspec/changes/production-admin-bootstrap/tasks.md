# Tasks

> 所有 dotnet／測試指令一律在容器內執行（`docker compose exec api ...`）。RED／GREEN 證據依 CLAUDE.md「證據貼原文」填入各 task 的「完成紀錄」；提案階段只定義計畫。
> 實作這些 task 的 AC 對應：ADMIN-BOOT-001~010（見 specs/admin-bootstrap/spec.md）。

## 1. Options 與啟動期設定驗證

- [x] 1.1 新增 `AdminBootstrapOptions` 與 `AdminBootstrapOptionsValidator`（WebApi），並於 `ResolveEagerlyValidatedOptions` 觸發；對應 ADMIN-BOOT-005/006/007。
  - 先寫測試（WebApi.Tests/Startup，比照 `CaptchaOptionsFailFastTests`）：兩項皆空 → 啟動成功；只設 Email／只設 Password → 啟動失敗，且各寫一案、缺哪一項訊息就含哪一項的名稱；失敗例外文字一併收集並斷言不含密碼值（ADMIN-BOOT-010）；Email 格式錯、含前後空白、密碼 <8 碼／無數字／無字母 → 失敗；失敗訊息不含密碼值。上述 006／007 失敗案例另以 Development 環境各跑一次（驗證在任何環境皆觸發，ADMIN-BOOT-006/007 的「任何環境」宣稱）。
  - 預期 RED：Options／validator 尚不存在或未驗證，斷言「啟動應失敗」得到成功（Expected exception／Actual no exception），目標行為不符而非編譯錯誤（先建空殼型別使其可編譯）。
  - GREEN：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~AdminBootstrapOptions"` 全數通過。
  - 完成紀錄（1.1）：
    - RED（先建空殼 `AdminBootstrapOptionsValidator` 恆回 Success，驗證邏輯未實作；正式程式碼修改前）：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~AdminBootstrapOptions"`，目標行為不符（非編譯／環境錯誤），失敗測試與訊息原文（每一案皆為 `Expected a <System.Exception> to be thrown, but no exception was thrown.`）：
      ```
      Failed ProjectC.WebApi.Tests.Startup.AdminBootstrapOptionsFailFastTests.CreatingHost_WithWeakPassword_FailsMentioningPasswordSettingWithoutLeakingValue(environmentName: "Testing", password: "Ab1") [407 ms]
        Error Message: Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.AdminBootstrapOptionsFailFastTests.CreatingHost_WithWeakPassword_FailsMentioningPasswordSettingWithoutLeakingValue(environmentName: "Testing", password: "NoDigitsHere") [76 ms]
        Error Message: Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.AdminBootstrapOptionsFailFastTests.CreatingHost_WithWeakPassword_FailsMentioningPasswordSettingWithoutLeakingValue(environmentName: "Testing", password: "12345678") [39 ms]
        Error Message: Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.AdminBootstrapOptionsFailFastTests.CreatingHost_WithWeakPassword_FailsMentioningPasswordSettingWithoutLeakingValue(environmentName: "Development", password: "Ab1") [1 s]
        Error Message: Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.AdminBootstrapOptionsFailFastTests.CreatingHost_WithWeakPassword_FailsMentioningPasswordSettingWithoutLeakingValue(environmentName: "Development", password: "12345678") [112 ms]
        Error Message: Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.AdminBootstrapOptionsFailFastTests.CreatingHost_WithWeakPassword_FailsMentioningPasswordSettingWithoutLeakingValue(environmentName: "Development", password: "NoDigitsHere") [116 ms]
        Error Message: Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.AdminBootstrapOptionsFailFastTests.CreatingHost_WithOnlyPassword_FailsMentioningEmailSettingAndNotPassword(environmentName: "Development") [102 ms]
        Error Message: Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.AdminBootstrapOptionsFailFastTests.CreatingHost_WithOnlyPassword_FailsMentioningEmailSettingAndNotPassword(environmentName: "Testing") [58 ms]
        Error Message: Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.AdminBootstrapOptionsFailFastTests.CreatingHost_WithOnlyEmail_FailsMentioningPasswordSetting(environmentName: "Development") [75 ms]
        Error Message: Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.AdminBootstrapOptionsFailFastTests.CreatingHost_WithOnlyEmail_FailsMentioningPasswordSetting(environmentName: "Testing") [79 ms]
        Error Message: Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.AdminBootstrapOptionsFailFastTests.CreatingHost_WithInvalidEmail_FailsMentioningEmailSetting(environmentName: "Development", email: "not-an-email") [66 ms]
        Error Message: Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.AdminBootstrapOptionsFailFastTests.CreatingHost_WithInvalidEmail_FailsMentioningEmailSetting(environmentName: "Development", email: " first-admin@example.com") [53 ms]
        Error Message: Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.AdminBootstrapOptionsFailFastTests.CreatingHost_WithInvalidEmail_FailsMentioningEmailSetting(environmentName: "Testing", email: "first-admin@example.com ") [34 ms]
        Error Message: Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.AdminBootstrapOptionsFailFastTests.CreatingHost_WithInvalidEmail_FailsMentioningEmailSetting(environmentName: "Testing", email: " first-admin@example.com") [39 ms]
        Error Message: Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.WebApi.Tests.Startup.AdminBootstrapOptionsFailFastTests.CreatingHost_WithInvalidEmail_FailsMentioningEmailSetting(environmentName: "Testing", email: "not-an-email") [37 ms]
        Error Message: Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed!  - Failed:    15, Passed:     4, Skipped:     0, Total:    19, Duration: 2 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - GREEN（實作 validator 後同一命令）：
      ```
      Passed!  - Failed:     0, Passed:    19, Skipped:     0, Total:    19, Duration: 1 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - 重構：N/A（無重構）。偏離：Email 規則為 FluentValidation `EmailAddress()` 加上「不得含前後空白」的 `Must`（EmailAddress 本身會放行前後空白，已由測試案例覆蓋）。

## 2. ProductionAdminBootstrapper

- [x] 2.1 實作 `ProductionAdminBootstrapper`（Infrastructure）；對應 ADMIN-BOOT-001/002/003/005/008/010。先對照 hardener 清單列出防禦分支（參數、`CancellationToken` 傳遞、並發、例外邊界、日誌遮蔽）一併納入測試。
  - 先寫測試（Infrastructure.Tests/Seeding，Testcontainers，比照 `DevelopmentDataSeederTests`）：無 Admin → 建立 Admin 且無 Organizer、密碼為雜湊且可驗證；已有 Admin（不同 Email／已停用）→ 無變更；相同 Email 非 Admin → 拋明確例外且該會員不變，並對該例外訊息斷言不含設定 Email 值與資料庫既有會員 Email 值（ADMIN-BOOT-003 的驗證證據）；未設定 → 略過；有未套用 migration → 略過，並斷言 Warning 等級且訊息含未套用 migration 數量；已有 Admin 與未設定的略過路徑斷言為 Info 等級；內層非 SqlState 23505 的 `DbUpdateException` 一律重拋、23505 但約束非 Email 唯一索引時不視為「已存在」而重拋（避免例外過濾過寬吞掉其他例外）；取消的 `CancellationToken` → 拋 `OperationCanceledException`；以 `ILogger` 擷取全部日誌與例外訊息，斷言不含密碼與雜湊。
  - 預期 RED：bootstrapper 為空殼時，「應建立 Admin」得到 0 筆（Expected 1／Actual 0）等斷言失敗。
  - GREEN：`docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~ProductionAdminBootstrapper"` 通過。
  - 完成紀錄（2.1）：
    - RED（先建空殼 `ProductionAdminBootstrapper.BootstrapAsync` 直接回傳；正式邏輯修改前；測試皆為獨立全新資料庫 + Testcontainers）：`docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~ProductionAdminBootstrapper"`，目標行為不符（非編譯／環境錯誤），失敗測試與訊息原文：
      ```
      Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_OnCreateAndSkipPaths_NeverLogsPasswordHashOrEmail [935 ms]
          System.InvalidOperationException : Sequence contains no elements
      Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenOnlyOneSettingConfigured_ThrowsWithoutWritingOrLeakingPassword(email: "only-email@example.com", password: null) [606 ms]
          Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown.
      Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenOnlyOneSettingConfigured_ThrowsWithoutWritingOrLeakingPassword(email: null, password: "Boot-Passw0rd") [490 ms]
          Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown.
      Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenNoAdminExists_CreatesActiveAdminWithHashedPasswordAndNoOrganizer [623 ms]
          Expected var admin = members to contain a single item, but the collection is empty.
      Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenDatabaseUnreachable_Throws [5 ms]
          Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenSaveFailsForUnrelatedConstraint_RethrowsOriginalDbUpdateException(sqlState: "23503", constraintName: "FK_Something") [510 ms]
          Expected a <Microsoft.EntityFrameworkCore.DbUpdateException> to be thrown, but no exception was thrown.
      Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenSaveFailsForUnrelatedConstraint_RethrowsOriginalDbUpdateException(sqlState: "23505", constraintName: "IX_Other_Unique") [389 ms]
          Expected a <Microsoft.EntityFrameworkCore.DbUpdateException> to be thrown, but no exception was thrown.
      Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenRunTwice_CreatesOnlyOneAdmin [516 ms]
          Expected (LoadMembersAsync(connectionString)) to contain a single item, but the collection is empty.
      Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenConfiguredEmailBelongsToNonAdmin_ThrowsWithoutEmailInMessageAndLeavesMemberUnchanged [1 s]
          Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WithPendingMigrations_SkipsWithWarningContainingPendingCount [452 ms]
          Expected var warning = logger.Entries to contain a single item, but the collection is empty.
      Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenAnyAdminExists_ChangesNothingAndLogsInformation(isExistingAdminActive: True) [1 s]
          Expected logger.Entries to contain a single item, but the collection is empty.
      Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenAnyAdminExists_ChangesNothingAndLogsInformation(isExistingAdminActive: False) [826 ms]
          Expected logger.Entries to contain a single item, but the collection is empty.
      Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenNotConfigured_WritesNothingAndLogsInformation(email: "  ", password: "  ") [537 ms]
          Expected logger.Entries to contain a single item, but the collection is empty.
      Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenNotConfigured_WritesNothingAndLogsInformation(email: null, password: null) [602 ms]
          Expected logger.Entries to contain a single item, but the collection is empty.
      Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenNotConfigured_WritesNothingAndLogsInformation(email: "", password: "") [465 ms]
          Expected logger.Entries to contain a single item, but the collection is empty.
      Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenCancellationRequested_ThrowsOperationCanceledException [414 ms]
          Expected a <System.OperationCanceledException> to be thrown, but no exception was thrown.
      Failed!  - Failed:    16, Passed:     0, Skipped:     0, Total:    16, Duration: 9 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
    - GREEN（實作後同一命令；當時 2.2 測試尚未加入）：
      ```
      Passed!  - Failed:     0, Passed:    16, Skipped:     0, Total:    16, Duration: 9 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
    - 重構：N/A（無重構）。hardener 防禦分支已納入上列測試：只設定其一 → `InvalidOperationException`（直接呼叫時的最後防線）、取消 → `OperationCanceledException`、DB 不可達 → 例外外洩（fail-closed）、非 Email 唯一索引的 23505／其他 SqlState → 原 `DbUpdateException` 重拋、日誌與例外不含密碼／雜湊／Email。
    - 偏離／備註：非 Admin 會員衝突時拋出的 `InvalidOperationException` 刻意不帶內部例外（Npgsql 衝突訊息會含 `Key (Email)=(...)`），已由測試以 `exception.ToString()` 斷言不含 Email。
- [x] 2.2 並發：兩個獨立 `DbContext` 同時執行 bootstrapper；對應 ADMIN-BOOT-009。核心業務邏輯（並發）依 CLAUDE.md 加做還原驗證（`bash scripts/verify-test-evidence.sh restore` 或 `mutate`，例如暫時移除唯一性衝突處理）。
  - 先寫測試：(a) 確定性測試——以 EF `SaveChangesInterceptor`（或等價機制）在 bootstrapper 的 `SaveChanges` 前，另以獨立 DbContext 提交一筆 Email 與 `AdminBootstrap__Email` 完全相同、角色為 Admin 的會員，使 bootstrapper 的寫入實際收到 Email 唯一索引造成的 SqlState 23505；斷言以全新查詢確認該會員為 Admin 後視為成功、不拋例外，並斷言確實走過 23505 恢復分支（以攔截到的 `DbUpdateException` 或日誌／計數佐證，不得只斷言最終資料狀態）；(b) 兩個獨立 `DbContext` 同時執行的壓力測試。
  - 預期 RED：移除衝突處理的版本，其中一個執行緒拋出未處理的 `DbUpdateException`（Expected no exception／Actual DbUpdateException）；變異驗證針對移除 23505 恢復分支。
  - GREEN：同上 filter 通過；多次重跑（至少 20 次）皆通過。
  - 完成紀錄（2.2）：
    - 誠實紀錄：測試晚於實作——恢復分支在 2.1 實作時已存在（2.2 測試後補），故無「實作前的 RED」，改以 mutation 取得 RED 並兼作還原驗證；以 mutate 暫時改動正式程式碼（移除 23505 恢復分支，`when (IsEmailUniqueViolation(exception))` → `when (false)`，一次一個改動），目標行為不符：
      ```
      $ bash scripts/verify-test-evidence.sh mutate --file src/ProjectC.Infrastructure/Persistence/Seeding/ProductionAdminBootstrapper.cs --from "when (IsEmailUniqueViolation(exception))" --to "when (false)" -- docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~ProductionAdminBootstrapper"
      mutation: when (IsEmailUniqueViolation(exception)) -> when (false)
      $ docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter FullyQualifiedName~ProductionAdminBootstrapper
        Determining projects to restore...
        All projects are up-to-date for restore.
        ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
        ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
        ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
        ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
        ProjectC.Infrastructure.Tests -> /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll
      Test run for /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll (.NETCoreApp,Version=v10.0)
      A total of 1 test files matched the specified pattern.
      [xUnit.net 00:00:09.17]     ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenTwoInstancesRunConcurrently_CreatesExactlyOneAdminAndBothComplete [FAIL]
        Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenTwoInstancesRunConcurrently_CreatesExactlyOneAdminAndBothComplete [727 ms]
        Error Message:
         Did not expect any exception because 第 0 輪兩個實例皆須正常完成, but found Microsoft.EntityFrameworkCore.DbUpdateException: An error occurred while saving the entity changes. See the inner exception for details.
       ---> Npgsql.PostgresException (0x80004005): 23505: duplicate key value violates unique constraint "IX_Members_Email"
      
      DETAIL: Detail redacted as it may contain sensitive data. Specify 'Include Error Detail' in the connection string to include this information.
         at Npgsql.Internal.NpgsqlConnector.ReadMessageLong(Boolean async, DataRowLoadingMode dataRowLoadingMode, Boolean readingNotifications, Boolean isReadingPrependedMessage)
         at System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder`1.StateMachineBox`1.System.Threading.Tasks.Sources.IValueTaskSource<TResult>.GetResult(Int16 token)
         at Npgsql.NpgsqlDataReader.NextResult(Boolean async, Boolean isConsuming, CancellationToken cancellationToken)
         at Npgsql.NpgsqlDataReader.NextResult(Boolean async, Boolean isConsuming, CancellationToken cancellationToken)
         at Npgsql.NpgsqlCommand.ExecuteReader(Boolean async, CommandBehavior behavior, CancellationToken cancellationToken)
         at Npgsql.NpgsqlCommand.ExecuteReader(Boolean async, CommandBehavior behavior, CancellationToken cancellationToken)
         at Npgsql.NpgsqlCommand.ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Storage.RelationalCommand.ExecuteReaderAsync(RelationalCommandParameterObject parameterObject, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Storage.RelationalCommand.ExecuteReaderAsync(RelationalCommandParameterObject parameterObject, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Update.ReaderModificationCommandBatch.ExecuteAsync(IRelationalConnection connection, CancellationToken cancellationToken)
        Exception data:
          Severity: ERROR
          SqlState: 23505
          MessageText: duplicate key value violates unique constraint "IX_Members_Email"
          Detail: Detail redacted as it may contain sensitive data. Specify 'Include Error Detail' in the connection string to include this information.
          SchemaName: public
          TableName: Members
          ConstraintName: IX_Members_Email
          File: nbtinsert.c
          Line: 666
          Routine: _bt_check_unique
         --- End of inner exception stack trace ---
         at Microsoft.EntityFrameworkCore.Update.ReaderModificationCommandBatch.ExecuteAsync(IRelationalConnection connection, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Update.Internal.BatchExecutor.ExecuteAsync(IEnumerable`1 commandBatches, IRelationalConnection connection, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Update.Internal.BatchExecutor.ExecuteAsync(IEnumerable`1 commandBatches, IRelationalConnection connection, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Update.Internal.BatchExecutor.ExecuteAsync(IEnumerable`1 commandBatches, IRelationalConnection connection, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Storage.RelationalDatabase.SaveChangesAsync(IList`1 entries, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.ChangeTracking.Internal.StateManager.SaveChangesAsync(IList`1 entriesToSave, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.ChangeTracking.Internal.StateManager.SaveChangesAsync(StateManager stateManager, Boolean acceptAllChangesOnSuccess, CancellationToken cancellationToken)
         at Npgsql.EntityFrameworkCore.PostgreSQL.Storage.Internal.NpgsqlExecutionStrategy.ExecuteAsync[TState,TResult](TState state, Func`4 operation, Func`4 verifySucceeded, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(Boolean acceptAllChangesOnSuccess, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(Boolean acceptAllChangesOnSuccess, CancellationToken cancellationToken)
         at ProjectC.Infrastructure.Persistence.Seeding.ProductionAdminBootstrapper.CreateAdminAsync(CancellationToken cancellationToken) in /src/src/ProjectC.Infrastructure/Persistence/Seeding/ProductionAdminBootstrapper.cs:line 84
         at ProjectC.Infrastructure.Persistence.Seeding.ProductionAdminBootstrapper.BootstrapAsync(CancellationToken cancellationToken) in /src/src/ProjectC.Infrastructure/Persistence/Seeding/ProductionAdminBootstrapper.cs:line 69
         at ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.<>c__DisplayClass24_1.<<BootstrapAsync_WhenTwoInstancesRunConcurrently_CreatesExactlyOneAdminAndBothComplete>b__0>d.MoveNext() in /src/tests/ProjectC.Infrastructure.Tests/Seeding/ProductionAdminBootstrapperTests.cs:line 358
      --- End of stack trace from previous location ---
         at ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.<>c__DisplayClass24_1.<<BootstrapAsync_WhenTwoInstancesRunConcurrently_CreatesExactlyOneAdminAndBothComplete>b__0>d.MoveNext() in /src/tests/ProjectC.Infrastructure.Tests/Seeding/ProductionAdminBootstrapperTests.cs:line 358
      --- End of stack trace from previous location ---
         at FluentAssertions.Specialized.NonGenericAsyncFunctionAssertions.NotThrowAsync(String because, Object[] becauseArgs).
        Stack Trace:
           at FluentAssertions.Specialized.DelegateAssertionsBase`2.NotThrowInternal(Exception exception, String because, Object[] becauseArgs)
         at FluentAssertions.Specialized.NonGenericAsyncFunctionAssertions.NotThrowAsync(String because, Object[] becauseArgs)
         at ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenTwoInstancesRunConcurrently_CreatesExactlyOneAdminAndBothComplete() in /src/tests/ProjectC.Infrastructure.Tests/Seeding/ProductionAdminBootstrapperTests.cs:line 363
      --- End of stack trace from previous location ---
      [xUnit.net 00:00:15.60]     ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenAnotherInstanceCreatesSameAdminBeforeSave_TreatsEmailConflictAsSuccess [FAIL]
        Failed ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenAnotherInstanceCreatesSameAdminBeforeSave_TreatsEmailConflictAsSuccess [711 ms]
        Error Message:
         Did not expect any exception, but found Microsoft.EntityFrameworkCore.DbUpdateException: An error occurred while saving the entity changes. See the inner exception for details.
       ---> Npgsql.PostgresException (0x80004005): 23505: duplicate key value violates unique constraint "IX_Members_Email"
      
      DETAIL: Detail redacted as it may contain sensitive data. Specify 'Include Error Detail' in the connection string to include this information.
         at Npgsql.Internal.NpgsqlConnector.ReadMessageLong(Boolean async, DataRowLoadingMode dataRowLoadingMode, Boolean readingNotifications, Boolean isReadingPrependedMessage)
         at System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder`1.StateMachineBox`1.System.Threading.Tasks.Sources.IValueTaskSource<TResult>.GetResult(Int16 token)
         at Npgsql.NpgsqlDataReader.NextResult(Boolean async, Boolean isConsuming, CancellationToken cancellationToken)
         at Npgsql.NpgsqlDataReader.NextResult(Boolean async, Boolean isConsuming, CancellationToken cancellationToken)
         at Npgsql.NpgsqlCommand.ExecuteReader(Boolean async, CommandBehavior behavior, CancellationToken cancellationToken)
         at Npgsql.NpgsqlCommand.ExecuteReader(Boolean async, CommandBehavior behavior, CancellationToken cancellationToken)
         at Npgsql.NpgsqlCommand.ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Storage.RelationalCommand.ExecuteReaderAsync(RelationalCommandParameterObject parameterObject, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Storage.RelationalCommand.ExecuteReaderAsync(RelationalCommandParameterObject parameterObject, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Update.ReaderModificationCommandBatch.ExecuteAsync(IRelationalConnection connection, CancellationToken cancellationToken)
        Exception data:
          Severity: ERROR
          SqlState: 23505
          MessageText: duplicate key value violates unique constraint "IX_Members_Email"
          Detail: Detail redacted as it may contain sensitive data. Specify 'Include Error Detail' in the connection string to include this information.
          SchemaName: public
          TableName: Members
          ConstraintName: IX_Members_Email
          File: nbtinsert.c
          Line: 666
          Routine: _bt_check_unique
         --- End of inner exception stack trace ---
         at Microsoft.EntityFrameworkCore.Update.ReaderModificationCommandBatch.ExecuteAsync(IRelationalConnection connection, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Update.Internal.BatchExecutor.ExecuteAsync(IEnumerable`1 commandBatches, IRelationalConnection connection, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Update.Internal.BatchExecutor.ExecuteAsync(IEnumerable`1 commandBatches, IRelationalConnection connection, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Update.Internal.BatchExecutor.ExecuteAsync(IEnumerable`1 commandBatches, IRelationalConnection connection, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Storage.RelationalDatabase.SaveChangesAsync(IList`1 entries, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.ChangeTracking.Internal.StateManager.SaveChangesAsync(IList`1 entriesToSave, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.ChangeTracking.Internal.StateManager.SaveChangesAsync(StateManager stateManager, Boolean acceptAllChangesOnSuccess, CancellationToken cancellationToken)
         at Npgsql.EntityFrameworkCore.PostgreSQL.Storage.Internal.NpgsqlExecutionStrategy.ExecuteAsync[TState,TResult](TState state, Func`4 operation, Func`4 verifySucceeded, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(Boolean acceptAllChangesOnSuccess, CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(Boolean acceptAllChangesOnSuccess, CancellationToken cancellationToken)
         at ProjectC.Infrastructure.Persistence.Seeding.ProductionAdminBootstrapper.CreateAdminAsync(CancellationToken cancellationToken) in /src/src/ProjectC.Infrastructure/Persistence/Seeding/ProductionAdminBootstrapper.cs:line 84
         at ProjectC.Infrastructure.Persistence.Seeding.ProductionAdminBootstrapper.BootstrapAsync(CancellationToken cancellationToken) in /src/src/ProjectC.Infrastructure/Persistence/Seeding/ProductionAdminBootstrapper.cs:line 69
         at FluentAssertions.Specialized.NonGenericAsyncFunctionAssertions.NotThrowAsync(String because, Object[] becauseArgs).
        Stack Trace:
           at FluentAssertions.Specialized.DelegateAssertionsBase`2.NotThrowInternal(Exception exception, String because, Object[] becauseArgs)
         at FluentAssertions.Specialized.NonGenericAsyncFunctionAssertions.NotThrowAsync(String because, Object[] becauseArgs)
         at ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenAnotherInstanceCreatesSameAdminBeforeSave_TreatsEmailConflictAsSuccess() in /src/tests/ProjectC.Infrastructure.Tests/Seeding/ProductionAdminBootstrapperTests.cs:line 324
         at ProjectC.Infrastructure.Tests.Seeding.ProductionAdminBootstrapperTests.BootstrapAsync_WhenAnotherInstanceCreatesSameAdminBeforeSave_TreatsEmailConflictAsSuccess() in /src/tests/ProjectC.Infrastructure.Tests/Seeding/ProductionAdminBootstrapperTests.cs:line 325
      --- End of stack trace from previous location ---
      
      Failed!  - Failed:     2, Passed:    16, Skipped:     0, Total:    18, Duration: 10 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
    - 還原驗證後工作樹：`git status --short` 顯示正式程式碼為還原後內容（`grep -c "IsEmailUniqueViolation(exception))"` 為 1）；注意腳本還原後須 `touch` 該檔再重建，否則 msbuild 沿用 mutation 的舊 DLL（曾因此誤得 2 failed，touch 後 18 passed）。
    - GREEN（touch 後重建，2.1 + 2.2 全部）：
      ```
      $ docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~ProductionAdminBootstrapper"
      Passed!  - Failed:     0, Passed:    18, Skipped:     0, Total:    18, Duration: 10 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
    - 連跑 20 次（逐輪原文；每輪命令相同，`--no-build`，filter 為兩個並發相關測試；2026-11-11 外部審查 B-001 後重跑取代原彙總紀錄）：
      ```
      # run 1: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 2: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 3: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 4: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 5: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 6: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 7: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 8: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 9: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 10: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 11: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 12: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 13: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 14: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 15: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 16: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 17: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 18: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 19: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      # run 20: docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~WhenTwoInstancesRunConcurrently|FullyQualifiedName~WhenAnotherInstanceCreatesSameAdminBeforeSave"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```

## 3. 註冊與啟動整合

- [x] 3.1 於 `WebApi/Extensions` 新增 DI 註冊（Scoped）並在 `Program.cs` 於 seeder 區塊旁以 `!IsDevelopment()` 呼叫；更新 `ServiceRegistrationSnapshot.baseline.txt`；對應 ADMIN-BOOT-004。
  - 先寫測試（WebApi.Tests，以 `WebApplicationFactory` 走真實啟動流程，被測主體為 `Program.cs` 的環境判斷，不得只測抽出的輔助方法）：成對的兩個測試，皆設定合法的 `AdminBootstrap__*` 且 `DevelopmentSeed` 未設定、資料庫無 Admin——Development 環境啟動後資料庫無 Admin 會員；Testing 環境啟動後有該 Email 的 Admin，並以該 Email 與密碼經既有登入流程（測試環境驗證碼可處理）取得 token。另加一案 Production 環境，只斷言 Admin 已建立、不登入，且不得設定 `Captcha:VerificationMode=AcceptAny`（Production 禁用）也不得帶入 `Captcha__*`。其他既有整合測試的 `CustomWebApplicationFactory` 不得帶入 `AdminBootstrap__*`（Testing 環境有設定即會執行）。
  - 預期 RED：未接上啟動流程時，非 Development 啟動後 Admin 不存在（Expected 1／Actual 0）；並以暫時移除或反轉 `!IsDevelopment()` 條件做變異驗證（`bash scripts/verify-test-evidence.sh mutate`），確認 Development 那個測試會失敗。
  - GREEN：`ServiceRegistrationSnapshotTests` 與新測試通過；整合測試使用 Testcontainers，不連線開發用 db。
  - 完成紀錄（3.1）：
    - RED（測試已寫、`Program.cs` 尚未呼叫 bootstrapper、`ProductionAdminBootstrapper` 尚未註冊；目標行為不符）：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ProductionAdminBootstrapStartupTests"`：
      ```
        Determining projects to restore...
        All projects are up-to-date for restore.
        ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
        ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
        ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
        ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
        ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
      Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
      A total of 1 test files matched the specified pattern.
      [xUnit.net 00:00:06.69]     ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InTestingWithBootstrapConfigured_CreatesAdminWhoCanLoginWithoutOrganizer [FAIL]
        Failed ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InTestingWithBootstrapConfigured_CreatesAdminWhoCanLoginWithoutOrganizer [1 s]
        Error Message:
         Expected var admin = (LoadAdminsAsync()) to contain a single item, but the collection is empty.
        Stack Trace:
           at FluentAssertions.Collections.GenericCollectionAssertions`3.ContainSingle(String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InTestingWithBootstrapConfigured_CreatesAdminWhoCanLoginWithoutOrganizer() in /src/tests/ProjectC.WebApi.Tests/Startup/ProductionAdminBootstrapStartupTests.cs:line 77
      --- End of stack trace from previous location ---
      [xUnit.net 00:00:07.07]     ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InProductionWithBootstrapConfigured_CreatesAdmin [FAIL]
      [xUnit.net 00:00:07.47]     ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InProductionWhenAdminAlreadyExists_DoesNotCreateSecondAdmin [FAIL]
        Failed ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InProductionWithBootstrapConfigured_CreatesAdmin [377 ms]
        Error Message:
         Expected var admin = (LoadAdminsAsync()) to contain a single item, but the collection is empty.
        Stack Trace:
           at FluentAssertions.Collections.GenericCollectionAssertions`3.ContainSingle(String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InProductionWithBootstrapConfigured_CreatesAdmin() in /src/tests/ProjectC.WebApi.Tests/Startup/ProductionAdminBootstrapStartupTests.cs:line 99
      --- End of stack trace from previous location ---
        Failed ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InProductionWhenAdminAlreadyExists_DoesNotCreateSecondAdmin [206 ms]
        Error Message:
         Expected (LoadAdminsAsync()) to contain a single item, but the collection is empty.
        Stack Trace:
           at FluentAssertions.Collections.GenericCollectionAssertions`3.ContainSingle(String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InProductionWhenAdminAlreadyExists_DoesNotCreateSecondAdmin() in /src/tests/ProjectC.WebApi.Tests/Startup/ProductionAdminBootstrapStartupTests.cs:line 116
      --- End of stack trace from previous location ---

      Failed!  - Failed:     3, Passed:     2, Skipped:     0, Total:     5, Duration: 1 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
      （Development 案例與 base factory 設定案例在實作前本就通過，Development 案例靠下方 mutation 證明有效。）
    - 發現並修正測試設定：第一版 Development 案例以 `UseSetting` 清空 `DevelopmentSeed`，因 api 容器環境變數（.env 的 `DevelopmentSeed__*`）優先序較高，展示用 seeder 仍建立 Admin 而使該案例失敗；改以較晚加入的 `AddInMemoryCollection` 明確清空後才是乾淨前置條件。
    - 快照 RED：`ServiceRegistrationSnapshotTests` 在新增註冊後失敗，訊息指出多出 4 項（Scoped `ProductionAdminBootstrapper`、`IConfigureOptions`／`IOptionsChangeTokenSource`／`IValidateOptions` 各一，皆 `AdminBootstrapOptions`）；以 `UPDATE_SERVICE_REGISTRATION_SNAPSHOT=1` 重新產生基準檔，`git diff` 恰好 +4 行，與上述一致。
      快照測試失敗原文（完整訊息，單行）：
      ```
         Expected (missing.Count + unexpected.Count) to be 0 because DI 圖與基準檔不一致。缺少的項目：；多出的項目：Scoped | ProjectC.Infrastructure.Persistence.Seeding.ProductionAdminBootstrapper | ProjectC.Infrastructure.Persistence.Seeding.ProductionAdminBootstrapper || Singleton | Microsoft.Extensions.Options.IConfigureOptions`1[ProjectC.Infrastructure.Persistence.Seeding.AdminBootstrapOptions] | instance || Singleton | Microsoft.Extensions.Options.IOptionsChangeTokenSource`1[ProjectC.Infrastructure.Persistence.Seeding.AdminBootstrapOptions] | instance || Singleton | Microsoft.Extensions.Options.IValidateOptions`1[ProjectC.Infrastructure.Persistence.Seeding.AdminBootstrapOptions] | ProjectC.WebApi.Extensions.AdminBootstrapOptionsValidator, but found 4 (difference of 4).
      Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 572 ms - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - 變異驗證（`bash scripts/verify-test-evidence.sh mutate --file src/ProjectC.WebApi/Program.cs --from "if (app.Environment.IsDevelopment())\n    {\n        using var seedScope" --to "if (!app.Environment.IsDevelopment())..."`，反轉環境條件）：
      ```
      mutation:     if (app.Environment.IsDevelopment())
          {
              using var seedScope ->     if (!app.Environment.IsDevelopment())
          {
              using var seedScope
      $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~ProductionAdminBootstrapStartupTests
        Determining projects to restore...
        All projects are up-to-date for restore.
        ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
        ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
        ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
        ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
        ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
      Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
      A total of 1 test files matched the specified pattern.
      [xUnit.net 00:00:09.66]     ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InTestingWithBootstrapConfigured_CreatesAdminWhoCanLoginWithoutOrganizer [FAIL]
        Failed ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InTestingWithBootstrapConfigured_CreatesAdminWhoCanLoginWithoutOrganizer [2 s]
        Error Message:
         Expected var admin = (LoadAdminsAsync()) to contain a single item, but the collection is empty.
        Stack Trace:
           at FluentAssertions.Collections.GenericCollectionAssertions`3.ContainSingle(String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InTestingWithBootstrapConfigured_CreatesAdminWhoCanLoginWithoutOrganizer() in /src/tests/ProjectC.WebApi.Tests/Startup/ProductionAdminBootstrapStartupTests.cs:line 77
      --- End of stack trace from previous location ---
      [xUnit.net 00:00:09.90]     ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InProductionWithBootstrapConfigured_CreatesAdmin [FAIL]
      [xUnit.net 00:00:10.44]     ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InDevelopmentWithBootstrapConfigured_DoesNotCreateAdmin [FAIL]
      [xUnit.net 00:00:10.64]     ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InProductionWhenAdminAlreadyExists_DoesNotCreateSecondAdmin [FAIL]
        Failed ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InProductionWithBootstrapConfigured_CreatesAdmin [239 ms]
        Error Message:
         Expected var admin = (LoadAdminsAsync()) to contain a single item, but the collection is empty.
        Stack Trace:
           at FluentAssertions.Collections.GenericCollectionAssertions`3.ContainSingle(String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InProductionWithBootstrapConfigured_CreatesAdmin() in /src/tests/ProjectC.WebApi.Tests/Startup/ProductionAdminBootstrapStartupTests.cs:line 99
      --- End of stack trace from previous location ---
        Failed ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InDevelopmentWithBootstrapConfigured_DoesNotCreateAdmin [538 ms]
        Error Message:
         Expected (LoadAdminsAsync()) to be empty because Development 由既有展示用 seeder 負責，不執行此機制, but found at least one item {
          ProjectC.Domain.Members.Member
          {
              DisplayName = "Administrator",
              Email = "user-0e6d20459b124481983926121a8464f5@example.com",
              HasRegisteredRealName = False,
              Id = {217b3ace-65f5-471e-a8b8-82e724728b5c},
              IsActive = True,
              NationalIdLast4 = <null>,
              PasswordHash = "$2a$11$xzpFUUO9pMPDO3iytRJlSOC.VvCgVOfXaPItyZTMaF4B9jM/arD9W",
              RealName = <null>,
              Role = MemberRole.Admin {value: 1}
          }
      }.
        Stack Trace:
           at FluentAssertions.Collections.GenericCollectionAssertions`3.<>c__DisplayClass10_0.<BeEmpty>b__0(AssertionChain chain)
         at FluentAssertions.Collections.GenericCollectionAssertions`3.BeEmpty(String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InDevelopmentWithBootstrapConfigured_DoesNotCreateAdmin() in /src/tests/ProjectC.WebApi.Tests/Startup/ProductionAdminBootstrapStartupTests.cs:line 65
      --- End of stack trace from previous location ---
        Failed ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InProductionWhenAdminAlreadyExists_DoesNotCreateSecondAdmin [199 ms]
        Error Message:
         Expected (LoadAdminsAsync()) to contain a single item, but the collection is empty.
        Stack Trace:
           at FluentAssertions.Collections.GenericCollectionAssertions`3.ContainSingle(String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.Startup.ProductionAdminBootstrapStartupTests.Startup_InProductionWhenAdminAlreadyExists_DoesNotCreateSecondAdmin() in /src/tests/ProjectC.WebApi.Tests/Startup/ProductionAdminBootstrapStartupTests.cs:line 116
      --- End of stack trace from previous location ---

      Failed!  - Failed:     4, Passed:     1, Skipped:     0, Total:     5, Duration: 3 s - ProjectC.WebApi.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
    - GREEN（還原並 touch 後；含快照、啟動整合、Options 失敗測試）：
      ```
      $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ServiceRegistrationSnapshotTests|FullyQualifiedName~ProductionAdminBootstrapStartupTests|FullyQualifiedName~AdminBootstrapOptions"
      Passed!  - Failed:     0, Passed:    25, Skipped:     0, Total:    25, Duration: 3 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - 重構：N/A。偏離／備註：Production 案例以 `CustomWebApplicationFactory`（FakeCaptcha 替身）執行，只斷言 `Captcha:VerificationMode` 設定為空且 Admin 已建立，不登入。

## 4. 部署設定與文件

- [x] 4.1 更新 `k8s/10-configmap.yaml`（`ASPNETCORE_ENVIRONMENT: Production`、`AdminBootstrap__Email`、移除 `DevelopmentSeed__*` 與過時註解）、`k8s/examples/secret.example.yaml`、`scripts/verify-k8s-manifests.sh`（`SECRET_KEYS` 與產生的 secret 改為 `AdminBootstrap__Password`）。`secret.example.yaml` 的佔位值須為不符合密碼強度規則的明顯佔位（例如 `CHANGE-ME-REQUIRED`，無數字），使未替換就部署時啟動驗證失敗，而非以公開範例密碼建出 Admin。TDD：N/A（部署設定），驗證見 4.3。
- [x] 4.2 更新 `docs/todo/2026-11-10-deployment-env-vars.md`（新鍵、移除 Development 專用說明）、`docs/todo/2026-11-02-deployment-prerequisites.md`／`docs/todo/2026-10-28-k8s-todo.md` 中「首位 Admin bootstrap 待辦」的敘述；註明 K8s 3.3 瀏覽器流程需改走會員申請 + Admin 核准；建立首位 Admin 後應移除 `AdminBootstrap__Password`；本機制不提供密碼輪替；若 Secret 殘留密碼，Admin 全被刪除後重啟會依該密碼重建（移除後此路徑才不會意外觸發）。文件須明列範例密碼佔位值必須替換。驗證：先以 grep 核對文件中引用的檔案行號與敘述屬實，再 `grep -rn "DevelopmentSeed" k8s docs/todo scripts` 無過時殘留。TDD：N/A（純文件）。
  - 完成紀錄（4.1／4.2，TDD N/A：部署設定與純文件，驗證見 4.3）：
    - 4.1：`k8s/10-configmap.yaml` 改 `ASPNETCORE_ENVIRONMENT: "Production"`、新增 `AdminBootstrap__Email`、移除 `DevelopmentSeed__*` 與 Development 註解；`secret.example.yaml` 的 `AdminBootstrap__Password` 佔位值為 `CHANGE-ME-REQUIRED`（符合 `MustBeStrongPassword` 前須有數字，此值無數字故未替換即啟動失敗）；`verify-k8s-manifests.sh` 的 `SECRET_KEYS`、產生 secret、`secret_values` 三處改 `AdminBootstrap__Password`。
    - 4.2：核對行號後更新 `deployment-env-vars`（兩鍵改 AdminBootstrap、刪 OrganizerName、ASPNETCORE_ENVIRONMENT 說明）、`k8s-todo`（Secret／ConfigMap 清單、第 3 項改為已決定 Production + 3.3 改走會員申請與 Admin 核准）、`deployment-prerequisites` 第 51 行標註已處理；明列佔位值必須替換、建立後移除 Password、無密碼輪替、殘留 Secret 的重建路徑。
    - `grep -rn "DevelopmentSeed" k8s docs/todo scripts` 輸出：（空，exit 1）
- [x] 4.3 重跑 `bash scripts/verify-k8s-manifests.sh`（`ASPNETCORE_ENVIRONMENT=Production`）並貼原文輸出，確認 api 就緒（含 `/health/ready`），並以 DB 查詢確認 Admin 已建立（Production 不可停用驗證碼，故不以腳本登入；登入由 3.1 整合測試覆蓋，必要時另以真實瀏覽器手動登入）；確認 OpenAPI／SwaggerUI 在 Production 不可用。完成紀錄見下。
  - 完成紀錄（4.3；TDD N/A）：先以 `docker build -f deploy/api.Dockerfile --target runtime -t projectc/api:local .` 重建含本次程式碼的映像（原映像為 4 小時前建置，不含 bootstrapper）。
    - 第 1 次 `bash scripts/verify-k8s-manifests.sh`：`SUMMARY: 13 passed, 1 failed, 1 unverified`，失敗為斷言 4：
      ```
      FAIL [4] recovered=1，請求總數=18（至少 20），非 2xx=0
      ---- 實際輸出 ----
           18 200
      NAME                     READY   STATUS      RESTARTS   AGE
      ```
      斷言 4 是刪除 Pod 期間以固定時間窗送請求並要求至少 20 次，總數 18 屬時間窗內吞吐不足（非 2xx=0，無任何請求失敗），與本次變更無關聯；第 2 次重跑即通過，視為腳本既有的時序不穩定，如實記錄、不當作已排除原因。
    - 第 2 次（`KEEP_UP=1`）全文：
      ```
      PASS [0] manifest 覆寫為 Namespace projectc-verify、NodePort 30081
      PASS [1] db／redis／seq／api／web 就緒（套用 NetworkPolicy 後仍就緒），migrator Complete，migration 筆數 16（預期 16），api 最大 restartCount=0
      PASS [2a] 探針路徑：liveness=/health/live readiness=/health/ready startup=/health/live
      PASS [2b] db 停止後 api Service endpoints 在 90 秒內清空（readiness 轉為 NotReady）
      PASS [2c] db 恢復後 api endpoints 在 150 秒內回到 Ready
      PASS [3a] GET http://localhost:30081/ → 200
      PASS [3b] GET /api/events → 200（2xx，非 3xx）
      PASS [4] 刪除 Pod api-57769dcd57-gdwml 期間 20 次請求全為 2xx，Deployment 回到 2 個 Ready
      PASS [5] rollout restart 期間 34 次請求全為 2xx
      PASS [6] 5 個隨機機敏值未出現在 logs、describe、k8s/、版控；Secret 鍵名不在 ConfigMap
      PASS [7] 缺 Jwt__SigningKey 時 api 進入 CrashLoopBackOff，上次結束碼=139
      PASS [8a] 經 web 取不到 /health/live、/health/ready、/api/health/*（回應不是 Healthy）
      PASS [8b] kubectl port-forward 到 api 可取得 /health/live（維運來源可達）
      UNVERIFIED [8c] 本機 CNI 不執行 NetworkPolicy（deny-all 保護的 Pod 仍可連通），無法驗證不可信 workload 不可達；見 docs/todo/2026-11-10-networkpolicy-enforcement-todo.md
      PASS [9] api 為 ClusterIP、僅 web 為 NodePort（30081）、api／web／migrator 皆 runAsNonRoot、OpenAPI／SwaggerUI 不可經 web 取得
      SUMMARY: 14 passed, 0 failed, 1 unverified
      UNVERIFIED: 8c
      EXIT=3
      ```
    - 保留的 `projectc-verify` 上以 kubectl 核對（之後已刪除 namespace）：ConfigMap `ASPNETCORE_ENVIRONMENT=Production`；DB `Members` 依 Role／IsActive 分組恰 1 列（Role=1 即 Admin、IsActive=t），`Organizers` 筆數 0；api 日誌 `Admin bootstrap skipped: an Admin already exists.`（另一副本先建立）；經 port-forward 直連 api：`/openapi/v1.json -> 404`、`/swagger -> 404`、`/health/ready -> 200`。
    - 未以腳本或瀏覽器登入（Production 不可停用驗證碼；登入由 3.1 的 Testing 啟動整合測試覆蓋）。

## 5. 最終檢查

- [x] 5.1 hardener 最終檢查：對 `ProductionAdminBootstrapper` 套用 `.claude/skills/hardener/SKILL.md` 清單並記錄；有補強依 TDD 先補 RED 測試。
  - 完成紀錄（5.1 hardener，層級＝Infrastructure 啟動元件；套用前必答：輸入驗證＝`AdminBootstrapOptionsValidator`＋`IValidateOptions` 於啟動時、bootstrapper 直接呼叫時另有只含設定項名稱的最後防線；權限＝非 HTTP 路徑、無使用者觸發面；N+1＝無；悲觀鎖＝否，改以 Email 唯一索引＋衝突後重查 Admin 序列化多副本並發）：
    - 參數驗證：兩項皆空→略過；僅一項空→丟 `InvalidOperationException`（測試：only-one-setting）；格式／強度由 validator 擋（19 個 fail-fast 測試）。
    - Entity 存在性／狀態機：已有任何 Admin（啟用或停用）→略過（測試覆蓋）；非 Admin 會員占用 Email→啟動失敗、不改該會員、訊息不含 Email（測試覆蓋）。
    - CancellationToken：`GetPendingMigrationsAsync`、`AnyAsync`、`SaveChangesAsync` 皆傳遞，無自建 timeout；取消測試覆蓋。
    - 並發：23505＋`IX_Members_Email` 且已有 Admin→視為成功（`CompetingInsertInterceptor` 確定性測試＋2 並發×10 輪壓力測試）；非 Email 的 23505／23503 原樣重拋（測試覆蓋）。
    - 例外邊界：屬情境 A（啟動必要步驟）；未包 catch-all，DB 不可達、取消皆往外拋使啟動失敗（DB 不可達測試覆蓋）；衝突例外刻意不帶內部例外（含 Email）。
    - 日誌：具名 placeholder、只記 `MemberId` 與 pending migration 數量，不含 Email／密碼／雜湊（log-leak 測試覆蓋）。
    - 結論：檢查清單無缺漏分支，未修改程式碼，故不需 RED 補強、不需重跑 `strict-reviewer`。不確定項（交由使用者）：Email 不做大小寫正規化，與既有登入的精確比對一致；若日後登入改為不分大小寫須同步處理。
- [x] 5.2 全套測試（api 容器內 `dotnet test` 各測試專案）貼各專案摘要行原文，0 failed／0 skipped；`graphify update .`。
  - 完成紀錄（5.2；各專案命令為 `docker compose exec -T api dotnet test tests/ProjectC.<Domain／Application／LoadTest.Seeder／Infrastructure／WebApi>.Tests`，各專案摘要行原文）：
    ```
    Passed!  - Failed:     0, Passed:   153, Skipped:     0, Total:   153, Duration: 128 ms - ProjectC.Domain.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   625, Skipped:     0, Total:   625, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 7 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   300, Skipped:     0, Total:   300, Duration: 1 m 4 s - ProjectC.Infrastructure.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   866, Skipped:     0, Total:   866, Duration: 4 m 58 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
    - `graphify update .` 已執行。
- [x] 5.3 spec 同步檢查：確認實作與 specs/admin-bootstrap 一致，偏離處記入 design.md。
  - 完成紀錄（5.3）：逐條對照 specs/admin-bootstrap ADMIN-BOOT-001～010，皆有對應測試且通過（001／004／005 啟動整合與 Options 測試、002／003／008／009／010 bootstrapper 測試、006／007 fail-fast 測試）。無偏離規格之處，故未修改 design.md。主 spec 尚未同步（archive 時才同步）。實作中的非規格事項：Role 沿用 `Entry(...).Property(Role)` 設定（與 DevelopmentDataSeeder 相同的已知技術債）。

## Workflow follow-up

- strict-reviewer 與全新對抗式 subagent 審查迴圈至無 blocking（CLAUDE.md 開發流程步驟 6）。
- 歸檔（`/opsx:archive`，含主 spec 同步）後，詢問是否以 `scripts/archive-todo.sh` 搬移已完成的 `docs/todo/` 項目；合併回 master。

## 6. 外部交叉審查紀錄（cross-review，實作階段）

- 第 1 輪：Devin CLI（GPT-6 Luna Medium Thinking，smart 模式），審查 commit d3e1fab。verdict FAIL，blocking 1 項：B-001（task 2.2「連跑 20 次」以 `Duration: N s` 佔位彙總呈現，非逐輪原文，位置 tasks.md 2.2 完成紀錄）。驗證成立；處理：重跑 20 次並將逐輪原文寫入 2.2，另展開 5.2 的命令、移除兩處行尾空白（只改證據，程式碼未動）。Devin 的 .NET 測試未跑完、無通過結論；subagent 另列的 B-002（5.2 證據）Devin 主審已降為建議，不採納為 blocking。
- 第 2 輪：Devin CLI（GPT-6 Luna Medium Thinking，smart 模式），審查 commit 956454d。verdict FAIL，blocking 1 項：B-002（task 2.2 mutation RED 的第一個失敗訊息停在 `See the i`，被截斷）。前輪 B-001 回歸核對：已修正。驗證成立（原檔該行確實截斷）；處理：重跑同一 mutate 指令並把完整原始輸出（兩個失敗案例的 Error Message、內層 `Npgsql.PostgresException 23505 ... IX_Members_Email`、Stack Trace、摘要行）貼入 2.2 完成紀錄；還原後 `git status --short` 乾淨、`grep -c "IsEmailUniqueViolation(exception))"` 為 1。
