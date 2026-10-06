# 下單 P95 優化結果報告

> OpenSpec change：`order-placement-p95-optimization`。結果只代表本機 Docker Compose／WSL2 環境（api 與 PostgreSQL 同機、`max_connections` 100），不代表正式環境容量。
> 目標：Release 下 P95 < 500ms（201 與 409 合併計算）。基準報告 `docs/load-test/report.md` 不修改。

## 1. 優化前量測（`LT_MEASURE_TAG=before`）

量測執行與正式驗收分開：量測執行疊加 `docker-compose.loadtest-measure.yml`（OrderService 分段 log 進 Seq），Release 組態；分段耗時以應用端 `Stopwatch` 單調時鐘計算，百分位由原始值精確計算（`loadtest/lib/phase-stats.jq`）。

### 1.1 無競爭基準（1 VU 依序 50 筆，每筆不同買家）

| 票種 | P50 (ms) | P95 (ms) | 判定 | 檔案 |
|---|---|---|---|---|
| 數量票 | 5.92 | 8.00 | 有效（50/50 為 201） | `measure-before-baseline-count-summary.json` |
| 座位票 | 15.04 | 33.34 | 有效（50/50 為 201） | `measure-before-baseline-seat-summary.json` |

分段中位數（`measure-before-phases-baseline-{count,seat}.json`）：

| 票種 | PreTransaction | BeginTransaction | EventLockWait | InLock | Commit | Total |
|---|---|---|---|---|---|---|
| 數量票 | 1.21 | 0.03 | 0.46 | 0.57 | 2.30 | 4.85 |
| 座位票 | 1.60 | 0.03 | 0.43 | 8.83 | 3.09 | 14.01 |

座位票與數量票的差距幾乎全在鎖內（InLock 8.83ms 對 0.57ms），對應鎖內整張 SeatMap 載入（優化 C 的目標）。

**非 201 的 exit code 確認（tasks 3.2）**：把 `tokens.json` 的買家 token 簽章改壞（admin token 不動）後以 `LT_MEASURE_TAG=non201-check` 執行座位票基準，50 筆皆 401，k6 exit code 99、`baseline.isValid=false`（`measure-non201-check-baseline-seat-summary.json`）；之後還原 token 檔。

### 1.2 競爭下的分段耗時（500 VU 同時下單，各 1 次）

| Scenario | k6 P95 (ms) | 伺服器 Total p95 (ms) | 成功／409／5xx | 檔案 |
|---|---|---|---|---|
| 數量票 | 761.53 | 748.9 | 50／450／0 | `measure-before-phases-count-ticket.json` |
| 座位票 | 4832.77 | 4787.1 | 50／450／0 | `measure-before-phases-seat-ticket.json` |

各分段平均（ms；各分段連續，逐筆加總與 Total 相差最多 4.51ms，為 commit 後的 Redis 鏡像清理）：

| Scenario × 結果 | 筆數 | PreTransaction | BeginTransaction | EventLockWait | InLock | Commit | Total |
|---|---|---|---|---|---|---|---|
| 數量票 409 | 450 | 248.4 | 147.1 | 138.9 | 1.0 | — | 535.4 |
| 數量票 201 | 50 | 36.6 | 14.0 | 188.8 | 2.9 | 4.5 | 247.2 |
| 座位票 409 | 450 | 161.8 | 1767.3 | 940.2 | 9.4 | — | 2878.8 |
| 座位票 201 | 50 | 39.5 | 186.0 | 516.5 | 11.9 | 3.9 | 757.7 |

### 1.3 等待事件分布

取自兩次被判無效的數量票量測（見 1.4）：取樣期間 `projectc_dev` 連線數峰值 98–99（取樣本身另占 1 條），連線數 ≥ 50 的取樣點中，`active / Lock / tuple` 占 590 個連線樣本，其餘（`idle ClientRead`、`Lock transactionid`、`WALSync`）合計約 50。即絕大多數連線停在 Event 列鎖的等待上。

### 1.4 被判無效的量測執行

| 次 | Scenario | 原因 | 檔案 |
|---|---|---|---|
| 1 | 數量票（含等待事件取樣） | api log 出現 `too many clients`，5xx 100、confirm 失敗 16、QuantitySold 34 | `measure-before-invalid/attempt1/` |
| 2 | 數量票（含等待事件取樣） | 同上，`too many clients` 306 次、5xx 91、QuantitySold 31 | `measure-before-invalid/attempt2/` |

連續 2 次因連線數無效，依 tasks 3.3 停止重跑並進入 3.4 決策點。使用者決定（2026-10-06）：**不納入連線池調整**，改以不取樣的方式各重跑 1 次（即 1.2 的結果，api log `too many clients` 0 次）；等待事件分布沿用兩次無效執行的取樣。api 連線池（Npgsql 預設上限 100）與 PostgreSQL `max_connections`（100）相同，沒有餘裕，取樣多占的 1 條連線足以造成拒絕連線。

另觀察：第 2 次無效執行的 k6 P95 為 21.2ms，與伺服器 Total p95 1396.6ms 矛盾，同一次 `send_offset_max` 為 −671ms（牆上時鐘倒退）。推測 k6 的請求耗時受 WSL2 牆上時鐘倒退影響，**未驗證**；有效的 2 次執行中 k6 P95 與伺服器 Total p95 相差 < 2%。

### 1.5 瓶頸判定（tasks 3.4）

**序列化（Event 列鎖）為根本瓶頸，連線池等待為其衍生現象。**

- 座位票 409 的 Total 平均 2878.8ms 中，BeginTransaction（向連線池取連線）占 1767.3ms、EventLockWait 占 940.2ms；鎖內實際工作只有 9.4ms。
- 每筆在鎖內的時間 × 500 筆依序通過，約等於整體延遲：座位票 9.4ms × 500 ≈ 4.7s（P95 4.8s），數量票 1.0ms × 500 ≈ 0.5s（P95 0.76s）。
- 連線池耗盡的原因是等鎖的請求各自握著一條連線；連線上限受 `max_connections` 100 限制，加大連線池只會把等待從連線池移到鎖佇列，不會縮短序列化時間。
- 因此依序執行：優化 C（座位分區檢查移到交易前，移除鎖內 SeatMap 載入）縮短鎖內時間；優化 A（交易前提前 409）讓註定失敗的請求不進入鎖佇列、不占連線。

## 2. 優化 C 之後（`LT_MEASURE_TAG=after-c`，座位票 1 次，不取樣）

座位樣板改在交易前依 SeatId 批次讀取並比對座位圖成員與分區，鎖內不再重讀活動、不再載入整張座位圖。

| 指標 | 優化前 | 優化 C 後 |
|---|---|---|
| k6 P95 (ms) | 4832.77 | 1169.26 |
| 伺服器 Total p95 (ms) | 4787.1 | 1087.5 |
| InLock 中位數／p95 (ms) | 8.77／— | 0.75／1.15 |
| 成功／409／5xx | 50／450／0 | 50／450／0（QuantitySold 50、超賣 0） |
| api log `too many clients` | 0 | 0 |

各分段平均（ms）：

| 結果 | 筆數 | PreTransaction | BeginTransaction | EventLockWait | InLock | Commit | Total |
|---|---|---|---|---|---|---|---|
| 409 | 450 | 298.2 | 335.6 | 145.0 | 1.0 | — | 779.8 |
| 201 | 50 | 305.6 | 43.3 | 162.0 | 1.7 | 3.9 | 516.4 |

- 鎖內時間降到與數量票相同量級（優化前數量票 InLock 中位數 0.57ms），序列化總長約 500 × 1ms，符合 1.5 的推論。
- 瓶頸轉移：PreTransaction 平均約 300ms 成為最大分段（交易前的讀取也要向同一個連線池取連線），BeginTransaction 仍有 335.6ms 的連線池等待。仍未達 P95 < 500ms，接著做優化 A（交易前提前 409，讓注定失敗的 450 筆不進鎖佇列、不占連線）。
- 本次 k6 P95 比伺服器 Total p95 高 7.5%（優化前有效執行 < 2%），同一次 `send_offset_max` 為 −2034ms（牆上時鐘倒退）；差距來源**未驗證**，以伺服器分段為主要依據。檔案：`measure-after-c-seat-ticket-release-run1-summary.json`、`measure-after-c-phases-seat-ticket.json`。

## 3. 優化 A 之後（`LT_MEASURE_TAG=after-a`，兩個 scenario 各 1 次，不取樣）

交易外讀到活動且未開排隊模式時，以 no-tracking 資料判斷座位不可暫扣／計數庫存不足即回 409，不開交易。

| Scenario | k6 P95 (ms) | 伺服器 Total p95 (ms) | 成功／409／5xx | 提早 409 筆數 | 檔案 |
|---|---|---|---|---|---|
| 座位票 | 1249.27 | 1136.1 | 50／450／0 | **0** | `measure-after-a-phases-seat-ticket.json` |
| 數量票 | 938.46 | 922.6 | 50／450／0 | **0** | `measure-after-a-phases-count-ticket.json` |

兩次皆 `too many clients` 0 次、QuantitySold 50、超賣 0。

**優化 A 在此情境沒有觸發。** 450 筆 409 全部在鎖內被拒（`BeginTransactionMs` 皆非 null）：

- 壓測是 500 VU 同一時刻送出，交易外讀取（PreTransaction）在請求開始後數百毫秒內完成，那時搶到的訂單還在 Event 鎖佇列裡、尚未 commit，交易外讀到的座位／庫存全是「可售」。座位票 409 的 PreTransaction p95 411.9ms，早於第一筆成功訂單完成（最短 Total 444.7ms）。
- 程式路徑本身有效：壓測結束後對已售完的計數票種再送 1 筆，回 409、`BeginTransactionMs=null`、Total 1.85ms（不進鎖、不占連線）。即優化 A 只對「售完之後才到的請求」有效，對同時湧入的第一波沒有作用。
- 數量票 P95 從 761.53 變為 938.46ms：優化 A 不增加資料庫查詢（沿用交易前已讀到的資料），優化 C 不影響數量票，差異應屬執行間波動；本次只跑 1 次，**未驗證**。座位票 1169.26 → 1249.27ms 同理。
- 本次數量票 `send_offset_max` 為 −2042ms（牆上時鐘倒退）；座位票 77ms，k6 P95 與伺服器 Total p95 相差 10%（1249.27 對 1136.1），原因**未驗證**。

## 4. 正式驗收（Release／Debug × 數量票／座位票各 3 次）

2026-10-06 依 `loadtest/README.md` 執行：不帶 `LT_MEASURE_TAG`、不疊加量測 override（每次執行前確認 api 環境變數不含 `Serilog__MinimumLevel__Override__*`、PID 1 組態正確）、不取樣、每次前重跑 seeder、間隔至少 60 秒。下表為 `aggregate.js` 輸出，未經修改。

<!-- report-tables:begin -->
### Release × 數量票（count-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 1273.22 | 1289.37 | 50 | 450 | 0 | 50 | 102.00 | 未通過 |
| 2 | 786.28 | 803.67 | 50 | 450 | 0 | 50 | 88.00 | 未通過 |
| 3 | 814.45 | 834.77 | 50 | 450 | 0 | 50 | 88.00 | 未通過 |

- P95 (ms)（3 次有值）：中位數 814.45／最小值 786.28／最大值 1273.22
- P99 (ms)（3 次有值）：中位數 834.77／最小值 803.67／最大值 1289.37
- 判定：未通過
  - 第 1 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 2 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 3 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500

### Release × 座位票（seat-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 5.34 | 5.73 | 50 | 450 | 0 | 50 | 763.00 | 通過 |
| 2 | 711.04 | 730.65 | 50 | 450 | 0 | 50 | 99.00 | 未通過 |
| 3 | 731.08 | 743.07 | 50 | 450 | 0 | 50 | 105.00 | 未通過 |

- P95 (ms)（3 次有值）：中位數 711.04／最小值 5.34／最大值 731.08
- P99 (ms)（3 次有值）：中位數 730.65／最小值 5.73／最大值 743.07
- 判定：未通過
  - 第 2 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 3 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500

### Debug × 數量票（count-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 1284.57 | 1309.51 | 50 | 450 | 0 | 50 | 85.00 | 未通過 |
| 2 | 834.84 | 881.38 | 50 | 450 | 0 | 50 | 102.00 | 未通過 |
| 3 | 747.37 | 761.73 | 50 | 450 | 0 | 50 | 84.00 | 未通過 |

- P95 (ms)（3 次有值）：中位數 834.84／最小值 747.37／最大值 1284.57
- P99 (ms)（3 次有值）：中位數 881.38／最小值 761.73／最大值 1309.51
- 判定：未通過
  - 第 1 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 2 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 3 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500

### Debug × 座位票（seat-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 841.05 | 890.91 | 50 | 450 | 0 | 50 | 67.00 | 未通過 |
| 2 | 740.70 | 756.05 | 50 | 450 | 0 | 50 | 104.00 | 未通過 |
| 3 | 705.02 | 722.89 | 50 | 450 | 0 | 50 | 75.00 | 未通過 |

- P95 (ms)（3 次有值）：中位數 740.70／最小值 705.02／最大值 841.05
- P99 (ms)（3 次有值）：中位數 756.05／最小值 722.89／最大值 890.91
- 判定：未通過
  - 第 1 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 2 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 3 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
<!-- report-tables:end -->

### 4.1 執行說明

- 12 次全部 QuantitySold 50、成功 50、409 450、5xx 0，無超賣。
- seeder 在每次執行前都因 api 連線池仍占著連線而 `too many clients` 失敗 3～4 次，依 README 間隔 30 秒重試後皆成功（exit 0），沒有沿用舊 token。
- **Release × 座位票第 1 次的 P95 5.34ms 不可信**：該次 `place_order_send_offset_ms` 介於 −753 與 10ms（送出時距 763ms，其他執行約 100ms），代表壓測進行中牆上時鐘倒退；同一次 place-order 中位數 0.30ms，低於本機一次 HTTP 往返的合理下限。依「任何結果都計入」的規則不排除、不重跑，資料表照列；判定不受影響（另兩次仍未達標）。
- 另有 5 次（count debug 2、count release 2、seat debug 1／2、seat release 2）全部請求的送出時距整體偏移約 −2000ms、但 max−min 仍約 100ms，推測時鐘倒退發生在送出之前，P95 與其他執行同量級；是否影響延遲數字**未驗證**。
- Release 與 Debug 的數量票第 1 次（1273.22ms、1284.57ms）都是 api 重啟後的第一次執行，明顯高於第 2、3 次（747～835ms）；推測為冷啟動（JIT、連線池建立），**未驗證**。

## 5. 最終判定與限制

**未達成 P95 < 500ms**（Release 數量票中位數 814.45ms、座位票中位數 711.04ms）。

| Scenario（Release，P95 中位數） | 優化前（`k6-load-test` 基準） | 本 change 後 |
|---|---|---|
| 數量票 | 約 0.8s | 814.45ms |
| 座位票 | 約 4.8s | 711.04ms |

- 座位票延遲降為約 1/6，主因為優化 C；數量票不受優化 C 影響，維持原水準。
- 優化 A 在同時湧入的情境沒有觸發（見第 3 節），對本次驗收數字沒有貢獻；程式路徑對「售完後才到的請求」有效，保留。
- 限制：結果只代表本機 Docker Compose／WSL2（api 與 PostgreSQL 同機、`max_connections` 100）；WSL2 牆上時鐘會倒退，影響 k6 端計時（見 4.1）；每組只有 3 次。

<!-- unmet-reason:count-ticket -->
**數量票未達標原因**：鎖內工作本來就很短，延遲來自鎖外的排隊。`measure-after-a-phases-count-ticket.json`（500 筆）中 InLock 中位數僅 0.69ms，但 PreTransaction p95 664.2ms、BeginTransaction（向連線池取連線）p95 548.0ms、EventLockWait p95 296.7ms，Total p95 922.6ms。500 筆同時到達時，交易前讀取與開交易都在等連線池（上限受 `max_connections` 100 限制），優化 C 不影響此路徑，優化 A 因交易外讀到的庫存在第一波全為可售而未觸發。要再降需縮小 Event 列鎖範圍或減少每請求占用連線的時間（候選 B，另開 change）。

<!-- unmet-reason:seat-ticket -->
**座位票未達標原因**：優化 C 已把鎖內時間從中位數 8.77ms 降到 0.78ms（`measure-after-a-phases-seat-ticket.json`），序列化不再是主因；剩餘延遲同樣在鎖外：PreTransaction 中位數 351.1ms／p95 411.6ms、BeginTransaction 中位數 420.6ms／p95 620.6ms、EventLockWait p95 298.8ms，Total p95 1136.1ms（量測模式含 Debug log 輸出到 Seq；正式驗收無量測 log 時 P95 中位數 711.04ms）。瓶頸已轉為連線池等待，與數量票相同，後續方向同上（候選 B，另開 change）。
