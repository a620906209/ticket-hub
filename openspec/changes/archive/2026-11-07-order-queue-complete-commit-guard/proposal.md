# Proposal

## Why

`OrderService.PlaceOrderCoreAsync` 在 `transaction.CommitAsync` **之前**呼叫 `queueEntry?.Complete()`（`src/ProjectC.Application/Orders/OrderService.cs:274-280`）。`UnitOfWorkTransaction.CommitAsync` 先 `SaveChangesAsync` 再 commit，之後沒有其他 SaveChanges；`queueEntry` 由 `GetForUpdateAsync` 載入而被 EF 追蹤。若有人把 `Complete()` 移到 `CommitAsync` 之後，推測 DB 內排隊紀錄會停在 `Admitted`，名額要等 `AdmissionExpiresAtUtc` 才釋放（違反 PQ-COMPLETE-001（同一交易內標記 `Completed`）與 PQ-COMPLETE-002（交易提交後名額立即可供下一位使用））；單元測試的 `FakeUnitOfWorkTransaction` 不保存快照，看不出差異。此缺口記錄於 `docs/project-scope.md` §8 技術債（2026-11-06，`place-order-core-complexity` 審查發現）。

規劃時另發現 `PurchaseQueueAdmissionServiceTests.AdvanceQueueOnceAsync_AfterOrderServiceCompletesAnEntry_NextRoundAdmitsTheNextWaitingEntryViaTheRealSyncPath`（`PurchaseQueueAdmissionServiceTests.cs`，`[Fact]` 在 502 行、方法簽名在 503 行；另一檔 `tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs` 的 `OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt` （`[Fact]` 335 行、方法 336 行、`Completed` 斷言 382 行）也斷言 `Completed`）已用真實 `OrderService` 與真實 Postgres 下單並斷言排隊紀錄為 `Completed`，缺口可能已被間接守護，需以突變驗證判定（design.md 決策 1）。

## What Changes

- 先做突變驗證：暫時把 `Complete()` 移到 `CommitAsync` 之後，跑上述既有測試，判定缺口是否已被守護。
- 若既有測試**未**失敗（缺口成立）：在 `tests/ProjectC.Infrastructure.Tests` 新增一個 Testcontainers 整合測試，下單成功後以新的 `DbContext` 讀取排隊紀錄，斷言 `Status == Completed`，並以突變證明它會失敗。
- 若既有測試**已**失敗（已守護）：不新增測試，只把 `project-scope.md` 技術債條目改為「已被既有測試守護」並附突變證據，另在 WebApi.Tests 該既有測試的 (a) 斷言旁補一行「為什麼」註解（僅註解）；第二條路徑下的 tasks 2.x 標示 N/A。
- 不論哪條路徑，完成後更新 `docs/project-scope.md` 該技術債條目。
- **非變更**：不改產品程式碼、不改 spec、不重構測試輔助碼、不處理 `ReconcileEventQueueAsync` 的 `S3776`。

## Capabilities

### New Capabilities
無。

### Modified Capabilities
無。只補測試守護，系統行為不變，因此在 `.openspec.yaml` 設定 `skip_specs: true`；受守護的既有規則為 `purchase-queue` PQ-COMPLETE-001／002。

## Impact

- 程式碼：`tests/ProjectC.Infrastructure.Tests`（缺口成立時新增一個測試方法，位置見 design.md 決策 2）；走「已守護」路徑時僅在 WebApi.Tests 既有測試加一行註解；`src/` 零變動（突變僅暫時套用，`git status --short src/` 須為空）。
- 文件：`docs/project-scope.md` 技術債條目。
- API／設定鍵／套件相依／DI 圖：無變動。
- Change 顆粒度：0 個 capability、約 8 項 task，不需拆分。
