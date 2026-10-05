> 所有指令都在容器內執行：.NET 用 `docker compose exec api ...`，k6 用 `docker compose --profile loadtest run --rm --no-deps k6 ...`。不在本機執行 dotnet 或 k6。
> 測試名稱前綴 Scenario ID（例如 `[LT-SEED-001]`），方便追溯。
> **不改產品程式碼**：`src/` 底下任何檔案都不得變更，strict-reviewer 審查時要確認這一點。
>
> **測試種類與 AC 對應**：
>
> | Scenario | 驗證方式 | 任務 |
> | --- | --- | --- |
> | LT-SEED-003、LT-SEED-005、LT-SEED-007 | xUnit 單元測試（進入點檢查；不碰 DB） | 2.5 |
> | LT-SEED-001、LT-SEED-002、LT-SEED-004、LT-SEED-006、LT-SEED-008 | xUnit 整合測試（Testcontainers PostgreSQL，直接呼叫 runner） | 2.6 |
> | LT-SEED-001（token 檔 JSON 格式） | xUnit 單元測試（序列化，不碰 DB） | 2.5 |
> | LT-SEED-004（完整 stdout／stderr 路徑） | 開發環境實跑後比對輸出 | 2.7 |
> | LT-GATE-004 | 判斷邏輯：k6 自我測試 `loadtest/tests/verify.test.js`（假資料逐條觸發失敗條件，可重複執行）；完整接線（verify scenario → `oversell_check_failed` → exit code）：`LT_FAULT=quantity51` 故障注入 | 4.6、5.6 |
> | LT-SETUP-001 | k6 實際執行 | 5.2 |
> | LT-SETUP-002、LT-SETUP-003、LT-GATE-002、LT-GATE-003、LT-GATE-005、LT-GATE-006 | k6 故障注入（`-e LT_FAULT=...`，不改檔案），並檢查 summary JSON 中預期失敗的 threshold | 5.3、5.6 |
> | LT-RUN-001、LT-RUN-002、LT-GATE-001 | k6 實際執行（compose 內） | 5.4、5.5 |
> | LT-COMPOSE-001、LT-COMPOSE-002、LT-COMPOSE-003 | 指令驗證 | 3.3、5.4、2.7 |
> | LT-RELEASE-001 | 指令驗證（切換與還原，檢查程序指令列與容器是否被重建） | 3.5 |
> | LT-RELEASE-002 | k6 實際執行（未設定與非法值各一次） | 5.3 |
> | LT-REPEAT-001、LT-REPEAT-002 | 判斷邏輯：k6 自我測試 `loadtest/tests/report.test.js`（假 summary 資料，3 次全過／1 次失敗／缺檔）；實際資料：12 次執行後跑彙整腳本 | 4.8、5.4、5.5、6.1 |
> | LT-REPORT-003、LT-REPORT-004 | 檔案流程：k6 自我測試 `loadtest/tests/aggregate-flow.test.js`（fixture 目錄走實際讀檔、彙整、寫檔流程） | 4.9 |
> | LT-REPORT-001、LT-REPORT-002、LT-REPORT-003 | 判斷邏輯：k6 自我測試 `loadtest/tests/report.test.js`（表格數字等於輸入；`QuantitySold ≠ orders_created` 判失敗）；實際資料：報告資料表與彙整輸出 `diff` | 4.8、6.2 |
>
> k6 腳本不以 xUnit 涵蓋，屬 design.md 決策 9 記錄的核准例外；改以 k6 自我測試與故障注入證明門檻確實能讓壓測失敗（CLAUDE.md 規則 9）。

## 1. 專案骨架

- [x] 1.1 用 `docker compose exec api dotnet new console` 建立 `tools/ProjectC.LoadTest.Seeder`，reference `ProjectC.Infrastructure`；用 `dotnet new xunit` 建立 `tests/ProjectC.LoadTest.Seeder.Tests`，reference seeder 專案。兩個專案都加入 `ProjectC.slnx`，套件版本走 `Directory.Packages.props`
- [x] 1.2 `docker-compose.yml` 的 api 服務加上兩個新專案 bin/obj 的 named volume（design 決策 8）；`.gitignore` 加入 `loadtest/.output/`

## 2. Seeder

- [x] 2.1 seeder 的進入點（`Program`）在建立 DbContext 之前完成所有檢查，任一不符就以非 0 exit code 結束（design 決策 1、「安全確認」）：
  - `ASPNETCORE_ENVIRONMENT` 必須是 Development；
  - 連線字串 Host 必須是 `db`；
  - `Jwt:Issuer`、`Jwt:Audience`、`Jwt:SigningKey` 都有值，且 SigningKey 至少 32 個字元（與產品 `JwtOptions` 相同）；
  - 只接受 `--buyers`（預設 500，整數 1–1000），其他參數一律拒絕；
  - 設定以 `AddEnvironmentVariables()` 明確讀取；Admin 隨機密碼用 `RandomNumberGenerator` 產生；
  - token 檔路徑寫死為 `/src/loadtest/.output/tokens.json`。
- [x] 2.2 自行註冊 DI：`ApplicationDbContext`（Npgsql）、`BCryptPasswordHasher`、`JwtTokenService` + `JwtOptions`、`DevelopmentDataSeeder`（Scoped），以及 `IDateTimeProvider`（Singleton）與 `AddLogging`。設定來源是環境變數，對應 api 容器內已有的 `ConnectionStrings__*` 與 `Jwt__*`。
  - 寫入邏輯放在 `LoadTestSeedRunner`，由參數接收買家數、輸出路徑、Admin 密碼與 `TextWriter`；
  - runner 一開始檢查 pending migration，有的話以非 0 結束並提示 migration 指令。
- [x] 2.3 壓測 Admin：
  - 用固定 email `loadtest-admin@loadtest.invalid` 和隨機密碼呼叫 `DevelopmentDataSeeder.SeedAsync`（design 決策 2）；
  - 依 design 決策 2 的 runner 流程：查回 Admin、確認 Role 是 Admin、以單一查詢取得恰好 1 個 Approved「LoadTest Organizer」，以 `GenerateAccessToken(admin, organizerId)` 簽發 admin token；任一不符以非 0 結束、說明原因，且不寫 token 檔。
- [x] 2.4 買家會員與 token 檔：
  - 冪等批次建立 `loadtest-buyer-{0001..N}@loadtest.invalid`：以單一查詢取得既有 email，共用一次 BCrypt 雜湊（design 決策 3）；
  - 確認會員是 Active，必要時呼叫 `Activate()`；
  - 依 email 排序簽發買家 token，寫檔前先 `Directory.CreateDirectory`，再寫入 token 檔（design 決策 4）；
  - 標準輸出只印筆數和路徑。
  - 全程傳遞 `CancellationToken`（Ctrl+C）。
- [x] 2.5 單元測試（進入點，不碰 DB）：
  - 可觀測接縫：進入點以注入的 DbContext 工廠委派（`Func<ApplicationDbContext>`）建立 DbContext，測試以「工廠未被呼叫」斷言「不建立 DbContext」；
  - `[LT-SEED-003]` 非 Development 時回傳非 0，且不建立 DbContext、不寫檔；
  - `[LT-SEED-007]` Issuer、Audience、SigningKey 各自為空時，以及 SigningKey 為 31 個字元時，各自回傳非 0，且不建立 DbContext、不寫檔；SigningKey 為 32 個字元時通過檢查（邊界）；
  - `[LT-SEED-005]` `--buyers 0`、`-1`、`1001`、`abc`、`--output x`、連線字串 Host 為 `localhost` 時，各自回傳非 0，且不建立 DbContext、不寫檔；`--buyers 1` 與 `1000` 通過檢查（邊界）；
  - `[LT-SEED-001]` token 檔 JSON 格式（`issuedAtUtc`、`adminToken`、`buyerTokens` 筆數）。
- [x] 2.6 整合測試（Testcontainers PostgreSQL；不連開發用 db）：
  - `[LT-SEED-001]` 首次執行（N = 5）建立 5 個 Active 買家，以及 1 個 Admin + Approved Organizer。token 檔有 5 個不重複的買家 token，每個都能用與 `Program.cs` 相同設定的 `TokenValidationParameters` 驗證成功、`sub` 各不相同；admin token 帶 OrganizerId claim。
  - `[LT-SEED-002]` 再跑一次後，會員數仍是 5、Admin 仍是 1 個，且 token 檔已被覆寫（`issuedAtUtc` 更新）。
  - `[LT-SEED-004]` 成功執行時以 `StringWriter` 擷取 runner 輸出，斷言不含 token 檔中任何一個 token，也不含測試傳入的 Admin 密碼。
  - 輸出目錄不存在時（指定一個尚未建立的暫存子目錄），runner 會自行建立目錄並寫出 token 檔。
  - 權限（2026-10-05 審查後補，design 決策 10「檔案權限」）：先前以 root 0755 建立的輸出目錄會改為 0700、擁有者 12345:12345；已存在的 0644 token 檔會被換成 0600、擁有者 12345:12345 的新檔，且不留下 `.tmp`。
  - `[LT-SEED-006]` 對未套用 migration 的空 DB 執行，回傳非 0、輸出含 migration 指令提示，沒有產生 token 檔，且 DB 中沒有建立任何資料表（未寫入 DB）。
  - `[LT-SEED-008]` 預先以壓測 Admin email 建立 Role = Member 的會員後執行，回傳非 0 且沒有產生 token 檔；另一案例預先替壓測 Admin 多建一個 Approved「LoadTest Organizer」，同樣回傳非 0 且沒有產生 token 檔。
- [x] 2.7 在開發環境實跑 `docker compose exec api dotnet run --project tools/ProjectC.LoadTest.Seeder -- --buyers 500`：確認 DB 有 500 個壓測買家，`git status` 看不到 token 檔（LT-COMPOSE-003）。
  - `[LT-SEED-004]` 將這次執行的 stdout 與 stderr 一併導到容器內暫存檔，以 `grep -F` 逐一比對 token 檔中的 admin token 與 500 個買家 token，確認全部沒有出現在輸出中；輸出只有筆數與檔案路徑等摘要。

## 3. Compose 的 k6 服務

- [x] 3.1 查出 `grafana/k6` 目前的穩定版本 tag，並在 compose 註解記下確認來源（Docker Hub 或 GitHub release）與日期。若新增 `Microsoft.Extensions.*` 套件，版本與 `Directory.Packages.props` 既有版本對齊。
- [x] 3.2 `docker-compose.yml` 新增 `k6` 服務（design 決策 8）：
  - 固定 tag、`profiles: ["loadtest"]`、`depends_on: api`；
  - `./loadtest:/scripts:ro` 與 `./loadtest/.output:/output`（可寫），不設 API 位址環境變數；
  - 不映射 port；
  - 加上說明「為什麼」的註解。
- [x] 3.3 驗證 LT-COMPOSE-001：執行 `docker compose up -d`，確認沒有建立 k6 容器；執行 `docker compose --profile loadtest config`，確認 k6 服務存在。
- [x] 3.4 新增 `docker-compose.loadtest-release.yml`：只覆寫 api 的 `entrypoint` 為 Release 組態的 `dotnet run`，以 list 形式撰寫（design 決策 10），加上說明「為什麼」的註解。
- [x] 3.5 `[LT-RELEASE-001]` 切換到 Release 並還原（design 決策 10）：
  - 切換前記下 db、redis 的容器 ID 與 `docker compose ps api` 的 port 映射；
  - 切換後輪詢從主機執行 `curl -fsS http://localhost:<主機 port>/api/events`（主機 port 以 `docker compose port api 8080` 取得；預設 8080，`.env` 的 `API_HOST_PORT` 可改）直到回 200（不用固定等待），再以 `docker compose exec api sh -c "tr '\\0' ' ' < /proc/1/cmdline"` 確認含 `-c Release`；
  - 還原後同樣輪詢到 200，確認 PID 1 指令列回到 `dotnet watch`、port 映射與切換前相同、db 與 redis 容器 ID 沒變。

## 4. k6 腳本（`loadtest/`）

- [x] 4.1 `loadtest/lib/`：
  - 常數 `API_BASE_URL = 'http://api:8080'`（寫死，不讀 `__ENV`）；
  - 共用的 token 載入（`SharedArray` + `open('/output/tokens.json')`）；setup 先檢查買家 token ≥ 500 且不重複，否則 `fail()`；
  - admin API 輔助函式：建立場館、2000 席座位圖、Title 以 `[LoadTest] ` 開頭的活動、20 個票種（design 決策 5），任一步驟非 2xx 就 `fail()`；`setupTimeout: '180s'`；
  - 起跑時間對齊（`startAt` + sleep）與時間安排（`maxDuration: '70s'`、`gracefulStop: '0s'`，design 決策 6）；
  - 故障注入開關 `LT_FAULT`（`p95`、`quantity51`、`bad-admin-token`、`bad-buyer-token`、`setup-timeout`），只能讓情況變壞（design 決策 9）；
  - 組態標籤 `LT_API_BUILD` 只接受 `debug`／`release`，否則 setup 一開始就 `fail()`（design 決策 10）；
  - 非故障注入時 `LT_RUN` 只接受 `1`／`2`／`3`，且 init 階段以 `open()` 試讀目標 summary 檔，已存在時 setup `fail()`（不覆寫既有結果）；
  - options 的 `summaryTrendStats` 包含 `p(99)`；
  - `setup()` 開頭對全部 7 個自訂 Counter（含 `oversell_check_failed`）各呼叫一次 `add(0)`，保證值為 0 的 Counter 也會出現在 summary；Gauge `verify_quantity_sold` 與 Trend `place_order_send_offset_ms` 刻意不補 0（design 決策 11）。
- [x] 4.2 `loadtest/count-ticket.js`：
  - `place-order` scenario 用 `per-vu-iterations`，500 VU、每個 VU 1 次，`exec.scenario.iterationInTest` 對應 token（原寫 `__VU - 1`，實測 `__VU` 為全測試編號、可能越界，見 design 決策 6），下 1 張數量票；成功後 confirm。
  - 自訂 Counter：`orders_created`、`place_order_conflict`（409）、`place_order_5xx`、`place_order_unexpected`（非 201／409）、`confirm_failed`、`buyer_iterations_completed`；Trend `place_order_send_offset_ms`（送出時距 `startAt` 的毫秒數，design 決策 7）。
  - thresholds：`http_req_duration{name:place-order}` p95<500、`orders_created` 恰為 50、5xx = 0、unexpected = 0、confirm 失敗 = 0、`buyer_iterations_completed` = 500。
- [x] 4.3 `loadtest/seat-ticket.js`：結構同 4.2，改成從 `HOT` 分區的 50 個 seat id 隨機選 1 個；`orders_created` 門檻為 1–50。
  - setup 依 design 決策 5 核對座位邊界（目標票種 `ZoneCode == "HOT"`、`RequiresSeat == true`；`HOT` 恰好 50 席、id 不重複、全部可售；座位池與這 50 個 id 集合相等），任一不符 `fail()`。
- [x] 4.4 `loadtest/lib/verify.js`：純函式 `evaluateVerification(input)`，依 design 決策 7 回傳失敗原因清單（`QuantitySold > 50`、數量票 ≠ 50、數量票剩餘量 ≠ 0、座位票非可售席數 ≠ `QuantitySold`、座位票非 `HOT` 分區有非可售座位）。
- [x] 4.5 兩支腳本都加上 `verify` scenario 與 summary 輸出：
  - 1 個 VU，`startTime: '85s'`（含票種快取 TTL，design 決策 6）；
  - 取 sales-report、票種列表、座位列表後呼叫 `evaluateVerification`，清單非空時 `oversell_check_failed` 加 1，threshold `count==0`；
  - 把 `QuantitySold` 記到 Gauge `verify_quantity_sold`，並用 `console.log` 輸出 `QuantitySold` 與失敗原因；所有 `fail()`、`check`、`console.log`（含 setup 失敗路徑）都不印 token 或 Authorization header（design「安全確認」）；
  - `handleSummary` 呼叫 `evaluateRunResult(data)`（`loadtest/lib/result.js`，design 決策 7），把 `runInfo` 與 `runVerdict` 連同 metrics 寫到 `/output/<scenario>-<LT_API_BUILD>-run<LT_RUN>-summary.json`，並輸出 stdout 文字摘要；故障注入時檔名為 `<scenario>-fault-<LT_FAULT>-summary.json`；`LT_API_BUILD` 非法或未設定時以 `invalid` 取代組態、`LT_RUN` 非法或未設定時以 `run-invalid` 取代序號；任何情況都不得拋例外；出錯時改寫 `runVerdict.passed == false` 的 summary（2026-10-05 審查後補：實測拋例外時 k6 exit 0 且不寫檔）。
- [x] 4.6 `[LT-GATE-004]` `loadtest/tests/verify.test.js`：
  - 以假資料逐條觸發 4.4 的每個失敗條件，各自斷言回傳對應原因；另一組正常資料斷言回傳空清單；
  - threshold `checks: rate==1`；
  - 執行 `docker compose --profile loadtest run --rm --no-deps k6 run /scripts/tests/verify.test.js`，exit code 為 0。
- [x] 4.7 `loadtest/lib/aggregate.js` 純函式 `aggregateRuns`、`renderReportTables`（缺少預期 metric 或欄位時判未通過、表格顯示「缺少」，不預設為 0；沒有例外），以及執行腳本 `loadtest/aggregate.js`：以 `loadRunSummaries('/output')` 只讀白名單 `{count-ticket,seat-ticket}-{debug,release}-run{1,2,3}-summary.json` 展開後的 12 個檔名（`count-ticket-debug-run1-summary.json` … `seat-ticket-release-run3-summary.json`），不用 glob；寫出 `/output/report-tables.md`；不發 HTTP 請求、不讀 token 檔（design 決策 11）。
- [x] 4.8 `[LT-REPORT-001]`、`[LT-REPORT-002]`、`[LT-REPORT-003]`、`[LT-REPEAT-001]`、`[LT-REPEAT-002]` `loadtest/tests/report.test.js`：
  - `evaluateRunResult`：全部一致且通過 → `passed`；`QuantitySold ≠ orders_created` → 失敗且原因含兩個數字；某 threshold `ok == false` → 失敗且原因含該 threshold；缺 `verify_quantity_sold` → 失敗；缺 `p(95)` 或 `orders_created` → 失敗且原因寫明缺少的欄位；缺 `place_order_5xx` → 失敗；某 threshold 缺 `ok` → 失敗且原因寫明該 threshold；送出時距 max−min > 1000ms → 失敗；全部時距一起為負但 max−min 小 → 通過；缺送出時距 min → 失敗（2026-10-05 審查後補）；
  - `aggregateRuns`：3 次全過 → 「通過」，中位數／最小值／最大值正確；第 2 次失敗 → 「未通過」並指出第 2 次與失敗原因；缺 1 個檔 → 「未通過」；某次缺 `p(99)`、`QuantitySold`、成功數或 409 數 → 該格顯示「缺少」、不參與統計、該組「未通過」；舊 summary 的 runVerdict 標通過但 max−min > 1000ms → 彙整自行判未通過，且 runVerdict 已含同一原因時不重複列出；
  - `renderReportTables`：表格中的 P95、P99、成功數、`QuantitySold` 等於輸入；
  - threshold `checks: rate==1`；執行 `docker compose --profile loadtest run --rm --no-deps k6 run /scripts/tests/report.test.js`，exit code 為 0。
- [x] 4.9 `[LT-REPORT-003]`、`[LT-REPORT-004]` `loadtest/tests/aggregate-flow.test.js` 與 fixture 目錄 `loadtest/tests/fixtures/aggregate/`（design 決策 11）：
  - fixture：白名單 12 個檔名中放 11 個（缺 `seat-ticket-release-run3-summary.json`）；其中 1 個缺 `p(95)`、1 個 `runVerdict.passed == false`、其餘完整；另放 `count-ticket-fault-p95-summary.json`、`count-ticket-invalid-run1-summary.json` 與 `precheck/count-ticket-debug-run1-summary.json` 三個不該被讀的檔案；
  - 走與 `aggregate.js` 相同的 `loadRunSummaries` → `aggregateRuns` → `renderReportTables` → `handleSummary` 流程，`baseDir` 為 `/scripts/tests/fixtures/aggregate`，輸出 `/output/aggregate-flow-report-tables.md`；
  - `check` 斷言：只讀到 11 個檔、白名單以外的檔案沒有被納入；缺 `p(95)` 那格為「缺少」且該組「未通過」；`runVerdict.passed == false` 那組「未通過」並指出第幾次；缺檔那組「未通過」並標示缺檔；完整的組「通過」；
  - threshold `checks: rate==1`；執行 `docker compose --profile loadtest run --rm --no-deps k6 run /scripts/tests/aggregate-flow.test.js`，exit code 為 0；再以 `grep` 確認輸出檔中缺 `p(95)` 那一列顯示「缺少」而不是 `0`。

## 5. 執行與驗收

- [x] 5.1 `loadtest/README.md` 說明重現步驟：
  - **每一次**壓測前都重跑 seeder（token 有效期 30 分鐘；整批可能超過 30 分鐘，所以不能每批只跑一次）；任何失敗都計入結果，不以 token 過期為由排除；
  - api 就緒以輪詢判斷，Release 首次啟動需要完整編譯；切換時 pgadmin 的 orphan 警告無害；
  - 順序固定為 seeder 完成 → 確認 `GET /api/events` 回 200 → 執行 k6；壓測中不得執行 seeder 或 `dotnet test`（design 決策 8）；
  - token 檔可手動刪除；測試活動 Title 以 `[LoadTest] ` 開頭，附清理建議；
  - 壓測 Admin 是 Development 專用帳號；只支援 Docker Desktop／WSL2（k6 以非 root 執行，Linux 原生 Docker 可能無法寫 `/output`）。
- [x] 5.2 `[LT-SETUP-001]` 執行前重跑 seeder；執行時確認 setup 建出 2000 席、20 個票種、販售中、非排隊模式。用 admin API 與公開 API 查詢核對，不用 raw SQL。
  - 非排隊模式：以公開 `GET /api/events` 找到本次活動，斷言 `IsQueueModeEnabled == false`（`EventDto` 有此欄位；建立活動時會清除這份清單的快取，所以讀到的是新值。admin 的 `GET /api/admin/events` 沒有此欄位，不用它）；販售中同樣以該筆核對：`SalesStartAtUtc` ≤ 現在、`SalesEndAtUtc` 為空，且現在 < `StartAtUtc`（`SalesEndAtUtc` 為空時產品以 `StartAtUtc` 當販售結束時間，見 `Event.GetSalesStatus`）。
  - 座位票另外以 `GET /api/events/{id}/ticket-types` 確認目標票種 `ZoneCode` 為 `HOT`，以 `GET /api/events/{id}/seats` 確認 `HOT` 分區恰好 50 席，並確認 stdout 中 setup 輸出的座位池筆數為 50。
- [x] 5.3 setup 中止驗證（證據為 exit code 非 0、stdout 含 setup 失敗訊息、`iterations` 為 0 或未出現；不依賴 summary JSON，design 決策 9）：
  - `[LT-SETUP-002]` 以 `-e LT_FAULT=bad-admin-token` 執行，確認 k6 中止、沒有下單；
  - `[LT-SETUP-002]` 以 `-e LT_FAULT=setup-timeout` 執行，確認 k6 因 setup 逾時中止、沒有下單（若該次 setup 未逾時，視為驗證無效並重跑；`setupTimeout` 原訂 1s，實測 setup 會在 1 秒內完成，已改為 1ms，見 design 決策 9）；
  - `[LT-SETUP-003]` 以 `--buyers 499` 重跑 seeder 後執行 k6，確認中止、沒有下單；結束後以 `--buyers 500` 重跑 seeder 還原；
  - `[LT-RELEASE-002]` 不帶 `LT_API_BUILD`、`-e LT_API_BUILD=prod`、不帶 `LT_RUN`、`-e LT_RUN=4`，以及目標 summary 檔已存在，各執行一次，確認中止、沒有下單、既有 summary 檔沒有被覆寫（「已存在」案例先手動放一個內容已知的假檔，事後比對內容未變）；
  - 5.3 結束後，把這一步產生或手動放置的 `*-run*-summary.json` 移到 `loadtest/.output/precheck/`，確保 5.4 開始時 `/output` 沒有任何正式結果檔。
- [x] 5.4 `[LT-RUN-001]`、`[LT-RUN-002]`、`[LT-GATE-001]`、`[LT-COMPOSE-002]`、`[LT-REPEAT-001]` Debug 批次：
  - 開始前以 PID 1 指令列確認 api 是 `dotnet watch`（同 3.5）；
  - `count-ticket.js`、`seat-ticket.js` 各執行 3 次（`-e LT_API_BUILD=debug -e LT_RUN=<1|2|3>`），每次間隔至少 60 秒；每一次執行前都重跑 seeder，並輪詢到 `GET /api/events` 回 200；
  - 每次記錄 exit code，並解析 summary JSON，確認：含 `runInfo`、7 個自訂 Counter 都出現（包含值為 0 者）；`runVerdict.passed == true` 且失敗原因清單為空；`verify_quantity_sold` 的值等於 `orders_created` 的 `count`（證明 Gauge → `handleSummary` → `evaluateRunResult` → summary JSON 的接線在真實執行中有效）；
  - exit code 為 0 但 `runVerdict.passed == false` 時，該次判為未通過（spec「通過門檻與超賣驗證」），依 5.7 記錄；以 `grep` 確認 stdout 與 summary JSON 都不含 token 檔中的任何 token。
- [x] 5.5 `[LT-REPEAT-001]` Release 批次：依 3.5 切換到 Release，重複 5.4 的步驟（`-e LT_API_BUILD=release`），且**每一次**執行前都以 PID 1 指令列確認仍含 `-c Release`（k6 必須帶 `--no-deps`，否則會把 api 重建回 Debug，design 決策 10）；全部完成後依 3.5 還原並做相同的還原檢查。
- [x] 5.6 故障注入驗證（不改檔案；每項都帶 `-e LT_API_BUILD=debug`，避免因組態標籤而提早中止；每項執行前都重跑 seeder，避免 token 過期讓驗證無效；每項都確認 exit code 非 0，且 summary JSON 中指定 threshold 的 `ok == false`）：
  - `[LT-GATE-002]` `LT_FAULT=p95`（數量票）→ `http_req_duration{name:place-order}` 失敗；
  - `[LT-GATE-003]`、`[LT-GATE-004]` `LT_FAULT=quantity51`（數量票）→ `orders_created` 上限失敗（GATE-003），且 `oversell_check_failed` 也失敗、verify 輸出的失敗原因含 `QuantitySold > 50`（GATE-004 完整接線）；
  - `[LT-GATE-005]`、`[LT-GATE-006]` `LT_FAULT=bad-buyer-token`（座位票）→ 下單全部 401，`place_order_unexpected` 失敗，且 `orders_created` 的 `count>=1` 失敗。
- [x] 5.7 如果 5.4／5.5 沒有達到門檻（例如 P95 超標），不要調整門檻或產品程式碼，如實記錄並回報使用者決定後續處理。

## 6. 報告與收尾

- [x] 6.1 執行 `docker compose --profile loadtest run --rm --no-deps k6 run /scripts/aggregate.js` 產生 `report-tables.md`，確認其中沒有任何「缺少」（證明 fixture 與真實 summary 結構一致；若出現，先修正彙整的欄位路徑與 fixture，不得手改表格）；把它與 12 份 summary JSON 複製到 `docs/load-test/`，並確認內容不含 token。
- [x] 6.2 撰寫 `docs/load-test/report.md`，涵蓋 LT-REPORT-001／002、LT-REPEAT-001／002：
  - 執行環境（含每批的 api 程序指令列）；
  - 重現步驟（含 Release 切換與還原）；
  - 資料表（各次結果、中位數／最小值／最大值、每組判定）原樣貼上 `report-tables.md`，不手改；以 `diff` 確認報告中的資料表與 `report-tables.md` 相同（LT-REPORT-001）；
  - 核對 5.4／5.5 記錄的 exit code：任一次 exit code 非 0 而 `runVerdict.passed` 為 true，視為判定邏輯有缺陷，回報使用者；
  - 依 design 決策 7 的歸因順序（產品問題 → 環境未達併發 → 低於預期需調查）判讀每一次未通過或座位票成功數 < 50 的執行；
  - §5 目標的最終判定（以 Release 為準）與 Debug／Release 差異；
  - 判讀與限制，包含 Release 仍搭配 Development 環境、WSL2 時鐘、本機共用資源。
- [x] 6.3 更新 docs/project-scope.md §8 ② 的狀態，連結到報告。
- [ ] 6.4 確認 `git diff --stat master -- src/` 為空（沒有修改產品程式碼），完整 .NET 測試套件在容器內通過（`docker compose exec api dotnet test`）。
  - **未完成**：`src/` 無差異已確認，但完整套件尚未能穩定地一次全過。以下失敗都與本 change 無關，但不能據此寫成「全部通過」。
  - 2026-10-05 第 1 次：1150 個測試中 1 個失敗（`PurchaseQueueAdmissionServiceTests.AdvanceQueueOnceAsync_WhenPromotionDecisionIsAbandonedBeforePostgresLands_ReconciliationCompletesItAfterPendingMarkerExpires`，Redis pending TTL 判斷，既有 WSL2 時鐘相關不穩定測試），單獨重跑 3 次皆通過；未修改或跳過。
  - 2026-10-05 使用者審查時：`RealNameLoggingTests.RedemptionFlow_WithHolderLookup_NeverLogsHolderRealNameOrLast4` 失敗，日誌含「2468」，來源為 `GetTicketHolderHandler`。查證後判定為測試誤判：該 handler 只記錄 4 個 Guid 與固定結果字串，「2468」是隨機 Guid 的片段。已修正 `tests/ProjectC.WebApi.Tests/Members/RealNameLoggingTests.cs`：先遮掉 Guid 與含 a-f 的 32／16 位 hex（TraceId／SpanId），再做原本的子字串比對。最初的做法是「只比對前後非英數字的獨立值」，strict-reviewer 指出這會漏抓 `Last42468` 這類緊貼英數字的外洩，因此改掉。新增 13 個案例：改回純子字串比對時 3 個失敗，改回獨立值比對時 4 個失敗。此項為無對應 spec 的測試修正，未修改 `src/`。
  - 2026-10-05 修正後：Domain 139、Application 449、Seeder 38、Infrastructure 197 全過；WebApi 473 中 1 個失敗（`RealNameLoggingTests.RealNameGates_ForRegisteredAndUnregisteredBuyers_NeverLogRealNameData`）。之後 WebApi 整組又連續重跑 6 次都全過，未重現，失敗訊息未取得，**原因不明**。
