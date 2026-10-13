> **AC 測試對應例外**：AWU-MOBILE-NAV-001、002（已核准 2026-10-09）、AWU-AREA-LAYOUT-001、AWU-AREA-NAV-OVERFLOW-001 以瀏覽器量測取代單元或整合測試，例外紀錄見 design.md「AC 測試對應例外」。後兩條核准狀態：**已核准（2026-10-13，核准人 RD-Hank）**。
>
> | AC | 驗證方式 | 任務 |
> | --- | --- | --- |
> | AWU-GUARD-001～005、007、008 | Vitest（`router/index.test.ts`） | 2.1 |
> | AWU-REVIEW-001～003 | Vitest（既有審核頁測試，路徑更新） | 2.2 |
> | AWU-LIST-001～005 | Vitest（`MyOrganizersPage.test.ts`） | 5.1 |
> | AWU-NAV-001～003 | Vitest（`AdminLayout.test.ts`、既有活動列表測試） | 4.1 |
> | AWU-MOBILE-NAV-001、002 | 瀏覽器量測 | 1.2、7.2 |
> | AWU-MOBILE-NAV-003～007 | Vitest（`AdminLayout.test.ts`） | 4.1 |
> | AWU-AREA-SWITCH-001～004 | Vitest（`AreaSwitcher.test.ts`、三個版面測試） | 3.1、4.1、4.3、4.5 |
> | AWU-AREA-SWITCH-005 | Vitest（`AreaSwitcher.test.ts`，Teleport DOM 驗證） | 3.1 |
> | AWU-AREA-LAYOUT-001 | 瀏覽器量測 | 7.2 |
> | AWU-PLATFORM-NAV-001 | Vitest（`PlatformLayout.test.ts`） | 4.5 |
> | AWU-AREA-NAV-OVERFLOW-001 | 瀏覽器量測 | 1.2、7.2 |
> | BW-ADMIN-ENTRY-001～003 | Vitest（`BuyerLayout.test.ts`） | 4.3 |
> | WCC-001～013（「頁面底」擴大） | Vitest（`morandi.contrast.test.ts`） | 6.1 |
> | BS-001、003、010 | Vitest（`build-checks/bundle-splitting.test.ts`、既有測試） | 7.1 |
> | BS-002、004 | Vitest（`build-checks/bundle-splitting.test.ts`） | 2.4、7.1 |
> | BS-005、014 | Vitest（`lazyRoutes.test.ts`） | 2.3 |

## 1. 量測腳本與基準值

- [ ] 1.1 以 archive 的 `measure-admin-nav.js` 為基礎，撰寫 `openspec/changes/web-area-separation/measure-area-nav.js`（隨 change 歸檔）。執行方式比照 admin-mobile-nav：使用者登入後，在前端 dev server 的同源分頁以 claude-in-chrome `javascript_tool` 執行；同源 iframe 依序設定各 W，iframe 內 `scrollbar-width: none`；腳本開頭的常數 `AREA`（`'organizer'`／`'platform'`／`'buyer'`）、`BASELINE`、`INJECT_LONG_TEXT`（只對 `'organizer'` 有效）切換量測內容。腳本輸出 JSON 陣列，每筆對應一組（`AREA`、W、目標路徑、是否注入），欄位與判定一律依下列定義，不得只靠「沿用原腳本」：

  **量測組合**

  | `AREA` | 目標路徑 | W | 導覽根元素 | 版面根元素 |
  | --- | --- | --- | --- | --- |
  | `'organizer'` | `/admin/redeem`、`/admin/venues`（各量一般名稱與 100 字元注入兩次） | 320、390、720、721、800、1280 | `.admin-nav` | `.admin-layout` |
  | `'platform'` | `/platform/organizers` | 320、390、720、721、800、1280 | `.platform-nav` | `.platform-layout` |
  | `'buyer'` | `/` | 320、390、1280 | `.buyer-header` | `.buyer-layout` |

  **必要元素**（selector 一律限定在導覽根元素內查找，輸出每個的 `count`、`visible`、`rect`〔`getBoundingClientRect()` 的 left／right／width／height〕）

  | `AREA` | 欄位名 | selector | 適用 AC |
  | --- | --- | --- | --- |
  | 三者 | `areaSwitcher` | `.area-switcher` | MOBILE-NAV-001、002、AREA-NAV-OVERFLOW-001 |
  | `'organizer'` | `menuButton` | `[aria-label="開啟後台選單"]` | MOBILE-NAV-001、002 |
  | `'organizer'` | `horizontalMenu` | `.admin-nav-menu` | MOBILE-NAV-001、002 |
  | `'organizer'` | `topBarLogoutButton` | `.admin-nav-logout` | MOBILE-NAV-001、002 |
  | `'organizer'` | `organizerIndicator` | `.organizer-indicator`（另輸出文字與文字長度） | MOBILE-NAV-001、002 |
  | `'platform'` | `platformMenu` | `.platform-nav-menu` | AREA-NAV-OVERFLOW-001 |
  | `'platform'` | `platformLogoutButton` | `.platform-nav-logout` | AREA-NAV-OVERFLOW-001 |
  | `'buyer'` | `brand`、`buyerNav`、`buyerActions` | `.brand`、`.buyer-nav`、`.buyer-actions` | AREA-NAV-OVERFLOW-001 |

  「可見」定義：`getComputedStyle` 的 `display` 不是 `none`、`visibility` 不是 `hidden`，且 `rect.width > 0`、`rect.height > 0`。

  **幾何輸出**：`clientWidth`（`documentElement.clientWidth`）、`documentScrollWidth`（`documentElement.scrollWidth`）、`navRootScrollWidth`、`navRootClientWidth`、`navMaxRight`（導覽根元素及其所有可見子孫的 `max(rect.right, rect.left + scrollWidth)`；`.organizer-indicator` 內的元素只取 `rect.right`，理由見 admin-mobile-nav 4.2 紀錄）、`overflowingElements`（可見子孫中 `scrollWidth > clientWidth` 者，排除 `.organizer-indicator` 內元素），以及 `layoutBackground`、`navBackground`、`navBorderTop`（定義見 7.2）。

  **每筆輸出 `valid`／`invalidReasons`（量測前提，任一成立即無效）**：`clientWidth` ≠ W；`location.pathname` ≠ 目標路徑；導覽根元素或版面根元素命中數不是 1；任何量測值不是有限數值；CSS 變數解析為空字串；`'organizer'` 注入時 `.organizer-indicator` 命中數不是 1 或文字長度不是 100。

  **每筆輸出 `pass`／`failReasons`（AC 判定，任一成立即不通過；`valid` 為 false 時 `pass` 一律為 false）**：
  - 共同：`navMaxRight` > W；`overflowingElements` 非空；`navRootScrollWidth` > `navRootClientWidth` 或 > W；`areaSwitcher.count` ≠ 1 或不可見（`BASELINE = true` 時此項只輸出不判定）；上表該 `AREA` 的任一必要元素 `count` ≠ 1（`BASELINE = true` 時排除 `areaSwitcher`，見下方 BASELINE 條款）。
  - `'organizer'`、W ≤ 720：`menuButton` 不可見，或 `rect.width` < 44、`rect.height` < 44；`horizontalMenu` 或 `topBarLogoutButton` 可見；目標為 `/admin/redeem` 時 `documentScrollWidth` > W。
  - `'organizer'`、W ≥ 721：`menuButton` 可見；`horizontalMenu` 或 `topBarLogoutButton` 不可見。
  - `'organizer'` 一律：`organizerIndicator` 不可見。
  - `'platform'`：`platformMenu` 或 `platformLogoutButton` 不可見。
  - `'buyer'`：`brand`、`buyerNav`、`buyerActions` 任一不可見。
  - `BASELINE = true` 時，`'organizer'` 的選單按鈕與 `.admin-nav-logout` 規則比照 admin-mobile-nav 只輸出不判定（修改前現況已符合 admin-mobile-nav，實際上會通過）；`'platform'` 不量（修改前不存在）；`'buyer'` 不檢查 `areaSwitcher`。
  - `documentScrollWidth` 只在 `'organizer'`、`/admin/redeem`、W ≤ 720 時判定（AWU-MOBILE-NAV-001 原文）；其餘組合（含 `/admin/redeem` W ≥ 721、`/admin/venues`、`/platform/organizers`、`/`）只輸出不判定（`/admin/venues` 的表格溢出 R2 不在範圍內）。

  腳本須能失敗的自我檢查：在 1.2 之後、修改之前，以 `'buyer'` W = 320 暫時把 `.area-switcher` 規則設為必判（即 `BASELINE = false`）執行一次，確認因 `areaSwitcher.count` = 0 得到 `pass: false`；再以 `'buyer'` W = 320 在 `.buyer-header` 內暫時注入一個 `width: 2000px` 的元素執行一次，確認因 `navMaxRight` > W 得到 `pass: false`、`failReasons` 指出該元素；兩次都記錄輸出，注入只存在於 iframe 內、不修改原始碼
- [ ] 1.2 修改前請使用者以 Admin 帳號手動登入（含驗證碼）並切換到一個 Approved Organizer，執行 `AREA = 'organizer'`（含注入與不注入）與 `'buyer'` 的基準量測（`BASELINE = true`），在本節記錄完整 JSON 與 1.1 的自我檢查輸出。基準不要求重現溢出；**通過條件**：所有紀錄 `valid: true`；並記錄會員前台 W = 320 時 `W - navMaxRight`（新增切換鈕前的餘裕）。量測無效時停止並回報

## 2. 路由

- [ ] 2.1 先在 `router/index.test.ts` 新增／修改測試（應先失敗）：AWU-GUARD-001 涵蓋 `/platform/organizers`；AWU-GUARD-004 對新舊兩個網址 × 已切換／未切換 Organizer 共 4 種組合各斷言一次；AWU-GUARD-005 改用新網址；新增 AWU-GUARD-008（`/admin/organizers` 與 `/platform` 最終路由名稱皆為 `platform-organizers`）。再修改 `router/index.ts`：新增 `/platform`（`PlatformLayout` 動態 import、`''` redirect、`organizers` 名稱 `platform-organizers`、meta `{ requiresAdmin: true }`），`/admin` 下的 `organizers` 改為 redirect；`beforeEach` 不修改。全部測試通過
- [ ] 2.2 審核頁既有測試與程式中所有指向 `/admin/organizers` 或 `admin-organizers` 路由名稱的地方改為新路由（已知需改：`App.test.ts` 的 `it.each` 路徑清單、`AdminLayout.test.ts` 中審核相關測試；以 `grep -rn "admin-organizers\|/admin/organizers" web/src` 確認只剩 redirect 定義與 AWU-GUARD-004／008 的測試）；AWU-REVIEW-001～003 測試通過
- [ ] 2.3 `lazyRoutes.test.ts` 依 BS-004／005／014 更新：延遲載入數 20、`EXPECTED_ROUTE_TREE` 依 spec 的預期樹更新、元件對照表 21 筆（含 `PlatformLayout`），對照表筆數斷言等於有元件的 route 數；通過
- [ ] 2.4 `web/build-checks/bundle-splitting.test.ts`：BS-004 的動態 import 數與不同 chunk 數由 19 改為 20，並斷言取出的路徑包含 `src/layouts/PlatformLayout.vue`；BS-002 的排除清單加入 `src/layouts/PlatformLayout.vue`（比照 `ADMIN_LAYOUT_MODULE` 新增常數）。在 7.1 執行通過

## 3. 色票與區域切換元件

- [ ] 3.1 先寫 `web/src/components/AreaSwitcher.test.ts`（應先失敗）：每支測試都沿用下方 005 的步驟 1、2、6 開啟與清理 dropdown（步驟 1 的 Email 改用一般值），涵蓋 AWU-AREA-SWITCH-001（Admin、`current="organizer"`：項目順序、文字；目前區域項目含 `is-disabled` class 且 `aria-disabled="true"`，其他項目不含）、002（非 Admin、`current="buyer"`：項目恰為 3 項且順序為登入帳號、會員前台、主辦方後台，因此不含「平台管理」；不得只做「找不到平台管理」的否定斷言）、003（點選三個項目分別呼叫 `router.push` 至 `/platform`、`/admin`、`/`，最終路徑由 4.x 的版面測試以真實 router 驗證）。AWU-AREA-SWITCH-005 依下列步驟獨立寫一支測試：
  1. 比照 `BuyerLayout.test.ts` 同時設定 `authStore.accessToken` 與 `authStore.member`（`isAuthenticated` 需兩者），`member.email` 設為 `'<b>x</b>@example.com'`，以 `attachTo: document.body` 掛載 `AreaSwitcher`；
  2. 先斷言 `document.body` 內 `.area-switcher-popper`（`el-dropdown` 的 `popper-class`）命中數為 1 且 `style.display` 為 `none`（尚未開啟）；點擊觸發按鈕 `.area-switcher-trigger` 後，以 `vi.waitFor` 輪詢直到該 popper 命中數為 1 且 `style.display !== 'none'`（Element Plus 以 `setTimeout` 開啟，`flushPromises` 不足以等到）；
  3. 在該 popper 內查找登入帳號項目 `.area-switcher-account`（掛在第一個 `el-dropdown-item` 上的 class），斷言命中數為 1；
  4. 斷言該項目的 `textContent` 包含原始字串 `<b>x</b>@example.com`；
  5. 斷言 `.area-switcher-popper` 整個 subtree 內 `b` 元素命中數為 0；
  6. 測試結束時 `unmount`，避免 Teleport 的 popper 殘留在 `document.body` 影響其他測試的命中數。

  能否失敗的確認：實作完成後，暫時把帳號項目改為 `v-html` 綁定 Email 執行此測試，確認步驟 4 或 5 失敗，記錄失敗輸出後還原。再實作 `AreaSwitcher.vue`（決策 2；`area-switcher` class 掛在元件根元素，也就是 `el-dropdown` 的可見觸發區域上，供 1.1 以根元素可見性代表觸發按鈕可見，3.1 斷言該根元素含觸發按鈕 `.area-switcher-trigger`；`el-dropdown` 設 `popper-class="area-switcher-popper"`，登入帳號項目加 class `area-switcher-account`；Email 以文字插值呈現）；通過
- [ ] 3.2 `morandi.css` 新增 `--color-bg-organizer`、`--color-bg-platform`、`--color-area-organizer`、`--color-area-platform`（決策 3 的數值）

## 4. 版面

- [ ] 4.1 先改 `AdminLayout.test.ts`（應先失敗）：AWU-NAV-002（Admin 選單恰為 4 項、無「主辦方審核」）、刪除 AWU-NAV-004 的測試並在測試檔註記原因（spec 已移除該 Scenario）、AWU-MOBILE-NAV-003（兩種角色抽屜都恰為 4 項）、AWU-MOBILE-NAV-004（移除第三種情境）、AWU-AREA-SWITCH-001（導覽列有一個 `.area-switcher`，觸發文字含「主辦方後台」）、AWU-AREA-SWITCH-002（非 Admin 在主辦方後台展開選單，項目恰為 3 項且含「會員前台」「主辦方後台」，不含「平台管理」）；展開切換選單一律沿用 3.1 中 005 的步驟 1、2、6；AWU-NAV-001、003、MOBILE-NAV-005～007 既有測試不放寬。再修改 `AdminLayout.vue`：`menuItems` 移除審核項、加入 `AreaSwitcher current="organizer"`、根元素與導覽列套用決策 3 的樣式；通過
- [ ] 4.2 （實作檢查，非 AC 驗證）確認 4.1 修改後窄螢幕抽屜與水平選單仍共用同一份 `menuItems`（不另寫一份清單），以 `grep` 記錄結果
- [ ] 4.3 先改 `BuyerLayout.test.ts`（應先失敗）：BW-ADMIN-ENTRY-001（Admin 未切換，經切換選單點「平台管理」後以真實 router 最終路由名稱為 `platform-organizers`）、002（非 Admin 切換選單恰為 3 項且含「會員前台」「主辦方後台」，不含「平台管理」）、003（兩種角色的會員下拉選單無「主辦方審核」、有「我的主辦方」）、AWU-AREA-SWITCH-002（會員前台順序）、AWU-AREA-SWITCH-004（未登入無 `.area-switcher`，同時斷言「登入」連結存在，確認版面已渲染）；展開切換選單一律沿用 3.1 中 005 的步驟 1、2、6。再修改 `BuyerLayout.vue`：移除審核項、已登入時加入 `AreaSwitcher current="buyer"`、根元素 `.buyer-layout` 設 `min-height: 100svh` 與 `background: var(--color-bg)`（與 `body` 同色，外觀不變，供 AWU-AREA-LAYOUT-001 量測）；通過
- [ ] 4.4 若 7.2 的會員前台量測在 W = 320 溢出，依 design.md Risks 調整切換鈕窄螢幕呈現；若需要改 spec 文字，先停下回報使用者
- [ ] 4.5 先寫 `web/src/layouts/PlatformLayout.test.ts`（應先失敗）：AWU-PLATFORM-NAV-001（一個 `.area-switcher` 觸發文字含「平台管理」、選單恰一項「主辦方審核」、「登出」呼叫登出並導向 `/login`）、AWU-AREA-SWITCH-003（以真實 router：平台管理點「主辦方後台」在未切換時被導向「選擇主辦方」頁面，點「會員前台」路徑為 `/`）；展開切換選單一律沿用 3.1 中 005 的步驟 1、2、6。再實作 `PlatformLayout.vue`（決策 4，樣式依決策 3；根元素 class `platform-layout`、導覽列 `platform-nav`、水平選單 `platform-nav-menu`、登出鈕 `platform-nav-logout`，供 1.1 量測定位）；通過

## 5. 我的主辦方代碼欄

- [ ] 5.1 先在 `MyOrganizersPage.test.ts` 新增 AWU-LIST-005（兩筆同名、Id 不同的 Organizer，各列顯示自己 Id 前 8 字元且兩列不同；斷言值寫死為測試資料 Id 的前 8 字元，不以同一函式計算預期值）。再新增「代碼」欄；AWU-LIST-001～004 既有測試不放寬；通過

## 6. 對比度

- [ ] 6.1 `morandi.contrast.test.ts`：「頁面底」集合加入 `--color-bg-organizer`、`--color-bg-platform`，所有以頁面底為對象的檢查（WCC-003、004、007、008 等）自動涵蓋；新增 WCC-013，斷言：兩個強調色對白底與各自區域底色 ≥ 4.5、兩個區域底色彼此不同、兩者都不等於 `--color-bg`。修改前先確認：把任一新底色暫時改深（例如 `#dddddd`）時 WCC-004 會失敗，記錄失敗輸出後還原；通過

## 7. 整體驗證

- [ ] 7.1 `docker compose exec -T web npx vitest run`、`docker compose exec -T web npx vue-tsc -b`、`docker compose exec web npm run test:bundle` 全部通過（BS-001～005、BS-010），記錄首屏 gzip 與基準的差異；若有既有 flaky 測試失敗，重跑確認並記錄，不得略過
- [ ] 7.2 修改後請使用者以 Admin 登入並切換 Organizer，執行 `measure-area-nav.js` 三種 `AREA`（`BASELINE = false`，`'organizer'` 含注入與不注入），記錄完整 JSON。AWU-AREA-LAYOUT-001 的欄位：`layoutBackground`（版面根元素計算背景色、對應 CSS 變數〔`--color-bg`／`--color-bg-organizer`／`--color-bg-platform`〕以 `getComputedStyle(documentElement)` 讀出後套到暫時元素取得的計算色）、`navBackground`（`.admin-nav`／`.platform-nav` 計算背景色與 `--color-bg-elevated` 的計算色；`'buyer'` 不輸出）、`navBorderTop`（`border-top-width`、`border-top-color` 與對應強調色的計算色）。**通過條件**（全部成立才完成）：
  - 每筆紀錄 `valid: true` 且 `pass: true`（即 1.1 列出的 MOBILE-NAV-001、002、AREA-NAV-OVERFLOW-001 判定全部成立）；
  - AWU-AREA-LAYOUT-001：三個 `AREA` 的 `layoutBackground` 各等於對應 CSS 變數的計算色，且三者兩兩不同；`'organizer'`、`'platform'` 的 `navBorderTop` 寬度為 `4px`、顏色等於 `--color-area-organizer`／`--color-area-platform` 的計算色；`navBackground` 等於 `--color-bg-elevated` 的計算色；
  - 任何背景色或邊框色為 `rgba(0, 0, 0, 0)` 或 `transparent` 即不成立
- [ ] 7.3 （人工 UX 驗證，非驗收條件，不影響 AC 通過與否）請使用者在瀏覽器實際看三個區域，確認背景色與色條足以辨識（design.md Risks 第 2 點）；若使用者認為不夠明顯，記錄回饋並回報，不自行改色

## 8. 收尾

- [ ] 8.1 依 CLAUDE.md 套用 hardener 檢查清單（本 change 只有前端，若無 Application／Repository 變更則記錄「不適用」），呼叫 strict-reviewer 並修正至 PASS
- [ ] 8.2 回報 spec 同步狀態（admin-web-ui、buyer-web-ui、web-color-contrast、web-bundle-splitting 四份 delta 與實作是否一致），等待使用者指示 commit
