# Tasks

> 所有指令都在容器內執行（`docker compose exec -T api dotnet ...`），不在本機執行 dotnet。
>
> 本 change 為純重構，無新增 Acceptance Criteria（`skip_specs: true`，見 proposal.md）。以下「行為不變準則」取代 AC（定義見 design.md 決策 3），每一條都對應驗證 task：
>
> | 行為不變準則 | 驗證 |
> | --- | --- |
> | I1：步驟順序 1→2→3→4→（計算 `waitingButAdmittedIds`）→5→6→7→8 | 2.1–2.6 各步驟突變、3.1 |
> | I2：`waitingButAdmittedIds` 在步驟 5 之前計算，整個集合排除在步驟 5 之外 | 2.6 突變 |
> | I3：snapshot 讀取失敗時整輪略過 | 3.1 |
> | I4：步驟 3／4／8 的集合互斥條件 | 2.1、2.2、2.3 突變 |
> | I5：錯誤處理路徑選擇（Warning-only／Tracked／格式損壞） | 2.1–2.6 突變、`git diff` 核對 |
> | I6：取消語意與 `CancellationToken` 傳遞 | 2.1–2.6 `git diff` 核對、4.1 |
> | I7：冪等（連續兩輪第二輪為 no-op） | 4.1 全套測試 |
> | C1：`S3776` 警告消失，且建置警告清單與基準相比只少該一筆 | 4.1 |
>
> 證據一律貼原文（CLAUDE.md「證據貼原文」）：每個失敗測試的名稱與訊息、每個測試專案的摘要行、`mutate` 腳本輸出；不得以數字、「全數通過」或「與 X 相同」代替。
>
> TDD：純重構、無新增可觀察行為，RED 標示 N/A（既有測試在重構前就通過是預期）；「測試在行為被破壞時會失敗」由各步驟的 `mutate` 證明（design.md 決策 4）。`restore`（RED 還原驗證）同樣 N/A：還原後測試照樣通過是行為不變重構的預期結果。未標 TDD 的 task 一律視為 TDD：N/A（理由同上）。
>
> mutate 命令的 `--from` 字串以實際程式碼為準（下列為撰寫提案時的原文），若字串因前一步驟搬移而改變，在完成紀錄說明；mutate 不唯一或找不到時不得改用多處合併套用。
>
> 對帳測試命令（簡稱「對帳測試」）：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~PurchaseQueueMirrorReconciliationTests"`
>
> 前端：無變更，不需真實瀏覽器驗證。
>
> 提交規則：不自動 commit／push；mutate 造成的暫時性改動不得進入 commit。實作與每次容器測試期間，不得有其他 session 同時改檔或建置。

## 1. 重構前基準

- [x] 1.1 在乾淨工作樹（`git status` 確認）記錄基準：`docker compose exec -T api dotnet build --no-incremental` 的 warning 清單（預期含 `S3776` 一筆，認知複雜度 28 to 15；其餘既有 warning 照列）；`docker compose exec -T api dotnet test` 全套各測試專案摘要行；「對帳測試」摘要行。已知偶發失敗（`RealNameLoggingTests`、`HostShutdownSeqFlushTests`、時間相關測試）失敗時照實記錄並單獨重跑。
  - 完成紀錄：
    - 工作樹：基準於乾淨 HEAD bc3dc2c 量測（重構前，未含任何本 change 的程式碼改動）。
    - `dotnet build --no-incremental` warning（`Warning(s)` 3、`Error(s)` 0）：
    ```
    /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs(411,24): warning S3776: Refactor this method to reduce its Cognitive Complexity from 28 to the 15 allowed. (https://rules.sonarsource.com/csharp/RSPEC-3776) [/src/src/ProjectC.WebApi/ProjectC.WebApi.csproj]
    warning MSB3277: Found conflicts between different versions of "Microsoft.EntityFrameworkCore.Relational"（ProjectC.Infrastructure.Tests、ProjectC.WebApi.Tests 各一筆；10.0.4 vs 10.0.11）
    ```
    - 全套 `dotnet test` 摘要行：
    ```
    Passed!  - Failed:     0, Passed:   153, Skipped:     0, Total:   153, Duration: 93 ms - ProjectC.Domain.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   624, Skipped:     0, Total:   624, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 9 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   280, Skipped:     0, Total:   280, Duration: 52 s - ProjectC.Infrastructure.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   811, Skipped:     0, Total:   811, Duration: 3 m 18 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
    - 對帳測試（`--filter FullyQualifiedName~PurchaseQueueMirrorReconciliationTests`）：
    ```
    Passed!  - Failed:     0, Passed:    30, Skipped:     0, Total:    30, Duration: 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
    - 偶發失敗：無。
  - TDD：N/A（基準量測）。

## 2. 各步驟：突變驗證 → 原樣抽取 → 測試（由低風險到高風險）

每個 task 的固定流程：(a) 突變驗證：用 `bash scripts/verify-test-evidence.sh mutate --file src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs --from <原字串> --to <新字串> -- <對帳測試>` 證明至少一個測試失敗，貼完整輸出；若測試仍通過（腳本退出碼 1）代表保護網有缺口，先在 `PurchaseQueueMirrorReconciliationTests.cs` 補特徵化測試（在現有碼上通過）再重做 (a)，補超過約 3 個新測試時暫停回報；(b) 原樣搬移成 private 方法，主方法改為 `await` 該方法（參數用 `ReconciliationRound` record，首次需要時於 2.1 建立）；`git diff` 確認搬移前後逐行等價（只有方法邊界、縮排與參數存取方式的差異），註解與 lambda 逐字保留；(c) 跑「對帳測試」貼摘要行。

> 各 task (a) 的 mutate 紀錄為 B-001 修正時，在**最終程式碼**（所有抽取完成後）重跑的完整原文：未過濾堆疊、未省略失敗測試。因此 `--from`／`--to` 使用抽取後的 `round.` 存取前綴（例如 `round.Database.SortedSetAddAsync(round.AdmittedKey, idStr, `），Total 為最終的 32 筆（先前紀錄為各步驟當時的 30／31 筆）。每次 mutate 後 `touch` 該檔並以 `git diff` 的 md5 比對確認工作樹與 mutate 前相同（15 次全部相同）。
> `2_2`、`2_3a` 的 `StartAsync_PerformsFullStartupReconciliationAcrossAllQueueModeEventsWithoutBlockingStartup [10 s]` 失敗訊息為 `System.TimeoutException : 等待條件在 10000ms 內未成立：等待啟動時的全量校正完成，涵蓋所有 IsQueueModeEnabled 活動`：該測試以 `WaitUntilAsync`（10 秒輪詢）等待 `admitted2`（2.2）／`waiting1`（2.3a）被補進 Redis 鏡像；突變把 ZADD 寫到錯誤的 key，成員永遠不出現而逾時。這是目標行為不符造成的斷言型失敗（不是環境逾時），其餘失敗測試亦同時成立。

- [x] 2.1 步驟 8 `RepairAbandonedTimeoutsAsync`（建立 `ReconciliationRound` private sealed record，欄位沿用原變數名）。突變：`--from "() => purchaseQueueRepository.ExpireBatchAsync([entryId], cancellationToken));" --to "() => Task.CompletedTask);"`，預期失敗的測試：`…ExpiredBatchUpdate_WhenItThrows…`、`…AbandonedExpiryUpdateFailure…`、`…OverdueAdmittedEntryNeverSyncedToRedis…` 一帶（以實際為準）。
  - 完成紀錄：
    - (a) 突變 `2_1`（`bash scripts/verify-test-evidence.sh mutate` 於最終程式碼重跑的完整原文，未過濾堆疊、未省略任何失敗測試；mutate 以備份還原檔案，不使用 stash，故無 stash 輸出，退出碼見下方摘要行）：
    ```
    mutation: () => round.PurchaseQueueRepository.ExpireBatchAsync([entryId], cancellationToken)); -> () => Task.CompletedTask);
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~PurchaseQueueMirrorReconciliationTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:04.34]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedExpiryUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedExpiryUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [89 ms]
      Error Message:
       Expected sink.Events {empty} to have an item matching (Convert(e.Level, Int32) == 4) because 達到門檻的那一輪起，當次失敗 MUST 升級為 Error.
      Stack Trace:
         at FluentAssertions.Collections.GenericCollectionAssertions`3.Contain(Expression`1 predicate, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedExpiryUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1220
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:05.60]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyAdmittedMirrorIsMissing_RebuildsAdmittedIncludingOverdueJudgmentWithoutAffectingWaiting [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyAdmittedMirrorIsMissing_RebuildsAdmittedIncludingOverdueJudgmentWithoutAffectingWaiting [77 ms]
      Error Message:
       Expected (ReadStatusAsync(overdue.Id)) to be PurchaseQueueEntryStatus.Expired {value: 3} because 已逾時但未落地的紀錄應被步驟 8 標記為 Expired, but found PurchaseQueueEntryStatus.Admitted {value: 1}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyAdmittedMirrorIsMissing_RebuildsAdmittedIncludingOverdueJudgmentWithoutAffectingWaiting() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1322
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:07.21]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ExpiredBatchUpdate_WhenItThrows_LeavesEntryAdmittedUntilNextReconciliationMarksItExpired [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ExpiredBatchUpdate_WhenItThrows_LeavesEntryAdmittedUntilNextReconciliationMarksItExpired [170 ms]
      Error Message:
       Expected (ReadStatusAsync(target.Id)) to be PurchaseQueueEntryStatus.Expired {value: 3} because 下一次成功的校正 MUST 代為完成落地為 Expired, but found PurchaseQueueEntryStatus.Admitted {value: 1}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ExpiredBatchUpdate_WhenItThrows_LeavesEntryAdmittedUntilNextReconciliationMarksItExpired() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 210
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:15.63]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_OverdueAdmittedEntryNeverSyncedToRedis_IsHandledOnlyByAbandonedExpiryStepNotGapFill [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_OverdueAdmittedEntryNeverSyncedToRedis_IsHandledOnlyByAbandonedExpiryStepNotGapFill [74 ms]
      Error Message:
       Expected (ReadStatusAsync(overdue.Id)) to be PurchaseQueueEntryStatus.Expired {value: 3} because 應只被步驟 8 處理為 Expired, but found PurchaseQueueEntryStatus.Admitted {value: 1}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_OverdueAdmittedEntryNeverSyncedToRedis_IsHandledOnlyByAbandonedExpiryStepNotGapFill() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1240
    --- End of stack trace from previous location ---

    Failed!  - Failed:     4, Passed:    28, Skipped:     0, Total:    32, Duration: 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    - (b) 原樣抽取為 `RepairAbandonedTimeoutsAsync`，並建立 `ReconciliationRound`（private sealed record，欄位為原變數名的 PascalCase）與主方法中的 `round` 建構。`git diff` 核對：迴圈本體與註解逐字不變，差異只有方法邊界與 `round.` 存取前綴（`admittedOverduePg`→`round.AdmittedOverduePg`、`admittedRedis`→`round.AdmittedRedis`、`purchaseQueueRepository`→`round.PurchaseQueueRepository`）；`cancellationToken` 仍只傳給 repository 呼叫。
    - (c) GREEN：對帳測試：
    ```
    Passed!  - Failed:     0, Passed:    30, Skipped:     0, Total:    30, Duration: 13 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
  - TDD：N/A（行為不變重構，理由見上）。
- [x] 2.2 步驟 4 `ReconcileMissingAdmittedAsync`。突變：`--from "database.SortedSetAddAsync(admittedKey, idStr, " --to "database.SortedSetAddAsync(admittedKey + \"x\", idStr, "`，預期失敗：`…OnlyAdmittedMirrorIsMissing…`、`…SimulatedRedisRestart…` 一帶。
  - 完成紀錄：
    - (a) 突變 `2_2`（`bash scripts/verify-test-evidence.sh mutate` 於最終程式碼重跑的完整原文，未過濾堆疊、未省略任何失敗測試；mutate 以備份還原檔案，不使用 stash，故無 stash 輸出，退出碼見下方摘要行）：
    ```
    mutation: round.Database.SortedSetAddAsync(round.AdmittedKey, idStr,  -> round.Database.SortedSetAddAsync(round.AdmittedKey + "x", idStr, 
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~PurchaseQueueMirrorReconciliationTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:04.17]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenSnapshotReadFails_SkipsTheWholeRoundAndRecoversOnTheNextRound [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenSnapshotReadFails_SkipsTheWholeRoundAndRecoversOnTheNextRound [183 ms]
      Error Message:
       Expected a value because 下一輪 snapshot 讀取成功後正常補回.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenSnapshotReadFails_SkipsTheWholeRoundAndRecoversOnTheNextRound() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 351
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:14.59]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.StartAsync_PerformsFullStartupReconciliationAcrossAllQueueModeEventsWithoutBlockingStartup [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.StartAsync_PerformsFullStartupReconciliationAcrossAllQueueModeEventsWithoutBlockingStartup [10 s]
      Error Message:
       System.TimeoutException : 等待條件在 10000ms 內未成立：等待啟動時的全量校正完成，涵蓋所有 IsQueueModeEnabled 活動
      Stack Trace:
         at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.WaitUntilAsync(Func`1 condition, String timeoutMessage, Int32 timeoutMs, Int32 pollIntervalMs) in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 79
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.StartAsync_PerformsFullStartupReconciliationAcrossAllQueueModeEventsWithoutBlockingStartup() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 118
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:15.85]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesMalformedNonGuidMembersFromBothMirrorsWithoutAffectingOtherLegalEntries [FAIL]
    [xUnit.net 00:00:15.91]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyAdmittedMirrorIsMissing_RebuildsAdmittedIncludingOverdueJudgmentWithoutAffectingWaiting [FAIL]
    [xUnit.net 00:00:16.03]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanAdmittedMirrorMemberWithNoCorrespondingPostgresRecord [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesMalformedNonGuidMembersFromBothMirrorsWithoutAffectingOtherLegalEntries [35 ms]
      Error Message:
       Expected a value because 其他合法 admitted entry 不受影響.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesMalformedNonGuidMembersFromBothMirrorsWithoutAffectingOtherLegalEntries() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 413
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyAdmittedMirrorIsMissing_RebuildsAdmittedIncludingOverdueJudgmentWithoutAffectingWaiting [59 ms]
      Error Message:
       Expected a value because 尚未逾時的 Admitted 紀錄應被重建進 admitted 鏡像.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyAdmittedMirrorIsMissing_RebuildsAdmittedIncludingOverdueJudgmentWithoutAffectingWaiting() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1319
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanAdmittedMirrorMemberWithNoCorrespondingPostgresRecord [37 ms]
      Error Message:
       Expected a value because 其他合法 entry 的校正結果不受影響.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanAdmittedMirrorMemberWithNoCorrespondingPostgresRecord() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 300
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:17.16]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ZRemFailureDuringReconciliation_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [FAIL]
    [xUnit.net 00:00:17.21]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ExpiredBatchUpdate_WhenItThrows_LeavesEntryAdmittedUntilNextReconciliationMarksItExpired [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ZRemFailureDuringReconciliation_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [1 s]
      Error Message:
       Expected sink.Events {empty} to have an item matching (Convert(e.Level, Int32) == 4) because 達到門檻的那一輪起，當次失敗 MUST 升級為 Error.
      Stack Trace:
         at FluentAssertions.Collections.GenericCollectionAssertions`3.Contain(Expression`1 predicate, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ZRemFailureDuringReconciliation_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1124
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ExpiredBatchUpdate_WhenItThrows_LeavesEntryAdmittedUntilNextReconciliationMarksItExpired [46 ms]
      Error Message:
       Expected a value because 校正應已把尚未逾時的紀錄補進 admitted 鏡像.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ExpiredBatchUpdate_WhenItThrows_LeavesEntryAdmittedUntilNextReconciliationMarksItExpired() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 197
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:19.24]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.IndividualRedisOperationTimeoutDuringReconciliation_DoesNotAbortTheRoundAndConvergesOnceItSucceeds [FAIL]
    [xUnit.net 00:00:19.30]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesStaleAdmittedMirrorEntryFromSimulatedRedisRestartWithoutAffectingOtherLegalEntries [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.IndividualRedisOperationTimeoutDuringReconciliation_DoesNotAbortTheRoundAndConvergesOnceItSucceeds [1 s]
      Error Message:
       Expected a value because 目標活動的名額尚未釋放（因為 ZREM 持續失敗），維持保守，不超額.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.IndividualRedisOperationTimeoutDuringReconciliation_DoesNotAbortTheRoundAndConvergesOnceItSucceeds() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1043
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesStaleAdmittedMirrorEntryFromSimulatedRedisRestartWithoutAffectingOtherLegalEntries [60 ms]
      Error Message:
       Expected a value because 合法的 Admitted 紀錄不受影響，應被補齊.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesStaleAdmittedMirrorEntryFromSimulatedRedisRestartWithoutAffectingOtherLegalEntries() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 273
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:20.67]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanWaitingMirrorMemberWithNoCorrespondingPostgresRecord [FAIL]
    [xUnit.net 00:00:20.74]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt [FAIL]
    [xUnit.net 00:00:21.81]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.JoinPurchaseQueue_WhenMirrorSyncFails_StillSucceedsAndIsReconciledIntoRedisNextRound [FAIL]
    [xUnit.net 00:00:21.94]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyWaitingMirrorIsMissing_RebuildsWaitingWithoutAffectingAdmitted [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanWaitingMirrorMemberWithNoCorrespondingPostgresRecord [64 ms]
      Error Message:
       Expected a value because 其他合法 waiting entry 不受影響，名額已滿故仍留在 waiting.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanWaitingMirrorMemberWithNoCorrespondingPostgresRecord() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 326
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt [65 ms]
      Error Message:
       Expected a value.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 449
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.JoinPurchaseQueue_WhenMirrorSyncFails_StillSucceedsAndIsReconciledIntoRedisNextRound [1 s]
      Error Message:
       Expected a value because 下一輪校正（步驟 3）MUST 把只存在於 Postgres 的紀錄補進 Redis waiting 鏡像.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.JoinPurchaseQueue_WhenMirrorSyncFails_StillSucceedsAndIsReconciledIntoRedisNextRound() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 545
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyWaitingMirrorIsMissing_RebuildsWaitingWithoutAffectingAdmitted [122 ms]
      Error Message:
       Expected a value because waiting 應被正確從 Postgres 重建.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyWaitingMirrorIsMissing_RebuildsWaitingWithoutAffectingAdmitted() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1289
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:22.21]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyOneEventsKeysAreFlushed_RebuildsOnlyThatEventWithoutAffectingOthers [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyOneEventsKeysAreFlushed_RebuildsOnlyThatEventWithoutAffectingOthers [131 ms]
      Error Message:
       Expected a value because 受影響活動的鏡像應被重建.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyOneEventsKeysAreFlushed_RebuildsOnlyThatEventWithoutAffectingOthers() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1351
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:23.99]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Cancellation_BetweenActivities_PropagatesAndLeavesTheTargetActivityUntouchedUntilRetried [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Cancellation_BetweenActivities_PropagatesAndLeavesTheTargetActivityUntouchedUntilRetried [1 s]
      Error Message:
       Expected a value because 目標活動完全未被處理到，鏡像狀態應維持取消前的樣子.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Cancellation_BetweenActivities_PropagatesAndLeavesTheTargetActivityUntouchedUntilRetried() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1003
    --- End of stack trace from previous location ---

    Failed!  - Failed:    15, Passed:    17, Skipped:     0, Total:    32, Duration: 21 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    - (b) 原樣抽取為 `ReconcileMissingAdmittedAsync(round)`；`git diff` 核對：迴圈本體與註解逐字不變，差異只有方法邊界與 `round.` 前綴（`admittedNotOverduePg`、`admittedRedis`、`database`、`admittedKey`）；無 token 參數（原碼此步驟亦不帶）。
    - (c) GREEN：對帳測試：
    ```
    Passed!  - Failed:     0, Passed:    30, Skipped:     0, Total:    30, Duration: 11 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
    - TDD：N/A（行為不變重構，理由見上）。
  - TDD：N/A。
- [x] 2.3 步驟 3 `ReconcileMissingWaitingAsync`。突變：`--from "database.SortedSetAddAsync(waitingKey, idStr, " --to "database.SortedSetAddAsync(waitingKey + \"x\", idStr, "`，另做一個排除條件突變：`--from "if (waitingRedis.Contains(idStr) || admittedRedis.ContainsKey(idStr))" --to "if (waitingRedis.Contains(idStr))"`（守 PQLE-REBUILD-005，預期 `…AtomicallyPromoted…DoesNotDuplicate…` 失敗）。兩個突變各自獨立執行。
  - 完成紀錄：
    - (a) 突變 `2_3a`（`bash scripts/verify-test-evidence.sh mutate` 於最終程式碼重跑的完整原文，未過濾堆疊、未省略任何失敗測試；mutate 以備份還原檔案，不使用 stash，故無 stash 輸出，退出碼見下方摘要行）：
    ```
    mutation: round.Database.SortedSetAddAsync(round.WaitingKey, idStr,  -> round.Database.SortedSetAddAsync(round.WaitingKey + "x", idStr, 
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~PurchaseQueueMirrorReconciliationTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:04.93]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyOnePendingMarkerKeyIsMissing_OnlyThatEntrysAbandonedDecisionIsTriggered [FAIL]
    [xUnit.net 00:00:05.09]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenSnapshotReadFails_SkipsTheWholeRoundAndRecoversOnTheNextRound [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyOnePendingMarkerKeyIsMissing_OnlyThatEntrysAbandonedDecisionIsTriggered [79 ms]
      Error Message:
       Expected (ReadStatusAsync(target.Id)) to be PurchaseQueueEntryStatus.Admitted {value: 1} because target 的 pending 標記遺失，MUST 被代為完成落地, but found PurchaseQueueEntryStatus.Waiting {value: 0}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyOnePendingMarkerKeyIsMissing_OnlyThatEntrysAbandonedDecisionIsTriggered() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1265
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenSnapshotReadFails_SkipsTheWholeRoundAndRecoversOnTheNextRound [160 ms]
      Error Message:
       Expected a value because 下一輪 snapshot 讀取成功後正常補回.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenSnapshotReadFails_SkipsTheWholeRoundAndRecoversOnTheNextRound() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 351
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:15.52]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.StartAsync_PerformsFullStartupReconciliationAcrossAllQueueModeEventsWithoutBlockingStartup [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.StartAsync_PerformsFullStartupReconciliationAcrossAllQueueModeEventsWithoutBlockingStartup [10 s]
      Error Message:
       System.TimeoutException : 等待條件在 10000ms 內未成立：等待啟動時的全量校正完成，涵蓋所有 IsQueueModeEnabled 活動
      Stack Trace:
         at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.WaitUntilAsync(Func`1 condition, String timeoutMessage, Int32 timeoutMs, Int32 pollIntervalMs) in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 79
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.StartAsync_PerformsFullStartupReconciliationAcrossAllQueueModeEventsWithoutBlockingStartup() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 118
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:15.69]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AdvanceQueueOnceAsync_WhenOneActivityThrows_LogsWarningForThatActivityAndStillProcessesOthers [FAIL]
    [xUnit.net 00:00:16.78]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.LockCompetition_WhicheverInstanceAcquiresTheLock_StillReconcilesAndReleasesTheTargetActivitysSlot [FAIL]
    [xUnit.net 00:00:16.88]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyAdmittedMirrorIsMissing_RebuildsAdmittedIncludingOverdueJudgmentWithoutAffectingWaiting [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AdvanceQueueOnceAsync_WhenOneActivityThrows_LogsWarningForThatActivityAndStillProcessesOthers [36 ms]
      Error Message:
       Expected (ReadStatusAsync(healthyWaiting.Id)) to be PurchaseQueueEntryStatus.Admitted {value: 1} because 其餘活動不因單一活動失敗而中止處理，仍應正常推進, but found PurchaseQueueEntryStatus.Waiting {value: 0}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AdvanceQueueOnceAsync_WhenOneActivityThrows_LogsWarningForThatActivityAndStillProcessesOthers() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 810
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.LockCompetition_WhicheverInstanceAcquiresTheLock_StillReconcilesAndReleasesTheTargetActivitysSlot [1 s]
      Error Message:
       Expected (ReadStatusAsync(waitingId)) to be PurchaseQueueEntryStatus.Admitted {value: 1} because 釋放的名額正常供其他 Waiting 紀錄使用, but found PurchaseQueueEntryStatus.Waiting {value: 0}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.LockCompetition_WhicheverInstanceAcquiresTheLock_StillReconcilesAndReleasesTheTargetActivitysSlot() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 877
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyAdmittedMirrorIsMissing_RebuildsAdmittedIncludingOverdueJudgmentWithoutAffectingWaiting [64 ms]
      Error Message:
       Expected a value because waiting 鏡像不受影響.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyAdmittedMirrorIsMissing_RebuildsAdmittedIncludingOverdueJudgmentWithoutAffectingWaiting() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1323
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:18.15]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ZRemFailureDuringReconciliation_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ZRemFailureDuringReconciliation_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [1 s]
      Error Message:
       Expected (ReadStatusAsync(waitingId)) to be PurchaseQueueEntryStatus.Admitted {value: 1}, but found PurchaseQueueEntryStatus.Waiting {value: 0}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ZRemFailureDuringReconciliation_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1129
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:19.12]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ReconciliationRound_WhilePendingMarkerIsAlive_DoesNotClearOrRevertTheTransitionState [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ReconciliationRound_WhilePendingMarkerIsAlive_DoesNotClearOrRevertTheTransitionState [64 ms]
      Error Message:
       Expected a value.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ReconciliationRound_WhilePendingMarkerIsAlive_DoesNotClearOrRevertTheTransitionState() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 145
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:20.90]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.IndividualRedisOperationTimeoutDuringReconciliation_DoesNotAbortTheRoundAndConvergesOnceItSucceeds [FAIL]
    [xUnit.net 00:00:21.16]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedPromotionUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [FAIL]
    [xUnit.net 00:00:21.23]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanWaitingMirrorMemberWithNoCorrespondingPostgresRecord [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.IndividualRedisOperationTimeoutDuringReconciliation_DoesNotAbortTheRoundAndConvergesOnceItSucceeds [1 s]
      Error Message:
       Expected a value because 目標活動的名額尚未釋放（因為 ZREM 持續失敗），維持保守，不超額.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.IndividualRedisOperationTimeoutDuringReconciliation_DoesNotAbortTheRoundAndConvergesOnceItSucceeds() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1043
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedPromotionUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [206 ms]
      Error Message:
       Expected sink.Events {empty} to have an item matching (Convert(e.Level, Int32) == 4) because 達到門檻的那一輪起，當次失敗 MUST 升級為 Error.
      Stack Trace:
         at FluentAssertions.Collections.GenericCollectionAssertions`3.Contain(Expression`1 predicate, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedPromotionUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1177
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanWaitingMirrorMemberWithNoCorrespondingPostgresRecord [64 ms]
      Error Message:
       Expected a value because 其他合法 waiting entry 不受影響，名額已滿故仍留在 waiting.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanWaitingMirrorMemberWithNoCorrespondingPostgresRecord() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 326
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:22.95]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt [1 s]
      Error Message:
       Expected (ReadStatusAsync(waiting.Id)) to be PurchaseQueueEntryStatus.Admitted {value: 1} because 名額釋放後，下一輪推進應提供給下一位等待者, but found PurchaseQueueEntryStatus.Waiting {value: 0}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 478
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:24.03]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.JoinPurchaseQueue_WhenMirrorSyncFails_StillSucceedsAndIsReconciledIntoRedisNextRound [FAIL]
    [xUnit.net 00:00:24.17]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyWaitingMirrorIsMissing_RebuildsWaitingWithoutAffectingAdmitted [FAIL]
    [xUnit.net 00:00:24.27]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.PromotedBatchUpdate_WhenItThrows_LeavesRedisAdmittedAndPendingMarkerUntouched [FAIL]
    [xUnit.net 00:00:24.48]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyOneEventsKeysAreFlushed_RebuildsOnlyThatEventWithoutAffectingOthers [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.JoinPurchaseQueue_WhenMirrorSyncFails_StillSucceedsAndIsReconciledIntoRedisNextRound [1 s]
      Error Message:
       Expected a value because 下一輪校正（步驟 3）MUST 把只存在於 Postgres 的紀錄補進 Redis waiting 鏡像.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.JoinPurchaseQueue_WhenMirrorSyncFails_StillSucceedsAndIsReconciledIntoRedisNextRound() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 545
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyWaitingMirrorIsMissing_RebuildsWaitingWithoutAffectingAdmitted [144 ms]
      Error Message:
       Expected a value because waiting 應被正確從 Postgres 重建.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyWaitingMirrorIsMissing_RebuildsWaitingWithoutAffectingAdmitted() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1289
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.PromotedBatchUpdate_WhenItThrows_LeavesRedisAdmittedAndPendingMarkerUntouched [69 ms]
      Error Message:
       Expected a value because Redis 端不做反向補償，仍留在 admitted.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.PromotedBatchUpdate_WhenItThrows_LeavesRedisAdmittedAndPendingMarkerUntouched() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 173
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyOneEventsKeysAreFlushed_RebuildsOnlyThatEventWithoutAffectingOthers [142 ms]
      Error Message:
       Expected a value because 受影響活動的鏡像應被重建.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyOneEventsKeysAreFlushed_RebuildsOnlyThatEventWithoutAffectingOthers() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1351
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:25.94]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Cancellation_BetweenActivities_PropagatesAndLeavesTheTargetActivityUntouchedUntilRetried [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Cancellation_BetweenActivities_PropagatesAndLeavesTheTargetActivityUntouchedUntilRetried [1 s]
      Error Message:
       Expected a value because 目標活動完全未被處理到，鏡像狀態應維持取消前的樣子.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Cancellation_BetweenActivities_PropagatesAndLeavesTheTargetActivityUntouchedUntilRetried() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1003
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:26.08]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenEntryHasBeenAtomicallyPromotedButPostgresSnapshotStillShowsWaiting_DoesNotDuplicateOrRePromoteIt [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenEntryHasBeenAtomicallyPromotedButPostgresSnapshotStillShowsWaiting_DoesNotDuplicateOrRePromoteIt [136 ms]
      Error Message:
       Expected a value.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenEntryHasBeenAtomicallyPromotedButPostgresSnapshotStillShowsWaiting_DoesNotDuplicateOrRePromoteIt() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 609
    --- End of stack trace from previous location ---

    Failed!  - Failed:    18, Passed:    14, Skipped:     0, Total:    32, Duration: 22 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    - (a) 突變 `2_3b`（`bash scripts/verify-test-evidence.sh mutate` 於最終程式碼重跑的完整原文，未過濾堆疊、未省略任何失敗測試；mutate 以備份還原檔案，不使用 stash，故無 stash 輸出，退出碼見下方摘要行）：
    ```
    mutation: if (round.WaitingRedis.Contains(idStr) || round.AdmittedRedis.ContainsKey(idStr)) -> if (round.WaitingRedis.Contains(idStr))
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~PurchaseQueueMirrorReconciliationTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:05.61]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ConcurrentAdvanceWithOneTransientFailure_StillConvergesCorrectlyWithoutRequiringMutualExclusion [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ConcurrentAdvanceWithOneTransientFailure_StillConvergesCorrectlyWithoutRequiringMutualExclusion [119 ms]
      Error Message:
       Expected (inWaiting && inAdmitted) to be False because entry 7b72fa2f-0583-44cb-9ac8-cec9b3ec3ab3 MUST NOT 同時出現在兩個鏡像, but found True.
      Stack Trace:
         at FluentAssertions.Primitives.BooleanAssertions`1.BeFalse(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ConcurrentAdvanceWithOneTransientFailure_StillConvergesCorrectlyWithoutRequiringMutualExclusion() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1413
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:15.01]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenEntryHasBeenAtomicallyPromotedButPostgresSnapshotStillShowsWaiting_DoesNotDuplicateOrRePromoteIt [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenEntryHasBeenAtomicallyPromotedButPostgresSnapshotStillShowsWaiting_DoesNotDuplicateOrRePromoteIt [138 ms]
      Error Message:
       Did not expect a value because MUST NOT 同時出現在 waiting 鏡像, but found 1794248479343.0.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.NotHaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.BeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenEntryHasBeenAtomicallyPromotedButPostgresSnapshotStillShowsWaiting_DoesNotDuplicateOrRePromoteIt() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 610
    --- End of stack trace from previous location ---

    Failed!  - Failed:     2, Passed:    30, Skipped:     0, Total:    32, Duration: 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    - (b) 原樣抽取為 `ReconcileMissingWaitingAsync(round)`；`git diff` 核對：迴圈本體與註解逐字不變，差異只有方法邊界與 `round.` 前綴；無 token 參數。兩個突變各自獨立執行，皆因測試斷言失敗（`Failed!`）而成立；排除條件突變失敗的測試含 `…AtomicallyPromoted…DoesNotDuplicateOrRePromoteIt`，如預期。
    - (c) GREEN：對帳測試：
    ```
    Passed!  - Failed:     0, Passed:    30, Skipped:     0, Total:    30, Duration: 13 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
  - TDD：N/A。
- [x] 2.4 步驟 6 `RemoveStaleWaitingMembersAsync`。突變：`--from "() => database.SortedSetRemoveAsync(waitingKey, member));" --to "() => Task.CompletedTask);"`，預期失敗：`…MalformedNonGuidMembers…` 不涵蓋 Guid 孤兒；以實際失敗測試為準，無測試失敗則補「waiting 中查無 Postgres 紀錄的孤兒 entryId 被清除」特徵化測試。
  - 完成紀錄：
    - (a) 突變 `2_4`（`bash scripts/verify-test-evidence.sh mutate` 於最終程式碼重跑的完整原文，未過濾堆疊、未省略任何失敗測試；mutate 以備份還原檔案，不使用 stash，故無 stash 輸出，退出碼見下方摘要行）（與 `2_4b` 為同一突變的兩次獨立執行：本次只有孤兒特徵化測試失敗、競態型測試 `ConcurrentAdvanceCalls_…` 未失敗；`2_4b` 兩者皆失敗，印證競態型測試為偶發訊號、孤兒特徵化測試為穩定守衛）：
    ```
    mutation: () => round.Database.SortedSetRemoveAsync(round.WaitingKey, member)); -> () => Task.CompletedTask);
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~PurchaseQueueMirrorReconciliationTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:11.44]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanWaitingMirrorMemberWithNoCorrespondingPostgresRecord [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanWaitingMirrorMemberWithNoCorrespondingPostgresRecord [85 ms]
      Error Message:
       Did not expect a value because 孤兒 waiting member MUST 被清除, but found 1794249099617.0.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.NotHaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.BeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanWaitingMirrorMemberWithNoCorrespondingPostgresRecord() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 325
    --- End of stack trace from previous location ---

    Failed!  - Failed:     1, Passed:    31, Skipped:     0, Total:    32, Duration: 13 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    - (a) 突變 `2_4b`（`bash scripts/verify-test-evidence.sh mutate` 於最終程式碼重跑的完整原文，未過濾堆疊、未省略任何失敗測試；mutate 以備份還原檔案，不使用 stash，故無 stash 輸出，退出碼見下方摘要行）：
    ```
    mutation: () => round.Database.SortedSetRemoveAsync(round.WaitingKey, member)); -> () => Task.CompletedTask);
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~PurchaseQueueMirrorReconciliationTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:08.23]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ConcurrentAdvanceCalls_NeverProduceDualMembershipOrExceedTheAdmissionLimit [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ConcurrentAdvanceCalls_NeverProduceDualMembershipOrExceedTheAdmissionLimit [1 s]
      Error Message:
       Expected (inWaiting && inAdmitted) to be False because iteration 9: entry fb761eb0-3363-45e8-ae5d-925ee1d34e3a MUST NOT 同時出現在 waiting 與 admitted 兩個鏡像, but found True.
      Stack Trace:
         at FluentAssertions.Primitives.BooleanAssertions`1.BeFalse(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ConcurrentAdvanceCalls_NeverProduceDualMembershipOrExceedTheAdmissionLimit() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 699
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:11.02]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanWaitingMirrorMemberWithNoCorrespondingPostgresRecord [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanWaitingMirrorMemberWithNoCorrespondingPostgresRecord [67 ms]
      Error Message:
       Did not expect a value because 孤兒 waiting member MUST 被清除, but found 1794249122467.0.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.NotHaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.BeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanWaitingMirrorMemberWithNoCorrespondingPostgresRecord() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 325
    --- End of stack trace from previous location ---

    Failed!  - Failed:     2, Passed:    30, Skipped:     0, Total:    32, Duration: 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    - (b) 突變 `2_4` 只有 `ConcurrentAdvanceCalls_…`（競態型測試）失敗，屬偶發訊號、不足以單獨守住步驟 6 的 Guid 孤兒清除；依 task 指示補 1 筆特徵化測試 `Reconciliation_RemovesOrphanWaitingMirrorMemberWithNoCorrespondingPostgresRecord`（名額已滿時孤兒不會被推進 ZPOPMIN，只能被步驟 6 清除），在現有碼上通過（GREEN 見 (c) 的 31 筆），並重做突變（`2_4b`）使其穩定失敗。過程中一度誤判該測試在原碼失敗：原因是 mutate 還原檔案用 `cp -p` 保留舊 mtime，增量建置沿用了突變版二進位；之後每次 mutate 後都 `touch` 該檔再跑測試。原樣抽取為 `RemoveStaleWaitingMembersAsync(round)`；`git diff` 核對：迴圈本體與註解逐字不變，差異只有方法邊界與 `round.` 前綴；無 token 參數。
    - (c) GREEN：對帳測試：
    ```
    Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31, Duration: 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
  - TDD：N/A。
- [x] 2.5 步驟 7 `RepairAbandonedPromotionsAsync`。突變：`--from "() => purchaseQueueRepository.AdmitIfWaitingAsync(entryId, now, expiresAtUtc, cancellationToken));" --to "() => Task.CompletedTask);"`，另做 pending 判斷突變：`--from "if (await database.KeyExistsAsync(PurchaseQueueAdmissionRedisKeys.Pending(entryId)))" --to "if (!await database.KeyExistsAsync(PurchaseQueueAdmissionRedisKeys.Pending(entryId)))"`（預期 `…WhilePendingMarkerIsAlive…`、`…OnlyOnePendingMarkerKeyIsMissing…`、`…AbandonedPromotionUpdateFailure…` 失敗）。兩個突變各自獨立執行。
  - 完成紀錄：
    - (a) 突變 `2_5a`（`bash scripts/verify-test-evidence.sh mutate` 於最終程式碼重跑的完整原文，未過濾堆疊、未省略任何失敗測試；mutate 以備份還原檔案，不使用 stash，故無 stash 輸出，退出碼見下方摘要行）：
    ```
    mutation: () => round.PurchaseQueueRepository.AdmitIfWaitingAsync(entryId, round.Now, expiresAtUtc, cancellationToken)); -> () => Task.CompletedTask);
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~PurchaseQueueMirrorReconciliationTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:03.93]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyOnePendingMarkerKeyIsMissing_OnlyThatEntrysAbandonedDecisionIsTriggered [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyOnePendingMarkerKeyIsMissing_OnlyThatEntrysAbandonedDecisionIsTriggered [57 ms]
      Error Message:
       Expected (ReadStatusAsync(target.Id)) to be PurchaseQueueEntryStatus.Admitted {value: 1} because target 的 pending 標記遺失，MUST 被代為完成落地, but found PurchaseQueueEntryStatus.Waiting {value: 0}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyOnePendingMarkerKeyIsMissing_OnlyThatEntrysAbandonedDecisionIsTriggered() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1265
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:11.36]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedPromotionUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedPromotionUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [1 s]
      Error Message:
       Expected sink.Events {empty} to have an item matching (Convert(e.Level, Int32) == 4) because 達到門檻的那一輪起，當次失敗 MUST 升級為 Error.
      Stack Trace:
         at FluentAssertions.Collections.GenericCollectionAssertions`3.Contain(Expression`1 predicate, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedPromotionUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1177
    --- End of stack trace from previous location ---

    Failed!  - Failed:     2, Passed:    30, Skipped:     0, Total:    32, Duration: 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    - (a) 突變 `2_5b`（`bash scripts/verify-test-evidence.sh mutate` 於最終程式碼重跑的完整原文，未過濾堆疊、未省略任何失敗測試；mutate 以備份還原檔案，不使用 stash，故無 stash 輸出，退出碼見下方摘要行）：
    ```
    mutation: if (await round.Database.KeyExistsAsync(PurchaseQueueAdmissionRedisKeys.Pending(entryId))) -> if (!await round.Database.KeyExistsAsync(PurchaseQueueAdmissionRedisKeys.Pending(entryId)))
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~PurchaseQueueMirrorReconciliationTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:03.92]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyOnePendingMarkerKeyIsMissing_OnlyThatEntrysAbandonedDecisionIsTriggered [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyOnePendingMarkerKeyIsMissing_OnlyThatEntrysAbandonedDecisionIsTriggered [66 ms]
      Error Message:
       Expected (ReadStatusAsync(target.Id)) to be PurchaseQueueEntryStatus.Admitted {value: 1} because target 的 pending 標記遺失，MUST 被代為完成落地, but found PurchaseQueueEntryStatus.Waiting {value: 0}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenOnlyOnePendingMarkerKeyIsMissing_OnlyThatEntrysAbandonedDecisionIsTriggered() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1265
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:08.36]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ReconciliationRound_WhilePendingMarkerIsAlive_DoesNotClearOrRevertTheTransitionState [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ReconciliationRound_WhilePendingMarkerIsAlive_DoesNotClearOrRevertTheTransitionState [151 ms]
      Error Message:
       Expected (ReadStatusAsync(target.Id)) to be PurchaseQueueEntryStatus.Waiting {value: 0} because pending 標記存活期間 MUST NOT 被清除或代為完成落地, but found PurchaseQueueEntryStatus.Admitted {value: 1}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ReconciliationRound_WhilePendingMarkerIsAlive_DoesNotClearOrRevertTheTransitionState() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 150
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:10.91]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedPromotionUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedPromotionUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [1 s]
      Error Message:
       Expected sink.Events {empty} to have an item matching (Convert(e.Level, Int32) == 4) because 達到門檻的那一輪起，當次失敗 MUST 升級為 Error.
      Stack Trace:
         at FluentAssertions.Collections.GenericCollectionAssertions`3.Contain(Expression`1 predicate, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedPromotionUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1177
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:15.45]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenEntryHasBeenAtomicallyPromotedButPostgresSnapshotStillShowsWaiting_DoesNotDuplicateOrRePromoteIt [FAIL]
    [xUnit.net 00:00:15.61]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AdvanceScript_WhenEntryAlreadyExistsInBothWaitingAndAdmittedMirrors_DoesNotResetItsAdmissionExpiry [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenEntryHasBeenAtomicallyPromotedButPostgresSnapshotStillShowsWaiting_DoesNotDuplicateOrRePromoteIt [127 ms]
      Error Message:
       Expected (ReadStatusAsync(target.Id)) to be PurchaseQueueEntryStatus.Waiting {value: 0} because pending 標記尚未過期，MUST NOT 被提前代為落地, but found PurchaseQueueEntryStatus.Admitted {value: 1}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenEntryHasBeenAtomicallyPromotedButPostgresSnapshotStillShowsWaiting_DoesNotDuplicateOrRePromoteIt() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 611
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AdvanceScript_WhenEntryAlreadyExistsInBothWaitingAndAdmittedMirrors_DoesNotResetItsAdmissionExpiry [84 ms]
      Error Message:
       Expected (ReadStatusAsync(target.Id)) to be PurchaseQueueEntryStatus.Waiting {value: 0} because pending 標記尚未過期，本輪不應代為落地，聚焦驗證 Redis 端防禦本身, but found PurchaseQueueEntryStatus.Admitted {value: 1}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AdvanceScript_WhenEntryAlreadyExistsInBothWaitingAndAdmittedMirrors_DoesNotResetItsAdmissionExpiry() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 655
    --- End of stack trace from previous location ---

    Failed!  - Failed:     5, Passed:    27, Skipped:     0, Total:    32, Duration: 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    - (b) 原樣抽取為 `RepairAbandonedPromotionsAsync(round, cancellationToken)`；`git diff` 核對：迴圈本體與註解逐字不變，差異只有方法邊界與 `round.` 前綴（`now`→`round.Now`）；`cancellationToken` 仍只傳給 `AdmitIfWaitingAsync`，`KeyExistsAsync` 不帶 token（與原碼一致）。兩個突變各自獨立執行。
    - (c) GREEN：對帳測試：
    ```
    Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31, Duration: 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
  - TDD：N/A。
- [x] 2.6 步驟 5 `RemoveStaleAdmittedMembersAsync`（最後做；`waitingButAdmittedIds` 的計算與其長註解留在主方法，作為第二個參數傳入）。突變 A：`--from "() => database.SortedSetRemoveAsync(admittedKey, member));" --to "() => Task.CompletedTask);"`（預期 `…StaleAdmittedMirrorEntry…`、`…OrphanAdmittedMirrorMember…`、`…ZRemFailureDuringReconciliation…` 失敗）；突變 B：`--from ".Concat(waitingButAdmittedIds)" --to ".Concat(Enumerable.Empty<string>())"`（守 I2，預期 `…WhilePendingMarkerIsAlive…`、`…AtomicallyPromoted…` 失敗）；突變 C：`--from "await TryRemoveMalformedMemberAsync(admittedKey, member, \"admitted\", database);" --to "continue;"`（預期 `…MalformedNonGuidMembers…` 失敗）。三個突變各自獨立執行。
  - 完成紀錄：
    - (a) 突變 `2_6a`（`bash scripts/verify-test-evidence.sh mutate` 於最終程式碼重跑的完整原文，未過濾堆疊、未省略任何失敗測試；mutate 以備份還原檔案，不使用 stash，故無 stash 輸出，退出碼見下方摘要行）：
    ```
    mutation: () => round.Database.SortedSetRemoveAsync(round.AdmittedKey, member)); -> () => Task.CompletedTask);
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~PurchaseQueueMirrorReconciliationTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:05.35]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.LockCompetition_WhicheverInstanceAcquiresTheLock_StillReconcilesAndReleasesTheTargetActivitysSlot [FAIL]
    [xUnit.net 00:00:05.64]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanAdmittedMirrorMemberWithNoCorrespondingPostgresRecord [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.LockCompetition_WhicheverInstanceAcquiresTheLock_StillReconcilesAndReleasesTheTargetActivitysSlot [1 s]
      Error Message:
       Did not expect a value because 不論哪個實例取得鎖，該輪的校正都涵蓋全部活動，目標活動的名額最終被釋放, but found 1794250987694.0.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.NotHaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.BeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.LockCompetition_WhicheverInstanceAcquiresTheLock_StillReconcilesAndReleasesTheTargetActivitysSlot() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 875
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanAdmittedMirrorMemberWithNoCorrespondingPostgresRecord [44 ms]
      Error Message:
       Did not expect a value because 孤兒 member MUST 被清除, but found 1794249789033.0.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.NotHaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.BeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesOrphanAdmittedMirrorMemberWithNoCorrespondingPostgresRecord() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 299
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:07.12]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ZRemFailureDuringReconciliation_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ZRemFailureDuringReconciliation_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [1 s]
      Error Message:
       Expected sink.Events {empty} to have an item matching (Convert(e.Level, Int32) == 4) because 達到門檻的那一輪起，當次失敗 MUST 升級為 Error.
      Stack Trace:
         at FluentAssertions.Collections.GenericCollectionAssertions`3.Contain(Expression`1 predicate, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ZRemFailureDuringReconciliation_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1124
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:09.56]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.IndividualRedisOperationTimeoutDuringReconciliation_DoesNotAbortTheRoundAndConvergesOnceItSucceeds [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.IndividualRedisOperationTimeoutDuringReconciliation_DoesNotAbortTheRoundAndConvergesOnceItSucceeds [1 s]
      Error Message:
       Did not expect a value because 故障排除後，下一次成功執行的校正 MUST 完成收斂, but found 1794250991779.0.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.NotHaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.BeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.IndividualRedisOperationTimeoutDuringReconciliation_DoesNotAbortTheRoundAndConvergesOnceItSucceeds() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1049
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:12.67]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt [1 s]
      Error Message:
       Did not expect a value because 下一次成功的校正 MUST 完成 ZREM，釋放名額, but found 1794250994391.0.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.NotHaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.BeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 477
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:15.29]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Cancellation_BetweenActivities_PropagatesAndLeavesTheTargetActivityUntouchedUntilRetried [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Cancellation_BetweenActivities_PropagatesAndLeavesTheTargetActivityUntouchedUntilRetried [1 s]
      Error Message:
       Did not expect a value because 下一輪重新嘗試後，目標活動的名額最終被釋放，不因曾經取消就被視為「已檢查、不再重試」, but found 1794250997543.0.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.NotHaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.BeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Cancellation_BetweenActivities_PropagatesAndLeavesTheTargetActivityUntouchedUntilRetried() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1010
    --- End of stack trace from previous location ---

    Failed!  - Failed:     6, Passed:    26, Skipped:     0, Total:    32, Duration: 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    - (a) 突變 `2_6b`（`bash scripts/verify-test-evidence.sh mutate` 於最終程式碼重跑的完整原文，未過濾堆疊、未省略任何失敗測試；mutate 以備份還原檔案，不使用 stash，故無 stash 輸出，退出碼見下方摘要行）：
    ```
    mutation: .Concat(waitingButAdmittedIds) -> .Concat(Enumerable.Empty<string>())
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~PurchaseQueueMirrorReconciliationTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:08.16]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ReconciliationRound_WhilePendingMarkerIsAlive_DoesNotClearOrRevertTheTransitionState [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ReconciliationRound_WhilePendingMarkerIsAlive_DoesNotClearOrRevertTheTransitionState [144 ms]
      Error Message:
       Expected a value because MUST NOT 被從 admitted 清除.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.ReconciliationRound_WhilePendingMarkerIsAlive_DoesNotClearOrRevertTheTransitionState() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 151
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:11.33]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedPromotionUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedPromotionUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [1 s]
      Error Message:
       Expected sink.Events {
        Serilog.Events.LogEvent
        {
            Exception = System.InvalidOperationException: Simulated AdmitIfWaitingAsync failure for test.
       at ProjectC.WebApi.Tests.TestSupport.FailingPurchaseQueueRepository.AdmitIfWaitingAsync(Guid entryId, DateTime admittedAtUtc, DateTime admissionExpiresAtUtc, CancellationToken cancellationToken) in /src/tests/ProjectC.WebApi.Tests/TestSupport/FailingPurchaseQueueRepository.cs:line 65
       at ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService.<>c__DisplayClass25_2.<RepairAbandonedPromotionsAsync>b__1() in /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs:line 581
       at ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService.TryTrackedReconcileOperationAsync(Guid entryId, String operation, Func`1 action) in /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs:line 642,
            Level = LogEventLevel.Warning {value: 3},
            MessageTemplate = Purchase queue reconciliation operation {Operation} for entry {EntryId} failed ({ConsecutiveFailureCount} consecutive failures so far).,
            Properties = {["Operation"] = "conditional UPDATE admit (abandoned decision)", ["EntryId"] = ae6b685a-0333-445c-b3eb-9ee0a147e05a, ["ConsecutiveFailureCount"] = 1, ["SourceContext"] = "ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService", ["TraceId"] = "70e22225-fb49-4a3a-8b1d-c9734ef1ccd0"},
            SpanId = <null>,
            Timestamp = <2026-11-09 18:33:39.1676666 +0h>,
            TraceId = <null>
        }
    } to have an item matching (Convert(e.Level, Int32) == 4) because 達到門檻的那一輪起，當次失敗 MUST 升級為 Error.
      Stack Trace:
         at FluentAssertions.Collections.GenericCollectionAssertions`3.Contain(Expression`1 predicate, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedPromotionUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1177
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:16.54]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenEntryHasBeenAtomicallyPromotedButPostgresSnapshotStillShowsWaiting_DoesNotDuplicateOrRePromoteIt [FAIL]
    [xUnit.net 00:00:16.69]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AdvanceScript_WhenEntryAlreadyExistsInBothWaitingAndAdmittedMirrors_DoesNotResetItsAdmissionExpiry [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenEntryHasBeenAtomicallyPromotedButPostgresSnapshotStillShowsWaiting_DoesNotDuplicateOrRePromoteIt [144 ms]
      Error Message:
       Expected a value.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenEntryHasBeenAtomicallyPromotedButPostgresSnapshotStillShowsWaiting_DoesNotDuplicateOrRePromoteIt() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 609
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AdvanceScript_WhenEntryAlreadyExistsInBothWaitingAndAdmittedMirrors_DoesNotResetItsAdmissionExpiry [78 ms]
      Error Message:
       Expected admittedScoreAfter!.Value to be 1794267224584.0 because 已在 admitted 的 entry 被重複選中時 MUST NOT 重設 score（等同非預期延長 AdmissionExpiresAtUtc）, but found 1794249524649.0 (difference of -17699935).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AdvanceScript_WhenEntryAlreadyExistsInBothWaitingAndAdmittedMirrors_DoesNotResetItsAdmissionExpiry() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 652
    --- End of stack trace from previous location ---

    Failed!  - Failed:     4, Passed:    28, Skipped:     0, Total:    32, Duration: 13 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    - (a) 突變 `2_6c`（`bash scripts/verify-test-evidence.sh mutate` 於最終程式碼重跑的完整原文，未過濾堆疊、未省略任何失敗測試；mutate 以備份還原檔案，不使用 stash，故無 stash 輸出，退出碼見下方摘要行）：
    ```
    mutation: await TryRemoveMalformedMemberAsync(round.AdmittedKey, member, "admitted", round.Database); -> continue;
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~PurchaseQueueMirrorReconciliationTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:06.50]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesMalformedNonGuidMembersFromBothMirrorsWithoutAffectingOtherLegalEntries [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesMalformedNonGuidMembersFromBothMirrorsWithoutAffectingOtherLegalEntries [67 ms]
      Error Message:
       Did not expect a value because 格式損壞的 admitted member MUST 被視為孤兒清除, but found 1794249838210.0.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.NotHaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.BeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_RemovesMalformedNonGuidMembersFromBothMirrorsWithoutAffectingOtherLegalEntries() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 411
    --- End of stack trace from previous location ---

    Failed!  - Failed:     1, Passed:    31, Skipped:     0, Total:    32, Duration: 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    - (b) 最後做。原樣抽取為 `RemoveStaleAdmittedMembersAsync(round, waitingButAdmittedIds)`；`waitingButAdmittedIds` 的計算與其長註解留在主方法，作為第二個參數傳入；步驟 5 的註解隨方法搬移。`git diff` 核對：迴圈本體與註解逐字不變，差異只有方法邊界與 `round.` 前綴；無 token 參數。三個突變各自獨立執行（A 守 ZREM、B 守 I2 排除集合、C 守格式損壞 member 清除）。主方法另加一段說明 3～8 順序依賴的註解（新增註解，無行為差異）。
    - (c) GREEN：對帳測試：
    ```
    Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31, Duration: 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
  - TDD：N/A。

## 3. 主方法收尾

- [x] 3.1 確認主方法只剩步驟 1、snapshot 讀取（含失敗 return）、`waitingButAdmittedIds` 計算與六個 `await`，順序為 3、4、（計算）、5、6、7、8。確認 I3：既有測試是否涵蓋「snapshot 讀取失敗時整輪略過」，用突變驗證（突變必須能編譯：例如把 catch 內的 `return;` 改為 `snapshot = new PurchaseQueueRedisMirrorSnapshot([], new Dictionary<string, double>());` 使其以空 snapshot 繼續執行；僅刪除 `return;` 會因 `snapshot` 未指派產生 CS0165，編譯錯誤不得當作證據），測試仍通過則補特徵化測試。所有 mutate 的失敗須為 `Failed!` 測試斷言失敗，腳本對編譯錯誤會回報無法判定（退出碼 2），不算證據。再做 I1 順序突變：對調步驟 5 與步驟 7 的呼叫行（暫時改動，驗後還原），預期至少一個測試失敗。
  - 完成紀錄：
    - (a) 突變 `3_1a0`（`bash scripts/verify-test-evidence.sh mutate` 於最終程式碼重跑的完整原文，未過濾堆疊、未省略任何失敗測試；mutate 以備份還原檔案，不使用 stash，故無 stash 輸出，退出碼見下方摘要行）（重現「尚未補特徵化測試」的狀態：以 `--filter` 排除新補的 `Reconciliation_WhenSnapshotReadFails_…`，故 Total 為 31；退出碼 1 = 測試仍通過，證據不成立）：
    ```
    mutation: skipping reconciliation this round.", eventId);
                return; -> skipping reconciliation this round.", eventId);
                snapshot = new PurchaseQueueRedisMirrorSnapshot([], new Dictionary<string, double>());
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~PurchaseQueueMirrorReconciliationTests&FullyQualifiedName!~Reconciliation_WhenSnapshotReadFails_SkipsTheWholeRoundAndRecoversOnTheNextRound
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31, Duration: 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試仍通過，證據不成立
    ```
    - (a) 突變 `3_1a`（`bash scripts/verify-test-evidence.sh mutate` 於最終程式碼重跑的完整原文，未過濾堆疊、未省略任何失敗測試；mutate 以備份還原檔案，不使用 stash，故無 stash 輸出，退出碼見下方摘要行）：
    ```
    mutation: skipping reconciliation this round.", eventId);
                return; -> skipping reconciliation this round.", eventId);
                snapshot = new PurchaseQueueRedisMirrorSnapshot([], new Dictionary<string, double>());
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~PurchaseQueueMirrorReconciliationTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:04.14]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenSnapshotReadFails_SkipsTheWholeRoundAndRecoversOnTheNextRound [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenSnapshotReadFails_SkipsTheWholeRoundAndRecoversOnTheNextRound [170 ms]
      Error Message:
       Did not expect a value because snapshot 讀取失敗時整輪略過，步驟 3 不得補回 waiting 缺漏的紀錄, but found 1794248982319.0.
      Stack Trace:
         at FluentAssertions.Numeric.NullableNumericAssertions`2.NotHaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Numeric.NullableNumericAssertions`2.BeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.Reconciliation_WhenSnapshotReadFails_SkipsTheWholeRoundAndRecoversOnTheNextRound() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 347
    --- End of stack trace from previous location ---

    Failed!  - Failed:     1, Passed:    31, Skipped:     0, Total:    32, Duration: 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    - (a) 突變 `3_1b`（`bash scripts/verify-test-evidence.sh mutate` 於最終程式碼重跑的完整原文，未過濾堆疊、未省略任何失敗測試；mutate 以備份還原檔案，不使用 stash，故無 stash 輸出，退出碼見下方摘要行）（退出碼 1 = 測試仍通過，等價突變，見 (b)）：
    ```
    mutation: await RemoveStaleAdmittedMembersAsync(round, waitingButAdmittedIds);
            await RemoveStaleWaitingMembersAsync(round);
            await RepairAbandonedPromotionsAsync(round, cancellationToken); -> await RepairAbandonedPromotionsAsync(round, cancellationToken);
            await RemoveStaleWaitingMembersAsync(round);
            await RemoveStaleAdmittedMembersAsync(round, waitingButAdmittedIds);
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~PurchaseQueueMirrorReconciliationTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:    32, Skipped:     0, Total:    32, Duration: 13 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試仍通過，證據不成立
    ```
    - (a) 突變 `3_1c`（`bash scripts/verify-test-evidence.sh mutate` 於最終程式碼重跑的完整原文，未過濾堆疊、未省略任何失敗測試；mutate 以備份還原檔案，不使用 stash，故無 stash 輸出，退出碼見下方摘要行）（本次突變為在步驟 3 之前額外插入一行 `await RepairAbandonedPromotionsAsync(round, cancellationToken);`，原步驟 7 的呼叫保留，即步驟 7 於步驟 3 前多執行一次）：
    ```
    mutation: await ReconcileMissingWaitingAsync(round); -> await RepairAbandonedPromotionsAsync(round, cancellationToken);
            await ReconcileMissingWaitingAsync(round);
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~PurchaseQueueMirrorReconciliationTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:10.71]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedPromotionUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [FAIL]
      Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedPromotionUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry [1 s]
      Error Message:
       Expected sink.Events {
        Serilog.Events.LogEvent
        {
            Exception = System.InvalidOperationException: Simulated AdmitIfWaitingAsync failure for test.
       at ProjectC.WebApi.Tests.TestSupport.FailingPurchaseQueueRepository.AdmitIfWaitingAsync(Guid entryId, DateTime admittedAtUtc, DateTime admissionExpiresAtUtc, CancellationToken cancellationToken) in /src/tests/ProjectC.WebApi.Tests/TestSupport/FailingPurchaseQueueRepository.cs:line 65
       at ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService.<>c__DisplayClass25_2.<RepairAbandonedPromotionsAsync>b__1() in /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs:line 582
       at ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService.TryTrackedReconcileOperationAsync(Guid entryId, String operation, Func`1 action) in /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs:line 643,
            Level = LogEventLevel.Error {value: 4},
            MessageTemplate = Purchase queue reconciliation operation {Operation} for entry {EntryId} has failed {ConsecutiveFailureCount} consecutive times.,
            Properties = {["Operation"] = "conditional UPDATE admit (abandoned decision)", ["EntryId"] = a497bd12-140d-44bc-a52e-2865a87838c9, ["ConsecutiveFailureCount"] = 4, ["SourceContext"] = "ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService", ["TraceId"] = "c8313fd6-fe23-4a74-854f-f64dd903408c"},
            SpanId = <null>,
            Timestamp = <2026-11-09 18:35:34.5164437 +0h>,
            TraceId = <null>
        },
        Serilog.Events.LogEvent
        {
            Exception = System.InvalidOperationException: Simulated AdmitIfWaitingAsync failure for test.
       at ProjectC.WebApi.Tests.TestSupport.FailingPurchaseQueueRepository.AdmitIfWaitingAsync(Guid entryId, DateTime admittedAtUtc, DateTime admissionExpiresAtUtc, CancellationToken cancellationToken) in /src/tests/ProjectC.WebApi.Tests/TestSupport/FailingPurchaseQueueRepository.cs:line 65
       at ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService.<>c__DisplayClass25_2.<RepairAbandonedPromotionsAsync>b__1() in /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs:line 582
       at ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService.TryTrackedReconcileOperationAsync(Guid entryId, String operation, Func`1 action) in /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs:line 643,
            Level = LogEventLevel.Error {value: 4},
            MessageTemplate = Purchase queue reconciliation operation {Operation} for entry {EntryId} has failed {ConsecutiveFailureCount} consecutive times.,
            Properties = {["Operation"] = "conditional UPDATE admit (abandoned decision)", ["EntryId"] = a497bd12-140d-44bc-a52e-2865a87838c9, ["ConsecutiveFailureCount"] = 3, ["SourceContext"] = "ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService", ["TraceId"] = "c8313fd6-fe23-4a74-854f-f64dd903408c"},
            SpanId = <null>,
            Timestamp = <2026-11-09 18:35:34.5162588 +0h>,
            TraceId = <null>
        },
        Serilog.Events.LogEvent
        {
            Exception = System.InvalidOperationException: Simulated AdmitIfWaitingAsync failure for test.
       at ProjectC.WebApi.Tests.TestSupport.FailingPurchaseQueueRepository.AdmitIfWaitingAsync(Guid entryId, DateTime admittedAtUtc, DateTime admissionExpiresAtUtc, CancellationToken cancellationToken) in /src/tests/ProjectC.WebApi.Tests/TestSupport/FailingPurchaseQueueRepository.cs:line 65
       at ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService.<>c__DisplayClass25_2.<RepairAbandonedPromotionsAsync>b__1() in /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs:line 582
       at ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService.TryTrackedReconcileOperationAsync(Guid entryId, String operation, Func`1 action) in /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs:line 643,
            Level = LogEventLevel.Warning {value: 3},
            MessageTemplate = Purchase queue reconciliation operation {Operation} for entry {EntryId} failed ({ConsecutiveFailureCount} consecutive failures so far).,
            Properties = {["Operation"] = "conditional UPDATE admit (abandoned decision)", ["EntryId"] = a497bd12-140d-44bc-a52e-2865a87838c9, ["ConsecutiveFailureCount"] = 2, ["SourceContext"] = "ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService", ["TraceId"] = "438b6d16-2f9f-4a1b-92c8-659b6f1c1b4f"},
            SpanId = <null>,
            Timestamp = <2026-11-09 18:35:34.4685832 +0h>,
            TraceId = <null>
        },
        Serilog.Events.LogEvent
        {
            Exception = System.InvalidOperationException: Simulated AdmitIfWaitingAsync failure for test.
       at ProjectC.WebApi.Tests.TestSupport.FailingPurchaseQueueRepository.AdmitIfWaitingAsync(Guid entryId, DateTime admittedAtUtc, DateTime admissionExpiresAtUtc, CancellationToken cancellationToken) in /src/tests/ProjectC.WebApi.Tests/TestSupport/FailingPurchaseQueueRepository.cs:line 65
       at ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService.<>c__DisplayClass25_2.<RepairAbandonedPromotionsAsync>b__1() in /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs:line 582
       at ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService.TryTrackedReconcileOperationAsync(Guid entryId, String operation, Func`1 action) in /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs:line 643,
            Level = LogEventLevel.Warning {value: 3},
            MessageTemplate = Purchase queue reconciliation operation {Operation} for entry {EntryId} failed ({ConsecutiveFailureCount} consecutive failures so far).,
            Properties = {["Operation"] = "conditional UPDATE admit (abandoned decision)", ["EntryId"] = a497bd12-140d-44bc-a52e-2865a87838c9, ["ConsecutiveFailureCount"] = 1, ["SourceContext"] = "ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService", ["TraceId"] = "438b6d16-2f9f-4a1b-92c8-659b6f1c1b4f"},
            SpanId = <null>,
            Timestamp = <2026-11-09 18:35:34.4683406 +0h>,
            TraceId = <null>
        }
    } to not have any items matching (Convert(e.Level, Int32) == 4) because 未達門檻前每一輪的失敗僅記錄 Warning, but found {
        Serilog.Events.LogEvent
        {
            Exception = System.InvalidOperationException: Simulated AdmitIfWaitingAsync failure for test.
       at ProjectC.WebApi.Tests.TestSupport.FailingPurchaseQueueRepository.AdmitIfWaitingAsync(Guid entryId, DateTime admittedAtUtc, DateTime admissionExpiresAtUtc, CancellationToken cancellationToken) in /src/tests/ProjectC.WebApi.Tests/TestSupport/FailingPurchaseQueueRepository.cs:line 65
       at ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService.<>c__DisplayClass25_2.<RepairAbandonedPromotionsAsync>b__1() in /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs:line 582
       at ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService.TryTrackedReconcileOperationAsync(Guid entryId, String operation, Func`1 action) in /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs:line 643,
            Level = LogEventLevel.Error {value: 4},
            MessageTemplate = Purchase queue reconciliation operation {Operation} for entry {EntryId} has failed {ConsecutiveFailureCount} consecutive times.,
            Properties = {["Operation"] = "conditional UPDATE admit (abandoned decision)", ["EntryId"] = a497bd12-140d-44bc-a52e-2865a87838c9, ["ConsecutiveFailureCount"] = 4, ["SourceContext"] = "ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService", ["TraceId"] = "c8313fd6-fe23-4a74-854f-f64dd903408c"},
            SpanId = <null>,
            Timestamp = <2026-11-09 18:35:34.5164437 +0h>,
            TraceId = <null>
        },
        Serilog.Events.LogEvent
        {
            Exception = System.InvalidOperationException: Simulated AdmitIfWaitingAsync failure for test.
       at ProjectC.WebApi.Tests.TestSupport.FailingPurchaseQueueRepository.AdmitIfWaitingAsync(Guid entryId, DateTime admittedAtUtc, DateTime admissionExpiresAtUtc, CancellationToken cancellationToken) in /src/tests/ProjectC.WebApi.Tests/TestSupport/FailingPurchaseQueueRepository.cs:line 65
       at ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService.<>c__DisplayClass25_2.<RepairAbandonedPromotionsAsync>b__1() in /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs:line 582
       at ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService.TryTrackedReconcileOperationAsync(Guid entryId, String operation, Func`1 action) in /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs:line 643,
            Level = LogEventLevel.Error {value: 4},
            MessageTemplate = Purchase queue reconciliation operation {Operation} for entry {EntryId} has failed {ConsecutiveFailureCount} consecutive times.,
            Properties = {["Operation"] = "conditional UPDATE admit (abandoned decision)", ["EntryId"] = a497bd12-140d-44bc-a52e-2865a87838c9, ["ConsecutiveFailureCount"] = 3, ["SourceContext"] = "ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService", ["TraceId"] = "c8313fd6-fe23-4a74-854f-f64dd903408c"},
            SpanId = <null>,
            Timestamp = <2026-11-09 18:35:34.5162588 +0h>,
            TraceId = <null>
        }
    }.
      Stack Trace:
         at FluentAssertions.Collections.GenericCollectionAssertions`3.NotContain(Expression`1 predicate, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.AbandonedPromotionUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 1174
    --- End of stack trace from previous location ---

    Failed!  - Failed:     1, Passed:    31, Skipped:     0, Total:    32, Duration: 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    - (b) 主方法核對：僅含步驟 1、snapshot 讀取（失敗 return）、`waitingButAdmittedIds` 計算與六個 `await`，順序 3、4、（計算）、5、6、7、8。I3 缺口：`3_1a0`（尚無特徵化測試時）突變後 31 個測試仍通過，證據不成立，故補 1 個特徵化測試 `Reconciliation_WhenSnapshotReadFails_SkipsTheWholeRoundAndRecoversOnTheNextRound`（連線包裝在快照讀取拋 RedisTimeoutException、其餘轉發真實 Redis；現有碼通過，突變 `3_1a` 後失敗）。這是第 2 個新測試（上限 3 個）。I1 順序：`3_1b`（對調步驟 5 與 7）測試仍通過——步驟 5 的排除集合已在呼叫前以 snapshot 算定，兩者互不可觀察，屬等價突變，不構成保護網缺口；改以 `3_1c`（步驟 7 提前到步驟 3 之前）取得有效證據。另發現本測試檔既有 helper `CreateFailNTimesOnSortedSetRemoveConnectionMultiplexer` 只 mock 了 `SortedSetAddAsync(…, CommandFlags)` 舊簽章，服務實際呼叫的是 `SortedSetWhen` 多載，新 helper 已兩者都 mock；舊 helper 未動（待使用者決定）。
    - (c) GREEN：對帳測試：
    ```
    Passed!  - Failed:     0, Passed:    32, Skipped:     0, Total:    32, Duration: 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
  - TDD：N/A。

## 4. 驗證與文件

- [x] 4.1 `docker compose exec -T api dotnet build --no-incremental` 的 warning 清單與 1.1 基準比對：`S3776` 消失、其餘不變且無新增；`docker compose exec -T api dotnet test` 全套各測試專案摘要行（失敗照實記錄並單獨重跑已知偶發項目）；`git diff --stat` 確認只動 `PurchaseQueueAdmissionService.cs`、`PurchaseQueueMirrorReconciliationTests.cs`（若補測試）與文件。
  - 完成紀錄：
    - `dotnet build --no-incremental`：`0 Error(s)`、`2 Warning(s)`；`grep -c S3776` = 0。剩下兩筆皆為 `warning MSB3277: Found conflicts between different versions of "Microsoft.EntityFrameworkCore.Relational"`（Infrastructure.Tests、WebApi.Tests），與 1.1 基準相同，無新增。基準 3 → 2，只少 `S3776` 一筆。
    - 全套 `docker compose exec -T api dotnet test` 摘要行（無失敗、無偶發項目）：
    ```
    Passed!  - Failed:     0, Passed:   153, Skipped:     0, Total:   153, Duration: 127 ms - ProjectC.Domain.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   624, Skipped:     0, Total:   624, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 10 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   280, Skipped:     0, Total:   280, Duration: 53 s - ProjectC.Infrastructure.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   813, Skipped:     0, Total:   813, Duration: 3 m 47 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
      WebApi.Tests 811 → 813 = 新增 2 個特徵化測試（2.4 的孤兒 waiting、3.1 的 snapshot 讀取失敗）。
    - `git diff --stat`（`git status --short` 僅此三檔，另待 4.2 動 `docs/project-scope.md`）：
    ```
     .../reconcile-event-queue-complexity/tasks.md      | 266 ++++++++++++++++++++-
     .../PurchaseQueueAdmissionService.cs               | 153 ++++++++----
     .../PurchaseQueueMirrorReconciliationTests.cs      |  85 +++++++
     3 files changed, 444 insertions(+), 60 deletions(-)
    ```
    - TDD：N/A（驗證 task）。
  - TDD：N/A。
- [x] 4.2 更新 `docs/project-scope.md`「已知技術債：佇列對帳方法複雜度」條目：改為已處理（保留歷史評估與完成日期），移除「須另開 change」的未決語氣；依 CLAUDE.md「文件存放規則」不新增文件。驗證：`bash scripts/check-doc-paths.sh` 通過。
  - 完成紀錄：
    - `docs/project-scope.md` 第 210 行：條目標題改為「已實作 2026-11-10，`reconcile-event-queue-complexity`：認知複雜度 28 → 拆為 6 個 private 方法，S3776 已消失；審查與歸檔進行中」，保留 2026-10-17 原始描述與 2026-10-20 影響評估，「須另開 change」改為已另開處理。未新增文件。歸檔後若需把「審查與歸檔進行中」改為「已歸檔」，由 6.2 一併處理。
    - `git diff --stat docs`：
    ```
     docs/project-scope.md | 2 +-
     1 file changed, 1 insertion(+), 1 deletion(-)
    ```
    - `bash scripts/check-doc-paths.sh`：
    ```
    OK: 所有 docs 路徑引用都存在
    ```
    - TDD：N/A（純文件）。
  - TDD：N/A（純文件）。

## 5. hardener 最終檢查

- [x] 5.1 程式碼變更全部完成後，依 `.claude/skills/hardener/SKILL.md` 檢查 `PurchaseQueueAdmissionService` 的新方法：本 change 不新增防禦分支（純搬移），此 task 確認既有的參數驗證、`CancellationToken` 傳遞、例外邊界（`when (exception is not OperationCanceledException)`）、日誌遮蔽在搬移後與原碼一致；發現搬移造成的差異才依 TDD 補 RED 測試。
  - 完成紀錄：
    - hardener 清單對照（`git diff -U0 src` 過濾 `cancellationToken|CancellationToken|catch|OperationCanceled|Log(Warning|Error)` 的原文輸出）：
    ```
    +        await RepairAbandonedPromotionsAsync(round, cancellationToken);
    +        await RepairAbandonedTimeoutsAsync(round, cancellationToken);
    +    private async Task RepairAbandonedPromotionsAsync(ReconciliationRound round, CancellationToken cancellationToken)
    -                () => purchaseQueueRepository.AdmitIfWaitingAsync(entryId, now, expiresAtUtc, cancellationToken));
    +                () => round.PurchaseQueueRepository.AdmitIfWaitingAsync(entryId, round.Now, expiresAtUtc, cancellationToken));
    +    private async Task RepairAbandonedTimeoutsAsync(ReconciliationRound round, CancellationToken cancellationToken)
    -                () => purchaseQueueRepository.ExpireBatchAsync([entryId], cancellationToken));
    +                () => round.PurchaseQueueRepository.ExpireBatchAsync([entryId], cancellationToken));
    ```
    - 參數驗證／Entity 存在性：搬移前後皆無（內部 private 方法，輸入來自同一輪已查得的集合，`Guid.TryParse` 失敗分支原樣保留在步驟 5／6）；無差異。
    - 狀態機驗證：步驟 7 仍走 `AdmitIfWaitingAsync`、步驟 8 仍走 `ExpireBatchAsync`（條件式 UPDATE，由 repository 端判定狀態），未變。
    - `CancellationToken`：只有步驟 7／8 的 repository 呼叫帶 token（原樣），Redis 呼叫仍不帶 token（維持 design.md 決策 1 所述現況）；步驟 3／4／5／6 的方法不接受 token，與原碼一致。
    - 例外邊界：`catch … when (exception is not OperationCanceledException)` 只存在於 `Try*` 輔助方法與 snapshot 讀取，上述 diff 過濾結果顯示 catch／`OperationCanceledException`／`LogWarning`／`LogError` 行沒有任何增減，邊界未變。
    - 並發：`_consecutiveFailureCounts` 的使用仍僅在 `TryTrackedReconcileOperationAsync`，未改；record 為不可變（`HashSet`／`Dictionary` 僅在單一輪內循序使用）。
    - 日誌遮蔽：log 訊息範本與參數（`operation`、`entryId`、`member`、`setName`）零差異，未新增記錄敏感資訊。
    - 結論：搬移未造成任何防禦分支差異，無需補 RED 測試、未修改程式碼；hardener 未導致再次呼叫 strict-reviewer 的額外改動。
    - TDD：N/A（無新增防禦分支）。
  - TDD：N/A（無新增行為；若發現缺口再補）。

## 6. 審查與歸檔

- [x] 6.1 依 CLAUDE.md「開發流程總覽」步驟 6：`strict-reviewer` 與全新的對抗式 general-purpose subagent 依序審查（只給 change 名稱與審查目標，不給前輪結論；對抗式 reviewer 的審查目標含「逐行核對搬移前後等價」與「步驟 5 排除集合的跨步驟依賴」），或以 `cross-review` skill 走外部審查；直到沒有 blocking。每輪的工具／模型與 commit 記錄於下。
  - 完成紀錄：採 cross-review 三 pane 流程。(1) 實作 pane 內 strict-reviewer 第 1 輪 FAIL（B-001 證據被過濾／截斷），修正後第 2 輪 PASS，無 blocking，未改程式碼。(2) 外部審查第 1 輪：Claude Code Opus 5.5（pane w2:p18）strict-reviewer 審 commit f464e2b，PASS、無 blocking，未改任何檔案；對帳測試 Passed! - Failed: 0, Passed: 32, Skipped: 0, Total: 32 - ProjectC.WebApi.Tests.dll (net10.0)。(3) 經使用者指示不做 cross-review 步驟 5 的最後主 session 審查（對抗式 subagent）。非 blocking 建議留待另案：舊 helper CreateFailNTimesOnSortedSetRemoveConnectionMultiplexer 只 mock 舊簽章；方法宣告順序與執行順序不同。
- [x] 6.2 使用者明確同意後 `/opsx:archive`（無 delta spec，skip_specs）並合併回 master。
  - 完成紀錄：使用者 2026-11-10 明確同意歸檔並合併 master；無 delta spec（skip_specs）。
