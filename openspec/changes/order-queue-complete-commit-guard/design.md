# Design

## Context

- `PlaceOrderCoreAsync` 的順序：`LockQueueEntryAsync`（`GetForUpdateAsync`，實體受 EF 追蹤）→ `CreateOrderHandler.Handle` → `queueEntry?.Complete()` → `_orderRepository.Add` → `transaction.CommitAsync` → 釋放連線 → Redis 鏡像同步（`OrderService.cs:239-286`）。
- `UnitOfWorkTransaction.CommitAsync`：`SaveChangesAsync` → `_transaction.CommitAsync`（`UnitOfWork.cs:154-163`），這是唯一的持久化點；commit 後再改追蹤實體不會被寫入。
- 既有保護：單元測試以 `FakeUnitOfWorkTransaction` 驗證 `Complete()` 被呼叫，但 Fake 不保存快照；`PurchaseQueueAdmissionServiceTests.cs`:502 的端到端測試斷言 `Completed`（可能已守護，待突變判定）。
- 後果推測（**未驗證**，本 change 的突變即為驗證）：`Complete()` 移到 commit 後 → DB 狀態停在 `Admitted`；之後鏡像同步 `SyncCompletionAsync` 仍會把 Redis admitted 鏡像移除，造成 Postgres 與 Redis 不一致，直到對帳修正。

## Goals / Non-Goals

**Goals:**
- 讓「`Complete()` 必須在 commit 之前、且結果落地為 `Completed`」有一個在移動後會失敗的自動化測試守護。
- 以突變驗證證明守護有效，證據貼原文。

**Non-Goals:**
- 不改產品程式碼、不改 spec、不重構既有測試或 helper、不處理 `ReconcileEventQueueAsync` 複雜度。

## Decisions

### 決策 1：先突變、後決定是否新增測試

理由：`PurchaseQueueAdmissionServiceTests.cs`:502 已斷言 `Completed`，直接新增可能重複。先用 `scripts/verify-test-evidence.sh mutate` 把 `Complete()` 暫移到 `CommitAsync` 之後，跑該既有測試：
- 失敗 → 缺口已被守護，不新增測試（避免重複），只更新技術債條目並附證據。
- 通過 → 缺口成立，進入決策 2。

突變須是**單一改動**（`Complete()` 由 commit 前移到 commit 後），multi-line 取代前先確認檔案換行（CRLF／LF），以 `mutate` 的字面取代處理；不得合併其他改動。

### 決策 2：新增測試的位置與形式（僅缺口成立時）

放在 `tests/ProjectC.Infrastructure.Tests/PurchaseQueue/OrderServiceQueueModeLinearizationTests.cs` 同組（或同資料夾新檔，若該檔職責不符則另開 `OrderServiceQueueEntryCompletionTests.cs`；實作時依檔案職責決定並記錄）。使用 `OrderServiceTestFactory.Create(dbContext)` 組真實 `OrderService` 與 `UnitOfWork`，用 `PostgresFixture`。情境：計數票種活動、開啟排隊模式、買家有一筆 `Admitted` 且未過期的 `PurchaseQueueEntry`；下單成功後以**新的** `DbContext` 讀回該筆紀錄，斷言 `Status == Completed`。

用新 `DbContext` 讀取的理由：同一 `DbContext` 會回傳追蹤中的記憶體實體，Complete() 移到 commit 後仍會讀到 `Completed`，無法區分是否落地。

替代方案：在單元測試的 Fake 加快照語意。否決：Fake 無法忠實模擬 SaveChanges 時點，維護成本高，且正是原缺口的成因。

### 決策 3：驗證方式（RED 由突變取得）

此為特徵化測試：本 change 不改正式程式碼，行為已正確，測試在現行程式碼下必然通過，字面上的「修改前失敗」不可能出現。RED 改由 `scripts/verify-test-evidence.sh mutate` 取得：暫時把 `Complete()` 移到 `CommitAsync` 之後，測試須因 `Status` 斷言（Expected Completed／Actual Admitted）失敗，失敗訊息貼原文；還原後（`touch` 並重跑）通過即 GREEN；重構 N/A。這是 CLAUDE.md 既有的「暫時改動正式程式碼的驗證用 `mutate`」，不另立 TDD 例外。CLAUDE.md 的 `restore` 還原驗證 N/A：本 change 沒有正式程式碼修改可還原。

## Risks / Trade-offs

- 突變須在容器內跑測試且 `bin/obj` 在 named volume；`mutate` 結束後確認 `git status --short src/` 為空。`mutate` 以 `cp -p` 還原會連 mtime 一併還原，MSBuild 不會重編而沿用突變版 DLL，因此 tasks 1.1／2.2 明列 `touch` 與重跑通過的步驟。
- 若移動後 EF 因其他路徑仍持久化，突變可能不失敗（已讀 `SyncAfterCommitAsync` 只呼叫 Redis 鏡像與快取失效，預期不會，但以實測為準）；此時如實記錄「推測不成立」，技術債條目改寫為實測結論，不為了有失敗而調整測試。
