## Why

`PurchaseQueueAdmissionService`（`purchase-queue` 能力的入場推進背景服務）目前假設整個應用程式只有一份執行個體：每個實例各自跑週期性輪詢，逐一掃描所有 `IsQueueModeEnabled = true` 的活動並嘗試取得該活動的 DB 悲觀鎖來推進入場名額。這在單一實例部署下沒有問題，但水平擴展成多個 API 實例後，**每個實例都會在同一個時間點各自嘗試處理同一批活動**：DB 交易層的悲觀鎖保證了不會超額入場（正確性沒有問題），但除了先取得鎖的那個實例外，其餘實例的整輪掃描與鎖等待都是白工，實例數越多、對 DB 的無效交易與鎖競爭就越重。這是典型的「多副本重複執行同一週期性背景任務」問題，適合用分散式鎖做 Leader Election 解決：**在鎖的租約有效期間內**，每一輪只讓其中一個實例真正執行推進邏輯，其餘實例該輪直接跳過——若持有鎖的實例執行時間超過租約，另一實例可能取得新鎖並重疊執行，此時的正確性由既有資料庫悲觀鎖保證，不是本次新增的分散式鎖本身要保證的範圍（見 design.md 決策 3）。

## What Changes

- 新增 Redis 服務至 `docker-compose.yml`，作為分散式鎖的儲存後端
- 新增分散式鎖能力：以 Redis `SET NX PX` 語義實作「租約有效期間內，當下這一輪推進最多只有一個實例可執行」的鎖，鎖天生具備 TTL（租約），避免持有鎖的實例當掉後其他實例永久等待；租約到期後即使原持有者仍在執行，另一實例也可能取得新鎖並重疊執行，此時不由本鎖保證正確性（見 design.md 決策 3）
- `PurchaseQueueAdmissionService.ExecuteAsync` 的每一輪輪詢，改為先嘗試取得該輪的分散式鎖：
  - 取得成功 → 依既有邏輯執行 `AdvanceQueueOnceCoreAsync`（不變更既有的 DB 悲觀鎖、推進順序、逾時判斷等既定行為）
  - 取得失敗（其他實例已持有）→ 該輪直接跳過，等待下一輪輪詢再嘗試
- Redis 連線失敗或無法取得鎖狀態時，MUST 採 fail-open 行為：直接視為已取得執行資格、照常執行本輪推進並記錄 Warning（見 design.md 決策 4），不得讓背景服務整個掛掉或造成所有實例同時放棄推進；正確性仍由既有資料庫悲觀鎖保證，代價是 Redis 故障期間多實例可能重複執行同一輪推進

## Capabilities

### New Capabilities
- `purchase-queue-leader-election`：多實例部署下，`purchase-queue` 入場推進背景服務在鎖租約有效期間內減少多實例重複執行的機制（基於 Redis 分散式鎖）；租約逾期造成的重疊執行由既有資料庫悲觀鎖保證正確性，非本機制本身的保證範圍

### Modified Capabilities
（無——`purchase-queue` 既有的入場推進正確性、順序、逾時等對外可觀察行為不變，這次異動純粹是背景服務執行層面的效率與資源競爭改善，不變更任何 API 契約或既有 Requirement）

## Impact

- **程式碼**：`src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs`（加上取鎖判斷）；新增 Infrastructure 層的 Redis 分散式鎖實作與對應 Domain/Application 介面
- **相依套件**：新增 Redis client 套件（`StackExchange.Redis`）
- **部署環境**：`docker-compose.yml` 新增 `redis` 服務；`api` 服務新增 Redis 連線字串環境變數（比照現有 `db`/`seq` 連線字串慣例，一律用 compose service name，禁止 `localhost`）
- **測試**：需要 Testcontainers 起 Redis 容器做整合測試（比照既有 PostgreSQL 整合測試模式），驗證多實例情境下的鎖互斥行為與 TTL 逾時後的自動釋放
- **不影響**：`seat-reservation` 座位鎖定機制維持現有 DB 悲觀鎖不變（技術上已正確支援多實例，無需改動，理由見本 change 討論過程）；`api-rate-limiting` 的記憶體限流跨實例不共用計數的問題，不在本次範疇內
