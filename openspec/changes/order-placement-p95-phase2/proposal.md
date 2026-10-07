## Why

`order-placement-p95-optimization`（2026-10-06 封存）完成優化 C、A 後，§5「500 併發搶 50 張、P95 < 500ms」仍未達成（Release P95 中位數：座位票 711ms、數量票 814ms，見 `docs/load-test/p95-optimization-report.md`）。分段量測顯示鎖內工作已 < 1ms，剩餘延遲在鎖外：

- **Event 列鎖仍讓同一活動所有下單逐筆通過**：每筆下單以 `FOR UPDATE` 鎖 Event，即使買的是不同座位也互斥（EventLockWait p95 約 300ms）。這個鎖只為了跟「切換排隊模式」線性化（`rate-limiting-queue` design.md 決策 4），下單本身只讀 `IsQueueModeEnabled`、不修改 Event。
- **連線池重複排隊**：交易前的每次查詢都各自向連線池借還連線（座位票 4 次、數量票 2 次），開交易時再借一次。500 筆同時到達、連線上限 100 時，每筆要排隊 3～5 次（PreTransaction p95 約 400～660ms、BeginTransaction p95 約 550～620ms）。

上一個 change 已把這兩項列為候選 B，明確要求另開 change、重走 design-hardener。

## What Changes

- **B1：下單改以共享鎖（`FOR SHARE`）鎖定 Event**：
  - 下單彼此不再因 Event 列互斥；同一活動不同座位的下單可以同時進行。
  - 切換排隊模式時對 Event 列的 `UPDATE` 仍與共享鎖互斥，線性化保證不變：進行中的下單結束前，切換會被阻塞；切換提交後才開始等鎖的下單，一定讀到新值。
  - 入場推進服務與加入排隊的 `FOR UPDATE` 不改。
  - 座位與票種仍以各自的 `FOR UPDATE` 保護，0% 超賣的保證不變。
- **B2：每筆下單只向連線池借一次連線**：
  - 從第一次查詢起持有同一條連線，直到交易結束，不再每次查詢各借各還。
  - 交易提交後，Redis 快取失效與入場鏡像同步不佔用資料庫連線。
  - **採用（2026-10-07 量測，design 決策 5）**：控制暖機後（每版 force-recreate api、座位票與數量票各暖身 1 次後才量），`after-b2-warm` 對比 `after-b1-warm`：座位票 Total p95 359.0 → 197.8ms、k6 P95 384.5 → 235.5ms；數量票 Total p95 481.5 → 372.9ms、k6 P95 496.4 → 380.8ms。第一次 `after-b1`／`after-b2` 量測因兩版各有一個 scenario 是重啟後第一次執行（冷啟動），一度誤判數量票變慢並回退，已作廢，見報告第 3 節。
- **量測**：
  - 沿用上一個 change 的量測工具（分段耗時 log、`LT_MEASURE_TAG`）。
  - B1、B2 各做完後量一次，正式驗收照舊 12 次。
  - B1、B2 各自判定採用與否（design 決策 5）：B2 若讓 Total p95 或 k6 P95 比 B1 慢，回退 B2 並撤銷其 delta spec，報告標「B2 未採用」，change 仍可完成；B1 若 EventLockWait 沒下降，或切換排隊模式延遲超過 3 秒，暫停回報使用者。
- **明確不在範疇**：
  - 調整連線池大小或 PostgreSQL `max_connections`：上一個 change 已決定不調整，本次維持。
  - Redis 預扣庫存閘門：屬於架構變更，若本次仍未達標，另開 change。
  - 犧牲資料耐久性的設定（`synchronous_commit=off` 等）：禁止。
  - 改變 P95 門檻或口徑（201 與 409 合併計算）：禁止。
  - 入場推進服務、加入排隊、切換排隊模式的鎖定方式：不改。
- **驗收**：
  - 沿用 `load-testing` 的腳本、門檻與流程（Debug／Release × 數量票／座位票各 3 次）。
  - P95 < 500ms 且 0% 超賣才算達成；未達成時照實記錄原因。
  - 數量票的熱點在同一筆 TicketType 列，B1 對它幫助有限，主要依賴 B2；預期可能仍無法達標。

- **拆分評估**：tasks 30 項、2 個 capability，在門檻邊界，不拆。B1、B2 都改 `OrderService.PlaceOrderCoreAsync` 同一段流程，B2 的量測要以 B1 為比較基準（單向相依），且兩者各有獨立的採用／回退判定（design 決策 5），拆成兩個 change 只會多一次正式驗收，不減少審查範圍。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `ticket-purchase`：新增「同一活動的下單彼此不因活動層級鎖互斥」的並發保證，並明確化下單與切換排隊模式之間的線性化語意（既有 TP-ORDER-015／016 行為不變）；新增「每筆下單只占用一條資料庫連線、提交後的 Redis 呼叫不占用連線」的保證。
- `load-testing`：新增第二階段優化結果報告（`docs/load-test/p95-phase2-report.md`），沿用第一階段報告的資料表比對與未達標說明規則。

## Impact

- **程式碼**：
  - `IEventRepository`／`EventRepository`：新增以 `FOR SHARE` 鎖定讀取 Event 的方法。既有 `GetForUpdateAsync` 保留，切換排隊模式、加入排隊、入場推進仍使用。
  - `IUnitOfWork`／`UnitOfWork`：新增在交易前就開啟並持有連線的入口（介面形式由 design 決定）。
  - `OrderService.PlaceOrderAsync`：改用共享鎖、在第一次查詢前持有連線。
- **API**：`POST /api/orders` 的狀態碼、錯誤型別、錯誤優先順序都不變。
- **壓測工具**：`export-measure-phases.sh` 匯出欄位加上 `ConnectionOpenMs`；新增 `loadtest/check-baseline-reports.sh` 與其自我測試，自動驗證第一階段報告未被修改（LT-REPORT-007）。`check-report-tables.sh` 本來就以參數接收報告路徑，不改。
- **文件**：新增 `docs/load-test/p95-phase2-report.md`；`docs/project-scope.md` §8 更新 P95 待辦狀態。
- **相依性**：無新增套件。
