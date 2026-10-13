> 所有指令都在容器內執行（例如 `docker compose exec web npm run test`），不在本機執行 node／npm。
>
> **AC 測試對應例外**：BW-FLOW-001、BW-SEAT-A11Y-002 的「實際樣式」部分、BW-SEAT-A11Y-007，以及「活動詳情頁在窄螢幕不產生頁面橫向捲動」既有 Scenario（BW-RESP-001～005）的重新驗證，改用瀏覽器量測取代 CLAUDE.md 要求的單元或整合測試。例外紀錄見 design.md「AC 測試對應例外」。核准狀態：**已核准**（2026-10-10）。
>
> | ID | Scenario | 驗證方式 | Task |
> | --- | --- | --- | --- |
> | BW-FLOW-001 | 座位很多時結帳列固定在視窗底部 | 瀏覽器量測（斷言 1） | 1.2（基準值）、4.2 |
> | BW-FLOW-002 | 結帳列列出已選座位，超過 6 個時截斷 | Vitest | 2.2 |
> | BW-FLOW-003 | 選位、區域隨選與送出的錯誤顯示在結帳列 | Vitest | 2.3 |
> | BW-FLOW-004 | 頁面層級錯誤維持顯示在頁面頂端 | Vitest | 2.4 |
> | BW-FLOW-005 | 摘要以 polite live region 公告 | Vitest | 2.2 |
> | BW-FLOW-006 | 排隊畫面不顯示結帳列 | Vitest | 2.4 |
> | BW-FLOW-007 | 重新操作時清除兩處的舊錯誤 | Vitest | 2.3 |
> | BW-SEAT-A11Y-001 | 三種狀態的圖例 | Vitest | 2.5 |
> | BW-SEAT-A11Y-002 | 不可選座位以刪除線與虛線區分 | Vitest（class 套用）＋瀏覽器量測（斷言 3，實際樣式） | 2.5、1.2、4.2 |
> | BW-SEAT-A11Y-003 | 座位中文名稱與選取狀態 | Vitest | 2.6 |
> | BW-SEAT-A11Y-004 | `aria-disabled`，未登入點選仍導向登入頁 | Vitest | 2.6 |
> | BW-SEAT-A11Y-005 | 每個分區一個 Tab 停留點 | Vitest | 2.7 |
> | BW-SEAT-A11Y-006 | 方向鍵移動焦點 | Vitest | 2.7 |
> | BW-SEAT-A11Y-007 | 焦點座位不被結帳列遮住 | 瀏覽器量測（斷言 2） | 1.2（基準值）、4.2 |
> | BW-QUICK-001～008 | 區域隨選只填入座位 | Vitest | 2.1 |
> | BW-RESP-001 | 390px 寬未登入瀏覽活動詳情頁（既有 Scenario，結帳列結構改變後重新驗證） | 瀏覽器量測（斷言 4，未登入輪） | 4.2 |
> | BW-RESP-002 | 320px 寬未登入瀏覽活動詳情頁（同上） | 瀏覽器量測（斷言 4，未登入輪） | 4.2 |
> | BW-RESP-003 | 已登入買家在窄螢幕瀏覽含計數票種的活動詳情頁（同上） | 瀏覽器量測（斷言 4，已登入輪） | 4.2 |
> | BW-RESP-004 | 桌面與平板寬度維持雙欄版面（同上） | 瀏覽器量測（斷言 4，已登入輪） | 4.2 |
> | BW-RESP-005 | 斷點上限寬度維持單欄且不橫向捲動（同上） | 瀏覽器量測（斷言 4，未登入輪） | 4.2 |
> | BW-SALES-010／011 的快速選位路徑 | 規則不變，操作路徑改為「自動選位 → 送出訂單」 | Vitest（改寫） | 2.1 |
> | 其餘既有 Scenario（BW-ORDER-*、BW-QUEUE-*、BW-SALES-*、BW-RN-*） | 既有行為不變 | 既有 Vitest，只能改查找方式（例如按鈕文字），不得改變斷言語意 | 3.6 |
>
> 測試名稱沿用 `EventDetailPage.test.ts` 既有的 `ID：情境描述` 格式（例如 `BW-QUEUE-001：…`），不採 CLAUDE.md 的 `MethodName_Scenario_ExpectedResult`。原因：前端元件測試沒有對應的單一方法，同一個檔案混用兩種格式會降低一致性（Rule 11）。

## 1. 量測腳本與基準值

- [x] 1.0 撰寫量測資料 fixture 腳本 `loadtest/fixtures/purchase-flow-ux-event.js`（design.md 決策 6「量測資料」）。k6 腳本，1 VU、1 iteration，重用 `loadtest/lib/` 的 `http.js`、`tokens.js` 與 `admin-api.js` 的 `getOk`／`assertEventOnSale`／`getSeats`／`getTicketTypes`，不修改 lib 與既有壓測腳本；`loadtest/README.md` 的腳本表格新增一列，並在「清理」段落補上本 fixture 的說明。內容包括：會留下哪些資料（場館、座位圖、活動、票種、2 筆訂單，以及量測時註冊的 `pfux-*` 帳號）、不得與壓測並行（seeder 會覆寫共用的 `tokens.json`）、保留上限與達到上限時的清理方式（見下方「保留上限」）。步驟 1 到 3 須連續執行：token 有效期 30 分鐘，過期時 fixture 會因 401 失敗。
  - **僅限 Development**：沿用既有的兩道保護。seeder 只在 `ASPNETCORE_ENVIRONMENT=Development`，且 DB Host 為 `db` 時執行；沒有 token 檔，fixture 就無法建立資料。k6 的 API 位址寫死為 compose 內部的 `http://api:8080`，沒有 `__ENV` 可以改到其他目標。fixture 不新增其他環境入口。
  - **保留上限**：建立任何資料前，先以 Admin token 呼叫 `GET /api/admin/events`（依 seeder Admin 的主辦方列出，不經過快取），計算標題以 `[PurchaseFlowUx] ` 開頭的活動數量。數量 `>= 6` 時立即 `fail()`，不建立任何資料，並輸出 `PURCHASE_FLOW_UX_RETENTION_LIMIT_REACHED count=<n>`。
    - 6 的理由：一次完整流程最多 2 輪（1.2 與 4.2），上限 6 可容納 2 次因前置失敗而重跑。
    - 失敗的執行同樣計入上限：fixture 中途 `fail()` 時，已建立的場館或活動不回滾，也不自動重試。
    - 達到上限時停止並回報使用者，不自動清理。產品沒有刪除 API，自動清理只能用原生 SQL，違反 CLAUDE.md 的禁令，而且要處理訂單、票券、座位、會員之間的關聯，誤刪風險高。清理由使用者依 `loadtest/README.md`「清理」段落的既有做法處理：用 pgAdmin 依 `[PurchaseFlowUx] ` 前綴與 `pfux-` Email 前綴辨識後手動處理，或重建 dev DB volume。清理後重跑 fixture，上限檢查會重新計數。
    - `pfux-*` 帳號沒有 API 可以計數。每輪只註冊 1 個，數量受活動上限間接限制，不另設上限。
  - **並行保護**（2026-10-13 補充：交叉審查發現先計數再建立不是原子操作。兩個 fixture 同時讀到 5 筆，都會通過檢查，最後變成 7 筆）：
    - **禁止並行**：fixture 之間不得並行，也不得與壓測並行。
    - **入口**：只能透過 wrapper `loadtest/fixtures/run-purchase-flow-ux-event.sh` 執行。wrapper 先在 compose 的 `redis` 上執行 `SET loadtest:purchase-flow-ux:fixture-lock <隨機 token> NX EX 300` 取得互斥鎖。
      - 取鎖指令本身失敗（redis 無法使用）時，輸出 `PURCHASE_FLOW_UX_FIXTURE_LOCK_FAILED`，以 3 結束，不執行 k6。
      - 鎖已被占用時，輸出 `PURCHASE_FLOW_UX_FIXTURE_LOCK_BUSY`，以 3 結束，不執行 k6。
      - 無法產生鎖 token 時，輸出 `PURCHASE_FLOW_UX_FIXTURE_LOCK_FAILED`，以 3 結束。
      - 所有主機與容器都連同一個 compose redis，所以這把鎖在不同執行環境之間一定共用。
    - **釋放鎖**：
      - k6 容器以 `--name pfux-fixture-<token>` 啟動。wrapper 結束時（k6 成功、失敗，或 Ctrl-C／SIGTERM 中斷）先以名稱確認容器已不存在；仍存在就 `docker rm -f`，再確認一次。確認容器已停止後，才用 `EVAL` 比對 token，只刪除自己的鎖。
      - 無法確認容器已停止時（`docker ps` 失敗或 `rm -f` 後仍存在），輸出 `PURCHASE_FLOW_UX_FIXTURE_LOCK_KEPT`，不釋放鎖，交給 TTL；寧可擋住下一次執行，也不讓兩個 fixture 同時計數。
      - Ctrl-C（SIGINT 送給整個 process group）：compose 用戶端結束，wrapper 以 130 結束。此時 daemon 可能仍在建立容器、第一次查詢看不到，所以中斷時等 2 秒再查一次，仍存在就移除，確認後才釋放鎖。
      - 只對 wrapper 送 SIGTERM 或 SIGHUP（例如關閉終端機）：bash 會等前景的 `docker compose run` 結束才處理訊號，所以不會中止 k6；k6 跑完後才以 143（SIGTERM）或 129（SIGHUP）結束，比照中斷重查容器後釋放鎖，期間鎖一直持有。
      - 在沒有 job control 的 shell 背景（`&`）啟動 wrapper 時，bash 會忽略 SIGINT，Ctrl-C 無效；要中止請改送 SIGTERM，或在前景執行。
      - 殘餘風險（未處理）：容器被移除前已送到 API 的請求，仍可能在鎖釋放後才在伺服器端完成並建立資料。機率極低，而且保留上限是軟限制；中斷後重跑前仍須檢查 `[PurchaseFlowUx]` 活動數。
      - wrapper 被 SIGKILL 等無法攔截的方式終止時，鎖由 300 秒 TTL 自動釋放。300 秒大於 k6 的 `setupTimeout` 120 秒加上容器啟動時間；但此時不會移除 k6 容器，重跑前須先確認沒有殘留的 `pfux-fixture-*` 容器，並檢查 `[PurchaseFlowUx]` 活動數。
    - **鎖在執行期間遺失或無法確認**：k6 結束後，wrapper 讀取鎖的值，訊息都附上 k6 結束碼，以 4 結束；即使 k6 成功，也不得視為通過。
      - 讀取失敗（redis 無法使用）：輸出 `PURCHASE_FLOW_UX_FIXTURE_LOCK_CHECK_FAILED`。
      - 值不是自己的 token（例如已過期）：輸出 `PURCHASE_FLOW_UX_FIXTURE_LOCK_LOST`。
    - **防止繞過**：k6 無法連 Redis，所以由 wrapper 以 `-e PFUX_FIXTURE_LOCK_TOKEN` 把 token 傳給 fixture。fixture 在 `setup()` 最前面檢查 token 是否為 32 個小寫十六進位字元，不符就 `fail()`，不建立任何資料。這只能擋住「誤用而直接執行 k6」，fixture 無法確認鎖本身，擋不住刻意偽造 token。
    - **自動化測試**：`bash loadtest/fixtures/run-purchase-flow-ux-event.test.sh`。以 PATH 上的假 docker 模擬 redis 與 k6 容器，不碰真實 redis、API 與保留額度，共 12 個案例，涵蓋鎖忙碌、釋放鎖失敗時輸出 `LOCK_RELEASE_FAILED` 並保留 k6 結束碼、redis 無法使用（取鎖時與 k6 結束後）、成功與失敗都釋放鎖、鎖遺失、中斷時先移除容器再釋放鎖、中斷時容器晚出現會重查並移除、容器移除失敗時保留鎖、只送 SIGTERM 或 SIGHUP 時 k6 跑完前一直持有鎖（SIGHUP 另驗證有重查容器）。假 k6 被中斷時會 kill 自己的背景 sleep，不留孤兒程序；假 k6 在 10 秒內沒啟動時直接記為失敗。假 docker 的 `ps`／`rm` 只認 `run --name` 登記的名稱，名稱不一致會讓測試失敗。必須全部 PASS（exit code 0）。修改 wrapper 後必須重跑。
    - **安全**：鎖 token 是隨機值，不是憑證，可以出現在命令列；這個環境變數也不影響 API 位址。
  - **輸出限制**：fixture 的 console 與 summary 只輸出 Id、數量與 HTTP 狀態碼；失敗時允許沿用 `loadtest/lib/http.js` 的 `describeFailure`（method、path、狀態碼與截斷至 300 字元的回應本文，不含 header）。不得輸出 token、`Authorization` header、request 本文或未截斷的回應本文。
  - **執行指令**（每次執行 1.2、4.2 前各跑一次）：
    1. `docker compose exec api dotnet run --project tools/ProjectC.LoadTest.Seeder -- --buyers 2`，必須 exit code 0；
    2. 依 `loadtest/README.md`「每一次執行的固定順序」第 2 步輪詢 api 就緒；
    3. `bash loadtest/fixtures/run-purchase-flow-ux-event.sh`：wrapper 先取鎖，再執行 `docker compose --profile loadtest run --rm --no-deps --name pfux-fixture-<token> k6 run`（見上方「並行保護」）。不得直接執行 k6。
  - **建立內容**：場館、座位圖（只有 `A` 區，400 席）、活動（標題 `[PurchaseFlowUx] <UTC 時間>`、`salesStartAtUtc` 為 1 分鐘前、`startAtUtc` 為 24 小時後、不送 `salesEndAtUtc` 與 `isRealNameRequired`）、票種（`A` 座位制；`GA` 計數制，`availableQuantity` 100）。第 1 位買家以 `POST /api/orders` 下單 `A` 區第 1 席並 `POST /api/orders/{id}/confirm`；第 2 位買家下單第 2 席，不付款。
  - **核對**（任一不符就 `fail()`，exit code 非 0）：每個建立與下單請求都回 2xx；`assertEventOnSale` 通過；公開座位 API 的 `A` 區共 400 席，第 1 席為 `Sold`，至少一席為 `Available`；公開票種 API 同時有 `requiresSeat` 為 true 與 false 的票種。第 2 席是否仍為 `Held` 不列入核對（持有期限到期會釋出）。
  - **輸出**：成功時 console 印出 `PURCHASE_FLOW_UX_EVENT_ID=<id>`，作為 1.1 的 `EVENT_ID`。
  - **驗證**：在 dev 環境執行一次，exit code 為 0，並以公開 `GET /api/events/{id}/seats` 手動確認第 1 席為 `Sold`；再將腳本中的座位數暫時改為 399 執行一次，必須因核對失敗而以非 0 結束（證明核對會失敗），確認後改回。上限檢查也要驗證：把腳本中的上限暫時改為「目前 `[PurchaseFlowUx]` 活動數」執行一次，必須輸出 `PURCHASE_FLOW_UX_RETENTION_LIMIT_REACHED` 並以非 0 結束，而且執行前後 `[PurchaseFlowUx]` 活動數相同（證明未建立資料），確認後改回。
  - **驗證紀錄（2026-10-12）**：
    - 實作偏差：建立與核對放在 k6 `setup()`，`default` 為空函式。原因：k6 在 `default` 內 `fail()` 只中止該次 iteration，exit code 仍為 0（第一次以 `default` 執行時確認），放在 `setup()` 才會以非 0（107）結束。
    - 成功路徑：exit code 0，`PURCHASE_FLOW_UX_EVENT_ID=f74ca0d4-15b3-4bd9-b0a4-e3562f2e6461`；公開 `GET /api/events/{id}/seats` 共 400 席，第 1 席 `Sold`、第 2 席 `Held`、398 席 `Available`。
    - 核對會失敗：座位數暫改 399，輸出 `fixture failed: zone A has 399 seats, expected 400`，exit code 107；已改回。
    - 上限會阻擋：上限暫改 2（當時 `[PurchaseFlowUx]` 活動數為 2），連續執行兩次都輸出 `PURCHASE_FLOW_UX_RETENTION_LIMIT_REACHED count=2`、exit code 107，第二次的計數仍為 2（未建立資料）；已改回 6。
  - **並行保護驗證紀錄（2026-10-13）**：
    - 驗證時 `[PurchaseFlowUx]` 活動有 5 筆。T3、T4 把上限暫時改成 5，讓取到鎖的那一次在上限檢查就失敗，不會建立資料；驗證完已改回 6。
    - T1 直接執行 k6，不帶 token：輸出 `fixture failed: run via loadtest/fixtures/run-purchase-flow-ux-event.sh (cross-run lock required)`，exit code 107。
    - T2 先手動以 `SET … other-holder EX 60` 占住鎖，再執行 wrapper：
      - 輸出 `PURCHASE_FLOW_UX_FIXTURE_LOCK_BUSY`，exit code 3；
      - 結束後鎖的值仍是 `other-holder`（沒有誤刪別人的鎖），之後手動刪除。
    - T3 先重跑 seeder（`--buyers 2`）並確認 api 就緒，再同時啟動兩個 wrapper：
      - 一個輸出 `PURCHASE_FLOW_UX_FIXTURE_LOCK_BUSY`，exit code 3；
      - 另一個取得鎖，輸出 `PURCHASE_FLOW_UX_RETENTION_LIMIT_REACHED count=5`，exit code 107；
      - 結束後 `EXISTS` 為 0，證明失敗路徑也會釋放鎖。
    - T4 執行 wrapper，在 k6 執行期間手動 `DEL` 鎖：輸出 `PURCHASE_FLOW_UX_FIXTURE_LOCK_LOST`，exit code 4。
    - 各次輸出的活動數都是 5，沒有建立任何資料。
    - 未直接驗證：上限 6、成功建立資料的那條路徑沒有重跑，因為重跑會用掉最後一個保留額度。這條路徑在 fixture 端只多了一個 token 檢查（T1 已驗證會擋），wrapper 端與 T3 走同一段程式碼，只差 k6 的結束碼。
    - T1–T4 是對修正前的 wrapper 執行（strict review 後改為：中斷時先移除容器再釋放鎖、區分 redis 無法使用、token 格式檢查）。修正後的驗證：
      - 自動化測試 `run-purchase-flow-ux-event.test.sh`：8 個案例全部 PASS。突變檢查：清理時不管容器直接釋放鎖 → 2 個中斷案例 FAIL；取鎖失敗不區分 → redis 無法使用案例 FAIL；k6 結束後不檢查 redis 讀取失敗 → 4 個案例 FAIL；每次突變後都已還原。
      - 第二次 strict review 後再修正（容器名稱一致性、中斷時重查晚出現的容器、SIGTERM 行為與殘餘風險寫入文件）：自動化測試擴充為 10 個案例，全部 PASS。突變檢查：`rm -f` 名稱不一致 → 2 個中斷案例 FAIL；`ps` 名稱不一致 → 3 個中斷案例 FAIL；中斷時不重查 → 晚出現案例 FAIL；清理直接釋放鎖 → 3 個中斷案例 FAIL；每次突變後都已還原。這一輪未再真實執行 wrapper。
      - 第三次 strict review 後再修正（假 k6 留下孤兒 sleep、等待假 k6 啟動逾時不報錯、SIGHUP 未視為中斷）：wrapper 新增 `trap … HUP`（結束碼 129）；自動化測試 11 個案例全部 PASS，跑完後沒有殘留的 `/usr/bin/sleep`（拿掉 kill 時會殘留 3 個，已手動清除）。突變檢查：移除 HUP trap → SIGHUP 案例 FAIL（wrapper 直接被 SIGHUP 終止，未等 k6 結束），已還原。這一輪同樣未真實執行 wrapper。
      - 第四次 strict review（FAIL，B-001）：上一筆「跑完後沒有殘留」不成立。審查連跑 7 次，有 2 次殘留孤兒 `sleep`（2 個、1 個）。原因是假 k6 先 `touch k6-started` 才啟動 `sleep` 並取得 `sleep_pid`；測試一看到 k6-started 就送 SIGINT，trap 可能 kill 空 PID，而背景 sleep 忽略 SIGINT。修正：先啟動 sleep、取得 `sleep_pid`，再 `touch k6-started`。修正後連跑 12 次，每次 11/11 PASS，跑完後 `/usr/bin/sleep` 殘留數都是 0。
      - 第五次 strict review（PASS）的 warning：16 次中有 1 次在 2 秒後計到 1 個暫時存在的 `sleep`，推測是晚出現容器案例中假 docker 的 `(sleep 1; touch …) &` 子程序。修正：子程序完成時另寫 `late-created` 標記，測試等到標記出現才判定，並把「標記存在」列入斷言（同時證明容器確實晚出現過）。修正後連跑 12 次，每次 11/11 PASS，跑完「立即」計數（不等待）`/usr/bin/sleep` 都是 0。
      - 真實 docker：以 `--name pfux-fixture-<32 字元>` 啟動 k6 容器（entrypoint 改為 `sleep`），`docker ps -aq --filter name=^<名稱>$` 找得到，只用前綴的完全比對找不到，`docker rm -f` 後找不到。
      - 真實執行 k6，帶格式不符的 token `forged`：輸出 cross-run lock required，exit code 107。
      - 真實執行修正後的 wrapper（上限暫改 5，已改回 6；當時 seeder token 已過期，k6 都在第一個 API `GET /api/admin/events` 回 401 失敗，未建立資料）：
        - 先手動占住鎖：`LOCK_BUSY`、exit code 3，鎖的值仍是 `other-holder`；
        - 一般執行：exit code 107，結束後 `EXISTS` 為 0；
        - 看到 `pfux-fixture-*` 容器後對 wrapper 的 process group 送 SIGINT：exit code 130，結束後沒有殘留容器，`EXISTS` 為 0。這次 k6 執行時間很短，無法在真實環境重現「compose 用戶端已結束、容器仍在執行」的情境；該情境由自動化測試的假 docker 涵蓋。
      - design-hardener（2026-10-13）warning：EVAL 釋放鎖失敗時沒有任何訊息，使用者只會在下一次執行看到 `LOCK_BUSY`。修正：釋放失敗時輸出 `PURCHASE_FLOW_UX_FIXTURE_LOCK_RELEASE_FAILED`（含 TTL 秒數），結束碼不變。自動化測試新增第 12 個案例，連跑 3 次皆 12/12 PASS，`/usr/bin/sleep` 殘留 0。突變檢查：讓警告永遠不輸出 → 新案例 FAIL，已還原。這一輪未真實執行 wrapper。
    - 目前 `[PurchaseFlowUx]` 活動數：3（第一次 `default` 版成功 1、399 驗證失敗 1、上述成功路徑 1）。
- [x] 1.1 撰寫瀏覽器量測腳本 `openspec/changes/purchase-flow-ux/measure-purchase-flow.js`（design.md 決策 6），隨 change 歸檔，供日後重跑。
  - **執行方式**：依下方「登入／登出前置」建立該輪的登入狀態，再在同源分頁以 claude-in-chrome 的 `javascript_tool` 執行腳本全文。iframe 與主頁同源，共用 localStorage 的登入狀態。
  - **登入／登出前置**（1.2、4.2 每次都照做）：
    - **帳號來源**：不使用 seeder 的壓測買家。它們的密碼是隨機產生、沒有保存，seeder 也只簽 access token，而前端只靠 localStorage 的 `ticketing.refreshToken` 恢復登入，無法注入。每次執行 1.2／4.2 前，由**使用者本人**在前端 `/register` 註冊一個新帳號：Email 為 `pfux-<YYYYMMDDHHmm>@example.test`，密碼由使用者自行決定。新註冊帳號立即啟用，角色為一般會員（`Member.Register`），不會是 Admin。
    - **密碼與 CAPTCHA**：註冊與登入時的密碼、CAPTCHA 答案都由使用者直接在瀏覽器表單輸入，不告訴 Claude，不寫進腳本、任務紀錄或版控。claude-in-chrome 不得代為辨識或填寫 CAPTCHA。
    - **已登入完成判準**：使用者在 `/login` 登入後告知完成，Claude 在主頁以 `javascript_tool` 核對：`localStorage.getItem('ticketing.refreshToken') !== null`；`.buyer-header a[href="/orders"]` 存在；`.member-trigger` 的文字不是空字串；`.buyer-header a[href="/login"]` 不存在。
    - **登出步驟**：已登入一輪結束後，由 Claude 以 claude-in-chrome 在主頁點開 `.member-trigger` 選單，再點「登出」。之後重新載入主頁並核對：`localStorage.getItem('ticketing.refreshToken') === null`；`.buyer-header a[href="/login"]` 存在；`.member-trigger` 不存在。前端的登出是 best-effort：`authStore.logout()` 會吞掉後端登出 API 的錯誤（`web/src/stores/auth.ts`），所以只看 DOM 和 localStorage 無法證明伺服器端已撤銷。因此還要以 claude-in-chrome 的 `read_network_requests` 篩選 `/api/auth/logout`，確認該次點擊產生了 `POST /api/auth/logout`，而且回應狀態為 2xx。點登出前先呼叫一次 `read_network_requests`，讓追蹤涵蓋這次點擊；如果工具取不到這筆請求，同樣視為登出核對失敗。**狀態碼判定（2026-10-12 修正，經使用者核准）**：實測 `read_network_requests` 對這個 204 無內容回應兩次都誤報為 503，同一請求在 API log 與瀏覽器 Resource Timing 都是 204，web 容器 log 也沒有 proxy 錯誤。因此 `read_network_requests` 只用來證明「這次點擊確實產生了 `POST /api/auth/logout`」。狀態碼改由下面兩個來源判定，**兩者都必須是 2xx**：(a) 頁面內 `performance.getEntriesByType('resource')` 中該路徑項目的 `responseStatus`，只輸出路徑、`initiatorType` 與 `responseStatus`；(b) `docker compose logs api` 中對應時間的 `HTTP POST /api/auth/logout responded <status>`。工具回報的狀態碼仍照實記錄。只記錄方法、路徑與狀態碼，不得記錄 request 本文（內含 refresh token）與 header。不得用 `localStorage.removeItem` 取代 UI 登出。
    - **登出時機**：已登入一輪不論量測成功、斷言失敗，或 `javascript_tool` 拋出例外，結束後都必須執行上述 UI 登出與核對。登出核對失敗時（包含沒有 logout 請求或回應非 2xx），整輪量測視為失敗。
    - **敏感資訊界線**（適用 `measure-purchase-flow.js` 與所有前置核對用的 `javascript_tool` 程式碼）：
      - 對 localStorage 只允許一種存取：`localStorage.getItem('ticketing.refreshToken') !== null`，而且必須在頁面內直接算成布林值，不得把 `getItem` 的回傳值存進變數、回傳或輸出。不得列舉或序列化 `localStorage`／`sessionStorage`（例如 `Object.keys`、`JSON.stringify(localStorage)`、迴圈）。
      - 不得讀取 `document.cookie`，不得攔截或包裝 `fetch`／`XMLHttpRequest`，不得讀取任何 `Authorization` header、request payload、密碼或 CAPTCHA 欄位的值。
      - 回傳值、console 輸出與量測 JSON 只能包含布林狀態、寬度、座標、computed style 字串、座位 Id 與無效原因；登出核對另可輸出 Resource Timing 的路徑、`initiatorType` 與 `responseStatus`（不得輸出其他欄位）。
      - 例外處理：腳本以 try／catch 包住每個寬度，只回傳 `error.name` 與 `error.message`，不回傳 stack 或物件內容。因為腳本本身不讀取憑證，訊息中也不會有憑證。`javascript_tool` 本身失敗時，只在任務紀錄寫下失敗的步驟與錯誤訊息。
      - 截圖只能拍活動詳情頁與登入、登出畫面，不得開啟或拍攝 DevTools 的 Application／Network 面板。
      - **可驗證的檢查**（2026-10-12 已執行，只命中第 22 行的布林判定）：1.1 完成時執行 `grep -nE "localStorage|sessionStorage|document\.cookie|fetch|XMLHttpRequest|Authorization|password|captcha" openspec/changes/purchase-flow-ux/measure-purchase-flow.js`。結果只能出現上述那一行布林判定，出現其他行就修正後重跑。
      - **暫存副本**（2026-10-13 design-hardener warning 後補）：若改從 dev server 以 `<script>` 載入（見偏差 13），只能複製到 `web/public/__pfux-*.js`（已列入 `.gitignore`），內容限於腳本全文，前面加 `window.__pfuxPromise = `、後面加一行把結果寫進 `window.__pfuxResult`；負向對照另可加入注入 style 的程式碼。產生副本後對副本執行同一個 grep，結果同樣只能命中那一行布林判定。dev server 會把副本暴露給可連到 5173 的主機，所以量測結束（不論成功或失敗）都要立刻刪除副本，並核對 `web/public` 只剩 `favicon.svg` 與 `icons.svg`、`git status --short --ignored web/public` 沒有 `__pfux-*`。
      - 為什麼可以用 `javascript_tool`：Claude 只會收到腳本的回傳值，頁面的其他內容不會傳回。只要腳本符合上面的限制（可用 grep 驗證），回傳內容就不會有憑證，所以不需要改用其他 runner。
    - **證據**：每次核對都輸出一個 JSON（`round`、`checkedAtUtc`、上述每項核對的結果、`passed`），貼在 1.2／4.2 下方。refresh token 只輸出 `hasRefreshToken` 布林值，不得輸出 token 本體；這份 JSON 會隨 change 歸檔進版控。任務紀錄只寫帳號 Email，不寫密碼。
    - **前置失敗**：任何一項核對不符（包含 UI 登出失敗、logout 請求缺少或非 2xx、註冊或登入失敗），就停止並回報使用者，附上核對 JSON，不執行該輪量測。已執行的量測結果也不得視為通過。
    - **量測腳本內的判定**：每個寬度都在 iframe 內用同一組條件判定 `isAuthenticated`：四項都符合已登入判準時為 `true`，都符合登出判準時為 `false`；兩組都不符（例如 refresh 失敗但 token 還留著）時，該寬度標記為無效量測。
  - **設定常數**：`EVENT_ID`（取自 1.0 fixture 的輸出；目標活動：第一個分區至少 400 個座位，其中至少一個 `Sold`／`Held`、一個 `Available`；同時有座位制與計數票種；販售中；不需實名；未啟用熱門搶購模式。400 的推算見 design.md 決策 6）；`BASELINE`（1.2 設為 `true`，4.2 設為 `false`）；`EXPECT_AUTHENTICATED`（宣告這一輪的登入狀態）。
  - **兩輪**：已登入一輪（`EXPECT_AUTHENTICATED = true`）量全部寬度與斷言 1 至 4；未登入一輪（`false`，先登出）只量 320／390／720 的斷言 4，略過需要結帳列與計數購票列的項目（對應以未登入為前提的 BW-RESP-001、002、005）。
  - **量測方式**：同源 iframe，高度 700px，寬度依序設為 320／390／720／721／800／1280，`scrollbar-width: none`。每個寬度輸出：`W`、`pathname`、`isAuthenticated`、`seatCount`、`documentScrollWidth`、`submitButtonRect`、`lastSeatTop`、`checkoutBarRect`、`lastAvailableSeatRectBefore`、`focusedSeatId`、`expectedFocusedSeatId`、`focusedSeatRect`、標題／票價表／計數購票列／結帳列的 `rect.right`、區域隨選按鈕與分區選單的 `rect.top`，以及 `.seat-btn.sold`、第一個 Available 座位（`.seat-btn:not(.sold)`）、`.seat-swatch--unavailable` 的 computed `text-decoration-line` 與 `border-top-style`（基準狀態下色塊不存在，輸出 `null`），再加上每項斷言的結果、`valid`、`invalidReasons`。
  - **斷言**：
    - 斷言 1：捲到第一個 `.zone-block` 頂端後，送出按鈕的 `top >= 0` 且 `bottom <= 700`，而且 `lastSeatTop > 700`；
    - 斷言 2（只在 390 與 1280 執行，對應 BW-SEAT-A11Y-007）：捲回 `scrollY = 0`，依 DOM 順序算出第一個分區最後一個 `Available` 座位（不依賴實作才有的屬性，基準狀態也能用），它一開始的 `top` 必須大於 700，否則這個寬度的量測無效（第一個分區座位不夠多，回報使用者，不得計為通過）。在第一個可選座位上 `focus({ preventScroll: true })`（避免第一次聚焦就改變捲動位置），再 `dispatchEvent` 送出 `End` keydown，等待 300ms 後，`iframe.contentDocument.activeElement` **就是**這個座位，而且它的 `rect.top >= 0`、`rect.bottom <= checkoutBarRect.top`；
    - 斷言 3：`.seat-btn.sold` 的 `text-decoration-line` 包含 `line-through`，且 `border-top-style === 'dashed'`；色塊的這兩個值與 `.seat-btn.sold` 相同；Available 座位的 `text-decoration-line` 不包含 `line-through`，且 `border-top-style === 'solid'`；
    - 斷言 4：`documentScrollWidth <= W`；`W ≤ 720` 時 grid 只有一欄且購票欄 `top >= 資訊欄 bottom`（位於資訊欄下方），`W ≥ 721` 時第一欄寬 320px、資訊欄寬 320px、購票欄左緣位於資訊欄右緣的右側；活動標題、票價表、計數購票列、結帳列的 `rect.right <= W`；`W = 320` 時區域隨選按鈕的 `top` 大於分區選單的 `top`（換到下一行）。斷言 4 的子項與 AC 對應如下（每條 AC 只看列出的輪次與寬度；子項在該寬度都必須成立）：

      | AC | 輪次 | 寬度 | 元素 | 斷言 4 子項 |
      | --- | --- | --- | --- | --- |
      | BW-RESP-001 | 未登入 | 390 | document、活動標題、票價表 | `documentScrollWidth <= 390`；標題與票價表 `rect.right <= 390` |
      | BW-RESP-002 | 未登入 | 320 | document、區域隨選按鈕、分區選單 | `documentScrollWidth <= 320`；區域隨選按鈕 `top` > 分區選單 `top` |
      | BW-RESP-003 | 已登入 | 390、320 | document、計數購票列、結帳列 | `documentScrollWidth <= W`；計數購票列與結帳列 `rect.right <= W` |
      | BW-RESP-004 | 已登入 | 721、800、1280 | document、grid、資訊欄、購票欄 | `documentScrollWidth <= W`；第一欄寬 320px、資訊欄寬 320px、購票欄左緣 > 資訊欄右緣 |
      | BW-RESP-005 | 未登入 | 720 | document、grid、資訊欄、購票欄 | `documentScrollWidth <= 720`；grid 只有一欄；購票欄 `top >= 資訊欄 bottom` |
  - **無效量測**（任一成立就標記 `valid: false`，並輸出原因）：`pathname` 不是 `/events/{EVENT_ID}`；登入狀態無法判定，或與 `EXPECT_AUTHENTICATED` 不符；第一個分區座位數 `< 400`；第一個分區找不到 `.seat-btn.sold` 或 Available 座位；找不到 `.zone-block`、區域隨選按鈕（以 `.quick-pick button` 查找，基準與新版的按鈕文字不同）；已登入一輪找不到送出按鈕、計數購票列，或 `BASELINE` 為 `false` 時找不到 `.checkout-bar`（基準狀態改量 `.summary`）；任何用於比較的座標不是有限數值，或元素寬度不大於 0。無效量測不得計為斷言通過。
  - **輸出**：一個 JSON 陣列。1.2 與 4.2 把完整 JSON 貼在該任務下方；內容太長時，比照 admin-mobile-nav，比對 SHA-256 確認內容一致。
- [x] 1.2 修改前執行腳本（`BASELINE = true`，已登入與未登入兩輪；每輪依 1.1「登入／登出前置」建立並核對登入狀態），在本任務下方記錄基準值與前置核對 JSON。執行前先跑 1.0 的 fixture，使用它輸出的 `EVENT_ID`；fixture 失敗時停止並回報使用者，不得改用 dev DB 中其他活動。**通過條件**：用來證明檢查本身會失敗。兩輪的每個寬度都要 `valid: true`，並逐輪逐項符合：
  - 已登入一輪：
    - BW-FLOW-001 的斷言 1，以下 5 個寬度逐一記錄，每個都必須 `valid: true` 而且斷言 1 失敗：320、390、720、721、1280。失敗原因必須是「送出按鈕的 `bottom > 700`（按鈕在視窗下緣之外），且 `lastSeatTop > 700`（頁面確實比視窗長）」。斷言 1 失敗只要不是這個原因（例如 `lastSeatTop <= 700`），就不算符合基準預期。800 是額外的回歸寬度，斷言 1 同樣必須以相同原因失敗，但不計入 BW-FLOW-001；
    - 斷言 3 在全部寬度失敗（`.seat-btn.sold` 沒有刪除線）；
    - 390 與 1280 的斷言 2 失敗（沒有 End 鍵處理，焦點停在第一個可選座位）；
    - 全部寬度的斷言 4 通過（mobile-layout-p0-fixes 已修正）；
  - 未登入一輪：320／390／720 的斷言 4 通過。
  - 在本任務下方，以 4.2 的驗收表格式逐列記錄基準結果（「預期」欄填失敗或通過）。

  任何一項不符時，停止並回報使用者。

  **1.2 執行紀錄（2026-10-12）**

  - **fixture**：1.0 fixture 成功，輸出 `PURCHASE_FLOW_UX_EVENT_ID=d076a4fe-7b19-4737-b2c0-20f2f7467663`，執行後保留數 4／6。
  - **偏差 1：沿用同一個活動**。第一輪作廢後，兩次重跑都沿用這個活動，沒有重跑 fixture，以免占用保留額度。量測期間沒有下單，座位狀態不變；兩次已登入重跑的 JSON 逐字元相同（SHA-256 一致），可佐證。
  - **第一輪（作廢）**：帳號 `pfux-202610121426@example.test`。
    - 前置核對：`{"round":"baseline-authenticated","checkedAtUtc":"2026-10-12T14:28:35.851Z","pathname":"/","hasRefreshToken":true,"hasOrdersLink":true,"hasMemberTriggerText":true,"hasLoginLink":false,"passed":true}`
    - 量測結果符合基準預期，但 UI 登出失敗：`POST /api/auth/logout` 的 API log 為 **401**（`read_network_requests` 回報 503）。
    - 登出後 DOM 核對：`{"round":"baseline-authenticated-logout","checkedAtUtc":"2026-10-12T14:37:37.337Z","pathname":"/login","hasRefreshToken":false,"hasLoginLink":true,"hasMemberTrigger":false}`
    - 依 1.1「登出時機」，整輪視為失敗。
    - 根因是既有 bug：`stores/auth.ts` 的 `logout()` 先清空 access token 才呼叫需要 `[Authorize]` 的 logout API，請求不帶 Authorization，必定 401，伺服器端的 refresh token 從未被撤銷。
    - 經使用者同意，以 hotfix 修正：`api/auth.ts`、`api/httpClient.ts`、`stores/auth.ts`，並新增 2 個單元測試。此修正是讓前端符合既有 `openspec/specs/authentication/spec.md`「已登入使用者可以登出」（登出後 Refresh Token 立即失效），不是新行為，不須補 spec；不屬於 purchase-flow-ux 的範圍，另以獨立 commit 提交。
  - **第二輪（作廢）**：帳號 `pfux-202610121445@example.test`。
    - 前置核對：`{"round":"baseline-authenticated-retry","checkedAtUtc":"2026-10-12T14:41:16.780Z","pathname":"/","hidden":true,"hasRefreshToken":true,"hasOrdersLink":true,"hasMemberTriggerText":true,"hasLoginLink":false,"passed":true}`
    - 量測 SHA-256 為 `734c2e4915254084858fb7505cf37f466807910e4e9595f4839cd1d108c035b7`，符合基準預期。
    - 登出由使用者手動點擊，點擊前也沒有開始 `read_network_requests` 追蹤，不符合 1.1 的登出程序；API log 為 204（14:48:59）。經使用者決定作廢，依規定重跑。
  - **第三輪（採用）**：帳號 `pfux-YYYYMMDDHHmm@example.test`。
    - **偏差 2**：使用者註冊時照字面輸入了範本字串，沒有換成時間戳記。帳號仍為一般會員（DB `Role = 0`），只有命名不符格式。
    - 已登入前置核對：`{"round":"baseline-authenticated-retry2","checkedAtUtc":"2026-10-12T14:53:33.511Z","pathname":"/","hidden":true,"hasRefreshToken":true,"hasOrdersLink":true,"hasMemberTriggerText":true,"hasLoginLink":false,"passed":true}`
    - UI 登出由 Claude 點擊 `.member-trigger` →「登出」。點擊前已用 `read_network_requests` 開始追蹤並清空，工具抓到 1 筆 `POST /api/auth/logout`。依 1.1 修正後的判定（**偏差 3**，經使用者核准）：
      - 工具回報 503（誤報）；
      - Resource Timing：`{"path":"/api/auth/logout","initiatorType":"fetch","responseStatus":204}`；
      - API log：`[14:55:47 INF] HTTP POST /api/auth/logout responded 204`；
      - → 通過。
    - 重新載入主頁後的 DOM 核對：`{"round":"baseline-authenticated-retry2-logout","checkedAtUtc":"2026-10-12T14:56:14.282Z","pathname":"/","hasRefreshToken":false,"hasLoginLink":true,"hasMemberTrigger":false,"passed":true}`
    - 未登入輪：在上述登出核對通過後執行，前置核對即為上面的登出 JSON。
  - **已登入輪完整 JSON**（第三輪；SHA-256 `734c2e4915254084858fb7505cf37f466807910e4e9595f4839cd1d108c035b7`，6564 字元，頁面內與本機存檔的雜湊一致）：

    ```json
    [{"W":320,"baseline":true,"expectAuthenticated":true,"pathname":"/events/d076a4fe-7b19-4737-b2c0-20f2f7467663","isAuthenticated":true,"seatCount":400,"documentScrollWidth":320,"submitButtonRect":{"top":2207.484375,"bottom":2239.484375,"left":16,"right":104,"width":88,"height":32},"lastSeatTop":1966.578125,"checkoutBarRect":{"top":2143.484375,"bottom":2239.484375,"left":16,"right":304,"width":288,"height":96},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":304,"ticketTable":304,"countRow":304,"checkoutBar":304},"gridColumns":1,"firstGridColumnWidth":288,"infoColumnWidth":288,"purchaseColumnLeft":16,"infoColumnRight":304,"quickPickButtonTop":577.578125,"zoneSelectTop":497.578125,"styles":{"soldSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":null},"assertion1":false,"assertion2":null,"assertion3":false,"assertion4":true,"valid":true,"invalidReasons":[]},{"W":390,"baseline":true,"expectAuthenticated":true,"pathname":"/events/d076a4fe-7b19-4737-b2c0-20f2f7467663","isAuthenticated":true,"seatCount":400,"documentScrollWidth":390,"submitButtonRect":{"top":1911.484375,"bottom":1943.484375,"left":16,"right":104,"width":88,"height":32},"lastSeatTop":1694.578125,"checkoutBarRect":{"top":2474.484375,"bottom":2546.484375,"left":16,"right":374,"width":358,"height":72},"lastAvailableSeatRectBefore":{"top":2297.578125,"bottom":2325.578125,"left":310,"right":346,"width":36,"height":28},"focusedSeatId":"387","expectedFocusedSeatId":"2","focusedSeatRect":{"top":631.578125,"bottom":659.578125,"left":16,"right":52,"width":36,"height":28},"rights":{"title":374,"ticketTable":374,"countRow":374,"checkoutBar":374},"gridColumns":1,"firstGridColumnWidth":358,"infoColumnWidth":358,"purchaseColumnLeft":16,"infoColumnRight":374,"quickPickButtonTop":537.578125,"zoneSelectTop":497.578125,"styles":{"soldSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":null},"assertion1":false,"assertion2":false,"assertion3":false,"assertion4":true,"valid":true,"invalidReasons":[]},{"W":720,"baseline":true,"expectAuthenticated":true,"pathname":"/events/d076a4fe-7b19-4737-b2c0-20f2f7467663","isAuthenticated":true,"seatCount":400,"documentScrollWidth":720,"submitButtonRect":{"top":1061.484375,"bottom":1093.484375,"left":16,"right":104,"width":88,"height":32},"lastSeatTop":844.578125,"checkoutBarRect":{"top":1021.484375,"bottom":1093.484375,"left":16,"right":704,"width":688,"height":72},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":704,"ticketTable":704,"countRow":704,"checkoutBar":704},"gridColumns":1,"firstGridColumnWidth":688,"infoColumnWidth":688,"purchaseColumnLeft":16,"infoColumnRight":704,"quickPickButtonTop":440.578125,"zoneSelectTop":440.578125,"styles":{"soldSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":null},"assertion1":false,"assertion2":null,"assertion3":false,"assertion4":true,"valid":true,"invalidReasons":[]},{"W":721,"baseline":true,"expectAuthenticated":true,"pathname":"/events/d076a4fe-7b19-4737-b2c0-20f2f7467663","isAuthenticated":true,"seatCount":400,"documentScrollWidth":721,"submitButtonRect":{"top":1911.8125,"bottom":1943.8125,"left":368,"right":456,"width":88,"height":32},"lastSeatTop":1694.90625,"checkoutBarRect":{"top":1871.8125,"bottom":1943.8125,"left":368,"right":705,"width":337,"height":72},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":336,"ticketTable":336,"countRow":705,"checkoutBar":705},"gridColumns":2,"firstGridColumnWidth":320,"infoColumnWidth":320,"purchaseColumnLeft":368,"infoColumnRight":336,"quickPickButtonTop":197.90625,"zoneSelectTop":157.90625,"styles":{"soldSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":null},"assertion1":false,"assertion2":null,"assertion3":false,"assertion4":true,"valid":true,"invalidReasons":[]},{"W":800,"baseline":true,"expectAuthenticated":true,"pathname":"/events/d076a4fe-7b19-4737-b2c0-20f2f7467663","isAuthenticated":true,"seatCount":400,"documentScrollWidth":800,"submitButtonRect":{"top":1571.8125,"bottom":1603.8125,"left":368,"right":456,"width":88,"height":32},"lastSeatTop":1354.90625,"checkoutBarRect":{"top":1531.8125,"bottom":1603.8125,"left":368,"right":784,"width":416,"height":72},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":336,"ticketTable":336,"countRow":784,"checkoutBar":784},"gridColumns":2,"firstGridColumnWidth":320,"infoColumnWidth":320,"purchaseColumnLeft":368,"infoColumnRight":336,"quickPickButtonTop":197.90625,"zoneSelectTop":157.90625,"styles":{"soldSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":null},"assertion1":false,"assertion2":null,"assertion3":false,"assertion4":true,"valid":true,"invalidReasons":[]},{"W":1280,"baseline":true,"expectAuthenticated":true,"pathname":"/events/d076a4fe-7b19-4737-b2c0-20f2f7467663","isAuthenticated":true,"seatCount":400,"documentScrollWidth":1280,"submitButtonRect":{"top":1027.8125,"bottom":1059.8125,"left":452,"right":540,"width":88,"height":32},"lastSeatTop":810.90625,"checkoutBarRect":{"top":1210.8125,"bottom":1282.8125,"left":452,"right":1180,"width":728,"height":72},"lastAvailableSeatRectBefore":{"top":1033.90625,"bottom":1061.90625,"left":788,"right":824,"width":36,"height":28},"focusedSeatId":"387","expectedFocusedSeatId":"2","focusedSeatRect":{"top":251.90625,"bottom":279.90625,"left":452,"right":488,"width":36,"height":28},"rights":{"title":420,"ticketTable":420,"countRow":1180,"checkoutBar":1180},"gridColumns":2,"firstGridColumnWidth":320,"infoColumnWidth":320,"purchaseColumnLeft":452,"infoColumnRight":420,"quickPickButtonTop":157.90625,"zoneSelectTop":157.90625,"styles":{"soldSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":null},"assertion1":false,"assertion2":false,"assertion3":false,"assertion4":true,"valid":true,"invalidReasons":[]}]
    ```

  - **未登入輪完整 JSON**（SHA-256 `5af2e87943ecfde2255c240535a6169cdcea0e5ac36c26f3b8f4bc4ce0d1c48e`，2875 字元；第二輪後與第三輪後各跑一次，雜湊相同）：

    ```json
    [{"W":320,"baseline":true,"expectAuthenticated":false,"pathname":"/events/d076a4fe-7b19-4737-b2c0-20f2f7467663","isAuthenticated":false,"seatCount":400,"documentScrollWidth":320,"submitButtonRect":null,"lastSeatTop":null,"checkoutBarRect":{"top":2842.484375,"bottom":2938.484375,"left":16,"right":304,"width":288,"height":96},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":304,"ticketTable":304,"countRow":null,"checkoutBar":null},"gridColumns":1,"firstGridColumnWidth":288,"infoColumnWidth":288,"purchaseColumnLeft":16,"infoColumnRight":304,"quickPickButtonTop":633.578125,"zoneSelectTop":553.578125,"styles":{"soldSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":null},"assertion1":null,"assertion2":null,"assertion3":null,"assertion4":true,"valid":true,"invalidReasons":[]},{"W":390,"baseline":true,"expectAuthenticated":false,"pathname":"/events/d076a4fe-7b19-4737-b2c0-20f2f7467663","isAuthenticated":false,"seatCount":400,"documentScrollWidth":390,"submitButtonRect":null,"lastSeatTop":null,"checkoutBarRect":{"top":2530.484375,"bottom":2602.484375,"left":16,"right":374,"width":358,"height":72},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":374,"ticketTable":374,"countRow":null,"checkoutBar":null},"gridColumns":1,"firstGridColumnWidth":358,"infoColumnWidth":358,"purchaseColumnLeft":16,"infoColumnRight":374,"quickPickButtonTop":593.578125,"zoneSelectTop":553.578125,"styles":{"soldSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":null},"assertion1":null,"assertion2":null,"assertion3":null,"assertion4":true,"valid":true,"invalidReasons":[]},{"W":720,"baseline":true,"expectAuthenticated":false,"pathname":"/events/d076a4fe-7b19-4737-b2c0-20f2f7467663","isAuthenticated":false,"seatCount":400,"documentScrollWidth":720,"submitButtonRect":null,"lastSeatTop":null,"checkoutBarRect":{"top":1583.484375,"bottom":1655.484375,"left":16,"right":704,"width":688,"height":72},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":704,"ticketTable":704,"countRow":null,"checkoutBar":null},"gridColumns":1,"firstGridColumnWidth":688,"infoColumnWidth":688,"purchaseColumnLeft":16,"infoColumnRight":704,"quickPickButtonTop":496.578125,"zoneSelectTop":496.578125,"styles":{"soldSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":null},"assertion1":null,"assertion2":null,"assertion3":null,"assertion4":true,"valid":true,"invalidReasons":[]}]
    ```

  - **基準驗收表**（第三輪）：

    | AC | 輪次 | 寬度 | 斷言 | 預期 | 實際 | valid | 原因／數值 |
    | --- | --- | --- | --- | --- | --- | --- | --- |
    | BW-FLOW-001 | 已登入 | 320 | 斷言 1 | 失敗 | 失敗 | true | 按鈕 bottom 2239.48 > 700；lastSeatTop 1966.58 > 700 |
    | BW-FLOW-001 | 已登入 | 390 | 斷言 1 | 失敗 | 失敗 | true | 按鈕 bottom 1943.48 > 700；lastSeatTop 1694.58 > 700 |
    | BW-FLOW-001 | 已登入 | 720 | 斷言 1 | 失敗 | 失敗 | true | 按鈕 bottom 1093.48 > 700；lastSeatTop 844.58 > 700 |
    | BW-FLOW-001 | 已登入 | 721 | 斷言 1 | 失敗 | 失敗 | true | 按鈕 bottom 1943.81 > 700；lastSeatTop 1694.91 > 700 |
    | BW-FLOW-001 | 已登入 | 1280 | 斷言 1 | 失敗 | 失敗 | true | 按鈕 bottom 1059.81 > 700；lastSeatTop 810.91 > 700 |
    | （額外回歸） | 已登入 | 800 | 斷言 1 | 失敗 | 失敗 | true | 按鈕 bottom 1603.81 > 700；lastSeatTop 1354.91 > 700 |
    | BW-SEAT-A11Y-002（實際樣式） | 已登入 | 320、390、720、721、800、1280 | 斷言 3 | 失敗 | 失敗（6／6） | true | `.seat-btn.sold` 為 `none`／`solid`；色塊不存在（`null`） |
    | BW-SEAT-A11Y-007 | 已登入 | 390 | 斷言 2 | 失敗 | 失敗 | true | 焦點停在 387（第一個可選），預期 2；起始 top 2297.58 > 700 |
    | BW-SEAT-A11Y-007 | 已登入 | 1280 | 斷言 2 | 失敗 | 失敗 | true | 焦點停在 387，預期 2；起始 top 1033.91 > 700 |
    | BW-RESP-001 390px 寬未登入瀏覽 | 未登入 | 390 | 斷言 4 | 通過 | 通過 | true | scrollWidth 390 |
    | BW-RESP-002 320px 寬未登入瀏覽 | 未登入 | 320 | 斷言 4 | 通過 | 通過 | true | scrollWidth 320；隨選按鈕 top 633.58 > 選單 553.58 |
    | BW-RESP-003 已登入買家在窄螢幕瀏覽含計數票種 | 已登入 | 390、320 | 斷言 4 | 通過 | 通過 | true | 右緣皆 ≤ W |
    | BW-RESP-004 桌面與平板寬度維持雙欄版面 | 已登入 | 721、800、1280 | 斷言 4 | 通過 | 通過 | true | 第一欄 320、資訊欄 320、購票欄左緣 > 資訊欄右緣 |
    | BW-RESP-005 斷點上限寬度維持單欄 | 未登入 | 720 | 斷言 4 | 通過 | 通過 | true | grid 1 欄 |
    | （已登入 720 斷言 4） | 已登入 | 720 | 斷言 4 | 通過 | 通過 | true | grid 1 欄 |

    觀察（既有行為，不違反 spec）：座位的 DOM 順序就是 API 回傳順序，第一個可選座位是 387，最後一個是 2。

## 2. 行為測試（先寫測試、確認失敗）

> 寫在 `web/src/pages/buyer/EventDetailPage.test.ts`，沿用既有的 `mountPage()`、`buildSeat()`、mock API 與 `flushPromises`。需要斷言焦點的測試，一律以 `attachTo: document.body` 掛載，並在測試結束時 `unmount`。`beforeEach` 以 `vi.fn()` 補上 `Element.prototype.scrollIntoView`（jsdom 沒有實作），`afterEach` 還原。錯誤位置以 `.checkout-bar .action-error` 與 `.page-error` 的存在與文字判定（design.md 決策 6）：一律同時斷言「出現在應有的位置」**和**「不出現在另一個位置」。完成本節後執行 `docker compose exec web npm run test -- src/pages/buyer/EventDetailPage.test.ts`，確認新測試是因為功能尚未實作而失敗（不是語法或匯入錯誤），並記錄失敗訊息。

- [x] 2.1 [BW-QUICK-001～008] 改寫區域隨選測試。`quickPickButton()` 改為以「自動選位」查找；`EventDetailPage.test.ts:757` 斷言按鈕「不存在」的字串也改為「自動選位」，否則改名後這個負向斷言永遠成立、無法失敗。
  - 改寫既有的 2 個販售期間測試（`:1493` BW-SALES-010、`:1527` BW-SALES-011 的快速選位路徑）：改為先點「自動選位」、斷言 `placeOrder` 未被呼叫，再點「送出訂單」，斷言 `placeOrder` 呼叫一次、抽出的座位仍保留在已選清單，SalesNotOpen 的訊息出現在 `.checkout-bar .action-error`；SalesClosed 維持原本斷言的頁面狀態。這兩個測試的規則（主 spec BW-SALES-010／011）不變，只是操作路徑改變。
  - 改寫既有的 3 個測試：「全部區域…直接送出訂單成功」（`:238`）、「指定單一分區…」（`:261`）、「使用區域隨選時一併送出已輸入的計數購買」（`:603`）。改寫後斷言：點選「自動選位」後 `placeOrder` **未被呼叫**；已選座位數與結帳列清單符合抽出的數量與分區；接著點「送出訂單」才呼叫一次 `placeOrder`，請求內容包含抽出的座位（以及計數項目），並導向訂單結果頁。
  - 新增 BW-QUICK-003：先自動選位，再手動取消其中一個座位，送出的請求不包含該座位。
  - 「分區有可售座位但沒有對應的座位制票種…」（`:287`）原本就斷言 `placeOrder` 未被呼叫，不屬於直接下單測試：只更新按鈕查找方式，另外加一個斷言，抽選被擋下後摘要仍為「已選 0 個座位」。
  - 既有的「張數超過…」「未登入…」「計數額度減少」「純計數不出現」測試只更新按鈕查找方式，斷言不變。張數超過的錯誤位置由 2.3 驗證。
- [x] 2.2 [BW-FLOW-002、005] 已登入，依序點選 7 個 Available 座位：`.checkout-seat-list` 的文字依點選順序列出前 6 個 `分區-號碼`，並包含「等 7 個座位」；取消第 1 個座位後，列出剩下的 6 個，且不包含「等」字。沒有已選座位時，`.checkout-seat-list` 不存在。摘要元素帶有 `aria-live="polite"`，而且點選後文字包含「已選 1 個座位」。
- [x] 2.3 [BW-FLOW-003] 以參數化測試（`it.each`）涵蓋 design.md 決策 1 中歸屬為「操作」的每個來源。每一案都斷言：訊息出現在 `.checkout-bar .action-error`，`.page-error` 不存在，`.action-error` 帶有 `role="alert"`。來源如下：
  - (a) 限購 1 張時點選第 2 個座位；
  - (b) 分區沒有座位制票種時點選該分區的座位；
  - (c) 區域隨選張數超過可售座位數；
  - (d) 區域隨選張數超過限購剩餘額度；
  - (e) 送出前合併總數超過限購：沿用既有測試（`EventDetailPage.test.ts:521`）的做法，在同一個同步批次內輸入兩個計數票種製造超量。另加一個子案：先用 BW-FLOW-007 的方式（一般下單失敗且重新載入也失敗）讓 `.page-error` 出現，再觸發 (e)，斷言 `.page-error` 仍在（未通過前置檢查不清除），`.action-error` 顯示限購訊息；
  - (f) 送出時回傳 429；
  - (g) 送出時回傳 `409 SalesNotOpen`；
  - (h) 送出時回傳一般失敗（例如 409 座位已被鎖定），而且 `loadData` 重新載入成功。

  另加一組 [BW-FLOW-003]「前置檢查提早返回不清除錯誤」：先用 BW-FLOW-007 的方式讓 `.page-error` 與 `.action-error` 並存，再逐一觸發下列情境，每案都斷言兩則訊息仍在、`placeOrder` 未被呼叫：
  - (i) 未登入時按「自動選位」：把 auth store 改為未登入（此時 `isSalesLocked` 為 false，按鈕可按），按下後斷言導向 `/login`（帶 `redirect`），兩則訊息仍在；
  - (j) 已登入、`salesStatus` 不是 `Open` 時：「自動選位」按鈕帶 `disabled`（`isSalesLocked`），無法觸發；
  - (k) 沒有已選內容時：「送出訂單」按鈕帶 `disabled`，無法觸發。
  `handleQuickPick` 的 `quickPickCount <= 0` 分支被 `el-input-number` 的 `:min="1"` 擋住，排隊未放行的 `!canPurchase` 分支也無法觸發：後端 `GetMyQueueStatusHandler` 只回傳 NotJoined／Waiting／Admitted／Expired，已登入且排隊模式開啟時，Waiting 由等待畫面（`showQueueWaiting`）取代選位區；NotJoined／Expired／尚未查詢（null）在販售中由加入畫面（`showJoinPrompt`）取代，非販售中則按鈕因 `isSalesLocked` 停用（即 (j)），兩者都是防禦性分支、UI 無法觸發，不另寫測試。

  另加一案 [BW-FLOW-007]：一般失敗**而且**重新載入也失敗時，`.page-error` 顯示載入失敗訊息，`.action-error` 顯示下單失敗訊息，兩者並存（design.md 決策 1）；失敗後已選內容會被清空（`EventDetailPage.vue:570`），所以先重新點選一個座位，並斷言此時兩則訊息仍並存（選位不清除錯誤），再按一次「送出訂單」，`placeOrder` 回傳 429：`.page-error` 不存在，`.action-error` 只顯示 429 的訊息。只清除 `actionErrorMessage` 的實作會在這一步失敗。
- [x] 2.4 [BW-FLOW-004、006]
  - (a) `getEvents` 失敗時，`.page-error` 顯示載入失敗訊息、帶有 `role="alert"`，而且 `.checkout-bar .action-error` 不存在；
  - (a2) 熱門搶購模式下，`getQueueStatus` 查詢失敗，以及 `joinQueue` 回傳一般失敗，各一案：訊息出現在 `.page-error`；
  - (b) 熱門搶購模式已入場（`Admitted`）的買家送出時回傳 `403 QueueAdmissionRequired`，重新查詢的結果為 `Waiting`：`.page-error` 顯示排隊資格訊息、`.checkout-bar` 不存在；
  - (c) 熱門搶購模式下，狀態為 `NotJoined` 與 `Waiting` 各一案：`.checkout-bar` 不存在、沒有「送出訂單」按鈕。
- [x] 2.5 [BW-SEAT-A11Y-001、002] 活動有座位時，`.seat-legend` 恰好出現一次，位於第一個 `.zone-block` 內、座位網格之前，依序包含「可選」「已選」「不可選」三項文字，每項各有一個帶 `aria-hidden="true"` 的 `.seat-swatch`。「不可選」色塊帶 `.seat-swatch--unavailable`。`Sold` 與 `Held` 座位帶 `.sold` class，`Available` 座位沒有。實際的刪除線與虛線樣式由量測斷言 3 驗證。
- [x] 2.6 [BW-SEAT-A11Y-003、004]
  - (a) 已登入，A 區 12（Available）、13（Sold）、14（Held），另有一個未知狀態 `Unknown`：斷言各座位的 `aria-label` 與 `title` 分別為「A 區 12 號，可選」「A 區 13 號，已售出」「A 區 14 號，保留中」「…，不可選」，12 號的 `aria-pressed` 為 `"false"`；點選 12 號後，它的 `aria-label` 與 `title` 變為「A 區 12 號，已選」，`aria-pressed` 為 `"true"`。
  - (b) 未登入，點選 Sold 座位：該座位的 `aria-disabled` 為 `"true"`、沒有 `disabled` 屬性，`router.push` 以 `/login` 與 `redirect` 呼叫。
  - (c) 已登入，點選 Sold 座位：不加入已選清單。
  - (d)（2026-10-13 外部審查後補）已登入，Held 座位：`aria-disabled` 為 `"true"`、沒有 `disabled` 屬性，點選後不加入已選清單。突變檢查：`aria-disabled` 改為只判斷 `Sold` → 本案 FAIL，已還原。
- [x] 2.7 [BW-SEAT-A11Y-005、006]
  - (a) A 區 = [Sold, Available, Available, Held, Available]（第 4 席 2026-10-13 由 Sold 改為 Held，讓 Held 被跳過也有測試；突變檢查：roving tabindex 與方向鍵改為只排除 `Sold` → (b) FAIL，已還原），B 區全部 Sold：A 區只有第 2 個座位的 `tabindex` 為 `"0"`，其餘為 `"-1"`；B 區所有座位都是 `"-1"`。
  - (b) 聚焦 A 區第 2 個座位，每一步都在**目前焦點的座位**上觸發 keydown（事件冒泡到 `.seat-grid`，與真實操作一致），依序為 `ArrowRight`、`ArrowRight`、`ArrowLeft`、`End`、`ArrowRight`、`Home`、`ArrowLeft`、`ArrowDown`、`ArrowUp`：`document.activeElement` 依序為第 3、5、3、5、5、2、2、3、2 個座位。第 2、3 步跨過第 4 個 Held 座位；記錄每一步的焦點，斷言第 1 個（Sold）與第 4 個（Held）座位從未取得焦點。每一步都斷言整個 A 區恰有一個 `tabindex="0"`，而且就是焦點座位；`scrollIntoView` 以 `{ block: 'nearest' }` 被呼叫；這 9 個 keydown 事件的 `defaultPrevented` 為 `true`；`Tab`、`Enter`、`Space`，以及 `altKey: true` 的 `ArrowLeft`、`ctrlKey: true` 的 `End` 的 `defaultPrevented` 為 `false`，而且焦點不動。
  - (b2)（2026-10-13 strict-reviewer warning 後補）聚焦第 4 個（Held）座位（模擬滑鼠點選不可選座位後焦點停在該席），依序按 `ArrowRight`、`ArrowLeft`、`Home`、`End`：每次 `defaultPrevented` 為 `false`、焦點仍在第 4 個，Tab 停留點仍為第 2 個。修正前焦點會跳到分區第一個可選座位。突變檢查：移除 `currentIndex === -1` 的提前返回 → (b2) FAIL，已還原並以 `cmp` 確認。
  - (c) 滑鼠點選第 5 個座位後，Tab 停留點變成第 5 個。
  - (d) 把記錄的座位（第 5 個）改為 Sold，重新載入資料（模擬下單失敗刷新）後，Tab 停留點回到第 2 個。

  **第 2 節失敗紀錄（2026-10-12，實作前）**：`EventDetailPage.test.ts` 共 104 個測試，45 failed／59 passed。失敗原因全部是功能尚未實作，沒有語法或匯入錯誤：
  - 20 個：「找不到「自動選位」按鈕」（區域隨選改寫測試、BW-SALES-004／005／007／010／011 的 `expectPurchaseControlsDisabled` 與快速選位路徑）；
  - 9 個：`Cannot call text on an empty DOMWrapper`（`.checkout-bar`／`.action-error`／`.page-error` 尚不存在）；
  - 其餘：`aria-label`／`aria-disabled`／`tabindex` 為 `undefined`、`.seat-legend` 數量為 0、方向鍵後焦點未移動、`.checkout-seat-list` 不存在（`expected false to be true`）、點選後 Tab 停留點未記錄（`expected [] to deeply equal ['5']`）。
  - 實作前即通過（守門斷言，現行行為已符合）：BW-FLOW-006 的 NotJoined／Waiting 兩案、BW-SEAT-A11Y-004 (c) 已登入點選 Sold 不加入。
  - 第一次執行時「混合座位選購與純計數購買並成功下單」也失敗（46 failed），單獨執行（`-t`）通過，第二次完整執行亦通過；判定為前面失敗中斷的測試殘留 wrapper／pending promise 造成的干擾，非本 change 引入。實作完成後（3.6）以完整執行再確認。

## 3. 實作

> 開始前確認：design.md 的 AC 例外已核准，且第 2 節的測試已確認失敗。

- [x] 3.1 錯誤 state 拆分（design.md 決策 1）：新增 `actionErrorMessage`，依決策 1 的表格逐一替換 `errorMessage.value = …` 的指派處。頂端 alert 加上 class `page-error`。`handleQuickPick` 與 `handleSubmit` 開頭同時清除 `errorMessage` 與 `actionErrorMessage`；`loadData` 與 `handleJoinQueue` 開頭只清除 `errorMessage`。一般下單失敗時，維持「先 `loadData()`、再設定訊息」的順序，並更新該段註解，說明現在兩則訊息會並存。
- [x] 3.2 結帳列（design.md 決策 2）：`.summary` 改為 `.checkout-bar`，內含 `.action-error` alert、`.checkout-seat-list`（以 computed 產生前 6 個座位與「等 N 個座位」）、帶 `aria-live="polite"` 的摘要，以及「送出訂單」按鈕。CSS 設定 sticky、不透明背景與上邊框；座位加上 `scroll-margin-bottom: 160px`。
- [x] 3.3 區域隨選只填入座位（design.md 決策 3）：移除 `await handleSubmit()`，改為同步函式；按鈕改為「自動選位」，移除 `:loading="submitting"`。更新 `handleQuickPick` 上方說明「一鍵購票路徑」的註解，改為說明為什麼只填入（U11：送出前要讓買家看到抽中的座位）。
- [x] 3.4 座位 `aria-*`、圖例與樣式（design.md 決策 4）：
  - 新增 `getSeatLabel(seat)`；
  - 座位綁定 `aria-pressed`、`aria-label`、`title`、`aria-disabled`；
  - `.seat-grid` 加上 `role="group"` 與 `aria-label`；
  - 第一個 `.zone-block` 內加入 `.seat-legend`；
  - `.seat-btn.sold, .seat-swatch--unavailable` 共用刪除線與虛線規則；
  - hover 改放在 `@media (hover: hover) and (pointer: fine)` 內。
- [x] 3.5 roving tabindex（design.md 決策 5）：
  - 新增 `focusedSeatIdByZone` 與 `getTabStopSeatId(zoneCode, zoneSeats)`；
  - 座位綁定 `:tabindex`、`data-seat-id`、`@focus`，並在 `@click` 時更新記錄；
  - `.seat-grid` 綁定 `@keydown`，只處理 6 個按鍵，以 `focus({ preventScroll: true })` 加上 `scrollIntoView({ block: 'nearest' })` 移動焦點。
- [x] 3.6 執行 `docker compose exec web npm run test -- src/pages/buyer/EventDetailPage.test.ts`：第 2 節的新測試全部通過；既有測試除了 2.1 列出的改寫（3 個區域隨選直接下單測試、BW-SALES-010／011 的 2 個快速選位路徑測試、`:287` 新增的摘要斷言、`:757` 的負向斷言）之外，斷言語意不變，全部通過。如果有既有測試因為查找方式改變而失敗，只能修改查找方式；如果需要修改斷言內容，停止並回報使用者。

## 4. 驗證

- [x] 4.1 執行完整前端測試 `docker compose exec web npm run test` 與型別檢查 `docker compose exec web npm run build`。記錄通過數；如果有失敗，記錄失敗的測試名稱，並判斷與本 change 是否有關（先在 master 重跑同一個測試確認），不得略過不報。
  - 執行紀錄（2026-10-12）：`npx vitest run` 39 個檔案、534 個測試全部通過，無失敗、無略過；`npm run build`（`vue-tsc -b && vite build`）通過。
  - 執行紀錄（2026-10-13，補 Held／連續自動選位／手動點選不清除錯誤的測試後）：`npx vitest run` 39 個檔案、537 個測試全部通過，無失敗、無略過；`npm run build` 通過。新增 3 個測試：Held 的 `aria-disabled` 與點選（2.6(d)）、[BW-QUICK-001] 連續自動選位不重複、[BW-FLOW-007] 手動點選座位不清除既有錯誤；另把 2.7 的第 4 席改為 Held。各自以突變檢查確認能失敗（自動選位候選移除 `!isSelected` → 連續選位測試 FAIL；`toggleSeat` 加入清除錯誤 → 手動點選測試 FAIL），已還原並以 `cmp` 確認原始檔一致。
  - 執行紀錄（2026-10-13，處理 strict-reviewer 3 項 warning 後）：`npx vitest run` 39 個檔案、539 個測試全部通過，無失敗、無略過；`npm run build` 通過；`openspec validate --strict` 通過。新增 2 個測試：2.7(b2) 焦點在不可選座位時方向鍵不攔截；logout hotfix 的 `logout_WhenLogoutApiRejectsWithExpiredAccessToken_StillClearsLocalStateWithoutRefreshing`（記錄 access token 過期時伺服器端 refresh token 不會被撤銷的已知殘餘風險，突變檢查：把 `catch` 改為 `finally` → 本案 FAIL，已還原）。[BW-QUICK-001] 第一個測試改為以已選清單標籤對照座位 Id 驗證送出內容，斷言強度不變。
- [x] 4.2 先跑 1.0 的 fixture，並依 1.1「登入／登出前置」建立登入狀態，再執行量測腳本（`BASELINE = false`），在本任務下方記錄完整結果與前置核對 JSON。**通過條件**：已登入與未登入兩輪的每個寬度都 `valid: true`，而且該輪量測的斷言全部通過。此外，在本任務下方以下表逐列記錄 `valid` 與斷言結果；缺任何一列或任何一列未通過，本任務就未完成：

  | AC | 輪次 | 寬度 | 斷言 |
  | --- | --- | --- | --- |
  | BW-FLOW-001 | 已登入 | 320、390、720、721、1280（各一列，共 5 列） | 斷言 1 |
  | （額外回歸，不計入 BW-FLOW-001） | 已登入 | 800 | 斷言 1 |
  | BW-SEAT-A11Y-002（實際樣式） | 已登入 | 320、390、720、721、800、1280 | 斷言 3 |
  | BW-SEAT-A11Y-007 | 已登入 | 390、1280 | 斷言 2 |
  | BW-RESP-001 390px 寬未登入瀏覽 | 未登入 | 390 | 斷言 4 |
  | BW-RESP-002 320px 寬未登入瀏覽 | 未登入 | 320 | 斷言 4 |
  | BW-RESP-003 已登入買家在窄螢幕瀏覽含計數票種 | 已登入 | 390、320 | 斷言 4 |
  | BW-RESP-004 桌面與平板寬度維持雙欄版面 | 已登入 | 721、800、1280 | 斷言 4 |
  | BW-RESP-005 斷點上限寬度維持單欄 | 未登入 | 720 | 斷言 4 |

  任何一項失敗時，停止並回報使用者，不得放寬斷言。

  **4.2 執行紀錄（2026-10-12）**

  - **fixture**：1.0 fixture 成功（exit code 0），輸出 `PURCHASE_FLOW_UX_EVENT_ID=b4b9076b-cc48-44e7-92cc-3807f04500e0`，執行後保留數 5／6。
  - **第一輪（作廢）**：帳號 `pfux-202610121520@example.test`。
    - 已登入前置核對通過（四項判準皆符合；當時沒有輸出成 1.1 規定的 `round`／`checkedAtUtc` JSON 格式，**偏差 4**）。
    - 量測 6 個寬度皆 `valid: true`，斷言 1、2、4 通過，**斷言 3 在 6 個寬度全部失敗**：`.seat-swatch--unavailable` 的 `border-top-style` 為 `solid`，與 `.seat-btn.sold` 的 `dashed` 不同（SHA-256 `ab79ab537052fc2a851cc8c707b8ee6fb804c92129a0498104f74a12a96af422`）。
    - 根因：`.seat-swatch` 基礎規則（含 `border` 簡寫與 `background`）排在 `.seat-btn.sold, .seat-swatch--unavailable` 共用規則之後，特異度相同，後者的 `border-style: dashed` 被覆蓋；同理 `.seat-swatch--selected` 的背景色也被蓋掉（無斷言涵蓋，一併修正）。修正：只把 `.seat-swatch` 基礎規則移到兩條共用規則之前，斷言未放寬。修正後 `npx vitest run` 39 個檔案、534 個測試全部通過，`npm run build` 通過。
    - **偏差 5**：這輪執行的腳本去掉了註解（邏輯不變）；第二輪改用腳本全文。
    - UI 登出：Claude 點開 `.member-trigger` 時分頁為 hidden，選單動畫停住，「登出」由**使用者手動點擊**（同 1.2 第二輪）。Resource Timing `{"path":"/api/auth/logout","initiatorType":"fetch","responseStatus":204}`；API log `[15:31:13 INF] HTTP POST /api/auth/logout responded 204`；工具回報 503（誤報）。本輪已因斷言 3 失敗作廢，不影響結論。
  - **偏差 6：沿用同一個活動**。第二輪沿用 `b4b9076b…`，沒有重跑 fixture（保留最後一次額度）。第一輪沒有下單，座位狀態不變；兩輪 JSON 除斷言 3 相關的兩個欄位外逐字元相同（以第一輪 JSON 只替換這兩個欄位，得到的 SHA-256 與頁面內第二輪結果一致），可佐證。經使用者同意。
  - **第二輪（作廢：登入／登出程序有偏差 7–9，使用者決定依 1.1 重跑）**：同一帳號 `pfux-202610121520@example.test`，使用者重新登入。量測結果與第三輪逐字元相同。
    - 已登入前置核對：`{"pathname":"/","hasRefresh":true,"loginLink":false,"memberTrigger":true}`（**偏差 7**：這次核對漏了 `/orders` 連結與 `.member-trigger` 文字兩項，也沒有 `checkedAtUtc`。量測腳本在每個寬度的 iframe 內以完整四項判準判定 `isAuthenticated`，6 個寬度皆為 `true`，可補足）。
    - UI 登出由 **Claude** 點擊 `.member-trigger` →「登出」（使用者先讓瀏覽器視窗成為作用中，分頁變為 visible，未點擊頁面）。
      - `read_network_requests`：點擊前沒有再呼叫一次（**偏差 8**），但工具沿用本 session 先前的追蹤，事後篩選 `/api/auth/logout` 共 2 筆 `POST`（第一輪與本輪各一），工具皆回報 503（誤報）；
      - Resource Timing：`{"path":"/api/auth/logout","initiatorType":"fetch","responseStatus":204}`；
      - API log：`[15:40:02 INF] HTTP POST /api/auth/logout responded 204`；
      - → 通過。
    - 重新載入主頁後的 DOM 核對：`{"round":"final-authenticated-r2-logout","checkedAtUtc":"2026-10-12T15:42:02.279Z","pathname":"/","hasRefreshToken":false,"hasLoginLink":true,"hasMemberTrigger":false}`
    - **偏差 9**：未登入輪是在登出後、上面這次重新載入核對**之前**執行（當時頁面在 `/login`，已核對 `hasRefresh: false`、有登入連結、無 `.member-trigger`）。量測腳本在每個寬度的 iframe 內判定 `isAuthenticated` 皆為 `false`。
  - **第三輪（採用，2026-10-12）**：帳號 `pfux-9453@example.test`，由使用者註冊並登入。沿用活動 `b4b9076b…`（偏差 6），沒有重跑 fixture，保留數仍為 5／6。執行的是腳本全文，只切換第 11 行的 `EXPECT_AUTHENTICATED`。
    - **偏差 10**：帳號 Email 不是 `pfux-<YYYYMMDDHHmm>` 格式，性質同偏差 2，只是命名不符。
    - 已登入前置核對（四項判準）：`{"round":"4.2-r3-authenticated","checkedAtUtc":"2026-10-12T15:59:31.780Z","pathname":"/","visibility":"hidden","hasRefreshToken":true,"hasOrdersLink":true,"hasMemberTriggerText":true,"hasLoginLink":false,"passed":true}`
    - 已登入輪：6 個寬度皆 `valid: true`，斷言 1～4 全部通過（390、1280 之外的寬度不執行斷言 2，值為 `null`）。SHA-256 `29bb5f6c…`，共 6795 字元，與第二輪逐字元相同，完整 JSON 見下方。
    - UI 登出：
      - 點擊前先呼叫 `read_network_requests`（`clear: true`），清掉第二輪留下的 1 筆舊紀錄。
      - 使用者讓視窗成為作用中，沒有點擊頁面；Claude 確認頁面 `visible` 且 `hasFocus: true` 後，才點 `.member-trigger` →「登出」。
      - `read_network_requests`：1 筆 `POST /api/auth/logout`，工具回報 503（已知誤報）。
      - Resource Timing：`{"path":"/api/auth/logout","initiatorType":"fetch","responseStatus":204}`。SPA 沒有重新載入，所以同一份清單裡還留著第二輪的 1 筆，也是 204。
      - API log：`[16:06:57 INF] HTTP POST /api/auth/logout responded 204`，時間與點擊相符。log 中另有 `[15:58:07] … 204` 一筆，發生在本輪登入核對（15:59:31）之前，不是本輪的點擊。
      - → 通過。
    - 重新載入主頁後核對：`{"round":"4.2-r3-logout","checkedAtUtc":"2026-10-12T16:07:12.533Z","pathname":"/","hasRefreshToken":false,"hasLoginLink":true,"hasMemberTrigger":false,"logoutRequestSeen":true,"resourceTimingStatus":204,"apiLogStatus":204,"passed":true}`
    - 未登入輪：在上一項核對通過後才執行。320、390、720 皆 `valid: true`、`isAuthenticated: false`，斷言 4 通過。SHA-256 `dcd108a1…`，共 3035 字元，與第二輪逐字元相同。
  - **已登入輪完整 JSON**（第三輪，與第二輪相同；SHA-256 `29bb5f6c74e648c2eb08d88a3284ebc892e272445a92bcafe3f0f8205b0cf2dd`，6795 字元，頁面內與本機存檔的雜湊一致）：

    ```json
    [{"W":320,"baseline":false,"expectAuthenticated":true,"pathname":"/events/b4b9076b-cc48-44e7-92cc-3807f04500e0","isAuthenticated":true,"seatCount":400,"documentScrollWidth":320,"submitButtonRect":{"top":656,"bottom":688,"left":16,"right":104,"width":88,"height":32},"lastSeatTop":1998.078125,"checkoutBarRect":{"top":587,"bottom":700,"left":16,"right":304,"width":288,"height":113},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":304,"ticketTable":304,"countRow":304,"checkoutBar":304},"gridColumns":1,"firstGridColumnWidth":288,"infoColumnWidth":288,"purchaseColumnLeft":16,"infoColumnRight":304,"quickPickButtonTop":537.578125,"zoneSelectTop":497.578125,"styles":{"soldSeat":{"textDecorationLine":"line-through","borderTopStyle":"dashed"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":{"textDecorationLine":"line-through","borderTopStyle":"dashed"}},"assertion1":true,"assertion2":null,"assertion3":true,"assertion4":true,"valid":true,"invalidReasons":[]},{"W":390,"baseline":false,"expectAuthenticated":true,"pathname":"/events/b4b9076b-cc48-44e7-92cc-3807f04500e0","isAuthenticated":true,"seatCount":400,"documentScrollWidth":390,"submitButtonRect":{"top":656,"bottom":688,"left":16,"right":104,"width":88,"height":32},"lastSeatTop":1726.078125,"checkoutBarRect":{"top":611,"bottom":700,"left":16,"right":374,"width":358,"height":89},"lastAvailableSeatRectBefore":{"top":2329.078125,"bottom":2357.078125,"left":310,"right":346,"width":36,"height":28},"focusedSeatId":"2","expectedFocusedSeatId":"2","focusedSeatRect":{"top":512.078125,"bottom":540.078125,"left":310,"right":346,"width":36,"height":28},"rights":{"title":374,"ticketTable":374,"countRow":374,"checkoutBar":374},"gridColumns":1,"firstGridColumnWidth":358,"infoColumnWidth":358,"purchaseColumnLeft":16,"infoColumnRight":374,"quickPickButtonTop":537.578125,"zoneSelectTop":497.578125,"styles":{"soldSeat":{"textDecorationLine":"line-through","borderTopStyle":"dashed"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":{"textDecorationLine":"line-through","borderTopStyle":"dashed"}},"assertion1":true,"assertion2":true,"assertion3":true,"assertion4":true,"valid":true,"invalidReasons":[]},{"W":720,"baseline":false,"expectAuthenticated":true,"pathname":"/events/b4b9076b-cc48-44e7-92cc-3807f04500e0","isAuthenticated":true,"seatCount":400,"documentScrollWidth":720,"submitButtonRect":{"top":656,"bottom":688,"left":16,"right":104,"width":88,"height":32},"lastSeatTop":876.078125,"checkoutBarRect":{"top":611,"bottom":700,"left":16,"right":704,"width":688,"height":89},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":704,"ticketTable":704,"countRow":704,"checkoutBar":704},"gridColumns":1,"firstGridColumnWidth":688,"infoColumnWidth":688,"purchaseColumnLeft":16,"infoColumnRight":704,"quickPickButtonTop":440.578125,"zoneSelectTop":440.578125,"styles":{"soldSeat":{"textDecorationLine":"line-through","borderTopStyle":"dashed"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":{"textDecorationLine":"line-through","borderTopStyle":"dashed"}},"assertion1":true,"assertion2":null,"assertion3":true,"assertion4":true,"valid":true,"invalidReasons":[]},{"W":721,"baseline":false,"expectAuthenticated":true,"pathname":"/events/b4b9076b-cc48-44e7-92cc-3807f04500e0","isAuthenticated":true,"seatCount":400,"documentScrollWidth":721,"submitButtonRect":{"top":656,"bottom":688,"left":368,"right":456,"width":88,"height":32},"lastSeatTop":1726.40625,"checkoutBarRect":{"top":611,"bottom":700,"left":368,"right":705,"width":337,"height":89},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":336,"ticketTable":336,"countRow":705,"checkoutBar":705},"gridColumns":2,"firstGridColumnWidth":320,"infoColumnWidth":320,"purchaseColumnLeft":368,"infoColumnRight":336,"quickPickButtonTop":197.90625,"zoneSelectTop":157.90625,"styles":{"soldSeat":{"textDecorationLine":"line-through","borderTopStyle":"dashed"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":{"textDecorationLine":"line-through","borderTopStyle":"dashed"}},"assertion1":true,"assertion2":null,"assertion3":true,"assertion4":true,"valid":true,"invalidReasons":[]},{"W":800,"baseline":false,"expectAuthenticated":true,"pathname":"/events/b4b9076b-cc48-44e7-92cc-3807f04500e0","isAuthenticated":true,"seatCount":400,"documentScrollWidth":800,"submitButtonRect":{"top":656,"bottom":688,"left":368,"right":456,"width":88,"height":32},"lastSeatTop":1386.40625,"checkoutBarRect":{"top":611,"bottom":700,"left":368,"right":784,"width":416,"height":89},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":336,"ticketTable":336,"countRow":784,"checkoutBar":784},"gridColumns":2,"firstGridColumnWidth":320,"infoColumnWidth":320,"purchaseColumnLeft":368,"infoColumnRight":336,"quickPickButtonTop":197.90625,"zoneSelectTop":157.90625,"styles":{"soldSeat":{"textDecorationLine":"line-through","borderTopStyle":"dashed"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":{"textDecorationLine":"line-through","borderTopStyle":"dashed"}},"assertion1":true,"assertion2":null,"assertion3":true,"assertion4":true,"valid":true,"invalidReasons":[]},{"W":1280,"baseline":false,"expectAuthenticated":true,"pathname":"/events/b4b9076b-cc48-44e7-92cc-3807f04500e0","isAuthenticated":true,"seatCount":400,"documentScrollWidth":1280,"submitButtonRect":{"top":656,"bottom":688,"left":452,"right":540,"width":88,"height":32},"lastSeatTop":842.40625,"checkoutBarRect":{"top":611,"bottom":700,"left":452,"right":1180,"width":728,"height":89},"lastAvailableSeatRectBefore":{"top":1065.40625,"bottom":1093.40625,"left":788,"right":824,"width":36,"height":28},"focusedSeatId":"2","expectedFocusedSeatId":"2","focusedSeatRect":{"top":512.40625,"bottom":540.40625,"left":788,"right":824,"width":36,"height":28},"rights":{"title":420,"ticketTable":420,"countRow":1180,"checkoutBar":1180},"gridColumns":2,"firstGridColumnWidth":320,"infoColumnWidth":320,"purchaseColumnLeft":452,"infoColumnRight":420,"quickPickButtonTop":157.90625,"zoneSelectTop":157.90625,"styles":{"soldSeat":{"textDecorationLine":"line-through","borderTopStyle":"dashed"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":{"textDecorationLine":"line-through","borderTopStyle":"dashed"}},"assertion1":true,"assertion2":true,"assertion3":true,"assertion4":true,"valid":true,"invalidReasons":[]}]
    ```

  - **未登入輪完整 JSON**（第三輪，與第二輪相同；SHA-256 `dcd108a1d895c3c57b676260547171ca6762c7ac844b11da95e38431db416415`，3035 字元，頁面內與本機存檔的雜湊一致）：

    ```json
    [{"W":320,"baseline":false,"expectAuthenticated":false,"pathname":"/events/b4b9076b-cc48-44e7-92cc-3807f04500e0","isAuthenticated":false,"seatCount":400,"documentScrollWidth":320,"submitButtonRect":null,"lastSeatTop":null,"checkoutBarRect":{"top":587,"bottom":700,"left":16,"right":304,"width":288,"height":113},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":304,"ticketTable":304,"countRow":null,"checkoutBar":null},"gridColumns":1,"firstGridColumnWidth":288,"infoColumnWidth":288,"purchaseColumnLeft":16,"infoColumnRight":304,"quickPickButtonTop":593.578125,"zoneSelectTop":553.578125,"styles":{"soldSeat":{"textDecorationLine":"line-through","borderTopStyle":"dashed"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":{"textDecorationLine":"line-through","borderTopStyle":"dashed"}},"assertion1":null,"assertion2":null,"assertion3":null,"assertion4":true,"valid":true,"invalidReasons":[]},{"W":390,"baseline":false,"expectAuthenticated":false,"pathname":"/events/b4b9076b-cc48-44e7-92cc-3807f04500e0","isAuthenticated":false,"seatCount":400,"documentScrollWidth":390,"submitButtonRect":null,"lastSeatTop":null,"checkoutBarRect":{"top":611,"bottom":700,"left":16,"right":374,"width":358,"height":89},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":374,"ticketTable":374,"countRow":null,"checkoutBar":null},"gridColumns":1,"firstGridColumnWidth":358,"infoColumnWidth":358,"purchaseColumnLeft":16,"infoColumnRight":374,"quickPickButtonTop":593.578125,"zoneSelectTop":553.578125,"styles":{"soldSeat":{"textDecorationLine":"line-through","borderTopStyle":"dashed"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":{"textDecorationLine":"line-through","borderTopStyle":"dashed"}},"assertion1":null,"assertion2":null,"assertion3":null,"assertion4":true,"valid":true,"invalidReasons":[]},{"W":720,"baseline":false,"expectAuthenticated":false,"pathname":"/events/b4b9076b-cc48-44e7-92cc-3807f04500e0","isAuthenticated":false,"seatCount":400,"documentScrollWidth":720,"submitButtonRect":null,"lastSeatTop":null,"checkoutBarRect":{"top":611,"bottom":700,"left":16,"right":704,"width":688,"height":89},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":704,"ticketTable":704,"countRow":null,"checkoutBar":null},"gridColumns":1,"firstGridColumnWidth":688,"infoColumnWidth":688,"purchaseColumnLeft":16,"infoColumnRight":704,"quickPickButtonTop":496.578125,"zoneSelectTop":496.578125,"styles":{"soldSeat":{"textDecorationLine":"line-through","borderTopStyle":"dashed"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":{"textDecorationLine":"line-through","borderTopStyle":"dashed"}},"assertion1":null,"assertion2":null,"assertion3":null,"assertion4":true,"valid":true,"invalidReasons":[]}]
    ```

  - **驗收表**（第三輪；數值與第二輪相同）：

    | AC | 輪次 | 寬度 | 斷言 | 結果 | valid | 數值 |
    | --- | --- | --- | --- | --- | --- | --- |
    | BW-FLOW-001 | 已登入 | 320 | 斷言 1 | 通過 | true | 按鈕 656～688 ≤ 700；lastSeatTop 1998.08 > 700 |
    | BW-FLOW-001 | 已登入 | 390 | 斷言 1 | 通過 | true | 按鈕 656～688；lastSeatTop 1726.08 |
    | BW-FLOW-001 | 已登入 | 720 | 斷言 1 | 通過 | true | 按鈕 656～688；lastSeatTop 876.08 |
    | BW-FLOW-001 | 已登入 | 721 | 斷言 1 | 通過 | true | 按鈕 656～688；lastSeatTop 1726.41 |
    | BW-FLOW-001 | 已登入 | 1280 | 斷言 1 | 通過 | true | 按鈕 656～688；lastSeatTop 842.41 |
    | （額外回歸） | 已登入 | 800 | 斷言 1 | 通過 | true | 按鈕 656～688；lastSeatTop 1386.41 |
    | BW-SEAT-A11Y-002（實際樣式） | 已登入 | 320、390、720、721、800、1280 | 斷言 3 | 通過（6／6） | true | 已售座位與色塊皆 `line-through`／`dashed`；Available 座位 `none`／`solid` |
    | BW-SEAT-A11Y-007 | 已登入 | 390 | 斷言 2 | 通過 | true | 焦點 2 = 預期 2；起始 top 2329.08 > 700；焦點 512.08～540.08 ≤ 結帳列 top 611 |
    | BW-SEAT-A11Y-007 | 已登入 | 1280 | 斷言 2 | 通過 | true | 焦點 2 = 預期 2；起始 top 1065.41 > 700；焦點 512.41～540.41 ≤ 611 |
    | BW-RESP-001 390px 寬未登入瀏覽 | 未登入 | 390 | 斷言 4 | 通過 | true | scrollWidth 390 |
    | BW-RESP-002 320px 寬未登入瀏覽 | 未登入 | 320 | 斷言 4 | 通過 | true | scrollWidth 320；隨選按鈕 top 593.58 > 選單 553.58 |
    | BW-RESP-003 已登入買家在窄螢幕瀏覽含計數票種 | 已登入 | 390、320 | 斷言 4 | 通過 | true | 右緣 374 ≤ 390、304 ≤ 320 |
    | BW-RESP-004 桌面與平板寬度維持雙欄版面 | 已登入 | 721、800、1280 | 斷言 4 | 通過 | true | 第一欄 320、資訊欄 320；購票欄左緣 368／368／452 > 資訊欄右緣 336／336／420 |
    | BW-RESP-005 斷點上限寬度維持單欄 | 未登入 | 720 | 斷言 4 | 通過 | true | grid 1 欄 |
    | （已登入 720 斷言 4） | 已登入 | 720 | 斷言 4 | 通過 | true | grid 1 欄 |

  - **第四輪（補量 BW-RESP-005，2026-10-13）**：外部審查指出 BW-RESP-005 的 THEN「購票欄位於資訊欄下方」沒有被斷言 4 檢查（原本單欄只看 grid 欄數）。使用者選擇補斷言：`measure-purchase-flow.js` 的斷言 4 在 `W ≤ 720` 時加上 `purchaseColumnTop >= infoColumnBottom`，並輸出這兩個值。只重跑未登入輪（320／390／720）。
    - **偏差 11**：沿用活動 `b4b9076b…`，沒有重跑 1.0 fixture（使用者核准；保留數維持 5／6，避免用掉最後一個名額）。腳本的 `valid` 檢查（pathname、座位數 400、含 Sold 與 Available）全部成立。
    - **偏差 12**：只重跑未登入輪。已登入輪 320／390／720 也是單欄，會套用新條件，但對應的 BW-RESP-003 與 BW-FLOW-001／BW-SEAT-A11Y-* 不要求上下順序，所以沿用第三輪結果，沒有重跑。
    - **偏差 13**：腳本改為從 dev server 的暫存副本 `web/public/__pfux-measure.js` 以 `<script>` 載入（前面加 `window.__pfuxPromise = `，後面加一行把結果寫進 `window.__pfuxResult`），內容與腳本全文相同。量測後已刪除暫存檔，沒有進版控。design-hardener（2026-10-13）指出三個缺口，補正如下：(1) 量測當時沒有對副本跑敏感資訊 grep——事後依同樣規則在暫存目錄重建副本（前綴、腳本全文、結尾一行）後執行同一個 grep，只命中第 22 行布林判定（負向對照只多注入 style 的程式碼，不含 grep 的關鍵字）；(2) 沒有 `.gitignore` 保護——已加入 `web/public/__pfux-*`；(3) 副本存在期間會被 dev server 對外提供——副本只含量測腳本（不含憑證或資料），已刪除並核對 `web/public` 只剩 `favicon.svg`、`icons.svg`。之後的量測依 1.1「暫存副本」規則執行。
    - **前置核對 JSON**（主頁，量測前）：`{"round":"4.2-round4-unauth-precheck","checkedAtUtc":"2026-10-13T06:20:33.124Z","href":"https://localhost:5173/","hasRefreshToken":false,"hasLoginLink":true,"hasMemberTrigger":false,"hasOrdersLink":false,"visible":"visible","hasFocus":false}`，符合登出判準。前面沒有已登入輪，所以這一輪不需要 UI 登出。
    - **未登入輪完整 JSON**（SHA-256 `facb92cf20f9b37b091cb4753f00fd577c877f2d019342622825dd5dbe7a0db0`，3218 字元。本機以第三輪 JSON 補上兩個新欄位重建，雜湊與頁面內一致；其餘欄位與第三輪逐字元相同）：

    ```json
    [{"W":320,"baseline":false,"expectAuthenticated":false,"pathname":"/events/b4b9076b-cc48-44e7-92cc-3807f04500e0","isAuthenticated":false,"seatCount":400,"documentScrollWidth":320,"submitButtonRect":null,"lastSeatTop":null,"checkoutBarRect":{"top":587,"bottom":700,"left":16,"right":304,"width":288,"height":113},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":304,"ticketTable":304,"countRow":null,"checkoutBar":null},"gridColumns":1,"firstGridColumnWidth":288,"infoColumnWidth":288,"purchaseColumnLeft":16,"infoColumnRight":304,"purchaseColumnTop":428.671875,"infoColumnBottom":396.671875,"quickPickButtonTop":593.578125,"zoneSelectTop":553.578125,"styles":{"soldSeat":{"textDecorationLine":"line-through","borderTopStyle":"dashed"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":{"textDecorationLine":"line-through","borderTopStyle":"dashed"}},"assertion1":null,"assertion2":null,"assertion3":null,"assertion4":true,"valid":true,"invalidReasons":[]},{"W":390,"baseline":false,"expectAuthenticated":false,"pathname":"/events/b4b9076b-cc48-44e7-92cc-3807f04500e0","isAuthenticated":false,"seatCount":400,"documentScrollWidth":390,"submitButtonRect":null,"lastSeatTop":null,"checkoutBarRect":{"top":611,"bottom":700,"left":16,"right":374,"width":358,"height":89},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":374,"ticketTable":374,"countRow":null,"checkoutBar":null},"gridColumns":1,"firstGridColumnWidth":358,"infoColumnWidth":358,"purchaseColumnLeft":16,"infoColumnRight":374,"purchaseColumnTop":428.671875,"infoColumnBottom":396.671875,"quickPickButtonTop":593.578125,"zoneSelectTop":553.578125,"styles":{"soldSeat":{"textDecorationLine":"line-through","borderTopStyle":"dashed"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":{"textDecorationLine":"line-through","borderTopStyle":"dashed"}},"assertion1":null,"assertion2":null,"assertion3":null,"assertion4":true,"valid":true,"invalidReasons":[]},{"W":720,"baseline":false,"expectAuthenticated":false,"pathname":"/events/b4b9076b-cc48-44e7-92cc-3807f04500e0","isAuthenticated":false,"seatCount":400,"documentScrollWidth":720,"submitButtonRect":null,"lastSeatTop":null,"checkoutBarRect":{"top":611,"bottom":700,"left":16,"right":704,"width":688,"height":89},"lastAvailableSeatRectBefore":null,"focusedSeatId":null,"expectedFocusedSeatId":null,"focusedSeatRect":null,"rights":{"title":704,"ticketTable":704,"countRow":null,"checkoutBar":null},"gridColumns":1,"firstGridColumnWidth":688,"infoColumnWidth":688,"purchaseColumnLeft":16,"infoColumnRight":704,"purchaseColumnTop":371.671875,"infoColumnBottom":339.671875,"quickPickButtonTop":496.578125,"zoneSelectTop":496.578125,"styles":{"soldSeat":{"textDecorationLine":"line-through","borderTopStyle":"dashed"},"availableSeat":{"textDecorationLine":"none","borderTopStyle":"solid"},"unavailableSwatch":{"textDecorationLine":"line-through","borderTopStyle":"dashed"}},"assertion1":null,"assertion2":null,"assertion3":null,"assertion4":true,"valid":true,"invalidReasons":[]}]
    ```

    - **反向檢查**（證明新條件能失敗）：暫存副本另外在 iframe 注入 `.purchase-column{order:-1}`，讓購票欄排到資訊欄上面，再跑同一輪。三個寬度都 `valid: true`，`documentScrollWidth` 與欄數都正常（1 欄），但斷言 4 都是 `false`：購票欄 top 89，資訊欄 bottom 2946.98／2650.98／1703.98。暫存副本已刪除。
    - **驗收表**（第四輪，只列重跑的 AC）：

    | AC | 輪次 | 寬度 | 斷言 | 結果 | valid | 數值 |
    | --- | --- | --- | --- | --- | --- | --- |
    | BW-RESP-001 390px 寬未登入瀏覽 | 未登入 | 390 | 斷言 4 | 通過 | true | scrollWidth 390；標題與票價表右緣 374 |
    | BW-RESP-002 320px 寬未登入瀏覽 | 未登入 | 320 | 斷言 4 | 通過 | true | scrollWidth 320；隨選按鈕 top 593.58 > 選單 553.58 |
    | BW-RESP-005 斷點上限寬度維持單欄 | 未登入 | 720 | 斷言 4 | 通過 | true | grid 1 欄；購票欄 top 371.67 ≥ 資訊欄 bottom 339.67；scrollWidth 720 |

    觀察：結帳列高度 320px 為 113（座位清單換行），其餘寬度 89。
- [x] 4.3 以 claude-in-chrome 在 390px（同源 iframe）與 1280px 手動檢查，並記錄結果：
  - (a) 用真實 Tab 鍵進入第一個分區時，焦點座位是否被結帳列遮住；如果被遮住，依 design.md Risks 回報使用者決定，不自行修改；
  - (b) 自動選位後，結帳列清單與網格上的選取狀態一致；
  - (c) 方向鍵移動時沒有動畫或延遲；
  - (d) 結帳列在 390px 寬時的實際高度。

  **4.3 執行紀錄（2026-10-12）**：活動 `b4b9076b-cc48-44e7-92cc-3807f04500e0`，帳號 `pfux-202610121520@example.test`（使用者重新登入）。前置核對：`{"round":"4.3-authenticated","checkedAtUtc":"2026-10-12T15:45:14.880Z","pathname":"/","visibility":"visible","hasRefreshToken":true,"hasOrdersLink":true,"hasMemberTriggerText":true,"hasLoginLink":false}`。兩個寬度都用同源 iframe（高 700px、`scrollbar-width: none`）；主視窗寬 952px，1280 也以 iframe 量測。按鍵與點擊都由 claude-in-chrome 送出真實輸入事件（`computer` 的 key／left_click），只用 `javascript_tool` 把起始焦點放在「自動選位」按鈕（`focus({ preventScroll: true })`）與讀取結果。
  - (a) 未被遮住。從「自動選位」按一次 Tab，焦點進入第一個分區的停留點（A 區 231，`tabindex="0"`）：
    - 390，座位已在視窗內（scrollY 403）：座位 260～288，結帳列 top 611，未捲動；
    - 390，座位在結帳列後方（座位 top 650，scrollY 13）：瀏覽器依 `scroll-margin-bottom: 160px` 捲到 scrollY 151，座位 512～540 ≤ 611；
    - 390，頁面頂端（scrollY 0，座位 top 663，同樣在結帳列後方）：結果同上，512～540；
    - 1280，頁面頂端：座位 283～311，未捲動。1280 的停留點在 scrollY 0 時就在視窗上半部，「座位在結帳列後方」的情境無法構成。
  - (b) 一致，未送出訂單。
    - 390，張數 3：網格 `.selected` 與 `aria-pressed="true"` 為 209、186、189；結帳列「A-209、A-186、A-189」；摘要「已選 3 個座位」；無錯誤。
    - 1280，張數 7：網格 7 席 `aria-pressed="true"`；結帳列「A-315、A-188、A-293、A-278、A-101、A-301 等 7 個座位」（依選取順序列出前 6 個），未列出的第 7 席 243 也在網格中為已選；摘要「已選 7 個座位」；無錯誤。
  - (c) 沒有動畫或延遲。兩個寬度：3 次 ArrowRight 後焦點都在第 4 個可選座位（274，與 DOM 順序推算一致），整區只有一個 `tabindex="0"`；End 後焦點在最後一個可選座位（2），按鍵後立即讀取的 scrollY 與 600ms 後相同（390：1817；1280：553），座位 512～540 ≤ 結帳列 top 611。`html` 的 `scroll-behavior` 為 `auto`，座位 `transition-duration: 0s`、`animation-name: none`。
  - (d) 390 寬：未選座位 89px；出現座位清單後 116.5px（3 席與 7 席相同，清單為單行）。4.2 量測中 320 寬為 113px（未選座位，摘要換行）。皆小於 `scroll-margin-bottom` 160px。
- [x] 4.4 更新 `docs/ui-ux-review-2026-10-08.md`：把 U5、U6、U7、U11、A1、A2 標記為「✅ 已修正（purchase-flow-ux）」，並簡述採用的做法；第 6 節「建議處理順序」的第 3 列同步標記。
