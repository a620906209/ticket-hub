# Proposal

## Why

k8s 以 2 個 api 副本運行（`k8s/40-api.yaml`），`ExpiredOrderCleanupService` 每個副本各跑一份，沒有任何互斥：兩邊每輪都掃描同一批過期 Pending 訂單並各自嘗試取消，造成重複工作，且輸家的取消會因訂單已非 Pending 而回 Conflict，在日誌留下無意義的 Warning 噪音（`docs/review/TODO.md` ⑦／H-6）。

已讀程式確認：庫存**不會**重複釋放。`OrderService.ChangeOrderStatusAsync` 已對座位／票種做 FOR UPDATE 並在鎖後重讀訂單，`CancelOrderHandler` 對非 Pending 訂單回 Conflict，`OrderServiceConcurrencyTests` 也已涵蓋並發取消（僅買家取消路徑）。H-6 所稱「非 FOR UPDATE 載入、無並發 token」的前提已過時，本 change 只處理「多副本重複掃描」。

## What Changes

- `ExpiredOrderCleanupService` 每輪先嘗試取得 `IDistributedLock`（單一固定 key）：取得才執行該輪清理並於 `finally` 釋放；鎖被其他實例持有則跳過本輪（Debug log）。
- Redis 不可用（`LockResult.RedisUnavailable`）時**照跑清理**（fail-open）並記一筆 Warning——清理的正確性由資料庫鎖保證，與 `PurchaseQueueAdmissionService` 的 fail-closed 不同（理由見 design.md）。
- 抽出新的公開進入點（含鎖邏輯）供測試呼叫；既有 `CleanupOnceAsync` 維持原語意。
- 補測試：鎖行為單元測試、兩個服務實例搭配真實 Redis 的整合測試、系統清理路徑（`CancelExpiredOrderAsync`）的並發取消測試。
- 文件：`docs/review/TODO.md` 的 H-6 改為實際範圍並標示 ⑦ 已實作。

不改：`ChangeOrderStatusAsync`、Order 並發 token／xmin、`PurchaseQueueAdmissionService` 既有鎖行為、⑧ `db-resilience`。

## Capabilities

### New Capabilities
- `order-cleanup-leader-election`: 逾時訂單清理背景服務的多實例互斥執行，以及分散式鎖不可用時的降級行為（比照既有 `purchase-queue-leader-election` 的能力邊界）。

### Modified Capabilities
<!-- 無：既有 `ticket-ordering`「取消訂單，統一處理主動取消與逾時清理」的對外行為不變。 -->

## Impact

- 程式碼：`src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs`（建構子新增 `IDistributedLock`、`DistributedLockOptions`，所有 `new ExpiredOrderCleanupService(...)` 的測試呼叫端需同步調整）。
- 測試：`tests/ProjectC.WebApi.Tests/BackgroundServices/`、`tests/ProjectC.Infrastructure.Tests/`（並發取消）；可能更新 `ServiceRegistrationSnapshot.baseline.txt`。
- 相依：沿用既有 Redis 與 `IDistributedLock`／`DistributedLockOptions`，無新增套件、無 migration、無 API 變更。
- 規模：單一 capability、預估 S 級，不需拆分。
