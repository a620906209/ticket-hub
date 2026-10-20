# Proposal

## Why

交易自行開啟資料庫連線時（呼叫端未先透過 `IUnitOfWork.OpenConnectionAsync` 開啟），EF Core 在提交成功後會於同一個 `CommitAsync` 呼叫內接著關閉連線。關閉失敗時，例外從 `UnitOfWorkTransaction.CommitAsync` 傳出：資料已落地，呼叫端卻視為提交失敗（全域例外處理回 500），且 `_completed` 維持 false，釋放時的自動回滾再記一筆誤導的回滾 Warning。正式程式碼 11 處 `CommitAsync` 呼叫端中，只有下單主路徑（`OrderService`）先開連線，其餘都走交易自行開啟連線的路徑。這是 `docs/project-scope.md` §8 待辦 ⑥（`unit-of-work-rollback-dispose` 審查時以暫時探測測試證實）。

## What Changes

- 開始交易時若連線尚未開啟，由工作單元先開啟連線再開始交易；EF 交易的提交與回滾不再關閉連線，改由工作單元關閉（見下一點）。
- 由工作單元開啟的連線，在提交或回滾成功後立即關閉（不占用連線等待提交後的 Redis 等 I/O），關閉失敗記 Warning、不從提交／回滾傳出；未完成的交易於釋放時關閉；重複釋放不再關閉。
- 提交成功後，`CommitAsync` 不再因關閉連線失敗而拋出例外；資料已落地，不記回滾 Warning。`RollbackAsync` 同理（修正前有同類問題）。
- 呼叫端已先開啟連線時，行為不變：不重複開啟、不負責關閉。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `unit-of-work-transaction`：新增「提交或回滾完成後立即關閉連線且關閉失敗不外傳」（UOW-TX-009、010、013、015、016）；修改「釋放交易時的失敗不外傳」中連線關閉的時點（未完成的交易於釋放時由工作單元關閉、重複釋放不再關閉）、UOW-TX-003、005、008 的描述及 UOW-TX-006 的範圍（限定可確定未提交），並新增 UOW-TX-014（提交前儲存變更失敗）、UOW-TX-017（重複釋放）。

## Impact

- 程式碼：`src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs`（`BeginTransactionAsync` 視連線狀態先開啟連線；`UnitOfWorkTransaction` 記錄是否擁有連線，提交／回滾成功後或 `DisposeAsync` finally 最後關閉一次）。
- 測試：`tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs` 新增測試；既有 `UnitOfWorkTests` 13 個與 `UnitOfWorkOpenConnectionTests` 9 個（共 22 個）維持通過。
- 文件：`docs/project-scope.md` §8 待辦 ⑥ 標記完成；新增待辦「開始交易失敗時連線留到 DbContext 釋放」（既有行為，本 change 不處理，見 design 決策 3）。
- 不影響 API、DB schema、前端；`IUnitOfWork` / `IUnitOfWorkTransaction` 介面簽章與 11 個呼叫端程式碼不變。
- `CommitAsync`／`RollbackAsync` 行為契約改變：成功後關閉連線失敗，由拋出例外改為正常返回並記 Warning。
