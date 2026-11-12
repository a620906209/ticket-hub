## 1. 系統清理路徑的並發取消測試（OCLE-009、OCLE-010）

- [x] 1.1 新增 `CancelExpiredOrderAsync` 並發取消測試，固定「正確性不依賴分散式鎖」
  - 測試先行（Infrastructure.Tests，比照 `OrderServiceConcurrencyTests`，真實 PostgreSQL、兩個獨立 `OrderService` scope）：
    - `CancelExpiredOrderAsync_TwoConcurrentCallsOnSameSeatOrder_OnlyOneSucceedsAndSeatReleasedOnce`（009）
    - `CancelExpiredOrderAsync_TwoConcurrentCallsOnSameCountingOrder_OnlyOneSucceedsAndQuantityRestoredOnce`（010）
  - RED 說明：這兩個測試固定的是**既有行為**（`ChangeOrderStatusAsync` 已鎖後重讀），正式程式碼不需修改，故不存在「正式碼修改前失敗」的 RED；改以 `scripts/verify-test-evidence.sh mutate` 證明測試能失敗——暫時移除 `ChangeOrderStatusAsync` 內的 `_orderRepository.ReloadAsync(...)` 呼叫，兩個測試須因目標行為不符而失敗（座位／庫存被重複釋放或兩個都回成功），一次一個改動；若移除 `ReloadAsync` 後測試仍通過（FOR UPDATE 序列化已足以擋下），改以移除 `GetForUpdateAsync` 鎖定作為 mutation，確認測試真的會失敗
  - 最小實作：無正式程式碼變更
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~CancelExpiredOrderAsync_TwoConcurrent"` 全數通過
  - 紀錄（原文）：
    - 測試設計（偏離 tasks 原文處）：先以「兩個 `Task.WhenAll` 同時啟動」實作時，在 mutation（移除 `ReloadAsync`）下 009 仍通過（只有 010 失敗），因為輸家常在贏家 commit 後才讀訂單，沒有真正重疊；改以外部交易先持有座位／票種列鎖、兩個取消都讀完訂單並排進鎖（`PostgresLockProbe.WaitUntilBackendsWaitForRowLockAsync`，新增，等 ≥2 條連線等鎖，因第二個等待者排在 tuple 鎖而非 transactionid）後才回滾外部交易放行，重疊由測試決定而非執行緒排程。另補斷言：輸家為 `ErrorType.Conflict`、座位釋放／庫存回補一次。
    - mutation 驗證（命令 `bash scripts/verify-test-evidence.sh mutate --file src/ProjectC.Application/Orders/OrderService.cs --from "await _orderRepository.ReloadAsync(order, cancellationToken);" --to "await Task.CompletedTask;" -- docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~CancelExpiredOrderAsync_TwoConcurrent"`，exit 0）：
      ```
      mutation: await _orderRepository.ReloadAsync(order, cancellationToken); -> await Task.CompletedTask;
      [xUnit.net 00:00:07.73]     ProjectC.Infrastructure.Tests.OrderServiceConcurrencyTests.CancelExpiredOrderAsync_TwoConcurrentCallsOnSameSeatOrder_OnlyOneSucceedsAndSeatReleasedOnce [FAIL]
        Error Message:
         Expected results.Count(r => r.IsSuccess) to be 1 because 兩個並發的逾時清理取消同一筆訂單，只能有一個成功, but found 2.
      [xUnit.net 00:00:07.91]     ProjectC.Infrastructure.Tests.OrderServiceConcurrencyTests.CancelExpiredOrderAsync_TwoConcurrentCallsOnSameCountingOrder_OnlyOneSucceedsAndQuantityRestoredOnce [FAIL]
        Error Message:
         Expected results.Count(r => r.IsSuccess) to be 1 because 兩個並發的逾時清理取消同一筆訂單，只能有一個成功, but found 2.
      Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 2 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
      還原後 `git diff --stat -- src` 為空（無輸出）。注意：腳本以 `cp -p` 還原會保留舊 mtime，MSBuild 會沿用 mutation 建出的 dll；還原後必須 `touch` 該檔再跑測試（第一次驗證因此在還原後的「GREEN」誤報 2 次成功，touch 後才恢復，已排除）。
    - GREEN（`docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~CancelExpiredOrderAsync_TwoConcurrent"`，還原並 touch 後）：
      `Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 1 s - ProjectC.Infrastructure.Tests.dll (net10.0)`
    - 重構：N/A（無正式程式碼變更）

## 2. 清理服務取得 leader lock（TDD）

- [x] 2.1 `ExpiredOrderCleanupService` 每輪取鎖，鎖被持有時跳過、取得時清理並釋放（對應 OCLE-001、003、004）
  - 測試先行（WebApi.Tests `BackgroundServices/ExpiredOrderCleanupServiceLeaderElectionTests.cs`，以假 `IDistributedLock`、假 `IServiceScopeFactory`／`IOrderRepository` 隔離 DB 與 Redis，不碰容器）：
    - `CleanupOnceWithLeaderElectionAsync_WhenLockAcquired_ScansAndReleasesWithOwnerToken`（001）
    - `CleanupOnceWithLeaderElectionAsync_WhenLockHeldByOther_SkipsWithoutScanningOrReleasing`（鎖被持有時 scope factory 與 repository 完全未被呼叫，並以 log 擷取斷言有 Debug 等級的跳過紀錄）
    - `CleanupOnceWithLeaderElectionAsync_AfterSkippedRound_TriesAcquireAgainNextRound`（003；連續兩輪，第一輪 `HeldByOther`、第二輪 `Acquired`）
    - `CleanupOnceWithLeaderElectionAsync_WhenScanThrows_StillReleasesLockAndPropagates`（004；以掃描階段例外驗證 `finally` 釋放）
    - 鎖 key／TTL：以假鎖記錄傳入值，斷言 key 為單一固定值、TTL = `IntervalSeconds × LockTtlMultiplier`；假鎖與假 repository 共用呼叫順序紀錄，001／004 斷言順序為 `TryAcquire → 掃描 → Release`
  - 預期 RED：新方法尚不存在，**編譯失敗不算 RED**——先加方法簽章（回傳 `Task.CompletedTask` 的空殼，建構子已含新參數）使測試可編譯，再執行；RED 為斷言失敗：Expected 假鎖 `TryAcquireAsync` 被呼叫 1 次，but found 0 times 等，須是目標行為不符
  - 最小實作：建構子新增 `IDistributedLock`、`DistributedLockOptions`；新增 `CleanupOnceWithLeaderElectionAsync`；`ExecuteAsync` 改呼叫帶鎖版本（保留外層 TraceId scope，不另 push）；既有 `CleanupOnceAsync` 不變；同步更新 6 處測試建構（`OrderServiceNotificationTests`、`QueryCachingFailOpenTests`、`ExceptionMaskingPipelineTests`、`BackgroundServiceCycleLevelFailureTraceIdTests`、`ExpiredOrderCleanupServiceTests` 與 `BackgroundServiceTraceIdTests` 的 `CreateService()` helper；以型別引用搜尋確認無遺漏）
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ExpiredOrderCleanupServiceLeaderElection"` 全數通過
  - 重構後重跑：同上，或標 N/A
  - 紀錄（原文，RED 標明為正式程式碼修改前）：
    - RED（正式程式碼修改前；已先加空殼 `CleanupOnceWithLeaderElectionAsync` 回傳 `Task.CompletedTask` 並更新 6 處建構，編譯通過）：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ExpiredOrderCleanupServiceLeaderElection"`
      ```
      Expected harness.AcquireCalls to contain a single item, but the collection is empty.   （AcquiresFixedKeyWithTtlFromIntervalAndMultiplier，intervalSeconds: 0 與 300 兩組）
      Expected harness.Events to be equal to {"acquire"}, but found empty collection.   （WhenLockHeldByOther_SkipsWithoutScanningOrReleasing）
      Expected harness.Events to be equal to {"acquire", "acquire", "scan", "release"}, but found empty collection.   （AfterSkippedRound_TriesAcquireAgainNextRound）
      Expected harness.Events to be equal to {"acquire", "scan", "release"}, but found empty collection.   （WhenLockAcquired_ScansAndReleasesWithOwnerTokenInOrder）
      Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown.   （WhenScanThrows_StillReleasesLockAndPropagates）
      Failed!  - Failed:     6, Passed:     0, Skipped:     0, Total:     6, Duration: 271 ms - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - GREEN（最小實作後，同命令）：`Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 229 ms - ProjectC.WebApi.Tests.dll (net10.0)`
    - 重構：N/A（2.2 之後的最終版本重跑見 2.2 GREEN）
  - 還原驗證：`restore --path` 會把服務檔還原成舊建構子簽章，連帶 6 處測試建構無法編譯（結果只會是 exit 2 的編譯錯誤，不是 RED），依 tasks.md 備案改用 `mutate`，一次一個；每次 mutate 後 `touch` 該檔（腳本用 `cp -p` 還原會保留舊 mtime，MSBuild 會沿用 mutation 的 dll，見 1.1 紀錄）：
    - mutation A（移除 `HeldByOther` 分支）：`bash scripts/verify-test-evidence.sh mutate --file src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs --from 'if (lockResult.LockResult == LockResult.HeldByOther)' --to 'if (false)' -- docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~ExpiredOrderCleanupServiceLeaderElection`
      ```
      ...LeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenLockHeldByOther_SkipsWithoutScanningOrReleasing [FAIL]
      ...LeaderElectionTests.CleanupOnceWithLeaderElectionAsync_AfterSkippedRound_TriesAcquireAgainNextRound [FAIL]
      Expected harness.Events to be equal to {"acquire"}, but {"acquire", "scan", "release"} contains 2 item(s) too many.
      Expected harness.Events to be equal to {"acquire", "acquire", "scan", "release"}, but {"acquire", "scan", "release", "acquire", "scan", "release"} contains 2 item(s) too many.
      Failed!  - Failed:     2, Passed:     4, Skipped:     0, Total:     6, Duration: 249 ms - ProjectC.WebApi.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
    - mutation B（移除 `finally` 釋放，`--from 'await _distributedLock.ReleaseAsync(LeaderElectionLockKey, lockResult.OwnerToken!, cancellationToken);' --to 'await Task.CompletedTask;'`，其餘同上）：
      ```
      ...AfterSkippedRound_TriesAcquireAgainNextRound [FAIL]
      ...WhenLockAcquired_ScansAndReleasesWithOwnerTokenInOrder [FAIL]
      ...WhenScanThrows_StillReleasesLockAndPropagates [FAIL]
      Expected harness.Events to be equal to {"acquire", "acquire", "scan", "release"}, but {"acquire", "acquire", "scan"} contains 1 item(s) less.
      Expected harness.Events to be equal to {"acquire", "scan", "release"}, but {"acquire", "scan"} contains 1 item(s) less.
      Expected harness.Events to be equal to {"acquire", "scan", "release"}, but {"acquire", "scan"} contains 1 item(s) less.
      Failed!  - Failed:     3, Passed:     3, Skipped:     0, Total:     6, Duration: 243 ms - ProjectC.WebApi.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
    - 還原後 `git diff --stat` 確認服務檔僅含本次實作、無 mutation 殘留；`touch` 後重跑 2.2 GREEN 全數通過（見下）。

- [x] 2.2 Redis 不可用時照跑清理並記 Warning（對應 OCLE-007、007b、007c、008）
  - 測試先行（同 2.1 檔案）：
    - `CleanupOnceWithLeaderElectionAsync_WhenRedisUnavailable_StillScansLogsWarningAndDoesNotRelease`（007；以 log 擷取斷言 Warning 存在且指出無鎖執行，非空前提：同一擷取方法在鎖正常取得時須無此 Warning）
    - `CleanupOnceWithLeaderElectionAsync_WhenAcquireThrowsNonConnectionRedisException_StillScansAndLogsWarning`（007b；假鎖丟 `RedisServerException`，並斷言 `ReleaseAsync` 未被呼叫）
    - `CleanupOnceWithLeaderElectionAsync_WhenScanThrowsAndReleaseThrows_PropagatesScanException`（007c；Warning 不含 owner token）
    - `CleanupOnceWithLeaderElectionAsync_WhenScanSucceedsAndReleaseThrows_DoesNotThrowAndLogsWarning`（007c；獨立測試）
    - `CleanupOnceWithLeaderElectionAsync_AfterRedisRecovers_ResumesLockExclusion`（008；第一輪 `RedisUnavailable`、第二輪 `HeldByOther` 即跳過）
  - 預期 RED：2.1 的最小實作只處理 `Acquired`／`HeldByOther`，若把 `RedisUnavailable` 視同跳過則 007 失敗（Expected 假 repository 被掃描 1 次，but found 0）；實際 RED 以執行結果為準，須是目標行為不符、非編譯錯誤
  - 最小實作：`RedisUnavailable` 與取鎖拋出的非取消例外皆記 Warning 後照跑清理、不呼叫 `ReleaseAsync`；`finally` 內釋放另包 try/catch 只記 Warning
  - GREEN／重構後重跑：同 2.1 命令
  - 紀錄（原文）：
    - RED（正式程式碼修改前，2.1 最小實作；同命令）：
      ```
      Did not expect any exception, but found StackExchange.Redis.RedisServerException: simulated release failure   （WhenScanSucceedsAndReleaseThrows_DoesNotThrowAndLogsWarning）
      （WhenAcquireThrowsNonConnectionRedisException_StillScansAndLogsWarning 失敗：取鎖例外往外傳）
      Expected harness.Events to be equal to {"acquire", "scan"}, but {"acquire", "scan", "release"} contains 1 item(s) too many.   （WhenRedisUnavailable_StillScansLogsWarningAndDoesNotRelease）
      Expected harness.Events to be equal to {"acquire", "scan", "acquire"}, but {"acquire", "scan", "release", "acquire"} contains 1 item(s) too many.   （AfterRedisRecovers_ResumesLockExclusion）
      Expected a <System.InvalidOperationException> to be thrown, but found <StackExchange.Redis.RedisServerException>:   （WhenScanThrowsAndReleaseThrows_PropagatesScanException：釋放例外遮蔽了掃描例外）
      Failed!  - Failed:     5, Passed:     6, Skipped:     0, Total:    11, Duration: 362 ms - ProjectC.WebApi.Tests.dll (net10.0)
      ```
      （RedisUnavailable 的 007 目標行為不符處：2.1 最小實作未特別處理，帶 `OwnerToken` 為 null 呼叫了 `ReleaseAsync`，與預期草稿「掃描 0 次」不同，實際 RED 以上方輸出為準。）
    - GREEN（`RedisUnavailable`／取鎖例外 fail-open、`finally` 釋放另包 try/catch 後，同命令）：`Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11, Duration: 255 ms - ProjectC.WebApi.Tests.dll (net10.0)`
    - 重構：N/A

## 3. 多實例整合測試（真實 PostgreSQL + Redis）

- [x] 3.1 兩個服務實例同時處理同一批逾時訂單（對應 OCLE-002、005、006）
  - 測試先行（Infrastructure.Tests，沿用 `RedisFixture` 與既有 PostgreSQL 夾具；兩個 `ExpiredOrderCleanupService` 實例共用同一個 `RedisDistributedLock`）：
    - `CleanupOnceWithLeaderElectionAsync_TwoInstancesConcurrently_OnlyOneProcessesAndSeatReleasedOnce`（002；斷言每筆訂單取消 1 次、座位釋放 1 次、無 Conflict Warning，至少 3 筆訂單以提高並發命中率）
    - `CleanupOnceWithLeaderElectionAsync_WhenHolderCrashesWithoutRelease_OtherInstanceProceedsAfterTtl`（005；先以同 key 預先持有短 TTL 的鎖模擬當掉，TTL 到期後第二實例成功執行；等待比照 `RedisDistributedLockTests` 既有輪詢手法，不依賴 wall-clock sleep 的單次邊界）
    - `CleanupOnceWithLeaderElectionAsync_WhenLockExpiredWhileHolderStillRunning_OverlapDoesNotDoubleRelease`（006；以可控的阻塞點讓 A 掃描中途停住、鎖過期，B 取得新鎖後**同樣停在掃描中不釋放**；此時放行 A 完成並釋放，斷言 B 持有的鎖仍在（釋放只由持有者完成），再放行 B。真正的重疊取消由 009／010 覆蓋，本測試只驗證鎖層行為）
  - 預期 RED：在 2.1／2.2 完成後這些測試對的是已實作的行為，故 RED 以 `mutate` 取代——暫時讓帶鎖版本略過取鎖（直接執行清理），002 須因 Warning／重複處理斷言失敗（Expected 取消次數 1，but found 2 等）；005／006 另以對應改動驗證能失敗，一次一個
  - 最小實作：無新增正式程式碼（若測試需要可控的阻塞點，優先透過既有 DI 替身，不為測試新增正式碼分支）
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~CleanupOnceWithLeaderElection"` 全數通過
  - 紀錄（原文）：
    - mutation 驗證：
      - 002（暫時把 `if (lockResult.LockResult == LockResult.HeldByOther)` 改為 `if (false)`，即略過互斥）：
        ```
        mutation: if (lockResult.LockResult == LockResult.HeldByOther) -> if (false)
        $ docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter FullyQualifiedName~CleanupOnceWithLeaderElection
          Determining projects to restore...
          All projects are up-to-date for restore.
          ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
          ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
          ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
          ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
          ProjectC.Infrastructure.Tests -> /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll
        Test run for /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll (.NETCoreApp,Version=v10.0)
        A total of 1 test files matched the specified pattern.
        [xUnit.net 00:00:08.69]     ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_TwoInstancesOverlapping_OnlyOneProcessesAndQuantityRestoredOnce [FAIL]
          Failed ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_TwoInstancesOverlapping_OnlyOneProcessesAndQuantityRestoredOnce [2 s]
          Error Message:
           Expected instanceB.Probe.ScanCount to be 0 because A 持有鎖，B 本輪不得掃描, but found 1 (difference of 1).
          Stack Trace:
             at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
           at ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_TwoInstancesOverlapping_OnlyOneProcessesAndQuantityRestoredOnce() in /src/tests/ProjectC.Infrastructure.Tests/Orders/ExpiredOrderCleanupLeaderElectionTests.cs:line 208
           at ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_TwoInstancesOverlapping_OnlyOneProcessesAndQuantityRestoredOnce() in /src/tests/ProjectC.Infrastructure.Tests/Orders/ExpiredOrderCleanupLeaderElectionTests.cs:line 221
           at ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_TwoInstancesOverlapping_OnlyOneProcessesAndQuantityRestoredOnce() in /src/tests/ProjectC.Infrastructure.Tests/Orders/ExpiredOrderCleanupLeaderElectionTests.cs:line 221
        --- End of stack trace from previous location ---
        [xUnit.net 00:00:09.96]     ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenHolderCrashesWithoutRelease_OtherInstanceProceedsAfterTtl [FAIL]
          Failed ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenHolderCrashesWithoutRelease_OtherInstanceProceedsAfterTtl [70 ms]
          Error Message:
           Expected instance.Probe.ScanCount to be 0 because TTL 未到期前鎖仍被持有，應跳過, but found 1 (difference of 1).
          Stack Trace:
             at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
           at ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenHolderCrashesWithoutRelease_OtherInstanceProceedsAfterTtl() in /src/tests/ProjectC.Infrastructure.Tests/Orders/ExpiredOrderCleanupLeaderElectionTests.cs:line 237
           at ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenHolderCrashesWithoutRelease_OtherInstanceProceedsAfterTtl() in /src/tests/ProjectC.Infrastructure.Tests/Orders/ExpiredOrderCleanupLeaderElectionTests.cs:line 243
        --- End of stack trace from previous location ---
        
        Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
        結果：測試如預期失敗
        ```
      - 005（暫時把 `RedisDistributedLock.TryAcquireAsync` 的 `ttl, When.NotExists` 改為 `null, When.NotExists`，即鎖永不過期）：
        ```
        mutation: ttl, When.NotExists -> null, When.NotExists
        $ docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter FullyQualifiedName~CleanupOnceWithLeaderElection
          Determining projects to restore...
          All projects are up-to-date for restore.
          ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
          ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
          ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
          ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
          ProjectC.Infrastructure.Tests -> /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll
        Test run for /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll (.NETCoreApp,Version=v10.0)
        A total of 1 test files matched the specified pattern.
        [xUnit.net 00:00:15.47]     ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenLockExpiredWhileHolderStillRunning_OverlapDoesNotDoubleRelease [FAIL]
          Failed ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenLockExpiredWhileHolderStillRunning_OverlapDoesNotDoubleRelease [7 s]
          Error Message:
           Expected stopwatch.Elapsed to be less than 7s because 鎖應在 TTL（加時鐘回跳餘裕）內被 Redis 淘汰, but found 7s, 80ms and 151.8µs.
          Stack Trace:
             at FluentAssertions.Primitives.SimpleTimeSpanAssertions`1.BeLessThan(TimeSpan expected, String because, Object[] becauseArgs)
           at ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.WaitUntilKeyExpiredAsync(IDatabase database, TimeSpan ttl) in /src/tests/ProjectC.Infrastructure.Tests/Orders/ExpiredOrderCleanupLeaderElectionTests.cs:line 185
           at ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenLockExpiredWhileHolderStillRunning_OverlapDoesNotDoubleRelease() in /src/tests/ProjectC.Infrastructure.Tests/Orders/ExpiredOrderCleanupLeaderElectionTests.cs:line 264
           at ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenLockExpiredWhileHolderStillRunning_OverlapDoesNotDoubleRelease() in /src/tests/ProjectC.Infrastructure.Tests/Orders/ExpiredOrderCleanupLeaderElectionTests.cs:line 284
           at ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenLockExpiredWhileHolderStillRunning_OverlapDoesNotDoubleRelease() in /src/tests/ProjectC.Infrastructure.Tests/Orders/ExpiredOrderCleanupLeaderElectionTests.cs:line 284
        --- End of stack trace from previous location ---
        [xUnit.net 00:00:22.53]     ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenHolderCrashesWithoutRelease_OtherInstanceProceedsAfterTtl [FAIL]
          Failed ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenHolderCrashesWithoutRelease_OtherInstanceProceedsAfterTtl [7 s]
          Error Message:
           Expected stopwatch.Elapsed to be less than 7s because 鎖應在 TTL（加時鐘回跳餘裕）內被 Redis 淘汰, but found 7s, 24ms and 581.7µs.
          Stack Trace:
             at FluentAssertions.Primitives.SimpleTimeSpanAssertions`1.BeLessThan(TimeSpan expected, String because, Object[] becauseArgs)
           at ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.WaitUntilKeyExpiredAsync(IDatabase database, TimeSpan ttl) in /src/tests/ProjectC.Infrastructure.Tests/Orders/ExpiredOrderCleanupLeaderElectionTests.cs:line 185
           at ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenHolderCrashesWithoutRelease_OtherInstanceProceedsAfterTtl() in /src/tests/ProjectC.Infrastructure.Tests/Orders/ExpiredOrderCleanupLeaderElectionTests.cs:line 239
           at ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenHolderCrashesWithoutRelease_OtherInstanceProceedsAfterTtl() in /src/tests/ProjectC.Infrastructure.Tests/Orders/ExpiredOrderCleanupLeaderElectionTests.cs:line 243
        --- End of stack trace from previous location ---
        
        Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3, Duration: 15 s - ProjectC.Infrastructure.Tests.dll (net10.0)
        結果：測試如預期失敗
        ```
      - 006（暫時把釋放腳本的 `if redis.call('get', KEYS[1]) == ARGV[1] then` 改為 `if true then`，即無條件刪除）：
        ```
        mutation: if redis.call('get', KEYS[1]) == ARGV[1] then -> if true then
        $ docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter FullyQualifiedName~CleanupOnceWithLeaderElection
          Determining projects to restore...
          All projects are up-to-date for restore.
          ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
          ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
          ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
          ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
          ProjectC.Infrastructure.Tests -> /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll
        Test run for /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll (.NETCoreApp,Version=v10.0)
        A total of 1 test files matched the specified pattern.
        [xUnit.net 00:00:09.67]     ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenLockExpiredWhileHolderStillRunning_OverlapDoesNotDoubleRelease [FAIL]
          Failed ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenLockExpiredWhileHolderStillRunning_OverlapDoesNotDoubleRelease [1 s]
          Error Message:
           Expected (database.KeyExistsAsync(LockKey)) to be True because A 的鎖早已過期，A 的釋放不得刪掉 B 現在持有的鎖, but found False.
          Stack Trace:
             at FluentAssertions.Primitives.BooleanAssertions`1.BeTrue(String because, Object[] becauseArgs)
           at ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenLockExpiredWhileHolderStillRunning_OverlapDoesNotDoubleRelease() in /src/tests/ProjectC.Infrastructure.Tests/Orders/ExpiredOrderCleanupLeaderElectionTests.cs:line 271
           at ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenLockExpiredWhileHolderStillRunning_OverlapDoesNotDoubleRelease() in /src/tests/ProjectC.Infrastructure.Tests/Orders/ExpiredOrderCleanupLeaderElectionTests.cs:line 284
           at ProjectC.Infrastructure.Tests.Orders.ExpiredOrderCleanupLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenLockExpiredWhileHolderStillRunning_OverlapDoesNotDoubleRelease() in /src/tests/ProjectC.Infrastructure.Tests/Orders/ExpiredOrderCleanupLeaderElectionTests.cs:line 284
        --- End of stack trace from previous location ---
        
        Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
        結果：測試如預期失敗
        ```
      - 還原：每次 `mutate` 結束後檔案由腳本還原並 `touch`（避免 MSBuild 沿用 mutate 版 dll）；`git status --short src` 只剩 2.2 的 `ExpiredOrderCleanupService.cs` 修改，`RedisDistributedLock.cs` 無差異。
    - GREEN：
      ```
      $ docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~CleanupOnceWithLeaderElection"
      Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 3 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
    - 外部審查前調整：005 測試預佔鎖 TTL 由 1 秒放寬為 5 秒（避免慢環境下第一輪呼叫前鎖已過期；仍以輪詢等待，不用單次 sleep）。只重跑該測試：
      ```
      $ docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~WhenHolderCrashesWithoutRelease_OtherInstanceProceedsAfterTtl"
      Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 6 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
    - 與 tasks.md 計畫的偏離：(1) 測試名稱改為 `_TwoInstancesOverlapping_OnlyOneProcessesAndQuantityRestoredOnce`（002 以計數票種的「可售數量只回補一次」觀察重複釋放，回補兩次會變成 103；座位版重複釋放由 1.1 的 OCLE-009 覆蓋）；(2) 002 以「掃描回傳後停在閘門」讓 A 持鎖處理到一半是確定狀態，而非靠執行緒排程碰巧重疊；(3) 類別以 `IClassFixture<RedisFixture>` 使用專屬 Redis 容器，避免與其他類別共用固定 key `order-cleanup:lock`；(4) 為在掃描處設閘門，以 DI 註冊包住真實 `OrderRepository` 的 Moq `IOrderRepository`，無新增正式碼。
    - 重構：N/A

## 4. 文件與收尾

- [x] 4.1 同步 `docs/review/TODO.md`：H-6 改為實際範圍（leader lock），註明 FOR UPDATE／xmin 部分原本已由現有鎖處理；路線圖 ⑦ 標為已實作（待歸檔）。TDD：N/A（純文件）
  - 紀錄：TDD N/A（純文件）；`docs/review/TODO.md` 路線圖 ⑦ 標為已實作（待歸檔）、H-6 改寫實際範圍並加「狀態」行。
- [x] 4.2 hardener 最終檢查：套用 `.claude/skills/hardener/SKILL.md`——`CancellationToken` 傳遞（取鎖、釋放、清理）、鎖釋放的例外邊界（`finally` 內釋放失敗不得遮蔽清理例外）、取消時是否仍釋放鎖、日誌不含敏感資訊；結果記錄於此：
  - hardener 檢查結果：
    - `CancellationToken` 傳遞：取鎖（`TryAcquireAsync`）與清理（`CleanupOnceCoreAsync`）沿用呼叫端 token；`finally` 釋放**刻意改用 `CancellationToken.None`**（決定：關機取消正是最需要釋放的時機，帶已取消 token 的釋放會被略過、鎖殘留到 TTL 預設 900 秒；單次 Redis 指令耗時由連線逾時設定界定）。依 TDD 補 `..._WhenCancelledDuringScan_StillReleasesLockWithUncancelledToken`。
      - RED（改正式碼前）：
        ```
        Expected harness.ReleaseTokens[0].IsCancellationRequested to be False because 關機取消不得讓釋放跟著被取消，否則鎖會殘留到 TTL 到期, but found True.
        Failed!  - Failed:     1, Passed:    11, Skipped:     0, Total:    12, Duration: 338 ms - ProjectC.WebApi.Tests.dll (net10.0)
        ```
      - GREEN：`Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 326 ms - ProjectC.WebApi.Tests.dll (net10.0)`
      - 還原驗證（mutate：把釋放的 `CancellationToken.None` 暫時改為 `new CancellationToken(true)`，等同傳入已取消的 stoppingToken；因釋放方法已不接收呼叫端 token，無法直接傳入 `cancellationToken`。此改動使 8 個測試失敗，目標測試為其一；原文）：
        ```
        mutation: ownerToken, CancellationToken.None); -> ownerToken, new CancellationToken(true));
        $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~ExpiredOrderCleanupServiceLeaderElectionTests
          Determining projects to restore...
          All projects are up-to-date for restore.
          ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
          ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
          ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
          ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
          ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
        Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
        A total of 1 test files matched the specified pattern.
        [xUnit.net 00:00:00.92]     ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenScanSucceedsAndReleaseThrows_DoesNotThrowAndLogsWarning [FAIL]
        [xUnit.net 00:00:00.95]     ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_AcquiresFixedKeyWithTtlFromIntervalAndMultiplier(intervalSeconds: 0, lockTtlMultiplier: 3, expectedTtlSeconds: 3) [FAIL]
        [xUnit.net 00:00:00.95]     ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_AcquiresFixedKeyWithTtlFromIntervalAndMultiplier(intervalSeconds: 300, lockTtlMultiplier: 3, expectedTtlSeconds: 900) [FAIL]
        [xUnit.net 00:00:00.97]     ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_AfterSkippedRound_TriesAcquireAgainNextRound [FAIL]
        [xUnit.net 00:00:01.00]     ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenCancelledDuringScan_StillReleasesLockWithUncancelledToken [FAIL]
        [xUnit.net 00:00:01.01]     ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenLockAcquired_ScansAndReleasesWithOwnerTokenInOrder [FAIL]
          Failed ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenScanSucceedsAndReleaseThrows_DoesNotThrowAndLogsWarning [185 ms]
          Error Message:
           Did not expect any exception, but found System.Threading.Tasks.TaskCanceledException: A task was canceled.
           at ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService.ReleaseLeaderLockAsync(String ownerToken) in /src/src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs:line 138
           at ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService.CleanupOnceWithLeaderElectionAsync(CancellationToken cancellationToken) in /src/src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs:line 112
           at FluentAssertions.Specialized.NonGenericAsyncFunctionAssertions.NotThrowAsync(String because, Object[] becauseArgs).
          Stack Trace:
             at FluentAssertions.Specialized.DelegateAssertionsBase`2.NotThrowInternal(Exception exception, String because, Object[] becauseArgs)
           at FluentAssertions.Specialized.NonGenericAsyncFunctionAssertions.NotThrowAsync(String because, Object[] becauseArgs)
           at ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenScanSucceedsAndReleaseThrows_DoesNotThrowAndLogsWarning() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/ExpiredOrderCleanupServiceLeaderElectionTests.cs:line 264
        --- End of stack trace from previous location ---
          Failed ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_AcquiresFixedKeyWithTtlFromIntervalAndMultiplier(intervalSeconds: 0, lockTtlMultiplier: 3, expectedTtlSeconds: 3) [4 ms]
          Error Message:
           System.Threading.Tasks.TaskCanceledException : A task was canceled.
          Stack Trace:
             at ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService.ReleaseLeaderLockAsync(String ownerToken) in /src/src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs:line 138
           at ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService.CleanupOnceWithLeaderElectionAsync(CancellationToken cancellationToken) in /src/src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs:line 112
           at ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_AcquiresFixedKeyWithTtlFromIntervalAndMultiplier(Int32 intervalSeconds, Int32 lockTtlMultiplier, Int32 expectedTtlSeconds) in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/ExpiredOrderCleanupServiceLeaderElectionTests.cs:line 139
        --- End of stack trace from previous location ---
          Failed ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_AcquiresFixedKeyWithTtlFromIntervalAndMultiplier(intervalSeconds: 300, lockTtlMultiplier: 3, expectedTtlSeconds: 900) [1 ms]
          Error Message:
           System.Threading.Tasks.TaskCanceledException : A task was canceled.
          Stack Trace:
             at ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService.ReleaseLeaderLockAsync(String ownerToken) in /src/src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs:line 138
           at ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService.CleanupOnceWithLeaderElectionAsync(CancellationToken cancellationToken) in /src/src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs:line 112
           at ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_AcquiresFixedKeyWithTtlFromIntervalAndMultiplier(Int32 intervalSeconds, Int32 lockTtlMultiplier, Int32 expectedTtlSeconds) in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/ExpiredOrderCleanupServiceLeaderElectionTests.cs:line 139
        --- End of stack trace from previous location ---
        [xUnit.net 00:00:01.06]     ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenScanThrows_StillReleasesLockAndPropagates [FAIL]
        [xUnit.net 00:00:01.07]     ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenScanThrowsAndReleaseThrows_PropagatesScanException [FAIL]
          Failed ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_AfterSkippedRound_TriesAcquireAgainNextRound [1 ms]
          Error Message:
           System.Threading.Tasks.TaskCanceledException : A task was canceled.
          Stack Trace:
             at ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService.ReleaseLeaderLockAsync(String ownerToken) in /src/src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs:line 138
           at ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService.CleanupOnceWithLeaderElectionAsync(CancellationToken cancellationToken) in /src/src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs:line 112
           at ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_AfterSkippedRound_TriesAcquireAgainNextRound() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/ExpiredOrderCleanupServiceLeaderElectionTests.cs:line 171
        --- End of stack trace from previous location ---
          Failed ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenCancelledDuringScan_StillReleasesLockWithUncancelledToken [30 ms]
          Error Message:
           Expected harness.ReleaseTokens[0].IsCancellationRequested to be False because 關機取消不得讓釋放跟著被取消，否則鎖會殘留到 TTL 到期, but found True.
          Stack Trace:
             at FluentAssertions.Primitives.BooleanAssertions`1.BeFalse(String because, Object[] becauseArgs)
           at ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenCancelledDuringScan_StillReleasesLockWithUncancelledToken() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/ExpiredOrderCleanupServiceLeaderElectionTests.cs:line 202
        --- End of stack trace from previous location ---
          Failed ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenLockAcquired_ScansAndReleasesWithOwnerTokenInOrder [6 ms]
          Error Message:
           System.Threading.Tasks.TaskCanceledException : A task was canceled.
          Stack Trace:
             at ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService.ReleaseLeaderLockAsync(String ownerToken) in /src/src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs:line 138
           at ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService.CleanupOnceWithLeaderElectionAsync(CancellationToken cancellationToken) in /src/src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs:line 112
           at ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenLockAcquired_ScansAndReleasesWithOwnerTokenInOrder() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/ExpiredOrderCleanupServiceLeaderElectionTests.cs:line 121
        --- End of stack trace from previous location ---
          Failed ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenScanThrows_StillReleasesLockAndPropagates [8 ms]
          Error Message:
           Expected a <System.InvalidOperationException> to be thrown, but found <System.Threading.Tasks.TaskCanceledException>:
        System.Threading.Tasks.TaskCanceledException: A task was canceled.
           at ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService.ReleaseLeaderLockAsync(String ownerToken) in /src/src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs:line 138
           at ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService.CleanupOnceWithLeaderElectionAsync(CancellationToken cancellationToken) in /src/src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs:line 112
           at FluentAssertions.Specialized.AsyncFunctionAssertions`2.InvokeWithInterceptionAsync(Func`1 action).
          Stack Trace:
             at FluentAssertions.Specialized.DelegateAssertionsBase`2.<>c__DisplayClass8_0`1.<ThrowInternal>b__0(AssertionChain chain)
           at FluentAssertions.Specialized.DelegateAssertionsBase`2.ThrowInternal[TException](Exception exception, String because, Object[] becauseArgs)
           at FluentAssertions.Specialized.AsyncFunctionAssertions`2.ThrowAsync[TException](String because, Object[] becauseArgs)
           at FluentAssertions.ExceptionAssertionsExtensions.WithMessage[TException](Task`1 task, String expectedWildcardPattern, String because, Object[] becauseArgs)
           at ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenScanThrows_StillReleasesLockAndPropagates() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/ExpiredOrderCleanupServiceLeaderElectionTests.cs:line 185
        --- End of stack trace from previous location ---
          Failed ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenScanThrowsAndReleaseThrows_PropagatesScanException [29 ms]
          Error Message:
           Expected a <System.InvalidOperationException> to be thrown, but found <System.Threading.Tasks.TaskCanceledException>:
        System.Threading.Tasks.TaskCanceledException: A task was canceled.
           at ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService.ReleaseLeaderLockAsync(String ownerToken) in /src/src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs:line 138
           at ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService.CleanupOnceWithLeaderElectionAsync(CancellationToken cancellationToken) in /src/src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs:line 112
           at FluentAssertions.Specialized.AsyncFunctionAssertions`2.InvokeWithInterceptionAsync(Func`1 action).
          Stack Trace:
             at FluentAssertions.Specialized.DelegateAssertionsBase`2.<>c__DisplayClass8_0`1.<ThrowInternal>b__0(AssertionChain chain)
           at FluentAssertions.Specialized.DelegateAssertionsBase`2.ThrowInternal[TException](Exception exception, String because, Object[] becauseArgs)
           at FluentAssertions.Specialized.AsyncFunctionAssertions`2.ThrowAsync[TException](String because, Object[] becauseArgs)
           at FluentAssertions.ExceptionAssertionsExtensions.WithMessage[TException](Task`1 task, String expectedWildcardPattern, String because, Object[] becauseArgs)
           at ProjectC.WebApi.Tests.BackgroundServices.ExpiredOrderCleanupServiceLeaderElectionTests.CleanupOnceWithLeaderElectionAsync_WhenScanThrowsAndReleaseThrows_PropagatesScanException() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/ExpiredOrderCleanupServiceLeaderElectionTests.cs:line 249
        --- End of stack trace from previous location ---
        
        Failed!  - Failed:     8, Passed:     4, Skipped:     0, Total:    12, Duration: 343 ms - ProjectC.WebApi.Tests.dll (net10.0)
        結果：測試如預期失敗
        ```
        還原後已 `touch`，重跑 GREEN：`Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 235 ms - ProjectC.WebApi.Tests.dll (net10.0)`；`git diff` 確認正式碼仍為 `CancellationToken.None`。
    - 例外邊界：釋放失敗 `catch`（排除 `OperationCanceledException`）只記 Warning、不帶 owner token，且不遮蔽清理例外（007c 兩個測試已覆蓋）；取鎖任何非取消例外視同 `RedisUnavailable`（007／007b）。
    - 取消時是否仍釋放：是（見上）。
    - 日誌：Warning／Debug 只含 lock key，無 owner token、無訂單內容。
    - 無需補強：參數驗證／Entity 存在性／狀態機（本類別無外部輸入，取消由 `CancelExpiredOrderAsync` 既有防禦負責）。
- [x] 4.3 全套測試（各專案摘要行貼原文）：`docker compose exec api dotnet test`（Domain／Application／Infrastructure／WebApi／LoadTest.Seeder 各專案）；若 `ServiceRegistrationSnapshotTests` 受影響則更新基準並逐行核對新增項目
  - 紀錄（原文，`docker compose exec -T api dotnet test`）：
    ```
    Passed!  - Failed:     0, Passed:   153, Skipped:     0, Total:   153, Duration: 208 ms - ProjectC.Domain.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   632, Skipped:     0, Total:   632, Duration: 2 s - ProjectC.Application.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 21 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   308, Skipped:     0, Total:   308, Duration: 1 m 32 s - ProjectC.Infrastructure.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   950, Skipped:     0, Total:   950, Duration: 6 m 40 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
    `ServiceRegistrationSnapshotTests` 未受影響（未新增服務註冊；`DistributedLockOptions`／`IDistributedLock` 為既有註冊）。
- [x] 4.4 `graphify update .`
  - 紀錄：已執行 `graphify update .`（AST-only）。
- [x] 4.5 Task 完成後告知：spec 同步狀態、AC 涵蓋（OCLE-001～010 皆有對應測試）、重複邏輯（與 `PurchaseQueueAdmissionService` 的取鎖流程是否值得抽共用，三處以上才抽）、命名一致性
  - 紀錄：
    - spec 同步：實作與 delta spec 一致；`finally` 釋放改用 `CancellationToken.None` 為 spec 未明述的實作細節（OCLE-004「無論例外皆釋放」的延伸），不需改 spec；歸檔時 delta 併入主 spec。
    - AC 涵蓋：OCLE-001／003／004／007／007b／007c／008 → `ExpiredOrderCleanupServiceLeaderElectionTests`（12 個）；OCLE-002／005／006 → `ExpiredOrderCleanupLeaderElectionTests`（3 個，真實 PG＋Redis）；OCLE-009／010 → `OrderServiceConcurrencyTests`。
    - 重複邏輯：取鎖／釋放／fail-open 流程與 `PurchaseQueueAdmissionService` 相似（2 處），未達三處，不抽共用。
    - 命名一致性：方法 `CleanupOnceWithLeaderElectionAsync`、鎖 key `order-cleanup:lock` 與既有 `purchase-queue:*` 風格一致。
