# Tasks

> 所有指令都在容器內執行（`docker compose exec api dotnet test ...`），不在本機執行 dotnet。整合測試用 Testcontainers，不連開發用的 db 服務。
>
> `UnitOfWorkTests` 指 `tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs`，`UnitOfWorkOpenConnectionTests` 指同目錄 `UnitOfWorkOpenConnectionTests.cs`。`ThrowingTransactionInterceptor` 是 `UnitOfWorkTests` 內的 private interceptor；`ThrowingCloseInterceptor`、`SwitchableCloseInterceptor`（2.1 新增）在 `TestSupport`。
>
> 測試命令（下稱 `UOW 測試`）：`docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~ProjectC.Infrastructure.Tests.UnitOfWork"`（同時涵蓋 `UnitOfWorkTests` 與 `UnitOfWorkOpenConnectionTests`）
>
> 證據一律貼原文（CLAUDE.md「證據貼原文」）：每個失敗測試的 Error Message 原文、stash push／list／pop 輸出、全套回歸各 DLL 摘要行；不得以「同 2.1」等摘要代替。

## AC 與測試對應

| AC | 測試檔／方法 | 任務 |
| --- | --- | --- |
| UOW-TX-018 | `UnitOfWorkTests`：新增 `BeginTransactionAsync_WhenStartFails_ClosesConnectionAndRethrows`（`ThrowingTransactionInterceptor(startFailure:)`；斷言拋出同一個開始例外、`CurrentTransaction` 為 null、`State` 為 `Closed`、無 Warning；移除 interceptor 失敗後可再開始交易：interceptor 以旗標切換，第二次開始前關閉注入） | 2.1、2.2、2.3 |
| UOW-TX-019 | `UnitOfWorkTests`：新增 `BeginTransactionAsync_WhenCancelledDuringStart_ClosesConnection`（`startFailure` 為 `OperationCanceledException`，見 design 決策 3「取消的模擬」；斷言拋出 `OperationCanceledException`、`State` 為 `Closed`、可再開始交易） | 2.1、2.2、2.3 |
| UOW-TX-020 | `UnitOfWorkTests`：新增 `BeginTransactionAsync_WhenStartAndConnectionCloseFail_PreservesStartExceptionAndLogsWarning`（`startFailure` 加上 `ThrowingCloseInterceptor`；斷言拋出的是開始例外、恰好一筆 Warning 且 `Exception` 為關閉例外） | 2.1、2.2、2.3 |
| UOW-TX-021 | `UnitOfWorkTests`：新增 `BeginTransactionAsync_WhenStartFailsOnCallerOpenedConnection_KeepsCallerConnectionOpen`（先 `OpenConnectionAsync`，再以 `startFailure` 開始交易；斷言拋出開始例外、`State` 為 `Open`、可執行 `SELECT 1`、無 Warning） | 2.1、2.2 |
| UOW-TX-022 | `UnitOfWorkTests`：新增 `CommitAsync_OnLeftoverOpenConnection_WhenCloseFailsAgain_DoesNotThrowAndPersistsChanges`（`SwitchableCloseInterceptor` 維持失敗；第一筆交易提交後連線留在 `Open`，第二筆交易寫入 venue 後提交；斷言第二次 `CommitAsync` 與 `DisposeAsync` 不拋、恰好兩筆 Warning 皆為關閉例外、另開 `DbContext` 讀得到資料） | 3.1、3.2、3.3 |
| UOW-TX-023 | `UnitOfWorkTests`：新增 `CommitAsync_OnLeftoverOpenConnection_ClosesConnectionBeforeDispose`（第一筆交易提交、關閉失敗後把 `IsFailing` 設為 false，第二筆交易提交；斷言提交後、釋放前 `State` 為 `Closed`、資料存在） | 3.1、3.2 |
| UOW-TX-024 | `UnitOfWorkOpenConnectionTests`：新增 `OpenConnectionAsync_OnLeftoverOpenConnection_TakesOverAndClosesOnDispose`（第一筆交易提交、關閉失敗後把 `IsFailing` 設為 false，再 `OpenConnectionAsync`；斷言不拋、兩次 `pg_backend_pid()` 相同且查詢後 `State` 為 `Open`（6.1 加強）、釋放連線後 `State` 為 `Closed`） | 3.1、3.2、3.3、6.1 |
| UOW-TX-025 | `UnitOfWorkOpenConnectionTests`：新增 `BeginTransactionAsync_AfterCallerConnectionDisposed_OwnsConnection`（`IsFailing` 初始為 false；`OpenConnectionAsync` 後釋放，再把 `IsFailing` 設為 true，開始交易、寫入、提交；斷言 `CommitAsync` 不拋、恰好一筆 Warning 為關閉例外、資料存在） | 3.1、3.2 |
| UOW-TX-026 | `UnitOfWorkOpenConnectionTests`：新增 `BeginTransactionAsync_AfterCallerConnectionCloseFailed_OwnsConnection`（掛 `ThrowingCloseInterceptor`；`OpenConnectionAsync` 後釋放（關閉失敗），再開始交易、寫入、提交；斷言 `CommitAsync` 與交易 `DisposeAsync` 不拋、恰好兩筆 Warning 皆為關閉例外、資料存在） | 3.1、3.2、3.3 |
| UOW-TX-027 | `UnitOfWorkOpenConnectionTests`：既有 `OpenConnectionAsync_WhenAlreadyOpen_ThrowsInvalidOperationException`（行為保護，判斷依據改為 `_hasCallerConnection` 後仍須通過） | 3.1、3.2 |
| UOW-TX-028 | `UnitOfWorkOpenConnectionTests`：既有 `OpenConnectionAsync_WhenTransactionInProgress_ThrowsInvalidOperationException`（行為保護） | 3.1、3.2 |
| UOW-TX-029 | `UnitOfWorkOpenConnectionTests`：新增 `DisposeAsync_WhileTransactionInProgress_LogsWarningAndCommitStillPersists`（`OpenConnectionAsync`、開始交易、寫入 venue，提交前釋放連線；斷言釋放不拋、恰好一筆 Warning 且 `Exception` 為 null、`CommitAsync` 不拋、提交後 `State` 為 `Closed`（7.1 加強）、資料存在） | 5.1、5.2、5.3、7.1 |
| UOW-TX-030 | `UnitOfWorkOpenConnectionTests`：新增 `DisposeAsync_AfterCommit_DoesNotLogWarning`（OrderService 順序：提交後釋放連線、再釋放交易；斷言無 Warning、`State` 為 `Closed`） | 5.1、5.2 |
| UOW-TX-031 | `UnitOfWorkOpenConnectionTests`：新增 `OpenConnectionAsync_OnLeftoverOpenConnection_WhenCancelled_ThrowsAndDoesNotTakeOver`（遺留連線後恢復關閉，以已取消的 token 開啟；斷言拋出 `OperationCanceledException`，之後再開啟並釋放後 `State` 為 `Closed`） | 7.2、7.3 |

主 spec 既有的 Scenario（UOW-TX-001～010、013～017；011、012 已於前一 change 移除）不在本 change 的 delta 內，既有測試（`UnitOfWorkTests` 20 個、`UnitOfWorkOpenConnectionTests` 9 個）由 `UOW 測試` 與 4.2 全套回歸涵蓋，必須維持通過。

## 1. 探測 EF 開啟計數推論

- [x] 1.1 探測（不保留，TDD N/A：驗證 design「Context」推論，不是目標行為的測試）：修改正式程式碼前，在 `UnitOfWorkTests` 暫時新增探測測試，直接操作 `dbContext.Database`，不經過工作單元：
  - (a) `OpenConnectionAsync` → 以 interceptor 讓 `BeginTransactionAsync` 失敗 → 呼叫 `CloseConnectionAsync` 一次後 `State` 為 `Open`，第二次後為 `Closed`。
  - (b) `OpenConnectionAsync` → `BeginTransactionAsync` → 提交，關閉時讓 `SwitchableCloseInterceptor` 失敗 → `State` 為 `Open`；把 `IsFailing` 設為 false，呼叫 `CloseConnectionAsync` 一次後為 `Closed`（計數已為 0）。
  - (c) 承 (b) 的遺留狀態，`OpenConnectionAsync` 一次、`CloseConnectionAsync` 一次後為 `Closed`（決策 1、2 接手的前提）。
  - (d) 承 (b) 的遺留狀態，`OpenConnectionAsync` 一次 → 以 interceptor 讓 `BeginTransactionAsync` 失敗 → 重複 `CloseConnectionAsync`，記錄每次關閉後的 `State`，預期不超過 3 次即為 `Closed`（決策 3 上限在「接手加開始失敗」組合下足夠）。

  探測所需的 interceptor 在本步驟先加入並保留：`ThrowingTransactionInterceptor` 的 `startFailure`（2.1 使用）與 `TestSupport/SwitchableCloseInterceptor`（3.1 使用）。執行後刪除探測測試，以 `git diff --stat tests` 確認只剩這兩項 interceptor 變更。**停止條件**：任一點與推論不符時，停止並回報，回到 design，不進入 2.1。
  - 完成紀錄（2026-10-20）：
    - 命令：`docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~UnitOfWorkTests.Probe"`。探測測試以 `XunitException` 訊息輸出實際狀態（刻意失敗作為輸出管道），輸出原文：
      ```
      PROBE(a): begin threw=start failed; after close#1=Open; after close#2=Closed
      PROBE: [b] after commit=Open; failing close threw=close failed, state=Open; (b) close once=Closed || [c] after commit=Open; failing close threw=close failed, state=Open; (c) after open=Open; (c) after close once=Closed || [d] after commit=Open; failing close threw=close failed, state=Open; (d) begin threw=start failed; (d) after close#1=Open; (d) after close#2=Closed
      Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 83 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
    - 結論：(a) 開始失敗後第 1 次關閉 `Open`、第 2 次 `Closed`；(b) 關閉失敗後 `Open`，恢復後關一次 `Closed`（計數已為 0）；(c) 接手（開啟一次）後關一次 `Closed`；(d) 接手後開始失敗，第 1 次關閉 `Open`、第 2 次 `Closed`，共 2 次，上限 3 足夠。四點皆與 design 推論相符，未觸發停止條件。
    - 刪除探測後 `git diff --stat tests`（另有 untracked `TestSupport/SwitchableCloseInterceptor.cs`）：
      ```
       tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs | 15 +++++++++++++--
       1 file changed, 13 insertions(+), 2 deletions(-)
      ?? tests/ProjectC.Infrastructure.Tests/TestSupport/SwitchableCloseInterceptor.cs
      ```

## 2. 開始交易失敗時關閉工作單元負責的連線

- [x] 2.1 測試先行（RED）：使用 1.1 加入的 `startFailure`（覆寫 `TransactionStartingAsync`，以可切換旗標控制是否拋出）；在 `UnitOfWorkTests` 新增 UOW-TX-018～021 四個測試（見 AC 表）。修改 `UnitOfWork.cs` 前執行 `UOW 測試`。預期：
  - UOW-TX-018（RED）：`Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Closed ... but found ConnectionState.Open`。
  - UOW-TX-019（RED）：同上，因取消而遺留 `Open`。
  - UOW-TX-020（RED）：`Expected logger.Entries to contain a single item matching ... but the collection is empty`（修正前不會嘗試關閉）。
  - UOW-TX-018～020 若在開始例外的型別或身分斷言上失敗（例如拋出的不是注入的例外），不算有效 RED，停止並回報。
  - UOW-TX-021（預期通過，行為保護）：修正前不會關閉呼叫端連線。
  - 既有 29 個測試維持通過。
  - 防禦分支對照（hardener 檢查清單）：
    - 參數驗證、Entity 存在性：不適用（無業務參數、無 Entity）。
    - 狀態機：已有進行中交易時拋出（既有測試）；呼叫端持有連線時不關閉（UOW-TX-021）。
    - `CancellationToken`：開始途中被取消時關閉連線（UOW-TX-019）；`Database.OpenConnectionAsync` 本身被取消或失敗時（try 之前）不處理，依 EF 開啟失敗不增加計數的行為判斷不會遺留連線（design 決策 3，推論）。
    - 並發：不適用（`DbContext` 非 thread-safe，同一工作單元不並發使用）。
    - 例外邊界：開始例外以 `throw;` 原樣重拋（UOW-TX-018 斷言同一實例）；關閉失敗不取代原例外（UOW-TX-020）。
    - 日誌遮蔽：Warning 只帶例外與固定訊息。
  - 安全確認（CLAUDE.md 安全確認問題）：不接受外部輸入、不新增或修改權限邏輯、API 或前端；資料庫存取全走 EF Core（`Database.OpenConnectionAsync`／`BeginTransactionAsync`／`CloseConnectionAsync`），不新增 SQL，測試中的 `SELECT 1` 與既有測試相同；不新增查詢，無 N+1 風險。
  - 完成紀錄：
    - 修改 `UnitOfWork.cs` 前：`2026-10-20 20:32:48`，`git status --short src` 輸出為空（正式程式碼未修改）。
    - `UOW 測試` 摘要行：`Failed!  - Failed:     3, Passed:    30, Skipped:     0, Total:    33, Duration: 675 ms - ProjectC.Infrastructure.Tests.dll (net10.0)`
    - 失敗測試原文：
      ```
      Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.BeginTransactionAsync_WhenStartFails_ClosesConnectionAndRethrows [29 ms]
      Error Message:
       Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Closed {value: 0} because 開始失敗後工作單元開啟的連線須立即歸還, but found ConnectionState.Open {value: 1}.
      Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.BeginTransactionAsync_WhenStartAndConnectionCloseFail_PreservesStartExceptionAndLogsWarning [6 ms]
      Error Message:
       Expected logger.Entries to contain a single item matching (Convert(e.Level, Int32) == 3), but the collection is empty.
      Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.BeginTransactionAsync_WhenCancelledDuringStart_ClosesConnection [6 ms]
      Error Message:
       Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Closed {value: 0} because 取消後工作單元開啟的連線須立即歸還, but found ConnectionState.Open {value: 1}.
      ```
    - 失敗原因：018、019 的例外身分／型別斷言已通過（斷言在 `State` 之前），失敗於連線仍為 `Open`，即修正前開始失敗不關閉；020 的例外身分斷言已通過，失敗於沒有 Warning，即修正前不嘗試關閉。皆為目標行為不符。
    - 通過的行為保護：UOW-TX-021 `BeginTransactionAsync_WhenStartFailsOnCallerOpenedConnection_KeepsCallerConnectionOpen` 與既有 29 個（30 passed = 29 + 1）。
- [x] 2.2 最小實作（GREEN）：依 design 決策 3，在 `BeginTransactionAsync` 以 try／catch 包住 `Database.BeginTransactionAsync`；`ownsConnection` 為 true 時重複 `CloseConnectionAsync` 至 `State == Closed`，上限 3 次，關閉失敗記 Warning 並停止，3 次後仍開啟另記 Warning；最後 `throw;`。catch 內的 `CloseConnectionAsync` 不傳 `CancellationToken`（避免已取消的 token 影響關閉）。註解須寫明：(1) 「3 次後仍開啟」的 Warning 是防禦分支、spec 不保證（design 決策 3；「沒有注入點可觸發」的原宣稱已被第 2 輪對抗式審查推翻，見 design 決策 3）；(2) 重複關閉依賴 EF Core（`Directory.Packages.props` 目前 10.0.11）開始失敗後計數殘留、遺留連線接手時只加計數的內部行為，升級 EF 時以 UOW-TX-018～026 作為回歸偵測。執行 `UOW 測試`，全數通過。
  - 完成紀錄：
    - 實作：catch 以例外篩選 `when (ownsConnection)` 限定自有連線，關閉迴圈抽成 `CloseConnectionAfterFailedStartAsync`。
    - `UOW 測試` 摘要行：`Passed!  - Failed:     0, Passed:    33, Skipped:     0, Total:    33, Duration: 595 ms - ProjectC.Infrastructure.Tests.dll (net10.0)`
    - 重構：N/A（實作即為最終形狀）。
- [x] 2.3 還原驗證：`git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs` → `git stash list` 確認存在 → 執行 `UOW 測試`，預期 UOW-TX-018、019、020 依 2.1 的原因失敗，其餘通過 → `git stash list` 確認後 `git stash pop`。pop 失敗或有衝突時立即停止並回報。
  - 完成紀錄：
      ```
      $ git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs
      Saved working directory and index state WIP on feature/unit-of-work-connection-ownership: ae68925 新增 unit-of-work-connection-ownership 提案
      $ git stash list
      stash@{0}: WIP on feature/unit-of-work-connection-ownership: ae68925 新增 unit-of-work-connection-ownership 提案
      $ UOW 測試
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.BeginTransactionAsync_WhenStartFails_ClosesConnectionAndRethrows [33 ms]
        Error Message:
         Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Closed {value: 0} because 開始失敗後工作單元開啟的連線須立即歸還, but found ConnectionState.Open {value: 1}.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.BeginTransactionAsync_WhenStartAndConnectionCloseFail_PreservesStartExceptionAndLogsWarning [6 ms]
        Error Message:
         Expected logger.Entries to contain a single item matching (Convert(e.Level, Int32) == 3), but the collection is empty.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.BeginTransactionAsync_WhenCancelledDuringStart_ClosesConnection [4 ms]
        Error Message:
         Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Closed {value: 0} because 取消後工作單元開啟的連線須立即歸還, but found ConnectionState.Open {value: 1}.
      Failed!  - Failed:     3, Passed:    30, Skipped:     0, Total:    33, Duration: 634 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      $ git stash list
      stash@{0}: WIP on feature/unit-of-work-connection-ownership: ae68925 新增 unit-of-work-connection-ownership 提案
      $ git stash pop
      ...
      Dropped refs/stash@{0} (fcdeb617cd5a83b061ed3477b6089fc3791fc2b5)
      ```
    - 結論：三個測試依 2.1 的原因失敗，其餘 30 個通過；pop 無衝突，工作樹還原。

## 3. 連線歸屬依工作單元的記錄判斷

- [x] 3.1 測試先行（RED）：使用 1.1 加入的 `SwitchableCloseInterceptor`；在 `UnitOfWorkTests` 新增 UOW-TX-022、023，在 `UnitOfWorkOpenConnectionTests` 新增 UOW-TX-024、025、026（見 AC 表）。執行 `UOW 測試`（此時 `UnitOfWork.cs` 只含 2.2 的變更）。預期：
  - UOW-TX-022（RED）：第二次 `CommitAsync` 拋出 `close failed`（Expected 不拋例外），堆疊經 `RelationalTransaction.CommitAsync` → `ClearTransactionAsync` → `RelationalConnection.CloseAsync`，即修正前判定為非自有、由 EF 在提交時關閉。若失敗於第一次提交或 `BeginTransactionAsync`，不算有效 RED，停止並回報。
  - UOW-TX-024（RED）：`OpenConnectionAsync` 拋出 `InvalidOperationException: A connection is already open on this unit of work.`。
  - UOW-TX-026（RED）：`CommitAsync` 拋出 `close failed`（Expected 不拋例外），堆疊經 `RelationalTransaction.CommitAsync` → `ClearTransactionAsync`：呼叫端連線釋放時關閉失敗留下開啟的連線，修正前判定為非自有。若失敗於 `OpenConnectionAsync` 或連線的 `DisposeAsync`，不算有效 RED，停止並回報。
  - UOW-TX-023、025（預期通過，行為保護，見 design 決策 4）；UOW-TX-027、028 為既有測試，維持通過。若 023 或 025 在修正前就失敗，代表 design 推論不符，停止並回報。
  - 2.1 的四個測試與既有 29 個測試維持通過。
  - 防禦分支對照（hardener 檢查清單）：
    - 狀態機：已有呼叫端連線時重複開啟仍拋出（既有 `OpenConnectionAsync_WhenAlreadyOpen_ThrowsInvalidOperationException`）；有進行中交易時開啟仍拋出（既有 `OpenConnectionAsync_WhenTransactionInProgress_ThrowsInvalidOperationException`）；呼叫端連線重複釋放不影響記錄（既有 `DisposeAsync_CalledTwice_DoesNotCloseConnectionOpenedLater`，回呼位於防重入之後）；呼叫端連線釋放時關閉失敗後記錄仍重設（決策 1，UOW-TX-026）。
    - 其餘項目同 2.1。
  - 完成紀錄（此時 `git diff --stat src` 僅 `.../Persistence/UnitOfWork.cs | 42 +++++++++++++++++++++-`，即 2.2 的變更）：
    - `UOW 測試` 摘要行：`Failed!  - Failed:     3, Passed:    35, Skipped:     0, Total:    38, Duration: 582 ms - ProjectC.Infrastructure.Tests.dll (net10.0)`
    - 失敗測試原文（堆疊節錄 EF／ProjectC 框架行）：
      ```
      Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.CommitAsync_OnLeftoverOpenConnection_WhenCloseFailsAgain_DoesNotThrowAndPersistsChanges [23 ms]
      Error Message:
       Did not expect any exception because 資料已落地，關閉連線失敗不得讓呼叫端誤判提交失敗, but found System.InvalidOperationException: close failed
       at ProjectC.Infrastructure.Tests.TestSupport.SwitchableCloseInterceptor.ConnectionClosingAsync(...) in /src/tests/ProjectC.Infrastructure.Tests/TestSupport/SwitchableCloseInterceptor.cs:line 16
       at Microsoft.EntityFrameworkCore.Diagnostics.Internal.RelationalConnectionDiagnosticsLogger.ConnectionClosingAsync(IRelationalConnection connection, DateTimeOffset startTime)
       at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.CloseAsync()
       at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.CloseAsync()
       at Microsoft.EntityFrameworkCore.Storage.RelationalTransaction.ClearTransactionAsync(CancellationToken cancellationToken)
       at Microsoft.EntityFrameworkCore.Storage.RelationalTransaction.CommitAsync(CancellationToken cancellationToken)
       at ProjectC.Infrastructure.Persistence.UnitOfWorkTransaction.CommitAsync(CancellationToken cancellationToken) in /src/src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs:line 144
      Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.OpenConnectionAsync_OnLeftoverOpenConnection_TakesOverAndClosesOnDispose [2 ms]
      Error Message:
       System.InvalidOperationException : A connection is already open on this unit of work.
         at ProjectC.Infrastructure.Persistence.UnitOfWork.OpenConnectionAsync(CancellationToken cancellationToken) in /src/src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs:line 82
      Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.BeginTransactionAsync_AfterCallerConnectionCloseFailed_OwnsConnection [6 ms]
      Error Message:
       Did not expect any exception, but found System.InvalidOperationException: close failed
       at ProjectC.Infrastructure.Tests.TestSupport.ThrowingCloseInterceptor.ConnectionClosingAsync(...) in /src/tests/ProjectC.Infrastructure.Tests/TestSupport/ThrowingCloseInterceptor.cs:line 11
       at Microsoft.EntityFrameworkCore.Diagnostics.Internal.RelationalConnectionDiagnosticsLogger.ConnectionClosingAsync(IRelationalConnection connection, DateTimeOffset startTime)
       at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.CloseAsync()
       at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.CloseAsync()
       at Microsoft.EntityFrameworkCore.Storage.RelationalTransaction.ClearTransactionAsync(CancellationToken cancellationToken)
       at Microsoft.EntityFrameworkCore.Storage.RelationalTransaction.CommitAsync(CancellationToken cancellationToken)
       at ProjectC.Infrastructure.Persistence.UnitOfWorkTransaction.CommitAsync(CancellationToken cancellationToken) in /src/src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs:line 144
      ```
    - 失敗原因：022 的前提斷言（第一次提交後 `Open`）已通過，失敗於第二次 `CommitAsync`，堆疊經 `RelationalTransaction.CommitAsync` → `ClearTransactionAsync` → `RelationalConnection.CloseAsync`，即判為非自有、由 EF 在提交時關閉；024 失敗於 `OpenConnectionAsync` 的狀態判斷；026 的 `OpenConnectionAsync`、連線 `DisposeAsync` 已通過，失敗於 `CommitAsync`，堆疊同 022。皆為有效 RED。
    - 通過的行為保護：UOW-TX-023、025、027、028 與 2.1 的四個、既有 29 個（35 passed = 29 + 4 + 023 + 025）。
- [x] 3.2 最小實作（GREEN）：依 design 決策 1、2 修改 `UnitOfWork.cs`：`UnitOfWork` 新增 `_hasCallerConnection`；`OpenConnectionAsync` 改以該欄位判斷是否重複開啟，成功後設為 true；`UnitOfWorkConnection` 接收回呼，於 `DisposeAsync` 防重入之後、關閉之前呼叫；`BeginTransactionAsync` 的 `ownsConnection` 改為 `!_hasCallerConnection`，為 true 時一律 `Database.OpenConnectionAsync`。更新 `BeginTransactionAsync` 內引用設計決策的註解，改指向本 change；`IUnitOfWork.cs` 中 `OpenConnectionAsync` 的 XML doc 由「連線已開啟…時 MUST 拋出」改為「已有呼叫端連線或進行中交易時拋出」；`IUnitOfWorkConnection` 的 XML doc 補上「關閉失敗時工作單元的呼叫端連線記錄仍會重設，之後的交易由工作單元負責連線」與「交易提交或回滾後可在交易物件釋放前先釋放；交易未完成時須先釋放交易」（明文化既有用法：`OrderService.cs:447` 提交後即釋放連線，既有 `DisposeAsync_AfterCommitBeforeTransactionDisposed_ClosesConnectionAndTransactionDisposeDoesNotThrow` 保護，須維持通過，design 決策 1）。執行 `UOW 測試`，全數通過。
  - 變異檢查（證明行為保護測試能失敗，檢查後還原並以 `git diff` 確認）：(a) 暫時移除 `UnitOfWorkConnection.DisposeAsync` 對回呼的呼叫，預期 UOW-TX-025、026 失敗（`CommitAsync` 拋出 `close failed`）；(b) 暫時移除開始失敗處理中的 `ownsConnection` 條件（呼叫端連線也重複關閉），預期 UOW-TX-021 失敗（`State` 為 `Closed`）。任一變異未使預期測試失敗時，停止並回報。
  - 完成紀錄：
    - `UOW 測試` 摘要行：`Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 585 ms - ProjectC.Infrastructure.Tests.dll (net10.0)`
    - 變異 (a)（`_onDisposing();` 註解掉）：
      ```
      Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.BeginTransactionAsync_AfterCallerConnectionDisposed_OwnsConnection [52 ms]
      Error Message:
       Did not expect any exception, but found System.InvalidOperationException: close failed
      Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.DisposeAsync_CalledTwice_DoesNotCloseConnectionOpenedLater [1 ms]
      Error Message:
       System.InvalidOperationException : A connection is already open on this unit of work.
      Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.BeginTransactionAsync_AfterCallerConnectionCloseFailed_OwnsConnection [5 ms]
      Error Message:
       Did not expect any exception, but found System.InvalidOperationException: close failed
      Failed!  - Failed:     3, Passed:    35, Skipped:     0, Total:    38, Duration: 609 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
      預期的 025、026 皆失敗；既有 `DisposeAsync_CalledTwice_DoesNotCloseConnectionOpenedLater` 亦失敗（記錄未重設，第二次開啟被拒）。
    - 變異 (b)（catch 的 `when (ownsConnection)` 移除）：
      ```
      Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.BeginTransactionAsync_WhenStartFailsOnCallerOpenedConnection_KeepsCallerConnectionOpen [28 ms]
      Error Message:
       Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Open {value: 1}, but found ConnectionState.Closed {value: 0}.
      Failed!  - Failed:     1, Passed:    37, Skipped:     0, Total:    38, Duration: 660 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
    - 每次變異後以備份還原，`grep -c MUTATION` 為 `0`，`git diff --stat src`：
      ```
       .../Common/Interfaces/IUnitOfWork.cs               |  7 ++-
       .../Persistence/UnitOfWork.cs                      | 63 +++++++++++++++++++---
       2 files changed, 61 insertions(+), 9 deletions(-)
      ```
    - 重構：N/A。
- [x] 3.3 還原驗證：`git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs`（還原至修正前，2.2 的變更也一併還原）→ `git stash list` → 執行 `UOW 測試`，預期 UOW-TX-018、019、020、022、024、026 依 2.1／3.1 的原因失敗，其餘通過 → `git stash list` 確認後 `git stash pop`。pop 失敗或有衝突時立即停止並回報。
  - 完成紀錄：
      ```
      $ git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs
      Saved working directory and index state WIP on feature/unit-of-work-connection-ownership: ae68925 新增 unit-of-work-connection-ownership 提案
      $ git stash list
      stash@{0}: WIP on feature/unit-of-work-connection-ownership: ae68925 新增 unit-of-work-connection-ownership 提案
      $ UOW 測試
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.BeginTransactionAsync_WhenStartFails_ClosesConnectionAndRethrows [26 ms]
        Error Message:
         Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Closed {value: 0} because 開始失敗後工作單元開啟的連線須立即歸還, but found ConnectionState.Open {value: 1}.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.BeginTransactionAsync_WhenStartAndConnectionCloseFail_PreservesStartExceptionAndLogsWarning [6 ms]
        Error Message:
         Expected logger.Entries to contain a single item matching (Convert(e.Level, Int32) == 3), but the collection is empty.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.BeginTransactionAsync_WhenCancelledDuringStart_ClosesConnection [5 ms]
        Error Message:
         Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Closed {value: 0} because 取消後工作單元開啟的連線須立即歸還, but found ConnectionState.Open {value: 1}.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.CommitAsync_OnLeftoverOpenConnection_WhenCloseFailsAgain_DoesNotThrowAndPersistsChanges [41 ms]
        Error Message:
         Did not expect any exception because 資料已落地，關閉連線失敗不得讓呼叫端誤判提交失敗, but found System.InvalidOperationException: close failed
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.OpenConnectionAsync_OnLeftoverOpenConnection_TakesOverAndClosesOnDispose [2 ms]
        Error Message:
         System.InvalidOperationException : A connection is already open on this unit of work.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.BeginTransactionAsync_AfterCallerConnectionCloseFailed_OwnsConnection [5 ms]
        Error Message:
         Did not expect any exception, but found System.InvalidOperationException: close failed
      Failed!  - Failed:     6, Passed:    32, Skipped:     0, Total:    38, Duration: 638 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      $ git stash list
      stash@{0}: WIP on feature/unit-of-work-connection-ownership: ae68925 新增 unit-of-work-connection-ownership 提案
      $ git stash pop
      ...
      Dropped refs/stash@{0} (4416da74e84c3fcb4269f536ef5793c3f2250380)
      ```
    - 結論：UOW-TX-018、019、020、022、024、026 依 2.1／3.1 的原因失敗，其餘 32 個通過；pop 無衝突。

## 4. 最終檢查與文件

- [x] 4.1 hardener 最終檢查：程式碼全部完成後，依 `.claude/skills/hardener/SKILL.md` 檢查 `UnitOfWork.BeginTransactionAsync`、`UnitOfWork.OpenConnectionAsync`、`UnitOfWorkConnection.DisposeAsync`。發現缺少的防禦分支時，新增 task 依 TDD 補上；沒有缺漏時記錄逐項結論。「3 次後仍開啟」的 Warning 列為已知無測試的防禦分支（design 決策 3），不視為缺漏。（第 2 輪對抗式審查後更正：此分支可經 Non-Goal 的計數殘留觸發，見 design 決策 3。）
  - 完成紀錄（依 SKILL.md「適用範圍」的 Infrastructure 資源元件規則，只看三件事）：
    - 失敗後資源仍釋放：`BeginTransactionAsync` 自有連線開始失敗時在 catch 內關閉（UOW-TX-018、019）；`UnitOfWorkConnection.DisposeAsync` 關閉失敗時記錄仍在關閉前重設（UOW-TX-026）。`OpenConnectionAsync` 的 `Database.OpenConnectionAsync` 失敗時 `_hasCallerConnection` 尚未設為 true（設定在 await 之後），沒有需釋放的資源。
    - 不蓋掉原例外：開始失敗以 `throw;` 重拋同一實例（UOW-TX-018 `BeSameAs`）；關閉失敗只記 Warning、固定訊息帶例外（UOW-TX-020）；catch 以 `when (ownsConnection)` 篩選，呼叫端連線不進入處理（UOW-TX-021）。
    - 失敗注入點 × 連線開啟者：
      | 失敗點 | 工作單元開啟 | 呼叫端開啟 | 遺留開啟連線 |
      | --- | --- | --- | --- |
      | `Database.OpenConnectionAsync`（開始交易前） | 不處理，依 EF 開啟失敗不加計數（design 決策 3 推論，無測試） | 不適用（不呼叫） | 只加計數，不做實體開啟 |
      | 開始交易 | UOW-TX-018、019 | UOW-TX-021 | 探測 1.1 (d)（2 次關閉），無獨立 UOW 測試 |
      | 開始失敗後關閉 | UOW-TX-020 | 不適用 | 同左欄規則 |
      | 提交後關閉 | 既有 UOW-TX-009 等 | 既有 | UOW-TX-022、023 |
      | 呼叫端連線釋放時關閉 | 不適用 | 既有 `DisposeAsync_WhenCloseFails_LogsWarningAndDoesNotThrow`、UOW-TX-026 | UOW-TX-024 |
    - CancellationToken：catch 內關閉不傳 token（design 決策 3），UOW-TX-019 覆蓋。
    - 已知無測試的防禦分支：「3 次後仍開啟」的 Warning（design 決策 3），不視為缺漏。
    - 結論：無缺漏，不新增 task、不修改程式碼。
- [x] 4.2 全套回歸：`docker compose exec api dotnet test`，所有測試專案 `Failed: 0`。若有失敗，判斷是否與本 change 相關：相關則修正；無關的既有 flake，記錄測試名稱與訊息原文，並單獨重跑通過。
  - 完成紀錄（`docker compose exec api dotnet test`，無失敗、無重跑）：
      ```
      Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 144 ms - ProjectC.Domain.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   527, Skipped:     0, Total:   527, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 11 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   257, Skipped:     0, Total:   257, Duration: 47 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   541, Skipped:     0, Total:   541, Duration: 2 m 33 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
- [x] 4.3 文件同步（TDD N/A：純文件）：`docs/project-scope.md` §8 待辦 ⑦ 標記 ✅，記錄由本 change 處理、探測結論，以及殘留的 Non-Goal（呼叫端持有連線時的計數殘留）。以 `git diff docs/project-scope.md` 確認只改 ⑦。
  - 完成紀錄：`git diff --stat docs/project-scope.md` 為 `1 file changed, 1 insertion(+), 1 deletion(-)`（§8 待辦整段為單一行）；word-diff 只有兩處：⑦ 前加 `✅`，⑦ 結尾「不以狀態推斷」之後、⑧ 之前追加處理紀錄（本 change、探測結論、殘留 Non-Goal）。其他項目未變。

## 5. 交易未完成時釋放呼叫端連線記錄 Warning（審查後追加，design 決策 6）

- [x] 5.1 測試先行（RED）：在 `UnitOfWorkOpenConnectionTests` 新增 UOW-TX-029、030（見 AC 表）。修改 `UnitOfWork.cs` 前執行 `UOW 測試`。預期：029 因沒有 Warning 失敗（`ContainSingle` 找不到項目）；030 通過（行為保護，防止 Warning 條件過寬）；其餘 38 個通過。
  - RED 紀錄（`UOW 測試`，修改 `UnitOfWork.cs` 前）：029 因沒有 Warning 失敗，030 與其餘 38 個通過，符合預期。
      ```
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.DisposeAsync_WhileTransactionInProgress_LogsWarningAndCommitStillPersists [29 ms]
        Error Message:
         Expected logger.Entries to contain a single item matching (Convert(e.Level, Int32) == 3), but the collection is empty.
      Failed!  - Failed:     1, Passed:    39, Skipped:     0, Total:    40, Duration: 718 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
- [x] 5.2 最小實作（GREEN）：`UnitOfWorkConnection.DisposeAsync` 在防重入、`_onDisposing()` 之後、關閉之前，`_dbContext.Database.CurrentTransaction` 不為 null 時記 Warning（不帶例外），之後照常關閉。註解說明為何只記錄不拋出（design 決策 6）。執行 `UOW 測試`，全數通過。另做變異檢查：條件改為恆真，確認 030 失敗後還原。
  - GREEN 紀錄（`UOW 測試`）：
      ```
      Passed!  - Failed:     0, Passed:    40, Skipped:     0, Total:    40, Duration: 800 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
  - 變異檢查（只把 `UnitOfWorkConnection.DisposeAsync` 的條件改為 `if (true)`，跑完以 `git diff` 確認已還原）：030 失敗；025、026 也因多一筆 Warning 失敗。
      ```
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.BeginTransactionAsync_AfterCallerConnectionDisposed_OwnsConnection [29 ms]
        Error Message:
         Expected logger.Entries to contain a single item matching (Convert(e.Level, Int32) == 3), but 2 such items were found.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.BeginTransactionAsync_AfterCallerConnectionCloseFailed_OwnsConnection [60 ms]
        Error Message:
         Expected logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Exception) to be equal to {System.InvalidOperationException: close failed
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.DisposeAsync_AfterCommit_DoesNotLogWarning [16 ms]
        Error Message:
         Expected logger.Entries {
      Failed!  - Failed:     3, Passed:    37, Skipped:     0, Total:    40, Duration: 744 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
    （第一次做變異時 sed 同時改到 `BeginTransactionAsync`、`OpenConnectionAsync` 開頭相同字串的兩行，那次結果不採用，已全部還原；`git diff` 確認這兩行沒有變更後，才以指定行號重做上面這次。）
- [x] 5.3 還原驗證：`git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs`（整個檔案還原，018～026 的既有失敗也會一併出現）→ `git stash list` → 執行 `UOW 測試`，預期 029 依 5.1 的原因失敗 → `git stash list` 確認後 `git stash pop`。pop 失敗或有衝突時立即停止並回報。
  - 還原驗證紀錄：
      ```
      $ git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs
      Saved working directory and index state WIP on feature/unit-of-work-connection-ownership: ae68925 新增 unit-of-work-connection-ownership 提案
      $ git stash list
      stash@{0}: WIP on feature/unit-of-work-connection-ownership: ae68925 新增 unit-of-work-connection-ownership 提案
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.BeginTransactionAsync_WhenStartFails_ClosesConnectionAndRethrows [29 ms]
        Error Message:
         Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Closed {value: 0} because 開始失敗後工作單元開啟的連線須立即歸還, but found ConnectionState.Open {value: 1}.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.BeginTransactionAsync_WhenStartAndConnectionCloseFail_PreservesStartExceptionAndLogsWarning [8 ms]
        Error Message:
         Expected logger.Entries to contain a single item matching (Convert(e.Level, Int32) == 3), but the collection is empty.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.BeginTransactionAsync_WhenCancelledDuringStart_ClosesConnection [5 ms]
        Error Message:
         Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Closed {value: 0} because 取消後工作單元開啟的連線須立即歸還, but found ConnectionState.Open {value: 1}.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.CommitAsync_OnLeftoverOpenConnection_WhenCloseFailsAgain_DoesNotThrowAndPersistsChanges [28 ms]
        Error Message:
         Did not expect any exception because 資料已落地，關閉連線失敗不得讓呼叫端誤判提交失敗, but found System.InvalidOperationException: close failed
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.DisposeAsync_WhileTransactionInProgress_LogsWarningAndCommitStillPersists [6 ms]
        Error Message:
         Expected logger.Entries to contain a single item matching (Convert(e.Level, Int32) == 3), but the collection is empty.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.OpenConnectionAsync_OnLeftoverOpenConnection_TakesOverAndClosesOnDispose [3 ms]
        Error Message:
         System.InvalidOperationException : A connection is already open on this unit of work.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.BeginTransactionAsync_AfterCallerConnectionCloseFailed_OwnsConnection [9 ms]
        Error Message:
         Did not expect any exception, but found System.InvalidOperationException: close failed
      Failed!  - Failed:     7, Passed:    33, Skipped:     0, Total:    40, Duration: 728 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      $ git stash list
      stash@{0}: WIP on feature/unit-of-work-connection-ownership: ae68925 新增 unit-of-work-connection-ownership 提案
      $ git stash pop
      Dropped refs/stash@{0} (56cd8fda51e5e6bd0b7982539bdcda8145f01680)
      ```
    - 結論：029 依 5.1 的原因失敗；018、019、020、022、024、026 依 2.1／3.1 的原因失敗（整個檔案一起還原）；030 與其餘測試通過；pop 無衝突。
- [x] 5.4 全套回歸：`docker compose exec api dotnet test`，所有測試專案 `Failed: 0`。
  - 完成紀錄（`docker compose exec api dotnet test`）：
      ```
      Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 146 ms - ProjectC.Domain.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   527, Skipped:     0, Total:   527, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 11 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   259, Skipped:     0, Total:   259, Duration: 50 s - ProjectC.Infrastructure.Tests.dll (net10.0)
        Failed ProjectC.WebApi.Tests.Captcha.CaptchaComponentTests.Get_ReturnsTokenAndBase64PngWithoutPlainTextAnswer [50 ms]
      Failed!  - Failed:     1, Passed:   540, Skipped:     0, Total:   541, Duration: 2 m 46 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - 與本 change 無關：`GET /api/captcha` 只走 Redis 與圖片產生，不經過 `UnitOfWork`。**這次 grep 只抓了測試名稱，沒擷取到 Error Message 原文，失敗原因無法核對。** 之後單獨重跑：
      ```
      $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --no-build --filter "FullyQualifiedName~CaptchaComponentTests"
      Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 760 ms - ProjectC.WebApi.Tests.dll (net10.0)
      （同一測試單獨再跑 3 次）
      Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 645 ms - ProjectC.WebApi.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 644 ms - ProjectC.WebApi.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 640 ms - ProjectC.WebApi.Tests.dll (net10.0)
      $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --no-build
      Passed!  - Failed:     0, Passed:   541, Skipped:     0, Total:   541, Duration: 3 m - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - 交叉審查追溯（2026-10-20）：原始 Error Message 已無法取回——當次輸出只經 grep 篩出名稱、未存檔，對話紀錄中也只有測試名稱，`tests/ProjectC.WebApi.Tests/TestResults/` 內只有 2026-10-02 以前的舊檔。改以存檔方式重跑全套嘗試重現（`docker compose exec api dotnet test > /tmp/fullsuite_b001.txt 2>&1`，`awk` 擷取失敗區塊為空）：
      ```
      Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 117 ms - ProjectC.Domain.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   527, Skipped:     0, Total:   527, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 10 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: 47 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   541, Skipped:     0, Total:   541, Duration: 2 m 20 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
      未重現，根因仍未知；該次失敗不能歸因，只能記為未解的偶發失敗。之後全套測試一律先存檔再篩選，避免再遺失原文。

## 6. 加強 UOW-TX-024：接手遺留連線須維持單一連線（第 3 輪對抗式審查追加）

- [x] 6.1 測試加強（TDD：正式程式碼行為已正確，只加強斷言；以變異 (c) 取代 RED，證明加強前抓不到、加強後抓得到）：原 024 只斷言「可查詢」與「釋放後 Closed」。若 `OpenConnectionAsync` 遇到已開啟的連線只設 `_hasCallerConnection`、不經 EF 開啟，遺留連線的計數為 0，第一次查詢後就被 EF 關閉歸還，釋放後一樣是 `Closed`，原測試仍通過；正式環境則失去 phase2 單一連線保證，且之後的交易被判為非自有，重現 §8 ⑥。spec UOW-TX-024 的 THEN 補上「查詢結束後、釋放前維持開啟，之後的查詢在同一資料庫連線」；測試改為兩次 `pg_backend_pid()` 相同、中間 `State` 為 `Open`。
  - 變異 (c)：`UnitOfWork.OpenConnectionAsync` 的 `await _dbContext.Database.OpenConnectionAsync(cancellationToken);` 改為 `if (_dbContext.Database.GetDbConnection().State != ConnectionState.Open) await _dbContext.Database.OpenConnectionAsync(cancellationToken);`，執行 `UOW 測試`：
    - 加強前的 024 斷言（以 Edit 暫時改回原斷言）：
      ```
      Passed!  - Failed:     0, Passed:    40, Skipped:     0, Total:    40, Duration: 555 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
    - 加強後的 024 斷言：
      ```
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.OpenConnectionAsync_OnLeftoverOpenConnection_TakesOverAndClosesOnDispose [24 ms]
        Error Message:
         Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Open {value: 1} because 接手後查詢結束不得歸還連線, but found ConnectionState.Closed {value: 0}.
      Failed!  - Failed:     1, Passed:    39, Skipped:     0, Total:    40, Duration: 579 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
  - 還原：正式程式碼還原後以 `git diff` 確認不含變異字串（計數 0），測試檔還原為加強版。GREEN（`UOW 測試`）：
      ```
      Passed!  - Failed:     0, Passed:    40, Skipped:     0, Total:    40, Duration: 566 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
  - 過程紀錄：第一次套用變異時行號錯誤，變異沒有生效，那次結果不採用；變異中途誤對測試檔做了一次 `git stash push`／`pop`，pop 後 `git stash list` 為空、測試檔內容確認仍是加強版，沒有影響。
- [x] 6.2 全套回歸：`docker compose exec api dotnet test`，所有測試專案 `Failed: 0`；失敗時先完整保存 Error Message 再判斷。
  - 完成紀錄（`docker compose exec api dotnet test`，無失敗、無重跑）：
      ```
      Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 115 ms - ProjectC.Domain.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   527, Skipped:     0, Total:   527, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 9 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   259, Skipped:     0, Total:   259, Duration: 50 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   541, Skipped:     0, Total:   541, Duration: 2 m 30 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```

## 7. 第 4 輪對抗式審查追加

- [x] 7.1 加強 UOW-TX-029（TDD：正式程式碼行為已正確，只加強斷言；以變異取代 RED）：原 029 沒斷言提交後連線關閉，「偵測到交易進行中只記 Warning 就 `return`、不關閉」的錯誤實作會通過（審查者以變異證實 40/40 通過）。spec 029 THEN 補「提交後該資料庫連線 MUST 已關閉」，測試在 `CommitAsync` 後斷言 `State == Closed`。
  - 變異：`UnitOfWorkConnection.DisposeAsync` 的 Warning 改為 `{ _logger.LogWarning(...); return; }`（以 `cp` 備份還原、`cmp` 確認一致），執行 `UOW 測試`（此時 7.2 的 031 已加入、尚未實作，故 031 一併失敗）：
      ```
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.DisposeAsync_WhileTransactionInProgress_LogsWarningAndCommitStillPersists [23 ms]
        Error Message:
         Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Closed {value: 0}, but found ConnectionState.Open {value: 1}.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.OpenConnectionAsync_OnLeftoverOpenConnection_WhenCancelled_ThrowsAndDoesNotTakeOver [6 ms]
        Error Message:
         Expected a <System.OperationCanceledException> to be thrown, but no exception was thrown.
      Failed!  - Failed:     2, Passed:    39, Skipped:     0, Total:    41, Duration: 592 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      RESTORED
      ```
- [x] 7.2 測試先行（RED）：新增 UOW-TX-031（見 AC 表）。本 change 讓 `OpenConnectionAsync` 遇到遺留連線時改為接手，EF 對已開啟連線只加計數、不檢查 token，已取消的 token 會靜默成功（修正前拋 `InvalidOperationException`）。修改 `UnitOfWork.cs` 前執行 `UOW 測試`（`git diff --stat src/` 與 6.2 時相同，只有本 change 既有變更）：
      ```
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.OpenConnectionAsync_OnLeftoverOpenConnection_WhenCancelled_ThrowsAndDoesNotTakeOver [22 ms]
        Error Message:
         Expected a <System.OperationCanceledException> to be thrown, but no exception was thrown.
      Failed!  - Failed:     1, Passed:    40, Skipped:     0, Total:    41, Duration: 604 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
- [x] 7.3 最小實作（GREEN）：`OpenConnectionAsync` 在呼叫 EF 前 `cancellationToken.ThrowIfCancellationRequested()`（design 決策 2）；同時把 `UnitOfWorkConnection.DisposeAsync` 註解的「照常關閉」改為與 doc/spec 一致的「釋放呼叫端的持有，實體連線於交易結束時關閉」（第 4 輪 strict-reviewer 建議）。重構 N/A。
  - GREEN（`UOW 測試`）：
      ```
      Passed!  - Failed:     0, Passed:    41, Skipped:     0, Total:    41, Duration: 596 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
  - 還原驗證：首次執行（stash `fa8976d2`）只擷取了 031 的訊息，其餘 7 個失敗以「同 5.3」帶過、沒有原文，不能當證據。交叉審查（B-001）指出後，以相同條件重新執行並擷取完整輸出（`awk` 擷取每個失敗測試從名稱到 `Stack Trace:` 之前的區塊，多行 Error Message 原樣保留）：
      ```
      $ git stash list
      $ git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs
      Saved working directory and index state WIP on feature/unit-of-work-connection-ownership: ae68925 新增 unit-of-work-connection-ownership 提案
      $ git stash list
      stash@{0}: WIP on feature/unit-of-work-connection-ownership: ae68925 新增 unit-of-work-connection-ownership 提案
      $ docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~UnitOfWork"
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.BeginTransactionAsync_WhenStartFails_ClosesConnectionAndRethrows [27 ms]
        Error Message:
         Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Closed {value: 0} because 開始失敗後工作單元開啟的連線須立即歸還, but found ConnectionState.Open {value: 1}.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.BeginTransactionAsync_WhenStartAndConnectionCloseFail_PreservesStartExceptionAndLogsWarning [5 ms]
        Error Message:
         Expected logger.Entries to contain a single item matching (Convert(e.Level, Int32) == 3), but the collection is empty.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.BeginTransactionAsync_WhenCancelledDuringStart_ClosesConnection [4 ms]
        Error Message:
         Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Closed {value: 0} because 取消後工作單元開啟的連線須立即歸還, but found ConnectionState.Open {value: 1}.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.CommitAsync_OnLeftoverOpenConnection_WhenCloseFailsAgain_DoesNotThrowAndPersistsChanges [37 ms]
        Error Message:
         Did not expect any exception because 資料已落地，關閉連線失敗不得讓呼叫端誤判提交失敗, but found System.InvalidOperationException: close failed
         at ProjectC.Infrastructure.Tests.TestSupport.SwitchableCloseInterceptor.ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) in /src/tests/ProjectC.Infrastructure.Tests/TestSupport/SwitchableCloseInterceptor.cs:line 16
         at Microsoft.EntityFrameworkCore.Diagnostics.Internal.RelationalConnectionDiagnosticsLogger.ConnectionClosingAsync(IRelationalConnection connection, DateTimeOffset startTime)
         at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.CloseAsync()
         at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.CloseAsync()
         at Microsoft.EntityFrameworkCore.Storage.RelationalTransaction.ClearTransactionAsync(CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Storage.RelationalTransaction.CommitAsync(CancellationToken cancellationToken)
         at ProjectC.Infrastructure.Persistence.UnitOfWorkTransaction.CommitAsync(CancellationToken cancellationToken) in /src/src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs:line 104
         at FluentAssertions.Specialized.NonGenericAsyncFunctionAssertions.NotThrowAsync(String because, Object[] becauseArgs).
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.DisposeAsync_WhileTransactionInProgress_LogsWarningAndCommitStillPersists [9 ms]
        Error Message:
         Expected logger.Entries to contain a single item matching (Convert(e.Level, Int32) == 3), but the collection is empty.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.OpenConnectionAsync_OnLeftoverOpenConnection_TakesOverAndClosesOnDispose [2 ms]
        Error Message:
         System.InvalidOperationException : A connection is already open on this unit of work.
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.BeginTransactionAsync_AfterCallerConnectionCloseFailed_OwnsConnection [8 ms]
        Error Message:
         Did not expect any exception, but found System.InvalidOperationException: close failed
         at ProjectC.Infrastructure.Tests.TestSupport.ThrowingCloseInterceptor.ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) in /src/tests/ProjectC.Infrastructure.Tests/TestSupport/ThrowingCloseInterceptor.cs:line 11
         at Microsoft.EntityFrameworkCore.Diagnostics.Internal.RelationalConnectionDiagnosticsLogger.ConnectionClosingAsync(IRelationalConnection connection, DateTimeOffset startTime)
         at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.CloseAsync()
         at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.CloseAsync()
         at Microsoft.EntityFrameworkCore.Storage.RelationalTransaction.ClearTransactionAsync(CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Storage.RelationalTransaction.CommitAsync(CancellationToken cancellationToken)
         at ProjectC.Infrastructure.Persistence.UnitOfWorkTransaction.CommitAsync(CancellationToken cancellationToken) in /src/src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs:line 104
         at FluentAssertions.Specialized.NonGenericAsyncFunctionAssertions.NotThrowAsync(String because, Object[] becauseArgs).
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.OpenConnectionAsync_OnLeftoverOpenConnection_WhenCancelled_ThrowsAndDoesNotTakeOver [7 ms]
        Error Message:
         Expected a <System.OperationCanceledException> to be thrown, but found <System.InvalidOperationException>:
      System.InvalidOperationException: A connection is already open on this unit of work.
         at ProjectC.Infrastructure.Persistence.UnitOfWork.OpenConnectionAsync(CancellationToken cancellationToken) in /src/src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs:line 42
         at FluentAssertions.Specialized.AsyncFunctionAssertions`2.InvokeWithInterceptionAsync(Func`1 action).
      Failed!  - Failed:     8, Passed:    33, Skipped:     0, Total:    41, Duration: 674 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      $ git stash list
      stash@{0}: WIP on feature/unit-of-work-connection-ownership: ae68925 新增 unit-of-work-connection-ownership 提案
      $ git stash pop
      Dropped refs/stash@{0} (d54100a50a969074533bdfdef3ef2306d8bace5a)
      $ git stash list
      $ cmp src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs /tmp/UnitOfWork.cs.bak && echo RESTORED
      RESTORED
      ```
    失敗原因：018、019 為連線未關閉（`Open`）；020、029 為未記 Warning；022、026 為關閉例外從 `CommitAsync` 外傳；024、031 為修正前的「A connection is already open」。
    整檔還原時 031 因修正前的「A connection is already open」拋出 `InvalidOperationException` 而失敗，不是目標原因；因此另做只刪除 `ThrowIfCancellationRequested` 那一行的變異（`cp` 備份還原、`cmp` 確認一致），證明失敗原因為目標行為：
      ```
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkOpenConnectionTests.OpenConnectionAsync_OnLeftoverOpenConnection_WhenCancelled_ThrowsAndDoesNotTakeOver [22 ms]
        Error Message:
         Expected a <System.OperationCanceledException> to be thrown, but no exception was thrown.
      Failed!  - Failed:     1, Passed:    40, Skipped:     0, Total:    41, Duration: 592 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      RESTORED
      ```
- [x] 7.4 全套回歸（`docker compose exec api dotnet test`，無失敗、無重跑）：
      ```
      Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 113 ms - ProjectC.Domain.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   527, Skipped:     0, Total:   527, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 8 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: 49 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   541, Skipped:     0, Total:   541, Duration: 2 m 36 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```

## Workflow follow-up

- strict-reviewer 與對抗式審查迴圈通過、使用者同意後，執行 `/opsx:archive`，同步主 spec。
- 歸檔同步後，依 design 決策 5 手動更新 `openspec/specs/unit-of-work-transaction/spec.md` 的 Purpose，並以 `openspec validate --specs` 確認。
