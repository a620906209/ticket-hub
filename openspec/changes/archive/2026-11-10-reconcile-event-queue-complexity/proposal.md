# Proposal

## Why

`PurchaseQueueAdmissionService.ReconcileEventQueueAsync`（`src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs:411-567`，約 157 行）認知複雜度 28，超過 SonarAnalyzer 的 `S3776` 門檻 15（`.editorconfig:124` 設為 warning），CI 每次建置都出現此警告。方法依 purchase-queue-redis-admission design.md Decision 5 的對帳步驟 3～8 循序撰寫，並在多處註解中記錄歷輪審查修正（第十輪、本輪審查修正、strict-reviewer 事後審查修正），日後修改任何一步都需要整段讀完才能確認不破壞步驟間依賴（最關鍵的是步驟 5 排除「候選過渡態集合」）。`docs/project-scope.md` 已將此列為已知技術債，並註明處理須另開 change；2026-11-10 評估後決定處理，因現有 `PurchaseQueueMirrorReconciliationTests` 對各步驟已有密集覆蓋（約 48 處 `Reconcil`／`PQLE-REBUILD` 相關），可在保護網下做純機械拆分。

## What Changes

- 把 `ReconcileEventQueueAsync` 拆成同一個類別內的 private 方法：步驟 3、4、5、6、7、8 各一個；主方法保留步驟 1（切分 W_pg／A_pg／A_pg_overdue）、步驟 2 的 snapshot 讀取（含失敗時提早 return）、`waitingButAdmittedIds` 的計算，並依原順序串接各步驟。
- 步驟間共用資料以一個 private sealed record 傳遞（參數超過 3 個，CLAUDE.md「Function 設計原則」）；不新增類別、不改建構子、不動 DI。
- 每個步驟先以 `verify-test-evidence.sh mutate` 證明既有測試在該步驟被破壞時會失敗（缺口才補特徵化測試），再原樣搬移，每步都重跑對帳測試。
- 完成後更新 `docs/project-scope.md`「已知技術債：佇列對帳方法複雜度」條目。
- **非變更**：不改 Redis 鏡像語意、步驟順序、條件、log 訊息與等級、`Try*` 輔助方法的選擇與實作、`CancellationToken` 傳遞方式（含「Redis 呼叫本來就不帶 token」的現況）；不處理其他 Sonar 警告；不調整 `.editorconfig` 門檻。

## Capabilities

### New Capabilities
無。

### Modified Capabilities
無。純內部重構，Redis 鏡像對帳的對外行為不變，因此在 `.openspec.yaml` 設定 `skip_specs: true`；行為不變的證據由 design.md 的不變量清單與 tasks.md 的突變驗證承擔，不為通過驗證而發明 Requirement（比照 `place-order-core-complexity`）。

## Impact

- 程式碼：`src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs`（僅 `ReconcileEventQueueAsync` 內部拆分；`TryReconcileOperationAsync`、`TryRemoveMalformedMemberAsync`、`TryTrackedReconcileOperationAsync`、`ReadRedisMirrorSnapshotAsync` 等其餘成員不動）。
- 測試：`tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs` 視突變驗證發現的缺口補特徵化測試，其餘測試檔不動。
- 文件：`docs/project-scope.md` 技術債條目。
- API／設定鍵／套件相依／DI 圖：無變動。
- Change 顆粒度：0 個 capability、tasks 約 12 項，未達拆分門檻，不需拆分。
