> 所有指令都在容器內執行（例如 `docker compose exec web npm run test`），不在本機執行 node／npm。
>
> **AC 測試對應例外**：BW-FLOW-001、BW-SEAT-A11Y-002 的「實際樣式」部分、BW-SEAT-A11Y-007，以及「活動詳情頁在窄螢幕不產生頁面橫向捲動」既有 Scenario 的重新驗證，改用瀏覽器量測取代 CLAUDE.md 要求的單元或整合測試。例外紀錄見 design.md「AC 測試對應例外」。核准狀態：**已核准**（2026-10-10）。
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
> | 窄螢幕不橫向捲動（既有 5 個 Scenario） | 結帳列結構改變後重新驗證 | 瀏覽器量測（斷言 4） | 4.2 |
> | BW-SALES-010／011 的快速選位路徑 | 規則不變，操作路徑改為「自動選位 → 送出訂單」 | Vitest（改寫） | 2.1 |
> | 其餘既有 Scenario（BW-ORDER-*、BW-QUEUE-*、BW-SALES-*、BW-RN-*） | 既有行為不變 | 既有 Vitest，只能改查找方式（例如按鈕文字），不得改變斷言語意 | 3.6 |
>
> 測試名稱沿用 `EventDetailPage.test.ts` 既有的 `ID：情境描述` 格式（例如 `BW-QUEUE-001：…`），不採 CLAUDE.md 的 `MethodName_Scenario_ExpectedResult`。原因：前端元件測試沒有對應的單一方法，同一個檔案混用兩種格式會降低一致性（Rule 11）。

## 1. 量測腳本與基準值

- [ ] 1.0 撰寫量測資料 fixture 腳本 `loadtest/fixtures/purchase-flow-ux-event.js`（design.md 決策 6「量測資料」）。k6 腳本，1 VU、1 iteration，重用 `loadtest/lib/` 的 `http.js`、`tokens.js` 與 `admin-api.js` 的 `getOk`／`assertEventOnSale`／`getSeats`／`getTicketTypes`，不修改 lib 與既有壓測腳本；`loadtest/README.md` 的腳本表格新增一列，並在「清理」段落補上本 fixture 的說明。內容包括：會留下哪些資料（場館、座位圖、活動、票種、2 筆訂單，以及量測時註冊的 `pfux-*` 帳號）、不得與壓測並行（seeder 會覆寫共用的 `tokens.json`）、保留上限與達到上限時的清理方式（見下方「保留上限」）。步驟 1 到 3 須連續執行：token 有效期 30 分鐘，過期時 fixture 會因 401 失敗。
  - **僅限 Development**：沿用既有的兩道保護。seeder 只在 `ASPNETCORE_ENVIRONMENT=Development`，且 DB Host 為 `db` 時執行；沒有 token 檔，fixture 就無法建立資料。k6 的 API 位址寫死為 compose 內部的 `http://api:8080`，沒有 `__ENV` 可以改到其他目標。fixture 不新增其他環境入口。
  - **保留上限**：建立任何資料前，先以 Admin token 呼叫 `GET /api/admin/events`（依 seeder Admin 的主辦方列出，不經過快取），計算標題以 `[PurchaseFlowUx] ` 開頭的活動數量。數量 `>= 6` 時立即 `fail()`，不建立任何資料，並輸出 `PURCHASE_FLOW_UX_RETENTION_LIMIT_REACHED count=<n>`。
    - 6 的理由：一次完整流程最多 2 輪（1.2 與 4.2），上限 6 可容納 2 次因前置失敗而重跑。
    - 失敗的執行同樣計入上限：fixture 中途 `fail()` 時，已建立的場館或活動不回滾，也不自動重試。
    - 達到上限時停止並回報使用者，不自動清理。產品沒有刪除 API，自動清理只能用原生 SQL，違反 CLAUDE.md 的禁令，而且要處理訂單、票券、座位、會員之間的關聯，誤刪風險高。清理由使用者依 `loadtest/README.md`「清理」段落的既有做法處理：用 pgAdmin 依 `[PurchaseFlowUx] ` 前綴與 `pfux-` Email 前綴辨識後手動處理，或重建 dev DB volume。清理後重跑 fixture，上限檢查會重新計數。
    - `pfux-*` 帳號沒有 API 可以計數。每輪只註冊 1 個，數量受活動上限間接限制，不另設上限。
  - **輸出限制**：fixture 的 console 與 summary 只輸出 Id、數量與 HTTP 狀態碼；失敗時允許沿用 `loadtest/lib/http.js` 的 `describeFailure`（method、path、狀態碼與截斷至 300 字元的回應本文，不含 header）。不得輸出 token、`Authorization` header、request 本文或未截斷的回應本文。
  - **執行指令**（每次執行 1.2、4.2 前各跑一次）：
    1. `docker compose exec api dotnet run --project tools/ProjectC.LoadTest.Seeder -- --buyers 2`，必須 exit code 0；
    2. 依 `loadtest/README.md`「每一次執行的固定順序」第 2 步輪詢 api 就緒；
    3. `MSYS_NO_PATHCONV=1 docker compose --profile loadtest run --rm --no-deps k6 run /scripts/fixtures/purchase-flow-ux-event.js`。
  - **建立內容**：場館、座位圖（只有 `A` 區，400 席）、活動（標題 `[PurchaseFlowUx] <UTC 時間>`、`salesStartAtUtc` 為 1 分鐘前、`startAtUtc` 為 24 小時後、不送 `salesEndAtUtc` 與 `isRealNameRequired`）、票種（`A` 座位制；`GA` 計數制，`availableQuantity` 100）。第 1 位買家以 `POST /api/orders` 下單 `A` 區第 1 席並 `POST /api/orders/{id}/confirm`；第 2 位買家下單第 2 席，不付款。
  - **核對**（任一不符就 `fail()`，exit code 非 0）：每個建立與下單請求都回 2xx；`assertEventOnSale` 通過；公開座位 API 的 `A` 區共 400 席，第 1 席為 `Sold`，至少一席為 `Available`；公開票種 API 同時有 `requiresSeat` 為 true 與 false 的票種。第 2 席是否仍為 `Held` 不列入核對（持有期限到期會釋出）。
  - **輸出**：成功時 console 印出 `PURCHASE_FLOW_UX_EVENT_ID=<id>`，作為 1.1 的 `EVENT_ID`。
  - **驗證**：在 dev 環境執行一次，exit code 為 0，並以公開 `GET /api/events/{id}/seats` 手動確認第 1 席為 `Sold`；再將腳本中的座位數暫時改為 399 執行一次，必須因核對失敗而以非 0 結束（證明核對會失敗），確認後改回。上限檢查也要驗證：把腳本中的上限暫時改為「目前 `[PurchaseFlowUx]` 活動數」執行一次，必須輸出 `PURCHASE_FLOW_UX_RETENTION_LIMIT_REACHED` 並以非 0 結束，而且執行前後 `[PurchaseFlowUx]` 活動數相同（證明未建立資料），確認後改回。
- [ ] 1.1 撰寫瀏覽器量測腳本 `openspec/changes/purchase-flow-ux/measure-purchase-flow.js`（design.md 決策 6），隨 change 歸檔，供日後重跑。
  - **執行方式**：依下方「登入／登出前置」建立該輪的登入狀態，再在同源分頁以 claude-in-chrome 的 `javascript_tool` 執行腳本全文。iframe 與主頁同源，共用 localStorage 的登入狀態。
  - **登入／登出前置**（1.2、4.2 每次都照做）：
    - **帳號來源**：不使用 seeder 的壓測買家。它們的密碼是隨機產生、沒有保存，seeder 也只簽 access token，而前端只靠 localStorage 的 `ticketing.refreshToken` 恢復登入，無法注入。每次執行 1.2／4.2 前，由**使用者本人**在前端 `/register` 註冊一個新帳號：Email 為 `pfux-<YYYYMMDDHHmm>@example.test`，密碼由使用者自行決定。新註冊帳號立即啟用，角色為一般會員（`Member.Register`），不會是 Admin。
    - **密碼與 CAPTCHA**：註冊與登入時的密碼、CAPTCHA 答案都由使用者直接在瀏覽器表單輸入，不告訴 Claude，不寫進腳本、任務紀錄或版控。claude-in-chrome 不得代為辨識或填寫 CAPTCHA。
    - **已登入完成判準**：使用者在 `/login` 登入後告知完成，Claude 在主頁以 `javascript_tool` 核對：`localStorage.getItem('ticketing.refreshToken') !== null`；`.buyer-header a[href="/orders"]` 存在；`.member-trigger` 的文字不是空字串；`.buyer-header a[href="/login"]` 不存在。
    - **登出步驟**：已登入一輪結束後，由 Claude 以 claude-in-chrome 在主頁點開 `.member-trigger` 選單，再點「登出」。之後重新載入主頁並核對：`localStorage.getItem('ticketing.refreshToken') === null`；`.buyer-header a[href="/login"]` 存在；`.member-trigger` 不存在。前端的登出是 best-effort：`authStore.logout()` 會吞掉後端登出 API 的錯誤（`web/src/stores/auth.ts`），所以只看 DOM 和 localStorage 無法證明伺服器端已撤銷。因此還要以 claude-in-chrome 的 `read_network_requests` 篩選 `/api/auth/logout`，確認該次點擊產生了 `POST /api/auth/logout`，而且回應狀態為 2xx。點登出前先呼叫一次 `read_network_requests`，讓追蹤涵蓋這次點擊；如果工具取不到這筆請求，同樣視為登出核對失敗。只記錄方法、路徑與狀態碼，不得記錄 request 本文（內含 refresh token）與 header。不得用 `localStorage.removeItem` 取代 UI 登出。
    - **登出時機**：已登入一輪不論量測成功、斷言失敗，或 `javascript_tool` 拋出例外，結束後都必須執行上述 UI 登出與核對。登出核對失敗時（包含沒有 logout 請求或回應非 2xx），整輪量測視為失敗。
    - **敏感資訊界線**（適用 `measure-purchase-flow.js` 與所有前置核對用的 `javascript_tool` 程式碼）：
      - 對 localStorage 只允許一種存取：`localStorage.getItem('ticketing.refreshToken') !== null`，而且必須在頁面內直接算成布林值，不得把 `getItem` 的回傳值存進變數、回傳或輸出。不得列舉或序列化 `localStorage`／`sessionStorage`（例如 `Object.keys`、`JSON.stringify(localStorage)`、迴圈）。
      - 不得讀取 `document.cookie`，不得攔截或包裝 `fetch`／`XMLHttpRequest`，不得讀取任何 `Authorization` header、request payload、密碼或 CAPTCHA 欄位的值。
      - 回傳值、console 輸出與量測 JSON 只能包含布林狀態、寬度、座標、computed style 字串、座位 Id 與無效原因。
      - 例外處理：腳本以 try／catch 包住每個寬度，只回傳 `error.name` 與 `error.message`，不回傳 stack 或物件內容。因為腳本本身不讀取憑證，訊息中也不會有憑證。`javascript_tool` 本身失敗時，只在任務紀錄寫下失敗的步驟與錯誤訊息。
      - 截圖只能拍活動詳情頁與登入、登出畫面，不得開啟或拍攝 DevTools 的 Application／Network 面板。
      - **可驗證的檢查**：1.1 完成時執行 `grep -nE "localStorage|sessionStorage|document\.cookie|fetch|XMLHttpRequest|Authorization|password|captcha" openspec/changes/purchase-flow-ux/measure-purchase-flow.js`。結果只能出現上述那一行布林判定，出現其他行就修正後重跑。
      - 為什麼可以用 `javascript_tool`：Claude 只會收到腳本的回傳值，頁面的其他內容不會傳回。只要腳本符合上面的限制（可用 grep 驗證），回傳內容就不會有憑證，所以不需要改用其他 runner。
    - **證據**：每次核對都輸出一個 JSON（`round`、`checkedAtUtc`、上述每項核對的結果、`passed`），貼在 1.2／4.2 下方。refresh token 只輸出 `hasRefreshToken` 布林值，不得輸出 token 本體；這份 JSON 會隨 change 歸檔進版控。任務紀錄只寫帳號 Email，不寫密碼。
    - **前置失敗**：任何一項核對不符（包含 UI 登出失敗、logout 請求缺少或非 2xx、註冊或登入失敗），就停止並回報使用者，附上核對 JSON，不執行該輪量測。已執行的量測結果也不得視為通過。
    - **量測腳本內的判定**：每個寬度都在 iframe 內用同一組條件判定 `isAuthenticated`：四項都符合已登入判準時為 `true`，都符合登出判準時為 `false`；兩組都不符（例如 refresh 失敗但 token 還留著）時，該寬度標記為無效量測。
  - **設定常數**：`EVENT_ID`（取自 1.0 fixture 的輸出；目標活動：第一個分區至少 400 個座位，其中至少一個 `Sold`／`Held`、一個 `Available`；同時有座位制與計數票種；販售中；不需實名；未啟用熱門搶購模式。400 的推算見 design.md 決策 6）；`BASELINE`（1.2 設為 `true`，4.2 設為 `false`）；`EXPECT_AUTHENTICATED`（宣告這一輪的登入狀態）。
  - **兩輪**：已登入一輪（`EXPECT_AUTHENTICATED = true`）量全部寬度與斷言 1 至 4；未登入一輪（`false`，先登出）只量 320／390／720 的斷言 4，略過需要結帳列與計數購票列的項目（對應窄螢幕需求中 3 個以未登入為前提的 Scenario）。
  - **量測方式**：同源 iframe，高度 700px，寬度依序設為 320／390／720／721／800／1280，`scrollbar-width: none`。每個寬度輸出：`W`、`pathname`、`isAuthenticated`、`seatCount`、`documentScrollWidth`、`submitButtonRect`、`lastSeatTop`、`checkoutBarRect`、`lastAvailableSeatRectBefore`、`focusedSeatId`、`expectedFocusedSeatId`、`focusedSeatRect`、標題／票價表／計數購票列／結帳列的 `rect.right`、區域隨選按鈕與分區選單的 `rect.top`，以及 `.seat-btn.sold`、第一個 Available 座位（`.seat-btn:not(.sold)`）、`.seat-swatch--unavailable` 的 computed `text-decoration-line` 與 `border-top-style`（基準狀態下色塊不存在，輸出 `null`），再加上每項斷言的結果、`valid`、`invalidReasons`。
  - **斷言**：
    - 斷言 1：捲到第一個 `.zone-block` 頂端後，送出按鈕的 `top >= 0` 且 `bottom <= 700`，而且 `lastSeatTop > 700`；
    - 斷言 2（只在 390 與 1280 執行，對應 BW-SEAT-A11Y-007）：捲回 `scrollY = 0`，依 DOM 順序算出第一個分區最後一個 `Available` 座位（不依賴實作才有的屬性，基準狀態也能用），它一開始的 `top` 必須大於 700，否則這個寬度的量測無效（第一個分區座位不夠多，回報使用者，不得計為通過）。在第一個可選座位上 `focus({ preventScroll: true })`（避免第一次聚焦就改變捲動位置），再 `dispatchEvent` 送出 `End` keydown，等待 300ms 後，`iframe.contentDocument.activeElement` **就是**這個座位，而且它的 `rect.top >= 0`、`rect.bottom <= checkoutBarRect.top`；
    - 斷言 3：`.seat-btn.sold` 的 `text-decoration-line` 包含 `line-through`，且 `border-top-style === 'dashed'`；色塊的這兩個值與 `.seat-btn.sold` 相同；Available 座位的 `text-decoration-line` 不包含 `line-through`，且 `border-top-style === 'solid'`；
    - 斷言 4：`documentScrollWidth <= W`；`W ≤ 720` 時 grid 只有一欄，`W ≥ 721` 時第一欄寬 320px、資訊欄寬 320px、購票欄左緣位於資訊欄右緣的右側；活動標題、票價表、計數購票列、結帳列的 `rect.right <= W`；`W = 320` 時區域隨選按鈕的 `top` 大於分區選單的 `top`（換到下一行）。
  - **無效量測**（任一成立就標記 `valid: false`，並輸出原因）：`pathname` 不是 `/events/{EVENT_ID}`；登入狀態無法判定，或與 `EXPECT_AUTHENTICATED` 不符；第一個分區座位數 `< 400`；第一個分區找不到 `.seat-btn.sold` 或 Available 座位；找不到 `.zone-block`、區域隨選按鈕（以 `.quick-pick button` 查找，基準與新版的按鈕文字不同）；已登入一輪找不到送出按鈕、計數購票列，或 `BASELINE` 為 `false` 時找不到 `.checkout-bar`（基準狀態改量 `.summary`）；任何用於比較的座標不是有限數值，或元素寬度不大於 0。無效量測不得計為斷言通過。
  - **輸出**：一個 JSON 陣列。1.2 與 4.2 把完整 JSON 貼在該任務下方；內容太長時，比照 admin-mobile-nav，比對 SHA-256 確認內容一致。
- [ ] 1.2 修改前執行腳本（`BASELINE = true`，已登入與未登入兩輪；每輪依 1.1「登入／登出前置」建立並核對登入狀態），在本任務下方記錄基準值與前置核對 JSON。執行前先跑 1.0 的 fixture，使用它輸出的 `EVENT_ID`；fixture 失敗時停止並回報使用者，不得改用 dev DB 中其他活動。**通過條件**：用來證明檢查本身會失敗。兩輪的每個寬度都要 `valid: true`，並逐輪逐項符合：
  - 已登入一輪：
    - BW-FLOW-001 的斷言 1，以下 5 個寬度逐一記錄，每個都必須 `valid: true` 而且斷言 1 失敗：320、390、720、721、1280。失敗原因必須是「送出按鈕的 `bottom > 700`（按鈕在視窗下緣之外），且 `lastSeatTop > 700`（頁面確實比視窗長）」。斷言 1 失敗只要不是這個原因（例如 `lastSeatTop <= 700`），就不算符合基準預期。800 是額外的回歸寬度，斷言 1 同樣必須以相同原因失敗，但不計入 BW-FLOW-001；
    - 斷言 3 在全部寬度失敗（`.seat-btn.sold` 沒有刪除線）；
    - 390 與 1280 的斷言 2 失敗（沒有 End 鍵處理，焦點停在第一個可選座位）；
    - 全部寬度的斷言 4 通過（mobile-layout-p0-fixes 已修正）；
  - 未登入一輪：320／390／720 的斷言 4 通過。
  - 在本任務下方，以 4.2 的驗收表格式逐列記錄基準結果（「預期」欄填失敗或通過）。

  任何一項不符時，停止並回報使用者。

## 2. 行為測試（先寫測試、確認失敗）

> 寫在 `web/src/pages/buyer/EventDetailPage.test.ts`，沿用既有的 `mountPage()`、`buildSeat()`、mock API 與 `flushPromises`。需要斷言焦點的測試，一律以 `attachTo: document.body` 掛載，並在測試結束時 `unmount`。`beforeEach` 以 `vi.fn()` 補上 `Element.prototype.scrollIntoView`（jsdom 沒有實作），`afterEach` 還原。錯誤位置以 `.checkout-bar .action-error` 與 `.page-error` 的存在與文字判定（design.md 決策 6）：一律同時斷言「出現在應有的位置」**和**「不出現在另一個位置」。完成本節後執行 `docker compose exec web npm run test -- src/pages/buyer/EventDetailPage.test.ts`，確認新測試是因為功能尚未實作而失敗（不是語法或匯入錯誤），並記錄失敗訊息。

- [ ] 2.1 [BW-QUICK-001～008] 改寫區域隨選測試。`quickPickButton()` 改為以「自動選位」查找；`EventDetailPage.test.ts:757` 斷言按鈕「不存在」的字串也改為「自動選位」，否則改名後這個負向斷言永遠成立、無法失敗。
  - 改寫既有的 2 個販售期間測試（`:1493` BW-SALES-010、`:1527` BW-SALES-011 的快速選位路徑）：改為先點「自動選位」、斷言 `placeOrder` 未被呼叫，再點「送出訂單」，斷言 `placeOrder` 呼叫一次、抽出的座位仍保留在已選清單，SalesNotOpen 的訊息出現在 `.checkout-bar .action-error`；SalesClosed 維持原本斷言的頁面狀態。這兩個測試的規則（主 spec BW-SALES-010／011）不變，只是操作路徑改變。
  - 改寫既有的 3 個測試：「全部區域…直接送出訂單成功」（`:238`）、「指定單一分區…」（`:261`）、「使用區域隨選時一併送出已輸入的計數購買」（`:603`）。改寫後斷言：點選「自動選位」後 `placeOrder` **未被呼叫**；已選座位數與結帳列清單符合抽出的數量與分區；接著點「送出訂單」才呼叫一次 `placeOrder`，請求內容包含抽出的座位（以及計數項目），並導向訂單結果頁。
  - 新增 BW-QUICK-003：先自動選位，再手動取消其中一個座位，送出的請求不包含該座位。
  - 「分區有可售座位但沒有對應的座位制票種…」（`:287`）原本就斷言 `placeOrder` 未被呼叫，不屬於直接下單測試：只更新按鈕查找方式，另外加一個斷言，抽選被擋下後摘要仍為「已選 0 個座位」。
  - 既有的「張數超過…」「未登入…」「計數額度減少」「純計數不出現」測試只更新按鈕查找方式，斷言不變。張數超過的錯誤位置由 2.3 驗證。
- [ ] 2.2 [BW-FLOW-002、005] 已登入，依序點選 7 個 Available 座位：`.checkout-seat-list` 的文字依點選順序列出前 6 個 `分區-號碼`，並包含「等 7 個座位」；取消第 1 個座位後，列出剩下的 6 個，且不包含「等」字。沒有已選座位時，`.checkout-seat-list` 不存在。摘要元素帶有 `aria-live="polite"`，而且點選後文字包含「已選 1 個座位」。
- [ ] 2.3 [BW-FLOW-003] 以參數化測試（`it.each`）涵蓋 design.md 決策 1 中歸屬為「操作」的每個來源。每一案都斷言：訊息出現在 `.checkout-bar .action-error`，`.page-error` 不存在，`.action-error` 帶有 `role="alert"`。來源如下：
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
- [ ] 2.4 [BW-FLOW-004、006]
  - (a) `getEvents` 失敗時，`.page-error` 顯示載入失敗訊息、帶有 `role="alert"`，而且 `.checkout-bar .action-error` 不存在；
  - (a2) 熱門搶購模式下，`getQueueStatus` 查詢失敗，以及 `joinQueue` 回傳一般失敗，各一案：訊息出現在 `.page-error`；
  - (b) 熱門搶購模式已入場（`Admitted`）的買家送出時回傳 `403 QueueAdmissionRequired`，重新查詢的結果為 `Waiting`：`.page-error` 顯示排隊資格訊息、`.checkout-bar` 不存在；
  - (c) 熱門搶購模式下，狀態為 `NotJoined` 與 `Waiting` 各一案：`.checkout-bar` 不存在、沒有「送出訂單」按鈕。
- [ ] 2.5 [BW-SEAT-A11Y-001、002] 活動有座位時，`.seat-legend` 恰好出現一次，位於第一個 `.zone-block` 內、座位網格之前，依序包含「可選」「已選」「不可選」三項文字，每項各有一個帶 `aria-hidden="true"` 的 `.seat-swatch`。「不可選」色塊帶 `.seat-swatch--unavailable`。`Sold` 與 `Held` 座位帶 `.sold` class，`Available` 座位沒有。實際的刪除線與虛線樣式由量測斷言 3 驗證。
- [ ] 2.6 [BW-SEAT-A11Y-003、004]
  - (a) 已登入，A 區 12（Available）、13（Sold）、14（Held），另有一個未知狀態 `Unknown`：斷言各座位的 `aria-label` 與 `title` 分別為「A 區 12 號，可選」「A 區 13 號，已售出」「A 區 14 號，保留中」「…，不可選」，12 號的 `aria-pressed` 為 `"false"`；點選 12 號後，它的 `aria-label` 與 `title` 變為「A 區 12 號，已選」，`aria-pressed` 為 `"true"`。
  - (b) 未登入，點選 Sold 座位：該座位的 `aria-disabled` 為 `"true"`、沒有 `disabled` 屬性，`router.push` 以 `/login` 與 `redirect` 呼叫。
  - (c) 已登入，點選 Sold 座位：不加入已選清單。
- [ ] 2.7 [BW-SEAT-A11Y-005、006]
  - (a) A 區 = [Sold, Available, Available, Sold, Available]，B 區全部 Sold：A 區只有第 2 個座位的 `tabindex` 為 `"0"`，其餘為 `"-1"`；B 區所有座位都是 `"-1"`。
  - (b) 聚焦 A 區第 2 個座位，每一步都在**目前焦點的座位**上觸發 keydown（事件冒泡到 `.seat-grid`，與真實操作一致），依序為 `ArrowRight`、`ArrowRight`、`ArrowLeft`、`End`、`ArrowRight`、`Home`、`ArrowLeft`、`ArrowDown`、`ArrowUp`：`document.activeElement` 依序為第 3、5、3、5、5、2、2、3、2 個座位。第 2、3 步跨過第 4 個 Sold 座位；記錄每一步的焦點，斷言第 1、4 個（Sold）座位從未取得焦點。每一步都斷言整個 A 區恰有一個 `tabindex="0"`，而且就是焦點座位；`scrollIntoView` 以 `{ block: 'nearest' }` 被呼叫；這 9 個 keydown 事件的 `defaultPrevented` 為 `true`；`Tab`、`Enter`、`Space`，以及 `altKey: true` 的 `ArrowLeft`、`ctrlKey: true` 的 `End` 的 `defaultPrevented` 為 `false`，而且焦點不動。
  - (c) 滑鼠點選第 5 個座位後，Tab 停留點變成第 5 個。
  - (d) 把記錄的座位（第 5 個）改為 Sold，重新載入資料（模擬下單失敗刷新）後，Tab 停留點回到第 2 個。

## 3. 實作

> 開始前確認：design.md 的 AC 例外已核准，且第 2 節的測試已確認失敗。

- [ ] 3.1 錯誤 state 拆分（design.md 決策 1）：新增 `actionErrorMessage`，依決策 1 的表格逐一替換 `errorMessage.value = …` 的指派處。頂端 alert 加上 class `page-error`。`handleQuickPick` 與 `handleSubmit` 開頭同時清除 `errorMessage` 與 `actionErrorMessage`；`loadData` 與 `handleJoinQueue` 開頭只清除 `errorMessage`。一般下單失敗時，維持「先 `loadData()`、再設定訊息」的順序，並更新該段註解，說明現在兩則訊息會並存。
- [ ] 3.2 結帳列（design.md 決策 2）：`.summary` 改為 `.checkout-bar`，內含 `.action-error` alert、`.checkout-seat-list`（以 computed 產生前 6 個座位與「等 N 個座位」）、帶 `aria-live="polite"` 的摘要，以及「送出訂單」按鈕。CSS 設定 sticky、不透明背景與上邊框；座位加上 `scroll-margin-bottom: 160px`。
- [ ] 3.3 區域隨選只填入座位（design.md 決策 3）：移除 `await handleSubmit()`，改為同步函式；按鈕改為「自動選位」，移除 `:loading="submitting"`。更新 `handleQuickPick` 上方說明「一鍵購票路徑」的註解，改為說明為什麼只填入（U11：送出前要讓買家看到抽中的座位）。
- [ ] 3.4 座位 `aria-*`、圖例與樣式（design.md 決策 4）：
  - 新增 `getSeatLabel(seat)`；
  - 座位綁定 `aria-pressed`、`aria-label`、`title`、`aria-disabled`；
  - `.seat-grid` 加上 `role="group"` 與 `aria-label`；
  - 第一個 `.zone-block` 內加入 `.seat-legend`；
  - `.seat-btn.sold, .seat-swatch--unavailable` 共用刪除線與虛線規則；
  - hover 改放在 `@media (hover: hover) and (pointer: fine)` 內。
- [ ] 3.5 roving tabindex（design.md 決策 5）：
  - 新增 `focusedSeatIdByZone` 與 `getTabStopSeatId(zoneCode, zoneSeats)`；
  - 座位綁定 `:tabindex`、`data-seat-id`、`@focus`，並在 `@click` 時更新記錄；
  - `.seat-grid` 綁定 `@keydown`，只處理 6 個按鍵，以 `focus({ preventScroll: true })` 加上 `scrollIntoView({ block: 'nearest' })` 移動焦點。
- [ ] 3.6 執行 `docker compose exec web npm run test -- src/pages/buyer/EventDetailPage.test.ts`：第 2 節的新測試全部通過；既有測試除了 2.1 列出的改寫（3 個區域隨選直接下單測試、BW-SALES-010／011 的 2 個快速選位路徑測試、`:287` 新增的摘要斷言、`:757` 的負向斷言）之外，斷言語意不變，全部通過。如果有既有測試因為查找方式改變而失敗，只能修改查找方式；如果需要修改斷言內容，停止並回報使用者。

## 4. 驗證

- [ ] 4.1 執行完整前端測試 `docker compose exec web npm run test` 與型別檢查 `docker compose exec web npm run build`。記錄通過數；如果有失敗，記錄失敗的測試名稱，並判斷與本 change 是否有關（先在 master 重跑同一個測試確認），不得略過不報。
- [ ] 4.2 先跑 1.0 的 fixture，並依 1.1「登入／登出前置」建立登入狀態，再執行量測腳本（`BASELINE = false`），在本任務下方記錄完整結果與前置核對 JSON。**通過條件**：已登入與未登入兩輪的每個寬度都 `valid: true`，而且該輪量測的斷言全部通過。此外，在本任務下方以下表逐列記錄 `valid` 與斷言結果；缺任何一列或任何一列未通過，本任務就未完成：

  | AC | 輪次 | 寬度 | 斷言 |
  | --- | --- | --- | --- |
  | BW-FLOW-001 | 已登入 | 320、390、720、721、1280（各一列，共 5 列） | 斷言 1 |
  | （額外回歸，不計入 BW-FLOW-001） | 已登入 | 800 | 斷言 1 |
  | BW-SEAT-A11Y-002（實際樣式） | 已登入 | 320、390、720、721、800、1280 | 斷言 3 |
  | BW-SEAT-A11Y-007 | 已登入 | 390、1280 | 斷言 2 |
  | 窄螢幕「390px 寬未登入瀏覽」 | 未登入 | 390 | 斷言 4 |
  | 窄螢幕「320px 寬未登入瀏覽」 | 未登入 | 320 | 斷言 4 |
  | 窄螢幕「已登入買家在窄螢幕瀏覽含計數票種」 | 已登入 | 390、320 | 斷言 4 |
  | 窄螢幕「桌面與平板寬度維持雙欄版面」 | 已登入 | 721、800、1280 | 斷言 4 |
  | 窄螢幕「斷點上限寬度維持單欄」 | 未登入 | 720 | 斷言 4 |

  任何一項失敗時，停止並回報使用者，不得放寬斷言。
- [ ] 4.3 以 claude-in-chrome 在 390px（同源 iframe）與 1280px 手動檢查，並記錄結果：
  - (a) 用真實 Tab 鍵進入第一個分區時，焦點座位是否被結帳列遮住；如果被遮住，依 design.md Risks 回報使用者決定，不自行修改；
  - (b) 自動選位後，結帳列清單與網格上的選取狀態一致；
  - (c) 方向鍵移動時沒有動畫或延遲；
  - (d) 結帳列在 390px 寬時的實際高度。
- [ ] 4.4 更新 `docs/ui-ux-review-2026-10-08.md`：把 U5、U6、U7、U11、A1、A2 標記為「✅ 已修正（purchase-flow-ux）」，並簡述採用的做法；第 6 節「建議處理順序」的第 3 列同步標記。
