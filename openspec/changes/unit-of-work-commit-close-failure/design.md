# Design

## Context

見 proposal.md「Why」。現況（`src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs`，`unit-of-work-rollback-dispose` 之後）：

- `BeginTransactionAsync` 直接呼叫 `Database.BeginTransactionAsync`；連線未開時由 EF 交易自行開啟，提交或回滾**完成**後 EF 於同一呼叫內清除交易並關閉連線；提交／回滾在完成前失敗時，關閉延到釋放交易（`RelationalTransaction.DisposeAsync`）。上一個 change 以探測測試證實。
- `UnitOfWorkTransaction.CommitAsync` 依序 `SaveChangesAsync`、`_transaction.CommitAsync`、`_completed = true`；關閉失敗的例外從 `_transaction.CommitAsync` 傳出，`_completed` 維持 false。
- `DisposeAsync`：try 自動回滾／catch 記 Warning／finally `_completed = true` 並以 try/catch 釋放交易。
- `IUnitOfWork.OpenConnectionAsync` 回傳 `UnitOfWorkConnection`，由呼叫端釋放時 `CloseConnectionAsync`（關閉失敗記 Warning 不重拋）。
- 正式程式碼 11 處 `CommitAsync`；只有 `OrderService` 先 `OpenConnectionAsync` 再 `BeginTransactionAsync`。

## Goals / Non-Goals

**Goals:**

- 提交成功後關閉連線失敗不再從 `CommitAsync` 傳出、不記誤導的回滾 Warning（UOW-TX-009）。
- 連線由工作單元在提交／回滾成功後立即關閉（UOW-TX-013、015），關閉失敗記 Warning、不從提交／回滾傳出（UOW-TX-009、016）；未完成的交易於釋放時關閉（UOW-TX-003、005、008），重複釋放不再關閉（UOW-TX-017）。
- 呼叫端先開連線時行為不變（UOW-TX-007、010）。

**Non-Goals:**

- `UnitOfWorkConnection` 行為、§8 待辦 ①④、11 個呼叫端與 `IUnitOfWork` / `IUnitOfWorkTransaction` 介面。
- 開始交易失敗時的連線外洩（見決策 3，已知缺口，列 §8 待辦）。
- 「提交本身失敗」與「提交後關閉失敗」以外的提交結果判定（例如提交指令送出後連線中斷、結果未知）：屬資料庫層級的不確定提交，不在此處理。UOW-TX-006 因此限定在提交送達資料庫前失敗、可確定未提交的情境（對應測試的 interceptor 在 EF 實際提交前拋例外），結果無法確定時 Requirement 明載不保證資料是否落地。

## Decisions

### 決策 1：連線未開時由工作單元先開啟，交易不擁有連線

`BeginTransactionAsync` 在 `GetDbConnection().State == Closed` 時先 `Database.OpenConnectionAsync(cancellationToken)`，再 `Database.BeginTransactionAsync`，並把「是否由工作單元開啟」傳給 `UnitOfWorkTransaction`（`ownsConnection`）。關閉時點見決策 5。

- 機制（推論，依 EF Core `RelationalConnection` 的開啟計數，未讀原始碼）：手動開啟使計數為 1，開始交易再加 1；提交／回滾後 EF 清除交易時減 1，計數不為 0、不關閉實體連線；`DisposeAsync` 最後 `Database.CloseConnectionAsync()` 減到 0 才關閉。呼叫端先開連線（UOW-TX-007）目前就是同一機制，已有測試證實提交／回滾不關閉連線。
  - 推論的驗證點：UOW-TX-010（手動開啟後開始交易、提交，`State` 仍為 `Open`）正是本決策依賴的計數行為，tasks 1.1 在修改正式程式碼前先跑；若不通過，停止並回報，回到 design 重新決定，不改用其他手段硬造。
- 替代方案「`CommitAsync` 判斷已提交、吞掉關閉例外」：需依 Npgsql 交易物件內部狀態判斷「已提交」，脆弱；否決。
- 替代方案「拋新例外型別讓呼叫端區分」：須修改 11 個呼叫端程式碼以區分例外型別；否決（Grill Q1）。

### 決策 2：`DisposeAsync` 於 finally 最後關閉尚未關閉的自有連線

（提交／回滾成功時連線已於決策 5 關閉，此處只處理未完成或提交失敗的交易。）順序：自動回滾（try/catch Warning）→ 釋放交易（try/catch Warning）→ `ownsConnection` 時 `CloseConnectionAsync`（try/catch Warning，新訊息「Failed to close the unit of work transaction connection.」）。三個 catch 訊息不同，可在日誌區分。不重拋理由同 `unit-of-work-rollback-dispose` 決策 1（釋放多在例外展開途中）。

- 釋放交易本身失敗的 catch 保留為防禦實作：交易不再擁有連線後，EF 的釋放交易不會觸發關閉，目前沒有可用的失敗注入點（transaction interceptor 沒有釋放攔截點），無法驗證，因此 Requirement 不再對此作保證，只保證回滾失敗與關閉連線失敗（spec-reviewer B-002，使用者 2026-10-20 選擇移除保證而非登記例外）。
- UOW-TX-005 內容改為「回滾與釋放時關閉連線都失敗」，對應實際仍可注入的路徑。005、008 的 Scenario 標題沿用主 spec 原文、只改內容：曾嘗試改標題，`openspec validate --strict` 報錯「MODIFIED omits scenario(s) the current spec still has」，MODIFIED 不允許以改名取代既有 Scenario。已知落差：008 標題「自動回滾過程中關閉連線失敗」在新設計下，關閉實際發生在回滾成功後、釋放的最後一步；005 的「釋放交易」指整個釋放動作（含關閉連線）。以 Scenario 內容為準（使用者 2026-10-20 核准沿用）。
- UOW-TX-014 補上「提交前 `SaveChangesAsync` 失敗」這條同屬可確定未提交的路徑（spec-reviewer 第 3 輪建議，使用者 2026-10-20 決定補）：以主鍵重複觸發真實 `DbUpdateException`，不需 interceptor。
- UOW-TX-013 補上成功路徑的關閉斷言（spec-reviewer 建議）：修正前 EF 提交時就關閉連線，此測試在 HEAD 通過；決策 5 後改為斷言「提交後、釋放前即關閉」，在決策 1／2 的釋放時關閉實作下失敗（tasks 3.1 RED）。
- 回滾失敗後連線狀態：回滾失敗時交易未清除，釋放交易時 EF 清除交易、計數減 1，再由 `CloseConnectionAsync` 關閉（UOW-TX-003）。

### 決策 3：開始交易失敗時的連線外洩不在本 change 處理（已知缺口）

原規劃：自行開啟連線後開始交易失敗時，於 catch 關閉連線再重拋（UOW-TX-011／012）。實作時 tasks 1.1 的停止條件觸發：

- 修正前 011 即失敗（EF 交易自行開啟連線、開始交易失敗後連線維持 `Open`），外洩為既有行為。
- 加入 catch 關閉後 011／012 仍失敗：連線仍為 `Open`，且關閉攔截器未被呼叫（012 無 Warning）。推論（未讀原始碼、未另行探測）：EF `BeginTransactionAsync` 內部再開啟一次使計數 +1，開始失敗時未減回，工作單元關一次後計數停在 1，不會實際關閉。
- 替代方案「重複關閉直到 `State == Closed`」：依賴 EF 未公開的計數行為；若 EF 日後於開始失敗時自行減回，多關一次會關掉呼叫端開啟的連線（下單主路徑），否決。
- 決定（使用者 2026-10-20）：移除 Requirement「開始交易失敗時不遺留連線」、UOW-TX-011／012 與該 catch；外洩維持修正前行為，連線於 `DbContext` 釋放（request 結束）時關閉；列入 `docs/project-scope.md` §8 待辦，日後另開 change 研究。

### 決策 4：測試沿用 interceptor 注入

- UOW-TX-009：只掛 `ThrowingCloseInterceptor`（`TestSupport`），開始交易、寫入、提交、釋放；修正前提交即拋 `close failed`（RED）。
- UOW-TX-010：呼叫端先開連線後提交，斷言 `State` 為 `Open`、可再開始交易、資料存在；修正前後皆通過（行為保護）。

### 決策 5：提交／回滾成功後立即關閉自有連線，只關一次

第 1 輪對抗式審查發現兩個問題，經核實成立：

- 連線留到釋放交易才關，違反 `IUnitOfWork.OpenConnectionAsync` 契約「持有連線期間不得等待資料庫以外的慢速 I/O（例如 Redis），應先 dispose」（`IUnitOfWork.cs`）。多數呼叫端在提交後、離開 `await using` 前呼叫 Redis（例如 `JoinPurchaseQueueHandler` 的 `SyncJoinAsync`、各 handler 提交後清快取），修正前提交即歸還連線，原設計會在 Redis 變慢時多占連線至 Redis 逾時（2 秒）。原 Risks 敘述「差距只有幾行程式」與事實不符。
- `DisposeAsync` 無防重入，重複釋放會再關一次連線，使 EF 開啟計數多減，關掉之後呼叫端開啟的連線（同檔 `UnitOfWorkConnection` 以 `_disposed` 防同一問題）。

決定（使用者 2026-10-20）：`CommitAsync`／`RollbackAsync` 成功後由工作單元關閉自有連線（`CloseOwnedConnectionAsync`），失敗記 Warning 不拋出；關閉前先把 `_ownsConnection` 設為 false，`DisposeAsync` 只關尚未關閉的連線，提交後與重複釋放都不會再關。

- 替代方案「維持釋放時關閉、只修重複釋放」：須另行記錄與 `IUnitOfWork` 契約的衝突，且排隊熱點受 Redis 延遲影響；否決。
- 附帶修正：HEAD 的 `RollbackAsync` 在 EF 回滾後關閉連線失敗時同樣把例外傳出（與 §8 ⑥ 同類），UOW-TX-016 的還原驗證證實；現由同一路徑吞下並記 Warning。
- 附帶效果：提交後、釋放前呼叫 `OpenConnectionAsync` 恢復為修正前行為（連線已關閉，可開啟）。
- 關閉失敗後不重試：連線可能維持開啟到 `DbContext` 釋放，與既有「關閉失敗」條款一致。

## Risks / Trade-offs

- [EF 開啟計數推論不成立] → tasks 1.1 先驗證，不成立即停止回報。
- [提交後關閉連線失敗時，該連線可能留到 DbContext 釋放] → 不重試關閉：重複關閉會使 EF 開啟計數多減（決策 5）；request 結束時 DbContext 釋放。
- [開始交易失敗時連線留到 DbContext 釋放] → 既有行為，見決策 3。
- [連線因開始交易失敗或關閉失敗留在開啟狀態時，同一 DbContext 之後的交易以 `State` 判斷為非自有連線，關閉交還 EF，提交後關閉再失敗會重現 §8 ⑥] → 第 2 輪對抗式審查探測證實，須同一 scope 連續失敗才觸發；併入 §8 ⑦ 研究（由工作單元記錄是否自行開啟，不以狀態推斷）。
- [失敗的提交或 `RollbackAsync` 失敗時，連線占到釋放交易才關閉] → spec 明載；呼叫端皆以 `await using`，例外展開時立即釋放。
- [釋放交易本身失敗的 catch 無測試] → 見決策 2，無注入點，已從 Requirement 保證中移除，只作防禦。
- [提交後關閉失敗改為只記 Warning，呼叫端無感] → 資料已落地，回傳成功才是正確結果；Warning 含例外，Seq 可查。

## Migration Plan

純程式修正，無 schema、設定或 API 變更；直接部署，回退即還原該 commit。
