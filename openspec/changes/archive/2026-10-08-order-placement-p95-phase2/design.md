## Context

- 起點：`order-placement-p95-optimization`（封存於 `openspec/changes/archive/2026-10-06-order-placement-p95-optimization/`）的報告 `docs/load-test/p95-optimization-report.md`。
  - Release P95 中位數：座位票 711.04ms、數量票 814.45ms。
  - 量測模式 500 筆（`measure-after-a-phases-*.json`）：InLock 中位數 < 1ms；PreTransaction p95 411.6（座位）／664.2ms（數量）；BeginTransaction p95 620.6／548.0ms；EventLockWait p95 298.8／296.7ms。
- 現況下單流程（`OrderService.PlaceOrderCoreAsync`）：
  - 交易前：no-tracking 讀取票種（每個票種 1 次）、活動座位（批次）、活動、實名（需實名的活動才讀）、座位樣板（批次）。數量票 2 次查詢，座位票 4 次。
  - EF Core 預設每次查詢自己開關連線，所以每次查詢都向 Npgsql 連線池借還一次；`BeginTransactionAsync` 再借一次，持有到 commit。
  - 交易內：`IEventRepository.GetForUpdateAsync`（`FOR UPDATE`）→ 排隊資格 `FOR UPDATE` → `EventSeat` `FOR UPDATE`（`ORDER BY "Id"`）→ `TicketType` `FOR UPDATE`（`ORDER BY "Id"`）→ commit → Redis 入場鏡像同步、票種快取失效。
- 以 `FOR UPDATE` 鎖 Event 的路徑共四條（`grep GetForUpdateAsync` 於 `IEventRepository` 呼叫端）：
  - `OrderService.PlaceOrderCoreAsync`：本次改成 `FOR SHARE`，只讀 `IsQueueModeEnabled`。
  - `SetEventQueueModeHandler`：**唯一寫入者**，`FOR UPDATE` 後 `Update()`，EF 對整列 `UPDATE`。`Event` 唯一可變欄位是 `IsQueueModeEnabled`（`EnableQueueMode`／`DisableQueueMode`）。
  - `JoinPurchaseQueueHandler`、`PurchaseQueueAdmissionService`：只讀 `IsQueueModeEnabled`，不改 Event。
  - 後三條本次都不改，仍是 `FOR UPDATE`，都會與下單的共享鎖互斥（見 Risks「寫入者飢餓」）。
- 不經 Event 鎖、會鎖座位／票種的路徑：`ChangeOrderStatusAsync`（確認、取消、逾時取消），順序同樣是 `EventSeat` → `TicketType`。
- `rate-limiting-queue` design.md 決策 4 選 `FOR UPDATE` 的理由，是讓下單跟切換排隊模式的寫入互斥；同一段也記錄「若未來量測到這是效能瓶頸」再改。下單本身不修改 Event。
- 連線上限：Npgsql 連線池預設 100、PostgreSQL `max_connections` 100。上一個 change 使用者決定不調整，本次維持。

## Goals / Non-Goals

**Goals:**

- 下單改用共享鎖鎖 Event，讓同一活動不同座位／票種的下單可以同時進行，並保住跟切換排隊模式的線性化。
- 每筆下單只向連線池借一次連線，消除交易前查詢與開交易之間的重複排隊。
- 以量測證明每一步的效果，正式驗收照舊。

**Non-Goals:**

- 連線池大小、`max_connections`、PostgreSQL 耐久性設定。
- Redis 預扣庫存閘門或其他架構性改變。
- `JoinPurchaseQueueHandler`、`PurchaseQueueAdmissionService`、`SetEventQueueModeHandler` 的鎖定方式。
- 合併或減少交易前查詢的次數：B2 之後這些查詢共用同一條連線，次數對連線池排隊不再有影響，只剩來回延遲（每次約 1ms 以下），不值得改動已被審查過的驗證順序。

## Decisions

### 決策 1：下單以 `FOR SHARE` 鎖 Event（B1）

- `IEventRepository` 新增 `GetForShareAsync(Guid eventId, CancellationToken)`：
  - SQL 為 `SELECT * FROM "Events" WHERE "Id" = {eventId} FOR SHARE`，no-tracking。
  - 沒有進行中的交易時 MUST fail fast，跟 `GetForUpdateAsync` 一樣呼叫 `EnsureActiveTransaction`。
  - 既有 `GetForUpdateAsync` 保留不改，其他三個呼叫端沿用。
  - 使用 Raw SQL 的理由：EF Core 沒有對應 `SELECT ... FOR SHARE` 的 LINQ API（同既有 `GetForUpdateAsync` 的註解）。以 `FromSqlInterpolated` 撰寫，`{eventId}` 由 EF 轉成 Npgsql 參數，不是字串拼接。
- `OrderService.PlaceOrderCoreAsync` 改呼叫 `GetForShareAsync`；鎖定順序仍是 Event → PurchaseQueueEntry → EventSeat → TicketType。
- 為什麼線性化仍成立：
  - PostgreSQL 列鎖相容性：`FOR SHARE` 與 `FOR SHARE` 相容；與 `FOR UPDATE`、`FOR NO KEY UPDATE`（一般 `UPDATE` 取得的列鎖）互斥。
  - 下單持有共享鎖期間，`SetEventQueueModeHandler` 的 `GetForUpdateAsync` 會等待，因此切換無法在下單讀到值之後、提交之前生效。
  - 切換先提交時，下單在 Read Committed 下對鎖定列讀到的是最新提交的版本，一定是新值。
  - 這跟原本 `FOR UPDATE` 提供的兩個保證相同，差別只在下單彼此不再互斥。
- 為什麼不改用樂觀鎖（`RowVersion`）：決策 4 當時提過，但樂觀鎖要在 commit 時比對版本並處理衝突重試，會改變錯誤語意與 API 行為；共享鎖不需要任何行為改變。
- 為什麼超賣保證不變：座位與票種仍以各自 `FOR UPDATE` 鎖定後判斷，`CreateOrderHandler` 仍是唯一權威；Event 鎖從來不是超賣的保護（`ChangeOrderStatusAsync` 本來就不經 Event 鎖）。
- 死鎖檢查：
  - 下單之間：原本 Event 互斥讓它們實質上不會同時持有座位／票種鎖；改成共享後會同時持有，順序由 `ORDER BY "Id"` 與「先座位、後票種」保證一致，`ChangeOrderStatusAsync` 也是同一順序。
  - 下單與切換排隊模式／加入排隊／入場推進：這三者先取得 Event `FOR UPDATE`，與下單的共享鎖互斥，兩邊不會同時持有 Event 之後的鎖，不形成循環。
  - 下單不會把共享鎖升級成排他鎖（不修改 Event），沒有「兩個共享持有者同時要升級」的死鎖。

### 決策 2：每筆下單只借一次連線（B2）

> **B2 採用（2026-10-07 量測，design 決策 5）**：控制暖機後（每版 force-recreate api、座位票與數量票各暖身 1 次後才量），`after-b2-warm` 對比 `after-b1-warm`：座位票 Total p95 359.0 → 197.8ms、k6 P95 384.54 → 235.47ms；數量票 Total p95 481.5 → 372.9ms、k6 P95 496.44 → 380.78ms。第一次 `after-b1`／`after-b2` 量測因兩版各有一個 scenario 是 api 重啟後第一次執行（冷啟動），一度誤判數量票變慢並回退，已作廢，見決策 3 的冷啟動控制與報告第 3 節。

- `IUnitOfWork` 新增 `OpenConnectionAsync(CancellationToken)`，回傳 `IAsyncDisposable`（命名 `IUnitOfWorkConnection`）：
  - 實作呼叫 `DbContext.Database.OpenConnectionAsync`；EF Core 對使用者自己開啟的連線不會在每次查詢後關閉。
  - `DisposeAsync` 呼叫 `CloseConnectionAsync`，連線回到連線池；重複 dispose 不做事。
  - 已有進行中的交易（`Database.CurrentTransaction != null`）或連線已開啟（`Database.GetDbConnection().State != Closed`）時 MUST 拋 `InvalidOperationException`，避免巢狀使用造成提早關閉。下單的 DbContext 是 request scope，進入 `PlaceOrderCoreAsync` 前沒有其他程式碼開啟連線；若未來有，這個檢查會讓它 fail fast 而不是靜默共用。
  - `IUnitOfWork` 的測試替身（`FakeUnitOfWork` 等）同步實作。
  - `IUnitOfWorkConnection.DisposeAsync` 不得讓關閉失敗蓋掉原本的例外（例如取消造成的 `OperationCanceledException`）：關閉失敗時以結構化 log 記錄後不再拋出，理由寫在註解（連線損毀時 Npgsql 會直接丟棄，不回連線池，伺服器端交易自動回滾、鎖釋放）。
  - 宣告順序固定為先 `await using` 連線、後 `await using` 交易，例外、取消、提早 return 時才會先回滾交易再關連線。
  - 專案目前沒有啟用 `EnableRetryOnFailure`（`Program.cs` 只有 `UseNpgsql`）。XML doc 註明：若未來啟用，手動開連線加使用者交易要包在 `CreateExecutionStrategy().ExecuteAsync` 內，否則 EF 會拋例外。
  - 既有 `UnitOfWorkTransaction.DisposeAsync` 在 rollback 拋例外時不會執行 `_transaction.DisposeAsync`，屬於既有問題，不在本次範圍，於 project-scope §8 列為後續待辦。
- `PlaceOrderCoreAsync` 在第一次資料庫查詢（`ticketTypeRepository.GetByIdAsync`）之前開啟，以 `await using` 確保所有提早 return 與例外路徑都會歸還。validator 不查資料庫，放在開啟之前，驗證失敗不佔連線。
- commit 之後、Redis 入場鏡像同步與快取失效之前，明確 dispose 連線，讓 Redis 呼叫不佔資料庫連線。commit 後 EF 已清除 `CurrentTransaction`，關閉連線不影響交易物件的 dispose（實作時以測試確認）。
- 提交後 Redis 呼叫的失敗語意**沿用既有規格，本次不改**，B2 只改變呼叫當下是否持有資料庫連線：
  - 票種快取失效：`RedisQueryCache.RemoveAsync` 攔截 `RedisException`／`RedisTimeoutException`，記 Warning，下單仍回 201（`query-caching` QC-FAIL-002）。
  - 入場鏡像同步：`RedisPurchaseQueueAdmissionMirror.SyncCompletionAsync` best-effort，攔截取消以外的例外並記 Warning，名額由背景校正收斂（`purchase-queue` PQ-COMPLETE-003）。
  - 兩者都不重試、不補償；取消（`OperationCanceledException`）照常往外拋，這是既有行為：訂單已提交，客戶端斷線本來就收不到回應。
  - TP-ORDER-038 的 AC → 測試對應（既有測試不重寫，只列追溯）：
    | 項目 | 測試 |
    |---|---|
    | Redis 呼叫時資料庫連線已關閉 | 本 change 新增，task 3.3 |
    | 快取失效失敗記 Warning、下單仍 201（QC-FAIL-002） | `QueryCachingFailOpenTests.PlaceOrder_WithCountingSelection_WhenRedisUnreachable_StillSucceedsAndLogsWarning`（QC-FAIL-002d） |
    | 鏡像同步失敗 best-effort、名額由校正收斂（PQ-COMPLETE-003） | `PurchaseQueueMirrorReconciliationTests.OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt` |
  - 取消（`OperationCanceledException`）明確排除在 TP-ORDER-038 驗收範圍外：B2 不改這部分。現況供參考：快取失效遇取消照常拋出，有 `RedisQueryCacheTests.RemoveAsync_WhenCancellationAlreadyRequested_ThrowsWithoutFailingOpen`；鏡像同步遇取消照常拋出，目前沒有測試（2026-10-07 查證），列入 project-scope §8 後續待辦。
- 為什麼有效：Npgsql 連線池的等待是先到先得。現況每筆請求在交易前與開交易時重複排隊，每次都排到 500 筆的隊尾，尾端延遲被放大 3～5 倍；改成只排一次後，拿到連線的請求可以一路做完。連線總佔用時間只多了交易前查詢之間的應用程式處理時間（微秒到毫秒級）。
- 量測影響：等待連線池的時間會從 BeginTransaction 移到 PreTransaction 的開頭。分段 log 新增 `ConnectionOpenMs`（開始到連線開啟完成，屬於 PreTransaction 的一部分；validator 失敗時為 null），PreTransaction 的定義不變（開始到開啟交易前），第一階段的數字仍可直接比較。`export-measure-phases.sh` 的欄位清單加上 `ConnectionOpenMs`（`phase-stats.jq` 依欄位名稱泛用處理，不需修改）。報告比較時以 Total 與 k6 P95 為主。
- 若 B2 的量測 Total p95 或 k6 P95 比 B1 慢：回退 B2，見決策 5。

### 決策 3：量測與驗收流程

- 起點不重量，直接引用第一階段報告的正式驗收與 `measure-after-a-*` 分段。
- B1 完成後：量測模式（`docker-compose.loadtest-measure.yml`）兩個 scenario 各 1 次，標籤 `after-b1`；匯出分段（`export-measure-phases.sh`）。
- B2 完成後：同上，標籤 `after-b2`。
- 冷啟動控制（2026-10-07 實測後補）：api 重啟後第一次執行明顯偏慢（同為 B1，座位票 k6 P95 冷 694ms、暖 385ms），差距比 B1／B2 之間還大。B1、B2 的判定量測 MUST 各自 force-recreate api 後先跑座位票、數量票各 1 次暖身（標籤 `<tag>-warmup`，結果捨棄），再量座位票、數量票各 1 次（標籤 `after-b1-warm`、`after-b2-warm`）；每次執行間隔 60 秒。
- 切換排隊模式延遲探測（決策 1 寫入者飢餓風險的量測）：B2 後（B2 回退時則在 B1 狀態）的座位票量測執行中，在 500 筆送出後立即以 admin API 切換一次排隊模式再切回，記錄兩次切換的回應時間。
  - 實際執行（2026-10-07 使用者決定）：探測另跑一次座位票量測（B2 採用後的狀態），不放在 B2 判定用的量測中，避免中途開啟排隊模式讓之後的下單改回 403、扭曲 B2 判定數據；暖機後的突發流量只持續約 300ms，主機定時或主機端觸發都晚於流量結束，最後採用 sidecar 容器（共用 db 的 PID namespace、接在 compose 網路上）輪詢 postgres process title，偵測到突發流量即在容器網路內送出 PATCH（標籤 `after-b2-probe4`），客戶端逾時 30 秒；sidecar 不開資料庫連線。重疊 MUST 以 Seq 的伺服器時間判定（PATCH 請求 log 對照下單分段 log 推回的共享鎖持有區間），不得用主機時間比對，主機與容器時鐘不同步。限制：切換請求與下單共用 api 連線池，切換耗時包含等連線的時間；切換 handler 沒有分段 log，探測只能證明端到端延遲在門檻內，無法把延遲歸因到列鎖不公平（2026-10-07 `after-b2-probe4` 的 222ms 經推算主要是連線池積壓；以「連線池名額已分配完（含建立中的實體連線）、之後近似先到先得」推算 PATCH 最可能在切換開始後 +158～+169ms 拿到連線（寬鬆下限 +123ms），列鎖等待上限約 46～92ms（推論，PATCH 拿到連線的時間未量到），見報告第 4 節）。這次量測不納入分段比較。
  - 門檻：兩次切換都 MUST 在 **3 秒**內完成。依據是第一階段 Release 座位票量測（`measure-after-a-seat-ticket-release-run1-summary.json`）最慢的一筆下單 1300ms：共享鎖持有者最晚在突發流量結束時全部離開，切換最多等到那時候；超過約兩倍代表切換在流量結束後仍等不到鎖，屬於真正的飢餓。
  - 客戶端逾時 30 秒，逾時視為超過門檻。
  - 超過門檻：B1 判定不可接受，暫停並回報使用者，附數據與選項（為切換加 `lock_timeout` 並讓 admin 重試、回退 B1）。不自行選擇。
  - 切換開啟期間若觀察到入場推進或加入排隊延遲，也如實記錄。
- 已知未量測的風險（明確接受，記入報告與 project-scope §8）：k6 是一次性突發，不是持續流量。排隊模式下的持續下單期間，加入排隊（`FOR UPDATE`）與共享鎖持有者重疊的情況沒有壓測場景可量。接受的條件是：排隊模式下同時下單者受入場名額上限限制，持有者之間會有空檔；若之後觀察到加入排隊延遲，後續處理方式同上（`lock_timeout` 或加入排隊改 `FOR SHARE`）。
- 正式驗收：
  - 執行前把第一階段的 12 份白名單 summary 與 `report-tables.md` 移到 `loadtest/.output/p95-optimization-phase1/`（沿用 `k6-load-test-baseline/` 的做法）。k6 遇到同名檔會中止（LT-RELEASE-002），不能覆寫；搬移也保住第一階段報告的可重現性。`aggregate.js` 只讀根目錄白名單，因此彙整的必定是第二階段的 12 份。
  - 照 `loadtest/README.md` 12 次，彙整到 `docs/load-test/p95-phase2-report.md`，以 `check-report-tables.sh` 比對。
- 每次壓測前確認：沒有外部 `dotnet test` 在 api 容器內執行、沒有殘留的 Testcontainers（第一階段 A/B 對照證實這兩者會干擾量測，報告 §6）。

### 決策 4：測試

- `TP-ORDER-034`（整合，Testcontainers）：用 `InterceptingEventRepository` 在買家 A 取得共享鎖後暫停（新增 `AfterGetForShareAsync` 掛點）；買家 B 對同一活動另一座位下單，**在 A 仍暫停時**完成並回 201，再放行 A 也回 201。
  - 判準是事件順序（B 在 A 被放行前完成），不是產品延遲門檻。測試裡的等待上限沿用既有 `WaitTimeout` 常數，只用來避免掛住；若仍是互斥鎖，B 永遠等不到 A 放行，會在 `WaitTimeout` 失敗。Docker 負載只會讓 B 變慢，不會讓 B 在 A 放行前完成，所以不會誤判通過。
  - 變異驗證：在現行程式碼上跑，掛點不會觸發，測試只是卡在等掛點，不能證明能抓到互斥。所以實作 B1 後，暫時把 `GetForShareAsync` 的 SQL 改成 `FOR UPDATE`，確認 034 失敗（B 卡到逾時）再改回。
- `TP-ORDER-035`（整合）：A 暫停在取得共享鎖之後；另一個 scope 執行 `SetEventQueueModeHandler` 開啟排隊模式；以 `pg_locks` 確認切換正在等待（不用固定睡眠，沿用 TP-ORDER-023 的做法）；放行 A 後 A 回 201、切換完成、活動為排隊模式。
- `TP-ORDER-036`（新增，整合）：2026-10-07 查證，搜尋範圍是 `tests/` 下所有呼叫 `PlaceOrderAsync` 且使用 `Task.WhenAll`／`Parallel`／`TaskCompletionSource` 的檔案，共 6 個：
  - `TicketTypeConcurrencyTests`：`PlaceOrderAsync_TwoConcurrentRequestsBuyingLastUnit_...`、`..._TwoDifferentCountingTicketTypes_DoesNotDeadlock`（計數票）、`ConfirmOrderAsync_...`。
  - `OrderServiceConcurrencyTests`：只有 `CancelOrderAsync_TwoConcurrentCancels...`、`ConfirmAndCancel_...`。
  - `OrderServiceEarlyConflictTests`：TP-ORDER-023，同座位，但持鎖者是刻意暫停的序列化構造，不是兩者同時競爭座位列鎖。
  - `RedeemTicketConcurrencyTests`、`PurchaseQueueMirrorReconciliationTests`、`PurchaseQueueAdmissionServiceTests`：與座位下單無關。
  - 結論：沒有「兩筆下單同時競爭同一座位列鎖」的測試。新增：兩個獨立 DbContext／`OrderService`，以 `AfterGetForShareAsync` 掛點讓兩者都取得共享鎖後才同時放行，各自下同一座位；斷言一個成功、一個 `SeatNoLongerAvailable` 409，`OrderItems` 該座位恰 1 筆。B1 前兩者在 Event 鎖就序列化；B1 後兩者會同時競爭座位列鎖，這個測試才真正覆蓋新的並發情境。
- `GetForShareAsync`：沒有交易時拋例外；兩個交易同時取得共享鎖不互等；持有共享鎖時另一交易的 `GetForUpdateAsync` 會等待。
- B2（TP-ORDER-037／038）：
  - 以 `DbConnectionInterceptor` 計算 `PlaceOrderAsync` 的 `ConnectionOpened` 次數：成功、交易外提早 409、交易內 409、交易內例外各為 1，validator 失敗為 0。現況是多次，所以這個測試能抓到回退。
  - 上述路徑結束後連線狀態為 Closed。
  - 呼叫票種快取失效時資料庫連線已關閉（在 `IQueryCache` 替身內斷言連線狀態）。
  - `UnitOfWork.OpenConnectionAsync`：重複開啟、已有交易時開啟都拋例外；dispose 後關閉、重複 dispose 安全；開啟後可以正常 `BeginTransactionAsync` 與 commit，commit 後 dispose 不拋例外。
- 分段 log（修改後的 LT-MEASURE-001～004）：
  - `ConnectionOpenMs` 在成功（001）、提早 409、validator 之後的交易前錯誤（002，以票種不存在 404 與跨活動 400 兩種為代表）都有值，validator 失敗為 null。
  - 交易內失敗與例外（004）的既有 log 測試補上 `ConnectionOpenMs` 有值的斷言。
  - LT-MEASURE-003（預設不輸出）不受 B2 影響，沿用既有測試。
- `TP-ORDER-023` 改寫（`OrderServiceEarlyConflictTests`）：
  - 現行構造是「持鎖者也是 `PlaceOrderAsync`，暫停在 Event 鎖之後；等鎖者卡在 Event 鎖」。B1 後兩者的共享鎖相容，等鎖者會直接搶到座位，斷言會反過來，因此必須改寫。
  - 新構造：新增 `InterceptingEventSeatRepository`（`AfterGetForUpdateAsync` 掛點），`OrderServiceTestFactory` 加上可選的 `eventSeatRepository` 參數。持鎖者暫停在**座位**列鎖之後、尚未提交；等鎖者在交易外讀到座位仍可售（持鎖者未提交），通過提早判斷後卡在座位列鎖（以 `pg_locks` 確認）；放行後持鎖者 201、等鎖者 `SeatNoLongerAvailable` 409、該座位 `OrderItems` 恰 1 筆。
  - 這仍證明 TP-ORDER-023 的三件事：交易外判斷為可售、等鎖期間座位被另一筆訂單暫扣、鎖內判斷為權威。
- 既有測試維持通過：`OrderServiceQueueModeLinearizationTests`（TP-ORDER-015／016）、`SetEventQueueModeHandlerConcurrencyTests`、`TicketTypeConcurrencyTests`。
- MODIFIED 的取代對象：`openspec/specs/load-testing/spec.md` 的 Requirement「下單分段耗時量測」（含 Scenario LT-MEASURE-001～004），取代範圍為整個 Requirement 區塊。OpenSpec 的 MODIFIED 語法就是以 `### Requirement:` 標題完全相符來指定對象，沒有另外的 ID 語法；`openspec change show order-placement-p95-phase2 --json --deltas-only` 可看到解析結果為 MODIFIED 該 Requirement。B2 回退時整段 MODIFIED 撤除，主 spec 維持第一階段版本。
- LT-MEASURE 規則與測試的對應：

  | 規則 | 測試 |
  |---|---|
  | 欄位集合（含新增 `ConnectionOpenMs`）、不含買家 Id（001） | `PlaceOrderAsync_WhenDebugEnabledAndOrderSucceeds_LogsAllPhasesWithoutBuyerId`；`PhaseTimingFields` 加入 `ConnectionOpenMs`，所有分段斷言有值 |
  | 未到達分段為 null 不是 0、交易前拒絕（002） | `..._WhenDebugEnabledAndRejectedBeforeTransaction_LogsNullInTransactionPhases`（validator 失敗，`ConnectionOpenMs` 為 null）、`..._WhenDebugEnabledAndRejectedEarlyWithConflict_LogsConflictWithNullInTransactionPhases`（`ConnectionOpenMs` 有值）；新增票種不存在 404、跨活動 400 兩個測試（`ConnectionOpenMs` 有值、交易內分段 null） |
  | 預設關閉、關閉時不組裝參數（003） | `..._WhenDebugDisabled_DoesNotCallLogAtDebug`（不變） |
  | 例外路徑仍輸出且照常拋出（004） | `..._WhenDebugEnabledAndExceptionThrown_LogsExceptionOutcomeAndRethrows`，補 `ConnectionOpenMs` 有值 |
  | 以 `Stopwatch` 量測、不以牆上時鐘相減 | 新增 `PlaceOrderAsync_WhenWallClockJumpsDuringOrder_PhaseTimingsUnaffected`：下單流程中（Event 鎖定讀取的替身內）把 `FakeDateTimeProvider.UtcNow` 往後推 1 小時，斷言所有分段與總耗時皆 ≥ 0 且 < 60 秒（測試資料的售票期間需涵蓋推進後的時間）。若任一時間點改取 `IDateTimeProvider`，該分段會 ≥ 3,600,000ms；若混用 `DateTime` ticks 與 `Stopwatch` 時間戳，差值會是巨大或負值，兩者都會失敗。變異驗證：暫時把 `EventLocked` 改成 `_dateTimeProvider.UtcNow.Ticks`，確認測試失敗後改回。這個測試抓不到「全部時間點一致改用 `DateTime.UtcNow`」，所以另加靜態測試 `OrderService_DoesNotReadWallClockDirectly`：以反射讀取 `OrderService` 及其所有巢狀型別（含 `PlaceOrderPhaseTimings`、async 狀態機、lambda closure）的 IL，逐一解析 call 指令，斷言沒有呼叫 `DateTime.Now`／`DateTime.UtcNow`／`DateTimeOffset.Now`／`DateTimeOffset.UtcNow`，且有呼叫 `Stopwatch.GetTimestamp`。用 IL 而不掃原始碼文字，是因為註解會誤判，`using static` 也能繞過文字比對。範圍是整個 `OrderService`：Application 層目前沒有任何直接讀牆上時鐘的程式碼（2026-10-07 grep 確認），業務時間一律走 `IDateTimeProvider`，所以不會誤判。變異驗證：暫時把一個時間點改成 `DateTime.UtcNow.Ticks`，確認失敗後改回。strict-reviewer 是第二層防線 |

- `IEventRepository` 新增方法後，測試替身（`FakeEventRepository`、`CountingEventRepository`、`InterceptingEventRepository`、`OrderServiceQueueModeLinearizationTests` 內的替身等）都要實作。

### 決策 5：B1、B2 各自判定採用，以及回退後的交付狀態

- delta spec 描述的是 B1、B2 都採用時的目標狀態。B1、B2 各自判定，互不連動：
  - B1 採用條件：B1 量測的 EventLockWait p95 下降，且切換延遲探測在門檻內（決策 3）。任一不成立時暫停回報使用者，不自行回退。
  - B2 採用條件：B2 量測的 Total p95 與 k6 P95 都不比 B1 慢。不成立時直接回退（proposal 已決定，不需再問）。
- B2 回退時要做的事（tasks 4.2）：
  - 移除 B2 的程式碼與測試（`OpenConnectionAsync`、`IUnitOfWorkConnection`、`ConnectionOpenMs`、TP-ORDER-037／038 測試、`export-measure-phases.sh` 欄位）。
  - 從 delta spec 刪除 Requirement「建立訂單每筆只占用一條資料庫連線」（TP-ORDER-037／038）與整段 MODIFIED「下單分段耗時量測」，主 spec 的 LT-MEASURE 維持第一階段版本。
  - proposal、design 標註 B2 未採用與原因；切換延遲探測改在 B1 狀態下執行。
  - 報告寫 B2 的量測數據與「未採用」判定，正式驗收在 B1 狀態下執行。
  - load-testing 報告 Requirement 標題的「（Event 共享鎖、單一連線）」改為「（Event 共享鎖）」。
- 交付狀態的定義：本 change 的交付物是「採用的優化 + 每項優化的量測與判定 + 第二階段報告」。
  - B2 回退仍算完成，報告標示「B2 未採用」。
  - §5 P95 目標是否達成另外判定，未達成照 LT-REPORT-008 記錄原因，不影響 change 完成。
  - B1 未通過且使用者決定回退 B1 時，等同兩項都未採用：change 以「優化未採用」結案，報告保留量測數據，由使用者決定是否直接開 Redis 預扣閘門。

## 安全確認（CLAUDE.md 安全強制規則）

- 輸入驗證：不新增外部輸入。`POST /api/orders` 的請求仍由既有 `PlaceOrderRequestValidator`（Application 層）驗證；`eventId` 來自交易外查得的票種／座位，不直接來自使用者字串。
- SQL：`GetForShareAsync` 以 `FromSqlInterpolated` 參數化，沒有字串拼接；沒有 shell 指令。
- N+1：交易前票種是每個不同 `TicketTypeId` 查一次，這是既有行為；validator 沒有選購筆數上限，`MaxTicketsPerOrder` 檢查也在這個迴圈之後，所以筆數只受請求大小限制。本 change 不改變查詢次數，B2 只讓它們共用同一條連線。這是既有風險，不在本次範圍，於 `docs/project-scope.md` §8 列為後續待辦（tasks 6.3）。
- 權限：`POST /api/orders` 仍需登入（`OrdersController` 類別層級 `[Authorize]`，加上 Application 層的實名等既有檢查）；切換排隊模式仍由 `AdminEventsController` 的 `RequireOrganizerContext` 政策保護。本次不改任何授權邏輯，未授權使用者能觸發的範圍不變。
- 前端：不涉及。
- 機敏資訊：不涉及。

## Risks / Trade-offs

- [寫入者飢餓：連線佔用] 被餓的 `FOR UPDATE` 路徑（加入排隊、入場推進）等待期間各佔一條連線、沒有 `lock_timeout`，多個加入排隊請求同時卡住時會吃掉連線池，間接拖慢下單。後果是延遲，不是超賣。切換延遲探測設客戶端逾時，逾時本身記為量測結果。
- [寫入者飢餓] PostgreSQL 不保證列鎖的公平性：已有共享持有者時，新的共享鎖請求可能直接取得，讓等待中的 `FOR UPDATE`（切換排隊模式、加入排隊、入場推進）在持續下單期間一直等不到。→ 每筆下單持有共享鎖只有交易期間（提案時量測 InLock 中位數 < 1ms 加上座位／票種鎖等待；2026-10-07 實測突發流量下持有時間（逐筆 InLock＋Commit）p95 約 44～314ms（B2 只計進交易的請求），主要是座位／票種鎖互等）；排隊模式下下單受入場名額限制，同時持有者有上限。決策 3 的切換延遲探測以 3 秒為門檻判定，超過即暫停回報使用者；持續流量下加入排隊的飢餓沒有量測場景，依決策 3 明確接受並記入 project-scope §8。
- [MultiXact 成本] 多個交易同時對同一列取 `FOR SHARE` 時，PostgreSQL 以 MultiXact 記錄持有者，大量併發時有額外成本。→ 由 B1 量測的 EventLockWait 直接反映；若 EventLockWait 沒有下降，依決策 5 暫停並回報使用者，不自行回退。
- [數量票熱點] 數量票所有請求都要鎖同一筆 `TicketType` 列，B1 只是把等待從 Event 列移到 TicketType 列，總序列化不變。→ 數量票主要靠 B2；報告須如實說明，可能仍未達標，屆時以 Redis 預扣閘門另開 change。
- [B2 佔用連線更久] 交易前的應用程式處理（迴圈、驗證）期間也持有連線，連線總佔用時間略增。→ 量測 Total 與 k6 P95 比較；變慢就回退（決策 2）。
- [B2 遺漏歸還] 若某條提早 return 路徑沒有歸還連線，會耗盡連線池。→ `await using` 加上三條路徑的連線狀態測試；壓測中若出現 `too many clients` 或連線池逾時，視為失敗。
- [量測口徑改變] 分段 log 新增 `ConnectionOpenMs`、PreTransaction 起點改變。→ 報告以 Total、k6 P95 為主要比較依據，分段比較時註明口徑差異。
- [WSL2 時鐘倒退] 沿用第一階段的處理：結果全部計入，不排除、不重跑，在報告註明。
