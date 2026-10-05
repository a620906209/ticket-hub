## Why

`k6-load-test`（2026-10-05）實測 `docs/project-scope.md` §5「500 併發搶 50 張、P95 < 500ms」時，500 併發與 0% 超賣達成，但 **P95 未達成**（Release P95 中位數：座位票約 4835ms、數量票約 802ms，見 `docs/load-test/report.md`）。Seq 依狀態碼拆分顯示九成樣本是 409（售完／座位已被持有），整體 P95 幾乎等於 409 的 P95；座位票即使只看 201 也未達標。這是 §5 目標中唯一未達成的項目，也是雲端部署評估的前置條件（§8）。

目前瓶頸只有推測（同一活動的下單因 Event `FOR UPDATE` 逐筆序列化，鎖內工作量決定總延遲），未經量測，因此本 change 採**先量測、後優化**：先確認時間花在哪，再只做量測證據支持的低風險優化。

## What Changes

- **量測（第一階段，不改行為）**：
  - `OrderService.PlaceOrderAsync` 新增以單調時鐘（`Stopwatch`）計時的分段耗時 Debug log：交易前檢查、開交易（含取得連線）、等待 Event 鎖、鎖內處理、commit。平時關閉，量測時以環境變數開啟。
  - 新增無競爭基準量測：單一買家依序下單，取得單筆下單在沒有鎖競爭時的耗時下限。
  - 壓測期間取樣 PostgreSQL `pg_stat_activity` 的 `wait_event_type`／`wait_event`，對照應用端分段耗時，拆分連線池等待、鎖等待與交易本身耗時。
  - 量測結果與判讀寫入報告，作為第二階段優化的依據。
- **優化（第二階段，依量測結果執行；以下兩項為預定候選）**：
  - **C：座位分區比對移到鎖之前**——現況座位票每筆訂單在持有 Event 鎖時，以追蹤查詢載入整張座位圖（壓測活動為 2000 席），已售座位的 409 也要付這個成本。座位與分區建立後不可變，改為交易前以 no-tracking 查詢只取所選座位比對分區，並顯式比對座位樣板屬於該活動座位圖（不符回 404，取代原本「在座位圖中找不到」的隱含檢查）。
  - **A：交易前提早回 409**——以交易前已載入的 no-tracking 座位與票種資料，在開交易前判斷座位不可鎖定或計數票種庫存不足，直接回 409，不排 Event 鎖。鎖內判斷仍是唯一權威（提前判斷通過不代表成功）。交易外讀到活動為排隊模式時跳過此檢查。
- **明確不在範疇**：
  - 縮小或改變 Event `FOR UPDATE` 鎖範圍（候選 B）：涉及 Queue Mode 線性化設計（`rate-limiting-queue` design.md 決策 4），若 A＋C 不足以達標，另開 change 並重走 design-hardener。
  - 連線池大小／PostgreSQL `max_connections` 調整：待量測證實為瓶頸後，再由使用者決定是否納入（若納入須更新本 proposal 並重走 spec-reviewer）。
  - 以 `synchronous_commit=off`、`fsync=off` 等犧牲資料耐久性的設定換取指標：禁止。
  - P95 口徑：維持 `load-testing` spec 的 201＋409 合併計算，不改口徑。
- **驗收**：沿用 `k6-load-test` 的腳本、門檻與流程（`loadtest/README.md`，Debug／Release × 數量票／座位票各 3 次），P95 < 500ms 且 0% 超賣才算達成。若優化後量測證明剩餘延遲主要來自本機磁碟／WSL2，照實記錄未達標與原因，不調整門檻。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `ticket-purchase`：建立訂單新增「交易前提早拒絕」規則——交易外判斷座位不可鎖定或計數票種庫存不足時回 409，不開交易；座位分區不一致在交易前拒絕；明確交易外讀到排隊模式時不做提早拒絕，以及 TP-ORDER-015 切換時間差內可能回 409 的語意。
- `load-testing`：新增瓶頸量測（無競爭基準、分段耗時、資料庫等待事件取樣）與優化後重跑結果的報告要求。

## Impact

- **程式碼**：`src/ProjectC.Application/Orders/OrderService.cs`（分段計時 log、提早 409、分區比對位置）；可能新增 `ISeatMapRepository` 的 no-tracking 查詢或沿用既有 `GetSeatsByIdsAsync`（design 決定）。
- **API**：`POST /api/orders` 狀態碼與錯誤型別不變；僅錯誤判定時點提前，以及 TP-ORDER-015 切換時間差內的錯誤優先順序（見 spec delta）。
- **壓測工具**：`loadtest/` 新增基準量測腳本（`baseline.js`、純函式 `lib/baseline.js`）、資料庫等待事件取樣腳本與 SQL（`sample-db-waits.sh`、`sql/db-waits.sql`）、報告資料表比對腳本（`check-report-tables.sh`）及其自我測試（`tests/baseline.test.js` 等）；`lib/config.js` 新增量測標籤 `LT_MEASURE_TAG`（隔離量測輸出）；新增 `docker-compose.loadtest-measure.yml`（量測時開啟分段 log）與量測有效性檢查腳本 `check-measure-logging.sh`；`src/ProjectC.WebApi/appsettings.json` 的 Console sink 明確限制為 Information 以上（現行輸出不變）；`loadtest/README.md` 補充量測流程。新增根目錄 `.gitattributes`（`*.sh`、`*.sql` 固定 LF）。
- **文件**：`docs/load-test/` 新增量測與優化結果報告；`docs/project-scope.md` §8 更新 P95 待辦狀態。
- **相依性**：無新增套件。
