# Design

## Context

見 proposal.md「Why」。現況（`src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs`）：

- `UnitOfWorkTransaction.DisposeAsync` 在 `_completed == false` 時 `await _transaction.RollbackAsync()`，之後才 `await _transaction.DisposeAsync()`；兩者之間沒有 try/finally。
- 同檔 `UnitOfWorkConnection.DisposeAsync` 關閉連線失敗時 catch、記 Warning、不重拋（`openspec/changes/archive/2026-10-08-order-placement-p95-phase2/design.md` 決策 2）。
- `UnitOfWorkTransaction` 目前沒有 logger；`UnitOfWork` 已有 `ILogger<UnitOfWork>`（可為 null，預設 `NullLogger`）。
- `UnitOfWorkTransaction` 是 `internal`，專案不使用 `InternalsVisibleTo`；既有連線關閉失敗測試以 EF Core interceptor 製造失敗、`ListLogger<UnitOfWork>` 斷言日誌。

## Goals / Non-Goals

**Goals:**

- 自動回滾失敗時交易一律釋放、例外不外傳、記 Warning（spec UOW-TX-002～004）。
- 釋放交易本身失敗時例外不外傳、記 Warning（UOW-TX-005），讓「釋放動作不蓋掉呼叫端原例外」的保證完整。
- 提交失敗後離開交易範圍時，呼叫端看到的是提交例外、交易已釋放（UOW-TX-006）；提交例外本身照常傳出，不變。
- 以測試先證實現況確實未釋放交易（RED），再修正。

**Non-Goals:**

- 明確呼叫 `RollbackAsync` 失敗：例外照常傳給呼叫端（呼叫端主動要求回滾，應知道失敗），`_completed` 維持 false，之後釋放時走本 change 的自動回滾路徑。2026-10-20 查 `src`：正式程式碼沒有任何明確呼叫 `RollbackAsync` 的地方，所有業務失敗（11 處 `CommitAsync` 的呼叫端）都是不提交直接返回、由釋放時的自動回滾收尾——本 change 修的釋放路徑是每次業務失敗都會經過的主路徑。
- 重複呼叫 `DisposeAsync`：第二次呼叫時 `_completed` 已為 true，只會再釋放一次交易；EF Core `RelationalTransaction` 釋放有自己的已釋放旗標，不另加防護與測試。
- `UnitOfWorkConnection`、入場鏡像取消（§8 待辦 ④）。

## Decisions

### 決策 1：回滾與釋放失敗都記 Warning、不重拋，交易於 finally 釋放

`DisposeAsync` 改為：自動回滾放在 try，catch `Exception` 後記 Warning（含例外），finally 內 `_completed = true` 並釋放交易；釋放交易本身也包在 try/catch `Exception` 內記 Warning。兩個 catch 的訊息不同，可在日誌區分是回滾還是釋放失敗。

- 釋放失敗也不重拋：理由與回滾相同（展開途中重拋會蓋掉原例外）。釋放失敗時連線可能留到 `DbContext`（request scope）結束才關閉，這是無法在此層補救的殘餘情況，記錄即可。

- 不重拋的理由：釋放多半發生在 `await using` 因例外展開的途中，重拋會蓋掉原本的業務例外（UOW-TX-004）。回滾失敗通常代表連線已損毀；Npgsql 對損毀連線直接丟棄、不回連線池，伺服器端交易隨連線中斷自動回滾、鎖釋放，所以記錄後不需要其他補救。這與 `UnitOfWorkConnection` 決策 2 一致，符合 CLAUDE.md「捕捉後必須處理、重拋或明確記錄理由」。
- 替代方案「釋放後重拋」：資源不再外洩，但仍會蓋掉原例外；否決。
- 替代方案「只在非例外展開時重拋」：C# 無法可靠得知 `await using` 是否因例外離開；否決。

### 決策 2：logger 由 `UnitOfWork` 傳入

`UnitOfWorkTransaction` 建構子新增 `ILogger` 參數，`BeginTransactionAsync` 傳入 `_logger`，比照 `UnitOfWorkConnection`。不另外向 DI 註冊 logger，日誌 category 沿用 `UnitOfWork`。

### 決策 3：測試以 EF Core transaction interceptor 製造回滾失敗

在 `UnitOfWorkTests` 以 `DbTransactionInterceptor` 覆寫 `TransactionRollingBackAsync` 拋出例外（在實際回滾前），透過 `PostgresFixture.CreateDbContext(interceptor)` 注入，`ListLogger<UnitOfWork>` 斷言日誌；比照既有 `DisposeAsync_WhenCloseFails_LogsWarningAndDoesNotThrow`。

- 釋放失敗（UOW-TX-005）：EF Core 的 transaction interceptor 沒有釋放的攔截點。推論（依對 EF Core `RelationalTransaction` 的理解，未讀原始碼）：交易由 `BeginTransactionAsync` 開啟連線時，EF 在提交或回滾**成功**後就清除交易並關閉連線，釋放時不會再關；只有提交／回滾在完成前失敗時，關閉連線才延到釋放交易時發生。因此 UOW-TX-005 不能用「提交成功後釋放」注入，改為同時掛 `ThrowingTransactionInterceptor`（回滾拋例外）與 `DbConnectionInterceptor.ConnectionClosingAsync` 拋例外，讓關閉連線的例外在 `_transaction.DisposeAsync()` 內發生。既有 `UnitOfWorkOpenConnectionTests` 內的 private `ThrowingCloseInterceptor` 移到 `TestSupport` 供兩個測試類別共用，不複製。
  - 推論的驗證點：修正前 RED 為 `DisposeAsync` 傳出回滾例外，無法區分機制；機制改由 GREEN 驗證——測試斷言恰好兩筆 Warning，例外分別為回滾例外與關閉例外實例。若實作完成後缺少關閉例外那筆（釋放交易沒有觸發關閉，或例外在別處被拋出／吞掉），或任何例外從 `DisposeAsync` 以外的呼叫處拋出，停止並回報，回到 design 重新決定失敗注入點，不改用其他手段硬造。
- 提交失敗（UOW-TX-006）：同一個 transaction interceptor 另覆寫 `TransactionCommittingAsync` 拋例外，模擬連線中斷時提交與回滾都失敗。
- 「交易已釋放」以可觀察結果判定：`Database.CurrentTransaction` 為 null、連線 `State` 為 `Closed`、同一 `UnitOfWork` 再呼叫 `BeginTransactionAsync` 不拋「already in progress」、交易內 `SaveChangesAsync` 寫入的資料在另一個 `DbContext` 查不到。
- 替代方案「fake `IDbContextTransaction` 單元測試」：需讓測試能建構 `internal` 類別（引入 `InternalsVisibleTo`，專案慣例不用）或繞過 `Database.BeginTransactionAsync`；且無法驗證真實連線與資料不落地；否決。

## Risks / Trade-offs

- [回滾失敗被記錄而非傳出，呼叫端無感] → 只發生在釋放路徑（交易本來就要放棄）；Warning 含例外，Seq 可查。明確 `RollbackAsync` 仍傳出例外。
- [釋放失敗被吞掉後，連線可能留到 request scope 結束] → 與修正前相比不更差（修正前同樣外洩且還蓋掉原例外）；Warning 可在 Seq 追查頻率。
- [interceptor 在實際回滾前拋出，與真實「連線中斷導致回滾失敗」不完全相同] → 這兩種情況下釋放交易時 Npgsql 都會回滾或丟棄連線；UOW-TX-003 以「資料不落地」斷言最終結果，不依賴失敗來源。

- [自動回滾過程中關閉連線失敗時，實體連線留到 `DbContext` 結束（2026-10-20 第 2 輪對抗式審查發現，暫時探測測試證實：交易已釋放、`State` 仍為 `Open`、同一工作單元再 `OpenConnectionAsync` 拋「A connection is already open」）] → EF 在呼叫關閉前已把連線視為關閉，此層再關也無效；與修正前相比不更差（修正前同樣留著且例外外傳），與 `UnitOfWorkConnection` 關閉失敗的處理一致。Warning 文字已涵蓋「回滾或其觸發的關閉」，UOW-TX-008 釘住可保證的部分。
- [「交易期間寫入的資料不落地」斷言無法區分修正前後] → 修正前交易未提交，其他連線本來就看不到資料；這條斷言只防誤提交。證明修正的是 `CurrentTransaction` 為 null、連線狀態、可再開交易與例外實例斷言（RED 紀錄的失敗點）。
- [提交成功後釋放交易失敗，由外傳改為只記 Warning] → 符合 Requirement「釋放交易本身失敗 MUST NOT 傳出」；提交成功時 EF 已關閉交易自行開啟的連線，此情況實際上很難發生，未另寫測試。

- [提交成功後關閉連線失敗，呼叫端收到提交例外但資料已落地（既有問題，2026-10-20 第 3 輪對抗式審查發現，暫時探測測試證實）] → 交易自行開啟連線時，EF 在提交成功後於 `CommitAsync` 內關閉連線；關閉失敗的例外從 `CommitAsync` 傳出，`_completed` 維持 false，釋放時的自動回滾得到 Npgsql「This NpgsqlTransaction has completed」並記為回滾 Warning（探測：例外 `close failed`、資料已存在、`CurrentTransaction` 為 null）。修正前更差（回滾例外蓋掉關閉例外）。呼叫端先開連線的下單主路徑不受影響。區分「提交失敗」與「提交後關閉失敗」會改變呼叫端契約，屬新的設計決策，列為 `docs/project-scope.md` §8 待辦 ⑥，另開 change。

## Migration Plan

純程式修正，無 schema、設定或 API 變更；直接部署，回退即還原該 commit。
