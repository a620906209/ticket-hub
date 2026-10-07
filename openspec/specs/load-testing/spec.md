# load-testing Specification

## Purpose
TBD - created by archiving change k6-load-test. Update Purpose after archive.
## Requirements
### Requirement: 壓測 seeder 準備壓測帳號與 token 檔

系統 SHALL 提供獨立的壓測 seeder console 工具（不屬於 `src/` 產品專案），在 Development 環境下：

- 建立（或沿用既有的）N 個壓測買家會員（預設 N = 500）與 1 個壓測 Admin。
  - 壓測 Admin 為一個 Approved Organizer 的 Owner。
  - 會員 email 一律使用保留網域 `loadtest.invalid`，不得對應真實信箱。
- 以產品既有的 token 簽發實作，在 process 內簽發 access token。
  - 買家 token 不帶 OrganizerId。
  - Admin token 帶該 Organizer 的 OrganizerId。
- 將所有 token 寫入單一 token 檔，供 k6 讀取。

seeder MUST 為冪等：重複執行不得產生重複會員，且每次執行都重新簽發 token（覆寫 token 檔）。

seeder MUST 只在本機 compose 環境執行。以下任一情況 MUST 以非 0 exit code 結束，且不建立 DbContext（因此不寫入 DB）、不寫 token 檔：

- `ASPNETCORE_ENVIRONMENT` 不是 `Development`。
- 資料庫連線字串的 Host 不是 compose service name `db`。
- JWT 設定（Issuer、Audience、SigningKey）缺漏，或 SigningKey 少於 32 個字元（與產品 JWT 設定規則相同）。
- `--buyers` 不是 1–1000 之間的整數，或出現未支援的參數。

資料庫有未套用的 migration 時，seeder MUST 以非 0 exit code 結束，錯誤訊息 MUST 提示執行 migration 指令，且不寫入 DB、不寫 token 檔。偵測 migration 需要建立 DbContext（只讀查詢），因此這一項不適用上面「不建立 DbContext」的保證。

以下情況 MUST 以非 0 exit code 結束，且不寫 token 檔（此時 DB 可能已寫入會員或 Organizer，下次執行會沿用）：

- 壓測 Admin email 對應的既有帳號 Role 不是 Admin。
- 壓測 Admin 擁有的 Approved「LoadTest Organizer」不是恰好 1 個。（0 個的情況只會在 `SeedAsync` 未完成時發生，難以在測試中構造，以防禦性檢查處理，不另設 Scenario。）

token 檔路徑 MUST 固定為 `loadtest/.output/tokens.json`，不得由參數指定。

seeder 輸出 MUST NOT 印出任何 token 或密碼內容。

#### Scenario: LT-SEED-001 首次執行建立帳號並產出 token 檔
- **WHEN** 在空的壓測帳號狀態下以 N = 500 執行 seeder
- **THEN** DB 新增 500 個 `loadtest.invalid` 網域的 Active 買家會員，以及 1 個 Admin（擁有 Approved Organizer）
- **AND** token 檔包含 1 個 admin token 與 500 個買家 token，每個買家 token 對應不同會員
- **AND** 每個 token 都能通過與產品相同設定的 JWT 驗證參數（issuer、audience、signing key、lifetime）

#### Scenario: LT-SEED-002 重複執行不重複建立會員
- **WHEN** seeder 已執行過一次後再以相同 N 執行
- **THEN** 壓測會員總數仍為 N，且 Admin 只有 1 個
- **AND** token 檔被新簽發的 token 覆寫

#### Scenario: LT-SEED-003 非 Development 環境拒絕執行
- **WHEN** 以 `ASPNETCORE_ENVIRONMENT` 非 `Development` 的設定執行 seeder
- **THEN** seeder 以非 0 exit code 結束，不建立 DbContext，也不寫 token 檔

#### Scenario: LT-SEED-007 JWT 設定不完整時拒絕執行
- **WHEN** 執行 seeder 時 `Jwt:Issuer`、`Jwt:Audience`、`Jwt:SigningKey` 任一為空，或 `Jwt:SigningKey` 少於 32 個字元
- **THEN** seeder 以非 0 exit code 結束，不建立 DbContext，也不寫 token 檔

#### Scenario: LT-SEED-005 非法參數或目標不符時拒絕執行
- **WHEN** 以 `--buyers 0`、`--buyers -1`、`--buyers 1001`、`--buyers abc`、未支援的參數（例如 `--output x`），或連線字串 Host 不是 `db` 執行 seeder
- **THEN** seeder 以非 0 exit code 結束，不建立 DbContext，也不寫 token 檔

#### Scenario: LT-SEED-006 有未套用 migration 時拒絕執行
- **WHEN** 資料庫有未套用的 migration 時執行 seeder
- **THEN** seeder 以非 0 exit code 結束，錯誤訊息提示執行 `docker compose exec api dotnet ef database update`，不寫入 DB，且不寫 token 檔

#### Scenario: LT-SEED-008 壓測 Admin 狀態異常時中止
- **WHEN** 壓測 Admin email 已被一個 Role 不是 Admin 的帳號使用，或該 Admin 擁有 2 個以上 Approved 的「LoadTest Organizer」
- **THEN** seeder 以非 0 exit code 結束、說明原因，且不寫 token 檔

#### Scenario: LT-SEED-004 不輸出機敏內容
- **WHEN** seeder 成功執行完成
- **THEN** 標準輸出只包含筆數與檔案路徑等摘要，不包含任何 token 字串或密碼

### Requirement: k6 setup 透過真實 admin API 建立測試活動

每次壓測執行時，k6 `setup()` SHALL 以壓測 Admin token 透過既有 admin API 依序建立：場館、座位圖、活動、票種。

- 開始前 MUST 確認 token 檔的買家 token 數量 ≥ 500 且彼此不重複，否則中止。
- 活動 Title MUST 以 `[LoadTest] ` 開頭，以便辨識與手動清理。
- 活動規模依 docs/project-scope.md §3 基準：座位圖共 2000 席，活動共 20 個票種。
- 其中包含被搶的目標票種：
  - 數量票 scenario：1 個 `RequiresSeat = false`、`AvailableQuantity = 50` 的數量票種。
  - 座位票 scenario：1 個 `RequiresSeat = true` 的座位票種，對應恰好 50 席的分區。
- 活動 MUST 處於販售中，且 MUST NOT 啟用排隊模式。販售中依產品既有規則 `SalesStartAtUtc <= 現在 < (SalesEndAtUtc ?? StartAtUtc)` 判定；本壓測不設定販售結束時間，所以條件為：`SalesStartAtUtc` ≤ 現在、`SalesEndAtUtc` 為空，且現在 < `StartAtUtc`。
- 任一建立步驟回應非 2xx 時，`setup()` MUST 中止整次壓測，不得在部分建立的資料上繼續下單。

#### Scenario: LT-SETUP-001 建立活動成功
- **WHEN** k6 `setup()` 以有效 admin token 執行
- **THEN** 建立出 2000 席、20 個票種、販售中、非排隊模式的活動，並把目標票種 id（座位票另含 50 個 event seat id）傳給 VU
- **AND** 座位票 scenario 的 setup 以公開 API 核對：目標票種 `ZoneCode` 為 `HOT` 且 `RequiresSeat` 為 true；`HOT` 分區恰好 50 席且全部可售；交給 VU 的座位池與這 50 個 id 集合相等。任一不符 MUST 中止且不送出任何下單請求

#### Scenario: LT-SETUP-002 建立步驟失敗時中止
- **WHEN** 任一 admin API 呼叫回應非 2xx（例如 admin token 無效或過期回應 401），或 setup 逾時
- **THEN** 壓測中止且不送出任何下單請求

#### Scenario: LT-SETUP-003 買家 token 不足時中止
- **WHEN** token 檔的買家 token 少於 500 個或有重複
- **THEN** 壓測中止且不送出任何下單請求

### Requirement: 500 名併發買家搶 50 張票

k6 MUST 以寫死的 `http://api:8080` 連線 API，不得由環境變數或參數覆寫。

每個 scenario SHALL 以 500 個 VU 併發，每個 VU 使用 token 檔中不同的買家 token，且只送出 1 次 `POST /api/orders`：

- 數量票：`Quantity = 1`。
- 座位票：從 50 席中隨機選 1 席。

下單成功（201）的 VU SHALL 立即以同一 token 呼叫 `POST /api/orders/{id}/confirm` 完成付款，使售出結果反映在銷售報表。

#### Scenario: LT-RUN-001 數量票 scenario
- **WHEN** 500 個 VU 併發各下 1 張目標數量票
- **THEN** 恰有 50 個 VU 下單成功（201），其餘回應皆為 409（庫存不足），沒有任何 5xx

#### Scenario: LT-RUN-002 座位票 scenario
- **WHEN** 500 個 VU 併發各從 50 席中隨機選 1 席下單
- **THEN** 下單成功筆數不超過 50，其餘回應皆為 409（座位已被持有），沒有任何一席被兩筆成功訂單持有，沒有任何 5xx

### Requirement: 通過門檻與超賣驗證

每個 scenario 的通過門檻 SHALL 以 k6 threshold 表達，任一未達成時 k6 MUST 以非 0 exit code 結束：

- `POST /api/orders` 請求（含成功與售完回應，不含 confirm 與 setup 請求）的 P95 < 500ms。
- `POST /api/orders` 的 5xx 筆數 = 0。
- `POST /api/orders` 中既非 201 也非 409 的回應（含 4xx 與網路錯誤）筆數 = 0。
- 500 個 VU 都在搶票時限內完成（下單，以及成功時的付款），確保驗證階段開始時沒有進行中的付款。
- 下單成功後的付款（confirm）失敗筆數 = 0（否則銷售報表無法反映下單結果）。
- 超賣 0%（k6 端）：k6 端統計的下單成功（201）筆數 ≤ 50。
- 超賣 0%（伺服器端）：搶票結束後的驗證階段以 admin `GET /api/admin/events/{eventId}/sales-report` 取得目標票種 `QuantitySold`，MUST ≤ 50；並以公開 API 交叉核對——數量票目標票種剩餘量 MUST 為 0，座位票目標分區中非可售席數 MUST 等於 `QuantitySold`，且非目標分區 MUST 沒有任何非可售的座位。
- 數量票 scenario 另 MUST 恰好售出 50 張（k6 成功筆數 = 50 且 `QuantitySold` = 50）。
- 座位票 scenario 的 k6 成功筆數 MUST ≥ 1。

故障注入開關（`LT_FAULT`）只可讓門檻更嚴或讓輸入變壞，MUST NOT 放寬任何門檻。

因 k6 自訂 metric 在執行期間無法跨 scenario 讀取，「`QuantitySold` 等於 k6 端成功筆數」不以 threshold 判定，而由 `handleSummary` 自動比對，結果寫入同一份 summary JSON 的 `runVerdict`。單次執行 MUST 同時滿足 exit code 為 0 與 `runVerdict.passed` 為 true 才算通過；驗證階段沒有取得 `QuantitySold` 時 MUST 判為未通過。

#### Scenario: LT-GATE-001 全部門檻達成
- **WHEN** 某 scenario 執行結束，P95 < 500ms、無 5xx、無非 201／409 回應、500 個 VU 皆在時限內完成、無付款失敗、k6 成功筆數 ≤ 50、`QuantitySold` ≤ 50 且交叉核對一致（數量票另為恰好 50）
- **THEN** k6 以 exit code 0 結束

#### Scenario: LT-GATE-002 P95 超標
- **WHEN** `POST /api/orders` 的 P95 ≥ 500ms
- **THEN** k6 以非 0 exit code 結束，且 summary 標示 P95 threshold 失敗

#### Scenario: LT-GATE-003 k6 端偵測到超賣
- **WHEN** k6 成功筆數大於 50
- **THEN** k6 以非 0 exit code 結束，且 summary 中 `orders_created` 的上限 threshold 標示失敗

#### Scenario: LT-GATE-004 伺服器端偵測到超賣或不一致
- **WHEN** 驗證階段取得的 `QuantitySold` 大於 50，或數量票 `QuantitySold` 不等於 50，或數量票剩餘量不為 0，或座位票目標分區非可售席數不等於 `QuantitySold`，或座位票非目標分區有非可售座位
- **THEN** 驗證判斷回報對應的失敗原因，`oversell_check_failed` threshold 標示失敗，k6 以非 0 exit code 結束

#### Scenario: LT-GATE-005 非預期回應
- **WHEN** 任一下單回應既不是 201 也不是 409（例如 401、429）
- **THEN** k6 以非 0 exit code 結束，且 summary 中 `place_order_unexpected` threshold 標示失敗

#### Scenario: LT-GATE-006 座位票無人成功
- **WHEN** 座位票 scenario 的 k6 成功筆數為 0
- **THEN** k6 以非 0 exit code 結束

### Requirement: 在 compose 環境內執行壓測

k6 SHALL 以 docker compose 中 `loadtest` profile 的服務執行，以 compose service name `http://api:8080` 連線 API。

- k6 image MUST 固定版本 tag。
- 預設的 `docker compose up`（未指定 profile）MUST NOT 啟動 k6 服務。
- seeder SHALL 透過 `docker compose exec api` 在既有 api 容器內執行，沿用其 DB 連線與 JWT 設定。
- token 檔所在目錄 MUST 列入 `.gitignore`。
- k6 的腳本目錄 MUST 以唯讀掛載；輸出目錄（讀 token 檔、寫 summary JSON）另行以可寫掛載。

#### Scenario: LT-COMPOSE-001 預設啟動不含 k6
- **WHEN** 執行 `docker compose up -d`
- **THEN** 不會建立或啟動 k6 容器

#### Scenario: LT-COMPOSE-002 以 profile 執行壓測
- **WHEN** 先執行 seeder，再執行 `docker compose --profile loadtest run --rm --no-deps k6 run <scenario 腳本>`
- **THEN** k6 從容器內連到 `http://api:8080` 完成壓測，並輸出 summary（含 JSON 匯出檔）

#### Scenario: LT-COMPOSE-003 token 檔不進版控
- **WHEN** seeder 產出 token 檔後執行 `git status`
- **THEN** token 檔不出現在未追蹤檔案清單中

### Requirement: Release 組態對照與重複執行

系統 SHALL 提供 `docker-compose.loadtest-release.yml`，只覆寫 api 的啟動指令，讓 api 以 Release 組態執行，且 service name 與 k6 連線位址不變。

- 每個 scenario SHALL 在 Debug（既有 `dotnet watch`）與 Release 兩種組態各執行 3 次。
- 每一次執行都 MUST 通過「通過門檻與超賣驗證」的全部門檻；任一次未通過，該組態的該 scenario 即判定為未通過。
- docs/project-scope.md §5 目標的最終判定 MUST 以 Release 組態的結果為準；Debug 結果列為對照，不影響判定。
- 每一次執行前 MUST 重跑 seeder 重新簽發 token；任何失敗（包含 token 過期造成的 401）都計入該次結果，不得排除或以重跑取代。
- k6 以 `LT_API_BUILD`（只接受 `debug` 或 `release`）標記組態，只用於 summary 檔名；其他值或未設定時 MUST 中止，且不送出任何下單請求。
- 非故障注入的執行 MUST 以 `LT_RUN`（只接受 `1`、`2`、`3`）標記執行序號，summary 檔名固定為 `<scenario>-<LT_API_BUILD>-run<LT_RUN>-summary.json`；`LT_RUN` 非法、未設定，或該檔名已存在時 MUST 中止且不送出任何下單請求（不得覆寫既有結果）。

#### Scenario: LT-RELEASE-001 切換到 Release 組態並還原
- **WHEN** 以 `docker compose -f docker-compose.yml -f docker-compose.loadtest-release.yml up -d --no-deps api` 切換，之後以 `docker compose up -d --no-deps api` 還原
- **THEN** 切換後 api 程序的指令列含 `-c Release` 且 `GET /api/events` 回 200；還原後 api 程序回到 `dotnet watch`，db、redis 容器沒有被重建

#### Scenario: LT-RELEASE-002 組態標籤非法時中止
- **WHEN** 未設定 `LT_API_BUILD`，或設為 `debug`、`release` 以外的值；或 `LT_RUN` 未設定、非法，或對應的 summary 檔已存在
- **THEN** k6 中止且不送出任何下單請求

#### Scenario: LT-REPEAT-001 三次皆通過
- **WHEN** 某組態的某 scenario 連續 3 次執行都通過全部門檻
- **THEN** 報告判定該組態的該 scenario 通過，並列出 3 次的 P95／P99 與中位數、最小值、最大值

#### Scenario: LT-REPEAT-002 任一次未通過
- **WHEN** 某組態的某 scenario 的 3 次執行中有任一次未通過門檻
- **THEN** 報告判定該組態的該 scenario 未通過，並記錄是第幾次、哪個門檻失敗

### Requirement: 壓測結果報告

系統 SHALL 在 `docs/load-test/report.md` 提供壓測結果報告，內容包含：

- 執行環境（主機規格、compose 服務、k6 版本、資料規模）
- 重現步驟（指令）
- 兩個 scenario 在 Debug 與 Release 組態下各 3 次的結果：P95／P99、成功筆數、售完回應數、5xx 數、銷售報表核對結果、門檻是否達成；以及每組 3 次 P95／P99 的中位數、最小值、最大值
- §5 目標的最終判定（以 Release 為準）與 Debug／Release 的差異
- 判讀與限制：本機 Docker Desktop／WSL2 環境非正式環境、Release 組態仍搭配 Development 環境設定、排隊模式未涵蓋、token 由 seeder 簽發未走真實登入

報告中的資料表 MUST 由彙整腳本從 12 份 summary JSON 產生後原樣貼入，不得手填；summary JSON 缺少預期的 metric 或欄位時，彙整 MUST 將該次判為未通過並標示缺少的欄位，MUST NOT 以 0 代替；報告 MUST 逐 scenario 比對 k6 成功筆數與 `QuantitySold`，兩者不一致時報告 MUST 判定該 scenario 未通過。

#### Scenario: LT-REPORT-001 報告與實際執行一致
- **WHEN** 檢視報告中任一 scenario 的 P95 與成功筆數
- **THEN** 其數值與同次執行保存的 k6 summary JSON 一致：彙整腳本以假資料測試時，產出表格的數字等於輸入；正式報告的資料表與彙整腳本輸出 `diff` 無差異

#### Scenario: LT-REPORT-002 報表與 k6 成功筆數不一致
- **WHEN** 某 scenario 的 `QuantitySold` 與 k6 成功筆數不同
- **THEN** 該次執行的 `runVerdict.passed` 為 false 且失敗原因記錄兩個數字，彙整後報告將該組判定為未通過

#### Scenario: LT-REPORT-004 彙整腳本只讀白名單檔名
- **WHEN** 彙整腳本的輸入目錄同時含 12 個白名單檔名的 summary，以及其他 summary 檔（故障注入、`invalid`、`precheck/` 下的檔案）
- **THEN** 彙整結果只包含 12 個白名單檔案；白名單中缺少的檔案記為缺檔、該組判未通過

#### Scenario: LT-REPORT-003 summary 缺少預期欄位
- **WHEN** 某份 summary JSON 缺少預期的 metric 或欄位（例如 `p(95)`、`orders_created`、某個 threshold 的 `ok`、`verify_quantity_sold`）
- **THEN** 該次判為未通過、失敗原因寫明缺少的欄位，表格中該格顯示「缺少」而非 0，且不參與統計

### Requirement: 下單分段耗時量測
系統 SHALL 在建立訂單流程中，以單調時鐘（`Stopwatch`）量測各分段耗時，並在每次建立訂單結束時（含成功、業務失敗與例外）輸出一筆 Debug 等級的結構化 log，欄位包含：交易前處理、取得資料庫連線（交易前處理的一部分：從開始到下單持有的連線開啟完成；開啟前就被拒絕時為空值）、開啟交易（沿用已取得的連線，不再含連線池等待）、等待活動鎖、取得活動鎖後到 commit 前、commit、總耗時（毫秒），以及結果（成功或錯誤類型）與是否含座位項目。流程未到達的分段 MUST 記為空值，不得記為 0。

- 此 log MUST 預設關閉，僅在以設定覆寫該類別的最低 log 等級為 Debug 時輸出；關閉時 MUST NOT 組裝 log 參數。
- 分段耗時 MUST NOT 以牆上時鐘時間戳相減計算。
- log 欄位僅限上述分段耗時、結果與是否含座位項目，MUST NOT 包含買家 Id 或任何個資（量測只需要分布，不需要追溯個別買家）。

#### Scenario: LT-MEASURE-001 開啟 Debug 時輸出分段耗時
- **WHEN** 該類別最低 log 等級為 Debug，買家成功建立一筆訂單
- **THEN** 系統輸出一筆 Debug log，所有分段欄位皆有值，結果為成功

#### Scenario: LT-MEASURE-002 交易前即被拒絕時未到達的分段為空值
- **WHEN** 該類別最低 log 等級為 Debug，建立訂單請求在開啟交易前就被拒絕（含交易前驗證錯誤與提早回 409）
- **THEN** 系統輸出一筆 Debug log，交易前處理與總耗時有值，取得資料庫連線在提早回 409 時有值、在請求格式驗證（validator）失敗時為空值、在 validator 之後的其他交易前拒絕（票種或座位不存在、跨活動、實名、限購等）時有值，開啟交易、等待活動鎖、鎖內處理、commit 為空值，結果為對應的錯誤類型（提早回 409 時為 `Conflict`），且未開啟資料庫交易

#### Scenario: LT-MEASURE-003 預設不輸出
- **WHEN** 使用預設 log 設定建立訂單
- **THEN** 系統不輸出分段耗時 log

#### Scenario: LT-MEASURE-004 例外路徑仍輸出
- **WHEN** 該類別最低 log 等級為 Debug，建立訂單流程拋出例外
- **THEN** 系統仍輸出一筆分段耗時 log，例外照常向上拋出，不被吞掉

### Requirement: 壓測瓶頸量測工具
壓測工具 SHALL 提供下列量測手段，用於定位下單延遲來源；量測執行與正式驗收執行（`通過門檻與超賣驗證`）MUST 分開進行，量測執行可開啟分段耗時 log 與資料庫等待事件取樣，正式驗收執行 MUST 關閉兩者。

- **量測輸出隔離**：量測執行（含無競爭基準與量測用的 `count-ticket.js`／`seat-ticket.js` 執行）MUST 以量測標籤 `LT_MEASURE_TAG`（小寫英數與連字號，1～32 字元）執行，summary 寫到 `measure-<LT_MEASURE_TAG>-` 開頭的檔名，MUST NOT 使用正式驗收的白名單檔名、MUST NOT 被彙整腳本讀取；標籤格式不符時 MUST 中止且不送出任何下單請求；無競爭基準未帶標籤時同樣 MUST 中止。量測標籤只改變輸出檔名，MUST NOT 改變任何門檻或判定。`Release 組態對照與重複執行` 的固定檔名規則僅適用於未帶 `LT_MEASURE_TAG` 的正式驗收執行。無競爭基準的 summary 檔名固定為 `measure-<LT_MEASURE_TAG>-baseline-<count|seat>-summary.json`。

- **無競爭基準**：以 1 個 VU 依序送出 50 筆下單，每筆使用不同買家 token，數量票庫存足夠、座位票逐筆選不同座位，全部 MUST 為 201；任一筆非 201 時該次基準量測判為無效。被限流（429）MUST 另外計數並標示為「被限流，量測無效」；基準量測 MUST NOT 為了避開限流而修改限流設定。setup MUST 在送出任何下單之前，解析前 50 個買家 token 的會員識別 claim（與 API 限流分區鍵相同的 `sub`），確認為 50 個彼此不同的會員；任一 token 無法解析、缺少該 claim 或會員不足 50 個時 MUST 中止且不送出任何下單。解析只用於此檢查，MUST NOT 記錄 token 或會員識別內容。活動建立方式與規模比照 `k6 setup 透過真實 admin API 建立測試活動`。
- **資料庫等待事件取樣**：壓測期間在 db 容器內以單一連線週期性（200ms）查詢 `pg_stat_activity`，限定應用資料庫並排除取樣連線自己，依狀態與等待事件分組計數，輸出至不進版控的輸出目錄；只做唯讀查詢。取樣 SQL 獨立成檔，供自動化測試在一次性的 PostgreSQL 容器（非開發用 db 服務）上驗證。取樣連線 MUST 以固定 application name 建立，啟動後 MUST 確認恰好一條該名稱的連線，否則中止；正常結束或被中斷（SIGINT／SIGTERM）時 MUST 以同時限定資料庫與 application name 的條件終止取樣連線，並確認沒有殘留，有殘留時以非 0 結束。

- **分段 log 量測有效性**：每次開啟分段耗時 log 的量測執行後，MUST 檢查 Seq 收到的分段 log 筆數等於該次有收到回應且非 429 的下單請求數（Seq 非同步寫入，檢查前 MUST 等待補齊，上限 15 秒；有任何 k6 逾時即判為無效），且 api console 輸出中沒有分段 log；任一不符 MUST 以非 0 結束，該次量測判為無效，不得寫入報告。Console sink MUST 由設定檔固定限制為 Information 以上，不依賴量測時的覆寫。

#### Scenario: LT-MEASURE-005 無競爭基準全部成功
- **WHEN** 執行無競爭基準量測（數量票或座位票）
- **THEN** 50 筆下單皆為 201，輸出 k6 端 P50／P95

#### Scenario: LT-MEASURE-006 無競爭基準出現非 201
- **WHEN** 無競爭基準量測中有任一筆回應非 201
- **THEN** k6 以非 0 exit code 結束，該次基準判為無效

#### Scenario: LT-MEASURE-007 等待事件取樣
- **WHEN** 在一次性 PostgreSQL 容器上，另一條連線處於交易中閒置（idle in transaction）時，執行取樣約 1 秒
- **THEN** 輸出至少 4 組分組計數，包含該 idle in transaction 連線，且不包含取樣連線自己

#### Scenario: LT-MEASURE-010 取樣中斷時清理連線
- **WHEN** 在一次性 PostgreSQL 容器上執行取樣，並在取樣期間送出 SIGINT 或 SIGTERM
- **THEN** 取樣腳本結束後 5 秒內，該資料庫沒有任何以取樣 application name 建立的連線

#### Scenario: LT-MEASURE-011 取樣連線 application name 未生效
- **WHEN** 取樣啟動後找不到恰好一條以取樣 application name 建立的連線
- **THEN** 腳本清理後以非 0 結束，不產出取樣結果

#### Scenario: LT-MEASURE-012 分段 log 設定未生效時量測無效
- **WHEN** 量測執行後，等待補齊後 Seq 的分段 log 筆數與有回應且非 429 的下單請求數不符、有 k6 逾時，或 api console 出現分段 log
- **THEN** 檢查腳本以非 0 結束，該次量測判為無效

#### Scenario: LT-MEASURE-013 基準量測被限流
- **WHEN** 無競爭基準量測中有任一筆回應 429
- **THEN** k6 以非 0 exit code 結束，輸出標示「被限流，量測無效」

#### Scenario: LT-MEASURE-014 基準量測的買家 token 不屬於 50 個不同會員
- **WHEN** 前 50 個買家 token 中有兩個以上的 `sub` 相同，或有 token 無法解析出 `sub`
- **THEN** setup 中止，不送出任何下單請求，錯誤訊息不含 token 或會員識別內容

#### Scenario: LT-MEASURE-008 量測輸出不進入正式彙整
- **WHEN** 以 `LT_MEASURE_TAG=before` 執行 `seat-ticket.js`
- **THEN** summary 寫到 `measure-before-` 開頭的檔名，彙整腳本的白名單不包含此檔

#### Scenario: LT-MEASURE-009 量測標籤格式不符
- **WHEN** 以不符格式的 `LT_MEASURE_TAG`（例如含 `/` 或超過 32 字元）執行任一腳本，或未帶 `LT_MEASURE_TAG` 執行無競爭基準
- **THEN** setup 中止，不送出任何下單請求

### Requirement: P95 優化結果報告
系統 SHALL 在 `docs/load-test/p95-optimization-report.md` 提供優化結果報告，`docs/load-test/report.md` 保留為優化前基準，MUST NOT 覆寫。報告 MUST 包含：

- 優化前量測：無競爭基準、壓測時各分段耗時分布、資料庫等待事件分布，以及據此判定的瓶頸
- 每項優化後的量測變化
- 依 `Release 組態對照與重複執行` 重跑的正式驗收結果，資料表沿用 `壓測結果報告` 的彙整與原樣貼入規則；資料表 MUST 置於 `<!-- report-tables:begin -->` 與 `<!-- report-tables:end -->` 兩行標記之間，供比對腳本擷取
- §5 P95 目標的最終判定（以 Release 為準）；未達成時 MUST 以量測證據說明剩餘延遲來源，MUST NOT 調整門檻或 P95 口徑（201 與 409 合併計算）
- 量測限制：量測執行含 Debug log 與取樣連線的額外成本，只用來看比例與分布

#### Scenario: LT-REPORT-005 優化結果報告與重跑結果一致
- **WHEN** 檢視優化結果報告的正式驗收資料表
- **THEN** 以比對腳本擷取報告中標記區段內的資料表，與彙整腳本對重跑產生的 12 份 summary JSON 的輸出比對無差異（比對腳本本身以一致與不一致兩組假資料自我測試），且 `git diff` 顯示 `docs/load-test/report.md` 未被修改

#### Scenario: LT-REPORT-006 未達標時說明原因
- **WHEN** 重跑後 Release 任一 scenario 的 P95 仍 ≥ 500ms
- **THEN** 報告判定未達成，並引用分段耗時或等待事件的量測數據說明剩餘延遲來源，門檻與口徑不變。可自動驗證的部分由比對腳本檢查：依彙整輸出找出 Release P95 ≥ 500ms 的 scenario，每個都必須有 `<!-- unmet-reason:<scenario> -->` 區段，且區段內至少引用一個 `measure-` 開頭的量測檔名與一個毫秒數值；報告中出現 `P95 < 500ms` 門檻字樣。原因說明是否合理由人工判讀，作為補充。

### Requirement: P95 優化第二階段結果報告
系統 SHALL 在 `docs/load-test/p95-phase2-report.md` 提供第二階段（Event 共享鎖、單一連線）優化結果報告；`docs/load-test/report.md` 與 `docs/load-test/p95-optimization-report.md` 保留為先前基準，MUST NOT 覆寫。報告 MUST 包含：

- 起點：引用第一階段報告的正式驗收結果與分段耗時，不重量優化前。
- 每項優化後的量測變化：B1（Event 共享鎖）、B2（單一連線）各量一次，以 `LT_MEASURE_TAG` 隔離輸出，比較分段耗時（PreTransaction、BeginTransaction、EventLockWait、InLock、Commit、Total）。B2 量測若比 B1 慢，MUST 記錄並回退 B2。報告 MUST 標示 B1、B2 各自「採用／未採用」及判定依據，並記錄切換排隊模式延遲探測的結果與 3 秒門檻判定。
- 依 `Release 組態對照與重複執行` 重跑的正式驗收結果，資料表規則與 `P95 優化結果報告` 相同（置於 `<!-- report-tables:begin -->` 與 `<!-- report-tables:end -->` 之間）。
- §5 P95 目標的最終判定（以 Release 為準）；未達成時 MUST 以量測證據說明剩餘延遲來源，並提供 `<!-- unmet-reason:<scenario> -->` 區段，MUST NOT 調整門檻或 P95 口徑（201 與 409 合併計算）。

#### Scenario: LT-REPORT-007 第二階段報告與重跑結果一致
- **WHEN** 檢視第二階段報告的正式驗收資料表
- **THEN** 以 `check-report-tables.sh` 比對報告標記區段與彙整腳本對重跑產生的 12 份 summary JSON 的輸出，無差異；且兩份第一階段報告（`docs/load-test/report.md`、`docs/load-test/p95-optimization-report.md`）的未修改證明 MUST 符合以下基準檔契約：
  - 正式驗收開始前建立 `loadtest/.output/p95-phase2-baseline.txt`，內含 `MASTER_SHA`、`BASELINE`、`START_HEAD` 三欄，皆為存在的 commit
  - `BASELINE` 為當時固定的 `MASTER_SHA` 與 `START_HEAD` 的 merge-base
  - 判定時只讀取該基準檔，MUST NOT 重新解析 `master`；檔案不存在、缺欄位或任一 SHA 無效即判定失敗
  - 驗收輸出列出的三個 SHA 與基準檔一致，並另列判定當下的 HEAD
  - `BASELINE` 到判定當下 HEAD 之間沒有任何 commit 觸及兩份報告（含改後又復原）
  - 工作樹與 index 也沒有修改兩份報告

#### Scenario: LT-REPORT-008 第二階段未達標時說明原因
- **WHEN** 重跑後 Release 任一 scenario 有任一次執行的 P95 ≥ 500ms（與 LT-REPEAT 的單次判定相同）
- **THEN** 報告判定未達成，`check-report-tables.sh` 對每個未達標 scenario 檢查到 `<!-- unmet-reason:<scenario> -->` 區段，且區段引用至少一個 `measure-` 開頭的量測檔名與一個毫秒數值；門檻與口徑不變
