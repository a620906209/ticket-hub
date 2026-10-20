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
| UOW-TX-024 | `UnitOfWorkOpenConnectionTests`：新增 `OpenConnectionAsync_OnLeftoverOpenConnection_TakesOverAndClosesOnDispose`（第一筆交易提交、關閉失敗後把 `IsFailing` 設為 false，再 `OpenConnectionAsync`；斷言不拋、可執行 `SELECT 1`、釋放連線後 `State` 為 `Closed`） | 3.1、3.2、3.3 |
| UOW-TX-025 | `UnitOfWorkOpenConnectionTests`：新增 `BeginTransactionAsync_AfterCallerConnectionDisposed_OwnsConnection`（`IsFailing` 初始為 false；`OpenConnectionAsync` 後釋放，再把 `IsFailing` 設為 true，開始交易、寫入、提交；斷言 `CommitAsync` 不拋、恰好一筆 Warning 為關閉例外、資料存在） | 3.1、3.2 |
| UOW-TX-026 | `UnitOfWorkOpenConnectionTests`：新增 `BeginTransactionAsync_AfterCallerConnectionCloseFailed_OwnsConnection`（掛 `ThrowingCloseInterceptor`；`OpenConnectionAsync` 後釋放（關閉失敗），再開始交易、寫入、提交；斷言 `CommitAsync` 與交易 `DisposeAsync` 不拋、恰好兩筆 Warning 皆為關閉例外、資料存在） | 3.1、3.2、3.3 |
| UOW-TX-027 | `UnitOfWorkOpenConnectionTests`：既有 `OpenConnectionAsync_WhenAlreadyOpen_ThrowsInvalidOperationException`（行為保護，判斷依據改為 `_hasCallerConnection` 後仍須通過） | 3.1、3.2 |
| UOW-TX-028 | `UnitOfWorkOpenConnectionTests`：既有 `OpenConnectionAsync_WhenTransactionInProgress_ThrowsInvalidOperationException`（行為保護） | 3.1、3.2 |

主 spec 既有的 Scenario（UOW-TX-001～010、013～017；011、012 已於前一 change 移除）不在本 change 的 delta 內，既有測試（`UnitOfWorkTests` 20 個、`UnitOfWorkOpenConnectionTests` 9 個）由 `UOW 測試` 與 4.2 全套回歸涵蓋，必須維持通過。

## 1. 探測 EF 開啟計數推論

- [ ] 1.1 探測（不保留，TDD N/A：驗證 design「Context」推論，不是目標行為的測試）：修改正式程式碼前，在 `UnitOfWorkTests` 暫時新增探測測試，直接操作 `dbContext.Database`，不經過工作單元：
  - (a) `OpenConnectionAsync` → 以 interceptor 讓 `BeginTransactionAsync` 失敗 → 呼叫 `CloseConnectionAsync` 一次後 `State` 為 `Open`，第二次後為 `Closed`。
  - (b) `OpenConnectionAsync` → `BeginTransactionAsync` → 提交，關閉時讓 `SwitchableCloseInterceptor` 失敗 → `State` 為 `Open`；把 `IsFailing` 設為 false，呼叫 `CloseConnectionAsync` 一次後為 `Closed`（計數已為 0）。
  - (c) 承 (b) 的遺留狀態，`OpenConnectionAsync` 一次、`CloseConnectionAsync` 一次後為 `Closed`（決策 1、2 接手的前提）。
  - (d) 承 (b) 的遺留狀態，`OpenConnectionAsync` 一次 → 以 interceptor 讓 `BeginTransactionAsync` 失敗 → 重複 `CloseConnectionAsync`，記錄每次關閉後的 `State`，預期不超過 3 次即為 `Closed`（決策 3 上限在「接手加開始失敗」組合下足夠）。

  探測所需的 interceptor 在本步驟先加入並保留：`ThrowingTransactionInterceptor` 的 `startFailure`（2.1 使用）與 `TestSupport/SwitchableCloseInterceptor`（3.1 使用）。執行後刪除探測測試，以 `git diff --stat tests` 確認只剩這兩項 interceptor 變更。**停止條件**：任一點與推論不符時，停止並回報，回到 design，不進入 2.1。
  - 完成紀錄：（命令、各探測斷言的實際 `State` 值與測試輸出原文，其中 (c) 須列出接手後那一次 `CloseConnectionAsync` 後的 `State`、(d) 須列出每次關閉後的 `State` 與總次數；刪除後的 `git diff --stat` 輸出）

## 2. 開始交易失敗時關閉工作單元負責的連線

- [ ] 2.1 測試先行（RED）：使用 1.1 加入的 `startFailure`（覆寫 `TransactionStartingAsync`，以可切換旗標控制是否拋出）；在 `UnitOfWorkTests` 新增 UOW-TX-018～021 四個測試（見 AC 表）。修改 `UnitOfWork.cs` 前執行 `UOW 測試`。預期：
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
  - 完成紀錄：（修改 `UnitOfWork.cs` 前的時間與 `git status --short src` 輸出、`UOW 測試` 摘要行原文、每個失敗測試的 Error Message 原文與失敗原因、通過的行為保護測試）
- [ ] 2.2 最小實作（GREEN）：依 design 決策 3，在 `BeginTransactionAsync` 以 try／catch 包住 `Database.BeginTransactionAsync`；`ownsConnection` 為 true 時重複 `CloseConnectionAsync` 至 `State == Closed`，上限 3 次，關閉失敗記 Warning 並停止，3 次後仍開啟另記 Warning；最後 `throw;`。catch 內的 `CloseConnectionAsync` 不傳 `CancellationToken`（避免已取消的 token 影響關閉）。註解須寫明：(1) 「3 次後仍開啟」的 Warning 是防禦分支，沒有注入點可觸發、spec 不保證（design 決策 3）；(2) 重複關閉依賴 EF Core（`Directory.Packages.props` 目前 10.0.11）開始失敗後計數殘留、遺留連線接手時只加計數的內部行為，升級 EF 時以 UOW-TX-018～026 作為回歸偵測。執行 `UOW 測試`，全數通過。
  - 完成紀錄：（命令與摘要行原文；重構：若有則記錄重構後重跑的摘要行，無則記 N/A）
- [ ] 2.3 還原驗證：`git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs` → `git stash list` 確認存在 → 執行 `UOW 測試`，預期 UOW-TX-018、019、020 依 2.1 的原因失敗，其餘通過 → `git stash list` 確認後 `git stash pop`。pop 失敗或有衝突時立即停止並回報。
  - 完成紀錄：（stash push／list／pop 輸出原文、摘要行原文、三個失敗測試的 Error Message 原文）

## 3. 連線歸屬依工作單元的記錄判斷

- [ ] 3.1 測試先行（RED）：使用 1.1 加入的 `SwitchableCloseInterceptor`；在 `UnitOfWorkTests` 新增 UOW-TX-022、023，在 `UnitOfWorkOpenConnectionTests` 新增 UOW-TX-024、025、026（見 AC 表）。執行 `UOW 測試`（此時 `UnitOfWork.cs` 只含 2.2 的變更）。預期：
  - UOW-TX-022（RED）：第二次 `CommitAsync` 拋出 `close failed`（Expected 不拋例外），堆疊經 `RelationalTransaction.CommitAsync` → `ClearTransactionAsync` → `RelationalConnection.CloseAsync`，即修正前判定為非自有、由 EF 在提交時關閉。若失敗於第一次提交或 `BeginTransactionAsync`，不算有效 RED，停止並回報。
  - UOW-TX-024（RED）：`OpenConnectionAsync` 拋出 `InvalidOperationException: A connection is already open on this unit of work.`。
  - UOW-TX-026（RED）：`CommitAsync` 拋出 `close failed`（Expected 不拋例外），堆疊經 `RelationalTransaction.CommitAsync` → `ClearTransactionAsync`：呼叫端連線釋放時關閉失敗留下開啟的連線，修正前判定為非自有。若失敗於 `OpenConnectionAsync` 或連線的 `DisposeAsync`，不算有效 RED，停止並回報。
  - UOW-TX-023、025（預期通過，行為保護，見 design 決策 4）；UOW-TX-027、028 為既有測試，維持通過。若 023 或 025 在修正前就失敗，代表 design 推論不符，停止並回報。
  - 2.1 的四個測試與既有 29 個測試維持通過。
  - 防禦分支對照（hardener 檢查清單）：
    - 狀態機：已有呼叫端連線時重複開啟仍拋出（既有 `OpenConnectionAsync_WhenAlreadyOpen_ThrowsInvalidOperationException`）；有進行中交易時開啟仍拋出（既有 `OpenConnectionAsync_WhenTransactionInProgress_ThrowsInvalidOperationException`）；呼叫端連線重複釋放不影響記錄（既有 `DisposeAsync_CalledTwice_DoesNotCloseConnectionOpenedLater`，回呼位於防重入之後）；呼叫端連線釋放時關閉失敗後記錄仍重設（決策 1，UOW-TX-026）。
    - 其餘項目同 2.1。
  - 完成紀錄：（`UOW 測試` 摘要行原文、每個失敗測試的 Error Message 原文與失敗原因、通過的行為保護測試）
- [ ] 3.2 最小實作（GREEN）：依 design 決策 1、2 修改 `UnitOfWork.cs`：`UnitOfWork` 新增 `_hasCallerConnection`；`OpenConnectionAsync` 改以該欄位判斷是否重複開啟，成功後設為 true；`UnitOfWorkConnection` 接收回呼，於 `DisposeAsync` 防重入之後、關閉之前呼叫；`BeginTransactionAsync` 的 `ownsConnection` 改為 `!_hasCallerConnection`，為 true 時一律 `Database.OpenConnectionAsync`。更新 `BeginTransactionAsync` 內引用設計決策的註解，改指向本 change；`IUnitOfWork.cs` 中 `OpenConnectionAsync` 的 XML doc 由「連線已開啟…時 MUST 拋出」改為「已有呼叫端連線或進行中交易時拋出」；`IUnitOfWorkConnection` 的 XML doc 補上「關閉失敗時工作單元的呼叫端連線記錄仍會重設，之後的交易由工作單元負責連線」與「交易提交或回滾後可在交易物件釋放前先釋放；交易未完成時須先釋放交易」（明文化既有用法：`OrderService.cs:447` 提交後即釋放連線，既有 `DisposeAsync_AfterCommitBeforeTransactionDisposed_ClosesConnectionAndTransactionDisposeDoesNotThrow` 保護，須維持通過，design 決策 1）。執行 `UOW 測試`，全數通過。
  - 變異檢查（證明行為保護測試能失敗，檢查後還原並以 `git diff` 確認）：(a) 暫時移除 `UnitOfWorkConnection.DisposeAsync` 對回呼的呼叫，預期 UOW-TX-025、026 失敗（`CommitAsync` 拋出 `close failed`）；(b) 暫時移除開始失敗處理中的 `ownsConnection` 條件（呼叫端連線也重複關閉），預期 UOW-TX-021 失敗（`State` 為 `Closed`）。任一變異未使預期測試失敗時，停止並回報。
  - 完成紀錄：（命令與摘要行原文；兩項變異檢查的失敗測試 Error Message 原文與還原後的 `git diff --stat`；重構後重跑或 N/A）
- [ ] 3.3 還原驗證：`git stash push -u -- src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs`（還原至修正前，2.2 的變更也一併還原）→ `git stash list` → 執行 `UOW 測試`，預期 UOW-TX-018、019、020、022、024、026 依 2.1／3.1 的原因失敗，其餘通過 → `git stash list` 確認後 `git stash pop`。pop 失敗或有衝突時立即停止並回報。
  - 完成紀錄：（stash push／list／pop 輸出原文、摘要行原文、六個失敗測試的 Error Message 原文）

## 4. 最終檢查與文件

- [ ] 4.1 hardener 最終檢查：程式碼全部完成後，依 `.claude/skills/hardener/SKILL.md` 檢查 `UnitOfWork.BeginTransactionAsync`、`UnitOfWork.OpenConnectionAsync`、`UnitOfWorkConnection.DisposeAsync`。發現缺少的防禦分支時，新增 task 依 TDD 補上；沒有缺漏時記錄逐項結論。「3 次後仍開啟」的 Warning 列為已知無測試的防禦分支（design 決策 3），不視為缺漏。
  - 完成紀錄：（逐項結論；若有補強，記錄新增的 task 編號）
- [ ] 4.2 全套回歸：`docker compose exec api dotnet test`，所有測試專案 `Failed: 0`。若有失敗，判斷是否與本 change 相關：相關則修正；無關的既有 flake，記錄測試名稱與訊息原文，並單獨重跑通過。
  - 完成紀錄：（各 DLL 摘要行原文；失敗與重跑的原文，若有）
- [ ] 4.3 文件同步（TDD N/A：純文件）：`docs/project-scope.md` §8 待辦 ⑦ 標記 ✅，記錄由本 change 處理、探測結論，以及殘留的 Non-Goal（呼叫端持有連線時的計數殘留）。以 `git diff docs/project-scope.md` 確認只改 ⑦。
  - 完成紀錄：（diff 摘要）

## Workflow follow-up

- strict-reviewer 與對抗式審查迴圈通過、使用者同意後，執行 `/opsx:archive`，同步主 spec。
- 歸檔同步後，依 design 決策 5 手動更新 `openspec/specs/unit-of-work-transaction/spec.md` 的 Purpose，並以 `openspec validate --specs` 確認。
