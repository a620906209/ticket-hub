## 1. B1：Event 共享鎖（TP-ORDER-034～036，design 決策 1、4）

- [x] 1.1 先寫測試（`tests/ProjectC.Infrastructure.Tests`，Testcontainers）`GetForShareAsync`：沒有交易時拋 `InvalidOperationException`；兩個交易同時取得同一活動的共享鎖不互等；持有共享鎖時，另一交易的 `GetForUpdateAsync` 會等待（以 `pg_locks` 確認，不用固定睡眠），放行後取得
- [x] 1.2 `IEventRepository` 新增 `GetForShareAsync`（XML doc 說明用途與為何不改既有 `GetForUpdateAsync`）；`EventRepository` 實作 `FOR SHARE`、no-tracking、`EnsureActiveTransaction`
- [x] 1.3 所有 `IEventRepository` 測試替身實作新方法：`FakeEventRepository`、`CountingEventRepository`、`InterceptingEventRepository`（新增 `AfterGetForShareAsync` 掛點）、`OrderServiceQueueModeLinearizationTests` 內的替身，以及 `grep` 找到的其他實作
- [x] 1.4 先寫測試 TP-ORDER-034（A 暫停在共享鎖之後，B 對同活動另一座位**在 A 被放行前**回 201，判準是事件順序，`WaitTimeout` 只防掛住；放行 A 後 A 也回 201；斷言座位 1、座位 2 各恰有 1 筆 `OrderItems`，分屬 A、B 的訂單）與 TP-ORDER-035（A 暫停時切換排隊模式被阻塞，以 `pg_locks` 確認；放行後 A 回 201、切換完成）
- [x] 1.5 新增 TP-ORDER-036 測試（`OrderServiceConcurrencyTests`；查證範圍與結果見 design 決策 4）：兩個獨立 DbContext／`OrderService` 都取得共享鎖後同時放行，下同一座位；斷言一個成功、一個 `SeatNoLongerAvailable` 409、該座位 `OrderItems` 恰 1 筆
- [x] 1.6 `OrderService.PlaceOrderCoreAsync` 改用 `GetForShareAsync`，更新該處「線性化時點」註解說明共享鎖為何足夠（引用本 change design 決策 1）；變異驗證：暫時把 `GetForShareAsync` 改成 `FOR UPDATE`，確認 TP-ORDER-034 失敗（B 卡到逾時）後改回
- [x] 1.7 依 design 決策 4 改寫 TP-ORDER-023（`OrderServiceEarlyConflictTests`）：新增 `InterceptingEventSeatRepository`、`OrderServiceTestFactory` 加可選 `eventSeatRepository` 參數；持鎖者暫停在座位列鎖之後，等鎖者通過提早判斷後卡在座位列鎖（`pg_locks` 確認），放行後持鎖者 201、等鎖者 409、座位 `OrderItems` 恰 1 筆
- [x] 1.8 執行 Application、Infrastructure、WebApi 測試全部通過，特別確認 `OrderServiceQueueModeLinearizationTests`（TP-ORDER-015／016，即 ticket-purchase delta Requirement「切換提交後才取得共享鎖的下單 MUST 讀到新值」的測試對應）、`SetEventQueueModeHandlerConcurrencyTests`、`OrderServiceConcurrencyTests`、`TicketTypeConcurrencyTests`；測試結束後清掉殘留 Testcontainers

## 2. B1 量測（design 決策 3）

- [x] 2.1 確認沒有外部 `dotnet test`、沒有殘留 Testcontainers 後，量測模式（`docker-compose.loadtest-measure.yml`，Release）兩個 scenario 各 1 次，`LT_MEASURE_TAG=after-b1`；每次後執行 `check-measure-logging.sh`，並確認 api log 沒有 `too many clients` 或連線池逾時（出現即視為失敗）、匯出 `measure-after-b1-phases-<scenario>.json`
- [x] 2.2 報告草稿 `docs/load-test/p95-phase2-report.md`：起點（引用第一階段報告）與 B1 變化（EventLockWait、Total、k6 P95）；若 EventLockWait 沒有下降，暫停並回報使用者（design 決策 5）

## 3. B2：每筆下單只借一次連線（TP-ORDER-037、038，LT-MEASURE-001～004 修改，design 決策 2、4）

- [x] 3.1 先寫測試 `UnitOfWork.OpenConnectionAsync`（Infrastructure，Testcontainers）：開啟後連線為 Open；dispose 後 Closed；重複 dispose 安全；已開啟或已有交易時再開拋 `InvalidOperationException`；開啟後 `BeginTransactionAsync`、commit 正常，commit 後 dispose 連線不拋例外；未 commit 時交易 dispose（回滾）後連線仍能關閉
- [x] 3.2 `IUnitOfWork` 新增 `OpenConnectionAsync` 與 `IUnitOfWorkConnection`（XML doc 說明為何需要、使用限制）；`UnitOfWork` 實作；`FakeUnitOfWork` 等 `IUnitOfWork` 替身同步實作
- [x] 3.3 先寫測試 TP-ORDER-037、038（Infrastructure，以 `DbConnectionInterceptor` 計數）：以 `[Theory]` 逐一跑 TP-ORDER-037 列出的每條路徑——成功；交易前拒絕：票種不存在 404、跨活動 400、不在販售期間、未實名、超過張數上限、提早 409；交易內拒絕：鎖定後 409、排隊模式未取得入場資格；交易內例外——每條 `ConnectionOpened` 為 1、結束後連線為 Closed，且斷言回傳的錯誤類型符合該路徑（避免測試資料沒走到預期分支卻通過）；validator 失敗為 0；以已取消的 `CancellationToken` 呼叫（交易前與交易內各一）結束後連線也為 Closed；`IQueryCache.RemoveAsync` 與 `IPurchaseQueueAdmissionMirror.SyncCompletionAsync`（排隊模式路徑）被呼叫時資料庫連線已關閉。在現行程式碼上執行，確認連線次數測試失敗
- [x] 3.4 先寫測試（Application，`OrderServiceTests`），依 design 決策 4 的 LT-MEASURE 對應表逐條修改或新增：`PhaseTimingFields` 加 `ConnectionOpenMs`（001）；validator 失敗為 null、提早 409 有值，新增票種不存在 404 與跨活動 400 兩個測試（002，交易內分段斷言為 null 不是 0）；003 不變；例外路徑補 `ConnectionOpenMs` 有值（004）；新增 `PlaceOrderAsync_WhenWallClockJumpsDuringOrder_PhaseTimingsUnaffected` 與 `OrderService_DoesNotReadWallClockDirectly`（單調時鐘規則，兩者都含變異驗證，見對應表）
- [x] 3.5 `OrderService.PlaceOrderCoreAsync`：validator 之後、第一次查詢之前 `await using` 開啟連線；commit 後、Redis 呼叫前明確 dispose；`PlaceOrderPhaseTimings` 新增 `ConnectionOpened` 並輸出 `ConnectionOpenMs`；更新提早 409 處「不占連線」的註解，改為「不開交易、不等鎖」
- [x] 3.6 `loadtest/export-measure-phases.sh` 欄位清單加入 `ConnectionOpenMs`；更新 `loadtest/README.md` 分段說明；執行 `loadtest/tests/` 既有自我測試
- [x] 3.7 執行 Application、Infrastructure、WebApi 測試全部通過，特別確認提交後 Redis 失敗語意未變：依 design 決策 2 的 TP-ORDER-038 對應表執行：`QueryCachingFailOpenTests.PlaceOrder_WithCountingSelection_WhenRedisUnreachable_StillSucceedsAndLogsWarning`（QC-FAIL-002d）、`PurchaseQueueMirrorReconciliationTests.OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt`（PQ-COMPLETE-003）；連線已關閉的斷言由 3.3 覆蓋；清掉殘留 Testcontainers

## 4. B2 量測、切換延遲探測與驗收工具（design 決策 2、3；LT-REPORT-007）

- [x] 4.1 量測模式兩個 scenario 各 1 次，`LT_MEASURE_TAG=after-b2`；同 2.1 的檢查與匯出
- [x] 4.2 判定 B2 是否採用（design 決策 5）：Total p95 或 k6 P95 比 B1 慢時，依決策 5 回退 B2（程式碼、測試、delta spec 的 TP-ORDER-037／038 與 MODIFIED LT-MEASURE、匯出欄位），load-testing 報告 Requirement 標題拿掉「單一連線」，proposal／design 標註未採用，重跑 3.7 確認測試通過
- [x] 4.3 座位票量測期間（B2 回退時在 B1 狀態），500 筆送出後立即以 admin API 切換排隊模式開、關各一次（沿用 `loadtest/lib/admin-api.js` 建立活動時所用的 organizer token，該活動屬於此 organizer）（客戶端逾時 30 秒）；任一次超過 3 秒即 B1 不可接受，暫停回報使用者（design 決策 3）；若觀察到入場推進或加入排隊延遲也記錄
- [x] 4.4 報告記錄 B2 變化（ConnectionOpen、PreTransaction、BeginTransaction、Total、k6 P95）、B1／B2 採用判定與切換延遲判定（這幾項是判讀結果，不做自動化檢查，由 5.2 審查與使用者確認）
- [x] 4.5 先寫測試 `loadtest/tests/check-baseline-reports.test.sh`（LT-REPORT-007 基準檔契約的自動化覆蓋，檔頭標明）：在暫存 git repository 建立 master 與 feature 分支，驗證：`record` 寫出有效的 `MASTER_SHA`、`BASELINE`、`START_HEAD`，且 `BASELINE` 為兩者的 merge-base；`verify` 在基準檔不存在、缺欄位、任一 SHA 無效、`BASELINE` 與 merge-base 不符時失敗；`record` 後 master 前移，`verify` 仍使用保存的 SHA 並通過；feature 上有 commit 觸及任一份第一階段報告（含改後又復原）時失敗；工作樹或 index 修改報告時失敗；成功時輸出的三個 SHA 與基準檔一致並列出當下 HEAD。在腳本不存在時執行確認失敗
- [x] 4.6 新增 `loadtest/check-baseline-reports.sh`（`record`／`verify` 兩個子命令，基準檔路徑與兩份報告路徑可用參數覆寫以便測試；`verify` 不得解析 `master`），讓 4.5 通過

## 5. 審查

> 審查是流程檢查，不能取代上面各 AC 對應的自動化測試。

- [x] 5.1 依 `.claude/skills/hardener/SKILL.md` 檢查 `OrderService`、`UnitOfWork`、`EventRepository` 的變更
- [x] 5.2 strict-reviewer 加獨立的 adversarial 審查（範圍含 4.5／4.6 的 `check-baseline-reports.sh` 與其測試），修正到通過；審查清單明列：分段時間點只用 `Stopwatch.GetTimestamp()`（第二層防線，自動化驗證見 3.4）

## 6. 正式驗收與文件（LT-REPORT-007、008）

- [x] 6.0 固定基準：執行 `bash loadtest/check-baseline-reports.sh record`（建立 `loadtest/.output/p95-phase2-baseline.txt`）；接著執行 `bash loadtest/tests/check-report-tables.test.sh` 與 `aggregate-flow.test.js`（`check-report-tables.sh`／`aggregate.js` 本次不改，這兩個既有自動化測試已涵蓋：標記區段比對、數字竄改被擋、只讀 12 個白名單檔名、未達標缺 `unmet-reason` 區段或區段缺 `measure-` 檔名／毫秒數時失敗），確認全部通過後才把它們當 LT-REPORT-007／008 的判定工具。AC → 既有測試對應（檔頭標的是第一階段的 LT-REPORT-005／006，判定邏輯與本 change 相同，故重用、不改檔）：LT-REPORT-007 的資料表比對 → `check-report-tables.test.sh` 的 LT-REPORT-005 區塊（表格相同通過、改一個數字失敗、截斷／缺標記／重複標記／缺彙整檔失敗）與 `aggregate-flow.test.js`（只讀 12 個白名單檔名）；LT-REPORT-007 的基準檔契約 → 4.5；LT-REPORT-008 → `check-report-tables.test.sh` 的 LT-REPORT-006 區塊（Release P95 恰為 500.00 或缺值判定未達標、缺 `unmet-reason` 區段失敗、區段屬於錯的 scenario 失敗、缺 `measure-` 檔名失敗、缺毫秒值失敗、證據在區段外不算、缺「P95 < 500ms」門檻字樣失敗）
- [x] 6.1 先把第一階段 12 份白名單 summary 與 `report-tables.md` 移到 `loadtest/.output/p95-optimization-phase1/`；再照 `loadtest/README.md` 執行 12 次正式驗收（Debug／Release × 數量票／座位票各 3 次），執行前確認沒有外部測試與殘留容器
- [x] 6.2 彙整資料表貼入報告標記區段；未達標 scenario 寫 `unmet-reason` 區段；執行 `check-report-tables.sh docs/load-test/p95-phase2-report.md loadtest/.output/report-tables.md` 通過（exit 0）；執行 `bash loadtest/check-baseline-reports.sh verify` 通過（exit 0），輸出貼入報告的驗收紀錄
- [x] 6.3 `docs/project-scope.md` §8 更新 P95 結果與後續待辦（未達標時列出 Redis 預扣閘門候選；另列既有風險「下單選購筆數無上限、票種逐筆查詢」與「`UnitOfWorkTransaction.DisposeAsync` rollback 拋例外時未 dispose 交易」「入場鏡像同步遇取消照常拋出沒有測試」為後續待辦）
- [x] 6.4 對照 spec 與 design 確認實作一致，偏差處更新文件
