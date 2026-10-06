## Context

- `k6-load-test` 實測 P95 未達標（Release 中位數：座位票約 4835ms、數量票約 802ms），九成樣本是 409，整體 P95 幾乎等於 409 的 P95（`docs/project-scope.md` §8 起點證據）。
- `OrderService.PlaceOrderAsync`（`src/ProjectC.Application/Orders/OrderService.cs`）對每筆訂單在交易內先 `IEventRepository.GetForUpdateAsync` 鎖定 Event（Queue Mode 線性化時點，`rate-limiting-queue` design.md 決策 4），同一活動的下單因此逐筆序列化。每筆在鎖內的耗時 × 排在前面的筆數，就是排隊延遲。
- 鎖內工作（現況）：鎖 Event → 販售期間重檢 →（排隊模式）鎖排隊紀錄 → 鎖座位／票種 → **座位項目：重新讀 Event、以追蹤查詢 `SeatMapRepository.GetByIdAsync`（`Include(Seats)`）載入整張座位圖（壓測活動 2000 席）比對分區** → `CreateOrderHandler.Handle` 判斷座位可鎖定／庫存足夠（409 在這裡產生）→ commit。
- 交易前已載入：no-tracking 的票種（含 `AvailableQuantity`）、no-tracking 的所選 `EventSeat`（`GetByIdsAsync`）、no-tracking 的 Event。
- 不可變性（2026-10-06 核對 `src/`，範圍與方法如下；不涵蓋手動改資料庫）：
  - 型別層：`Seat` 的 `Id`／`SeatMapId`／`ZoneCode`／`SeatNumber`、`EventSeat` 的 `Id`／`EventId`／`SeatId`、`Event.SeatMapId` 皆為 getter-only auto-property（無 setter、無 `init`）；`Seat`、`EventSeat` 建構子為 `internal`。
  - 建立路徑：`new Seat` 只在 `SeatMap.AddSeat`，其唯一呼叫端是 `CreateSeatMapHandler`；`new EventSeat` 只在 `Event.CreateEventSeats`（拒絕非本活動座位圖，`Event.cs:97`），其唯一呼叫端是 `CreateEventHandler`。
  - 修改／刪除路徑：`src/` 內沒有對 `Seats`、`EventSeats` 的 `Remove`／`RemoveRange`／`ExecuteUpdate`／`ExecuteDelete`；raw SQL（`FromSqlInterpolated`／`ExecuteSqlInterpolated`）中 `EventSeats` 只有 `FOR UPDATE` 讀取，寫入只有 `PurchaseQueueEntries`；migration 的 `Sql()` 不涉及座位資料表；`ISeatMapRepository` 沒有更新方法。
  - 資料庫層：`EventSeats.SeatId`、`EventId` 有 FK（Restrict）與 `(EventId, SeatId)` 唯一索引，但**沒有**「`EventSeat` 的座位屬於該活動座位圖」的資料庫約束——這只由 Domain 保證，因此決策 4 不依賴它，改以顯式比對（見決策 4）。
- 環境：Docker Desktop／WSL2，Docker 可用 20 CPU、15.5 GiB；PostgreSQL 資料在 named volume；Npgsql 連線池預設上限 100、PostgreSQL `max_connections` 100；WSL2 牆上時鐘會倒退（約每 29 秒倒退約 1.5 秒）。Release 與 Debug 的 P95 相近，CPU／JIT 不是主因。

## Goals / Non-Goals

**Goals:**

- 以單調時鐘量測拆出連線池等待、Event 鎖等待、鎖內處理、commit 的耗時，並取得無競爭基準，確認瓶頸。
- 只做低風險、不改變鎖設計的優化（C、A），讓 `load-testing` 既有門檻（P95 < 500ms、0% 超賣）在本機達成。
- 若未達成，以量測證據說明剩餘延遲來源。

**Non-Goals:**

- 改變 Event `FOR UPDATE` 鎖範圍或 Queue Mode 線性化設計（候選 B）。
- 調整連線池／`max_connections`（量測後由使用者決定，不在本設計內）。
- 犧牲耐久性的資料庫設定（`synchronous_commit=off`、`fsync=off` 等）。
- 修改 P95 口徑、k6 門檻或既有壓測腳本的判定邏輯。
- 排隊模式下的效能（`load-testing` 不涵蓋排隊模式）。

## Decisions

### 1. 分段計時：保留在產品程式碼，以 Debug 等級 log 輸出

- `PlaceOrderAsync` 以 `Stopwatch.GetTimestamp()`／`Stopwatch.GetElapsedTime()` 記錄分段：`PreTransaction`（驗證、交易前查詢與檢查）、`BeginTransaction`（含從連線池取得連線）、`EventLockWait`（`GetForUpdateAsync(event)` 呼叫耗時）、`InLock`（取得 Event 鎖後到 commit 前）、`Commit`、`Total`，以及結果（成功，或 `ErrorType`）與是否含座位項目。未到達的分段記為 null（例如交易前就被拒絕）。**不記錄買家 Id 或任何個資**：量測只需要分布。
- 在 `finally` 統一輸出一筆 Debug log，涵蓋所有提前 return 與例外路徑；以 `_logger.IsEnabled(LogLevel.Debug)` 守門，關閉時不組字串、不配置參數陣列。
- **採用 `LogDebug`＋`IsEnabled` 守門，而非 `LoggerMessage` source generator**：codebase 目前沒有使用 `LoggerMessage`，依 CLAUDE.md「符合既有慣例」不引入新寫法；守門後關閉時的成本只剩幾次 `Stopwatch.GetTimestamp()`。
- 開啟方式：新增 `docker-compose.loadtest-measure.yml`（與既有 `docker-compose.loadtest-release.yml` 同樣的 override 慣例，只在量測執行疊加），對 api 注入：
  - `Serilog__MinimumLevel__Override__ProjectC.Application.Orders.OrderService=Debug`（`SerilogConfigurator` 使用 `ReadFrom.Configuration`；環境變數的 `__` 對應設定階層，key 內的 `.` 保留為類別名稱的一部分，這是 Serilog 設定的標準寫法，實作時以一次實測確認生效）
- Console 不收 Debug：**在 `appsettings.json` 的 Console sink 明確寫入 `"Args": { "restrictedToMinimumLevel": "Information" }`**，不用環境變數覆寫陣列索引。
  - 現況核對：Console sink 是 `Serilog:WriteTo[0]`，沒有 `Args` 節點；Seq sink 不在 `WriteTo` 陣列，由 `SerilogConfigurator.cs:22-26` 以程式碼加入、不設等級限制，所以 Debug 分段 log 只會進 Seq。
  - 為什麼不用 `Serilog__WriteTo__0__Args__...`：要對不存在的節點補值，又依賴陣列索引，`Program.cs:93-96` 已記錄這類覆寫「脆弱且難以確認」。寫進設定檔，Console 的限制就不依賴量測時的覆寫是否生效。
  - 為什麼不在 `SerilogConfigurator` 另建 filter／sub-logger：要把 Console 從 `ReadFrom.Configuration` 移到程式碼，會改動 observability 既有的 sink 組裝，超出本 change 需要。
  - 行為影響：目前 `MinimumLevel.Default` 是 Information，`appsettings.Development.json` 沒有 Serilog 區段，現有 log 沒有低於 Information 而輸出到 Console 的，所以這行對現有輸出沒有影響；之後如果有人降低等級來除錯，Debug 只會進 Seq。Seq 斷線時分段 log 不會改由 Console 輸出，量測檢查會判為無效（見下）。
  - 結果：量測 override 只有上面那一個純量 key，不以環境變數修改 sink 陣列。
  - 自動化：新增設定測試讀取 `src/ProjectC.WebApi/appsettings.json`，斷言 Console sink 的 `Args.restrictedToMinimumLevel` 為 `Information`，避免日後被刪掉而只在量測時才發現。
- **設定不生效時量測必須失敗，不可靜默**：新增 `loadtest/check-measure-logging.sh <起始時間> <結束時間> <k6 summary JSON>`（實作時由傳入預期數改為傳入 summary：預期數由腳本從 summary 的 `place_order_requests`／`place_order_rate_limited`／`place_order_no_response` counter 推算，讓 429 扣除與逾時判無效也能被自我測試涵蓋；counter 缺少即無效），每次量測執行後必跑，下列任一不符就以非 0 結束，該次量測無效、不得寫入報告：
  - Seq 在該時間範圍內的分段 log 筆數 = 預期下單數。筆數為 0 代表 Override 沒生效，少於或多於預期代表 Seq 掉事件或混入其他請求。
  - 預期下單數 = summary 中**有收到 HTTP 回應且不是 429** 的下單請求數：429 在 rate limiter 被擋下、沒有進入 `PlaceOrderAsync`，不會有分段 log；k6 逾時（沒有回應）無法判斷伺服器是否處理完，所以只要有任何逾時，該次量測直接判為無效，不嘗試推算筆數。
  - Seq 寫入是非同步批次：腳本在 k6 結束後輪詢 Seq，筆數達到預期或等滿 15 秒才判定，避免尚未 flush 就誤判；超過預期同樣判為無效。
  - `docker compose logs api --since <起始時間>` 中分段 log 的訊息範本出現 0 次（代表 Console 限制生效）。
  - 腳本以假資料自我測試（`loadtest/tests/check-measure-logging.test.sh`，涵蓋：全部符合、Seq 0 筆、Seq 少於預期、Seq 多於預期、輪詢期間筆數才補齊、有 429 時預期數扣除 429、有逾時即無效、Console 出現分段 log），不連開發用服務。
  - 時間範圍只用來篩選 log，不拿來相減算耗時。
- **取出方式**：Seq 未掛 volume、容器重建後歷史清空，所以每次量測後立即以 Seq 查詢（依時間範圍與 log 範本過濾）算出各分段 p50／p95 與筆數，寫入 `loadtest/.output/measure-<tag>-phases-<scenario>.json`（Seq 匯出 JSON 或查詢結果），不依賴之後 Seq 仍保有資料。實作偏差（2026-10-06）：Seq SQL 的 `percentile()` 實測為近似值（50 筆 p95 誤差約 6%，且不同分段被算成同一值），改為以 Seq 查詢取出原始值、由 `loadtest/lib/phase-stats.jq` 精確計算，原始值一併寫入匯出檔。
- **例外路徑**：log 不附帶 Exception 物件或訊息（例外訊息可能含 Id 與 SQL 參數），只記固定字串 `Exception` 作為結果；`finally` 內的 log 不得拋例外遮蔽原例外。
- 為什麼不用時間戳相減：WSL2 牆上時鐘會倒退，Seq 時間戳相減會失真。`Stopwatch` 是單調時鐘。
- 替代方案：OpenTelemetry tracing——需新增套件與 collector，超出量測需要，不採用。量測後移除——使用者決定保留，供後續回歸量測使用。

### 2. 資料庫等待事件取樣

- 新增 `loadtest/sample-db-waits.sh`：在 db 容器內開一個 `psql` 連線，以 `\watch 0.2` 每 200ms 查詢 `pg_stat_activity`（限定應用資料庫、排除取樣連線自己），依 `state`、`wait_event_type`、`wait_event` 分組計數，輸出到 `loadtest/.output/`（不進版控）。
- 取樣只用來看「同一時間有多少連線在等鎖／在等 IO／閒置在交易中」的分布，耗時本身以決策 1 的應用端計時為準。取樣列的時間戳只用來對齊壓測時段，不拿來相減算耗時。
- 取樣 SQL 獨立成 `loadtest/sql/db-waits.sql`，以 `current_database()` 限定資料庫、`pid <> pg_backend_pid()` 排除取樣連線自己；腳本可用參數指定目標容器與取樣秒數。
- 自動化測試 `loadtest/tests/sample-db-waits.test.sh`：以 `docker run --rm` 啟動一次性的 `postgres:16-alpine`（CLAUDE.md 禁止測試連線開發用 db 服務），開一條 idle in transaction 連線後取樣約 1 秒，斷言至少 4 組輸出、包含該連線、不包含取樣連線，結束後移除容器。
- 取樣本身占 1 條連線，量測報告須註明。
- **取樣連線的建立**：`docker compose exec -T -e PGAPPNAME=lt-db-waits db sh -c 'psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" ...'`。
  - application name 用 libpq 的 `PGAPPNAME` 環境變數設定。
  - 帳號和資料庫取自 db 容器自己的 `POSTGRES_USER`／`POSTGRES_DB`，經容器內 unix socket 連線，帳密不出現在主機命令列或檔案中。
  - 若實測發現 socket 連線需要密碼，同樣在容器內以 `PGPASSWORD="$POSTGRES_PASSWORD"` 傳入。
  - 目標容器的執行前綴可用參數替換（預設 `docker compose exec -T db`，測試改為一次性容器的 `docker exec -i <container>`）。
- **有界**：以 PG16 psql 的 `\watch i=0.2 c=N`（N 由秒數換算）結束，不使用無限 `\watch`。
- **確認 application name 生效（啟動前後檢查）**：
  - 啟動前：若已存在 `application_name = 'lt-db-waits'` 的 backend（上一次殘留），中止並提示清理。
  - 啟動後 3 秒內：以管理連線確認恰好 1 個 `lt-db-waits` backend，否則立即清理並以非 0 結束。這樣 application name 沒設成功時不會進入無法清理的狀態。
- **清理（`trap` 於 `EXIT INT TERM`）**：
  - 另開一條管理連線，前綴、帳號與資料庫同上，`PGAPPNAME=lt-db-waits-cleanup`。
  - 執行 `SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = 'lt-db-waits' AND datname = current_database() AND pid <> pg_backend_pid()`，以 `datname` 與 `application_name` 雙重限定。
  - 之後輪詢最多 5 秒確認殘留為 0，否則以非 0 結束並印出殘留 pid。
  - 帳號同為 `POSTGRES_USER`（db 容器的 superuser），有權限終止同資料庫的 backend。
  - 這是因為中止 `docker compose exec` 的主機端 client 不保證會結束容器內的 psql，所以一律以終止 backend 收尾。
- **失敗即中止**：連線失敗或輸出列數少於預期時以非 0 結束，該次量測無效。
- **輸出檔名** `measure-<LT_MEASURE_TAG>-db-waits-<scenario>.csv`，標籤格式同決策 7；檔案已存在時中止，不覆寫（與 k6 一致）。
- **換行字元**：新增 `.gitattributes`（`*.sh`、`*.sql` 為 `eol=lf`），避免 Windows autocrlf 把腳本轉成 CRLF；README 指令範例保留 `MSYS_NO_PATHCONV=1`。
- 這是壓測工具的唯讀查詢，不是應用程式碼，不受「禁止 raw SQL」規則約束。

### 3. 無競爭基準

- 新增 `loadtest/baseline.js`：沿用既有 `lib/` 的 setup（建立同規模活動），以 **1 個 VU 依序**送出 50 筆下單，每筆使用不同買家 token（避開每位買家的下單頻率限制），座位票逐筆選不同座位、數量票庫存設為足夠，全部都應為 201。以 `LT_BASELINE_TICKET=count|seat` 選擇票種。summary 檔名為 `measure-<LT_MEASURE_TAG>-baseline-<count|seat>-summary.json`（不使用 `LT_API_BUILD`／`LT_RUN`）。
- **限流前提（2026-10-06 核對）**：
  - `POST /api/orders` 套用 `place-order` policy（`OrdersController.cs:46`），分區鍵為 token 中的會員 Id（`Program.cs:410-413` `CreateMemberPartition`），Fixed Window 每分區 `PermitLimit` 20／`WindowSeconds` 60（`appsettings.json` `RateLimiting`），`QueueLimit` 0。
  - baseline 每個 token 只送 1 筆，50 個不同會員就是 50 個分區，各用掉 1／20，現行設定下不會被限流。
  - setup 呼叫的 admin API 不套用 `place-order` policy。
- **不放寬、不繞過限流**：baseline 不修改 `RateLimiting` 設定，也不使用特殊隔離。
  - 若日後 partition 改成 IP 或全域，baseline 會收到 429，視為該次量測無效，必須先修改本設計再量測，不得調高限制來湊出結果。
  - 正式驗收的門檻與限流設定一律不變。
- **429 偵測**：
  - 任何非 201 都計入 `baseline_non_201` 並使 k6 以非 0 結束。
  - 429 另計 `baseline_rate_limited` counter，輸出中明確標示「被限流，量測無效」，與其他失敗區分。
- **分區唯一性預檢（setup，任何下單之前）**：
  - 現況 `lib/tokens.js` 的 `validateTokens` 只檢查 token 字串不重複，無法證明屬於不同會員。
  - 伺服器的分區鍵取自 JWT `sub` claim（`ClaimsPrincipalExtensions.cs:11` `FindFirstValue(JwtRegisteredClaimNames.Sub)`，且 `Program.cs:348` `MapInboundClaims = false`，所以 claim 名稱就是 `sub`）。
  - 因此 baseline 在 `lib/baseline.js` 以純函式解析前 50 個買家 token 的 payload：取第二段、base64url 解碼（`k6/encoding` 的 `b64decode(..., 'rawurl', 's')`）、`JSON.parse`、取 `sub`。
  - 只用來確認分區唯一性，不驗證簽章（簽章由 API 驗證）；不記錄 token 或 `sub` 內容，錯誤訊息只帶 token 序號。
  - 任一 token 格式不符、解碼失敗、沒有字串型 `sub`，或前 50 個 `sub` 不是 50 個彼此不同的值，setup 立即中止，不送出任何下單。
  - 不改 seeder 的 token 檔格式：改格式會影響既有 `count-ticket.js`／`seat-ticket.js` 與 seeder 測試，而 `sub` 本身就是伺服器實際使用的分區鍵，比 seeder 另外輸出的 memberId 更直接。
- 產出：k6 端 P50／P95，以及（開啟決策 1 的 Debug log 時）各分段的中位數。
- 判定「50 筆皆 201」的邏輯放在 `lib/` 的純函式，於 `loadtest/tests/` 以假資料自我測試（比照既有 `report.test.js`）。
- 用途：得到「單筆在鎖內的成本」。若 50 筆搶鎖時的耗時 ≈ 排隊筆數 × 單筆鎖內成本，就可以確認瓶頸是序列化；若遠大於，表示還有其他等待（例如連線池）。

### 4. 優化 C：座位分區比對移到交易前

- 交易前以 `ISeatMapRepository.GetSeatsByIdsAsync`（既有方法，已核對為 `AsNoTracking`）一次取得所選座位對應的 `Seat`（依交易前已載入的 `EventSeat.SeatId`），比對 `ZoneCode` 與票種分區；不一致回 400（驗證錯誤），查無座位樣板回 404（與現行鎖內「座位不在座位圖中」的 NotFound 相同），皆不開交易。
- 現行鎖內以「在該活動座位圖的 `Seats` 中尋找」隱含確認座位樣板屬於該活動的座位圖。改以 Id 直接查詢後，**不依賴** `Event.CreateEventSeats` 的建立期不變量，而是顯式比對 `Seat.SeatMapId == Event.SeatMapId`：不符時回 404，訊息與現行「座位不在座位圖中」相同，與現行鎖內行為等價（現行查不到即 404）。
  - 交易外讀到活動時：樣板存在、座位圖成員、分區三項都在交易前比對，成員比對用該活動的 `SeatMapId`。
  - 交易外讀不到活動時（TP-ORDER-024 的時間差）：樣板存在仍在交易前檢查（不需要活動）；**成員與分區比對一起延到鎖內**，用 `GetForUpdateAsync` 回傳的鎖定 Event 比對（只比對記憶體中的值與交易前已取得的 `Seat`，不新增查詢）。分區不跟著留在交易前，是為了兩條路徑都維持同一個順序；這條路徑只在兩次讀取之間的時間差出現，延到鎖內的成本可忽略。
  - 鎖內位置（僅讀不到活動的路徑）：Event 鎖定 → 販售期間重檢 → 實名檢查（現行 `orderEvent is null` 分支）→ **成員比對（404）→ 分區比對（400）** → 排隊資格（403）→ 座位／票種 `FOR UPDATE`。放在排隊資格之前，與讀到活動的路徑「分區 400 優先於 403」一致；放在座位／票種鎖定之前，異常資料不會鎖到任何座位或票種。
  - 比對順序兩條路徑皆為：座位樣板存在 → 座位圖成員 → 分區，所以異常資料一律回 404 而非 400。單一座位時與現行鎖內順序相同；多座位時改為「全部座位逐項檢查完一種再檢查下一種」，現行則是逐座位依序檢查三項，因此「座位 1 分區錯、座位 2 樣板不存在」以前回 400、現在回 404。`EventSeats.SeatId` 有 FK（Restrict），樣板不存在實際上不會發生，此差異不影響可觀察行為。
- 鎖內移除「重新讀 Event＋載入整張座位圖」，改用交易前比對的結果。安全性依據：`Seat.ZoneCode`、`EventSeat.SeatId`、`Event.SeatMapId` 皆不可變（見 Context），交易前讀取與鎖內讀取必然相同。若未來新增座位圖或分區的修改功能，必須把比對移回鎖內——在程式碼註解記錄此不變量，並以 Domain 測試鎖住「這些屬性是 getter-only 自動屬性」（無 setter／init，且 backing field 為編譯器產生的 readonly 欄位，TP-ORDER-031），新增 setter 或改成可被方法修改的自訂欄位時測試會失敗、迫使重新檢視。
- 實作時須確認 `GetSeatsByIdsAsync` 為 no-tracking；若不是，改用 no-tracking 版本，避免同一 context 追蹤大量實體。
- 錯誤優先順序變化：分區不一致的 400 會早於交易內的販售期間重檢、排隊資格 403 與座位鎖定，排隊模式下未入場買家選錯分區也回 400（TP-ORDER-026）。這與既有「跨活動驗證在取得任何鎖之前完成」的 400 一致，屬於請求本身的驗證錯誤。交易外的既有檢查順序不變：販售期間 > 實名 > 限購 > 樣板存在 > 成員 > 分區，比對接在限購之後，所以同時違反限購與分區時仍回限購錯誤（TP-ORDER-032）。

### 5. 優化 A：交易前提早回 409

- 位置：交易外所有既有檢查（販售期間、實名、限購）與決策 4 的分區比對之後、`BeginTransactionAsync` 之前。
- 條件：交易外讀到的 Event 存在且 `IsQueueModeEnabled == false` 時才執行；否則跳過，全交給鎖內判斷。
- 判斷：以交易前的 no-tracking 資料與當下時間——任一座位 `IsAvailableForHold(now) == false`（透過 `seat-reservation` spec 規定的狀態方法，不讀內部欄位），或任一計數票種 `AvailableQuantity < 該票種購買數量` → 回 409（`ErrorType.Conflict`，訊息與鎖內判斷一致），不開交易。
- 鎖內判斷不變，仍是唯一權威：提前判斷通過的請求照常進鎖，鎖內可能仍因競爭回 409。
- 為什麼這樣是正確的：Sold 是永久狀態，交易前讀到 Sold 必然正確；交易前讀到 Held（未逾時）或庫存不足，相當於這個請求在讀取當下被序列化處理，結果與「在那一刻排到鎖」相同。唯一的差異是讀取後、原本會拿到鎖之前，持有者取消或逾時釋放的情況——這時本請求會回 409，而原本可能成功。這個時間差與既有「早一點送出就會 409」的情況等價，不違反「不超賣」與「鎖內權威」。交易前讀取順序為票種 → 座位 → 活動（`OrderService.cs:93、137、166`），座位／庫存資料與排隊旗標不在同一時點，差距只有同一請求內數個查詢的時間；提早判斷只拒絕不放行，這個陳舊窗口不影響安全。
- 為什麼排隊模式跳過：排隊模式的 403 必須以鎖內重讀為準（TP-ORDER-015）。交易外讀到排隊模式就跳過，避免在排隊模式下回 409 取代 403；排隊模式本身已把同時下單人數限制在 `MaxConcurrentAdmittedBuyers`，提前判斷的效益有限。剩下的時間差：交易外讀到「未開排隊」、鎖內讀到「已開排隊」的極短時間內，未入場買家選到已不可售的項目會得到 409 而非 403——spec delta 明確記錄這一點。
- 錯誤訊息：提早判斷與鎖內判斷（`CreateOrderHandler`）的 409 訊息共用同一處定義，不各寫一份。
- 測試 TP-ORDER-023 的時序控制：整合測試先以另一個 `DbContext` 開交易持有該活動 Event 列的 `FOR UPDATE` 鎖，讓下單請求在交易外讀取（座位可售）後卡在 Event 鎖；接著持鎖的交易暫扣該座位並 commit 放行，斷言下單回 409、座位只被一筆訂單持有。以「等到下單請求確實在等鎖」（查 `pg_locks` 有等待中的鎖）作為放行條件，不用固定睡眠時間。
- 替代方案：鎖內提早判斷（拿到 Event 鎖後立即檢查）——仍要排鎖，無法解決排隊延遲，不採用。

### 6. 實施順序與決策點

1. 先完成決策 1–3、7 的量測工具與輸出隔離，搬移 `k6-load-test` 舊結果，在**現行程式碼**上量測（一律帶 `LT_MEASURE_TAG`）（Debug log 開啟的量測執行與正式驗收執行分開：log 本身有成本，正式驗收時關閉）。
2. 依量測結果寫入報告，確認瓶頸。**決策點**：若量測顯示主要時間在連線池等待，暫停並請使用者決定是否納入連線池調整（須更新 proposal）。
3. 實作 C → 重量測；實作 A → 重量測。
4. 依 `loadtest/README.md` 完整驗收（Debug／Release × 兩 scenario × 3 次）。
5. 未達標時：以量測證據寫明剩餘延遲來源；若需要候選 B，另開 change。

### 7. 輸出隔離與結果報告

- **量測輸出**：`lib/config.js` 新增 `LT_MEASURE_TAG`（`^[a-z0-9-]{1,32}$`）。有標籤時 summary 寫到 `/output/measure-<tag>-<原檔名>`（扁平檔名，不建子目錄，避免 k6 寫檔時目錄不存在），不在彙整白名單內；格式不符時與既有參數錯誤同樣在 setup 中止。量測執行一律帶標籤，正式驗收執行不帶。標籤解析的自我測試加入 `loadtest/tests/`。
- **舊結果搬移**：第一次量測之前，就把 `loadtest/.output/` 根目錄內 `k6-load-test` 的 12 份白名單 summary 與 `report-tables.md` 搬到 `loadtest/.output/k6-load-test-baseline/`，不刪除、不覆寫；根目錄的白名單檔名留給本 change 的正式驗收。
- 新增 `docs/load-test/p95-optimization-report.md`，保留 `docs/load-test/report.md` 作為優化前的基準紀錄不覆寫。
- 報告資料表沿用 `aggregate.js` 產生、原樣貼入的規則，並置於 `<!-- report-tables:begin -->`／`<!-- report-tables:end -->` 標記之間。新增 `loadtest/check-report-tables.sh <報告> <彙整輸出>`：擷取標記區段與彙整輸出逐字比對，不一致時非 0 結束；以一致／不一致兩組假資料的自我測試（`loadtest/tests/check-report-tables.test.sh`）驗證。
- 同一腳本另做 LT-REPORT-006 的自動化部分：從彙整輸出找出 Release P95 ≥ 500ms 的 scenario，要求報告中有 `<!-- unmet-reason:<scenario> -->` 區段（到下一個 `<!-- ` 標記或檔尾為止），區段內至少一個 `measure-` 開頭的檔名與一個 `<數字>ms`，且全文含 `P95 < 500ms`。腳本只能檢查「有沒有引用證據」，不能判斷原因是否合理，後者由 tasks 7.4 人工判讀補充。

## 安全確認

本 change 觸及外部輸入、資料庫讀寫與已登入端點，依 CLAUDE.md 逐條回答：

- **輸入驗證在哪一層**：`POST /api/orders` 的請求在 `PlaceOrderAsync` 開頭由既有 FluentValidation（`PlaceOrderRequestValidator`）驗證，本 change 不新增外部輸入欄位；新增的提早判斷與分區比對只使用已驗證過的 Id 與資料庫讀回的資料。
- **有沒有拼接 SQL 或 shell**：產品程式碼新增的查詢只用既有 EF Core repository 方法（`GetSeatsByIdsAsync` 以 LINQ `Contains` 產生參數化查詢），沒有 raw SQL。壓測工具的 `db-waits.sql` 是固定的唯讀查詢，不接受任何外部輸入；shell 腳本的參數（容器名、秒數）由開發者在本機輸入，腳本內以引號包住並驗證秒數為正整數。
- **EF Core／參數化**：是，見上一點。
- **N+1 風險**：無。分區比對以一次 `GetSeatsByIdsAsync` 批次取得所有所選座位，提早判斷只使用交易前已載入的資料，不新增逐筆查詢；且移除了鎖內每筆訂單載入整張座位圖的查詢。
- **需要什麼權限、在哪一層檢查**：`POST /api/orders` 維持既有 `[Authorize]`（已登入會員），由 ASP.NET Core 授權 middleware 在進入 Controller 前檢查；排隊資格（403）仍在交易內由 `OrderService` 檢查，提早判斷不會讓未入場者成功建立訂單（提早判斷只會拒絕，不會放行）。
- **有沒有可能被未授權使用者觸發**：否。提早判斷位在授權之後；它只可能讓請求更早失敗，不會略過任何既有的權限或資格檢查。分段耗時 log 預設關閉、不含買家 Id 與個資。
- **資訊洩漏**：座位狀態、分區與計數庫存本為公開資料（未授權的 `GET /api/events/{id}/seats`、ticket-types），提早 409／交易前分區 400 不提供比公開 API 更多的資訊；排隊模式下未入場者仍得到 403（TP-ORDER-022）。
- **前端**：本 change 不改前端。

## Risks / Trade-offs

- [結果只代表本機環境] → 所有數字只代表本機 Docker Compose／WSL2 的壓測環境（Release 組態 + Development 環境），報告 MUST 註明，不宣稱為正式部署的效能。
- [連線池或 `max_connections` 用盡造成的失敗] → 依執行類型分別處理：
  - seeder 失敗：不算一次執行，依 README 等待後重跑 seeder。
  - 量測執行中出現 `too many clients` 或連線逾時：該次量測標為無效，記錄原因後重跑，不混入同一份量測統計。
  - 重跑上限：同一 scenario 同一標籤連續 2 次因連線池／連線數無效，就停止重跑，視為「連線池可能是瓶頸」的證據，進入 task 3.4 的使用者決策點；連續 2 次因 Seq 筆數不符（掉事件）無效時同樣停止重跑並暫停請使用者決定（例如降低量測併發或改用其他收集方式），不得放寬筆數檢查；無效執行的次數與原因 MUST 寫進報告，不因判無效而消失。
  - 正式驗收執行：沿用既有規則「任何失敗都計入結果，不得排除或重跑」（`loadtest/README.md`），不套用上述無效判定。這兩條規則刻意不同：量測只用來找瓶頸，正式驗收用來判定達標。

- [量測時開 Debug log 會增加耗時] → 量測執行與驗收執行分開；報告註明量測執行的數字含 log 成本，只用來看比例與分布。
- [提前 409 讓少數「本來可能成功」的請求失敗（讀取後持有者釋放）] → 與提早一點送出時的結果等價，不影響超賣與公平性；在 design 與 spec delta 明確記錄。
- [交易前分區比對依賴座位／分區不可變] → 程式碼註解記錄不變量；未來新增修改座位圖功能時必須移回鎖內。
- [排隊模式切換時間差內 409 取代 403] → 只在 Admin 切換排隊模式的瞬間發生，且回應如實反映項目已不可售；spec delta 明確記錄。
- [A＋C 仍不足以達標（例如每筆 commit 的磁碟延遲在 WSL2 上過高）] → 依量測證據照實記錄未達標，不改門檻、不用犧牲耐久性的設定；候選 B 另開 change。
- [`pg_stat_activity` 取樣占用 1 條連線] → 報告註明；取樣只在量測執行使用，驗收執行不開。

## Migration Plan

- 無資料庫遷移。部署即生效；回滾為部署前一版即可，無資料相容性問題。

## Open Questions

- 連線池／`max_connections` 是否納入：待決策 6 第 2 步量測後由使用者決定。
