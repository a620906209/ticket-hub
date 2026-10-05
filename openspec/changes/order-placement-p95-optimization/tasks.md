## 1. 分段耗時量測（LT-MEASURE-001～004、012，design 決策 1）

- [ ] 1.1 先寫測試（`tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs`，新增可攔截 log 與切換 Debug 開關的測試用 logger 輔助類別）：Debug 開啟時成功下單輸出一筆含全部分段、不含買家 Id 的 log（LT-MEASURE-001）；以既有的交易前拒絕路徑（跨活動驗證錯誤）觸發時交易內分段為 null（LT-MEASURE-002 的前置驗證；提早 409 路徑在 5.1 驗證）；Debug 關閉時不輸出（LT-MEASURE-003）；例外路徑仍輸出且例外照常拋出，log 欄位集合與成功路徑相同、不附帶例外物件或訊息（LT-MEASURE-004）
- [ ] 1.2 `OrderService.PlaceOrderAsync` 以 `Stopwatch.GetTimestamp()` 記錄分段，`finally` 中以 `_logger.IsEnabled(LogLevel.Debug)` 守門輸出結構化 Debug log
- [ ] 1.3 `appsettings.json` 的 Console sink 補上 `Args.restrictedToMinimumLevel: Information`；新增 `docker-compose.loadtest-measure.yml`（只放 OrderService Debug override）；先在 `tests/ProjectC.WebApi.Tests` 新增設定測試，讀 `appsettings.json` 斷言 Console sink `Args.restrictedToMinimumLevel == "Information"`；先寫 `loadtest/tests/check-measure-logging.test.sh`（全部符合／Seq 0 筆／Seq 少於預期／Seq 多於預期／輪詢期間才補齊／429 從預期數扣除／有逾時即無效／Console 出現分段 log）再寫 `loadtest/check-measure-logging.sh`（LT-MEASURE-012）；實測確認 Seq 收到分段 log、console 沒有；同時確定 Seq 查詢算出分段 p50／p95 與筆數的具體做法並寫進 README
- [ ] 1.4 執行 Application 與 Infrastructure 既有測試，確認行為不變

## 2. 壓測量測工具（LT-MEASURE-005～011、013、014，LT-REPORT-005、006，design 決策 2、3、7）

- [ ] 2.1 `lib/config.js` 新增 `LT_MEASURE_TAG` 解析與 `measure-<tag>-` 檔名；先在 `loadtest/tests/` 寫自我測試：合法標籤的檔名不在彙整白名單內（LT-MEASURE-008）、不合法標籤產生錯誤；baseline 未帶標籤時也產生錯誤（LT-MEASURE-009）
- [ ] 2.2 `loadtest/baseline.js`：1 VU 依序 50 筆、每筆不同買家、`LT_BASELINE_TICKET=count|seat`、必須帶 `LT_MEASURE_TAG`；可測邏輯放 `lib/baseline.js` 純函式，先在 `loadtest/tests/baseline.test.js` 寫自我測試（LT-MEASURE-005、006）：
  - 設定解析：`LT_BASELINE_TICKET` 只接受 `count|seat`、缺 `LT_MEASURE_TAG` 時錯誤、summary 檔名為 `measure-<tag>-baseline-<count|seat>-summary.json`
  - 執行設定：匯出的 options 為 `vus: 1`、`iterations: 50`（依序）、含 `baseline_non_201` counter 的 `count==0` threshold（k6 threshold 失敗即非 0 exit code）
  - 指派：第 i 筆的買家 token 與座位 50 筆互不重複；數量票庫存設定 ≥ 50
  - 分區唯一性（LT-MEASURE-014）：以測試內自製的假 JWT（header.payload.signature，payload 為 base64url JSON）驗證——50 個不同 `sub` 通過；兩個 token 不同但 `sub` 相同時失敗；payload 非 base64url、非 JSON、缺 `sub`、`sub` 非字串、段數不是 3 時失敗；失敗訊息不含 token 與 `sub` 內容
  - 判定：全 201 通過、任一非 201 判無效；429 計入 `baseline_rate_limited` 並標示「被限流，量測無效」（LT-MEASURE-013）
  - 輸出：summary 摘要含 `p(50)`、`p(95)` 與無效旗標
  - 3.2 首次實跑時另以一次故意的非 201（例如重複座位）確認 exit code 非 0，結果記入報告
- [ ] 2.3 `loadtest/sql/db-waits.sql` 與 `loadtest/sample-db-waits.sh`；先寫 `loadtest/tests/sample-db-waits.test.sh`（一次性 `postgres:16-alpine` 容器，含 idle in transaction 連線，斷言 ≥ 4 組、包含該連線、不含取樣連線；以 `c=N` 有界結束、無殘留 `lt-db-waits` 連線、輸出檔已存在時中止）（LT-MEASURE-007）；中斷清理：以 `set -m` 讓背景工作不忽略 SIGINT，取樣中分別送 SIGINT、SIGTERM，斷言 5 秒內殘留為 0（LT-MEASURE-010）；以不設 `PGAPPNAME` 的前綴啟動，斷言非 0 結束且無殘留（LT-MEASURE-011）；啟動前已有殘留時中止；新增 `.gitattributes`（`*.sh`、`*.sql` eol=lf）
- [ ] 2.4 `loadtest/check-report-tables.sh` 與 `loadtest/tests/check-report-tables.test.sh`：資料表一致／不一致兩組假資料（LT-REPORT-005）；未達標原因檢查——有未達標 scenario 且有合格 `unmet-reason` 區段通過、缺區段失敗、區段內沒有 `measure-` 檔名或毫秒數值失敗、報告缺少 `P95 < 500ms` 門檻字樣失敗、全部達標時不要求區段（LT-REPORT-006 的自動化部分）
- [ ] 2.5 `loadtest/README.md` 補「瓶頸量測」章節：量測 override 檔用法、`LT_MEASURE_TAG`、基準與取樣指令、新增的自我測試指令、量測執行與正式驗收執行必須分開；`loadtest-release.yml` 與 `loadtest-measure.yml` 的疊加指令；`check-measure-logging.sh` 的用法（每次量測後必跑，非 0 即無效）

## 3. 優化前量測與決策點（design 決策 6 第 1–2 步）

- [ ] 3.1 把 `loadtest/.output/` 根目錄內 `k6-load-test` 的 12 份白名單 summary 與 `report-tables.md` 移到 `loadtest/.output/k6-load-test-baseline/`
- [ ] 3.2 在現行程式碼上以 `LT_MEASURE_TAG=before` 執行無競爭基準（數量票、座位票），記錄 P50／P95 與分段中位數
- [ ] 3.3 疊加量測 override 與等待事件取樣，Release 組態下兩個 scenario 各執行 1 次（`LT_MEASURE_TAG=before`），整理分段耗時與等待事件分布；每次量測後立即匯出分段統計到 `measure-<tag>-phases-<scenario>.json`，並執行 `check-measure-logging.sh`，非 0 即該次無效；出現 `too many clients` 或連線逾時也標為無效並重跑，同一 scenario 連續 2 次因此無效就停止重跑、直接進 3.4 決策點（連續 2 次因 Seq 筆數不符無效時同樣暫停請使用者決定），無效次數與原因記入報告（4.3、5.4 同樣適用；正式驗收不適用，見 design Risks）
- [ ] 3.4 依量測寫下瓶頸判定（序列化／連線池／其他）到報告草稿；**若連線池等待為主要來源，或 3.3 因連線池／連線數連續無效，暫停並請使用者決定是否納入連線池調整**

## 4. 優化 C：分區比對移到交易前（TP-ORDER-021、026、029～033，design 決策 4）

- [ ] 4.1 先寫單元測試：分區不一致回驗證錯誤且未開啟交易（以 `FakeUnitOfWork` 斷言未呼叫 `BeginTransactionAsync`）（TP-ORDER-021）；排隊模式活動未入場會員選錯分區回驗證錯誤而非 403（TP-ORDER-026）；座位樣板查無資料回 404 且未開啟交易（TP-ORDER-029）；座位樣板屬於另一張座位圖時回 404（TP-ORDER-030，單元測試，不持久化任何異常資料）：
  - Fixture：`FakeSeatMapRepository` 放座位圖 M1（活動的）與 M2（另一張）；`FakeEventRepository` 只放活動 E（`SeatMapId = M1`）。異常 `EventSeat` 的 `EventId = E.Id`、`SeatId` 為 M2 的座位，因 `EventSeat` 建構子為 `internal`，由一個只在測試內使用、不加入任何 repository 的 `Event` 實例（Id 同 E、`SeatMapId = M2`）呼叫 `CreateEventSeats(M2)` 產生，放進 `FakeEventSeatRepository`
  - 交易外讀到活動：斷言 404、不建立訂單、`BeginTransactionCallCount == 0`；票種分區刻意與座位不同，斷言回 404 而非 400（成員比對先於分區比對）
  - 交易外讀不到活動：`GetByIdOverride` 回傳 null、`GetForUpdateOverride` 回傳 E（E 設為排隊模式、買家未入場）；票種分區**刻意與座位不同**；斷言 404（非 400、非 403）、不建立訂單、`BeginTransactionCallCount == 1`、`FakeEventSeatRepository.GetForUpdateCallCount == 0`、`FakeTicketTypeRepository.GetForUpdateCallCount == 0`、排隊紀錄未被鎖定讀取（鎖內順序：成員 → 分區 → 排隊資格 → 座位／票種鎖定）
  - 交易外讀不到活動、座位圖相同但分區不一致（TP-ORDER-033）：斷言 400、`BeginTransactionCallCount == 1`、座位／票種 `GetForUpdateCallCount == 0`
  - 其他：`tests/ProjectC.Domain.Tests` 以反射斷言 `Seat.SeatMapId`、`Seat.ZoneCode`、`EventSeat.SeatId`、`EventSeat.EventId`、`Event.SeatMapId` 沒有 setter／`init`（TP-ORDER-031）；同時違反限購與分區時回限購錯誤且未開啟交易（TP-ORDER-032）；確認既有 TP-ORDER-004 測試仍通過
- [ ] 4.2 交易前以 `GetSeatsByIdsAsync`（已核對為 no-tracking）比對分區，移除鎖內的 Event 重讀與座位圖載入；交易前顯式比對 `Seat.SeatMapId == Event.SeatMapId`（交易外讀不到活動時，成員與分區比對一起移到鎖內實名檢查之後、排隊資格檢查之前），順序為樣板存在 → 座位圖成員 → 分區；以註解記錄不可變前提
- [ ] 4.3 以 `LT_MEASURE_TAG=after-c` 重跑 3.3 的座位票量測，記錄變化

## 5. 優化 A：交易前提早回 409（TP-ORDER-017～020、022～025、027、028，LT-MEASURE-002，design 決策 5）

- [ ] 5.1 先寫單元測試（皆以 `FakeUnitOfWork` 斷言是否開啟交易）：座位已售出（TP-ORDER-017）；座位由其他訂單暫扣中（TP-ORDER-018）；暫扣已逾時不被提早拒絕、成功建立（TP-ORDER-019）；計數票種庫存不足（TP-ORDER-020）；交易外讀到排隊模式時未入場買家選已售座位回 403（TP-ORDER-022）；交易外讀不到活動時開啟交易並由交易內回 409（TP-ORDER-024）；交易外讀到未開排隊、鎖內已開排隊且座位已售出時回 409 且未開啟交易（TP-ORDER-025，測試名稱／註解註明驗證的是「提早判斷以交易外讀到的旗標為準」，不是並發測試）；3 席其中 1 席已售出（TP-ORDER-027）；座位可售但計數庫存不足的混合訂單（TP-ORDER-028）；Debug 開啟時提早 409 路徑輸出一筆分段 log：結果為 `Conflict`、未開啟交易、`BeginTransaction`／`EventLockWait`／`InLock`／`Commit` 為 null、`PreTransaction`／`Total` 有值（LT-MEASURE-002）；並斷言提早 409 與鎖內 409（`CreateOrderHandler`）的訊息相同
- [ ] 5.2 整合測試（Testcontainers）：依 design 決策 5 的時序控制（測試持有 Event 鎖、以 `pg_locks` 確認下單請求在等鎖後才放行），驗證交易外可鎖定、取鎖前被暫扣時回 409 且不超賣（TP-ORDER-023）；確認既有 `OrderServiceQueueModeLinearizationTests` 與 `OrderServiceConcurrencyTests` 仍通過
- [ ] 5.3 實作提早判斷：位置在交易外既有檢查與分區比對之後、`BeginTransactionAsync` 之前；僅在交易外讀到活動且未開排隊模式時執行；座位以 `IsAvailableForHold(now)` 判斷；409 訊息與 `CreateOrderHandler` 共用同一處定義
- [ ] 5.4 以 `LT_MEASURE_TAG=after-a` 重跑 3.3 的量測（兩個 scenario），記錄變化

## 6. 防禦性檢查與審查

- [ ] 6.1 對 `OrderService.PlaceOrderAsync` 套用 `.claude/skills/hardener/SKILL.md` 檢查清單
- [ ] 6.2 呼叫 strict-reviewer，修正 blocking 問題

## 7. 正式驗收（LT-REPORT-005、006，design 決策 7）

- [ ] 7.1 先確認 api 容器環境變數不含量測覆寫（`Serilog__MinimumLevel__Override__*`），再依 `loadtest/README.md` 執行 Debug／Release × 數量票／座位票各 3 次（不帶 `LT_MEASURE_TAG`、不疊加量測 override、不取樣），以 `aggregate.js` 彙整
- [ ] 7.2 撰寫 `docs/load-test/p95-optimization-report.md`（註明結果只代表本機 Docker Compose／WSL2 環境，以及被判無效的量測執行與原因）：優化前量測、各優化變化、正式驗收資料表（置於標記區段內）、最終判定與限制
- [ ] 7.3 以 `check-report-tables.sh` 比對報告資料表與彙整輸出並檢查未達標原因區段（LT-REPORT-006），並以 `git diff --exit-code docs/load-test/report.md` 確認基準報告未修改（LT-REPORT-005）
- [ ] 7.4 人工判讀補充 LT-REPORT-006（自動化部分已在 7.3）：每個未達標的 scenario 都有引用具體量測數字的原因說明，門檻與口徑與 `load-testing` 既有 Requirement 一致（全部達標時記錄「不適用」）

## 8. 文件同步

- [ ] 8.1 更新 `docs/project-scope.md` §8 P95 待辦狀態（達成，或未達成與後續方向，例如候選 B 另開 change）
