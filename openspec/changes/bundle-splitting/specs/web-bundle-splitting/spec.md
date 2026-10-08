## ADDED Requirements

### Requirement: 買家首屏只下載首頁需要的程式
前端 production build（`web/` 執行 `vite build`）中，「首屏集合」定義為：entry chunk 及其靜態 import（`imports`）遞迴閉包，聯集首頁元件 `src/pages/buyer/EventListPage.vue` 所在 chunk 及其靜態 import 遞迴閉包（不含 `dynamicImports`）。首屏集合的 JS gzip 總量 MUST 不超過 200 kB（Node `zlib.gzipSync` 預設等級計算），且 MUST NOT 包含後台、主辦單位頁面與 `jsqr`。

計算首屏集合前，驗證 MUST 先確認：恰好一個 `isEntry` chunk；`src/pages/buyer/EventListPage.vue` 恰好出現在一個 chunk 的 `moduleIds` 中。任一查找失敗時測試 MUST 失敗，不得以較小的集合繼續判定。

#### Scenario: BS-001 首屏 JS gzip 總量
- **WHEN** 以 `BUNDLE_REPORT=1` 執行 `vite build`，讀取 bundle 報告並計算首屏集合中所有 JS chunk 的 gzip 位元組數總和
- **THEN** 總和不超過 200 × 1000 位元組

#### Scenario: BS-002 首屏不含後台、主辦單位與其他買家頁面
- **WHEN** 列出首屏集合所有 chunk 的 `moduleIds`
- **THEN** 首屏集合同時含 entry chunk 與首頁 chunk（兩者可以是同一個 chunk）；不含任何 `src/pages/admin/` 下的模組、`src/layouts/AdminLayout.vue`、`src/pages/organizers/` 下的模組，以及 `src/pages/buyer/` 下除了 `EventListPage.vue` 以外的頁面模組

#### Scenario: BS-003 jsqr 只隨驗票頁載入
- **WHEN** 找出 `moduleIds` 含 `node_modules/jsqr/` 模組的所有 chunk，以及含 `src/pages/admin/RedemptionScannerPage.vue` 的 chunk
- **THEN** 含 jsqr 的 chunk 至少一個，驗票頁 chunk 恰好一個；含 jsqr 的 chunk 都不在首屏集合內，且每一個都在驗票頁 chunk 的靜態 import 遞迴閉包內（含該 chunk 本身）

### Requirement: 路由頁面延遲載入且路由行為不變
`web/src/router/index.ts` 中，除了 `BuyerLayout` 以外的每一個 route 元件（18 個頁面與 `AdminLayout`）MUST 以動態 import 載入，各自成為以該元件為 facade 的獨立 chunk（19 個元件對應 19 個不同 chunk），且都不在 entry chunk 的靜態閉包內。路由的 path、name、meta、redirect 與巢狀結構，以及 `beforeEach` 守衛行為 SHALL 不變。

#### Scenario: BS-004 頁面不在 entry 靜態閉包內
- **WHEN** 以正規表示式從 `web/src/router/index.ts` 原始碼取出所有 `import('...')` 的模組路徑，並在 bundle 報告中找出每一個模組所在的 chunk
- **THEN** 取出的路徑恰好 19 個（18 個頁面與 `src/layouts/AdminLayout.vue`），每一個都在報告中恰好一個 chunk 內找到；該 chunk 是 dynamic entry（`isDynamicEntry` 為 true），其 `facadeModuleId` 就是該模組，且不在 entry chunk 的靜態 import 遞迴閉包內；19 個模組對應的 chunk 兩兩不同（共 19 個不同的 chunk）

#### Scenario: BS-005 每個延遲載入的 route 元件都能載入
- **WHEN** 在獨立測試檔中以 `vi.resetModules()` 後動態 import `web/src/router/index.ts`，取得尚未導航過的全新 router，走訪 `router.getRoutes()`，對每個 `components.default` 為函式的 route 呼叫並等待（vue-router 導航後會把已解析的元件寫回 route record，所以不得與會導航的測試共用 router 實例）
- **THEN** 這類 route 恰好 19 個，每一個都解析成 `default` 為 Vue 元件物件的模組

#### Scenario: BS-014 路由設定與元件對應不變
- **WHEN** 在 BS-005 的獨立測試檔中，以 `vi.resetModules()` 後取得的全新 router，遞迴走訪 `router.options.routes`，把每個 route 轉成 `{ path, name, meta, redirect, children }`（未設定的欄位以 `undefined` 表示，`children` 依原順序遞迴轉換），與測試內寫死的預期樹做深度相等比對；預期樹依本 change 開始前的 `web/src/router/index.ts` 逐項抄寫（2 個頂層 route；`/` 下 10 個子 route；`/admin` 下 9 個子 route，其中 `''` 為 `redirect: { name: 'admin-venues' }`）
- **THEN** 
  - 兩者深度相等（任何 path、name、meta、redirect、子 route 數量、順序或父子關係的差異都使比對失敗）；
  - 預期樹中每個有元件的 route，以測試內寫死的「route name（頂層 layout 以 path）→ 元件檔案路徑」對照表，解析出的 `default` 與直接 `import` 該檔案取得的 `default` 為同一物件（共 20 筆：18 個頁面與 2 個 layout；`BuyerLayout` 為靜態元件、直接比對）；
  - 對照表筆數等於預期樹中有元件的 route 數量，不得少比對

#### Scenario: BS-010 守衛與導航行為回歸
- **WHEN** 執行既有的 `router/index.test.ts`（守衛測試）、`App.test.ts`、`layouts/BuyerLayout.test.ts`、`layouts/AdminLayout.test.ts`
- **THEN** 全部通過，且沒有放寬任何斷言（只允許調整等待非同步元件載入的方式）

### Requirement: Element Plus 按需引入 JS，CSS 全量且色票覆寫仍生效
Element Plus 的元件與指令 MUST 由編譯期按需 import，不得在 runtime 以 `app.use(ElementPlus)` 全量註冊；實際使用的元件 MUST 進入 bundle，未使用的元件 MUST NOT 出現在任何 chunk。「使用集合」指 `web/src` 非測試檔中出現的 `<el-*>` 標籤、`v-loading` 指令，以及從 `element-plus`／`element-plus/es` 具名 import 的 API（如 `ElMessage`）所對應的元件目錄；「允許集合」指從使用集合各目錄的 `index.mjs` 出發，沿 `node_modules/element-plus/es` 內的相對路徑 `.mjs` import 遞迴可到達的所有 `es/components/<目錄>/`（涵蓋被使用元件內部依賴的元件，例如 select 內部使用的 tag）；不在允許集合內的元件即為「未使用」。Element Plus 的樣式 SHALL 維持由 `element-plus/dist/index.css` 全量引入，任何 chunk MUST NOT 引入 theme-chalk 的個別元件樣式模組。`web/src/styles/morandi.css` 的覆寫在首屏 CSS 中，對根元素、預設按鈕與五個實心語意色按鈕 MUST 在 CSS cascade（`!important`、specificity、來源順序）中勝出。

#### Scenario: BS-006 未使用的元件與個別元件樣式不進 bundle
- **WHEN** 依 Requirement 的定義計算使用集合與允許集合（使用集合的對照表與 BS-011 共用；具名 import 以測試內的對照表轉成目錄，對照表沒有的名稱直接失敗），並列出所有 chunk 的 `moduleIds` 中出現的 `element-plus/es/components/<目錄>/` 目錄
- **THEN** 
  - 使用集合非空，且每個使用集合目錄都有 `index.mjs`（找不到即失敗）；允許集合非空且包含使用集合；
  - bundle 中出現的每一個元件目錄都屬於允許集合（違反時列出所有不屬於允許集合的目錄）；
  - `carousel`、`tree`、`upload`、`color-picker`、`transfer`、`cascader` 既不在允許集合內，也不在 bundle 中（前提檢查：若其中任一進入允許集合，代表元件使用或依賴有變動，測試失敗並要求更新本 spec）；
  - 不含任何路徑符合 `element-plus/theme-chalk/` 或 `element-plus/es/components/*/style/` 的模組；
  - `web/src/main.ts` 不含 `app.use(ElementPlus)`
- 上述各項 MUST 是各自獨立的測試案例，任一項失敗時不遮蔽其他項的結果

#### Scenario: BS-011 實際使用的元件與指令都進入 bundle
- **WHEN** 掃描 `web/src/**/*.vue`（不含測試）中所有 `<el-*>` 標籤與 `v-loading`，以測試內的對照表轉成 Element Plus 含 JS 的元件目錄（例如 `el-option` → `select`、`el-form-item` → `form`、`el-table-column` → `table`、`el-dropdown-item` → `dropdown`、`v-loading` → `loading`；Element Plus 2.14.4 的 `table-column`、`option`、`form-item` 等目錄只有樣式、沒有 `index.mjs`，不得作為對照目標），再列出所有 chunk 的 `moduleIds`
- **THEN** 掃描到的每個標籤都在對照表中（對照表沒有的標籤直接失敗，強制更新對照表）；每一個對照出的目錄都至少有一個 `element-plus/es/components/<目錄>/` 下的模組出現在某個 chunk 中

#### Scenario: BS-012 不安裝 Element Plus 全域 plugin 時頁面仍能解析所有元件
- **WHEN** 以 `@vue/test-utils` 掛載下列情境，`global.plugins` 不含 `ElementPlus`，API 以 `vi.mock` 控制，並監聽 `console.warn`：
  - 後台 `pages/admin/EventListPage.vue`，活動列表 API 以手動控制的 deferred promise 回傳，resolve 前檢查一次、resolve 後（資料含至少一筆 `isRealNameRequired` 為 true 的活動）再檢查一次；
  - 後台 `pages/admin/EventListPage.vue`，活動列表 API reject；
  - 買家 `pages/buyer/EventListPage.vue` 三次獨立掛載：API 為 pending 的 deferred promise、resolve 為空陣列、reject
- **THEN** 
  - 後台 resolve 前：`.el-loading-mask` 存在；
  - 後台 resolve 後：`.el-table`、`.el-select`、`.el-switch`、`.el-input`、`.el-input-number`、`.el-form`、`.el-button`、`.el-tag` 都存在；
  - 後台 reject：`.el-alert` 存在；
  - 買家 pending：`.el-loading-mask` 存在；買家空陣列：`.el-empty` 存在；買家 reject：`.el-alert` 存在；
  - 所有情境中 `console.warn` 都沒有任何含 `Failed to resolve component` 或 `Failed to resolve directive` 的呼叫

#### Scenario: BS-007 首屏 CSS 中 morandi 覆寫在 cascade 中勝出
- **WHEN** 讀取首屏集合引用的 CSS asset，依來源順序解析出所有規則（逗號分隔的選擇器清單拆成個別選擇器，各自計算 specificity）；對下列 7 個目標元素，各自找出「可能套用到它」的全部宣告，依 CSS cascade 決定每個 custom property 的勝出值（先比 `!important`，再比 specificity，同 specificity 時後出現者勝）：
  - 根元素（`<html>`），比對 `morandi.css` 的 `:root` 區塊；
  - class 為 `el-button` 的按鈕，比對 `.el-button` 區塊；
  - class 為 `el-button el-button--<語意色>` 的 5 個實心按鈕，比對對應的 `.el-button--<語意色>` 區塊
- 跨 CSS asset 的來源順序：先 entry chunk 的 `importedCss`（依其陣列順序），再依首屏集合的走訪順序（entry 閉包在前、首頁 chunk 閉包在後，各自依 `imports` 陣列順序深度優先）加入其餘 chunk 的 `importedCss`，重複的 asset 只算第一次出現
- `@media`、`@supports` 等條件規則內的宣告一律視為可能套用（保守）
- 「可能套用」採保守判定：選擇器最右側的複合選擇器中，每個 class 都在目標元素的 class 內，type 選擇器只允許 `html`（僅根元素）或 `*`，且不含 id 或屬性選擇器；pseudo-class（如 `:hover`、`:not()`）一律視為可能成立；最右側以外的祖先條件一律視為可能成立
- **THEN** 
  - 首屏 CSS 至少一個且非空；
  - 每個目標元素對應的 morandi 待比對 property 集合非空；
  - 每一個 property 都至少找到一條可能套用的宣告（找不到即失敗）；
  - 勝出宣告的值等於 `morandi.css` 原始值（hex 色碼不分大小寫，3 位與 6 位等價縮寫視為相同；`var()` 參照比對去除空白後的字串）
- 若因保守判定納入的規則造成失敗，實作時 MUST 停下回報，不得自行把該規則加入排除清單

### Requirement: bundle 驗證以真實 build 產出執行且不外洩
bundle 報告 SHALL 只在環境變數 `BUNDLE_REPORT` 的值恰為 `1` 時由 build 產生；未設定或為其他值時，報告外掛 MUST NOT 註冊、MUST NOT 產生任何報告檔。預設寫在 `web/node_modules/.tmp/bundle-report.json`，可由環境變數 `BUNDLE_REPORT_PATH` 覆寫（供 BS-013 使用暫存路徑，不碰預設報告），MUST NOT 寫入 `dist/`。報告 MUST 記錄 entry chunk 的 `fileName`；驗證測試以 `dist/index.html` 是否引用該 `fileName` 判定報告與 `dist/` 屬於同一次 build（不比較檔案修改時間，因為報告與 `dist/` 的寫入先後不保證）。報告不存在或與 `dist/` 不屬同一次 build 時，測試 MUST 失敗，不得略過。

#### Scenario: BS-008 報告不進 dist
- **WHEN** 以 `BUNDLE_REPORT=1` 完成 build 後列出 `dist/` 下所有檔案
- **THEN** `dist/` 至少含 `index.html`；沒有任何檔名含 `bundle-report` 的檔案，也沒有任何檔案內容含 `bundle-report.json` 字串

#### Scenario: BS-009 報告缺失或過期時測試失敗
- **WHEN** bundle 驗證測試啟動時報告檔不存在，或報告記錄的 entry `fileName` 沒有出現在 `dist/index.html` 中（以暫存目錄中的假報告與假 `index.html` 造出兩種情境）
- **THEN** 測試以失敗結束，錯誤訊息指出需要先執行 `npm run test:bundle`

#### Scenario: BS-013 未設定 BUNDLE_REPORT 時不產生報告
- **WHEN** 在暫存目錄中，以 `BUNDLE_REPORT_PATH` 指向該目錄下的報告路徑、`--outDir` 指向該目錄下的 dist，先確認報告檔不存在，再依序執行三次 `vite build`（子行程，環境變數從目前環境複製後調整）：
  1. 移除 `BUNDLE_REPORT`；
  2. `BUNDLE_REPORT=0`；
  3. `BUNDLE_REPORT=1`（正向對照）
- **THEN** 
  - 每次 build 都成功結束（exit code 0）；
  - 第 1、2 次 build 後報告檔不存在；
  - 第 3 次 build 後報告檔存在，且其 entry `fileName` 出現在該次的 `index.html` 中
