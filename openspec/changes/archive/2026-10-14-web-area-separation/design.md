## Context

後端權限已分三層：任何登入者都是會員；切換 Organizer 後 Access Token 帶 `OrganizerId` claim，才能使用主辦方功能；`Admin` 角色只用在主辦方審核（`AdminOrganizersController` 的 `AdminOnly`）。前端現況：

- `web/src/router/index.ts`：`/`（`BuyerLayout`，靜態 import）與 `/admin`（`AdminLayout`，延遲載入）兩棵路由；審核頁 `/admin/organizers`（`requiresAdmin`）掛在 `/admin` 之下，與主辦方後台共用版面。
- `AdminLayout.vue`：水平選單＋窄螢幕抽屜共用 `menuItems`，`Admin` 多一項「主辦方審核」；頂列右側有目前主辦方名稱入口（連到「我的主辦方」）與登出。沒有回到前台的入口。
- `BuyerLayout.vue`：會員下拉選單有「實名資料」「我的主辦方」「主辦方審核」（`Admin`）「登出」。
- 色票在 `web/src/styles/morandi.css`，`body` 背景為 `--color-bg`（`#f3f0ea`），三區相同；對比度由 `morandi.contrast.test.ts` 把關，`--color-text-secondary`（`#736b61`）對 `--color-bg` 只有 4.61:1，任何比 `#f3f0ea` 更暗的底色都可能跌破 4.5。

## Goals / Non-Goals

**Goals:**

- 一眼看出目前在哪一區：背景色、導覽列色條與文字區域名稱三者並用（顏色不是唯一線索）。
- 三區之間有一致且明顯的切換入口，`Admin` 專屬區域只對 `Admin` 顯示。
- 能確認目前登入的帳號，以及區分同名的主辦方。

**Non-Goals:**

- 不改後端、API、DB；不改任何路由守衛的權限判斷邏輯（只搬移審核頁的路徑）。
- 不改主辦方後台網址（`/admin/*` 保留）。
- 不改登入後的預設導向。
- 區域切換不加動畫。

## Decisions

### 決策 1：主辦方後台保留 `/admin/*`，只把審核頁搬到 `/platform/organizers`

使用者看到的是版面與文字，不是網址；把 `/admin` 改名為 `/organizer` 要改 router、`LoginPage`／`MyOrganizersPage` 的導向、各頁內的 `/admin/orders/${id}` 連結、所有測試與 admin-web-ui spec 中數十處路徑，還會與既有的會員頁 `/organizers` 只差一個字母，容易混淆。改名收益只有「網址語意」，成本與風險不成比例。

審核頁則必須搬出 `/admin`：它要用不同的版面（平台管理），而 vue-router 的版面由父路由決定。新增頂層路由：

```
/platform                → PlatformLayout（延遲載入）
  ''                     → redirect { name: 'platform-organizers' }
  organizers             → name 'platform-organizers', meta { requiresAdmin: true }
/admin/organizers        → redirect { name: 'platform-organizers' }（舊網址、書籤）
```

`requiresAdmin` 的守衛判斷不變（`to.matched.some(...)`），掛在子路由即可。舊網址的 redirect 放在 `/admin` 的 children 中；vue-router 在 redirect 時不會載入父路由的元件，不會先閃出主辦方後台版面。

**不採用**：把 `/admin` 改名為 `/organizer`（理由如上）；審核頁留在 `/admin` 只換選單位置（版面無法區分，違背本次目標）。

### 決策 2：共用 `AreaSwitcher` 元件做區域切換

三個版面放同一個 `web/src/components/AreaSwitcher.vue`（`el-dropdown`，`trigger="click"`），觸發按鈕文字為目前區域名稱（「會員前台」「主辦方後台」「平台管理」），前方有該區的色塊。目前區域由 prop `current: 'buyer' | 'organizer' | 'platform'` 指定，不從路由推導（版面本身就知道自己是哪一區，避免路由改名後元件判斷錯誤）。三個版面都把切換鈕放在導覽列最右邊，其他功能都在它左邊，切換區域時位置不會跳動：會員前台依序為「我的訂單」、會員選單、切換鈕；主辦方後台依序為主辦方名稱、登出、切換鈕（窄螢幕不顯示登出）；平台管理依序為水平選單、登出、切換鈕（2026-10-14 依使用者回饋統一）。

展開內容依序：

1. 「登入帳號：{email}」——停用的項目，只顯示，不可點。
2. 「會員前台」→ `/`
3. 「主辦方後台」→ `/admin`（沒有切換 Organizer 時，既有守衛會導向「我的主辦方」，那裡可以切換或申請）
4. 「平台管理」→ `/platform`，僅 `authStore.isAdmin` 時顯示
5. 目前所在區域的項目顯示為停用並標示「（目前）」，不提供點擊。

只在已登入時顯示（`BuyerLayout` 未登入時不顯示；主辦方後台與平台管理一定已登入）。

原本的入口調整：

- `AdminLayout` 的 `menuItems` 移除「主辦方審核」，四項固定。
- `BuyerLayout` 會員下拉選單移除「主辦方審核」；「我的主辦方」保留（它是會員層級的功能：查看、申請、切換）。
- `AdminLayout` 的主辦方名稱入口保持現狀（連到「我的主辦方」可切換）。

**不採用**：在前台導覽列直接放「主辦方後台」「平台管理」兩個文字連結——會員前台頂列已經有「活動」「我的訂單」與會員名稱，窄螢幕會擠；三個版面也需要各自的回程入口，用同一個元件才能保證三處行為一致。

### 決策 3：區域色票與套用位置

在 `morandi.css` 新增 token（數值已用 WCAG 公式試算，實作時由 `morandi.contrast.test.ts` 把關）：

| token | 值 | 用途 |
| --- | --- | --- |
| `--color-bg-organizer` | `#eef2f3` | 主辦方後台頁面底（冷灰藍，取 primary 色相） |
| `--color-bg-platform` | `#f4eef1` | 平台管理頁面底（灰紫） |
| `--color-area-organizer` | `#616f72` | 主辦方後台色條與切換鈕色塊（= primary） |
| `--color-area-platform` | `#7d6474` | 平台管理色條與切換鈕色塊 |

試算結果：五個文字色與兩個主色在兩個新底色上的最低對比度為 4.56:1（`--el-color-primary` 對 `#f4eef1`）；兩個強調色對白底與各自底色都高於 4.5:1。會員前台維持 `--color-bg`，不加色條，外觀不變。

邊界（3:1）：`--el-border-color` `#8e877c` 對 `#eef2f3` 約 3.16、對 `#f4eef1` 約 3.11；hover 邊框 `#797369` 約 4.17／4.11，皆 ≥ 3:1，但裕度只有約 0.1，由 WCC-007／008 把關。

套用方式：版面根元素（`.buyer-layout`、`.admin-layout`、`.platform-layout`）設 `min-height: 100svh` 與對應背景色（會員前台為 `--color-bg`，與 `body` 同色，外觀不變，只是讓量測有明確的根元素可讀）；導覽列（`.admin-nav`、`.platform-nav`）設白底（`--color-bg-elevated`）與 `border-top: 4px solid var(--color-area-*)`，讓頁面底色與導覽列的白有明顯分界。`body` 的背景不改。

**不採用**：整條導覽列改深色底、白字——要覆寫 Element Plus `el-menu` 的所有狀態色，並重新驗證 hover／active 的對比度，超出本次範圍。

### 決策 4：平台管理版面

`PlatformLayout.vue` 的導覽列只有：`AreaSwitcher`、「主辦方審核」（`el-menu` 水平選單，一項）、登出。只有一個選單項目，窄螢幕也放得下，不做抽屜。導覽列在 320 ≤ W ≤ 1280 不溢出，以瀏覽器量測驗證（見決策 6）。

### 決策 5：顯示登入帳號與主辦方代碼

- 帳號：`AreaSwitcher` 展開內容的第一項顯示 `authStore.member.email`（決策 2）。
- 代碼：「我的主辦方」清單新增「代碼」欄，顯示 Organizer Id 的前 8 碼（GUID 第一段）。只在這個清單顯示，導覽列的主辦方名稱入口不加，避免影響 AWU-MOBILE-NAV-007（`title` 為完整名稱）與窄螢幕寬度。8 碼不是唯一保證，但只用於人眼區分少數幾個同名項目，碰撞機率可忽略；完整 Id 不需要顯示。

### 決策 6：驗證方式

- **行為**（守衛、redirect、選單項目、切換選單的內容與導覽、代碼欄）：Vitest。路由沿用 `router/index.test.ts` 的真實 router 寫法；版面沿用 `AdminLayout.test.ts`／`BuyerLayout.test.ts` 的掛載方式。`el-dropdown` 的選單 Teleport 到 body，掛載時用 `attachTo: document.body`，先點觸發按鈕再在 dropdown 內查找，命中數先斷言為 1。
- **色票**：擴充 `morandi.contrast.test.ts` 的「頁面底」集合，加入兩個新底色；新增強調色對白底、對各自底色的檢查。
- **區域背景確實套用**：jsdom 不計算 CSS 變數的最終樣式，所以背景色是否真的出現在畫面上，以瀏覽器量測腳本讀 `getComputedStyle` 驗證（三個版面各一頁）。
- **版面溢出**：沿用 admin-mobile-nav 的 `measure-admin-nav.js` 做法，複製為本 change 的 `measure-area-nav.js`，量測項目新增：
  - 主辦方後台：已切換 Organizer 的 `Admin`（現在是 4 個選單項目＋切換鈕）在 W = 320、390、720、721、800、1280 量 `/admin/redeem`、`/admin/venues`，含 100 字元主辦方名稱注入；
  - 平台管理：`Admin` 在相同寬度量 `/platform/organizers`；
  - 會員前台：已登入的會員在 W = 320、390、1280 量 `/` 的頂列（新增切換鈕後不得溢出）；8.1 審查後加量斷點兩側 361、480、481，並檢查單行、會員名稱未被上下裁切、30 字元會員名稱注入。
- 量測無效條件比照 admin-mobile-nav（`clientWidth` ≠ W、路徑不符、選擇器命中數不是 1 等），並新增：`.area-switcher` 命中數不是 1。
- 修改前先量基準值：W = 320 時會員前台頂列（目前沒有切換鈕）記錄餘裕；主辦方後台記錄現況。基準不要求重現溢出（本 change 不是修溢出，而是確保新增元素後不溢出），但需記錄數值以比對。

## Risks / Trade-offs

- [會員前台頂列在 320px 新增切換鈕後溢出] → 決策 6 的量測涵蓋；若溢出，切換鈕在窄螢幕只顯示色塊與縮寫文字，或把「我的訂單」收進會員下拉選單——任一做法都屬於版面細節，在 tasks 中處理，若需要改 spec 文字則先回報使用者。
- [背景色差異太淡，使用者覺得看不出來] → AA 限制下底色只能接近白色；主要區分靠導覽列 4px 色條與區域名稱。實作後請使用者在瀏覽器確認，若仍不夠明顯，下一步是加粗色條或加深導覽列，不改底色。
- [`Admin` 的書籤或習慣仍走 `/admin/organizers`] → 決策 1 的 redirect 處理。
- [首屏多載入 `AreaSwitcher`] → `BuyerLayout` 已使用 `el-dropdown`，新增元件只多幾百位元組；由 BS-001（200 kB 上限）把關。
- [瀏覽器量測沒有自動化] → 與 admin-mobile-nav 相同的已知風險，見「AC 測試對應例外」。

## 安全確認

本 change 只修改前端，觸及「身份驗證／授權邏輯」（路由搬移、依角色顯示入口）。

- **輸入驗證**：不新增表單、API 參數或 URL query；沒有拼接 SQL 或 shell 指令。顯示的使用者資料只有 Email 與主辦方名稱，皆來自既有 API 回應。
- **資料庫**：不修改後端；不新增 API 呼叫（Email 來自既有的 `authStore.member`，主辦方清單沿用 `GET /api/organizers/mine`）。
- **權限**：審核頁搬移後仍以 `meta.requiresAdmin` 經既有 `router.beforeEach` 把關；舊網址 redirect 到新路由後同樣經過守衛（redirect 後的目標仍走 `beforeEach`）。`AreaSwitcher` 的「平台管理」只在 `isAdmin` 時顯示，但顯示與否不是權限邊界——真正的邊界在後端 `AdminOnly` Policy，本 change 不修改。
- **前端 XSS**：Email、主辦方名稱與代碼以 Vue 文字插值呈現，不使用 `v-html`。
- **Auth Header**：不新增 API 呼叫。
- **機敏資訊**：Email 只顯示給登入者本人；不寫入 log、URL 或 localStorage。

## AC 測試對應例外

| 項目 | 內容 |
| --- | --- |
| 偏離的規則 | CLAUDE.md「OpenSpec 工作流規則 → Task 執行前」第 2 點：每條 AC 至少對應一項單元測試或整合測試 |
| 適用範圍 | 僅限：既有 AWU-MOBILE-NAV-001、002（本 change 修改其內容，例外沿用）；新增的 AWU-AREA-LAYOUT-001（三區背景色與色條實際套用）、AWU-AREA-NAV-OVERFLOW-001（平台管理與會員前台頂列不溢出，含會員前台單行判定與 30 字元會員名稱量測）；AWU-AREA-SWITCH-006 中「W = 320／390／480／481／1280 的可見文字」這一部分（`aria-label`、`title` 與縮寫元素有無仍以 Vitest 驗證）。其餘 Scenario 一律以 Vitest 驗證 |
| 替代驗證 | `measure-area-nav.js` 瀏覽器量測（決策 6），驗證 MOBILE-NAV-001、002、AREA-NAV-OVERFLOW-001、AREA-LAYOUT-001；`measure-switch-abbreviation.js` 瀏覽器量測，驗證 SWITCH-006 的可見文字部分。每個 Scenario 都有可判定的通過／失敗條件與無效量測條件，並各有自我檢查確認失敗情境會判為失敗（`SELF_CHECK_INJECT_WIDE`、`SELF_CHECK_BREAK_COLORS`、`SELF_CHECK_BREAK_LINES`、`SELF_CHECK_BREAK_TRUNCATION`、`SELF_CHECK_FORCE_FULL_LABEL`） |
| 理由 | jsdom 不做版面計算、不套用 media query、不解析 CSS 變數的最終值；只為這幾條需求引入 E2E 框架，不符合最小解法 |
| 剩餘風險 | 沒有回歸保護，之後的修改可能造成溢出或背景色失效而不被發現；會員名稱「不被上下裁切」只以 `scrollHeight` ≤ `clientHeight` 代理判定，字形超出字型度量的情況量測不到；有傳統捲軸、W 約 463～480 的「完整名稱＋寬間距」組合未直接量測，以 W = 481 的餘裕推算；macOS／Android 字型未實測 |
| 解除條件 | 專案引入 E2E 框架（例如 Playwright）後改寫為自動化測試並刪除本例外 |
| 核准狀態 | **已核准**：AWU-MOBILE-NAV-001、002 於 2026-10-09 核准；擴大到 AWU-AREA-LAYOUT-001 與 AWU-AREA-NAV-OVERFLOW-001 於 2026-10-13 核准（核准人 RD-Hank）；擴大到 AWU-AREA-SWITCH-006 的可見文字部分於 2026-10-14 核准（核准人 RD-Hank） |
