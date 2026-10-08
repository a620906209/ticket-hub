## Context

- Vite 8.2.1（rolldown）、Vue 3.5、vue-router 5、Element Plus 2.14.4、Vitest 4。2026-10-09 baseline（web 容器 `npx vite build`）：
  - JS：一支 `index-*.js`，1,212.80 kB，gzip 394.23 kB。
  - CSS：一支 `index-*.css`，371.01 kB，gzip 50.55 kB。
- `web/src/router/index.ts` 靜態 import 2 個 layout、18 個頁面。`beforeEach` 同步讀 auth store 判斷 `meta.requiresAuth`／`requiresAdmin`／`requiresOrganizerContext`。
- `web/src/main.ts` 的載入與註冊順序：
  1. `import ElementPlus` 與 `element-plus/dist/index.css`；
  2. `./styles/morandi.css`；
  3. `./style.css`；
  4. `app.use(ElementPlus)`；
  5. `await authStore.bootstrapAsync()` 之後才 `app.use(router)`（避免初始導航競態，既有註解）。
- `jsqr` 只透過 `utils/cameraScanner.ts` → `composables/useRedemptionScanner.ts` → `pages/admin/RedemptionScannerPage.vue` 進入 bundle。
- `ElMessage`／`ElMessageBox` 已在 9 個非測試檔以具名 import 使用（`import { ElMessage } from 'element-plus'`）。template 用到 21 種 `<el-*>` 元件與 `v-loading`（10 個檔案共 12 處）。
- 沒有 Element Plus 全域型別設定（tsconfig 未引用 `element-plus/global`），template 的 `<el-*>` 目前就不做型別檢查。
- 測試：
  - 21 個檔案 import `ElementPlus` 並在掛載時放進 `global.plugins`（含 `[ElementPlus]` 與 `[ElementPlus, router]` 兩種寫法，及共用的 `pages/buyer/orderPageTestSupport.ts`）。
  - `router/index.test.ts`（11 個守衛測試）、`App.test.ts`、`layouts/BuyerLayout.test.ts`、`layouts/AdminLayout.test.ts` 共 4 個檔案使用真實 router 單例。
- 只有開發用 Vite dev server，沒有 production 靜態站台。
- 使用者 2026-10-09 決定：
  - JS 按需，CSS 維持全量；
  - 按需方式採 `unplugin-vue-components`。

## Goals / Non-Goals

**Goals:**
- 買家首頁（`/`）首屏 JS 的 gzip 總量降到 200 kB 以下（baseline 394.23 kB）。
- 後台、主辦單位頁面與 `jsqr` 不進買家首屏。
- Element Plus 未使用的元件不進任何 chunk。
- 以可重現、會失敗的自動化測試驗證上述三點。
- 路由行為、頁面外觀、色票覆寫零退化。

**Non-Goals:**
- Element Plus CSS 按需（使用者決定延後；見決策 3）。
- 手動 `manualChunks`／vendor 拆分策略調校。
- 部署後舊 chunk 失效（`Failed to fetch dynamically imported module`）的重新載入處理。目前沒有 production 站台；之後有部署 change 時再處理。
- 報告 §5.1 以外的效能項目（海報 aspect-ratio、index.html spinner 等）。
- Element Plus template 型別（`components.d.ts`）。現況就沒有，不在本 change 新增。

## Decisions

### 決策 1：頁面延遲載入，`BuyerLayout` 維持靜態
- 18 個頁面與 `AdminLayout` 都改為 `component: () => import('...')`，各自成為以該元件為 facade 的獨立 chunk（BS-004 要求 19 個不同 chunk）。各頁獨立分塊，買家進入某頁時才不會順帶下載同 chunk 的其他頁面。rolldown 預設就會把每個動態 import 目標切成自己的 entry chunk，所以這是把現有預設行為固定成可驗證的要求，不需要額外設定。
- `BuyerLayout` 是幾乎所有買家頁面（含首頁）的外框，維持靜態 import，首頁少一次 chunk 請求。
- 不使用 `webpackChunkName` 類 magic comment，由 rolldown 依檔名命名。
- 路由 path、name、meta、redirect、children 結構一律不變。

### 決策 2：`unplugin-vue-components` + `ElementPlusResolver`，`importStyle: false`，`dts: false`
- 在 `vite.config.ts` 加入 `Components({ resolvers: [ElementPlusResolver({ importStyle: false })], dirs: [], dts: false })`。
- `dirs: []`：關閉預設的 `src/components` 自動註冊。專案的本地元件都已明確 import，不讓外掛改寫它們，範圍只限 Element Plus。
- 編譯時，`<el-*>` 與 `v-loading` 會改為從 `element-plus/es` 個別 import。`v-loading` 走 resolver 預設的 `directives: true`。
- `main.ts` 移除 `import ElementPlus` 與 `app.use(ElementPlus)`。
- 已存在的 `ElMessage`／`ElMessageBox` 具名 import 不動：`element-plus` 的 ES 版可 tree-shake。
- `dts: false`：現況沒有元件型別，產生 `components.d.ts` 會讓 vue-tsc 開始檢查所有 template。這是另一件事，不混入本 change。
- 選項名稱與預設值以安裝後的套件型別定義為準（tasks 1.1 核對），不依賴文件。
- 測試沿用 `vite.config.ts` 的 plugins，所以測試中 template 也會被轉換。既有測試仍安裝 `ElementPlus` plugin：區域 import 優先於全域註冊，兩者並存無害，不需修改 21 個測試。

### 決策 3：Element Plus CSS 維持全量，順序不變
- `main.ts` 保留 `import 'element-plus/dist/index.css'`，且在 `morandi.css` 之前。
- 理由：按需 CSS 會在延遲載入的 chunk 中後注入，同權重的 `:root` 變數與 `.el-button--<色>` 覆寫會被元件樣式蓋掉，`web-color-contrast` 會退化。
- 以 BS-007 驗證首屏 CSS 中 morandi 覆寫在 cascade 中勝出。不能只比對「同選擇器的最後一次宣告」：Element Plus 本身就有權重更高、同樣設定按鈕變數的選擇器（例如 `.el-button--primary.is-plain`／`.is-text`／`.is-link`／`.is-dashed`，權重 0,2,0，高於 morandi 的 0,1,0；2026-10-09 於 `element-plus/dist/index.css` 核對）。BS-007 改為對 7 個目標元素（根元素、`.el-button`、5 個 `.el-button--<色>` 實心按鈕）以 class 集合做保守的選擇器比對，再依 `!important`／specificity／順序決定勝出值。`.is-plain` 這類規則因目標元素沒有該 class 而不納入，這符合實際：plain／text／link／dashed 變體本來就不在 `web-color-contrast` 的覆寫範圍。
- 這是靜態近似，不處理祖先元素重新定義變數後再繼承的情況，例如某容器重設 `--el-color-primary`。因此 tasks 4.3 另在瀏覽器以 `getComputedStyle(按鈕).getPropertyValue('--el-button-hover-bg-color')` 等讀取實際 cascade 結果，作為補充驗證。以 BS-006 驗證沒有任何 chunk 引入 theme-chalk 的元件樣式模組，防止之後有人把 `importStyle` 改回預設值卻沒處理 cascade。

### 決策 4：bundle 報告外掛（只在 `BUNDLE_REPORT=1` 時啟用）
- `vite.config.ts` 內聯一個小外掛。它在 `generateBundle` 把每個 chunk 寫成 JSON：
  - 每個 chunk 的欄位：`fileName`、`isEntry`、`isDynamicEntry`、`facadeModuleId`、`moduleIds`、`imports`、`dynamicImports`、`code` 的 gzip 位元組數。
  - CSS asset 另外記錄 `fileName`、`source` 的 gzip 位元組數，以及被哪些 chunk 引用（`viteMetadata.importedCss`）。
  - 輸出位置是 `web/node_modules/.tmp/bundle-report.json`。
- 報告另記錄 entry chunk 的 `fileName`，供驗證測試判定報告與 `dist/` 屬於同一次 build（決策 5）。
- 不寫進 `dist/`：避免模組路徑隨靜態檔外流。
- 只有 `BUNDLE_REPORT` 恰為 `'1'` 時才註冊外掛；未設定或為其他值時不註冊，一般 `npm run build` 與 dev server 完全不受影響（BS-013 以子行程實際 build 驗證）。
- 輸出路徑可由 `BUNDLE_REPORT_PATH` 覆寫，讓 BS-013 在暫存目錄驗證，不刪除、不覆蓋預設報告；其他測試讀取的仍是預設路徑的報告。
- gzip 用 Node `zlib.gzipSync` 預設等級。數字會與 Vite reporter 略有差異；門檻 200 kB 留有餘裕，以本報告為準。
- 如果 rolldown 的 chunk 物件缺少上述任一欄位（例如 `viteMetadata`），外掛直接 throw，不輸出不完整的報告（tasks 1.1 核對）。

### 決策 5：驗證以獨立 Vitest 設定執行，不進日常 `npm run test`
- 新增 `web/build-checks/bundle-splitting.test.ts`，以及 `web/vitest.bundle.config.ts`（`environment: 'node'`，`include` 只含 `build-checks/**`）。
- 主設定 `test.exclude` 設為 `[...configDefaults.exclude, 'build-checks/**']`，保留 Vitest 預設排除。
- `package.json` 新增 `"test:bundle": "BUNDLE_REPORT=1 vite build && vitest run --config vitest.bundle.config.ts"`。
- 理由：必須先 build 才有報告，build 約數秒，不適合每次單元測試都跑；放進一般 `test` 也會在沒有報告時失敗。
- 報告檔不存在，或報告記錄的 entry `fileName` 沒有出現在 `dist/index.html` 中，測試直接失敗並提示先跑 `test:bundle`，不 skip。不用 mtime 判定：`generateBundle` 在檔案寫入磁碟之前執行，報告的 mtime 必然早於或等於 `dist/index.html`，用先後比較會讓每次正常執行都誤判過期。entry 檔名含內容雜湊，程式碼有變動就會不同，足以判定是否同一次 build。
- 這仍是 Vitest 自動化測試（讀真實 build 產出的整合測試），不是替代驗證，不需要 CLAUDE.md 的測試種類例外。
- 「首屏集合」定義：
  1. entry chunk（`isEntry`）及其 `imports` 遞迴閉包；
  2. 加上 `facadeModuleId` 為 `src/pages/buyer/EventListPage.vue` 的 chunk，及其 `imports` 遞迴閉包；
  3. 取聯集並去重。
  - 不含 `dynamicImports`。
- `build-checks/` 加入 `tsconfig.node.json` 的 `include`，讓 `vue-tsc -b` 涵蓋型別檢查。

### 決策 6：路由與元件解析另以一般單元測試覆蓋
- BS-005 放在新檔 `web/src/router/lazyRoutes.test.ts`，不放進既有的 `router/index.test.ts`。原因：既有守衛測試會導航，vue-router 導航後會把解析好的元件寫回 route record，共用同一個 router 單例會讓「`components.default` 是函式」的計數隨執行順序變動。測試以 `vi.resetModules()` 後動態 import router，取得從未導航過的實例。
  - 斷言延遲載入的 route 恰好 19 個（18 頁 + AdminLayout），每一個載入後 `default` 都是 Vue 元件物件。打錯 import 路徑或漏改頁面都會失敗。
- BS-012 新增 `web/src/pages/elementPlusOnDemand.test.ts`：不安裝 `ElementPlus` plugin，掛載買家首頁與後台活動列表頁，涵蓋條件渲染的元件：後台用 deferred promise 在 resolve 前驗 loading mask，resolve 後（資料含需實名活動以渲染 tag）驗 table／select／switch／input／input-number／form／button／tag，另以 API reject 驗 alert；買家首頁的 alert／empty／loading 三者互斥，分三次掛載。斷言沒有 `Failed to resolve component/directive` warning，且元件根 class 有渲染。這證明 resolver 真的在編譯期轉換。既有 21 個測試全域安裝 plugin，會掩蓋漏轉換，所以需要這個測試。
- BS-006 的「未使用元件」不再只檢查 6 個列舉元件，而是檢查完整清單：bundle 中出現的元件目錄必須屬於「允許集合」，也就是從使用集合沿 Element Plus ES 原始碼的相對 import 遞迴可到達的目錄。不能直接用「124 個目錄扣掉使用集合」，因為被使用元件內部會依賴其他元件（例如 select 依賴 tag、tooltip、scrollbar），直接扣除會誤判失敗。2026-10-09 在 web 容器內用原型腳本實測：從 19 個使用目錄（含 `loading`、`message`、`message-box`）出發，可到達 33 個目錄；6 個列舉元件都不在其中。允許集合是靜態 import 的上限估計（tree-shaking 只會讓實際更少），所以「bundle ⊆ 允許集合」不會因 tree-shaking 誤判；它能抓到的是「與使用元件完全無關的元件被帶進來」。6 個列舉元件保留為前提檢查。
- 同一次實測發現：Element Plus 2.14.4 的 `table-column`、`option`、`form-item`、`dropdown-item`、`menu-item` 等目錄只有樣式，沒有 `index.mjs`，元件 JS 在 `table`、`select`、`form`、`dropdown`、`menu` 內。BS-011 的對照表必須對到含 JS 的目錄。
- BS-011（bundle 層級）補上正向檢查：src 中出現的每種 `<el-*>`／`v-loading` 都必須在某個 chunk 中找到對應元件目錄的模組；對照表沒有的新標籤直接失敗。
- 既有守衛與導航測試（`router/index.test.ts`、`App.test.ts`、兩個 layout 測試）不改，作為守衛行為不變的回歸（BS-010）。
- 既有守衛測試只涵蓋部分路由的 meta，改動 path、name、redirect 或其他路由的 meta 不一定會讓它失敗。因此另以 BS-014 用寫死的預期路由樹（path／name／meta／redirect／子 route 順序與父子關係）對 `router.options.routes` 做深度相等比對，並逐筆確認 route 對到正確的元件檔案。預期樹在改動前依現行 `index.ts` 抄寫並先確認通過，作為基準。BS-004 只確認「有 19 個動態 import」，不確認哪個 route 對到哪個檔案，元件對應由 BS-014 負責。

### 決策 7：集合型斷言不得 vacuous 通過
- bundle 驗證中所有「集合每個元素都成立」的斷言，都先斷言查找成功與集合非空，數量條件寫在各 scenario：entry 恰一個、首頁 chunk 恰一個、jsqr chunk 至少一個、驗票頁 chunk 恰一個、動態 import 路徑恰 19 個、morandi 待比對 property 非空。
- BS-007 比對的是 minify 後的 CSS。選擇器清單先以逗號拆開，各自計算 specificity；任一 morandi property 找不到可能套用的宣告時直接失敗，不略過。minifier 若合併或改寫規則而導致找不到，測試會失敗，由實作時調整解析，不放寬斷言。保守判定若納入不相干的規則而造成失敗，停下回報，不自行排除。

## Risks / Trade-offs

- **[真實 router 測試在延遲載入後變成非同步解析元件]**：`router.push` 會等 lazy component 載入完成才 resolve，既有 `await router.push` 寫法仍成立。影響範圍是 4 個使用真實 router 單例的測試檔。若有測試假設同步，tasks 3.2 修正；只能改等待方式，不得放寬斷言。
- **[200 kB 門檻是估計值]**：首頁用到的 Element Plus 元件有 dropdown（popper）、alert、empty、button、tag、loading，估計首屏約 120–160 kB gzip。實測若超過，停下回報並更新 spec，不自行調高門檻。
- **[CSS 未切]**：首屏仍下載全量 Element Plus CSS（gzip 約 50 kB）。這是使用者接受的取捨，換取 `web-color-contrast` 零退化。
- **[resolver 漏轉換的元件]**：template 若用了 resolver 不認得的寫法（例如動態 `<component :is="'el-x'">`），會變成未註冊元件、只在執行時出 warning。tasks 4.x 在瀏覽器實測主要頁面，並檢查 console 沒有 `Failed to resolve component`。目前 grep 沒有字串形式的動態元件。
- **[新增 devDependency]**：`unplugin-vue-components` 32.1.0（npm 2026-05-20 更新，peer `vue ^3.0.0`）。只在 build／dev 期使用，不進 runtime bundle。
- **[報告外掛依賴 rolldown 內部欄位]**：`viteMetadata` 是 Vite 擴充欄位，升級時可能變動。外掛缺欄位就 throw（決策 4），不會靜默通過。
