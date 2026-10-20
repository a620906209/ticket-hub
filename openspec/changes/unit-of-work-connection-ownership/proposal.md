# Proposal

## Why

工作單元以連線目前是否開啟（`GetDbConnection().State`）推斷連線歸屬，連線因失敗而停在開啟狀態時會連鎖出錯：

- 開始交易失敗（含取消）時，工作單元與 EF 為此開啟的連線不會關閉，留到 `DbContext` 釋放（request 結束）。這是 `docs/project-scope.md` §8 待辦 ⑦，`unit-of-work-commit-close-failure` 實作時發現，修正前即存在（該 change design 決策 3）。
- 連線因開始交易失敗或提交／回滾後關閉失敗而停在開啟狀態時，同一工作單元之後：
  - 開始交易會判定連線屬於呼叫端，改由 EF 在提交時關閉；關閉再失敗時，`CommitAsync` 會在資料已落地後拋出例外，重現 §8 ⑥（前一 change 第 2 輪對抗式審查探測證實）。
  - `OpenConnectionAsync` 會拋出「A connection is already open」，下單主路徑（`OrderService`）因此失敗。

## What Changes

- 工作單元自行記錄是否有呼叫端透過 `OpenConnectionAsync` 開啟、且尚未釋放的連線，不再以連線狀態推斷歸屬（`OpenConnectionAsync` 是唯一的手動開啟入口）。
- 沒有呼叫端連線時，開始交易所用的連線一律由工作單元負責，包含先前失敗遺留的開啟連線；提交／回滾後的關閉一律由工作單元執行，關閉失敗只記 Warning（沿用既有規則）。
- 由工作單元負責連線時，開始交易失敗（含取消）會關閉該連線，再把開始交易的原例外傳給呼叫端；關閉失敗記 Warning，不蓋掉原例外。
- `OpenConnectionAsync` 遇到先前失敗遺留的開啟連線時不再拋出例外，改為接手，於呼叫端釋放時關閉；已有呼叫端連線或進行中交易時仍拋出。
- 呼叫端自己開啟連線時，開始交易失敗不關閉呼叫端的連線（維持修正前行為；EF 的開啟計數殘留可能讓該連線留到 `DbContext` 釋放，明載於 spec）。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `unit-of-work-transaction`：新增「開始交易失敗時關閉工作單元負責的連線」（UOW-TX-018～021）與「連線歸屬依工作單元的記錄判斷」（UOW-TX-022～028）兩個 Requirement；既有 Requirement 與 Scenario 不變。主 spec 的 Purpose 只描述釋放行為，歸檔時手動改為涵蓋開始、提交／回滾、釋放三個階段的連線管理（delta 無法修改 Purpose，見 design 決策 5）。

## Impact

- 程式碼：`src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs`（`UnitOfWork` 記錄呼叫端連線；`BeginTransactionAsync`、`OpenConnectionAsync` 改用該記錄判斷；開始交易失敗時的關閉；`UnitOfWorkConnection` 釋放時通知工作單元）。
- 測試：`tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs`、`UnitOfWorkOpenConnectionTests.cs` 新增測試，`TestSupport` 新增可切換的關閉失敗 interceptor；既有 `UnitOfWorkTests` 20 個與 `UnitOfWorkOpenConnectionTests` 9 個維持通過。
- 文件：`docs/project-scope.md` §8 待辦 ⑦ 標記完成；`src/ProjectC.Application/Common/Interfaces/IUnitOfWork.cs` 中 `OpenConnectionAsync` 的 XML doc（「連線已開啟…時 MUST 拋出」）改為「已有呼叫端連線或進行中交易時拋出」。
- 不影響 API、DB schema、前端；`IUnitOfWork` / `IUnitOfWorkTransaction` / `IUnitOfWorkConnection` 介面簽章與呼叫端程式碼不變。
- `OpenConnectionAsync` 行為契約改變：連線因先前失敗而開啟時，由拋出 `InvalidOperationException` 改為接手並正常返回。
