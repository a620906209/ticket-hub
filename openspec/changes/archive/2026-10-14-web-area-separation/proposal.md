## Why

2026-10-13 示範資料走查時，使用者覺得會員、主辦方、平台 Admin 三種身分在前端混在一起：平台 Admin 專屬的「主辦方審核」跟活動、場館、核銷放在同一個 `/admin` 後台選單；進主辦方後台的入口藏在會員下拉選單；三個區域外觀完全一樣，看不出自己在哪一區、用哪個帳號。走查中實際發生過「用另一個同名主辦方的帳號核銷，看到查無此票券」而找不出原因的情況。後端的權限模型本來就分開（會員／切換 Organizer／`Admin` 角色），問題只在前端的呈現。

## What Changes

- 前端分成三個區域，各自有獨立版面：
  - **會員前台**（`/`，既有 `BuyerLayout`）
  - **主辦方後台**（沿用 `/admin/*` 網址，既有 `AdminLayout`）：場館、活動、訂單、核銷、銷售報表
  - **平台管理**（新增 `/platform/*`，新的 `PlatformLayout`，僅限 `Admin` 角色）：主辦方審核頁從 `/admin/organizers` 移到 `/platform/organizers`；舊網址導向新網址
- 三個區域使用不同的頁面背景色（會員前台維持現有暖米色，主辦方後台為冷灰藍，平台管理為灰紫），主辦方後台與平台管理的導覽列頂端另有該區的色條；背景色不是唯一的區分方式，導覽列一律以文字顯示目前區域名稱。新增色票全部維持 WCAG AA。
- 三個版面的導覽列都有同一個「區域切換」選單：顯示目前區域名稱，展開後可前往其他區域（「平台管理」只對 `Admin` 顯示），並顯示目前登入帳號的 Email，讓使用者能確認自己用哪個帳號。
- 主辦方後台導覽選單不再包含「主辦方審核」；會員下拉選單的「主辦方審核」入口移除，改由區域切換選單進入平台管理。
- 「我的主辦方」清單新增「代碼」欄（Organizer Id 前 8 碼），同名主辦方可以區分。
- **不包含**：主辦方後台網址從 `/admin` 改名（見 design.md 決策 1）；登入後的預設導向（`Admin` 登入後仍導向 `/admin`）；後端、API、DB 任何修改；區域切換的動畫。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `admin-web-ui`：路由守衛的審核頁路徑改為 `/platform/organizers`；後台導覽入口規則移除「主辦方審核」；新增「三區版面與區域切換」需求；「我的主辦方」清單新增代碼欄；窄螢幕導覽需求的抽屜選單項目同步調整並涵蓋平台管理版面。審核頁不再使用主辦方後台版面，因此移除依附於 `/admin/organizers` 的情境：AWU-NAV-004 整條 Scenario（未切換的 Admin 在審核頁看到後台選單），以及 AWU-MOBILE-NAV-001 的審核頁量測、AWU-MOBILE-NAV-004 的「在審核頁選取場館管理」情境。
- `buyer-web-ui`：「Admin 可從買家端版面進入主辦方審核頁」改為由區域切換選單進入平台管理。
- `web-color-contrast`：「頁面底」的定義納入兩個新區域背景色，新增區域強調色對白底的對比度要求。
- `web-bundle-splitting`：延遲載入的 route 元件由 19 個變為 20 個（新增 `PlatformLayout`），路由樹對照基準更新。

### 拆分評估

影響 4 個 capability，超過 3 個的門檻。評估後不拆：`web-color-contrast` 與 `web-bundle-splitting` 只是本次版面調整的連帶數字與色票更新（分別約 1 條 Requirement 的修改），單獨拆成 change 沒有可獨立交付的價值，反而要讓路由基準與色票在兩個 change 之間處於不一致狀態。tasks 共 20 項，未超過 30 項。

## Impact

- 程式碼：`web/src/router/index.ts`、`web/src/layouts/AdminLayout.vue`、`web/src/layouts/BuyerLayout.vue`、新增 `web/src/layouts/PlatformLayout.vue`、新增 `web/src/components/AreaSwitcher.vue`、`web/src/pages/organizers/MyOrganizersPage.vue`、`web/src/styles/morandi.css`
- 測試：`router/index.test.ts`、`router/lazyRoutes.test.ts`（BS-005／BS-014 基準）、`layouts/*.test.ts`、`styles/morandi.contrast.test.ts`、`MyOrganizersPage` 測試；`web/build-checks/` 的 bundle 驗證（20 個 chunk）
- 版面溢出無法在 jsdom 驗證，沿用 admin-mobile-nav 已核准的瀏覽器量測做法並擴大到平台管理版面（見 design.md「AC 測試對應例外」，需使用者核准擴大範圍）
- 不影響後端、API、DB；不新增相依套件
