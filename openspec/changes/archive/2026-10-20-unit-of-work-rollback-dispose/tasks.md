# Tasks

> 所有指令都在容器內執行（`docker compose exec api dotnet test ...`），不在本機執行 dotnet。整合測試用 Testcontainers，不連開發用的 db 服務。
>
> `UnitOfWorkTests` 指 `tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs`；`ThrowingTransactionInterceptor` 為該檔內新增的 private `DbTransactionInterceptor`，可分別設定 `TransactionRollingBackAsync`／`TransactionCommittingAsync` 拋出測試建立的例外實例；`ThrowingCloseInterceptor` 由 `UnitOfWorkOpenConnectionTests` 的 private 類別移到 `tests/ProjectC.Infrastructure.Tests/TestSupport/ThrowingCloseInterceptor.cs` 共用（design 決策 3）。
>
> 測試命令（下稱 `UOW 測試`）：`docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~ProjectC.Infrastructure.Tests.UnitOfWorkTests"`

## AC 與測試對應

| AC | 測試檔／方法 | 任務 |
| --- | --- | --- |
| UOW-TX-001 | `UnitOfWorkTests`：既有 `DisposeAsync_WithoutCommitOrRollback_AutomaticallyRollsBack`（行為已存在，維持通過） | 1.2、2.1 |
| UOW-TX-002 | `UnitOfWorkTests`：`DisposeAsync_WhenRollbackFails_LogsWarningAndDoesNotThrow`（斷言 `DisposeAsync` 不拋例外、`ListLogger<UnitOfWork>` 恰好一筆 Warning 且 `Exception` 為 interceptor 拋出的同一實例） | 1.1、1.2 |
| UOW-TX-003 | `UnitOfWorkTests`：`DisposeAsync_WhenRollbackFails_ReleasesTransactionAndDiscardsWrites`（交易內 `Venues.Add` 後 `SaveChangesAsync` 寫入；以 `Record.ExceptionAsync` 執行 `DisposeAsync`，使本測試只判定釋放結果、不受是否拋例外影響；斷言 `Database.CurrentTransaction` 為 null、連線 `State` 為 `Closed`、同一 `UnitOfWork` 再 `BeginTransactionAsync` 成功、另一個 `DbContext` 查不到該 venue） | 1.1、1.2 |
| UOW-TX-004 | `UnitOfWorkTests`：`DisposeAsync_WhenRollbackFailsDuringExceptionUnwind_PreservesOriginalException`（在 `await using` 範圍內拋出測試建立的 `InvalidOperationException` 實例；斷言外層捕捉到的是同一實例，不是回滾例外；並斷言恰好一筆 Warning 且 `Exception` 為回滾例外，證明回滾確實觸發且被吞掉） | 1.1、1.2 |
| UOW-TX-005 | `UnitOfWorkTests`：`DisposeAsync_WhenRollbackAndTransactionDisposeFail_LogsBothWarningsAndDoesNotThrow`（同時掛 `ThrowingTransactionInterceptor`（回滾拋例外）與 `ThrowingCloseInterceptor`；未提交即 `DisposeAsync`；斷言不拋例外、恰好兩筆 Warning，`Exception` 分別為回滾例外與關閉例外的同一實例（design 決策 3 推論的驗證點）） | 1.1、1.2 |
| UOW-TX-006 | `UnitOfWorkTests`：`DisposeAsync_AfterCommitFails_PreservesCommitExceptionAndReleasesTransaction`（`ThrowingTransactionInterceptor` 提交與回滾都拋例外；`await using` 範圍內 `Venues.Add` 後 `CommitAsync`；`CommitAsync` 與離開範圍分段：以 `Record.ExceptionAsync` 包住整個 `await using` 區塊；斷言捕捉到的是提交例外實例、恰好一筆 Warning 且 `Exception` 為回滾例外、`Database.CurrentTransaction` 為 null、另一個 `DbContext` 查不到該 venue） | 1.1、1.2 |
| UOW-TX-007 | `UnitOfWorkTests`：`DisposeAsync_WhenRollbackFailsOnCallerOpenedConnection_ReleasesTransactionAndKeepsConnectionOpen`（先 `OpenConnectionAsync` 再 `BeginTransactionAsync`，交易內 `Venues.Add` 後 `SaveChangesAsync`；`ThrowingTransactionInterceptor` 回滾拋例外；斷言 `DisposeAsync` 不拋例外、`Database.CurrentTransaction` 為 null、連線 `State` 為 `Open`、同一 `UnitOfWork` 再 `BeginTransactionAsync` 成功；釋放 `IUnitOfWorkConnection` 後連線 `State` 為 `Closed`、另一個 `DbContext` 查不到該 venue） | 3.1 |
| UOW-TX-008 | `UnitOfWorkTests`：`DisposeAsync_WhenConnectionCloseFailsDuringRollback_LogsWarningAndReleasesTransaction`（只掛 `ThrowingCloseInterceptor`；未提交即 `DisposeAsync`；斷言不拋例外、恰好一筆 Warning 且 `Exception` 為關閉例外實例、`Database.CurrentTransaction` 為 null） | 3.3 |

## 1. 自動回滾失敗時仍釋放交易

- [x] 1.1 測試先行（RED）：將 `ThrowingCloseInterceptor` 移到 `TestSupport`（先跑 `UnitOfWorkOpenConnectionTests` 確認搬移後仍通過，屬測試重構、不算 RED）；在 `UnitOfWorkTests` 新增 `ThrowingTransactionInterceptor` 與 UOW-TX-002～006 五個測試，修改 `UnitOfWork.cs` 前執行 `UOW 測試`。預期 RED（目標行為不符，非編譯或環境錯誤）：
  - UOW-TX-002：`DisposeAsync` 拋出 interceptor 的回滾例外（Expected 不拋例外）。
  - UOW-TX-003：`Database.CurrentTransaction` Expected null，Actual 非 null（交易未被釋放，即 §8 待辦 ③ 的推論）；若此斷言意外通過，停止並回報，推論不成立需回到 Grill。
  - UOW-TX-004：捕捉到的是回滾例外而非原本的 `InvalidOperationException`。
  - UOW-TX-005：`DisposeAsync` 拋出回滾例外（Expected 不拋例外）。此 RED 無法證明「關閉例外發生在釋放交易內」，該推論由 1.2 GREEN 驗證（見 1.2 停止條件）。
  - UOW-TX-006：捕捉到的是回滾例外而非提交例外（離開範圍時的回滾例外蓋掉提交例外）。
  - 所有 RED 的失敗例外都必須從 `DisposeAsync`（或 `await using` 離開範圍）處傳出，UOW-TX-006 的提交例外除外；若任何測試在其他呼叫處（例如 `BeginTransactionAsync`、`SaveChangesAsync`）失敗，不算有效 RED，停止並回報。
  - 防禦分支對照（hardener 檢查清單）：參數驗證／Entity 存在性／狀態機／並發不適用（`DisposeAsync` 無參數、無 Entity、無共享狀態）；`CancellationToken` 不適用（`IAsyncDisposable.DisposeAsync` 無 token，自動回滾維持不帶 token）；例外邊界即 UOW-TX-002／004；日誌遮蔽：Warning 只帶例外與固定訊息，不含資料內容。
  - 完成紀錄：
    - 搬移後 `UnitOfWorkOpenConnectionTests` 9/9 通過（測試重構，非 RED）。
    - RED 命令與時間點（修改正式程式碼前）：`UOW 測試`，2026-10-20T00:38+08:00（`git status --short src` 無輸出，`UnitOfWork.cs` 未改）；結果 `Failed: 5, Passed: 6, Total: 11`。
    - 失敗測試與失敗訊息原文摘錄（Expected／Actual）：
      - UOW-TX-002 `DisposeAsync_WhenRollbackFails_LogsWarningAndDoesNotThrow`：`Did not expect any exception, but found System.InvalidOperationException: rollback failed`（堆疊經 `UnitOfWorkTransaction.DisposeAsync() ... UnitOfWork.cs:line 112`）。
      - UOW-TX-003 `DisposeAsync_WhenRollbackFails_ReleasesTransactionAndDiscardsWrites`：`Expected dbContext.Database.CurrentTransaction to be <null> because 回滾失敗仍須釋放交易, but found Microsoft.EntityFrameworkCore.Storage.RelationalTransaction`。
      - UOW-TX-004 `DisposeAsync_WhenRollbackFailsDuringExceptionUnwind_PreservesOriginalException`：`Expected (act to refer to System.InvalidOperationException: business failure ..., but found System.InvalidOperationException: rollback failed`。
      - UOW-TX-005 `DisposeAsync_WhenRollbackAndTransactionDisposeFail_LogsBothWarningsAndDoesNotThrow`：`Did not expect any exception, but found System.InvalidOperationException: rollback failed`。
      - UOW-TX-006 `DisposeAsync_AfterCommitFails_PreservesCommitExceptionAndReleasesTransaction`：`Expected exception to refer to System.InvalidOperationException: commit failed ..., but found System.InvalidOperationException: rollback failed`（回滾例外堆疊經 `UnitOfWork.cs:line 112`）。
    - 失敗原因（對應目標行為）：回滾例外從 `UnitOfWorkTransaction.DisposeAsync` 傳出（002／004／005／006，蓋掉業務例外與提交例外），且其後的 `_transaction.DisposeAsync()` 被跳過、交易未釋放（003，證實 §8 待辦 ③ 推論）。所有失敗例外都從 `DisposeAsync`（或 `await using` 離開範圍）傳出，006 的提交例外照預期來自 `CommitAsync`。
    - 停止條件處理（使用者 2026-10-20 核准）：第一次 RED（00:36）UOW-TX-005 回報的是 `close failed`，拋出點為測試結尾 `await using dbContext` 的 `DbContext.DisposeAsync → RelationalConnection.ResetStateAsync → RelationalTransaction.DisposeAsync → ClearTransactionAsync → CloseAsync`，蓋掉了斷言失敗。這正是被漏掉的交易延到 DbContext 結束才釋放的表現，也證實決策 3 的推論（交易自行開啟的連線在回滾失敗後，改在釋放交易時才關閉）。處理方式：只改 005 的 DbContext 釋放，改為 try/finally，並以 `Record.ExceptionAsync` 吞掉釋放失敗、附註解說明原因；改完重跑得到上列斷言訊息。殘餘風險：GREEN 後若 DbContext 釋放仍拋例外，005 不會察覺；由 `UnitOfWorkOpenConnectionTests.DisposeAsync_WhenCloseFails_LogsWarningAndDoesNotThrow`（仍用 `await using dbContext`）的同類行為間接佐證。
- [x] 1.2 最小實作（GREEN）：依 design 決策 1、2，`UnitOfWorkTransaction` 建構子新增 `ILogger`、`BeginTransactionAsync` 傳入 `_logger`；`DisposeAsync` 改為 try（自動回滾）／catch `Exception`（記 Warning，註解說明不重拋理由）／finally（`_completed = true`，並以 try／catch `Exception` 釋放交易，失敗記另一則 Warning）。執行 `UOW 測試`，通過條件：UOW-TX-001～006 全數通過、既有 `UnitOfWorkTests` 其餘測試通過；再執行 `docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~UnitOfWork"`（含 `UnitOfWorkOpenConnectionTests`）全數通過。停止條件：若 UOW-TX-005 因缺少關閉例外那筆 Warning 而無法通過（design 決策 3 推論不成立），停止並回報，回到 design 重新決定失敗注入點，不得改寫斷言遷就實作。
  - 完成紀錄：
    - GREEN 命令與結果（通過數／失敗數）：`docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~UnitOfWork"` → `Passed: 20, Failed: 0`（`UnitOfWorkTests` 11 個，含 UOW-TX-001～006，加上 `UnitOfWorkOpenConnectionTests` 9 個）；`UOW 測試` → `Passed: 11, Failed: 0`。
    - 停止條件：未觸發。UOW-TX-005 通過，恰好兩筆 Warning，例外依序為回滾例外與關閉例外，決策 3 的推論成立。
    - 殘餘風險核對：暫時把 005 finally 內的 `Record.ExceptionAsync` 改回直接 `await dbContext.DisposeAsync()` 並單獨執行 005 → `Passed: 1`，表示修正後 DbContext 釋放不會再拋關閉例外；核對完已還原測試檔。
- [x] 1.3 還原驗證（bug fix 適用 CLAUDE.md「RED 還原驗證」）：`git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs` → 執行 `UOW 測試`，確認 UOW-TX-002～006 因目標行為不符而失敗（測試檔保留，不是編譯錯誤）→ `git stash list` 確認該 stash 存在 → `git stash pop`；pop 失敗或衝突時立即停止並回報。pop 後再跑一次 `UOW 測試` 確認恢復通過。
  - 完成紀錄：
    - 第一次還原驗證（1.2 GREEN 後，當時 5 個新測試）：stash → `UOW 測試` → `Failed: 5, Passed: 6, Total: 11`（UOW-TX-002～006）→ `git stash list` 有 `stash@{0}` → `git stash pop` 成功（`Dropped refs/stash@{0}`）；pop 後 `UOW 測試` → `Passed: 11, Failed: 0`。當次只保留摘要，未逐一抄錄各測試訊息，因此以下方重跑紀錄為準。
    - 還原驗證重跑（2026-10-20T01:11:12+08:00，3.1～3.3 完成後，涵蓋 UOW-TX-002～008，逐測試保留訊息原文）：
      1. `git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs` → `Saved working directory and index state WIP on feature/unit-of-work-rollback-dispose: 59bbfab branch-and-propose 需求明確時可直接提出共識摘要，不為形式提問`；`git status --short src` 無輸出。
      2. `git stash list` → `stash@{0}: WIP on feature/unit-of-work-rollback-dispose: 59bbfab branch-and-propose 需求明確時可直接提出共識摘要，不為形式提問`。
      3. `docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~ProjectC.Infrastructure.Tests.UnitOfWorkTests"` → exit 1，`Failed!  - Failed:     7, Passed:     6, Skipped:     0, Total:    13`；輸出中 `error CS` 0 筆（非編譯錯誤）。通過的 6 個是 UOW-TX-001 與既有測試。失敗測試與 Error Message 原文：
         - UOW-TX-002 `DisposeAsync_WhenRollbackFails_LogsWarningAndDoesNotThrow`：`Did not expect any exception, but found System.InvalidOperationException: rollback failed`
         - UOW-TX-003 `DisposeAsync_WhenRollbackFails_ReleasesTransactionAndDiscardsWrites`：`Expected dbContext.Database.CurrentTransaction to be <null> because 回滾失敗仍須釋放交易, but found Microsoft.EntityFrameworkCore.Storage.RelationalTransaction`
         - UOW-TX-004 `DisposeAsync_WhenRollbackFailsDuringExceptionUnwind_PreservesOriginalException`：`Expected (act to refer to System.InvalidOperationException: business failure ...（堆疊 UnitOfWorkTests.cs:line 173）, but found System.InvalidOperationException: rollback failed`
         - UOW-TX-005 `DisposeAsync_WhenRollbackAndTransactionDisposeFail_LogsBothWarningsAndDoesNotThrow`：`Did not expect any exception, but found System.InvalidOperationException: rollback failed`
         - UOW-TX-006 `DisposeAsync_AfterCommitFails_PreservesCommitExceptionAndReleasesTransaction`：`Expected exception to refer to System.InvalidOperationException: commit failed ...（堆疊 ThrowingTransactionInterceptor.TransactionCommittingAsync）, but found System.InvalidOperationException: rollback failed`
         - UOW-TX-007 `DisposeAsync_WhenRollbackFailsOnCallerOpenedConnection_ReleasesTransactionAndKeepsConnectionOpen`：`Did not expect any exception, but found System.InvalidOperationException: rollback failed`
         - UOW-TX-008 `DisposeAsync_WhenConnectionCloseFailsDuringRollback_LogsWarningAndReleasesTransaction`：`Did not expect any exception, but found System.InvalidOperationException: close failed`
         - 正式程式碼堆疊：002／004～008 的 `but found` 例外堆疊皆經 `ProjectC.Infrastructure.Persistence.UnitOfWorkTransaction.DisposeAsync()`（006 另有提交例外經 `UnitOfWorkTransaction.CommitAsync`，照預期）；003 失敗於 `CurrentTransaction` 斷言（交易未釋放）。皆為目標行為不符。
      4. `git stash list` → 同一筆 `stash@{0}` 仍在 → `git stash pop` → `Dropped refs/stash@{0} (655b3f8af66ceb4f8e488df1d34532e7fbba0d6d)`，無衝突；`git status --short` 與 stash 前相同。
    - pop 後重跑結果（GREEN）：`docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~ProjectC.Infrastructure.Tests.UnitOfWork"` → `Passed!  - Failed:     0, Passed:    22, Skipped:     0, Total:    22`（`UnitOfWorkTests` 13 + `UnitOfWorkOpenConnectionTests` 9）。
- [x] 1.4 重構（若有）：重構後重跑 `UOW 測試` 並記錄結果；無需重構則記錄 N/A。
  - 完成紀錄：N/A。GREEN 的改動只有一個建構子參數加上 try/catch/finally，結構比照同檔 `UnitOfWorkConnection.DisposeAsync`，沒有可抽出的重複。
- [x] 1.5 文件：`docs/project-scope.md` §8 待辦（2026-10-07）③ 標記 ✅ 並註明由 `unit-of-work-rollback-dispose` 處理、RED 已證實推論。驗證：`grep -n "unit-of-work-rollback-dispose" docs/project-scope.md` 有結果。TDD N/A（純文件）。

## 2. 整合確認

- [x] 2.1 全套回歸：`docker compose exec api dotnet test ProjectC.slnx` 全數通過（失敗時先判斷是否為既知 flaky，並在此記錄測試名稱與重跑結果，不得略過）；`openspec validate unit-of-work-rollback-dispose --strict` 通過。TDD N/A（整合確認，不新增行為）。
  - 完成紀錄：`docker compose exec api dotnet test ProjectC.slnx` → Domain 151、Application 527、LoadTest.Seeder 38、Infrastructure 239、WebApi 541，全數通過（0 失敗、0 略過），本次沒有出現 flaky；`openspec validate unit-of-work-rollback-dispose --strict` → `Change 'unit-of-work-rollback-dispose' is valid`。
    - 3.1／3.2 後重跑（2026-10-20）：Infrastructure 1 失敗 `QueryCacheTtlSafetyNetTests.GetTicketTypesHandler_WhenInvalidationHappensWhileQueryInFlight_CacheTemporarilyResurrectsStaleValue_ButExpiresWithinTtl`（TTL 計時測試，與 UnitOfWork 無關；執行時對抗式 subagent 同時在容器內跑測試）。單獨重跑該類別 3 次皆 `Passed: 9`，Infrastructure 全套重跑 `Passed: 240`，判定為既知時間類 flaky。
    - 3.3 後重跑：Domain 151、Application 527、LoadTest.Seeder 38、Infrastructure 241、WebApi 541，全數通過。
    - 最終全套（2026-10-20T01:12:52+08:00，還原驗證重跑 pop 之後；完整輸出存檔後擷取各 DLL 摘要行）：`docker compose exec api dotnet test ProjectC.slnx` → exit 0，`error CS` 0 筆：
      - `Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151 - ProjectC.Domain.Tests.dll`
      - `Passed!  - Failed:     0, Passed:   527, Skipped:     0, Total:   527 - ProjectC.Application.Tests.dll`
      - `Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38 - ProjectC.LoadTest.Seeder.Tests.dll`
      - `Passed!  - Failed:     0, Passed:   241, Skipped:     0, Total:   241 - ProjectC.Infrastructure.Tests.dll`
      - `Passed!  - Failed:     0, Passed:   541, Skipped:     0, Total:   541 - ProjectC.WebApi.Tests.dll`

## 3. 審查補強（第 1 輪對抗式審查，2026-10-20）

- [x] 3.1 呼叫端先開連線的回滾失敗測試（UOW-TX-007）：下單主路徑（`OrderService` 先 `OpenConnectionAsync` 再 `BeginTransactionAsync`）沒有回滾失敗測試。正式程式碼已在 1.2 修正，因此 RED 改以還原法取得：新增測試 → `git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs` → 執行 `UOW 測試` → 預期 RED：UOW-TX-007 因 `DisposeAsync` 拋出回滾例外而失敗（Expected 不拋例外）→ `git stash list` 確認 → `git stash pop`（失敗或衝突立即停止回報）→ GREEN：`UOW 測試` 全數通過。重構 N/A（只新增測試）。
  - 完成紀錄：
    - RED（還原法，2026-10-20T00:46+08:00）：`git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs`（`git status --short src` 無輸出）→ `UOW 測試` → `Failed: 6, Passed: 6, Total: 12`（UOW-TX-002～007）。UOW-TX-007 `DisposeAsync_WhenRollbackFailsOnCallerOpenedConnection_ReleasesTransactionAndKeepsConnectionOpen`：`Did not expect any exception, but found System.InvalidOperationException: rollback failed`。失敗原因對應目標行為：修正前回滾例外從 `DisposeAsync` 傳出。→ `git stash list` 顯示 `stash@{0}: WIP on feature/unit-of-work-rollback-dispose: 59bbfab ...` → `git stash pop` 成功（`Dropped refs/stash@{0}`），沒有衝突。
    - GREEN：pop 後連同 3.2 一起執行 `--filter "FullyQualifiedName~UnitOfWork"` → `Passed: 21, Failed: 0`（`UnitOfWorkTests` 12 個加 `UnitOfWorkOpenConnectionTests` 9 個）。
- [x] 3.2 Warning 文字：自動回滾的 Warning 改為「Automatic rollback of the unit of work transaction failed on dispose (the rollback or the connection close it triggers).」。原因：交易自行開啟連線時，EF 在 `RollbackAsync` 內接著關閉連線，關閉失敗也會從回滾傳出，原文字會誤判為回滾失敗（推論，未驗證）。TDD N/A：只改訊息字串，行為不變；測試只斷言例外實例，不斷言訊息文字。驗證：`UOW 測試` 通過。
  - 完成紀錄：`UnitOfWork.cs` 回滾 catch 的訊息已替換；`--filter "FullyQualifiedName~UnitOfWork"` → `Passed: 21, Failed: 0`。

- [x] 3.3 回滾過程中關閉連線失敗（UOW-TX-008，第 2 輪對抗式審查發現、主 agent 以暫時探測測試證實）：交易自行開啟連線、回滾成功但關閉連線失敗時，交易已釋放、例外不外傳，但實體連線維持 `Open` 到 `DbContext` 釋放，期間同一工作單元 `OpenConnectionAsync` 拋「A connection is already open」。此殘餘寫入 spec Requirement 與 design Risks；新增測試釘住可保證的部分。RED 以還原法取得：新增測試 → `git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs` → `UOW 測試` → 預期 RED：UOW-TX-008 因 `DisposeAsync` 拋出關閉例外而失敗 → `git stash list` → `git stash pop`（失敗或衝突立即停止回報）→ GREEN：`UOW 測試` 全數通過。重構 N/A（只新增測試）。
  - 完成紀錄：
    - 探測（修正後、寫測試前，暫時測試已刪除）：只掛 `ThrowingCloseInterceptor`、未提交即釋放 → `disposeEx=` 無、`txNull=True`、`state=Open`、Warning 一筆（回滾訊息，例外 `close failed`）、同一工作單元 `OpenConnectionAsync` 拋 `A connection is already open on this unit of work.`、`DbContext` 釋放無例外。
    - RED（還原法，2026-10-20T00:55+08:00）：`git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs` → `UOW 測試` → `Failed: 7, Passed: 6, Total: 13`（UOW-TX-002～008）；UOW-TX-008 `DisposeAsync_WhenConnectionCloseFailsDuringRollback_LogsWarningAndReleasesTransaction`：`Did not expect any exception, but found System.InvalidOperationException: close failed`（修正前關閉例外經回滾從 `DisposeAsync` 傳出）→ `git stash list` 顯示 `stash@{0}: WIP on feature/unit-of-work-rollback-dispose: 59bbfab ...` → `git stash pop` 成功（`Dropped refs/stash@{0}`），無衝突。
    - GREEN：`--filter "FullyQualifiedName~UnitOfWork"` → `Passed: 22, Failed: 0`。
    - 同一還原法涵蓋 002～008 全部測試的逐測試失敗訊息見 1.3「還原驗證重跑」。

## Workflow follow-up

- 程式碼全部完成後套用 `hardener`（Infrastructure 交易層），發現缺漏依 TDD 補 RED 並新增「hardener 補強」task。
- 審查迴圈：`strict-reviewer` + 全新對抗式 general-purpose subagent，直到兩者皆無 blocking。
- 無前端變更，不需真實瀏覽器驗證。
- 使用者同意後 `/opsx:archive`（建立主 spec `openspec/specs/unit-of-work-transaction/spec.md`）→ merge 回 master；不自動 commit／push。
