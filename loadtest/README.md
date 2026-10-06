# k6 搶票壓測

500 名買家同時搶 50 張票，驗證 P95 < 500ms 與 0% 超賣（OpenSpec change `k6-load-test`；結果見 `docs/load-test/report.md`）。

| 腳本 | 內容 |
|---|---|
| `count-ticket.js` | 數量票：500 VU 各下 1 張 `GA` 純計數票（庫存 50） |
| `seat-ticket.js` | 座位票：500 VU 各從 `HOT` 分區 50 席隨機選 1 席 |
| `aggregate.js` | 彙整 12 份正式 summary JSON，產生 `/output/report-tables.md` |
| `baseline.js` | 瓶頸量測：無競爭基準（1 VU 依序 50 筆），見「瓶頸量測」 |
| `tests/*.test.js` | 判定與彙整邏輯的自我測試 |

## 前提

- 只支援本機 Docker Desktop／WSL2，Linux 原生 Docker 未驗證。k6 image 以非 root（uid 12345）執行。seeder（api 容器內的 root）每次都把 `loadtest/.output` 設為 0700、`tokens.json` 設為 0600，擁有者都是 12345:12345：只有 k6 能讀 token、寫 summary。
- 下列 k6 指令都帶 `MSYS_NO_PATHCONV=1` 前綴：Git Bash 會把 `/scripts/...` 改寫成 Windows 路徑。PowerShell／WSL 殼層可省略。
- Linux 權限只在 Docker／WSL2 內有效；Windows 主機端的存取由 NTFS ACL 決定，`C:\` 底下新建的目錄預設讓 `BUILTIN\Users`（本機所有帳號）完全控制。第一次使用前（以及重新 clone 後）在 Git Bash 執行一次，只保留自己、SYSTEM、Administrators：

  ```bash
  mkdir -p loadtest/.output && MSYS_NO_PATHCONV=1 icacls 'loadtest\.output' /inheritance:r /grant:r "$(whoami):(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F'
  ```

  還原：`MSYS_NO_PATHCONV=1 icacls 'loadtest\.output' /reset /T`。token 只對開發用 DB 的壓測帳號有效、30 分鐘過期，用完可刪（見「清理」）。
- 壓測 Admin（`loadtest-admin@loadtest.invalid`）與 500 名買家（`loadtest-buyer-0001@loadtest.invalid` …）是 Development 專用帳號，seeder 只在 `ASPNETCORE_ENVIRONMENT=Development` 且 DB Host 為 `db` 時執行。

## 每一次執行的固定順序

**每一次**壓測前都要重跑 seeder：token 有效期 30 分鐘，一批 6 次執行可能超過 30 分鐘，不能每批只跑一次。任何失敗（包含 401）都計入結果，不得以 token 過期為由排除或重跑。

1. 簽發 token（寫到 `loadtest/.output/tokens.json`）：

   ```bash
   docker compose exec api dotnet run --project tools/ProjectC.LoadTest.Seeder -- --buyers 500
   ```

   seeder 必須成功（exit code 0）才能繼續。剛壓測完時 api 的連線池可能還占著大量 PostgreSQL 連線（`max_connections` 為 100），seeder 會因 `too many clients` 失敗；等 30 秒後重跑即可，不得沿用舊的 token 檔直接執行 k6。

2. 確認 api 就緒（seeder 建置會寫入共用 bin/obj，`dotnet watch` 可能因此重新建置）。輪詢到回 200 為止，不用固定等待時間：

   ```bash
   until curl -fsS "http://localhost:$(docker compose port api 8080 | cut -d: -f2)/api/events" >/dev/null; do sleep 2; done
   ```

3. 執行 k6（`LT_API_BUILD` 為 `debug` 或 `release`，`LT_RUN` 為 `1`／`2`／`3`）：

   ```bash
   MSYS_NO_PATHCONV=1 docker compose --profile loadtest run --rm --no-deps k6 run -e LT_API_BUILD=debug -e LT_RUN=1 /scripts/count-ticket.js
   ```

   k6 一律帶 `--no-deps`：k6 有 `depends_on: api`，不帶時 Compose 會把以 Release 覆寫啟動的 api 重建回 `dotnet watch`（見下方 Release 組態對照）。

   結果寫到 `loadtest/.output/<scenario>-<LT_API_BUILD>-run<LT_RUN>-summary.json`。目標檔已存在時 k6 會中止、不覆寫既有結果。

壓測進行中不得執行 seeder 或 `dotnet test`，避免建置影響正在量測的 api。每次執行之間至少間隔 60 秒。

## 單次通過的定義

exit code 為 0 **且** summary JSON 的 `runVerdict.passed == true`。`handleSummary` 不保證能改變 exit code，「`QuantitySold` 等於 k6 成功筆數」只反映在 `runVerdict`。

## Release 組態對照

api 平時以 `dotnet watch`（Debug）執行。切換到 Release（只覆寫 api 的啟動指令，service name 與 k6 位址不變）：

```bash
docker compose -f docker-compose.yml -f docker-compose.loadtest-release.yml up -d --no-deps api
```

還原：

```bash
docker compose up -d --no-deps api
```

- 切換後**每一次**執行 k6 前都要確認 PID 1 仍含 `-c Release`（指令見下）。
- Release 首次啟動需要完整編譯，切換與還原後都用上面的輪詢指令確認就緒。
- 若 pgadmin（本機 `docker-compose.override.yml`）正在執行，切換時 Compose 會印 orphan 警告，無害。
- 確認目前組態（讀 api 容器 PID 1 的指令列；Release 會含 `-c Release`，Debug 為 `dotnet watch`）：

  ```bash
  docker compose exec api sh -c "tr '\\0' ' ' < /proc/1/cmdline"
  ```

- Release 仍搭配 `ASPNETCORE_ENVIRONMENT=Development`，不是完整的正式環境設定。

## 彙整報告資料表

12 次正式執行完成後：

```bash
MSYS_NO_PATHCONV=1 docker compose --profile loadtest run --rm --no-deps k6 run /scripts/aggregate.js
```

只讀白名單的 12 個檔名，產出 `loadtest/.output/report-tables.md`。缺少的欄位顯示「缺少」、缺少的檔案顯示「缺檔」，該組判未通過。

## 故障注入（`LT_FAULT`）

只能讓情況變壞，用來確認門檻真的會失敗。故障注入不需要 `LT_RUN`，結果寫到 `<scenario>-fault-<LT_FAULT>-summary.json`，不在彙整白名單內。

| 值 | 效果 |
|---|---|
| `p95` | P95 門檻改為 `p(95)<1` |
| `quantity51` | 數量票庫存改為 51（只適用 `count-ticket.js`） |
| `bad-admin-token` | setup 使用竄改過的 admin token，setup 中止 |
| `bad-buyer-token` | 下單使用竄改過的買家 token，全部 401 |
| `setup-timeout` | `setupTimeout` 改為 1ms（setup 有數十個 HTTP 請求，必然逾時），setup 逾時中止 |

## 瓶頸量測

OpenSpec change `order-placement-p95-optimization` 的「先量測後優化」工具。**量測執行與正式驗收執行必須分開**：分段 log 與等待事件取樣本身有成本，量測結果只用來看比例與分布；正式驗收（上面的 12 次執行）不得疊加量測 override、不得取樣、不得帶 `LT_MEASURE_TAG`。

### 量測標籤 `LT_MEASURE_TAG`

- 格式 `^[a-z0-9-]{1,32}$`；不符時 setup 中止，不送出任何下單。
- 帶標籤時 summary 寫到 `/output/measure-<tag>-<原檔名>`，不在彙整白名單內，不會被 `aggregate.js` 讀到；門檻與判定不變。已存在時同樣中止、不覆寫。
- 量測執行一律帶標籤（例如 `before`、`after-c`、`after-a`）。

### 開啟分段耗時 log

```bash
# Release + 分段 log（量測用）
docker compose -f docker-compose.yml -f docker-compose.loadtest-release.yml -f docker-compose.loadtest-measure.yml up -d --no-deps api
# 還原（Debug、關閉分段 log）
docker compose up -d --no-deps api
```

- `docker-compose.loadtest-measure.yml` 只把 `OrderService` 降到 Debug。Console sink 在 `appsettings.json` 限制為 Information，分段 log 只進 Seq（`http://localhost:8081`）。
- 每筆下單一行：`PlaceOrder phase timings: Outcome=… HasSeatItems=… PreTransactionMs=… BeginTransactionMs=… EventLockWaitMs=… InLockMs=… CommitMs=… TotalMs=…`（未走到的分段為 null）。
- 切換後依「Release 組態對照」確認 PID 1，並確認環境變數：`docker compose exec api printenv | grep Serilog__`。

### 無競爭基準（`baseline.js`）

1 個 VU 依序送 50 筆下單，每筆不同買家（setup 先確認前 50 個 token 的 JWT `sub` 互不相同）、座位票每筆不同座位，全部都應為 201：

```bash
MSYS_NO_PATHCONV=1 docker compose --profile loadtest run --rm --no-deps k6 run -e LT_BASELINE_TICKET=seat -e LT_MEASURE_TAG=before /scripts/baseline.js
```

- `LT_BASELINE_TICKET=count|seat`；必須帶 `LT_MEASURE_TAG`。輸出 `measure-<tag>-baseline-<count|seat>-summary.json`，stdout 與 summary 的 `baseline` 欄位含 `p50`、`p95`、`isValid`。
- 任一筆非 201 → k6 非 0 結束、該次無效；429 另標示「被限流，量測無效」（不得調整限流設定來湊結果）。

### 等待事件取樣（`sample-db-waits.sh`）

壓測開始前另開一個終端啟動，秒數涵蓋整個下單時段（約 90 秒）：

```bash
bash loadtest/sample-db-waits.sh before seat-ticket 90
```

- 每 200ms 查一次 `pg_stat_activity`（`sql/db-waits.sql`），依 `state`／`wait_event_type`／`wait_event` 分組計數，輸出 `loadtest/.output/measure-<tag>-db-waits-<scenario>.csv`；已存在時中止。
- 取樣連線的 application name 為 `lt-db-waits`，占 1 條連線（報告須註明）；啟動前有殘留即中止並印出清理 SQL；正常結束或 Ctrl+C 都會終止該連線並確認沒有殘留。非 0 結束即該次量測無效。

### 每次量測後必做

記下壓測開始與結束的 UTC 時間（`date -u +%Y-%m-%dT%H:%M:%SZ`），結束後立即：

```bash
# 1. 有效性檢查：Seq 分段 log 筆數 = 有回應且非 429 的下單數、Console 沒有分段 log、沒有逾時。非 0 即無效，不得寫入報告。
bash loadtest/check-measure-logging.sh <start-utc> <end-utc> loadtest/.output/measure-before-seat-ticket-release-run1-summary.json
# 2. 匯出分段統計（Seq 未掛 volume，容器重建後就沒了）
bash loadtest/export-measure-phases.sh <start-utc> <end-utc> loadtest/.output/measure-before-phases-seat-ticket.json
```

- 匯出內容：全部樣本與依 `Outcome` 分組的各分段筆數（非 null）、p50、p95、max（毫秒），以及原始值（`raw`）。以 Seq 的 `/api/data` 取出原始值，再由 `lib/phase-stats.jq` 精確計算（排序後線性內插，與 k6 的 `p(N)` 同定義）：

  ```sql
  select Outcome, PreTransactionMs, BeginTransactionMs, EventLockWaitMs, InLockMs, CommitMs, TotalMs
  from stream where @MessageTemplate like 'PlaceOrder phase timings:%' limit 100000
  ```

  不用 Seq SQL 的 `percentile()`：實測它是近似值（同批 50 筆 `EventLockWaitMs` p95 精確值 0.731ms，Seq 回 0.779ms），在 Seq UI 用 `percentile()` 查到的數字會與匯出檔略有差異。取回筆數與 `count(*)` 不符時中止、不寫檔。
- 時間範圍只用來篩選 log，耗時一律以應用端單調時鐘計算（WSL2 牆上時鐘會倒退）。

### 報告比對（`check-report-tables.sh`）

```bash
bash loadtest/check-report-tables.sh docs/load-test/p95-optimization-report.md loadtest/.output/report-tables.md
```

比對報告 `<!-- report-tables:begin -->`／`<!-- report-tables:end -->` 之間的資料表與彙整輸出；Release 未達標的 scenario 必須有 `<!-- unmet-reason:<scenario> -->` 區段且引用 `measure-` 檔名與毫秒數值；報告須含 `P95 < 500ms`。

## 自我測試

```bash
MSYS_NO_PATHCONV=1 docker compose --profile loadtest run --rm --no-deps k6 run /scripts/tests/verify.test.js
MSYS_NO_PATHCONV=1 docker compose --profile loadtest run --rm --no-deps k6 run /scripts/tests/report.test.js
MSYS_NO_PATHCONV=1 docker compose --profile loadtest run --rm --no-deps k6 run /scripts/tests/aggregate-flow.test.js
MSYS_NO_PATHCONV=1 docker compose --profile loadtest run --rm --no-deps k6 run /scripts/tests/config.test.js
MSYS_NO_PATHCONV=1 docker compose --profile loadtest run --rm --no-deps k6 run /scripts/tests/baseline.test.js
# 瓶頸量測的 shell 腳本（sample-db-waits 會啟動一次性的 postgres:16-alpine 容器，約 1 分鐘）
bash loadtest/tests/check-measure-logging.test.sh
bash loadtest/tests/check-report-tables.test.sh
bash loadtest/tests/phase-stats.test.sh
bash loadtest/tests/sample-db-waits.test.sh
```

exit code 為 0 代表全部 check 通過。`aggregate-flow.test.js` 會在 `loadtest/.output/` 留下 `aggregate-flow-report-tables.md`，不在彙整白名單內，可直接刪除。

## 清理

- token 檔可隨時手動刪除：`rm loadtest/.output/tokens.json`（整個 `loadtest/.output/` 已列入 `.gitignore`）。
- 每次執行都會建立新的場館與活動，活動 Title 以 `[LoadTest] ` 開頭，場館名稱以 `[LoadTest] venue` 開頭。產品沒有刪除活動的 API，需要清理時建議直接重建開發用 DB volume，或以 pgAdmin 依 Title 前綴辨識後手動處理。
