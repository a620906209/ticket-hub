# Ticket-Hub 搶票壓測報告（k6）

> OpenSpec change：`k6-load-test`。執行日期：2026-10-05。重現步驟見 [`loadtest/README.md`](../../loadtest/README.md)。

## 結論

| §5 目標 | 結果 |
|---|---|
| 500 併發搶 50 張 | ✅ 12 次執行都有 500 個 VU 送出，k6 量到的送出時距 max−min ≤ 118ms；伺服器端 Seq 日誌交叉核對見下方 |
| 0% 超賣 | ✅ 12 次都是成功數 = `QuantitySold` = 50，沒有座位被重複售出，`oversell_check_failed` 皆為 0 |
| P95 < 500ms | ❌ **未達成**。以 Release 為準：數量票 P95 中位數 802.33ms，座位票 4834.73ms |

依 spec「3 次 `runVerdict.passed` 都為 true 才通過」，4 組（Release／Debug × 數量票／座位票）全部**未通過**，12 次的失敗原因都只有 `http_req_duration{name:place-order}: p(95)<500`，沒有其他門檻失敗。

依 5.7 的規則，**沒有**調整門檻或產品程式碼。P95 的瓶頸分析與優化不在此 change 範圍內（見文末「後續」）。

## 執行環境

- 主機：Windows 11 + Docker Desktop 29.8.1（Compose 5.5.1），WSL2 kernel `6.18.40.1-microsoft-standard-WSL2`，Docker 可用 20 CPU、約 15.5 GiB 記憶體。
- 映像檔：`postgres:16-alpine`、`redis:7-alpine`、`datalust/seq:2026.1.17114`、`grafana/k6:2.3.0`；api 為既有開發用 SDK image。
- 程式版本：分支 `feature/k6-load-test`，基準 commit `445d246`，加上本 change 尚未 commit 的壓測檔案。`src/` 沒有任何變更。
- k6、api、PostgreSQL、Redis、Seq 都在同一台主機上，共用 CPU 與磁碟。
- 每次執行前都重新執行 seeder，並以 api 容器 PID 1 的指令列確認組態：

| 批次 | 時間（UTC） | api PID 1 指令列 |
|---|---|---|
| Debug | 10:28–10:43 | `dotnet watch --project src/ProjectC.WebApi/ProjectC.WebApi.csproj run --no-launch-profile --urls http://0.0.0.0:8080` |
| Release | 11:59–12:26 | `dotnet run --project src/ProjectC.WebApi/ProjectC.WebApi.csproj -c Release --no-launch-profile --urls http://0.0.0.0:8080` |

Release 仍搭配 `ASPNETCORE_ENVIRONMENT=Development`，屬於「Release 組態 + Development 環境」，不是完整的正式環境設定。

## 資料表

以下由 `aggregate.js` 從 12 份 summary JSON 產生，原樣貼上未手改；原始檔與 `report-tables.md` 放在本目錄。

### Release × 數量票（count-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 1182.84 | 1211.40 | 50 | 450 | 0 | 50 | 92.00 | 未通過 |
| 2 | 802.33 | 815.69 | 50 | 450 | 0 | 50 | 100.00 | 未通過 |
| 3 | 779.74 | 792.89 | 50 | 450 | 0 | 50 | 74.00 | 未通過 |

- P95 (ms)（3 次有值）：中位數 802.33／最小值 779.74／最大值 1182.84
- P99 (ms)（3 次有值）：中位數 815.69／最小值 792.89／最大值 1211.40
- 判定：未通過
  - 第 1 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 2 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 3 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500

### Release × 座位票（seat-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 5403.99 | 5628.98 | 50 | 450 | 0 | 50 | 81.00 | 未通過 |
| 2 | 4834.73 | 5019.62 | 50 | 450 | 0 | 50 | 90.00 | 未通過 |
| 3 | 4818.04 | 5088.10 | 50 | 450 | 0 | 50 | 93.00 | 未通過 |

- P95 (ms)（3 次有值）：中位數 4834.73／最小值 4818.04／最大值 5403.99
- P99 (ms)（3 次有值）：中位數 5088.10／最小值 5019.62／最大值 5628.98
- 判定：未通過
  - 第 1 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 2 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 3 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500

### Debug × 數量票（count-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 604.85 | 617.30 | 50 | 450 | 0 | 50 | 102.00 | 未通過 |
| 2 | 611.44 | 626.33 | 50 | 450 | 0 | 50 | 81.00 | 未通過 |
| 3 | 627.89 | 642.47 | 50 | 450 | 0 | 50 | 107.00 | 未通過 |

- P95 (ms)（3 次有值）：中位數 611.44／最小值 604.85／最大值 627.89
- P99 (ms)（3 次有值）：中位數 626.33／最小值 617.30／最大值 642.47
- 判定：未通過
  - 第 1 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 2 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 3 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500

### Debug × 座位票（seat-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 4996.08 | 5221.53 | 50 | 450 | 0 | 50 | 113.00 | 未通過 |
| 2 | 5019.12 | 5199.94 | 50 | 450 | 0 | 50 | 80.00 | 未通過 |
| 3 | 4983.35 | 5161.49 | 50 | 450 | 0 | 50 | 118.00 | 未通過 |

- P95 (ms)（3 次有值）：中位數 4996.08／最小值 4983.35／最大值 5019.12
- P99 (ms)（3 次有值）：中位數 5199.94／最小值 5161.49／最大值 5221.53
- 判定：未通過
  - 第 1 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 2 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 3 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500

## 伺服器端交叉核對（與 k6 腳本無關的資料來源）

- **Seq 請求日誌**（Serilog `RequestLoggingMiddleware`，依各次執行的時間窗查 `POST /api/orders` 與 `/confirm`）：12 次都是 201 × 50、409 × 450、其他狀態 0，confirm 2xx × 50。與 k6 的成功數、409 數完全一致。
- **伺服器端 P95**（`Elapsed`，api 內 Stopwatch 計時）與 k6 P95 相近：數量票 596.7–1042.5ms，座位票 4830.8–5368.6ms。都超過 500ms，P95 未達標不是 k6 端量測造成的。
- **伺服器收到請求的時間分布**（日誌時間戳減 `Elapsed`）：11 次在 37–206ms 內，Release 座位票第 3 次為 881ms（中間有一段 640ms 空隙，可能是時鐘倒退落在這段時間窗）。12 次都在 1000ms 內。
- **資料庫唯讀查詢**（`docker compose exec db psql`，只用 SELECT）：12 個壓測活動各有 50 筆訂單（全部同一狀態）、50 筆 OrderItem；座位票 50 席售出、`EventSeatId` 無重複；數量票剩餘數量為 0。
- 未解釋的差異：Release 座位票第 3 次的伺服器端 P95（5280.3ms）**高於** k6 P95（4818.0ms），其餘 11 次都是伺服器端略低。正常情況下伺服器端計時應該包含在客戶端計時內。可能原因是執行緒集區飢餓，讓 log 計時的延續工作晚於回應送出，但**未驗證**。這不影響「未通過」的結論。

## exit code 與 runVerdict 核對

12 次的 exit code 都是 99（k6 threshold 失敗），`runVerdict.passed` 都是 false，兩者一致。沒有「exit code 非 0 但 `runVerdict.passed` 為 true」的情況，判定邏輯沒有發現缺陷。

## 未通過的歸因（design 決策 7 的順序）

1. **產品問題**：12 次的 `oversell_check_failed`、`place_order_5xx`、`place_order_unexpected`、`confirm_failed` 都是 0，`QuantitySold` 都等於 `orders_created`（50）→ 沒有。
2. **壓測環境未達預期併發**：12 次的 `buyer_iterations_completed` 都是 500，`place_order_send_offset_ms` 的最大值減最小值都 ≤ 118ms（門檻 1000ms）→ 沒有。
3. **座位票成功數 < 50**：6 次座位票的成功數都是 50 → 不適用。

所以 12 次未通過的原因只有 P95 超過 500ms，判讀為**產品在本機環境下的回應時間未達目標**，不是壓測環境或判定邏輯的問題。

### 送出時距以「最大值減最小值」判定

Debug 數量票第 3 次、Debug 座位票第 2 次、Release 座位票第 3 次，每個 VU 量到的送出時距都是負的（最大值 -620／-1503／-1496ms），但最大值減最小值只有 80–107ms。

原因：k6 腳本只能用牆上時鐘。`Date.now()` 與 `exec.instance.currentTestRunDuration` 都是牆上時鐘，k6 沒有 `performance.now()`。2026-10-05 實測，本機 WSL2 時鐘約每 29 秒倒退約 1.53 秒。VU 等待期間若時鐘倒退，所有 VU 的時距會一起平移，但彼此的差距不變。

因此 design 決策 7 的「併發佐證」改為判定最大值減最小值 ≤ 1000ms（`evaluateSendSpread`，同時寫進 `runVerdict` 與彙整）。原本的「最大值 ≤ 1000ms」規則會被時鐘倒退影響，所以不採用。12 份 summary 都含 min／max，彙整時重新計算，不需要重跑。重算後判定與原本相同。

## Debug／Release 差異

| 組 | P95 中位數（ms） | P99 中位數（ms） |
|---|---|---|
| Release × 數量票 | 802.33 | 815.69 |
| Debug × 數量票 | 611.44 | 626.33 |
| Release × 座位票 | 4834.73 | 5088.10 |
| Debug × 座位票 | 4996.08 | 5199.94 |

- 座位票兩種組態都在約 5 秒，差異不大。
- 數量票的 Release 反而比 Debug 慢。Release 第 1 次（1182.84ms）是 api 重啟後的第一批請求，冷啟動可能有影響，但第 2、3 次（約 780–800ms）仍然比 Debug 慢。兩批相隔約 1.5 小時，主機上其他負載也可能不同。

兩組都顯示 Release 的編譯最佳化沒有改善回應時間。瓶頸**可能**不在 CPU／JIT，而在資料庫連線或鎖等待（見下方觀察），但**未經驗證**。

## 故障注入驗證

確認門檻真的能讓壓測失敗（每項都帶 `LT_API_BUILD=debug`，執行前重跑 seeder）：

| 故障 | 結果 |
|---|---|
| `p95`（數量票） | exit 99；`http_req_duration{name:place-order}` 的 `p(95)<1` 為 `ok=false` |
| `quantity51`（數量票） | exit 99；成功數 51；`orders_created: count<=50` 與 `oversell_check_failed: count==0` 為 `ok=false`；verify 原因含 `QuantitySold > 50 (QuantitySold=51)` |
| `bad-buyer-token`（座位票） | exit 99；api 日誌 500 次 `POST /api/orders` 都回 401；`place_order_unexpected: count==0` 與 `orders_created: count>=1` 為 `ok=false` |
| `bad-admin-token` | exit 107；setup 在建立場館時收到 401 而中止，沒有下單 |
| `setup-timeout` | exit 100；setup 逾時中止，沒有下單 |

另外確認了以下設定錯誤都會讓 setup 中止（exit 107）、沒有下單：`LT_API_BUILD` 未設定或為 `prod`、`LT_RUN` 未設定或為 `4`、只有 499 個買家 token、目標 summary 檔已存在（事後確認既有檔案未被覆寫）。

## 執行過程中的偏差與事故

- **第一次 Release 批次無效，已整批重跑。** k6 服務有 `depends_on: api`，第一次 Release 批次執行 k6 時沒有帶 `--no-deps`，Compose 依預設 compose 檔把 api 重建回 `dotnet watch`。結果這 6 次標為 release 的執行，實際上打在 Debug 上，第 1 次還遇到 api 重啟而 setup 中止。這是組態標籤錯誤，不是以 token 過期等理由排除失敗結果。原始檔保留在 `loadtest/.output/invalid-release-attempt/`（不進版控），不列入資料表。修正方式：k6 一律以 `run --rm --no-deps` 執行，並改成每一次執行前都檢查 PID 1。
- **`setup-timeout` 故障注入由 1s 改為 1ms。** 實測 API 熱機後，setup 能在 1 秒內完成，原本的 1s 不會觸發逾時。
- **seeder 遇到 PostgreSQL `too many clients`。** 每次壓測後約 2 分鐘內，seeder 會因 PostgreSQL 連線數達上限（`max_connections` 為 100）而失敗。流程是每 30 秒重試一次，最多 5 次，成功後才執行 k6，不沿用舊的 token 檔。

## 觀察（未驗證，供後續調查）

- 壓測後 PostgreSQL 連線在約 2 分鐘內維持在上限，推測壓測期間 api 的 Npgsql 連線池已撐到上限，請求在等待連線。這可能是 P95 偏高的原因之一，尤其座位票還要取得座位的悲觀鎖。
- 12 次的 5xx 都是 0，表示連線池上限造成的是排隊等待，而不是失敗。

## 判讀與限制

- 所有服務與 k6 都在同一台本機上，k6 本身也會占用 CPU，數字只能代表本機開發環境，不能直接推論正式環境。
- Release 仍是 Development 環境（例如 Serilog 寫 Seq、通知以 log 代替寄信），並非正式環境設定。
- WSL2 時鐘會倒退，影響送出時距的絕對值，所以改以 max−min 判定（見上方說明）。k6 的 HTTP 計時與伺服器端 `Elapsed` 都用單調時鐘，不受影響。
- 每組只跑 3 次，樣本數小。不過同組內 P95 的差距不大（Release 數量票第 1 次除外），結論（P95 未達標）不受波動影響。

## 後續

- P95 未達標需要另開 change：分析連線池、鎖等待與資料庫查詢，再決定是否優化產品程式碼或調整目標。此 change 不處理。
