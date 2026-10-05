## Why

docs/project-scope.md §5 宣稱「500 名併發買家搶單一活動 50 張票，0% 超賣，下單 P95 < 500ms」，但目前沒有任何可重現的壓測證據；§8 ② 已排定以 k6 在 compose 環境內驗證這個目標，並產出腳本與結果報告，作為 Phase 3 收尾與後續是否上雲的判斷依據。

## What Changes

- 新增獨立 console 工具 `tools/ProjectC.LoadTest.Seeder`（不屬於產品程式碼、不加入產品執行路徑）：
  - 建立 500 個壓測買家會員與 1 個壓測 Admin（擁有一個 Approved Organizer）——因註冊／登入皆需驗證碼，k6 無法走真實登入。
  - 重用 Infrastructure 既有的 `BCryptPasswordHasher`、`JwtTokenService`、`DevelopmentDataSeeder`，在 process 內簽發 access token 寫入 gitignore 的 token 檔供 k6 讀取。
- 新增 k6 腳本（`loadtest/`），兩個 scenario：
  - **數量票**：單一數量票種 `AvailableQuantity = 50`，500 VU 各下 1 張。
  - **座位票**：單一座位票種對應 50 席的分區，500 VU 各隨機選 1 席。
  - 測試活動由 k6 `setup()` 以壓測 Admin token 透過真實 admin API 建立（場館 → 座位圖 → 活動 → 票種），整體規模依 §3 基準（2000 席 / 20 票種），非排隊模式、販售中。
  - 只量測 `POST /api/orders` 的 P95；以 k6 threshold 判定 P95 < 500ms。
  - 超賣驗證雙重核對：k6 端統計成功（201）筆數，以及跑完後以 admin `sales-report` API 核對售出數：數量票皆 MUST 恰好等於 50；座位票 MUST 介於 1–50，且不得有任何一席被兩筆成功訂單持有。
- docker compose 新增 `loadtest` profile 的 k6 服務（預設 `docker compose up` 不啟動），連線寫死為 `http://api:8080`。
- 新增 `docker-compose.loadtest-release.yml`：暫時把 api 改以 Release 組態執行（切換本身不改產品程式碼與 `docker-compose.yml`，只疊加這個檔案）。每個 scenario 在 Debug（既有 `dotnet watch`）與 Release 兩種組態各執行 3 次；§5 目標以 Release 3 次皆通過為準，Debug 數據列為對照。
- 新增壓測結果報告 `docs/load-test/report.md`（含執行環境、指令、兩個 scenario 的結果與判讀）。
- **不改產品程式碼**（`src/` 下任何專案不變更）；排隊模式（入隊需驗證碼）不在本次範圍。

## Capabilities

### New Capabilities

- `load-testing`: 壓測資料準備（seeder 工具與 token 檔）、k6 scenario 定義與通過門檻、超賣驗證方式、compose 執行方式與結果報告要求。

### Modified Capabilities

（無——產品行為不變）

## Impact

- 新增：`tools/ProjectC.LoadTest.Seeder/`（reference `ProjectC.Infrastructure`）、`loadtest/`（k6 腳本）、`docs/load-test/report.md`、對應的 seeder 單元／整合測試專案。
- 新增：`docker-compose.loadtest-release.yml`（只覆寫 api 的 entrypoint）。
- 修改：`docker-compose.yml`（k6 服務於 `loadtest` profile；seeder bin/obj named volume）、`.gitignore`（token 檔輸出目錄）、`ProjectC.slnx`（加入 seeder 與其測試專案）。
- 相依：k6 官方 image（固定版本 tag）。
- 資料影響：在開發 DB 留下壓測會員、壓測 Organizer 與每次執行建立的測試活動；seeder 為冪等（重跑不重複建立會員）。
- 安全：只支援本機 compose，目標一律寫死。
  - seeder 只允許在 Development 環境、且連線字串 Host 為 compose 的 `db` 時執行；`--buyers` 限 1–1000；token 檔路徑寫死，不提供 `--output`。
  - k6 的 API 位址寫死為 `http://api:8080`，不可用 `-e` 覆寫。
  - token 檔含有效 JWT（30 分鐘失效），MUST 不進版控。
  - 逐條安全確認見 design.md「安全確認」。
- 顆粒度：1 個 capability、34 項 task，略超過 ~30 的評估門檻。評估後不拆分：Release 對照、重複執行與報告彙整腳本都是同一批 k6 腳本的執行與判讀方式，拆成獨立 change 沒有可單獨交付的價值。
