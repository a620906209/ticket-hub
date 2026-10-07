# 下單 P95 優化第二階段結果報告

> OpenSpec change：`order-placement-p95-phase2`。結果只代表本機 Docker Compose／WSL2 環境（api 與 PostgreSQL 同機、`max_connections` 100），不代表正式環境容量。
> 目標：Release 下 P95 < 500ms（201 與 409 合併計算）。先前基準 `docs/load-test/report.md`、`docs/load-test/p95-optimization-report.md` 不修改。

## 1. 起點（引用第一階段，不重量）

第一階段正式驗收（`docs/load-test/p95-optimization-report.md` 第 4、5 節）：Release P95 中位數數量票 814.45ms、座位票 711.04ms，未達成 P95 < 500ms。

第一階段最後一次量測（`LT_MEASURE_TAG=after-a`，Release，量測模式）的分段 p95（毫秒）：

| Scenario | PreTransaction | BeginTransaction | EventLockWait | InLock | Commit | Total | k6 P95 | 檔案 |
|---|---|---|---|---|---|---|---|---|
| 座位票 | 411.6 | 620.6 | 298.8 | 1.3 | 5.4 | 1136.1 | 1249.27 | `measure-after-a-phases-seat-ticket.json` |
| 數量票 | 664.2 | 548.0 | 296.7 | 1.9 | 4.0 | 922.6 | 938.46 | `measure-after-a-phases-count-ticket.json` |

第一階段判定剩餘延遲在鎖外：Event 列 `FOR UPDATE` 讓同活動的下單逐筆通過（EventLockWait），排隊中的交易各占一條連線，造成連線池等待（BeginTransaction）。

## 2. B1：下單改以 `FOR SHARE` 鎖 Event（`LT_MEASURE_TAG=after-b1-warm`）

量測方式（design 決策 3 的冷啟動控制）：Release + `docker-compose.loadtest-measure.yml`，force-recreate api 後先跑座位票、數量票各 1 次暖身（`after-b1-warm-warmup`，捨棄），再各量 1 次，每次間隔 60 秒。執行前確認無外部 `dotnet test`、無殘留 Testcontainers。

分段 p95（毫秒，500 筆）：

| Scenario | PreTransaction | BeginTransaction | EventLockWait | InLock | Commit | Total | k6 P95 | 成功／409／5xx | 檔案 |
|---|---|---|---|---|---|---|---|---|---|
| 座位票 | 311.0 | 100.5 | **20.0** | 44.1 | 29.3 | 359.0 | 384.54 | 50／450／0 | `measure-after-b1-warm-phases-seat-ticket.json` |
| 數量票 | 163.8 | 311.1 | **3.5** | 224.8 | 18.1 | 481.5 | 496.44 | 50／450／0 | `measure-after-b1-warm-phases-count-ticket.json` |

兩次皆 `check-measure-logging.sh` 通過（Seq 500 筆）、api log 無 `too many clients` 或連線池逾時、限流 0 筆、超賣 0。

**EventLockWait 明顯下降**（座位票 p95 298.8 → 20.0ms、數量票 296.7 → 3.5ms），B1 採用條件的第一項成立；第二項（切換排隊模式延遲探測）見第 4 節。

- 等待移到鎖內：InLock 從約 1ms 升到座位票 p95 44.1ms、數量票 p95 224.8ms。InLock 包含座位／票種 `FOR UPDATE` 的等待，Event 不再逐筆通過後，同座位（座位票）與同票種（數量票）的請求改在這些列上互等。數量票所有請求鎖同一筆 `TicketType`，總序列化不變，與 design「數量票熱點」風險一致。
- Total p95 座位票 1136.1 → 359.0ms、數量票 922.6 → 481.5ms；k6 P95 座位票 1249.27 → 384.54ms、數量票 938.46 → 496.44ms。各只跑 1 次，量測模式含 Debug log 成本，僅看比例。

**作廢的第一次量測（`after-b1`，2026-10-06 18:36～18:43 UTC）**：api 重啟後第一個 scenario（座位票）是冷啟動，k6 P95 694.20ms、Total p95 578.5ms，比暖機後的 384.54／359.0ms 慢很多；數量票 621.21／596.6ms。第一次 `after-b2` 也有同樣問題（數量票為重啟後第一次），兩版各有一個 scenario 冷啟動，一度誤判 B2 讓數量票變慢並回退。冷啟動的影響比 B1／B2 之間的差距還大，因此改為上述控制暖機的量測，原始檔案保留在 `loadtest/.output/` 供查。

## 3. B2：每筆下單只借一次連線（`LT_MEASURE_TAG=after-b2-warm`）

量測方式同第 2 節（暖身 `after-b2-warm-warmup` 捨棄）。

分段 p95（毫秒；ConnectionOpen、PreTransaction、Total 為 500 筆，BeginTransaction、EventLockWait、InLock 只計進交易的請求，見下方「樣本數不同」）：

| Scenario | ConnectionOpen | PreTransaction | BeginTransaction | EventLockWait | InLock | Commit | Total | k6 P95 | 成功／409／5xx | 檔案 |
|---|---|---|---|---|---|---|---|---|---|---|
| 座位票 | 165.4 | 186.8 | 0.02 | 15.0 | 45.0 | 43.8 | 197.8 | 235.47 | 50／450／0 | `measure-after-b2-warm-phases-seat-ticket.json` |
| 數量票 | 286.1 | 287.9 | 0.02 | 7.3 | 313.6 | 21.1 | 372.9 | 380.78 | 50／450／0 | `measure-after-b2-warm-phases-count-ticket.json` |

兩次皆 `check-measure-logging.sh` 通過、api log 無 `too many clients`、限流 0 筆、超賣 0。

- **樣本數不同**：BeginTransaction、EventLockWait、InLock 在 B1 皆為 500 筆，B2 只有座位票 133 筆、數量票 149 筆（其餘在交易前就以提早 409 結束，沒有這三段）。以下這三段的前後比較是不同母體的 p95，不是同一批請求的對照；B2 判定只依 n 皆為 500 的 Total 與 k6 P95。
- **提早 409 在 B2 才生效**：B1 的兩次量測 500 筆全部進交易（提早 409 為 0 筆）；B2 下座位票 367 筆、數量票 351 筆在交易外就回 409。機制是推論（log 沒有逐查詢的時間可直接驗證；以下 commit 時間取成功 log 時間，含 commit 後的 Redis 呼叫），兩個 scenario 不同：數量票要等第 50 筆成功（售完）後才可能提早 409，售完時間 B1 約在第一個請求後 +374ms、B2 約 +334ms，B2 的 351 筆提早 409 拿到連線的時間全在 +333ms 之後（此處改用分段推回的 commit 結束：最早一筆比最後一筆售出的 commit 結束晚約 0.04ms，若用成功 log 時間反而早約 0.1ms，皆在換算誤差內，推測是售出者歸還的連線直接交給它），也就是交易前讀取排到連線池等待之後、售完後才讀；座位票只要該座位被買走就可能提早 409，B2 第一筆 commit 約 +23ms（B1 約 +247ms），先拿到連線的請求不必在 BeginTransaction 再排隊、更早提交，加上交易前讀取排在連線池等待之後，讀到已提交的結果。B1 下座位票 19 筆、數量票 9 筆的交易前階段結束晚於最後一筆成功，仍沒有提早 409；推論的前提是決定 409 的座位／票種讀取在交易前階段的前段（之後還有活動、實名等查詢，B1 每次查詢各自排連線池）。B2 的改善可能有部分來自這個效果，不只是「連線池只排一次」，且與突發流量的形狀有關；但未量化：數量票 B2 最慢的 25 筆（p95 尾端）全是進交易的請求，座位票則有 15 筆是提早 409，提早 409 對尾端的幫助若有，主要是少佔連線的間接效果。
- 連線池等待集中到開頭一次：BeginTransaction p95 從座位票 100.5ms、數量票 311.1ms 降到 0.02ms，等待移到 ConnectionOpen（座位票 165.4ms、數量票 286.1ms）。
- 數量票 InLock p95 224.8 → 313.6ms：拿到連線的請求更快進到 `TicketType` 鎖互等，熱點本身沒有消失。
- `OpenConnectionAsync` 本身拋例外（例如等連線池逾時）時 ConnectionOpen 為 null，不會計入統計；本次 500 筆皆有值，沒有這種情況。日後若出現連線池逾時，ConnectionOpen p95 會低估等待，須同時看例外 log 數。
- Total p95 座位票 359.0 → 197.8ms、數量票 481.5 → 372.9ms；k6 P95 座位票 384.54 → 235.47ms、數量票 496.44 → 380.78ms。

**判定：B2 採用**（兩個 scenario 的 Total p95 與 k6 P95 都比 B1 快，design 決策 5）。

## 4. 切換排隊模式延遲探測（`LT_MEASURE_TAG=after-b2-probe4`，B2 狀態）

另跑一次座位票量測（force-recreate api，先暖身 1 次 `after-b2-probe4-warmup` 捨棄），不納入分段比較。觸發方式：k6 log 出現 `setup ok` 後啟動 sidecar 容器（api image、共用 db 的 PID namespace、接在 compose 網路上），輪詢 db 的 postgres process title，執行中或交易中的連線達 3 條即視為突發流量開始（腳本留存於 `loadtest/.output/queue-switch-probe-after-b2-probe4.sidecar.sh`），直接在容器網路內以 admin API 送出 PATCH `queue-mode`（開，再關），客戶端逾時 30 秒。sidecar 不開資料庫連線（api 連線池 100 = `max_connections` 100，多一條會讓 api 拿不到連線）。結果：`loadtest/.output/queue-switch-probe-after-b2-probe4.txt`。

重疊判定只用 Seq 的伺服器時間（api 同一行程、同一時鐘）：切換開始 = PATCH 請求 log 時間 − 耗時；共享鎖持有區間 = 下單分段 log 推回的「取得 Event 鎖」到「Commit 結束」（EventLockWaitMs 有值的 142 筆）。

| 切換 | 伺服器開始（UTC） | 狀態 | 伺服器耗時 | 開始時持有共享鎖的請求 | 切換開始後才取得共享鎖的請求 |
|---|---|---|---|---|---|
| 開啟 | 21:38:06.809 | 204 | 222.4ms | 13 | 128 |
| 關閉 | 21:38:07.038 | 204 | 3.6ms | 0 | 0 |

- 共享鎖持有區間 21:38:06.713～07.024；開啟切換在 07.031 完成。
- **延遲主要來自 api 端積壓，不是列鎖**：切換開始時 500 筆中已有 499 筆進入下單流程（下單 log 以 TotalMs 往回推，不含 middleware），其中 435 筆還在等連線（ConnectionOpen）。PATCH 與下單共用同一個連線池（同一個 `AddDbContext`），handler 先開交易借連線再 `GetForUpdateAsync`。（ConnectionOpen 也含建立實體連線的時間，不全是排隊。）PATCH 拿到連線的時間沒有量到，下限依以下推論：切換開始時持有連線的有 63 筆（另 1 筆已結束），在第一筆釋放（+14.5ms）前又有 5 筆拿到連線，表示當下還在建立實體連線；499 筆需求遠超過連線池上限 100（重建的同時持有數最高 101 可佐證），推論連線池名額已分配完（含建立中的實體連線，所以持有數 63 低於 100），PATCH 只能排隊；連線池滿之後拿到連線的順序近似先到先得（切換開始後拿到連線的 436 筆中，順序倒置的配對約 1.5%），切換前 20ms 內進入的 249 筆在 +123～+169ms 才拿到連線，這 249 筆被任何後到者超前最多 18.9ms。更大的超前都發生在實體連線仍在建立的時段：全體最大 121ms 那一對兩方都在突發開頭進入，被超前者在切換開始後 +49.7ms 才拿到連線，同樣落在實體連線建立的時段；切換開始後到約 +52ms 仍有新連線建立完成，期間有一筆 S−83.4ms 進入、+51.3ms 拿到連線的請求超前 53.3ms，推測是拿到剛建好的實體連線，不是排隊超前。越接近切換開始進入的請求越晚拿到連線：切換前最後 10 筆（S−5.0～−0.3ms 進入）在 +157.8～+168.8ms 拿到。PATCH 最可能在 +158～+169ms 拿到連線；下表 +123ms 是寬鬆下限（須超前約 35～46ms，大於觀察到的 18.9ms），+169ms 也不是上限（倒置約 1.5%，晚於 +169ms 時之後才取得共享鎖的可能為 0 筆）。依 PATCH 拿到連線的可能時間推算：

  | PATCH 拿到連線 | 之後才取得共享鎖 | 當時持有共享鎖 | 列鎖等待上限 |
  |---|---|---|---|
  | +123ms（06.932） | 26 筆 | 9 筆 | 約 92ms |
  | +150ms | 8 筆 | 7 筆 | 約 65ms |
  | +158ms（最可能區間起點） | 6 筆 | 6 筆 | 約 57ms |
  | +169ms（06.978） | 4 筆 | 6 筆 | 約 46ms |

  表中「128 筆」大多是切換還沒拿到連線時就取得共享鎖的請求，不能算是插隊。切換 handler 沒有分段 log，以上由下單 log 推算，沒有直接量到切換送出 `FOR UPDATE` 的時間點；時間都來自 api 容器的牆上時鐘加 Stopwatch 耗時，WSL2 時鐘偶有跳動；以此重建的同時持有連線數最高算到 101（上限 100），換算誤差約 1ms 等級。列鎖等待上限未扣除切換 commit、Redis 與回應的時間（最後一個持有者釋放到 PATCH log 約 7.6ms）。
- **寫入者飢餓本次未觀測到，也無法排除**：列鎖等待上限約 46～92ms（最可能約 46～57ms）、切換拿到連線後才取得共享鎖的約 0～26 筆（最可能 4～6 筆）。這次探測量到的是端到端上限：222ms 遠低於 3 秒門檻，**B1 採用**（design 決策 3）。若要直接量列鎖等待，需在切換 handler 的 `GetForUpdateAsync` 前後加分段 log，本 change 不做。
- 500 筆皆為成功或 409、沒有 403：切換在所有持有者結束後才提交，突發流量中的下單都讀到排隊模式關閉。
- 持續流量下共享鎖持有者可能沒有空檔，切換的等待不再有上限；本次 k6 是一次性突發，量不到（**已接受的限制**：B1 採用只建立在一次性突發的量測上，design 決策 3 已知風險，6.x 一併記入 project-scope §8）。本次沒有排隊中的使用者，未觀察入場推進。

作廢的探測（皆未與突發流量重疊，不採計）：
- `after-b1-probe`（2 次，B1 狀態）、`after-b2-probe`、`after-b2-probe2`：主機依 `setup ok` 定時送出。最初報告曾以主機 curl 時間對照 Seq 時間判定 `after-b2-probe` 重疊，對抗式審查以 Seq 的 PATCH log 查出切換其實晚於最後一筆下單約 81ms（主機與容器時鐘差約 130～210ms 且不固定），已更正。
- `after-b2-probe3`：主機經 `docker compose exec` 讀 db 的 process title 觸發，Windows 端的串流與 curl 延遲讓切換仍晚約 320ms。

## 5. 正式驗收（Release／Debug × 數量票／座位票各 3 次）

2026-10-07 03:51～04:48 UTC 依 `loadtest/README.md` 執行，流程與第一階段相同：不帶 `LT_MEASURE_TAG`、不疊加量測 override（每次執行前確認 api 環境變數不含 `Serilog__MinimumLevel__Override__*`、PID 1 組態正確）、不取樣、每次前重跑 seeder、間隔至少 60 秒。先 force-recreate api 為 Release 跑 3 輪（每輪數量票、座位票各 1 次），再 force-recreate 回 Debug 跑 3 輪；**沒有捨棄用的暖身**（README 未要求，維持與第一階段同口徑）。開始前確認 api 容器內無外部 `dotnet test`、殘留的 Testcontainers 已清除。第一階段的 12 份 summary 與 `report-tables.md` 已移到 `loadtest/.output/p95-optimization-phase1/`。

- 12 次的 `place_order_unexpected` 皆為 0（無 401 等非預期狀態）、成功 50、409 450、5xx 0、QuantitySold 50、超賣 0。
- seeder 除了 api 重建後的第一次以外，每次都因前一輪留下的連線占滿 `max_connections` 而失敗 4 次（`Routine: InitProcess`，共 40 次），每次間隔 30 秒；暫存腳本只記錄失敗、第 5 次的結果沒有記錄，因此無法直接證明每次 k6 都用到當次簽發的 token（README 要求不得沿用舊 token）。12 次皆無 401，只能證明 token 未過期。第一階段同樣重試（`p95-optimization-phase1/formal.log` 38 次）。

下表為 `aggregate.js` 輸出，未經修改。

<!-- report-tables:begin -->
### Release × 數量票（count-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 918.34 | 945.86 | 50 | 450 | 0 | 50 | 102.00 | 未通過 |
| 2 | 414.61 | 431.75 | 50 | 450 | 0 | 50 | 105.00 | 通過 |
| 3 | 369.37 | 376.18 | 50 | 450 | 0 | 50 | 113.00 | 通過 |

- P95 (ms)（3 次有值）：中位數 414.61／最小值 369.37／最大值 918.34
- P99 (ms)（3 次有值）：中位數 431.75／最小值 376.18／最大值 945.86
- 判定：未通過
  - 第 1 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500

### Release × 座位票（seat-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 241.28 | 249.74 | 50 | 450 | 0 | 50 | 89.00 | 通過 |
| 2 | 203.26 | 223.60 | 50 | 450 | 0 | 50 | 112.00 | 通過 |
| 3 | 266.67 | 278.31 | 50 | 450 | 0 | 50 | 119.00 | 通過 |

- P95 (ms)（3 次有值）：中位數 241.28／最小值 203.26／最大值 266.67
- P99 (ms)（3 次有值）：中位數 249.74／最小值 223.60／最大值 278.31
- 判定：通過

### Debug × 數量票（count-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 693.90 | 704.26 | 50 | 450 | 0 | 50 | 56.00 | 未通過 |
| 2 | 408.88 | 423.72 | 50 | 450 | 0 | 50 | 127.00 | 通過 |
| 3 | 392.43 | 404.87 | 50 | 450 | 0 | 50 | 117.00 | 通過 |

- P95 (ms)（3 次有值）：中位數 408.88／最小值 392.43／最大值 693.90
- P99 (ms)（3 次有值）：中位數 423.72／最小值 404.87／最大值 704.26
- 判定：未通過
  - 第 1 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500

### Debug × 座位票（seat-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 257.82 | 266.59 | 50 | 450 | 0 | 50 | 92.00 | 通過 |
| 2 | 219.41 | 225.39 | 50 | 450 | 0 | 50 | 62.00 | 通過 |
| 3 | 222.66 | 226.54 | 50 | 450 | 0 | 50 | 87.00 | 通過 |

- P95 (ms)（3 次有值）：中位數 222.66／最小值 219.41／最大值 257.82
- P99 (ms)（3 次有值）：中位數 226.54／最小值 225.39／最大值 266.59
- 判定：通過
<!-- report-tables:end -->

- 數量票 Release、Debug 都是第 1 次（api 重建後的第一次執行）未通過，第 2、3 次皆通過；座位票 6 次全部通過。
- 與第一階段（`docs/load-test/p95-optimization-report.md` 第 4 節）比較，Release P95 中位數：數量票 814.45 → 414.61ms，座位票 711.04 → 241.28ms。

<!-- unmet-reason:count-ticket -->
**數量票未達標原因**：判定要求 3 次都通過，第 1 次是 api 重建後的第一次執行（冷啟動），Release P95 918.34ms，第 2、3 次為 414.61、369.37ms。同版程式碼（B2）的冷、暖量測：`measure-after-b2-count-ticket-release-run1-summary.json`（重啟後第一次）k6 P95 733.89ms、Total p95 637.7ms、ConnectionOpen p95 519.7ms；`measure-after-b2-warm-count-ticket-release-run1-summary.json`（暖身後）k6 P95 380.78ms、Total p95 372.9ms、ConnectionOpen p95 286.1ms（分段見 `measure-after-b2-phases-count-ticket.json`、`measure-after-b2-warm-phases-count-ticket.json`）。冷啟動多出的時間大半落在 ConnectionOpen（含建立實體連線、連線池等待），推測與 JIT、連線池從零建立 100 條連線有關，**未驗證**。座位票的第 1 次排在數量票之後，已非冷啟動。暖機後的數量票 P95 已低於 500ms；要讓冷啟動的第一波也達標，需減少突發時占用的連線數或預先建立連線（候選：Redis 預扣閘門，先在 Redis 擋掉售完後的請求、不進資料庫；另開 change，見 `docs/project-scope.md` §8）。

**基準檔驗證（LT-REPORT-007）**：`bash loadtest/check-baseline-reports.sh verify`（exit 0）

```
MASTER_SHA=8922118d76ed9b0ec94f5b9b668b5856ce0b720f
BASELINE=8922118d76ed9b0ec94f5b9b668b5856ce0b720f
START_HEAD=2f95ca11605be49db79faee666cf323474dbff36
HEAD=2f95ca11605be49db79faee666cf323474dbff36
OK: phase 1 reports unchanged since BASELINE (docs/load-test/report.md docs/load-test/p95-optimization-report.md)
```

`START_HEAD` 是 record 當下的 HEAD；B1、B2 的程式碼在量測與驗收時尚未 commit（工作樹），因此 `START_HEAD` 不代表量測的程式碼版本。此檢查只證明第一階段報告未被修改，不受影響。

## 6. 最終判定與限制

- **座位票：達成** Release P95 < 500ms（3 次皆通過，中位數 241.28ms）。
- **數量票：未達成**（依 3 次皆須通過的判定）。暖機後 2 次為 414.61、369.37ms，冷啟動第 1 次 918.34ms，原因見第 5 節。
- B1、B2 皆採用；12 次無超賣、無 5xx。
- 限制：
  - 切換排隊模式只在突發流量下探測 1 次（第 4 節，222ms），寫入者飢餓本次未觀測到但無法排除；持續流量下加入排隊與共享鎖重疊的情況沒有壓測場景，**已接受**，記入 `docs/project-scope.md` §8。
  - 正式驗收不含暖身，冷啟動對數量票的影響大於 B1／B2 之間的差距；冷啟動原因未驗證。
  - 環境為單機 Docker Desktop（WSL2），Release 仍以 `ASPNETCORE_ENVIRONMENT=Development` 執行。
