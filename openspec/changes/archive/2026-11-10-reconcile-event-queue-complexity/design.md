# Design

## Context

動機見 proposal.md「Why」。現況（已讀 `PurchaseQueueAdmissionService.cs:408-604` 確認）：

- `ReconcileEventQueueAsync(eventId, purchaseQueueRepository, database, now, cancellationToken)` 依序做：讀 Postgres 活躍紀錄並切成三個互斥集合（步驟 1）→ 讀 Redis 鏡像 snapshot，失敗時記 Warning 並 `return`（步驟 2）→ 步驟 3 補 waiting → 步驟 4 補 admitted → 計算 `waitingButAdmittedIds` → 步驟 5 清 admitted 殘留 → 步驟 6 清 waiting 殘留 → 步驟 7 修復放棄的推進決策 → 步驟 8 修復放棄的逾時標記。
- 兩個呼叫端（`:305`、`:394`）只呼叫整個方法，簽章不變即不受影響。
- 三個輔助方法已存在且不動：`TryReconcileOperationAsync`（步驟 3／4／6，Warning-only）、`TryTrackedReconcileOperationAsync`（步驟 5／7／8，連續失敗計數與升級）、`TryRemoveMalformedMemberAsync`（步驟 5／6 的格式損壞 member）。
- 認知複雜度主要來自 6 個巢狀 `foreach` + `if continue` + `if (!Guid.TryParse)` + `await` 條件，累計 28。
- 既有測試：`PurchaseQueueMirrorReconciliationTests`（約 48 處對帳相關）與 `PurchaseQueueAdmissionServiceLeaderElectionTests`；整合測試使用真實 Redis（Testcontainers）。

本 change 設 `skip_specs: true`：沒有任何 Requirement 新增或修改（proposal.md「Capabilities」）。

## Goals / Non-Goals

**Goals:**
- `S3776` 對 `ReconcileEventQueueAsync` 與每個新抽出的方法都不再報警（認知複雜度 ≤ 15），建置警告清單與基準相比只少 `S3776` 一筆。
- 重構前後對帳測試結果一致；下方「不變量」每一條都有測試對應，並以突變驗證證明該測試會失敗。

**Non-Goals:**
- 不改任何對外行為、不新增類別、不改建構子與 DI、不調整 `.editorconfig`。
- 不順手改善被搬動的邏輯（命名、重複的 `ToString()`／`Select`、集合運算等原樣保留）；註解（含審查修正的歷史說明、`design.md Decision N` 引用）隨程式碼搬移，內容不改。
- 不處理其他 Sonar 警告。

## Decisions

### 決策 1：拆成同類別 private 方法，步驟間資料用一個 private sealed record

抽成獨立類別需新增型別與 DI 註冊，而本 change 的目標是零結構變動；方法換掉實作並不會讓業務規則描述改變，無須抽象。

步驟 3、4、5、6、7、8 都需要同一批資料（eventId、database、repository、now、三個 Postgres 集合、兩個 Redis 集合、兩個 Redis key），遠超過 3 個參數，因此以一個 private sealed record（暫名 `ReconciliationRound`）承載；`cancellationToken` 維持獨立參數，不放進 record（維持現況「各步驟 Redis 呼叫不帶 token，只有 repository 呼叫帶」）。步驟 5 另需 `waitingButAdmittedIds`，作為第二個參數傳入。

替代方案：用 `ref struct`／tuple 傳遞。否決：可讀性差，且 record 讓欄位命名與現有變數名一致，搬移時零改名。

### 決策 2：切分與順序（實作可微調命名，不得調整順序）

| 區塊 | 方法 | 留在主方法或抽出 |
| --- | --- | --- |
| 步驟 1：切分 W_pg／A_pg／A_pg_overdue | — | 留在主方法（單純 LINQ，不計入大量複雜度） |
| 步驟 2：snapshot 讀取與失敗 return | — | 留在主方法（`try/catch` + `return`，讓提早結束一眼可見） |
| 步驟 3 | `ReconcileMissingWaitingAsync` | 抽出 |
| 步驟 4 | `ReconcileMissingAdmittedAsync` | 抽出 |
| `waitingButAdmittedIds` 計算 | — | 留在主方法（步驟 5 的前置，順序依賴一眼可見） |
| 步驟 5 | `RemoveStaleAdmittedMembersAsync` | 抽出（需 `waitingButAdmittedIds`） |
| 步驟 6 | `RemoveStaleWaitingMembersAsync` | 抽出 |
| 步驟 7 | `RepairAbandonedPromotionsAsync` | 抽出 |
| 步驟 8 | `RepairAbandonedTimeoutsAsync` | 抽出 |

主方法依 3、4、（計算）5、6、7、8 的原順序 `await` 各方法。每個抽出方法的本體是原迴圈原樣搬移；`TryReconcileOperationAsync` 等呼叫與其 lambda 逐字保留。

### 決策 3：不變量（行為不變準則，取代 Acceptance Criteria）

| # | 不變量 | 守護 |
| --- | --- | --- |
| I1 | 步驟順序為 1→2→3→4→（計算 `waitingButAdmittedIds`）→5→6→7→8 | 突變：對調相鄰步驟（見 tasks 各步驟）；PQLE-REBUILD-002／005 相關測試 |
| I2 | `waitingButAdmittedIds` 在步驟 5 之前以步驟 3／4 同一份 snapshot 計算，且整個集合不論 pending 是否存活都排除在步驟 5 之外 | 突變：從 `legalAdmittedIds` 移除該集合，測試須失敗（約 129、505） |
| I3 | snapshot 讀取失敗時整輪略過（Warning + return），不進入任何步驟 | 既有測試；若無則 tasks 6 的主方法 task 補 |
| I4 | 步驟 3 排除「已在 waiting 或已在 admitted」、步驟 4 只處理未逾時的 admitted、步驟 8 只處理逾時且 Redis 無的紀錄（三個集合互斥） | 各步驟突變 |
| I5 | Warning-only 與 Tracked（升級）兩條錯誤處理路徑的選擇不變：3／4／6 走 `TryReconcileOperationAsync`，5／7／8 走 `TryTrackedReconcileOperationAsync`，格式損壞 member 走 `TryRemoveMalformedMemberAsync` | 既有測試（約 1011、1050、1102、307）；突變：改變呼叫選擇 |
| I6 | 取消語意：`OperationCanceledException` 不被吞，repository 呼叫仍帶原 `cancellationToken`，Redis 呼叫仍不帶 token | 既有測試（約 900、936）；`git diff` 逐行核對 |
| I7 | 全部步驟冪等：連續兩輪無狀態變化時第二輪為 no-op | 既有測試（約 1343、1273） |

### 決策 4：驗證方式——每步驟「突變驗證 → 原樣抽取 → 測試」，TDD 的 RED 標 N/A

沒有新行為，RED 不存在。每個步驟的 task 依序做：(1) 用 `bash scripts/verify-test-evidence.sh mutate` 對該步驟做突變，證明至少一個既有測試失敗（貼完整輸出，包含失敗測試名稱與訊息）；全部通過代表保護網有缺口，先補特徵化測試（在現有碼上通過）再重做突變；(2) 原樣搬移成 private 方法；(3) 跑對帳測試，貼摘要行。

CLAUDE.md 的「RED 還原驗證」（`restore`）在本 change N/A：行為不變重構的還原後測試照樣通過是預期結果，責任由 `mutate` 承擔。`mutate` 一次只做一個改動，且在 mutate 前後 `git diff` 確認工作樹只剩預期差異。

**步驟順序**：由低風險到高風險 8、4、3、6、7、5。步驟 5 最後，因為它耦合最強（依賴 `waitingButAdmittedIds`，歷經最多輪審查修正）；先做簡單的步驟讓搬移手法穩定，主方法逐漸縮短。

### 決策 5：複雜度量測方式

`S3776` 警告訊息帶實際值（基準：`Refactor this method to reduce its Cognitive Complexity from 28 to the 15 allowed`），`.editorconfig:124` 設為 warning。以 `docker compose exec -T api dotnet build --no-incremental` 的 warning 清單判定：重構後不得有任何 `S3776`，且與基準相比不得新增其他 warning。每個新方法的複雜度由 Sonar 對每個方法各自檢查，不另行手算；若某新方法觸發 `S3776`，代表切分仍過粗，需在同一 task 內再拆，不得以 `SuppressMessage` 規避。

## Risks / Trade-offs

- **搬移時誤改條件或順序**：風險集中在步驟 5。以突變驗證加 `git diff` 逐行比對緩解，審查迴圈要求對抗式 reviewer 專門核對「搬移前後逐行等價」。
- **record 欄位與原區域變數不一致**：欄位名沿用原變數名（`waitingPg`、`admittedRedis` 等），降低搬移差異。
- **複雜度只是被拆開**：步驟 5 與 7 的耦合不會消失，只是變成參數傳遞；本 change 接受此取捨，主方法的順序依賴仍可一眼看見。
- **既有測試對某步驟覆蓋不足**：由突變驗證發現，補測試本身就是本 change 的價值；補測試量過大（超過約 3 個新測試）時暫停，與使用者確認是否拆成獨立 change。
- **並行 session**：開發期間另一個 pane 的 session 與主 session 共用同一個工作目錄；實作與每次容器測試期間不得有其他 session 改檔或執行 build。

## Migration Plan

無資料或設定遷移。單次部署即可，回滾為還原 commit。

## Open Questions

無。突變驗證若發現測試缺口，依「Risks」處理。
