## MODIFIED Requirements

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


## ADDED Requirements

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
