# Tasks

> 所有指令在容器內執行（`docker compose exec ...`），不在本機執行 dotnet。
>
> 本 change `skip_specs: true`，無新增 Acceptance Criteria；受守護的既有規則為 `purchase-queue` PQ-COMPLETE-001／002。以下準則取代 AC：
>
> | 準則 | 驗證 |
> | --- | --- |
> | G1：`Complete()` 移到 `CommitAsync` 之後時，至少一個自動化測試因排隊紀錄 `Status` 斷言（Expected `Completed`／Actual `Admitted`）失敗；Redis 或環境造成的失敗不算 | 1.1（既有測試）或 2.2（新測試）的突變證據 |
> | G2：`src/` 無殘留變動 | 3.1 |
>
> 證據一律貼實際輸出（每個失敗測試名稱與訊息、每個測試專案摘要行、`mutate`／`git status` 輸出），不得以摘要或「全數通過」代替。
>
> TDD：特徵化測試，RED 由 `mutate` 取得（暫移 `Complete()` 後測試因 `Status` 斷言失敗）、GREEN 為還原後重跑通過、重構 N/A（design.md 決策 3，不另立例外）。`restore` 還原驗證 N/A（無正式程式碼修改）。前端無變更。提交規則：不自動 commit／push。
>
> hardener：不適用（無正式程式碼變更）。

## 1. 判定缺口是否已被既有測試守護

- [x] 1.1 先確認基準：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~AdvanceQueueOnceAsync_AfterOrderServiceCompletesAnEntry_NextRoundAdmitsTheNextWaitingEntryViaTheRealSyncPath|FullyQualifiedName~OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt"`（兩個既有測試）皆通過。再用 `bash scripts/verify-test-evidence.sh mutate`（單一改動：`OrderService.cs` 的 `queueEntry?.Complete();` 由 `CommitAsync` 前移到後；字面取代）對同一 filter 跑一次。`--from` 須從 `queueEntry?.Complete();` 一路包到第 280 行的 `await transaction.CommitAsync(cancellationToken);`（該字串在檔內出現兩次，單獨比對會失敗；檔案為 CRLF，參數用 `$'...\r\n...'`，不可用 LF），`--to` 把 `Complete()` 放在該 `CommitAsync` 之後、`connection.DisposeAsync()` 之前，確切字串寫入完成紀錄。**mutate 結束後必須**：`touch src/ProjectC.Application/Orders/OrderService.cs`（腳本以 `cp -p` 還原會連 mtime 一併還原，MSBuild 不會重編而沿用突變版 DLL），再重跑同一 filter 確認通過，貼 touch 與重跑原文。判定（filter 已含兩個既有測試）：任一個因 `Completed` 斷言（Expected Completed／Actual Admitted）失敗 → 記為「已守護」並跳到第 3 節（第 2 節 N/A）；通過 → 缺口成立，進第 2 節。
  - 完成紀錄：
    - 基準：`Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 2 s - ProjectC.WebApi.Tests.dll (net10.0)`
    - `mutate` 原文（2026-11-07 重跑，輸出為腳本原文，未經編輯；`--from`／`--to` 實際以 bash 的 ANSI-C 引號字串傳入 CRLF，腳本回顯時 CR 不可見；`--file src/ProjectC.Application/Orders/OrderService.cs`）：
      ```
      mutation: queueEntry?.Complete();
      
              _orderRepository.Add(result.Value!);
              timings.CommitStarting = Stopwatch.GetTimestamp();
              await transaction.CommitAsync(cancellationToken); -> _orderRepository.Add(result.Value!);
              timings.CommitStarting = Stopwatch.GetTimestamp();
              await transaction.CommitAsync(cancellationToken);
              queueEntry?.Complete();
      $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~AdvanceQueueOnceAsync_AfterOrderServiceCompletesAnEntry_NextRoundAdmitsTheNextWaitingEntryViaTheRealSyncPath|FullyQualifiedName~OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt
        Determining projects to restore...
        All projects are up-to-date for restore.
        ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
        ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
        ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
        ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
        ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
      Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
      A total of 1 test files matched the specified pattern.
      [xUnit.net 00:00:05.21]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueAdmissionServiceTests.AdvanceQueueOnceAsync_AfterOrderServiceCompletesAnEntry_NextRoundAdmitsTheNextWaitingEntryViaTheRealSyncPath [FAIL]
        Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueAdmissionServiceTests.AdvanceQueueOnceAsync_AfterOrderServiceCompletesAnEntry_NextRoundAdmitsTheNextWaitingEntryViaTheRealSyncPath [1 s]
        Error Message:
         Expected (ReadStatusAsync(admittedEntry.Id)) to be PurchaseQueueEntryStatus.Completed {value: 2}, but found PurchaseQueueEntryStatus.Admitted {value: 1}.
        Stack Trace:
           at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueAdmissionServiceTests.AdvanceQueueOnceAsync_AfterOrderServiceCompletesAnEntry_NextRoundAdmitsTheNextWaitingEntryViaTheRealSyncPath() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueAdmissionServiceTests.cs:line 550
      --- End of stack trace from previous location ---
      [xUnit.net 00:00:06.00]     ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt [FAIL]
        Failed ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt [1 s]
        Error Message:
         Expected (ReadStatusAsync(admittedEntry.Id)) to be PurchaseQueueEntryStatus.Completed {value: 2} because Postgres 端立即為 Completed，不受 Redis 同步失敗影響, but found PurchaseQueueEntryStatus.Admitted {value: 1}.
        Stack Trace:
           at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.BackgroundServices.PurchaseQueueMirrorReconciliationTests.OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt() in /src/tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs:line 382
      --- End of stack trace from previous location ---
      
      Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 1 s - ProjectC.WebApi.Tests.dll (net10.0)
      結果：測試如預期失敗
      exit=0
      ```
    - 還原與重跑原文（`touch` 無輸出）：
      ```
      $ touch src/ProjectC.Application/Orders/OrderService.cs
      $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~AdvanceQueueOnceAsync_AfterOrderServiceCompletesAnEntry_NextRoundAdmitsTheNextWaitingEntryViaTheRealSyncPath|FullyQualifiedName~OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt"
        Determining projects to restore...
        All projects are up-to-date for restore.
        ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
        ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
        ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
        ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
        ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
      Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
      A total of 1 test files matched the specified pattern.
      
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 2 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - `git status --short src/` 為空。
    - 判定：**已守護**（失敗皆為 `ReadStatusAsync(admittedEntry.Id)` 的 `Completed` 斷言，非 Redis／環境）。G1 由本節滿足；第 2 節 N/A。
  - TDD：此突變即 RED／GREEN 證據（理由見上）。

## 2. 新增整合測試（僅 1.1 判定缺口成立時；否則整節 N/A 並註明）

- [x] 2.1 （N/A：1.1 判定已守護，不新增測試） 在 `tests/ProjectC.Infrastructure.Tests`（位置依 design.md 決策 2）新增 `PlaceOrderAsync_WhenAdmittedQueueEntryOrderSucceeds_PersistsEntryAsCompletedInDatabase` 類名稱的測試（命名遵循 `MethodName_Scenario_ExpectedResult`）：用 `PostgresFixture`、`OrderServiceTestFactory.Create`；Seed 計數票種活動＋排隊模式開啟＋買家 `Admitted` 且未過期的紀錄；下單成功；以**新** `DbContext` 讀回紀錄，斷言 `Status == Completed`，並斷言下單結果成功（確認真的走到 `Complete()` 分支，不是因提早失敗而空轉）。
  - 完成紀錄：N/A（1.1 判定已守護，不新增測試）
  - TDD：特徵化測試，RED／GREEN 由 2.2 突變取得；先決條件為 1.1 判定缺口成立。
- [x] 2.2 （N/A：1.1 判定已守護，不新增測試） 突變證明：對新測試跑 `bash scripts/verify-test-evidence.sh mutate`（同 1.1 的單一改動）；預期新測試失敗，貼失敗訊息原文（Expected／Actual）；結束後同 1.1 須 `touch` 並重跑新測試確認通過，貼原文。若不失敗，如實記錄並回到設計（design.md 風險）。
  - 完成紀錄：N/A（同上）
- [x] 2.3 （N/A：1.1 判定已守護，不新增測試） 跑相關回歸：`docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~OrderService"`，貼摘要行。
  - 完成紀錄：N/A（同上）

## 3. 收尾

- [x] 3.1 確認 `git status --short src/` 為空、`git status --short` 只含 `tests/`、`docs/`、`openspec/`（`openspec/changes/...` 的新檔為 untracked，不能只看 `git diff --stat`）；貼輸出原文。
  - 完成紀錄：
    ```
    $ git status --short src/
    （空）
    $ git status --short
     M docs/project-scope.md
     M openspec/changes/order-queue-complete-commit-guard/tasks.md
     M tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueAdmissionServiceTests.cs
    ```
- [x] 3.2 更新 `docs/project-scope.md` §8 該技術債條目：寫入判定結果（已被既有測試守護／已由新測試守護）、測試名稱、突變證據位置，並移除「之後若處理須另開 change」之未來式。走「已守護」路徑時，另在 `PurchaseQueueAdmissionServiceTests` 該測試 (a) 斷言旁補一行「為什麼」註解（此測試同時守護 `Complete()` 必須在 `CommitAsync` 之前；僅註解，無行為變動），此為本 change 唯一允許的 WebApi.Tests 變動。
  - TDD：N/A（純文件）。
- [x] 3.3 全套回歸：`docker compose exec api dotnet test`，各測試專案摘要行貼原文；已知偶發失敗（`RealNameLoggingTests`、`HostShutdownSeqFlushTests`、時間相關）失敗時照實記錄並單獨重跑。
  - 完成紀錄：
    - `Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 114 ms - ProjectC.Domain.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   549, Skipped:     0, Total:   549, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 9 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: 49 s - ProjectC.Infrastructure.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   676, Skipped:     0, Total:   676, Duration: 3 m 1 s - ProjectC.WebApi.Tests.dll (net10.0)`
- [x] 3.4 程式碼變更（新測試或註解）完成後，依 CLAUDE.md 流程跑 strict-reviewer 與全新對抗式 subagent 審查迴圈，兩者皆無 blocking 才可歸檔。
- [x] 3.5 完成後告知：與 spec 一致性、spec 同步狀態（無需更新）、AC 測試涵蓋（G1、G2）、維護性提醒（重複邏輯、命名一致性）。
