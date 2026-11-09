# Tasks

> 所有指令都在容器內執行（`docker compose exec -T api dotnet ...`），不在本機執行 dotnet。
>
> 本 change 為純重構，無新增 Acceptance Criteria（`skip_specs: true`，見 proposal.md）。以下「行為不變準則」取代 AC（定義見 design.md 決策 3），每一條都對應驗證 task：
>
> | 行為不變準則 | 驗證 |
> | --- | --- |
> | I1：步驟順序 1→2→3→4→（計算 `waitingButAdmittedIds`）→5→6→7→8 | 2.1–2.6 各步驟突變、3.1 |
> | I2：`waitingButAdmittedIds` 在步驟 5 之前計算，整個集合排除在步驟 5 之外 | 2.6 突變 |
> | I3：snapshot 讀取失敗時整輪略過 | 3.1 |
> | I4：步驟 3／4／8 的集合互斥條件 | 2.1、2.2、2.3 突變 |
> | I5：錯誤處理路徑選擇（Warning-only／Tracked／格式損壞） | 2.1–2.6 突變、`git diff` 核對 |
> | I6：取消語意與 `CancellationToken` 傳遞 | 2.1–2.6 `git diff` 核對、4.1 |
> | I7：冪等（連續兩輪第二輪為 no-op） | 4.1 全套測試 |
> | C1：`S3776` 警告消失，且建置警告清單與基準相比只少該一筆 | 4.1 |
>
> 證據一律貼原文（CLAUDE.md「證據貼原文」）：每個失敗測試的名稱與訊息、每個測試專案的摘要行、`mutate` 腳本輸出；不得以數字、「全數通過」或「與 X 相同」代替。
>
> TDD：純重構、無新增可觀察行為，RED 標示 N/A（既有測試在重構前就通過是預期）；「測試在行為被破壞時會失敗」由各步驟的 `mutate` 證明（design.md 決策 4）。`restore`（RED 還原驗證）同樣 N/A：還原後測試照樣通過是行為不變重構的預期結果。未標 TDD 的 task 一律視為 TDD：N/A（理由同上）。
>
> mutate 命令的 `--from` 字串以實際程式碼為準（下列為撰寫提案時的原文），若字串因前一步驟搬移而改變，在完成紀錄說明；mutate 不唯一或找不到時不得改用多處合併套用。
>
> 對帳測試命令（簡稱「對帳測試」）：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~PurchaseQueueMirrorReconciliationTests"`
>
> 前端：無變更，不需真實瀏覽器驗證。
>
> 提交規則：不自動 commit／push；mutate 造成的暫時性改動不得進入 commit。實作與每次容器測試期間，不得有其他 session 同時改檔或建置。

## 1. 重構前基準

- [ ] 1.1 在乾淨工作樹（`git status` 確認）記錄基準：`docker compose exec -T api dotnet build --no-incremental` 的 warning 清單（預期含 `S3776` 一筆，認知複雜度 28 to 15；其餘既有 warning 照列）；`docker compose exec -T api dotnet test` 全套各測試專案摘要行；「對帳測試」摘要行。已知偶發失敗（`RealNameLoggingTests`、`HostShutdownSeqFlushTests`、時間相關測試）失敗時照實記錄並單獨重跑。
  - 完成紀錄：
  - TDD：N/A（基準量測）。

## 2. 各步驟：突變驗證 → 原樣抽取 → 測試（由低風險到高風險）

每個 task 的固定流程：(a) 突變驗證：用 `bash scripts/verify-test-evidence.sh mutate --file src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs --from <原字串> --to <新字串> -- <對帳測試>` 證明至少一個測試失敗，貼完整輸出；若測試仍通過（腳本退出碼 1）代表保護網有缺口，先在 `PurchaseQueueMirrorReconciliationTests.cs` 補特徵化測試（在現有碼上通過）再重做 (a)，補超過約 3 個新測試時暫停回報；(b) 原樣搬移成 private 方法，主方法改為 `await` 該方法（參數用 `ReconciliationRound` record，首次需要時於 2.1 建立）；`git diff` 確認搬移前後逐行等價（只有方法邊界、縮排與參數存取方式的差異），註解與 lambda 逐字保留；(c) 跑「對帳測試」貼摘要行。

- [ ] 2.1 步驟 8 `RepairAbandonedTimeoutsAsync`（建立 `ReconciliationRound` private sealed record，欄位沿用原變數名）。突變：`--from "() => purchaseQueueRepository.ExpireBatchAsync([entryId], cancellationToken));" --to "() => Task.CompletedTask);"`，預期失敗的測試：`…ExpiredBatchUpdate_WhenItThrows…`、`…AbandonedExpiryUpdateFailure…`、`…OverdueAdmittedEntryNeverSyncedToRedis…` 一帶（以實際為準）。
  - 完成紀錄：
  - TDD：N/A（行為不變重構，理由見上）。
- [ ] 2.2 步驟 4 `ReconcileMissingAdmittedAsync`。突變：`--from "database.SortedSetAddAsync(admittedKey, idStr, " --to "database.SortedSetAddAsync(admittedKey + \"x\", idStr, "`，預期失敗：`…OnlyAdmittedMirrorIsMissing…`、`…SimulatedRedisRestart…` 一帶。
  - 完成紀錄：
  - TDD：N/A。
- [ ] 2.3 步驟 3 `ReconcileMissingWaitingAsync`。突變：`--from "database.SortedSetAddAsync(waitingKey, idStr, " --to "database.SortedSetAddAsync(waitingKey + \"x\", idStr, "`，另做一個排除條件突變：`--from "if (waitingRedis.Contains(idStr) || admittedRedis.ContainsKey(idStr))" --to "if (waitingRedis.Contains(idStr))"`（守 PQLE-REBUILD-005，預期 `…AtomicallyPromoted…DoesNotDuplicate…` 失敗）。兩個突變各自獨立執行。
  - 完成紀錄：
  - TDD：N/A。
- [ ] 2.4 步驟 6 `RemoveStaleWaitingMembersAsync`。突變：`--from "() => database.SortedSetRemoveAsync(waitingKey, member));" --to "() => Task.CompletedTask);"`，預期失敗：`…MalformedNonGuidMembers…` 不涵蓋 Guid 孤兒；以實際失敗測試為準，無測試失敗則補「waiting 中查無 Postgres 紀錄的孤兒 entryId 被清除」特徵化測試。
  - 完成紀錄：
  - TDD：N/A。
- [ ] 2.5 步驟 7 `RepairAbandonedPromotionsAsync`。突變：`--from "() => purchaseQueueRepository.AdmitIfWaitingAsync(entryId, now, expiresAtUtc, cancellationToken));" --to "() => Task.CompletedTask);"`，另做 pending 判斷突變：`--from "if (await database.KeyExistsAsync(PurchaseQueueAdmissionRedisKeys.Pending(entryId)))" --to "if (!await database.KeyExistsAsync(PurchaseQueueAdmissionRedisKeys.Pending(entryId)))"`（預期 `…WhilePendingMarkerIsAlive…`、`…OnlyOnePendingMarkerKeyIsMissing…`、`…AbandonedPromotionUpdateFailure…` 失敗）。兩個突變各自獨立執行。
  - 完成紀錄：
  - TDD：N/A。
- [ ] 2.6 步驟 5 `RemoveStaleAdmittedMembersAsync`（最後做；`waitingButAdmittedIds` 的計算與其長註解留在主方法，作為第二個參數傳入）。突變 A：`--from "() => database.SortedSetRemoveAsync(admittedKey, member));" --to "() => Task.CompletedTask);"`（預期 `…StaleAdmittedMirrorEntry…`、`…OrphanAdmittedMirrorMember…`、`…ZRemFailureDuringReconciliation…` 失敗）；突變 B：`--from ".Concat(waitingButAdmittedIds)" --to ".Concat(Enumerable.Empty<string>())"`（守 I2，預期 `…WhilePendingMarkerIsAlive…`、`…AtomicallyPromoted…` 失敗）；突變 C：`--from "await TryRemoveMalformedMemberAsync(admittedKey, member, \"admitted\", database);" --to "continue;"`（預期 `…MalformedNonGuidMembers…` 失敗）。三個突變各自獨立執行。
  - 完成紀錄：
  - TDD：N/A。

## 3. 主方法收尾

- [ ] 3.1 確認主方法只剩步驟 1、snapshot 讀取（含失敗 return）、`waitingButAdmittedIds` 計算與六個 `await`，順序為 3、4、（計算）、5、6、7、8。確認 I3：既有測試是否涵蓋「snapshot 讀取失敗時整輪略過」，用突變驗證（突變必須能編譯：例如把 catch 內的 `return;` 改為 `snapshot = new PurchaseQueueRedisMirrorSnapshot([], new Dictionary<string, double>());` 使其以空 snapshot 繼續執行；僅刪除 `return;` 會因 `snapshot` 未指派產生 CS0165，編譯錯誤不得當作證據），測試仍通過則補特徵化測試。所有 mutate 的失敗須為 `Failed!` 測試斷言失敗，腳本對編譯錯誤會回報無法判定（退出碼 2），不算證據。再做 I1 順序突變：對調步驟 5 與步驟 7 的呼叫行（暫時改動，驗後還原），預期至少一個測試失敗。
  - 完成紀錄：
  - TDD：N/A。

## 4. 驗證與文件

- [ ] 4.1 `docker compose exec -T api dotnet build --no-incremental` 的 warning 清單與 1.1 基準比對：`S3776` 消失、其餘不變且無新增；`docker compose exec -T api dotnet test` 全套各測試專案摘要行（失敗照實記錄並單獨重跑已知偶發項目）；`git diff --stat` 確認只動 `PurchaseQueueAdmissionService.cs`、`PurchaseQueueMirrorReconciliationTests.cs`（若補測試）與文件。
  - 完成紀錄：
  - TDD：N/A。
- [ ] 4.2 更新 `docs/project-scope.md`「已知技術債：佇列對帳方法複雜度」條目：改為已處理（保留歷史評估與完成日期），移除「須另開 change」的未決語氣；依 CLAUDE.md「文件存放規則」不新增文件。驗證：`bash scripts/check-doc-paths.sh` 通過。
  - 完成紀錄：
  - TDD：N/A（純文件）。

## 5. hardener 最終檢查

- [ ] 5.1 程式碼變更全部完成後，依 `.claude/skills/hardener/SKILL.md` 檢查 `PurchaseQueueAdmissionService` 的新方法：本 change 不新增防禦分支（純搬移），此 task 確認既有的參數驗證、`CancellationToken` 傳遞、例外邊界（`when (exception is not OperationCanceledException)`）、日誌遮蔽在搬移後與原碼一致；發現搬移造成的差異才依 TDD 補 RED 測試。
  - 完成紀錄：
  - TDD：N/A（無新增行為；若發現缺口再補）。

## 6. 審查與歸檔

- [ ] 6.1 依 CLAUDE.md「開發流程總覽」步驟 6：`strict-reviewer` 與全新的對抗式 general-purpose subagent 依序審查（只給 change 名稱與審查目標，不給前輪結論；對抗式 reviewer 的審查目標含「逐行核對搬移前後等價」與「步驟 5 排除集合的跨步驟依賴」），或以 `cross-review` skill 走外部審查；直到沒有 blocking。每輪的工具／模型與 commit 記錄於下。
  - 完成紀錄：
- [ ] 6.2 使用者明確同意後 `/opsx:archive`（無 delta spec，skip_specs）並合併回 master。
  - 完成紀錄：
