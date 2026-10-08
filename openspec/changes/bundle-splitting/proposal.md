## Why

前端目前只產出一支 JS（1,212.80 kB / gzip 394.23 kB，2026-10-09 於 web 容器 `npx vite build` 實測），買家第一次打開首頁就得下載全部 19 個頁面、整套 Element Plus 與只有後台驗票頁才用到的 `jsqr`。`docs/ui-ux-review-2026-10-08.md` 第 0 節把它列為 P0 第 2 項、第 5.1 節列出原因：router 靜態 import 全部頁面、`app.use(ElementPlus)` 全量註冊。行動網路下首屏載入時間是買家體驗的直接瓶頸，也是之後 editorial redesign 的前提。

## What Changes

- 路由頁面改為 `() => import()` 延遲載入，每個頁面（含後台 layout）各自成為獨立 chunk；買家首頁只下載首頁需要的程式。
- Element Plus 的 **JS** 由全量註冊改為按需引入：template 用到的元件與 `v-loading` 指令在編譯時改為個別 import，未使用的元件不進 bundle。Element Plus 的 **CSS 維持全量引入**、載入順序維持在 `morandi.css` 之前，避免按需樣式在延遲載入的 chunk 中後注入、蓋掉 `web-color-contrast` 的色票覆寫（使用者 2026-10-09 決定；CSS 按需留待之後的 change）。
- `jsqr` 只跟著後台驗票頁的 chunk 載入，不出現在買家首屏會下載的任何 chunk。
- 新增 build 產出檢查：以 Vite 外掛在指定環境變數下輸出 chunk／模組對照表，並以獨立的 Vitest 設定驗證首屏 chunk 組成與 gzip 大小上限（不加入日常 `npm run test`）。
- 不改變任何頁面的功能、路由路徑、守衛行為與外觀。

## Capabilities

### New Capabilities
- `web-bundle-splitting`: 前端 production build 的程式碼切割要求——買家首屏下載的 chunk 組成、gzip 大小上限、後台頁面與 `jsqr` 的隔離，以及由 build 產出驗證的方式。

### Modified Capabilities
（無。路由路徑、守衛與頁面行為不變；`web-color-contrast` 的色票覆寫須在按需引入後仍然生效，但其 requirement 本身不變，由本 change 的 spec 另訂不退化條件。）

## Impact

- 程式碼：`web/src/router/index.ts`（頁面改 dynamic import）、`web/src/main.ts`（移除 `app.use(ElementPlus)`，保留 `element-plus/dist/index.css` 與其後的 `morandi.css` 引入順序）、`web/vite.config.ts`（按需引入外掛、bundle 報告外掛）、新增 bundle 驗證測試與其 Vitest 設定、`web/package.json` scripts。
- 相依套件：新增 devDependency `unplugin-vue-components`（使用者 2026-10-09 決定）；無新增 runtime 相依。
- 測試：既有 21 個以全域 `ElementPlus` plugin 掛載的測試檔，與 4 個使用真實 router 的測試檔（`router/index.test.ts`、`App.test.ts`、`BuyerLayout.test.ts`、`AdminLayout.test.ts`），須在延遲載入後仍通過；新增 `router/lazyRoutes.test.ts`（BS-005 延遲載入、BS-014 路由設定與元件對應不變）與不裝全域 plugin 的元件解析測試（BS-012）。
- 部署：本專案目前只有開發用 Vite dev server（`web/Dockerfile`），沒有 production 靜態站台；新版部署後舊 chunk 失效的處理不在本 change 範圍。
- 文件：`docs/ui-ux-review-2026-10-08.md` §5.1、§0 標記修正結果。
