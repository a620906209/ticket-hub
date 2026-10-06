## ADDED Requirements

### Requirement: 下單分段耗時量測
系統 SHALL 在建立訂單流程中，以單調時鐘（`Stopwatch`）量測各分段耗時，並在每次建立訂單結束時（含成功、業務失敗與例外）輸出一筆 Debug 等級的結構化 log，欄位包含：交易前處理、開啟交易（含取得資料庫連線）、等待活動鎖、取得活動鎖後到 commit 前、commit、總耗時（毫秒），以及結果（成功或錯誤類型）與是否含座位項目。流程未到達的分段 MUST 記為空值，不得記為 0。

- 此 log MUST 預設關閉，僅在以設定覆寫該類別的最低 log 等級為 Debug 時輸出；關閉時 MUST NOT 組裝 log 參數。
- 分段耗時 MUST NOT 以牆上時鐘時間戳相減計算。
- log 欄位僅限上述分段耗時、結果與是否含座位項目，MUST NOT 包含買家 Id 或任何個資（量測只需要分布，不需要追溯個別買家）。

#### Scenario: LT-MEASURE-001 開啟 Debug 時輸出分段耗時
- **WHEN** 該類別最低 log 等級為 Debug，買家成功建立一筆訂單
- **THEN** 系統輸出一筆 Debug log，所有分段欄位皆有值，結果為成功

#### Scenario: LT-MEASURE-002 交易前即被拒絕時未到達的分段為空值
- **WHEN** 該類別最低 log 等級為 Debug，建立訂單請求在開啟交易前就被拒絕（含交易前驗證錯誤與提早回 409）
- **THEN** 系統輸出一筆 Debug log，交易前處理與總耗時有值，開啟交易、等待活動鎖、鎖內處理、commit 為空值，結果為對應的錯誤類型（提早回 409 時為 `Conflict`），且未開啟資料庫交易

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
