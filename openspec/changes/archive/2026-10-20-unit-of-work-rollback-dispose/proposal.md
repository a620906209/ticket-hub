# Proposal

## Why

`IUnitOfWorkTransaction` 的契約是「未提交也未明確回滾的交易，在釋放時自動回滾」。目前實作在釋放時先回滾、再釋放交易；回滾一旦拋例外，釋放交易的步驟就被跳過，交易物件與其占用的資料庫連線會留到 `DbContext`（request scope）結束才釋放，且回滾例外會從 `await using` 的釋放動作傳出，蓋掉呼叫端原本正在處理的例外（例如下單流程中的業務例外）。這是 `docs/project-scope.md` §8 待辦（2026-10-07）③，至今為推論、未以測試證實；本 change 先以測試證實再修正。

## What Changes

- 自動回滾失敗時，仍一律釋放交易（含其占用的連線資源）。
- 自動回滾失敗的例外以 Warning 記錄，不從釋放動作傳出，與同檔 `UnitOfWorkConnection` 關閉連線失敗的處理一致。
- 釋放交易本身失敗時，同樣記 Warning、不從釋放動作傳出，避免蓋掉原例外。
- 提交失敗後離開交易範圍時，呼叫端看到的是提交例外，交易已釋放。
- 釋放後同一工作單元可再開始新的交易，交易內已寫入的資料不落地。
- 既有「未提交即釋放時自動回滾」行為不變，首次寫入 spec。

## Capabilities

### New Capabilities

- `unit-of-work-transaction`：工作單元交易在釋放時的自動回滾行為，以及自動回滾失敗時的資源釋放、記錄與例外傳遞規則。

### Modified Capabilities

（無）

## Impact

- 程式碼：`src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs`（`UnitOfWorkTransaction` 改為接收 `UnitOfWork` 的 logger；`DisposeAsync` 改為 try/catch/finally）。
- 測試：`tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs` 新增測試（Testcontainers Postgres + EF Core transaction interceptor）。
- 文件：`docs/project-scope.md` §8 待辦 ③ 標記完成。
- 不影響 API、DB schema、前端；`IUnitOfWork` / `IUnitOfWorkTransaction` 介面不變。
