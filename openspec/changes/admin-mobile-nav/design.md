## Context

`web/src/layouts/AdminLayout.vue` 的導覽列是 flex 列：`el-menu mode="horizontal" :ellipsis="false"`（`flex-grow: 1`）＋主辦方名稱連結（`.organizer-indicator`，導向「我的主辦方」）＋「登出」文字按鈕。`:ellipsis="false"` 讓選單不會收合，5 個項目（非 Admin 4 個）加上名稱與登出鈕的總寬度遠超過手機寬度。審視報告把 R1 標為「仍未實測」，原因是後台需要登入，而登入要過驗證碼。

其他相關事實：
- 主辦方名稱上限 100 字元（`Organizer.cs`、`OrganizerConfiguration.cs`），由使用者輸入，可能是沒有斷點的長字串；目前沒有截斷，在桌面寬度也可能把導覽列撐寬。
- 核銷頁本身的內容已有 `max-width: 480px; width: 100%`（`RedemptionScannerPage.vue`），預期窄螢幕下不會溢出，但尚未實測。
- `@element-plus/icons-vue` 未列為直接相依（`package.json`），但它是 element-plus 的 dependency，`node_modules` 中存在；`el-drawer` 本身會 import 它的 `Close` 圖示，所以 bundle 會多出 drawer 與 Close 圖示的程式碼（AdminLayout 延遲載入，不影響首屏）。
- `el-drawer`（Element Plus 2.14.4）內建 focus trap，開啟時把焦點移入抽屜；Esc 預設可關閉；有 `title` 時以標題作為對話框的名稱。釋放時還原的目標是**開啟當下的 `document.activeElement`**（`focus-trap.vue_vue_type_script_lang.mjs` 的 `startTrap`），沒有則落在 `body`。Safari／iOS 點擊按鈕不會讓按鈕取得焦點，jsdom 的 `trigger('click')` 也不會，所以只靠預設行為，觸控開啟後關閉時焦點會掉到 `body`。
- `el-drawer` 預設 Teleport 到 `body`，內容不在元件的 wrapper 內；關閉後內容以 `v-show` 隱藏仍留在 DOM（`destroy-on-close` 預設 false）。
- Element Plus 已改為按需引入（web-bundle-splitting）。`build-checks/bundle-splitting.test.ts` 的 `<el-*>` 對照表沒有列入的標籤會直接失敗（BS-011），新增 `el-drawer` 必須同步更新對照表；`AdminLayout` 是延遲載入的，不會進入買家首屏。
- 買家端活動詳情頁的斷點是 `@media (max-width: 720px)`。

## Goals / Non-Goals

**Goals:**
- 320px 至 1280px 寬時，後台導覽列沒有橫向溢出（含主辦方名稱為 100 字元長字串時）。
- 窄螢幕時所有選單項目仍可到達，且依角色顯示的規則與桌面版一致。
- 抽屜可用鍵盤操作：Esc 關閉，關閉後焦點回到選單按鈕。

**Non-Goals:**
- 後台表格在手機上的收合（報告 R2，P1）、各後台頁面內容本身的 RWD。核銷頁是例外：它是這次要解決的使用情境，量測時會一併確認整頁沒有溢出（見決策 5）。
- 核銷頁的精簡版型。
- `prefers-reduced-motion`：全站目前沒有任何 reduced-motion 處理，抽屜沿用 Element Plus 預設的滑入動畫。動畫規範留給買家探索頁改版一併定義，避免這次只有一個元件例外。
- 抽屜開啟時視窗從窄變寬（例如平板旋轉）時自動關閉：抽屜在寬螢幕下仍可正常操作與關閉，不會造成功能問題，不為此加入 `matchMedia` 監聽。

## Decisions

### 決策 1：以 CSS media query 切換兩種版面，不用 JS 判斷寬度

選單按鈕與水平選單兩者都在 DOM 中，由 `@media (max-width: 720px)` 決定顯示哪一個；抽屜首次開啟前不渲染，開啟過一次後以 `v-show` 保留在 DOM（`el-drawer` 預設行為）。斷點沿用活動詳情頁的 720px。

**理由**：不需要 `matchMedia` 狀態與監聽清理，第一次渲染就是正確版面，不會閃爍。Vitest（jsdom）不計算 media query，行為測試可以直接操作兩者，版面切換交給瀏覽器量測驗證。

**替代方案**：`matchMedia` 加上 `v-if` 只渲染其中一種。可以少一份 DOM，但要處理監聽的註冊與移除；jsdom 也沒有 `matchMedia`，測試必須另外 mock。不採用。

### 決策 2：選單項目抽成單一清單，兩個選單共用

在 script 中以 computed 產生選單項目清單（`index`、`label`；「主辦方審核」只在 `authStore.isAdmin` 時加入），水平選單與抽屜內的直式選單都用 `v-for` 渲染同一份清單。

**理由**：「後台導覽入口依頁面權限規則顯示」需求要求兩處一致。若在 template 寫兩份，之後新增選單時可能只改到一份。

### 決策 3：抽屜內是 `el-menu`（直式、`router` 模式），選取後由 `select` 事件關閉抽屜

抽屜加上專屬 class `admin-nav-drawer`（`el-drawer` 的 `class` 會套在抽屜元素上），抽屜內選單使用 class `admin-nav-drawer-menu`。測試一律在 `document.body` 內以這兩個 class 限定查找範圍，避免查到頂列中同樣文字的水平選單項目或登出鈕（jsdom 不套用 media query，兩者都在 DOM 中）。

抽屜內的選單與水平選單一樣使用 `router` 模式導覽，`default-active` 綁定目前路由路徑，讓目前所在頁面有選取狀態。`@select` 觸發時關閉抽屜。

**理由**：以「使用者選了項目」作為關閉時機，而不是「路由變更」。點選目前所在頁面時路由不會變更，但使用者預期抽屜要關閉；守衛把使用者導到其他頁面時，抽屜也同樣會關閉。

**替代方案**：`watch(route.path)` 關閉。點選目前頁面時抽屜不會關，不採用。

### 決策 4：抽屜與選單按鈕的細節

- 抽屜從左側滑出（`direction="ltr"`，與左上角的選單按鈕同側），寬度 `280px`，320px 寬時右側仍留 40px 遮罩可以點擊關閉。開關動畫、遮罩、Esc 關閉與 focus trap 都沿用 Element Plus 預設，不自訂。
- 抽屜 `title="後台選單"`，作為對話框的名稱。
- **焦點還原不依賴 Element Plus 預設行為**：抽屜關閉後，由元件明確把焦點移回選單按鈕，不論開啟時按鈕是否取得過焦點（觸控、滑鼠、鍵盤都一樣）。這符合 WAI-ARIA 對話框模式「關閉後焦點回到觸發元素」的要求。Element Plus 的 focus trap 釋放時會 `tryFocus(lastFocusBeforeTrapped ?? document.body)`，自己的還原必須在它之後執行才不會被覆蓋。實作優先掛 `el-drawer` 的 `@close-auto-focus`（drawer 把 focus trap 的 `onFocusAfterReleased` 接到這個事件）：先 `event.preventDefault()` 阻止預設還原，再 `menuButton.focus()`；若此事件在 jsdom 或實際瀏覽器中不觸發，改掛 `@closed`（after-leave 之後，焦點會晚到動畫結束）。**不得**掛在 `@close` 或 `update:modelValue` 的 watch，這兩者早於 focus trap 釋放，會被預設還原覆蓋；2.3 的「不預先聚焦」測試能擋住這種實作。程式化聚焦在指標操作後不會顯示 `:focus-visible` 外框，觸控使用者不會看到多餘的框線。
  - **替代方案**：把 Scenario 限定為「以鍵盤開啟」。手機點擊正是這個功能的主要使用情境，限定後等於不保證主要情境，不採用。
- 登出鈕放在抽屜內選單下方。主辦方名稱連結留在頂列，不在抽屜中重複。抽屜內的登出**先關閉抽屜，再呼叫既有的登出流程**：若先 `await authStore.logout()` 再關閉，`logout` 失敗或 `router.push` 被守衛擋下時，遮罩、body 捲動鎖與 focus trap 會一直蓋著畫面。
- 抽屜 Teleport 到 `body`，不會帶 `AdminLayout.vue` `<style scoped>` 的 data-v 屬性，寫在 scoped 區塊的抽屜樣式會靜默失效。抽屜內若需自訂樣式（例如直式選單的 `border-right`、登出鈕間距），另開不帶 scoped 的 `<style>` 區塊，選擇器一律以 `.admin-nav-drawer` 開頭限定範圍。
- 選單按鈕使用 `el-button`（與既有登出鈕一致），內容為 inline SVG 三條線（`aria-hidden="true"`），`aria-label="開啟後台選單"`，`aria-expanded` 綁定抽屜開關狀態；觸控區至少 44×44px。不為一個圖示安裝 icons 套件。
- 主辦方名稱在任何寬度都以 `text-overflow: ellipsis` 截斷（`min-width: 0`、`white-space: nowrap`、`overflow: hidden`；桌面另設 `max-width`），`title` 屬性帶完整名稱。

### 決策 5：驗證方式

- **行為**（抽屜開關、選單項目、導覽、Esc 與焦點、登出）：Vitest，`AdminLayout.test.ts` 沿用既有的真實 router 與 mock API 寫法，以 `attachTo: document.body` 掛載（抽屜 Teleport 到 body）。抽屜內的元素一律在 `.admin-nav-drawer` 內查找，並先斷言命中數為 1 再操作。「抽屜關閉」以抽屜元素不可見（`v-show` 隱藏）加上 `aria-expanded="false"` 判定，不以元素不存在判定。焦點測試**不預先聚焦**選單按鈕，直接點擊開啟（模擬觸控與 Safari 的情境），斷言 Esc 後 `document.activeElement` 是選單按鈕；這樣只靠 Element Plus 預設行為的實作會失敗。**如果 jsdom 無法觸發抽屜的 Esc 關閉或關閉事件**，不得把該 Scenario 改用瀏覽器量測了事，必須回報使用者，由使用者決定是否把例外範圍擴大到該 Scenario。
- **版面**：沿用 mobile-layout-p0-fixes 的瀏覽器量測腳本做法。同源 iframe 設定 320／390／720／721／800／1280px 寬（720 與 721 是斷點兩側），以 `scrollbar-width: none` 隱藏 iframe 的捲軸，使 `clientWidth` 等於 W。量測使用 **Admin 角色**（5 個選單項目，最寬的情況）並已切換 Organizer，在 `/admin/redeem` 與 `/admin/venues` 量測；另外在 W = 320、390 時，以尚未切換 Organizer 的 Admin 開啟 `/admin/organizers`（頂列顯示「尚未切換主辦方」，對應既有 AWU-NAV-004 的情境）量測導覽列。量測項目：
  - `.admin-nav` 內每個元素的右緣（`left + scrollWidth`）≤ W，且沒有任何元素 `scrollWidth > clientWidth`（被截斷的主辦方名稱除外，它本來就以 `overflow: hidden` 截斷）；
  - 核銷頁的 `document.documentElement.scrollWidth` ≤ W；
  - 窄螢幕時選單按鈕可見且外框 ≥ 44×44，水平選單 `display: none`；寬螢幕時相反；
  - `INJECT_LONG_TEXT`：把主辦方名稱換成 100 字元的無斷點字串後重量一次。
- **無效量測**：任一條件成立時，該次量測無效，視為未通過（不得當作通過，也不得略過不記錄）：
  - `clientWidth` ≠ W；
  - iframe 實際的 `location.pathname` 不等於目標路徑（例如未登入被導向 `/login`、未切換 Organizer 被導向選擇主辦方頁）；
  - `.admin-nav` 的命中數不是 1，或其中可量測的元素數為 0；
  - 選單按鈕（`[aria-label="開啟後台選單"]`）、水平選單（`.admin-nav-menu`）或主辦方名稱入口（`.organizer-indicator`）的命中數不是 1，或主辦方名稱入口不可見（修改前的基準量測不檢查選單按鈕，因為它尚未存在）；
  - 開啟 `INJECT_LONG_TEXT` 時，替換後的主辦方名稱元素命中數不是 1，或其文字長度不是 100；
  - 任何量測到的座標或尺寸不是有限數值，或可見元素的寬高 ≤ 0。
- 修改前先量一次基準值，用以證明這個檢查真的能抓到問題：W = 390 時導覽列必須溢出（AWU-MOBILE-NAV-001 的失敗情境）；W = 721 且開啟 `INJECT_LONG_TEXT` 時導覽列必須溢出（AWU-MOBILE-NAV-002 的失敗情境，目前名稱沒有截斷）。任一項沒有重現時停止並回報使用者。若基準量測發現核銷頁的**內容本身**（不是導覽列）溢出，停下來回報使用者，不得自行擴大範圍。
- 後台需要登入，量測前由使用者在瀏覽器手動登入（需要通過驗證碼）並切換到一個 Approved Organizer。

**不採用**：用 Vitest 讀取 `<style>` 字串斷言含有 media query。只能驗證寫法，不能驗證行為（違反 Rule 9）。

## Risks / Trade-offs

- [瀏覽器量測沒有自動化，之後的修改可能再次造成溢出] → 與 mobile-layout-p0-fixes 相同，暫時接受；量測腳本存為 `measure-admin-nav.js` 隨 change 歸檔、量測結果記錄在 tasks.md，方便重跑，引入 E2E 框架後再自動化。
- [桌面瀏覽器的傳統捲軸佔 15px，W = 721～735 時實際內容寬度比 media query 判定的寬度少 15px] → 這個區間沒有實測，與 mobile-layout-p0-fixes 的已知偏差相同；721px 寬時水平選單的實際寬度遠小於 706px，預期不受影響，量測會記錄 721 的實際餘裕。
- [選單按鈕與水平選單同時存在 DOM，既有測試以 `.admin-nav-menu` 找選單項目] → 水平選單保留 `.admin-nav-menu`，抽屜內選單使用不同的 class，既有測試不受影響；抽屜首次開啟前不渲染，既有測試從未開啟抽屜，不會找到重複項目。
- [Element Plus 升級後 focus trap 或 Esc 行為改變] → 由 Vitest 的焦點與 Esc 測試把關。
- [瀏覽器量測取代自動化測試] → 見下方「AC 測試對應例外」。

## AC 測試對應例外

| 項目 | 內容 |
| --- | --- |
| 偏離的規則 | CLAUDE.md「OpenSpec 工作流規則 → Task 執行前」第 2 點：每條 AC 至少對應一項單元測試或整合測試 |
| 適用範圍 | 僅限 AWU-MOBILE-NAV-001、AWU-MOBILE-NAV-002（版面溢出、斷點兩側的版面切換、觸控尺寸）。AWU-MOBILE-NAV-003～007 仍以 Vitest 驗證，不適用此例外 |
| 替代驗證 | tasks.md 的瀏覽器量測腳本（決策 5）；修改前先量基準值，390px 必須重現導覽列溢出 |
| 理由 | jsdom 不做版面計算，也不套用 media query；只為這兩條需求引入 E2E 框架，不符合最小解法 |
| 剩餘風險 | 沒有回歸保護，之後的修改可能再次造成溢出而不被發現 |
| 解除條件 | 專案引入 E2E 框架（例如 Playwright）後，將此量測改寫為自動化測試並刪除本例外 |
| 核准狀態 | **已核准（2026-10-09，核准人 RD-Hank）**：使用者在 spec review 對話中，看過 cross review 的 blocking 後選擇核准此例外，範圍限 AWU-MOBILE-NAV-001、002 |
