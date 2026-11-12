## Context

`ExpiredOrderCleanupService` 每 `OrderCleanupOptions.IntervalSeconds`（預設 300）掃描一次逾時 Pending 訂單並逐筆呼叫 `OrderService.CancelExpiredOrderAsync`；`k8s/40-api.yaml` 為 2 個副本，兩份服務無互斥。動機見 proposal.md - Why。

已讀取的事實：
- `OrderService.ChangeOrderStatusAsync`：交易內先 `GetForUpdateAsync` 鎖座位、再鎖計數票種，鎖後 `ReloadAsync` 重讀訂單；`CancelOrderHandler` 對非 Pending 回 Conflict。庫存正確性已由此保證，H-6 所稱「非 FOR UPDATE 載入」不成立。
- `OrderServiceConcurrencyTests` 已有「兩個並發買家取消」「Confirm 與 Cancel 並發」，**沒有**系統清理路徑（`requestingBuyerId = null` 的 `CancelExpiredOrderAsync`）的並發測試。
- 既有可比照的機制：`PurchaseQueueAdmissionService.AdvanceQueueOnceWithLeaderElectionAsync`（`IDistributedLock.TryAcquireAsync` → `Acquired`／`HeldByOther`／`RedisUnavailable`，`finally` 釋放）；`DistributedLockOptions.LockTtlMultiplier`（預設 3）；`RedisDistributedLock` 對 Redis 連線／逾時例外只記 Warning 並回傳 `RedisUnavailable`，釋放失敗亦只記 Warning。
- `new ExpiredOrderCleanupService(...)` 在 4 個測試檔直接建構（`OrderServiceNotificationTests`、`QueryCachingFailOpenTests`、`ExceptionMaskingPipelineTests`、`BackgroundServiceCycleLevelFailureTraceIdTests`），另有 `ExpiredOrderCleanupServiceTests`、`BackgroundServiceTraceIdTests` 以 `CreateService()` helper（目標型別 `new(...)`）建構，共 6 處；盤點須以型別引用搜尋，不能只搜 `new ExpiredOrderCleanupService(`。

## Goals / Non-Goals

**Goals**
- 多副本下每輪只有一個實例執行清理，消除重複掃描與 Conflict Warning 噪音。
- Redis 故障不影響逾時訂單的回收。
- 補上系統清理路徑的並發取消測試，把「正確性不依賴鎖」固定下來。

**Non-Goals**
- 不改 `ChangeOrderStatusAsync`、不替 Order 加 xmin／並發 token（沒有對應失敗情境，且需 migration 與衝突重試路徑）。
- 不改 `PurchaseQueueAdmissionService` 既有鎖行為；不處理 ⑧ `db-resilience`；不做鎖自動續租。

## Decisions

1. **比照排隊推進服務的鎖流程**：新增公開方法 `CleanupOnceWithLeaderElectionAsync`（含鎖邏輯）；`ExecuteAsync` 正式排程改走此方法。既有 `CleanupOnceAsync`（無鎖）維持原語意，繼續供既有整合測試直接呼叫單輪清理邏輯。理由：不破壞既有測試與 TraceId scope 的巢狀規則（`ExecuteAsync` 已在外層 push TraceId，鎖包裝不得再 push 第二個）。替代方案：在 `CleanupOnceCoreAsync` 內直接取鎖——會讓所有既有直接呼叫 `CleanupOnceAsync` 的測試都需要 Redis，放棄。
2. **鎖 key 與 TTL**：單一固定 key `order-cleanup:lock`，涵蓋整輪、不分活動。TTL = `Max(1, IntervalSeconds) × LockTtlMultiplier`（預設 300 × 3 = 900 秒）。因正常結束即釋放，TTL 只決定持有者當掉後的最長恢復時間；不做續租（與 `purchase-queue-leader-election` 一致）。
3. **Redis 不可用時 fail-open**（與排隊推進的 fail-closed 刻意不同）。排隊推進的「不超額」保證依賴 Redis Lua Script，Redis 掛了就沒有其他機制；清理的正確性完全在資料庫鎖，Redis 鎖只是效率優化。fail-closed 的代價是 Redis 一掛，逾時訂單的座位與庫存永遠不被回收（賣不出去的座位卡住），遠大於「兩副本短暫重複掃描」的代價。`RedisUnavailable` 時服務自己再記一筆 Warning（指出本輪無鎖執行；`RedisDistributedLock` 已記錄連線失敗本身），不呼叫 `ReleaseAsync`（無 owner token）。
   - **非連線類 Redis 例外也 fail-open**：`RedisDistributedLock` 只攔截 `RedisConnectionException`／`RedisTimeoutException`，failover 時的 `RedisServerException`（READONLY／LOADING／OOM）會直接拋出。服務端因此對 `TryAcquireAsync` 另包 `catch (Exception) when (不是 OperationCanceledException)`，視同 `RedisUnavailable`（記 Warning、無鎖照跑）；不放寬共用鎖的 catch，避免改變 `PurchaseQueueAdmissionService` 的 fail-closed 語意。
   - **釋放失敗不遮蔽清理結果**：`finally` 內的 `ReleaseAsync` 另包 try/catch（排除 `OperationCanceledException`），失敗只記 Warning 並吞掉（TTL 會兜底），清理本身的例外照常往外傳。Warning 只帶固定 key 與例外，不含 owner token。
4. **建構子新增 `IDistributedLock`、`DistributedLockOptions`**（皆 Singleton，已註冊於 `RedisServiceCollectionExtensions`；服務為 HostedService，生命週期符合）。需同步更新 6 處測試建構。替代方案：可選參數（null 表示不用鎖）——會讓「忘記注入鎖」靜默退化成無互斥，違反 fail loud，放棄。
5. **介面歸類**：不新增介面，沿用 `Application/Common/Interfaces/IDistributedLock`（既有核准例外），無分層變更。
6. **測試位置**：並發取消測試與「兩個服務實例＋真實 Redis」整合測試都放在 `Infrastructure.Tests`（已有 `RedisFixture` 與真實 PostgreSQL 夾具，並已引用 WebApi 建構過 `ExpiredOrderCleanupService`），比照 `OrderServiceConcurrencyTests`；鎖行為單元測試放 `WebApi.Tests/BackgroundServices/`，以替身隔離 DB 與 Redis。皆不連線開發用 `db` 服務。
7. **文件**：`docs/review/TODO.md` H-6 改為實際範圍，並註明 FOR UPDATE／xmin 部分原本已由現有鎖處理；⑦ 標為已實作。

## 跨模組慣例對照

不適用：本 change 不新增或修改 API 端點、錯誤回應、認證／授權、DTO 欄位或列表端點，只改背景服務的啟動排程邏輯。

## Risks / Trade-offs

- 持有者清理耗時超過 TTL（900 秒）→ 另一實例取得新鎖形成重疊 → 正確性由資料庫鎖保證（OCLE-006、OCLE-009／010 驗證），僅多一些 Conflict Warning。
- 持有者當掉 → 最長 900 秒內無人清理 → 逾時訂單的座位晚釋放；查詢端本就依到期時間推導 Expired，買家端可見性不受影響。
- Redis 故障期間兩副本重複掃描 → 預期且可接受（fail-open 的代價），有 Warning 可觀測。
- fail-open 與排隊推進 fail-closed 並存 → 兩者在各自 spec 與本文件都有理由說明，避免日後被誤統一。

## Migration Plan

單次部署即生效，無資料移轉。滾動更新期間新舊副本並存：舊版不取鎖、新版取鎖，此時等同現況（重複掃描由資料庫鎖兜底），無新風險。回滾：還原映像即可。

## Open Questions

無。
