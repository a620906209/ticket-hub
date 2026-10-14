> **AC 測試對應例外**：AWU-MOBILE-NAV-001、002（已核准 2026-10-09）、AWU-AREA-LAYOUT-001、AWU-AREA-NAV-OVERFLOW-001 以瀏覽器量測取代單元或整合測試，例外紀錄見 design.md「AC 測試對應例外」。後兩條核准狀態：**已核准（2026-10-13，核准人 RD-Hank）**。AWU-AREA-SWITCH-006 的可見文字部分：**已核准（2026-10-14，核准人 RD-Hank）**。
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
> | AWU-AREA-SWITCH-006 | Vitest（`AreaSwitcher.test.ts`：aria-label、title、縮寫元素有無）；W = 320／390／480／481／1280 的可見文字由瀏覽器確認（已列入例外，2026-10-14 核准） | 4.4、8.1 |
> | AWU-AREA-SWITCH-007 | Vitest（`AdminLayout.test.ts`、`BuyerLayout.test.ts`、`PlatformLayout.test.ts` 的 DOM 順序測試；2026-10-14 依使用者回饋新增） | 7.2 |
> | AWU-AREA-LAYOUT-001 | 瀏覽器量測 | 7.2 |
> | AWU-PLATFORM-NAV-001 | Vitest（`PlatformLayout.test.ts`） | 4.5 |
> | AWU-AREA-NAV-OVERFLOW-001 | 瀏覽器量測（含會員前台單行判定與 30 字元會員名稱注入，2026-10-14 擴充） | 1.2、7.2 |
> | AWU-AREA-NAV-OVERFLOW-002 | Vitest（`BuyerLayout.test.ts`；2026-10-14 依 8.1 審查新增） | 8.1 |
> | BW-ADMIN-ENTRY-001～003 | Vitest（`BuyerLayout.test.ts`） | 4.3 |
> | WCC-001～013（「頁面底」擴大） | Vitest（`morandi.contrast.test.ts`） | 6.1 |
> | BS-001、003、010 | Vitest（`build-checks/bundle-splitting.test.ts`、既有測試） | 7.1 |
> | BS-002、004 | Vitest（`build-checks/bundle-splitting.test.ts`） | 2.4、7.1 |
> | BS-005、014 | Vitest（`lazyRoutes.test.ts`） | 2.3 |

## 1. 量測腳本與基準值

- [x] 1.1 以 archive 的 `measure-admin-nav.js` 為基礎，撰寫 `openspec/changes/web-area-separation/measure-area-nav.js`（隨 change 歸檔）。執行方式比照 admin-mobile-nav：使用者登入後，在前端 dev server 的同源分頁以 claude-in-chrome `javascript_tool` 執行；同源 iframe 依序設定各 W，iframe 內 `scrollbar-width: none`；腳本開頭的常數 `AREA`（`'organizer'`／`'platform'`／`'buyer'`）、`BASELINE`、`INJECT_LONG_TEXT`（原本只對 `'organizer'` 有效；8.1 起也對 `'buyer'` 注入 30 字元會員名稱）切換量測內容。腳本輸出 JSON 陣列，每筆對應一組（`AREA`、W、目標路徑、是否注入），欄位與判定一律依下列定義，不得只靠「沿用原腳本」：

  **量測組合**

  | `AREA` | 目標路徑 | W | 導覽根元素 | 版面根元素 |
  | --- | --- | --- | --- | --- |
  | `'organizer'` | `/admin/redeem`、`/admin/venues`（各量一般名稱與 100 字元注入兩次） | 320、390、720、721、800、1280 | `.admin-nav` | `.admin-layout` |
  | `'platform'` | `/platform/organizers` | 320、390、720、721、800、1280 | `.platform-nav` | `.platform-layout` |
  | `'buyer'` | `/` | 320、390、1280（8.1 審查後加 361、480、481） | `.buyer-header` | `.buyer-layout` |

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
  - `'buyer'`（8.1 擴充，`BASELINE = false` 時判定，見 7.2「8.1 第 3 輪」「8.1 第 4 輪」紀錄）：`memberTrigger`（`.member-trigger`）命中數須為 1（注入長名稱時命中數不是 1 或注入後文字不符即判無效）；`lineCounts` 中 W ≥ 361 時品牌名稱、「活動」、「我的訂單」、會員名稱皆為 1 行，W ≤ 360 時只要求會員名稱 1 行；會員名稱 `scrollHeight` 不得大於 `clientHeight`；頂列子孫元素底緣不得大於頂列底緣。截斷中的會員名稱只有在計算樣式為 `overflow-x: hidden` 且 `text-overflow: ellipsis` 時才排除在水平溢出檢查外。
  - `BASELINE = true` 時，`'organizer'` 的選單按鈕與 `.admin-nav-logout` 規則比照 admin-mobile-nav 只輸出不判定（修改前現況已符合 admin-mobile-nav，實際上會通過）；`'platform'` 不量（修改前不存在）；`'buyer'` 不檢查 `areaSwitcher`。
  - `documentScrollWidth` 只在 `'organizer'`、`/admin/redeem`、W ≤ 720 時判定（AWU-MOBILE-NAV-001 原文）；其餘組合（含 `/admin/redeem` W ≥ 721、`/admin/venues`、`/platform/organizers`、`/`）只輸出不判定（`/admin/venues` 的表格溢出 R2 不在範圍內）。

  腳本須能失敗的自我檢查：在 1.2 之後、修改之前，以 `'buyer'` W = 320 暫時把 `.area-switcher` 規則設為必判（即 `BASELINE = false`）執行一次，確認因 `areaSwitcher.count` = 0 得到 `pass: false`；再以 `'buyer'` W = 320 在 `.buyer-header` 內暫時注入一個 `width: 2000px` 的元素執行一次，確認因 `navMaxRight` > W 得到 `pass: false`、`failReasons` 指出該元素；兩次都記錄輸出，注入只存在於 iframe 內、不修改原始碼
  > **1.1 紀錄（2026-10-13）**：完成。依上述定義實作，另新增兩個輸出欄位：`descendantMaxRight`（不含導覽根元素本身的子孫最大右緣）與 `elementsBeyondW`（右緣超出 W 的子孫）。原因：第一次執行自我檢查時，注入 2000px 元素後 `failReasons` 只指向 `header.buyer-header`（根元素的 `scrollWidth` 含溢出子孫），無法指出注入的元素；另外根元素外框本身就是 W，`W - navMaxRight` 恆為 0，量不出餘裕。修正後 `navMaxRight` > W 的 `failReasons` 會列出超出 W 的子孫。判定規則沒有放寬。自我檢查兩次都用修正後的腳本重跑，輸出見 1.2。
  >
  > 執行方式補充：`javascript_tool` 的回傳值約 1000 字元就會截斷，且會擋下 base64 輸出。因此暫時把腳本複製到 `web/public/`，由頁面 `fetch` 取得（SHA-256 與檔案一致：`82ed61e3…2c1976`，量測後已刪除）；腳本開頭的 5 個常數改為函式參數，其餘邏輯不變。結果 JSON 以 gzip 加 hex 分段取回，解碼後與瀏覽器端的 SHA-256 一致。
- [x] 1.2 修改前請使用者以 Admin 帳號手動登入（含驗證碼）並切換到一個 Approved Organizer，執行 `AREA = 'organizer'`（含注入與不注入）與 `'buyer'` 的基準量測（`BASELINE = true`），在本節記錄完整 JSON 與 1.1 的自我檢查輸出。基準不要求重現溢出；**通過條件**：所有紀錄 `valid: true`；並記錄會員前台 W = 320 時 `W - navMaxRight`（新增切換鈕前的餘裕）。量測無效時停止並回報

  > **1.2 紀錄（2026-10-13，`BASELINE = true`，Admin 已切換到「Ticket-Hub 示範主辦方」）**：通過。
  > - 完整 JSON 存在 `measurements/1.2-baseline.json`（30 KB，以 key 區分 `orgBase`／`orgInject`／`buyerBase`／`selfCheckSwitcher`／`selfCheckWide`，SHA-256 `d1360247…8ecf3513`，與瀏覽器端一致）。內容太長，不直接貼在本節。
  > - `orgBase` 12 筆、`orgInject` 12 筆、`buyerBase` 3 筆全部 `valid: true`、`pass: true`。主辦方後台在各寬度的 `navMaxRight` = W，子孫最大右緣 = W − 16（導覽列的 `padding-right`）。注入 100 字元名稱後，`.organizer-indicator` 在 W = 320 時寬 244、右緣 304，名稱被截斷。
  > - 會員前台 W = 320：`W - navMaxRight` = 0，因為根元素本身算在內。子孫最大右緣 296.33，比 `buyer-header` 右側 padding 的內緣（296）多 0.33px，來源是「活動」文字。此時 `.brand` 已經換行（高 54），`.buyer-nav` 被壓到 24.17px。**實際上已經沒有餘裕**。W = 390 時，`.buyer-nav` 寬 62.5，扣掉「活動」文字後約剩 34px。新增切換鈕後，W = 320 與 390 預期會溢出，4.4 很可能要處理。
  > - 自我檢查（W = 320）：`selfCheckSwitcher`（`BASELINE = false`）得到 `pass: false`，`failReasons` 為 `areaSwitcher 命中數=0 | areaSwitcher 不可見`。`selfCheckWide`（注入 2000px）得到 `pass: false`，`failReasons` 為 `navMaxRight=2202 > W（header.buyer-header；超出 W 的子孫：div.self-check-wide@2201.6875） | overflowingElements: header.buyer-header | navRootScrollWidth=2202（clientWidth=320、W=320）`。注入只存在於 iframe 內。

## 2. 路由

- [x] 2.1 先在 `router/index.test.ts` 新增／修改測試（應先失敗）：AWU-GUARD-001 涵蓋 `/platform/organizers`；AWU-GUARD-004 對新舊兩個網址 × 已切換／未切換 Organizer 共 4 種組合各斷言一次；AWU-GUARD-005 改用新網址；新增 AWU-GUARD-008（`/admin/organizers` 與 `/platform` 最終路由名稱皆為 `platform-organizers`）。再修改 `router/index.ts`：新增 `/platform`（`PlatformLayout` 動態 import、`''` redirect、`organizers` 名稱 `platform-organizers`、meta `{ requiresAdmin: true }`），`/admin` 下的 `organizers` 改為 redirect；`beforeEach` 不修改。全部測試通過
- [x] 2.2 審核頁既有測試與程式中所有指向 `/admin/organizers` 或 `admin-organizers` 路由名稱的地方改為新路由（已知需改：`App.test.ts` 的 `it.each` 路徑清單、`AdminLayout.test.ts` 中審核相關測試；以 `grep -rn "admin-organizers\|/admin/organizers" web/src` 確認只剩 redirect 定義與 AWU-GUARD-004／008 的測試）；AWU-REVIEW-001～003 測試通過
- [x] 2.3 `lazyRoutes.test.ts` 依 BS-004／005／014 更新：延遲載入數 20、`EXPECTED_ROUTE_TREE` 依 spec 的預期樹更新、元件對照表 21 筆（含 `PlatformLayout`），對照表筆數斷言等於有元件的 route 數；通過
- [x] 2.4 `web/build-checks/bundle-splitting.test.ts`：BS-004 的動態 import 數與不同 chunk 數由 19 改為 20，並斷言取出的路徑包含 `src/layouts/PlatformLayout.vue`；BS-002 的排除清單加入 `src/layouts/PlatformLayout.vue`（比照 `ADMIN_LAYOUT_MODULE` 新增常數）。在 7.1 執行通過

## 3. 色票與區域切換元件

- [x] 3.1 先寫 `web/src/components/AreaSwitcher.test.ts`（應先失敗）：每支測試都沿用下方 005 的步驟 1、2、6 開啟與清理 dropdown（步驟 1 的 Email 改用一般值），涵蓋 AWU-AREA-SWITCH-001（Admin、`current="organizer"`：項目順序、文字；目前區域項目含 `is-disabled` class 且 `aria-disabled="true"`，其他項目不含）、002（非 Admin、`current="buyer"`：項目恰為 3 項且順序為登入帳號、會員前台、主辦方後台，因此不含「平台管理」；不得只做「找不到平台管理」的否定斷言）、003（點選三個項目分別呼叫 `router.push` 至 `/platform`、`/admin`、`/`，最終路徑由 4.x 的版面測試以真實 router 驗證）。AWU-AREA-SWITCH-005 依下列步驟獨立寫一支測試：
  1. 比照 `BuyerLayout.test.ts` 同時設定 `authStore.accessToken` 與 `authStore.member`（`isAuthenticated` 需兩者），`member.email` 設為 `'<b>x</b>@example.com'`，以 `attachTo: document.body` 掛載 `AreaSwitcher`；
  2. 先斷言 `document.body` 內 `.area-switcher-popper`（`el-dropdown` 的 `popper-class`）命中數為 1 且 `style.display` 為 `none`（尚未開啟）；點擊觸發按鈕 `.area-switcher-trigger` 後，以 `vi.waitFor` 輪詢直到該 popper 命中數為 1 且 `style.display !== 'none'`（Element Plus 以 `setTimeout` 開啟，`flushPromises` 不足以等到）；
  3. 在該 popper 內查找登入帳號項目 `.area-switcher-account`（掛在第一個 `el-dropdown-item` 上的 class），斷言命中數為 1；
  4. 斷言該項目的 `textContent` 包含原始字串 `<b>x</b>@example.com`；
  5. 斷言 `.area-switcher-popper` 整個 subtree 內 `b` 元素命中數為 0；
  6. 測試結束時 `unmount`，避免 Teleport 的 popper 殘留在 `document.body` 影響其他測試的命中數。

  能否失敗的確認：實作完成後，暫時把帳號項目改為 `v-html` 綁定 Email 執行此測試，確認步驟 4 或 5 失敗，記錄失敗輸出後還原。再實作 `AreaSwitcher.vue`（決策 2；`area-switcher` class 掛在元件根元素，也就是 `el-dropdown` 的可見觸發區域上，供 1.1 以根元素可見性代表觸發按鈕可見，3.1 斷言該根元素含觸發按鈕 `.area-switcher-trigger`；`el-dropdown` 設 `popper-class="area-switcher-popper"`，登入帳號項目加 class `area-switcher-account`；Email 以文字插值呈現）；通過

  > **3.1 紀錄（2026-10-13）**：測試先行時因元件不存在而失敗，實作後 6 支全部通過。能否失敗的確認：把帳號項目暫時改成 `<span v-html="`登入帳號：${email}`" />`，005 在步驟 4 失敗：`AssertionError: expected '登入帳號：x@example.com' to contain '<b>x</b>@example.com'`；已還原，檔案內沒有 `v-html`。會員前台沒有強調色 token，所以切換鈕上的會員前台色塊用 `--color-bg` 加 `--el-border-color` 外框表示。
- [x] 3.2 `morandi.css` 新增 `--color-bg-organizer`、`--color-bg-platform`、`--color-area-organizer`、`--color-area-platform`（決策 3 的數值）

  > **3.2 紀錄**：照決策 3 的四個值加在 `:root` 自訂 token 區。

## 4. 版面

- [x] 4.1 先改 `AdminLayout.test.ts`（應先失敗）：AWU-NAV-002（Admin 選單恰為 4 項、無「主辦方審核」）、刪除 AWU-NAV-004 的測試並在測試檔註記原因（spec 已移除該 Scenario）、AWU-MOBILE-NAV-003（兩種角色抽屜都恰為 4 項）、AWU-MOBILE-NAV-004（移除第三種情境）、AWU-AREA-SWITCH-001（導覽列有一個 `.area-switcher`，觸發文字含「主辦方後台」）、AWU-AREA-SWITCH-002（非 Admin 在主辦方後台展開選單，項目恰為 3 項且含「會員前台」「主辦方後台」，不含「平台管理」）；展開切換選單一律沿用 3.1 中 005 的步驟 1、2、6；AWU-NAV-001、003、MOBILE-NAV-005～007 既有測試不放寬。再修改 `AdminLayout.vue`：`menuItems` 移除審核項、加入 `AreaSwitcher current="organizer"`、根元素與導覽列套用決策 3 的樣式；通過

  > **4.1 紀錄（2026-10-14）**：先改測試時有 4 支失敗（NAV-002、MOBILE-NAV-003 的 Admin、AREA-SWITCH-001、002），實作後 14 支全部通過。`menuItems` 不再依角色變動，改成常數陣列（原本的 computed 只是為了 isAdmin 分支）。切換鈕放在選單按鈕與水平選單之間。
- [x] 4.2 （實作檢查，非 AC 驗證）確認 4.1 修改後窄螢幕抽屜與水平選單仍共用同一份 `menuItems`（不另寫一份清單），以 `grep` 記錄結果

  > **4.2 紀錄**：`grep -n menuItems web/src/layouts/AdminLayout.vue` 共 3 處：第 14 行定義、第 79 行水平選單 `v-for`、第 95 行抽屜 `v-for`。沒有第二份清單。
- [x] 4.3 先改 `BuyerLayout.test.ts`（應先失敗）：BW-ADMIN-ENTRY-001（Admin 未切換，經切換選單點「平台管理」後以真實 router 最終路由名稱為 `platform-organizers`）、002（非 Admin 切換選單恰為 3 項且含「會員前台」「主辦方後台」，不含「平台管理」）、003（兩種角色的會員下拉選單無「主辦方審核」、有「我的主辦方」）、AWU-AREA-SWITCH-002（會員前台順序）、AWU-AREA-SWITCH-004（未登入無 `.area-switcher`，同時斷言「登入」連結存在，確認版面已渲染）；展開切換選單一律沿用 3.1 中 005 的步驟 1、2、6。再修改 `BuyerLayout.vue`：移除審核項、已登入時加入 `AreaSwitcher current="buyer"`、根元素 `.buyer-layout` 設 `min-height: 100svh` 與 `background: var(--color-bg)`（與 `body` 同色，外觀不變，供 AWU-AREA-LAYOUT-001 量測）；通過

  > **4.3 紀錄（2026-10-14）**：先改測試時有 3 支失敗（ENTRY-003 Admin、ENTRY-002／AREA-SWITCH-002、ENTRY-001）。AREA-SWITCH-004 在實作前就通過，因為當時還沒有切換鈕，這是預期中的否定斷言；它同時斷言「登入」連結存在，可以排除版面沒渲染的情況。實作後 6 支全部通過。切換鈕放在 `.buyer-actions` 的最前面（「我的訂單」左邊）。
- [x] 4.4 若 7.2 的會員前台量測在 W = 320 溢出，依 design.md Risks 調整切換鈕窄螢幕呈現；若需要改 spec 文字，先停下回報使用者

  > **4.4 紀錄（2026-10-14）**：7.2 量測沒有水平溢出，但 W = 320 時頂列被擠壓：品牌名稱幾乎一行一個字（高 108），「活動」與「我的訂單」直排，「我的訂單」高 84，超出 56px 的頂列。回報使用者後，決定採用縮寫，並修改 spec 文字（admin-web-ui 區域辨識 Requirement 加入例外，新增 AWU-AREA-SWITCH-006）。
  > - 先用 iframe 試做：只縮寫時「我的訂單」仍然直排，高 84；縮寫並在 W ≤ 360 收窄頂列間距（header gap 24→12、padding 24→16、actions gap 16→8）後，所有元素最多兩行。所以兩者一起實作：`AreaSwitcher` 在 `current === 'buyer'` 時加上 `aria-label`／`title`、完整名稱與「前台」兩個 span，由 media query 切換；`BuyerLayout` 加上 ≤ 360px 的間距規則。
  > - 測試先行：AWU-AREA-SWITCH-006 的 3 支測試在實作前失敗，實作後通過；元件與版面測試 34 支全部通過。
  > - 瀏覽器：以 `measure-switch-abbreviation.js` 執行（結果在 `measurements/4.4-switch-006.json`，2 筆都 `valid`、`pass`）。W = 320 時可見文字為「前台」，W = 390 時為「會員前台」，`aria-label`／`title` 都是「會員前台」。頂列內元素底緣 55，沒有超出頂列底緣 57。iframe 試做時另外量到品牌名稱高 54（與基準相同），「我的訂單」高 42。
  > - 斷點後來在 8.1 改為 480px（見 7.2 紀錄「8.1 第 3 輪審查後修正」），本段數字為當時的量測。
  > - 失敗確認：把兩個檔案的 `max-width: 360px` 暫時改成 `300px` 後，W = 320 判定失敗（可見文字「會員前台」，元素底緣 82 > 57）；已還原。
  > - 重跑 `measure-area-nav.js` 的 buyer（`measurements/4.4-buyer-after.json`，sha256 `1b0be0db…9ac9`）：3 筆都是 `valid`、`pass`，`overflowingElements` 為空，W = 320 時子孫最大右緣 304.42。
- [x] 4.5 先寫 `web/src/layouts/PlatformLayout.test.ts`（應先失敗）：AWU-PLATFORM-NAV-001（一個 `.area-switcher` 觸發文字含「平台管理」、選單恰一項「主辦方審核」、「登出」呼叫登出並導向 `/login`）、AWU-AREA-SWITCH-003（以真實 router：平台管理點「主辦方後台」在未切換時被導向「選擇主辦方」頁面，點「會員前台」路徑為 `/`）；展開切換選單一律沿用 3.1 中 005 的步驟 1、2、6。再實作 `PlatformLayout.vue`（決策 4，樣式依決策 3；根元素 class `platform-layout`、導覽列 `platform-nav`、水平選單 `platform-nav-menu`、登出鈕 `platform-nav-logout`，供 1.1 量測定位）；通過

  > **4.5 紀錄（2026-10-14）**：2.1 為了讓路由可以 import，先建了只有 `<RouterView />` 的佔位版本。測試先行時 5 支全部失敗，實作後全部通過。另外加了一支測試，確認「主辦方審核」選單項目會前往 `/platform/organizers`。

## 5. 我的主辦方代碼欄

- [x] 5.1 先在 `MyOrganizersPage.test.ts` 新增 AWU-LIST-005（兩筆同名、Id 不同的 Organizer，各列顯示自己 Id 前 8 字元且兩列不同；斷言值寫死為測試資料 Id 的前 8 字元，不以同一函式計算預期值）。再新增「代碼」欄；AWU-LIST-001～004 既有測試不放寬；通過

  > **5.1 紀錄（2026-10-14）**：先寫測試時因為找不到 `td.organizer-code` 而失敗，實作後 7 支全部通過。代碼欄放在名稱右邊，寬 110px。

## 6. 對比度

- [x] 6.1 `morandi.contrast.test.ts`：「頁面底」集合加入 `--color-bg-organizer`、`--color-bg-platform`，所有以頁面底為對象的檢查（WCC-003、004、007、008 等）自動涵蓋；新增 WCC-013，斷言：兩個強調色對白底與各自區域底色 ≥ 4.5、兩個區域底色彼此不同、兩者都不等於 `--color-bg`。修改前先確認：把任一新底色暫時改深（例如 `#dddddd`）時 WCC-004 會失敗，記錄失敗輸出後還原；通過

  > **6.1 紀錄（2026-10-13）**：集合擴充後 50 支全部通過。失敗確認：把 `--color-bg-platform` 暫時改成 `#dddddd` 後有 6 支失敗：WCC-003 兩支、WCC-004 兩支（`--el-text-color-secondary`／`--color-text-secondary`，3.86）、WCC-007（2.62）、WCC-013 platform（3.92）；已還原。

## 7. 整體驗證

- [x] 7.1 `docker compose exec -T web npx vitest run`、`docker compose exec -T web npx vue-tsc -b`、`docker compose exec web npm run test:bundle` 全部通過（BS-001～005、BS-010），記錄首屏 gzip 與基準的差異；若有既有 flaky 測試失敗，重跑確認並記錄，不得略過

  > **7.1 紀錄（2026-10-14）**：
  > - vitest：第一次跑完整套件，`lazyRoutes.test.ts` 的 BS-005 在 40 秒時逾時（測試上限 30 秒）。它要冷轉換全部 20 個延遲載入元件，這次又多了 PlatformLayout。另外 566 支通過。立刻重跑，42 個檔案、567 支全部通過。在這之前，局部執行時 `App.test.ts`、`index.test.ts` 也各出現過一次 10 秒逾時，單獨重跑都通過。這幾次失敗都是負載下的逾時，不是斷言失敗。
  > - `vue-tsc -b` exit 0，沒有輸出。
  > - `test:bundle` 25 支全部通過（含 BS-001～005、BS-010）。
  > - 首屏 gzip：基準是在容器內以 HEAD 版本的 5 個原始檔另外 build，再用相同的首屏閉包算法計算。JS 103,209 → 103,567 B（+358），CSS 49,171 → 49,549 B（+378），首屏 chunk 數都是 20。
  > - 4.4 改完後重跑：vitest 42 個檔案、570 支全部通過，第一次就通過；`vue-tsc -b` exit 0；`test:bundle` 25 支全部通過。首屏 gzip 沒有重新計算，4.4 只多了兩個 span 和兩段 media query。
  > - 切換鈕移到右側後再跑一次：vitest 572 支全部通過；`vue-tsc -b` exit 0；`test:bundle` 25 支全部通過。
  > - 切換鈕統一移到導覽列最右邊後再跑一次：vitest 42 個檔案、573 支全部通過；`vue-tsc -b` exit 0；`test:bundle` 25 支全部通過。
  > - 8.1 修正會員前台 W = 390 擠壓後再跑一次：vitest 42 個檔案、574 支全部通過；`vue-tsc -b` exit 0；`test:bundle` 25 支全部通過。
  > - 8.1 第 4 輪修正後再跑一次：vitest 574 支全部通過；`vue-tsc -b` exit 0；`test:bundle` 25 支全部通過。
- [x] 7.2 修改後請使用者以 Admin 登入並切換 Organizer，執行 `measure-area-nav.js` 三種 `AREA`（`BASELINE = false`，`'organizer'` 含注入與不注入），記錄完整 JSON。AWU-AREA-LAYOUT-001 的欄位：`layoutBackground`（版面根元素計算背景色、對應 CSS 變數〔`--color-bg`／`--color-bg-organizer`／`--color-bg-platform`〕以 `getComputedStyle(documentElement)` 讀出後套到暫時元素取得的計算色）、`navBackground`（`.admin-nav`／`.platform-nav` 計算背景色與 `--color-bg-elevated` 的計算色；`'buyer'` 不輸出）、`navBorderTop`（`border-top-width`、`border-top-color` 與對應強調色的計算色）。**通過條件**（全部成立才完成）：
  - 每筆紀錄 `valid: true` 且 `pass: true`（即 1.1 列出的 MOBILE-NAV-001、002、AREA-NAV-OVERFLOW-001 判定全部成立）；
  - AWU-AREA-LAYOUT-001：三個 `AREA` 的 `layoutBackground` 各等於對應 CSS 變數的計算色，且三者兩兩不同；`'organizer'`、`'platform'` 的 `navBorderTop` 寬度為 `4px`、顏色等於 `--color-area-organizer`／`--color-area-platform` 的計算色；`navBackground` 等於 `--color-bg-elevated` 的計算色；
  - 任何背景色或邊框色為 `rgba(0, 0, 0, 0)` 或 `transparent` 即不成立

  > **7.2 紀錄（2026-10-14）**：完整 JSON 存在 `measurements/7.2-after.json`（33 筆：organizer 不注入 12、注入 12、platform 6、buyer 3；sha256 `fac59a89…4d9f`）。通過條件全部成立：33 筆都是 `valid: true`、`pass: true`，`overflowingElements` 全部為空。`layoutBackground`：organizer `rgb(238, 242, 243)`、platform `rgb(244, 238, 241)`、buyer `rgb(243, 240, 234)`，各等於對應變數，三者兩兩不同。`navBorderTop`：organizer 4px `rgb(97, 111, 114)`、platform 4px `rgb(125, 100, 116)`，各等於對應強調色。`navBackground` 都是 `rgb(255, 255, 255)`，等於 `--color-bg-elevated`。
  > - **切換鈕統一移到導覽列最右邊後重量（2026-10-14）**：使用者回饋切換鈕在會員前台位於右側，在後台與平台管理卻位於左側，切換區域時位置會跳動；之後再要求三區切換鈕都放在最右邊，其他功能都在它左邊（design.md 決策 2）。三個版面各新增一支 DOM 順序測試，實作前失敗、實作後通過。重量結果存為 `measurements/7.2-after-switcher-far-right.json`（33 筆，sha256 `acdc5570…3fdd`），全部 `valid`、`pass`；`layoutBackground`、`navBackground`、`navBorderTop` 的判定都成立。切換鈕右緣：W = 320 時三區都是 304；W = 1280 時後台與平台 1264、會員前台 1256（`buyer-header` 左右 padding 24，後台與平台為 16）。同一輪 SWITCH-006 的 2 筆重量結果與 `4.4-switch-006.json` 相同。中間版本（切換鈕在登出左邊）的量測檔已刪除。
  > - **8.1 審查後補強量測腳本並重量（2026-10-14）**：strict-reviewer 指出 `measure-area-nav.js` 只輸出 `layoutBackground`、`navBackground`、`navBorderTop`，沒有納入 `pass` 判定，背景色或色條失效時仍會回報通過，不符合例外紀錄「替代驗證須能在失敗情境下失敗」。修正：`BASELINE = false` 時，版面根元素背景須等於對應變數且非透明、三區背景變數的計算色須兩兩不同、導覽列背景須等於 `--color-bg-elevated`、色條須為 4px 且等於強調色，任一不成立即推入 `failReasons`；版面根元素命中數不是 1、CSS 變數解析為空也改列失敗而非無效。新增自我檢查 `SELF_CHECK_BREAK_COLORS`。`measure-switch-abbreviation.js` 加上 `pathname === '/'` 的有效條件，並把切換鈕命中數從有效條件移到通過條件。第 2 輪 adversarial 指出 alpha 為 0 的顏色仍會通過、導覽列未判白色、自我檢查只走透明一條路徑，再修正：顏色須為不透明 `rgb(r, g, b)`、導覽列背景須為 `rgb(255, 255, 255)`；`SELF_CHECK_BREAK_COLORS` 改為同時把版面背景改成別的顏色、把 `--color-bg-elevated` 改透明、色條改 0px、把另一區背景變數改成與本區相同。`measure-switch-abbreviation.js` 改以 `innerText` 取可見文字、以 try/finally 移除 iframe，新增自我檢查 `SELF_CHECK_FORCE_FULL_LABEL`（強制顯示完整名稱）。重量結果存為 `measurements/7.2-layout-judged.json`（sha256 `0be6e968…4cbe`）：正式 33 筆全部 `valid`、`pass`，三區背景 organizer `rgb(238, 242, 243)`、platform `rgb(244, 238, 241)`、buyer `rgb(243, 240, 234)`；`SELF_CHECK_BREAK_COLORS` 自我檢查（W = 1280，organizer 2 筆、platform 1 筆各 5 條 `failReasons`：版面背景不符、三區未兩兩不同、導覽列非不透明、非白色、色條 0px；buyer 1 筆 2 條）全部 `valid: true`、`pass: false`；SWITCH-006 2 筆 `pathname` 為 `/`，全部通過；`SELF_CHECK_FORCE_FULL_LABEL` 自我檢查 W = 320 顯示「會員前台」判為不通過，W = 390 通過（該寬度本就顯示完整名稱）。（此為 360px 斷點時的結果；8.1 第 3 輪後斷點改為 480px，W = 390 也縮寫，見下方「8.1 第 3 輪審查後修正」）
  > - 會員前台 W = 320 沒有水平溢出（子孫最大右緣 296.31），但版面被擠壓得比基準嚴重：`.brand` 寬 24.31、高 108（每行約一個字），`.buyer-nav` 寬 14、高 42（「活動」直排），`.buyer-actions` 換成兩行、高 84。基準是 `.brand` 高 54、`.buyer-nav` 寬 24.17。見 4.4。
  > - **8.1 第 3 輪審查後修正會員前台 W = 390 擠壓（2026-10-14）**：adversarial 審查指出 W = 390 時品牌名稱與「活動」被擠成兩行（原本縮寫與收窄間距只在 W ≤ 360）。使用者決定修正，採「擴大緊湊模式範圍」，並核准 4 項 spec 修改：縮寫門檻改為 W ≤ 480（有捲軸時不保證的區間改為約 465～480）；361 ≤ W ≤ 1280 時品牌名稱、「活動」、「我的訂單」、會員名稱各以單行顯示，會員名稱放不下時以「…」截斷並提供 `title`；SWITCH-006 改為 W = 320、390 顯示「前台」、W = 1280 顯示「會員前台」；OVERFLOW-001 加入單行判定與 30 字元會員名稱量測。
  >   - 實作：`AreaSwitcher` 縮寫與 `BuyerLayout` 收窄間距的斷點都改為 `max-width: 480px`；會員名稱一律單行截斷（`.buyer-actions`、`.member-dropdown`、`.member-trigger` 設 `min-width: 0`，`.member-trigger` 加 `title`）；W ≥ 361 時 `.brand`、`.nav-link` 不換行。
  >   - **與核准內容的差異（已回報使用者）**：原核准 W ≤ 360 維持現狀，但量測時 30 字元會員名稱在 W = 320 換行、把頂列撐高（元素底緣 82 > 57），所以會員名稱的單行截斷擴大到所有寬度，W ≤ 360 另以 `max-width: 4em` 限制會員名稱寬度，其餘三者在 W ≤ 360 仍可換行。spec 文字同步為「會員名稱 SHALL 以單行顯示……W ≤ 360 時這三者可換行，但頂列內元素不得超出頂列底緣」。
  >   - 測試先行：新增 AWU-AREA-NAV-OVERFLOW-002（`title` 為 30 字元完整名稱），實作前失敗、實作後通過。
  >   - 量測腳本：`measure-area-nav.js` 的 `INJECT_LONG_TEXT` 也適用 buyer（把 `.member-trigger` 換成 30 字元字串）；buyer 輸出 `lineCounts`（brand、activityLink、ordersLink、memberTrigger），W ≥ 361 四項都須為 1、W ≤ 360 只要求 memberTrigger 為 1，且子孫元素底緣不得超過頂列底緣；截斷中的 `.member-trigger` 只有在計算樣式為 `overflow-x: hidden` 且 `text-overflow: ellipsis` 時才排除在溢出檢查外。新增 `SELF_CHECK_BREAK_LINES` 強制品牌名稱與會員名稱換行。`measure-switch-abbreviation.js` 的 `EXPECTED` 改為 320／390「前台」、1280「會員前台」。
  >   - 重量：完整 JSON 存在 `measurements/7.2-buyer-single-line.json`（sha256 `e8871c58…767a`，與瀏覽器端一致）。buyer 3 筆、buyer 注入長名稱 3 筆、organizer 不注入 12、注入 12、platform 6，全部 `valid`、`pass`；W = 320 時品牌名稱、「活動」、「我的訂單」各 2 行、會員名稱 1 行，元素底緣 55 ≤ 57；W = 390、1280 四項都是 1 行。SWITCH-006：320、390「前台」，1280「會員前台」，3 筆都 `pass`。
  >   - 自我檢查：`SELF_CHECK_BREAK_LINES` 3 筆都判為不通過（W = 320：會員名稱 2 行、底緣 82 > 57；W = 390、1280 另加品牌名稱 4 行）；`SELF_CHECK_BREAK_COLORS` organizer 2 筆、platform 1 筆、buyer 1 筆都判為不通過；SWITCH-006 強制完整名稱時 320、390 判為不通過，1280 通過（預期本來就是完整名稱）。
  > - **8.1 第 4 輪審查後修正（2026-10-14）**：strict-reviewer PASS、adversarial 無 blocking。使用者決定修正 4 項建議：
  >   - 會員名稱字母下伸部被裁約 1px（`el-dropdown` 的 `line-height: 1` 加上 `overflow: hidden`）：`.member-trigger` 加 `line-height: normal`；量測腳本新增「`scrollHeight` > `clientHeight` 即判失敗」，spec 加入「文字 MUST NOT 被上下裁切」。
  >   - 斷點兩側沒有量測守住：OVERFLOW-001 的會員前台寬度改為 320、361、390、480、481、1280（含長名稱注入），SWITCH-006 改為 320、390、480（「前台」）與 481、1280（「會員前台」）。
  >   - 自我檢查補強：`SELF_CHECK_BREAK_LINES` 也強制「活動」「我的訂單」換行；新增 `SELF_CHECK_BREAK_TRUNCATION`（`text-overflow: clip`、`line-height: 1`）。
  >   - `max-width: 360px`／`min-width: 361px` 之間 360.x 的空隙改用範圍語法 `width <= 360px`／`width > 360px`；7.2 舊紀錄補上「斷點已改為 480」的註記。
  >   - 重量（`measurements/8.1-buyer-breakpoints.json`，sha256 `c09c48e1…a41c`，與瀏覽器端一致）：buyer 6 筆、長名稱注入 6 筆全部 `valid`、`pass`；W = 320 行數為品牌 2、活動 2、我的訂單 2、會員名稱 1，底緣 55 ≤ 57；361～1280 四項都是 1 行、底緣 44；會員名稱 `clientHeight` = `scrollHeight` = 18（修正前 14／16）。`SELF_CHECK_BREAK_LINES` 6 筆都判為不通過（361 以上四項行數與底緣 82 都列入 failReasons）；`SELF_CHECK_BREAK_TRUNCATION`（長名稱、W = 320、361、390、480）4 筆都判為不通過，failReasons 同時含水平溢出與「上下被裁切（16 > 14）」。organizer、platform 的 CSS 與判定沒有變動，未重量。
  >   - **SWITCH-006 在 W = 480 判為失敗，查明是量測腳本的問題**：`measure-switch-abbreviation.js` 原本保留捲軸、把 iframe 加寬來補捲軸寬，media query 寬度因此是 W + 捲軸寬（約 495），落在 spec「有捲軸時約 465～480 不保證」的區間。上述 JSON 的 `switch006` 是這個版本的結果（480 顯示「會員前台」），已作廢；`4.4-switch-006.json`、`7.2-layout-judged.json`、`7.2-buyer-single-line.json` 內的 switch006 也是同一種加寬量法（media 寬度約 W + 15），僅為歷史紀錄，現行結論只依據 `8.1-switch-006-no-scrollbar.json`。使用者決定量測時隱藏捲軸（比照 `measure-area-nav.js`），spec SWITCH-006 的 WHEN 加註「量測時隱藏捲軸，使 media query 寬度等於 W」。重量（`measurements/8.1-switch-006-no-scrollbar.json`，sha256 `3bc33bfc…44cc`）：5 筆 `clientWidth` 都等於 W、`valid`、`pass`，320／390／480 為「前台」、481／1280 為「會員前台」；強制完整名稱的自我檢查在 320、390、480 判為不通過。
- [x] 7.3 （人工 UX 驗證，非驗收條件，不影響 AC 通過與否）請使用者在瀏覽器實際看三個區域，確認背景色與色條足以辨識（design.md Risks 第 2 點）；若使用者認為不夠明顯，記錄回饋並回報，不自行改色

  > **7.3 紀錄（2026-10-14）**：使用者在瀏覽器檢視三個區域，回饋「三個區域有差別」，背景色與色條足以辨識，不需調整。寬螢幕下會員前台切換鈕右緣比另外兩區少 8px（7.2 紀錄），已提出給使用者，未要求調整，維持現狀。

## 8. 收尾

- [x] 8.1 依 CLAUDE.md 套用 hardener 檢查清單（本 change 只有前端，若無 Application／Repository 變更則記錄「不適用」），呼叫 strict-reviewer 並修正至 PASS

  > **8.1 紀錄（2026-10-14）**：hardener 不適用（本 change 只有前端，沒有 Application／Repository 變更）。共 5 輪 strict-reviewer 加新的 adversarial general-purpose 審查；第 5 輪兩者都沒有 blocking（strict-reviewer PASS，574 支測試通過、量測 JSON sha256 與數字一致）。各輪修正見 7.1、7.2 紀錄。第 5 輪的文件建議已修（1.1 補上 buyer 判定、`INJECT_LONG_TEXT` 說明、舊 switch006 量法註記、腳本註解）；需要改 spec 文字的兩項建議（「上下裁切」判定只是行框代理判定、有捲軸 463～480 時「完整名稱＋寬間距」沒有量測）未修，於 8.2 回報使用者。
  > - **8.2 前補 spec（2026-10-14，使用者指示「先補spec」）**：admin-web-ui 區域辨識 Requirement 的捲軸例外補上「463～480 時仍適用會員前台頂列的不溢出與單行要求，但不直接量測，以 W = 481 的餘裕推算」；平台管理 Requirement 的「不被上下裁切」註明是 `scrollHeight` ≤ `clientHeight` 的代理判定及其不保證範圍。design.md 例外表「剩餘風險」同步。只改文字，實作、測試、量測不變。
- [x] 8.2 回報 spec 同步狀態（admin-web-ui、buyer-web-ui、web-color-contrast、web-bundle-splitting 四份 delta 與實作是否一致），等待使用者指示 commit

  > **8.2 紀錄（2026-10-14）**：已回報四份 delta 的 spec 同步狀態（admin-web-ui 依使用者核准修改、與實作一致；buyer-web-ui、web-color-contrast、web-bundle-splitting 與實作一致）與未修的 non-blocking 項目；使用者指示 commit。

  > **commit 後重新審查（2026-10-14，使用者指示處理）**：(1) proposal 的 admin-web-ui 項目補寫移除 AWU-NAV-004 與 MOBILE-NAV-001／004 審核頁情境的原因（delta 以 MODIFIED 整段取代，歸檔後這些情境會從主 spec 消失，原本只有 4.1 與測試檔註解提到）。(2) `AreaSwitcher.test.ts` 新增「點選停用的帳號列與『（目前）』項目不導覽」測試（AWU-AREA-SWITCH-001），原本只驗證停用樣式，不導覽依賴 Element Plus 內部判斷；已確認拿掉 `:disabled="item.isCurrent"` 時「（目前）」案例會失敗；帳號列本身沒有 click 綁定，該案例只防「日後加上 click 卻沒保留停用」，拿掉 `disabled` 不會使它失敗。
