## MODIFIED Requirements

### Requirement: 買家首屏只下載首頁需要的程式
前端 production build（`web/` 執行 `vite build`）中，「首屏集合」定義為：entry chunk 及其靜態 import（`imports`）遞迴閉包，聯集首頁元件 `src/pages/buyer/EventListPage.vue` 所在 chunk 及其靜態 import 遞迴閉包（不含 `dynamicImports`）。首屏集合的 JS gzip 總量 MUST 不超過 200 kB（Node `zlib.gzipSync` 預設等級計算），且 MUST NOT 包含後台、平台管理、主辦單位頁面與 `jsqr`。

計算首屏集合前，驗證 MUST 先確認：恰好一個 `isEntry` chunk；`src/pages/buyer/EventListPage.vue` 恰好出現在一個 chunk 的 `moduleIds` 中。任一查找失敗時測試 MUST 失敗，不得以較小的集合繼續判定。

#### Scenario: BS-001 首屏 JS gzip 總量
- **WHEN** 以 `BUNDLE_REPORT=1` 執行 `vite build`，讀取 bundle 報告並計算首屏集合中所有 JS chunk 的 gzip 位元組數總和
- **THEN** 總和不超過 200 × 1000 位元組

#### Scenario: BS-002 首屏不含後台、平台管理、主辦單位與其他買家頁面
- **WHEN** 列出首屏集合所有 chunk 的 `moduleIds`
- **THEN** 首屏集合同時含 entry chunk 與首頁 chunk（兩者可以是同一個 chunk）；不含任何 `src/pages/admin/` 下的模組、`src/layouts/AdminLayout.vue`、`src/layouts/PlatformLayout.vue`、`src/pages/organizers/` 下的模組，以及 `src/pages/buyer/` 下除了 `EventListPage.vue` 以外的頁面模組

#### Scenario: BS-003 jsqr 只隨驗票頁載入
- **WHEN** 找出 `moduleIds` 含 `node_modules/jsqr/` 模組的所有 chunk，以及含 `src/pages/admin/RedemptionScannerPage.vue` 的 chunk
- **THEN** 含 jsqr 的 chunk 至少一個，驗票頁 chunk 恰好一個；含 jsqr 的 chunk 都不在首屏集合內，且每一個都在驗票頁 chunk 的靜態 import 遞迴閉包內（含該 chunk 本身）

### Requirement: 路由頁面延遲載入且路由行為不變
`web/src/router/index.ts` 中，除了 `BuyerLayout` 以外的每一個 route 元件（18 個頁面、`AdminLayout` 與 `PlatformLayout`）MUST 以動態 import 載入，各自成為以該元件為 facade 的獨立 chunk（20 個元件對應 20 個不同 chunk），且都不在 entry chunk 的靜態閉包內。路由的 path、name、meta、redirect 與巢狀結構 SHALL 與 BS-014 的預期樹一致，`beforeEach` 守衛行為 SHALL 不變。

#### Scenario: BS-004 頁面不在 entry 靜態閉包內
- **WHEN** 以正規表示式從 `web/src/router/index.ts` 原始碼取出所有 `import('...')` 的模組路徑，並在 bundle 報告中找出每一個模組所在的 chunk
- **THEN** 取出的路徑恰好 20 個（18 個頁面、`src/layouts/AdminLayout.vue` 與 `src/layouts/PlatformLayout.vue`），每一個都在報告中恰好一個 chunk 內找到；該 chunk 是 dynamic entry（`isDynamicEntry` 為 true），其 `facadeModuleId` 就是該模組，且不在 entry chunk 的靜態 import 遞迴閉包內；20 個模組對應的 chunk 兩兩不同（共 20 個不同的 chunk）

#### Scenario: BS-005 每個延遲載入的 route 元件都能載入
- **WHEN** 在獨立測試檔中以 `vi.resetModules()` 後動態 import `web/src/router/index.ts`，取得尚未導航過的全新 router，走訪 `router.getRoutes()`，對每個 `components.default` 為函式的 route 呼叫並等待（vue-router 導航後會把已解析的元件寫回 route record，所以不得與會導航的測試共用 router 實例）
- **THEN** 這類 route 恰好 20 個，每一個都解析成 `default` 為 Vue 元件物件的模組

#### Scenario: BS-014 路由設定與元件對應不變
- **WHEN** 在 BS-005 的獨立測試檔中，以 `vi.resetModules()` 後取得的全新 router，遞迴走訪 `router.options.routes`，把每個 route 轉成 `{ path, name, meta, redirect, children }`（未設定的欄位以 `undefined` 表示，`children` 依原順序遞迴轉換），與測試內寫死的預期樹做深度相等比對；預期樹為：bundle-splitting 基準（`/` 下 10 個子 route；`/admin` 下 9 個子 route，其中 `''` 為 `redirect: { name: 'admin-venues' }`），加上之後的變更——`/admin` 下的 `organizers` 改為 `redirect: { name: 'platform-organizers' }`、不再有元件與 meta；新增頂層 `/platform`（`''` 為 `redirect: { name: 'platform-organizers' }`，`organizers` 的 name 為 `platform-organizers`、meta 為 `{ requiresAdmin: true }`）；最後為未知網址的 `/:pathMatch(.*)*`（`redirect: { name: 'events' }`）
- **THEN** 
  - 兩者深度相等（任何 path、name、meta、redirect、子 route 數量、順序或父子關係的差異都使比對失敗）；
  - 預期樹中每個有元件的 route，以測試內寫死的「route name（頂層 layout 以 path）→ 元件檔案路徑」對照表，解析出的 `default` 與直接 `import` 該檔案取得的 `default` 為同一物件（共 21 筆：18 個頁面與 3 個 layout；`BuyerLayout` 為靜態元件、直接比對）；
  - 對照表筆數等於預期樹中有元件的 route 數量，不得少比對

#### Scenario: BS-010 守衛與導航行為回歸
- **WHEN** 執行既有的 `router/index.test.ts`（守衛測試）、`App.test.ts`、`layouts/BuyerLayout.test.ts`、`layouts/AdminLayout.test.ts`
- **THEN** 全部通過；除了本次 change 明確修改的行為（審核頁路徑與選單項目），沒有放寬任何斷言（只允許調整等待非同步元件載入的方式）
