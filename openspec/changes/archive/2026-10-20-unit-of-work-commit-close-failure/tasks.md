# Tasks

> 所有指令都在容器內執行（`docker compose exec api dotnet test ...`），不在本機執行 dotnet。整合測試用 Testcontainers，不連開發用的 db 服務。
>
> `UnitOfWorkTests` 指 `tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs`；`ThrowingTransactionInterceptor` 為該檔內 private interceptor，`ThrowingCloseInterceptor` 在 `TestSupport`。
>
> 測試命令（下稱 `UOW 測試`）：`docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~ProjectC.Infrastructure.Tests.UnitOfWorkTests"`
>
> 證據一律貼原文（CLAUDE.md「證據貼原文」）：每個失敗測試的 Error Message 原文、stash push／list／pop 輸出、全套回歸各 DLL 摘要行；不得以「同 1.1」等摘要代替。

## AC 與測試對應

| AC | 測試檔／方法 | 任務 |
| --- | --- | --- |
| UOW-TX-002 | `UnitOfWorkTests`：既有 `DisposeAsync_WhenRollbackFails_LogsWarningAndDoesNotThrow`（維持通過） | 1.2、2.2 |
| UOW-TX-003 | `UnitOfWorkTests`：既有 `DisposeAsync_WhenRollbackFails_ReleasesTransactionAndDiscardsWrites`（已斷言釋放後連線 `Closed`；改由工作單元關閉後維持通過） | 1.2、2.2 |
| UOW-TX-004 | `UnitOfWorkTests`：既有 `DisposeAsync_WhenRollbackFailsDuringExceptionUnwind_PreservesOriginalException`（維持通過） | 1.2、2.2 |
| UOW-TX-005 | `UnitOfWorkTests`：既有 `DisposeAsync_WhenRollbackAndTransactionDisposeFail_LogsBothWarningsAndDoesNotThrow`（斷言不變：兩筆 Warning 依序為回滾例外、關閉例外；維持通過） | 1.2、2.2 |
| UOW-TX-006 | `UnitOfWorkTests`：既有 `DisposeAsync_AfterCommitFails_PreservesCommitExceptionAndReleasesTransaction`（interceptor 在 EF 實際提交前拋例外，即可確定未提交的情境；維持通過） | 1.2、2.2 |
| UOW-TX-014 | `UnitOfWorkTests`：新增 `CommitAsync_WhenSaveChangesFails_PreservesExceptionAndDiscardsWrites`（無注入；交易內先寫入 venue A 並 `SaveChangesAsync`，再 `Add` 與 A 同 Id 的 venue，`CommitAsync` 內 `SaveChangesAsync` 因主鍵重複拋 `DbUpdateException`；斷言呼叫端收到該例外、`CurrentTransaction` 為 null、`State` 為 `Closed`、另開 `DbContext` 讀不到 A） | 1.1、1.2 |
| UOW-TX-007 | `UnitOfWorkTests`：既有 `DisposeAsync_WhenRollbackFailsOnCallerOpenedConnection_ReleasesTransactionAndKeepsConnectionOpen`（維持通過） | 1.2、2.2 |
| UOW-TX-008 | `UnitOfWorkTests`：既有 `DisposeAsync_WhenConnectionCloseFailsDuringRollback_LogsWarningAndReleasesTransaction`（維持通過；Warning 改由連線關閉的 catch 記錄） | 1.2、2.2 |
| UOW-TX-013 | `UnitOfWorkTests`：`CommitAsync_OnOwnedConnection_ClosesConnectionBeforeDispose`（原 `DisposeAsync_AfterSuccessfulCommit_ClosesConnection`，3.1 改名並加斷言：提交後、釋放前 `State` 為 `Closed`；其餘斷言不變） | 1.1、1.2、3.1 |
| UOW-TX-015 | `UnitOfWorkTests`：新增 `RollbackAsync_OnOwnedConnection_ClosesConnectionBeforeDisposeAndDiscardsWrites`（無注入；寫入並 `SaveChangesAsync` 後明確回滾；斷言回滾後、釋放前 `Closed`、無 Warning、資料不存在） | 3.1 |
| UOW-TX-016 | `UnitOfWorkTests`：新增 `RollbackAsync_WhenConnectionCloseFailsAfterRollback_DoesNotThrowAndLogsWarning`（只掛 `ThrowingCloseInterceptor`；斷言 `RollbackAsync` 與 `DisposeAsync` 皆不拋、恰好一筆 Warning 為關閉例外、無進行中交易） | 3.1 |
| UOW-TX-017 | `UnitOfWorkTests`：新增 `DisposeAsync_CalledTwice_DoesNotCloseConnectionOpenedAfterwards`（開始交易、未完成即釋放、`OpenConnectionAsync`、再釋放交易一次；斷言連線仍 `Open`，釋放連線後 `Closed`） | 3.1 |
| UOW-TX-009 | `UnitOfWorkTests`：新增 `CommitAsync_WhenConnectionCloseFailsAfterCommit_DoesNotThrowAndPersistsChanges`（只掛 `ThrowingCloseInterceptor`；交易內 `Venues.Add` 後提交；斷言 `CommitAsync` 與 `DisposeAsync` 皆不拋、恰好一筆 Warning 且 `Exception` 為關閉例外、另開 `DbContext` 讀得到資料） | 1.1、1.2、1.3 |
| UOW-TX-010 | `UnitOfWorkTests`：新增 `CommitAsync_OnCallerOpenedConnection_KeepsConnectionOpenUntilCallerDisposes`（先 `OpenConnectionAsync` 再開始交易、寫入、提交；斷言提交與釋放交易後 `State` 為 `Open`、可再開始交易、釋放連線後 `Closed`、資料存在） | 1.1、1.2 |

UOW-TX-001 不在本 change 的 delta 內，既有測試由 2.2 全套回歸涵蓋。

## 1. 提交後關閉連線失敗不外傳

- [x] 1.1 測試先行（RED）：在 `UnitOfWorkTests` 新增 UOW-TX-009、010、013、014 四個測試（RED 執行時另含 011／012 與 `startFailure` 注入，1.2 依 design 決策 3 移出）；修改 `UnitOfWork.cs` 前執行 `UOW 測試`。預期：
  - UOW-TX-009（RED）：`CommitAsync` 拋出 `close failed`（Expected 不拋例外），堆疊經 `UnitOfWorkTransaction.CommitAsync`。若在其他呼叫處失敗（例如 `BeginTransactionAsync`、`SaveChangesAsync`），不算有效 RED，停止並回報。
  - UOW-TX-010（預期通過）：驗證 design 決策 1 依賴的 EF 開啟計數行為。若不通過，停止並回報，回到 design。
  - UOW-TX-013（預期通過）：修正前 EF 提交時即關閉連線，作為行為保護。
  - UOW-TX-014（預期通過）：修正前自動回滾即丟棄寫入並關閉連線，作為行為保護；修正後改由工作單元關閉連線。
  - UOW-TX-011／012（已移出，見 design 決策 3）：修正前照實記錄，不作 RED 證據；1.2 GREEN 後 011 仍因連線未關閉而失敗，觸發停止條件，經使用者決定移出本 change。
  - 既有 13 個測試維持通過。
  - 防禦分支對照（hardener 檢查清單）：參數驗證／Entity 存在性不適用（無業務參數、無 Entity）；狀態機：重複開始交易、已有呼叫端連線由既有測試與 UOW-TX-010 涵蓋；`CancellationToken`：`OpenConnectionAsync`／`BeginTransactionAsync` 接收並傳遞呼叫端 token；`BeginTransactionAsync` 被取消時已開啟的連線與開始交易失敗同屬 design 決策 3 的已知缺口，留到 `DbContext` 釋放；並發不適用（`DbContext` 非 thread-safe，同一工作單元不並發使用）；例外邊界即 UOW-TX-009；日誌遮蔽：Warning 只帶例外與固定訊息。
  - 完成紀錄（2026-10-20T02:38:21+08:00，修改 `UnitOfWork.cs` 前；`git status --short src` 輸出為空）：命令 `UOW 測試`。摘要行：
    ```
    Failed!  - Failed:     3, Passed:    16, Skipped:     0, Total:    19, Duration: 554 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
    ```
    - UOW-TX-009 `CommitAsync_WhenConnectionCloseFailsAfterCommit_DoesNotThrowAndPersistsChanges`（有效 RED，失敗於 `UnitOfWorkTransaction.CommitAsync`）：
      ```
      Did not expect any exception because 資料已落地，關閉連線失敗不得讓呼叫端誤判提交失敗, but found System.InvalidOperationException: close failed
         at ProjectC.Infrastructure.Tests.TestSupport.ThrowingCloseInterceptor.ConnectionClosingAsync(...) in /src/tests/ProjectC.Infrastructure.Tests/TestSupport/ThrowingCloseInterceptor.cs:line 11
         at Microsoft.EntityFrameworkCore.Diagnostics.Internal.RelationalConnectionDiagnosticsLogger.ConnectionClosingAsync(IRelationalConnection connection, DateTimeOffset startTime)
         at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.CloseAsync()
         at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.CloseAsync()
         at Microsoft.EntityFrameworkCore.Storage.RelationalTransaction.ClearTransactionAsync(CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Storage.RelationalTransaction.CommitAsync(CancellationToken cancellationToken)
         at ProjectC.Infrastructure.Persistence.UnitOfWorkTransaction.CommitAsync(CancellationToken cancellationToken) in /src/src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs:line 96
      ```
      失敗原因：EF 提交成功後於 `ClearTransactionAsync` 關閉交易自行開啟的連線，關閉例外從 `CommitAsync` 傳出（即目標 bug）。
    - UOW-TX-011 `BeginTransactionAsync_WhenStartFails_ClosesConnectionAndRethrows`（修正前失敗，不作 RED 證據）：
      ```
      Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Closed {value: 0} because 開始交易失敗不得遺留工作單元開啟的連線, but found ConnectionState.Open {value: 1}.
      ```
    - UOW-TX-012 `BeginTransactionAsync_WhenStartAndConnectionCloseFail_PreservesStartExceptionAndLogsWarning`（修正前失敗，不作 RED 證據）：
      ```
      Expected logger.Entries to contain a single item matching (Convert(e.Level, Int32) == 3), but the collection is empty.
      ```
    - UOW-TX-010、013、014 與既有 13 個測試通過（16 passed）；UOW-TX-010 通過，design 決策 1 依賴的開啟計數推論成立。
- [x] 1.2 最小實作（GREEN）：依 design 決策 1～2 修改 `UnitOfWork.cs`：`BeginTransactionAsync` 於連線 `Closed` 時先 `OpenConnectionAsync(cancellationToken)`；`UnitOfWorkTransaction` 新增 `ownsConnection`，`DisposeAsync` 的 finally 在釋放交易後以 try／catch 關閉自己開啟的連線（失敗記 Warning「Failed to close the unit of work transaction connection.」）；自動回滾 Warning 訊息移除「或其觸發的連線關閉」字樣（回滾不再觸發關閉）。執行 `UOW 測試` 全數通過。
  - 完成紀錄：
    - 第一次 GREEN（含決策 3 原規劃的開始失敗 catch），命令 `UOW 測試`，摘要行：
      ```
      Failed!  - Failed:     2, Passed:    17, Skipped:     0, Total:    19, Duration: 521 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
      - `BeginTransactionAsync_WhenStartFails_ClosesConnectionAndRethrows`：
        ```
        Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Closed {value: 0} because 開始交易失敗不得遺留工作單元開啟的連線, but found ConnectionState.Open {value: 1}.
        ```
      - `BeginTransactionAsync_WhenStartAndConnectionCloseFail_PreservesStartExceptionAndLogsWarning`：
        ```
        Expected logger.Entries to contain a single item matching (Convert(e.Level, Int32) == 3), but the collection is empty.
        ```
      觸發 1.1 停止條件，使用者 2026-10-20 決定移出 011／012、`startFailure` 注入與開始失敗 catch（design 決策 3）；原定暫時破壞 (a)(b) 隨之取消。
    - 移出後 GREEN，命令 `UOW 測試`，摘要行：
      ```
      Passed!  - Failed:     0, Passed:    17, Skipped:     0, Total:    17, Duration: 566 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
- [x] 1.3 還原驗證（bug fix 適用 CLAUDE.md「RED 還原驗證」）：`git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs` → `UOW 測試`，確認 UOW-TX-009 因目標行為不符而失敗（非編譯錯誤）→ `git stash list` 確認該 stash 存在 → `git stash pop`；pop 失敗或衝突立即停止並回報。pop 後重跑 `UOW 測試` 全數通過。
  - 完成紀錄（2026-10-20）：
    - `git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs`：
      ```
      Saved working directory and index state WIP on feature/unit-of-work-commit-close-failure: 4d975b7 開發流程規範補強：證據貼原文、hardener 改以判準界定並列為 task
      ```
    - `UOW 測試` 摘要行：
      ```
      Failed!  - Failed:     1, Passed:    16, Skipped:     0, Total:    17, Duration: 516 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
    - 失敗測試 `CommitAsync_WhenConnectionCloseFailsAfterCommit_DoesNotThrowAndPersistsChanges`：
      ```
      Did not expect any exception because 資料已落地，關閉連線失敗不得讓呼叫端誤判提交失敗, but found System.InvalidOperationException: close failed
         at ProjectC.Infrastructure.Tests.TestSupport.ThrowingCloseInterceptor.ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) in /src/tests/ProjectC.Infrastructure.Tests/TestSupport/ThrowingCloseInterceptor.cs:line 11
         at Microsoft.EntityFrameworkCore.Diagnostics.Internal.RelationalConnectionDiagnosticsLogger.ConnectionClosingAsync(IRelationalConnection connection, DateTimeOffset startTime)
         at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.CloseAsync()
         at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.CloseAsync()
         at Microsoft.EntityFrameworkCore.Storage.RelationalTransaction.ClearTransactionAsync(CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Storage.RelationalTransaction.CommitAsync(CancellationToken cancellationToken)
         at ProjectC.Infrastructure.Persistence.UnitOfWorkTransaction.CommitAsync(CancellationToken cancellationToken) in /src/src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs:line 96
      ```
      失敗原因：還原後 EF 提交成功即關閉交易自行開啟的連線，關閉例外從 `CommitAsync` 傳出（目標行為不符，非編譯錯誤）。
    - `git stash list`：
      ```
      stash@{0}: WIP on feature/unit-of-work-commit-close-failure: 4d975b7 開發流程規範補強：證據貼原文、hardener 改以判準界定並列為 task
      ```
    - `git stash pop`（exit 0）：
      ```
      modified:   src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs
      modified:   tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs
      Dropped refs/stash@{0} (9150e4f340496783fd4a6f36cd4e8c54f57ec35d)
      ```
    - pop 後 `UOW 測試` 摘要行：
      ```
      Passed!  - Failed:     0, Passed:    17, Skipped:     0, Total:    17, Duration: 529 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
- [x] 1.4 重構（若有）：重構後重跑 `UOW 測試` 並記錄結果；無需重構則記錄 N/A 與理由。
  - 完成紀錄：N/A。正式程式碼僅新增 `ownsConnection` 判斷與一段 try／catch 關閉；關閉的 try／catch 與 `UnitOfWorkConnection.DisposeAsync` 結構相同但訊息不同（日誌需可區分），只有兩處，不抽共用。
- [x] 1.5 文件：`docs/project-scope.md` §8 待辦（2026-10-07）⑥ 標記 ✅，註明由 `unit-of-work-commit-close-failure` 處理及作法（連線改由工作單元開啟與關閉）；並更正原文「修正需區分提交失敗與提交後關閉失敗，會改變呼叫端契約」的預估：介面簽章與呼叫端程式碼不變，但 `CommitAsync` 可觀察行為契約改變（提交成功後關閉連線失敗由拋例外改為正常返回）。另於 §8 新增待辦「開始交易失敗（含取消）時連線留到 `DbContext` 釋放」（既有行為，見本 change design 決策 3）。驗證：`grep -n "unit-of-work-commit-close-failure" docs/project-scope.md` 有結果。TDD N/A（純文件）。
  - 完成紀錄：§8 待辦 ⑥ 標記 ✅ 並更正契約預估，新增 ⑦；`grep -n "unit-of-work-commit-close-failure" docs/project-scope.md` 輸出 `196:      - **待辦（2026-10-07）**：① …`（⑥⑦ 位於同一行）。

## 2. 最終檢查

- [x] 2.1 hardener 最終檢查：程式碼全部完成後，依 `.claude/skills/hardener/SKILL.md`（含「Infrastructure 資源元件」）檢查 `UnitOfWork.cs` 變更。逐格記錄失敗注入矩陣（提交／回滾／開始交易／關閉連線／釋放交易 × 工作單元自行開啟／呼叫端先開）每格的對應測試或不適用理由（例如「釋放交易本身失敗」無注入點，見 design 決策 2；「開始交易失敗」為已知缺口，見 design 決策 3）。發現缺漏依 TDD 補 RED 並新增「hardener 補強」task。TDD N/A（檢查本身不新增行為）。
  - 完成紀錄：三項檢查——(1) 任一步驟失敗後資源仍於 `finally` 釋放：回滾失敗、釋放交易失敗後仍關閉自己開啟的連線；(2) 釋放路徑不蓋掉原例外：三個 catch 皆記 Warning 不重拋並註明理由；(3) 失敗注入矩陣：

    | 失敗點 | 工作單元自行開啟 | 呼叫端先開 |
    | --- | --- | --- |
    | 無失敗（成功提交） | UOW-TX-013 | UOW-TX-010 |
    | 提交前 `SaveChangesAsync` 失敗 | UOW-TX-014 | 不適用：`ownsConnection` 為 false，走修正前既有路徑，連線由呼叫端釋放（`UnitOfWorkOpenConnectionTests`） |
    | 提交本身失敗 | UOW-TX-006 | 同上 |
    | 提交成功後關閉失敗 | UOW-TX-009 | 不適用：提交不關閉呼叫端連線（UOW-TX-010） |
    | 自動回滾失敗 | UOW-TX-002、003、004 | UOW-TX-007 |
    | 回滾成功、釋放時關閉失敗 | UOW-TX-008 | 不適用：釋放交易不關閉呼叫端連線（UOW-TX-007、010） |
    | 回滾與關閉皆失敗 | UOW-TX-005 | 不適用：同上 |
    | 釋放交易本身失敗 | 無注入點，catch 為防禦實作（design 決策 2） | 同左 |
    | 開啟連線失敗 | 不適用：連線未開啟，無資源可釋放，例外原樣傳出 | 不適用：不會開啟 |
    | 開始交易失敗（含取消） | 已知缺口，§8 ⑦（design 決策 3） | 不適用：連線由呼叫端釋放 |

    建議性觀察（不修改）：`ownsConnection` 以 `State == Closed` 判斷，連線為 `Broken` 時視為非自行開啟，EF 交易會自行重開並在提交後關閉，回到修正前行為；`OpenConnectionAsync` 同樣以 `!= Closed` 判斷，屬既有一致寫法，`Broken` 罕見。結論：無缺漏需補，未修改程式碼。
- [x] 2.2 全套回歸：`docker compose exec api dotnet test ProjectC.slnx` 全數通過（輸出存檔後擷取各 DLL 摘要行原文；失敗時先判斷是否為既知 flaky，記錄測試名稱與重跑結果，不得略過）；`openspec validate unit-of-work-commit-close-failure --strict` 通過。TDD N/A（整合確認）。
  - 執行紀錄（2026-10-20；全套未全數通過，唯一失敗經下方時鐘探測判定為既有時鐘回跳 flake，使用者 2026-10-20 同意勾選並列 `docs/project-scope.md` §8 待辦 ⑧）：
    - 全套各 DLL 摘要行：
      ```
      Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 118 ms - ProjectC.Domain.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   527, Skipped:     0, Total:   527, Duration: 2 s - ProjectC.Application.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 9 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
      Failed!  - Failed:     1, Passed:   244, Skipped:     0, Total:   245, Duration: 49 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   541, Skipped:     0, Total:   541, Duration: 3 m 11 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - 失敗測試與訊息：
      ```
      Failed ProjectC.Infrastructure.Tests.Caching.QueryCacheTtlSafetyNetTests.GetEventsHandler_WhenInvalidationHappensWhileQueryInFlight_CacheTemporarilyResurrectsStaleValue_ButExpiresWithinTtl [8 s]
      Error Message:
       Expected (database.KeyExistsAsync(targetKey)) to be False because 陳舊快取應在 TTL 到期後自然過期。
      ws=0ms we=0.8ms rs=3.2ms re=3.5ms PTTL=4998ms bounds=[4995.4712, 4998.5957]ms c6s=4201.1ms c6e=4203.1ms c7s=5201.8ms T=5000ms G=2000ms M1=500ms B=300ms M2=200ms
      ```
      （QueryCacheTtlSafetyNetTests.cs:line 602，(7) 到期後過期檢查）
    - 判斷：key 在 Stopwatch 計 TTL + 200ms 時仍存在，PTTL 寫入當下在界內，事件紀錄無後續寫入；與該測試檔註解所述 WSL2 牆上時鐘回跳（Redis 以牆上時鐘計到期）一致，(7) 沒有時鐘回跳餘裕。受測流程中 UnitOfWork 只用於寫入方提交，快取失效在交易範圍內完成，本 change 不影響 Redis 寫入或 TTL。
    - 重跑（filter `QueryCacheTtlSafetyNetTests`，每輪 9 個測試），HEAD = `git stash push -- UnitOfWork.cs UnitOfWorkTests.cs` 後、每次 `git stash list` 確認後 `git stash pop`：
      - 改動：`Failed!  - Failed:     1, Passed:     8, ... Total:     9`（GetTicketTypesHandler_…_ButExpiresWithinTtl）
      - HEAD ×2：`Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 29 s`／`Duration: 28 s`
      - 改動 ×3：`Passed! ... Total:     9, Duration: 30 s`；`Failed!  - Failed:     1, Passed:     8, ... Duration: 29 s`（GetTicketTypesHandler_…，`PTTL=4989ms bounds=[4987.35, 4990.5416]ms c7s=5201.8ms`）；`Failed!  - Failed:     1, Passed:     8, ... Duration: 29 s`（GetEventsHandler_…，`PTTL=4998ms bounds=[4996.4358, 4999.183]ms c7s=5203.7ms`）
      - HEAD ×3：`Passed! ... Total:     9, Duration: 28 s`／`30 s`／`30 s`
      - 交替 4 輪：
        ```
        [HEAD] Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 29 s
        [CHANGE]   Failed ...QueryCacheTtlSafetyNetTests.GetEventsHandler_WhenInvalidationHappensWhileQueryInFlight_CacheTemporarilyResurrectsStaleValue_ButExpiresWithinTtl [7 s]
        [CHANGE] Failed!  - Failed:     1, Passed:     8, Skipped:     0, Total:     9, Duration: 28 s
        [HEAD] Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 30 s
        [CHANGE]   Failed ...QueryCacheTtlSafetyNetTests.GetTicketTypesHandler_WhenInvalidationHappensWhileQueryInFlight_CacheTemporarilyResurrectsStaleValue_ButExpiresWithinTtl [7 s]
        [CHANGE] Failed!  - Failed:     1, Passed:     8, Skipped:     0, Total:     9, Duration: 29 s
        [HEAD]   Failed ...QueryCacheTtlSafetyNetTests.GetEventsHandler_WhenInvalidationHappensWhileQueryInFlight_CacheTemporarilyResurrectsStaleValue_ButExpiresWithinTtl [7 s]
        [HEAD] Failed!  - Failed:     1, Passed:     8, Skipped:     0, Total:     9, Duration: 29 s
        [CHANGE] Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 30 s
        [HEAD] Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 29 s
        [CHANGE] Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 31 s
        ```
        每次 stash list：`stash@{0}: WIP on feature/unit-of-work-commit-close-failure: 4d975b7 開發流程規範補強：證據貼原文、hardener 改以判準界定並列為 task`；pop 皆成功，最後 `git status --short` 與修改前相同。
      - 合計：HEAD 1/9 輪失敗、改動 6/11 輪失敗（含全套那次）。HEAD 可重現同一失敗型態，為既有問題；但改動側失敗比例較高，原因未查明（未發現機制上的關聯）。
    - 時鐘探測（使用者選擇先查清比例差異）：在 api 容器每 100ms 取樣「牆上時鐘 − `/proc/uptime`」差值，變動超過 ±50ms 即記錄（容器與 Redis 容器共用 WSL2 核心時鐘）；同時以 `--logger trx` 跑 5 輪（改動側），取兩個競態測試的起訖時間：
      ```
      19:09:04.845 jump -814ms / 19:09:05.339 jump -721ms
      19:09:33.366 jump -801ms / 19:09:33.860 jump -689ms
      19:10:01.834 jump -809ms / 19:10:02.325 jump -722ms
      19:10:30.341 jump -797ms / 19:10:30.831 jump -707ms
      19:10:58.788 jump -806ms / 19:10:59.285 jump -696ms
      19:11:27.259 jump -799ms / 19:11:27.849 jump -691ms
      19:11:55.844 jump -805ms / 19:11:56.342 jump -697ms
      run1 GetEvents 19:09:16.268 -> 19:09:24.195 Passed；GetTicketTypes 19:08:54.628 -> 19:09:02.582 Passed
      run2 GetTicketTypes 19:09:33.910 -> 19:09:41.157 Passed；GetEvents 19:09:54.712 -> 19:10:02.644 Passed
      run3 GetEvents 19:10:33.968 -> 19:10:41.853 Passed；GetTicketTypes 19:10:12.566 -> 19:10:20.388 Passed
      run4 GetEvents 19:11:11.719 -> 19:11:19.666 Passed；GetTicketTypes 19:10:51.906 -> 19:10:59.742 Passed
      run5 GetTicketTypes 19:11:28.953 -> 19:11:36.826 Passed；GetEvents 19:11:50.314 -> 19:11:56.738 Failed
      ```
      摘要行：前 4 輪 `Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9`，第 5 輪 `Failed!  - Failed:     1, Passed:     8, Skipped:     0, Total:     9, Duration: 29 s`。
    - 結論：牆上時鐘約每 28.5 秒回跳約 1.5 秒；唯一失敗的測試（run5 GetEvents）其執行期間包含 19:11:55.8 的回跳，未回跳的測試全數通過（run2 GetEvents、run4 GetTicketTypes 的回跳落在測試尾段，推定已過 (7) 檢查點）。失敗機制為 Redis 以牆上時鐘計 TTL、回跳使 key 實際存活超過 T + M2，與本 change 無關。先前 HEAD／改動的比例差異歸因於相位：每輪約 29 秒、與回跳週期幾乎相同，連續幾輪的回跳會落在同一相位，因此失敗成串出現（此次改動側 5 輪僅 1 輪失敗）。修正方向（(7) 加入時鐘回跳餘裕或改以輪詢）不在本 change 範圍。
    - `openspec validate unit-of-work-commit-close-failure --strict`：`Change 'unit-of-work-commit-close-failure' is valid`

## 3. 審查第 1 輪修正（design 決策 5）

- [x] 3.1 提交／回滾成功後立即關閉自有連線、只關一次（對抗式審查 B1、B2；使用者 2026-10-20 選擇此方向）：先在 `UnitOfWorkTests` 修改 UOW-TX-013、新增 015、016、017（見 AC 表），修改 `UnitOfWork.cs` 前執行 `UOW 測試`；預期 013、015 因提交／回滾後連線仍 `Open` 失敗，017 因第二次釋放關閉連線失敗；016 在當時實作下預期通過（連線延到釋放才關，回滾不觸發關閉）。再以 `CloseOwnedConnectionAsync` 實作。防禦分支對照：關閉失敗（009、016）、重複釋放（017）、呼叫端先開（010、007）；`CloseConnectionAsync` 不接收 token，提交／回滾已完成後不因取消而中斷關閉。同時更正 `UnitOfWorkTests` 兩處過時註解（UOW-TX-005、008 測試）。
  - RED（2026-10-20T03:19:14+08:00，修改 `UnitOfWork.cs` 前；`git status --short src` 輸出 ` M src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs` 為第 1 輪前的既有修改）：命令 `docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~ProjectC.Infrastructure.Tests.UnitOfWorkTests"`。
      ```
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.CommitAsync_OnOwnedConnection_ClosesConnectionBeforeDispose [175 ms]
        Error Message:
         Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Closed {value: 0} because 提交成功後須立即歸還連線, but found ConnectionState.Open {value: 1}.
        Stack Trace:
           at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
         at ProjectC.Infrastructure.Tests.UnitOfWorkTests.CommitAsync_OnOwnedConnection_ClosesConnectionBeforeDispose() in /src/tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs:line 339
         at ProjectC.Infrastructure.Tests.UnitOfWorkTests.CommitAsync_OnOwnedConnection_ClosesConnectionBeforeDispose() in /src/tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs:line 340
         at ProjectC.Infrastructure.Tests.UnitOfWorkTests.CommitAsync_OnOwnedConnection_ClosesConnectionBeforeDispose() in /src/tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs:line 347
      --- End of stack trace from previous location ---
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.RollbackAsync_OnOwnedConnection_ClosesConnectionBeforeDisposeAndDiscardsWrites [101 ms]
        Error Message:
         Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Closed {value: 0} because 回滾成功後須立即歸還連線, but found ConnectionState.Open {value: 1}.
        Stack Trace:
           at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
         at ProjectC.Infrastructure.Tests.UnitOfWorkTests.RollbackAsync_OnOwnedConnection_ClosesConnectionBeforeDisposeAndDiscardsWrites() in /src/tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs:line 387
         at ProjectC.Infrastructure.Tests.UnitOfWorkTests.RollbackAsync_OnOwnedConnection_ClosesConnectionBeforeDisposeAndDiscardsWrites() in /src/tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs:line 388
         at ProjectC.Infrastructure.Tests.UnitOfWorkTests.RollbackAsync_OnOwnedConnection_ClosesConnectionBeforeDisposeAndDiscardsWrites() in /src/tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs:line 392
      --- End of stack trace from previous location ---
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.DisposeAsync_CalledTwice_DoesNotCloseConnectionOpenedAfterwards [9 ms]
        Error Message:
         Expected dbContext.Database.GetDbConnection().State to be ConnectionState.Open {value: 1} because 重複釋放交易不得關閉呼叫端之後開啟的連線, but found ConnectionState.Closed {value: 0}.
        Stack Trace:
           at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
         at ProjectC.Infrastructure.Tests.UnitOfWorkTests.DisposeAsync_CalledTwice_DoesNotCloseConnectionOpenedAfterwards() in /src/tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs:line 425
         at ProjectC.Infrastructure.Tests.UnitOfWorkTests.DisposeAsync_CalledTwice_DoesNotCloseConnectionOpenedAfterwards() in /src/tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs:line 427
      --- End of stack trace from previous location ---
      
      Failed!  - Failed:     3, Passed:    17, Skipped:     0, Total:    20, Duration: 593 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
    三個失敗皆為連線狀態斷言（目標行為），非編譯或環境錯誤；016 通過，符合預期。
  - GREEN：命令 `docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~ProjectC.Infrastructure.Tests.UnitOfWork"`（含 `UnitOfWorkOpenConnectionTests`）：
      ```
      Passed!  - Failed:     0, Passed:    29, Skipped:     0, Total:    29, Duration: 587 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
  - 重構：N/A（`CloseOwnedConnectionAsync` 實作時即抽出，供提交、回滾、釋放三處共用，無後續重構）。
  - 還原驗證（`git stash push -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs` 還原到 HEAD，同一命令）：
      ```
      Saved working directory and index state WIP on feature/unit-of-work-commit-close-failure: 4d975b7 開發流程規範補強：證據貼原文、hardener 改以判準界定並列為 task
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.RollbackAsync_WhenConnectionCloseFailsAfterRollback_DoesNotThrowAndLogsWarning [24 ms]
        Error Message:
         Did not expect any exception because 回滾已完成，關閉連線失敗不得讓呼叫端誤判回滾失敗, but found System.InvalidOperationException: close failed
         at ProjectC.Infrastructure.Tests.TestSupport.ThrowingCloseInterceptor.ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) in /src/tests/ProjectC.Infrastructure.Tests/TestSupport/ThrowingCloseInterceptor.cs:line 11
         at Microsoft.EntityFrameworkCore.Diagnostics.Internal.RelationalConnectionDiagnosticsLogger.ConnectionClosingAsync(IRelationalConnection connection, DateTimeOffset startTime)
         at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.CloseAsync()
         at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.CloseAsync()
         at Microsoft.EntityFrameworkCore.Storage.RelationalTransaction.ClearTransactionAsync(CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Storage.RelationalTransaction.RollbackAsync(CancellationToken cancellationToken)
         at ProjectC.Infrastructure.Persistence.UnitOfWorkTransaction.RollbackAsync(CancellationToken cancellationToken) in /src/src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs:line 104
         at FluentAssertions.Specialized.NonGenericAsyncFunctionAssertions.NotThrowAsync(String because, Object[] becauseArgs).
        Stack Trace:
           at FluentAssertions.Specialized.DelegateAssertionsBase`2.NotThrowInternal(Exception exception, String because, Object[] becauseArgs)
         at FluentAssertions.Specialized.NonGenericAsyncFunctionAssertions.NotThrowAsync(String because, Object[] becauseArgs)
         at ProjectC.Infrastructure.Tests.UnitOfWorkTests.RollbackAsync_WhenConnectionCloseFailsAfterRollback_DoesNotThrowAndLogsWarning() in /src/tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs:line 404
         at ProjectC.Infrastructure.Tests.UnitOfWorkTests.RollbackAsync_WhenConnectionCloseFailsAfterRollback_DoesNotThrowAndLogsWarning() in /src/tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs:line 410
      --- End of stack trace from previous location ---
        Failed ProjectC.Infrastructure.Tests.UnitOfWorkTests.CommitAsync_WhenConnectionCloseFailsAfterCommit_DoesNotThrowAndPersistsChanges [14 ms]
        Error Message:
         Did not expect any exception because 資料已落地，關閉連線失敗不得讓呼叫端誤判提交失敗, but found System.InvalidOperationException: close failed
         at ProjectC.Infrastructure.Tests.TestSupport.ThrowingCloseInterceptor.ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) in /src/tests/ProjectC.Infrastructure.Tests/TestSupport/ThrowingCloseInterceptor.cs:line 11
         at Microsoft.EntityFrameworkCore.Diagnostics.Internal.RelationalConnectionDiagnosticsLogger.ConnectionClosingAsync(IRelationalConnection connection, DateTimeOffset startTime)
         at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.CloseAsync()
         at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.CloseAsync()
         at Microsoft.EntityFrameworkCore.Storage.RelationalTransaction.ClearTransactionAsync(CancellationToken cancellationToken)
         at Microsoft.EntityFrameworkCore.Storage.RelationalTransaction.CommitAsync(CancellationToken cancellationToken)
         at ProjectC.Infrastructure.Persistence.UnitOfWorkTransaction.CommitAsync(CancellationToken cancellationToken) in /src/src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs:line 96
         at FluentAssertions.Specialized.NonGenericAsyncFunctionAssertions.NotThrowAsync(String because, Object[] becauseArgs).
        Stack Trace:
           at FluentAssertions.Specialized.DelegateAssertionsBase`2.NotThrowInternal(Exception exception, String because, Object[] becauseArgs)
         at FluentAssertions.Specialized.NonGenericAsyncFunctionAssertions.NotThrowAsync(String because, Object[] becauseArgs)
         at ProjectC.Infrastructure.Tests.UnitOfWorkTests.CommitAsync_WhenConnectionCloseFailsAfterCommit_DoesNotThrowAndPersistsChanges() in /src/tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs:line 292
         at ProjectC.Infrastructure.Tests.UnitOfWorkTests.CommitAsync_WhenConnectionCloseFailsAfterCommit_DoesNotThrowAndPersistsChanges() in /src/tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs:line 299
      --- End of stack trace from previous location ---
      
      Failed!  - Failed:     2, Passed:    27, Skipped:     0, Total:    29, Duration: 562 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      stash@{0}: WIP on feature/unit-of-work-commit-close-failure: 4d975b7 開發流程規範補強：證據貼原文、hardener 改以判準界定並列為 task
      Dropped refs/stash@{0} (321ef042c1d0d5fa68e5e993d0c2e9d3838bfbf6)
      ```
    pop 後重跑：`Passed!  - Failed:     0, Passed:    29, Skipped:     0, Total:    29, Duration: 541 ms - ProjectC.Infrastructure.Tests.dll (net10.0)`。
    說明：stash 只能還原到 HEAD，不能還原到第 1 輪前的中間狀態。HEAD 下 009（§8 ⑥ 本體）與 016 失敗：HEAD 的 `RollbackAsync` 有同類問題（EF 回滾後關閉連線失敗，例外從 `RollbackAsync` 傳出，堆疊經 `UnitOfWork.cs:line 104`），本輪一併修正（design 決策 5）。013、015、017 在 HEAD 通過（HEAD 由 EF 於提交／回滾內關閉連線、釋放不關閉），它們防的是第 1 輪前實作引入的退化，對應的失敗證據為上方 RED。
- [x] 3.2 全套回歸重跑：`docker compose exec api dotnet test ProjectC.slnx`，擷取各 DLL 摘要行原文；`QueryCacheTtlSafetyNetTests` 若失敗，依 2.2 判讀（§8 ⑧）並記錄重跑結果；`openspec validate unit-of-work-commit-close-failure --strict` 通過。TDD N/A（整合確認）。
  - 完成紀錄（2026-10-20，命令 `docker compose exec api dotnet test ProjectC.slnx`，exit 0）：
      ```
      Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 120 ms - ProjectC.Domain.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   527, Skipped:     0, Total:   527, Duration: 2 s - ProjectC.Application.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 10 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   248, Skipped:     0, Total:   248, Duration: 51 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   541, Skipped:     0, Total:   541, Duration: 2 m 39 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    `QueryCacheTtlSafetyNetTests` 本次未失敗，不需重跑。`openspec validate unit-of-work-commit-close-failure --strict`：
      ```
      Change 'unit-of-work-commit-close-failure' is valid
      ```

## Workflow follow-up

- 審查迴圈：`strict-reviewer` + 全新對抗式 general-purpose subagent，直到兩者皆無 blocking；修正後需重跑 1.3 還原驗證時，紀錄以最後一次為準並逐測試保留原文。
- 無前端變更，不需真實瀏覽器驗證。
- 使用者同意後 `/opsx:archive`（同步主 spec `openspec/specs/unit-of-work-transaction/spec.md`）→ merge 回 master；不自動 commit／push。
