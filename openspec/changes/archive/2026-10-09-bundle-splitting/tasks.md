## 1. 相依套件與 bundle 報告

- [x] 1.1 `docker compose exec -T web npm install -D unplugin-vue-components@32.1.0`。核對安裝後的型別定義：`ElementPlusResolver` 的 `importStyle: false`、`directives` 預設值、`Components` 的 `dts` 選項。核對 Vite 8／rolldown 的 `generateBundle` chunk 物件確實有 `moduleIds`、`isDynamicEntry`、`facadeModuleId`、`imports`、`dynamicImports`、`viteMetadata.importedCss`，並確認 `moduleIds` 是否包含被完全 tree-shake 掉的模組（若包含，BS-006／BS-011 改用 `modules[id].renderedLength > 0` 判定，同步更新 design 決策 4）。同時依 design 決策 2「相依套件治理」核對：
  - 安裝前先複製一份 `package-lock.json` 到暫存位置；
  - 安裝後記錄 `unplugin-vue-components` 的解析版本、`integrity`、`license`，以及新增傳遞相依的授權；
  - 比對前後 lockfile，確認既有套件的解析版本沒有變動；
  - `npm ls unplugin-vue-components` 沒有 peer 衝突或 deprecated 警告。
  結果記錄在本 task 下方；任何一項不符就停下回報，並更新 design 決策 2／4。

  **1.1 結果（2026-10-09）**
  - `unplugin-vue-components` 解析為 32.1.0，license MIT，integrity `sha512-YiUkSxuRjab18XFOrX5VsIxXzccrfmHVGsGeJgSgklb829DQmCy9E4vvDUE4tuvZZdxyFJZX0Oc4TPnnxiiMyg==`；peer `vue ^3.0.0`（`@nuxt/kit` 為選用 peer），engines `node >=20.19.0`。
  - lockfile 前後比對：只新增 `node_modules/unplugin-vue-components` 一筆，沒有變動或移除的項目。它的 9 個相依（chokidar 5.0.0、local-pkg 1.2.1、magic-string 0.30.21、mlly 1.8.2、obug 2.1.4、picomatch 4.0.5、tinyglobby 0.2.17、unplugin 3.3.0、unplugin-utils 0.3.2）原本就在 lockfile 內、版本未變，全部 MIT。
  - `npm ls unplugin-vue-components` 無 peer 衝突、無 deprecated 警告。`npm audit` 的 14 個既有弱點清單不含本套件或其相依（既有問題，不在本 change 範圍）。
  - 型別定義：`ElementPlusResolverOptions.importStyle?: boolean | 'css' | 'sass'`（預設 `'css'`）、`directives?: boolean`（預設 true，`resolveOptions` 實作亦為 `directives: true`）；`Options.dts?: boolean | string`、`dirs?: string | string[]`。resolver 產生的 import 來源是 `element-plus/es`（barrel），不是個別元件路徑。
  - rolldown 1.2.4 的 `generateBundle` chunk 物件有 `moduleIds`、`isDynamicEntry`、`facadeModuleId`、`imports`、`dynamicImports`、`modules`、`viteMetadata`（`importedCss` 為 Set）。
  - tree-shake 探測：以只 `import { ElButton } from 'element-plus/es'` 的暫存 entry build，1566 個模組被轉換，chunk 的 `moduleIds` 只有 61 個，Element Plus 目錄只有 `icon`、`form`、`button`、`config-provider`，與 `renderedLength > 0` 的結果相同，`carousel` 不在其中。結論：`moduleIds` 不含被完全 tree-shake 掉的模組，維持以 `moduleIds` 判定，design 決策 4 不變。
  - 4.2 合併 chunk 用的選項：rolldown 1.2.4 有 `output.codeSplitting`（`CodeSplittingOptions`）與 `output.manualChunks`。
- [x] 1.2 在 `web/vite.config.ts` 內聯 bundle 報告外掛（design 決策 4）：
  - 只在 `process.env.BUNDLE_REPORT === '1'` 時註冊；
  - 預設輸出 `web/node_modules/.tmp/bundle-report.json`，`BUNDLE_REPORT_PATH` 有值時改寫到該路徑；寫檔前先遞迴建立上層目錄；
  - 缺欄位就 throw。
- [x] 1.3 先寫失敗測試：
  - 新增 `web/vitest.bundle.config.ts`（node 環境，`include: ['build-checks/**/*.test.ts']`）。
  - 主設定 `test.exclude` 設為 `[...configDefaults.exclude, 'build-checks/**']`，保留 Vitest 預設排除。
  - `package.json` 新增 `test:bundle` script。
  - `tsconfig.node.json` 的 `include` 加入 `build-checks/**/*.ts` 與 `vitest.bundle.config.ts`。
- [x] 1.4 新增 `web/build-checks/bundle-splitting.test.ts`，實作以下測試：
  - helper `loadBundleReport(reportPath, distIndexPath)`，報告不存在，或報告的 entry `fileName` 未出現在 `dist/index.html` 中時 throw，訊息含 `npm run test:bundle`（不比較 mtime，見 design 決策 5）；
  - helper `collectStaticClosure(report, startChunks)`；
  - BS-001、BS-002、BS-003、BS-004、BS-006、BS-007、BS-008、BS-009、BS-011、BS-013。
    - BS-006 實作使用集合掃描（與 BS-011 共用標籤對照表，另加具名 import 對照表）與允許集合遞迴走訪（讀 `node_modules/element-plus/es` 的相對 `.mjs` import）；spec 列出的子項目各自寫成獨立的 `it`。
    - BS-013 以 `child_process` 在暫存目錄跑三次 `vite build`（移除變數、`0`、`1`），不碰預設報告與 `dist/`：
      - 用 `execFile`／`spawn` 搭配參數陣列呼叫 `node_modules/.bin/vite build --outDir <tmp>/dist --emptyOutDir`，`cwd` 設為 `web` 目錄，不拼接 shell 字串；
      - 暫存目錄以 `fs.mkdtempSync(path.join(os.tmpdir(), ...))` 建立，`afterAll` 刪除；
      - 該 `it` 明確設定 timeout 120 秒；
      - 子行程 exit code 非 0 時，把 stderr 放進失敗訊息。
    - 報告中的模組 id 先正規化成相對於 `web` 目錄、以 `/` 分隔的路徑再比對（design 決策 5），路徑一律用 `path.resolve` 與 `import.meta.dirname` 組出。
    - 所有「對集合每個元素成立」的斷言先斷言查找成功與集合非空（spec 各 scenario 已列出的數量條件），查找失敗即 throw，不得 vacuous 通過。
    - BS-007 實作 specificity 計算（id／class＋屬性＋pseudo-class／type；`:not()` 取參數權重、`:where()` 為 0）與保守的「可能套用」判定，並以小型合成 CSS 單元測試確認：
      - 權重較高的 `.el-button--primary.x` 規則在目標元素含 `x` 時勝出、不含時不納入；
      - 同權重時後出現者勝；
      - `!important` 優先；
      - type 選擇器只允許 `html`（僅根元素）或 `*`，其他 type（如 `button.el-button--primary`）不納入；
      - 含 id 或屬性選擇器不納入；
      - pseudo-class（如 `.el-button--primary:hover`）視為可能套用；
      - 逗號清單拆開後各自判定與計算權重；
      - `html.dark` 因根元素無 class 而不納入；
      - `@media` 內的規則視為可能套用；
      - 跨 asset 順序依 spec BS-007 定義。
      找不到可能套用的宣告時直接失敗。
    - BS-009 用暫存目錄造出「不存在」與「entry 檔名不符」兩種情境，直接測 `loadBundleReport`。
    - BS-007 的 CSS 規則解析與 `morandi.contrast.test.ts` 的 `parseCssBlocks` 邏輯相同。若重複，抽到 `web/src/styles/` 下的共用檔，並同步修改原測試的 import；不另寫第二套。
  - 測試名稱以 BS 編號開頭。
- [x] 1.5 在目前尚未切割的程式碼執行 `docker compose exec -T web npm run test:bundle`。確認 BS-001、002、003、004（取出 0 個動態 import 路徑）、006 的「bundle ⊆ 允許集合」、「6 個列舉元件不在 bundle」、「main.ts 不含 app.use」三個案例（全量註冊）失敗，失敗訊息指向正確原因；BS-006 的「無個別元件樣式」案例，以及 BS-007、008、009、011、013 通過（全量註冊下所有元件目錄都在，BS-011 的失敗情境由 4.2 證明）。結果記錄在本 task 下方，這是「測試能在失敗情境下失敗」的證據。

  **1.5 結果（2026-10-09，切割前 baseline）**：7 失敗、18 通過，與預期一致。
  - 失敗：BS-001（首屏 `assets/index-*.js` gzip 390,894 B > 200,000）、BS-002（首屏含 18 個禁止模組）、BS-003（jsqr 所在 chunk 就是首屏 entry）、BS-004（取出 0 個動態 import 路徑）、BS-006「bundle ⊆ 允許集合」（58 個目錄不在允許集合，例如 affix、anchor）、BS-006「6 個列舉元件」（6 個都在 bundle；允許集合不含它們，前提成立）、BS-006「main.ts 不含 app.use」。
  - 通過：BS-006「無個別元件樣式」與「使用集合／允許集合非空」、BS-007（含 10 個合成案例）、BS-008、BS-009（2 案例）、BS-011、BS-013（三次子行程 build 約 3.9 秒）。

## 2. 路由延遲載入

- [x] 2.1 先寫失敗測試：新增獨立檔 `web/src/router/lazyRoutes.test.ts`（BS-005）。既有 `web/src/router/index.test.ts` 的守衛測試會導航並把解析後元件寫回 route record，所以 BS-005 不放進該檔；以 `vi.resetModules()` 後動態 import router 取得全新實例。在現況（全部靜態）執行，確認因延遲載入 route 數量為 0 而失敗。
- [x] 2.1a 在同一檔新增 BS-014：依目前（尚未改動）的 `web/src/router/index.ts` 逐項寫出預期路由樹與元件對照表。在現況執行，確認通過（這是改動前的基準）；再暫時把 `my-orders` 的 `meta.requiresAuth` 刪掉、把 `admin-redeem` 的元件換成另一頁，各自確認 BS-014 失敗，然後還原。結果記錄在本 task 下方。

  **2.1／2.1a 結果（2026-10-09，改動前）**：BS-005 失敗（`expected [] to have a length of 19 but got +0`）；BS-014 通過。變異 A（刪掉 `my-orders` 的 `meta.requiresAuth`）→ BS-014 失敗於路由樹深度比對；變異 B（`admin-redeem` 換成 `AdminOrganizersPage`）→ BS-014 失敗於 `admin-redeem 的元件`。兩者皆以 `git checkout` 還原。
  - 偏差：首次 import 全部頁面（Element Plus 經 inline 轉換）在容器內超過預設 5 秒，BS-005／BS-014 個別設定 `timeout: 30_000`；第一次執行 BS-005 是逾時失敗，加上 timeout 後才確認是數量斷言失敗。
- [x] 2.2 `web/src/router/index.ts`：18 個頁面與 `AdminLayout` 改為 `() => import('...')`，`BuyerLayout` 維持靜態（design 決策 1）。path、name、meta、redirect、children 不動。
- [x] 2.3 執行 `docker compose exec -T web npm run test`。確認 BS-005、BS-014 通過，`router/index.test.ts`、`App.test.ts`、`BuyerLayout.test.ts`、`AdminLayout.test.ts`（BS-010）通過。若有失敗，只能調整等待方式（例如 `await router.isReady()`／`flushPromises`），不得放寬斷言；有調整就記錄在本 task 下方。

  **2.3 結果（2026-10-09）**：全套 38 檔 490 測試連跑兩次全部通過。調整（只改等待方式，斷言未動）：
  - `App.test.ts`：watcher 觸發的 `router.push(login)` 須先載入 LoginPage chunk，`flushPromises` 不保證導航完成（第一個案例失敗：`expected 'admin-venues' to be 'login'`）。改為 `vi.waitFor`（timeout 20 秒）包住原本兩個斷言。
  - `router/index.test.ts`、`App.test.ts`、`layouts/AdminLayout.test.ts`、`layouts/BuyerLayout.test.ts` 的 describe 設定 `timeout: 30_000`：整套並行時，第一次導航到某頁的 chunk 轉換可能超過 5 秒（`[AWU-GUARD-005]` 曾以 5005ms 逾時失敗）。
  - 期間 `EventDetailPage.test.ts` 的「區域隨選」案例偶發失敗兩次（不同案例），單獨重跑通過；該檔整個 mock 掉 `vue-router`，與本 change 無關，屬既有 flake。

## 3. Element Plus 按需引入

- [x] 3.0 先寫失敗測試：新增 `web/src/pages/elementPlusOnDemand.test.ts`（BS-012），沿用 `pages/buyer/EventListPage.test.ts` 與 `pages/admin/EventListPage.test.ts` 的 API mock 方式，`global.plugins` 不含 `ElementPlus`；依 spec BS-012 列出的 6 個檢查點建立情境，loading 檢查以手動控制的 deferred promise 在 resolve 前斷言，不靠計時。後台頁含 `<router-link>`，須提供 router 或 stub `RouterLink`，避免非 Element Plus 的 resolve warning 造成誤失敗；`el-tag` 在表格列內，resolve 後 `flushPromises` 再斷言。在尚未加入 resolver 時執行，確認因 `Failed to resolve component` 而失敗。
  - 結果：5 個 `it`（後台 loading→載入完成、後台 API 失敗、買家 loading、買家空清單、買家 API 失敗）在加 resolver 前全數失敗，warning 為 `Failed to resolve component: el-alert`／`el-empty` 與 `Failed to resolve directive: loading`。DOM 檢查用 `expect.soft`，讓單次執行列出全部缺漏；warning 清單用一般 `expect`。
- [x] 3.1 `web/vite.config.ts` 加入 `Components({ resolvers: [ElementPlusResolver({ importStyle: false })], dirs: [], dts: false })`（design 決策 2）。
- [x] 3.2 `web/src/main.ts` 移除 `import ElementPlus from 'element-plus'` 與 `app.use(ElementPlus)`。保留 `import 'element-plus/dist/index.css'`，並維持在 `morandi.css`、`style.css` 之前（design 決策 3）。`await authStore.bootstrapAsync()` 之後才 `app.use(router)` 的順序不動。
- [x] 3.3 執行 `docker compose exec -T web npm run test`。全部通過，含 `morandi.contrast.test.ts` 與 BS-012。
  - 結果：39 個檔案、495/495 通過（BS-012 5/5）。
- [x] 3.4 執行 `docker compose exec -T web npm run build`（含 `vue-tsc -b`）通過。
  - 結果：通過，共 61 個 chunk。

## 4. 驗證

- [x] 4.1 `docker compose exec -T web npm run test:bundle`：BS-001～004、006～009、011、013 全部通過。在本 task 下方記錄：
  - 首屏 gzip 總量；
  - entry 與首頁 chunk 清單；
  - 各 chunk 大小；
  - 與 baseline 394.23 kB 的比較。
  BS-001 若未過，停下回報，不調整門檻。
  - 結果：25/25 通過。首屏 gzip 151,517 B（JS 102,346 + CSS 49,171），基準 390,894 B（Vite 顯示 394.23 kB，口徑不同），減少約 61%，低於 200 kB 門檻。
  - entry：`index-BDUGoFph.js`；首頁 chunk：`EventListPage-ConKCCsv.js`。
  - 首屏 chunk（gzip）：index 16,371；_plugin-vue_export-helper 45,356（rolldown 以此命名共用 vendor chunk，內含 Vue／vue-router／Pinia 等）；tooltip 13,970；button 6,943；auth 3,442 + 281；scrollbar 2,970；use-form-item 2,770；focus-trap 2,160；directive（v-loading）1,958；empty 1,585；alert 896；EventListPage 888；tag 855；event 495；SalesStatusTag 471；useApi 458；errors 170；events 157；castArray 150。CSS：index 48,782（element-plus 全量 + morandi + style）、EventListPage 389。
- [x] 4.2 反向驗證（測試真的會失敗）：
  - 暫時把 `RedemptionScannerPage` 改回靜態 import，執行 `test:bundle`，確認 BS-002、BS-003、BS-004 失敗；
  - 暫時把 `importStyle` 改為 `'css'`，確認 BS-006 失敗；
  - 暫時把 `main.ts` 的 `morandi.css` import 移到 `element-plus/dist/index.css` 之前，確認 BS-007 失敗；
  - 暫時在 `style.css` 末尾加入 `.el-button.el-button--primary { --el-button-hover-bg-color: #ff0000; }`（不同選擇器、權重較高），確認 BS-007 失敗；
  - 暫時讓 rolldown 把兩個後台頁面合併到同一 chunk（例如在 `build.rolldownOptions.output` 加入把兩者歸到同一組的切分設定，實際選項名稱依 1.1 核對結果），確認 BS-004 失敗；
  - 暫時把報告輸出路徑改到 `dist/bundle-report.json`，確認 BS-008 失敗；
  - 暫時把報告外掛改為不論環境變數都註冊，確認 BS-013 失敗（第 1、2 次 build 後出現報告）；
  - 暫時在 `main.ts` 加入 `import { ElRate } from 'element-plus/es/components/rate/index.mjs'` 並使用它（深層路徑不在具名 import 掃描範圍，模擬繞過掃描帶進的元件），確認 BS-006 的「bundle ⊆ 允許集合」案例失敗，且失敗訊息列出 `rate`；
  - 暫時移除 `Components(...)` 外掛（保留 main.ts 已移除的全域註冊），確認 BS-011 與 BS-012 失敗；
  - 全部還原，再跑一次 `test:bundle` 與 `test` 全部通過。
  結果記錄在本 task 下方。
  - 結果（突變皆以備份檔還原，不用 `git checkout`，因工作區修改尚未 commit）：
    - 掃描頁改靜態 import：BS-002、BS-003、BS-004 失敗。
    - `importStyle: 'css'`：BS-006「bundle ⊆ 允許集合」與「無樣式模組」失敗，BS-007 也連帶失敗（按需樣式蓋過 morandi 的 `--el-color-error*` 等）。第一次執行因 `vite.config.ts` 註解也含 `importStyle: false`，字串替換只改到註解、突變未生效而全過；改成鎖定 `ElementPlusResolver({ importStyle: false })` 後重跑才是有效結果。
    - morandi.css 移到 element-plus CSS 之前：BS-007 失敗。
    - `style.css` 加 `.el-button.el-button--primary { --el-button-hover-bg-color: #ff0000; }`：BS-007 失敗。
    - `build.rolldownOptions.output.manualChunks` 把 VenueListPage、AdminOrganizersPage 歸到 `admin-merged`：BS-004 失敗（BS-002 連帶失敗）。
    - 報告路徑改 `dist/bundle-report.json`：BS-008 失敗；另有 9 個讀報告的案例因 node_modules/.tmp 下是舊報告、與 dist/index.html 不符而失敗（`loadBundleReport` 的一致性檢查生效）。
    - 報告外掛無條件註冊：BS-013 失敗。
    - `main.ts` 深層 import `ElRate`：BS-006「bundle ⊆ 允許集合」失敗，訊息為 `expected [ 'rate' ] to deeply equal []`。
    - 移除 `Components(...)`：BS-011 失敗；BS-012 5/5 失敗（`Failed to resolve component: el-alert`／`el-empty`、`Failed to resolve directive: loading`）。
    - 還原後：`test:bundle` 25/25 通過。`test` 跑 3 次，2 次各有 1 個 `EventDetailPage.test.ts` 案例失敗（每次不同案例，斷言型），1 次 495/495 通過；該檔單獨跑 3 次皆 75/75。與 2026-10-08 在 HEAD 6ac5298 量到的既有完整套件 flake（5 次失敗 2 次、每次不同案例）一致，判定非本變更造成，未修。
- [x] 4.3 瀏覽器實測（https://localhost:5173，dev server）：
  - 買家：首頁、活動詳情、登入、我的訂單、訂單詳情。
  - 後台（先在 /organizers 切換主辦單位）：場館、活動列表、建立活動、訂單列表、銷售報表、驗票頁。
  - 每一頁確認：畫面正常渲染；console 沒有 `Failed to resolve component`／`Failed to resolve directive`；`v-loading` 有出現；`ElMessage` 可觸發。
  - 色票不退化（補充 BS-007 的靜態近似）：在首頁以 JS 對 5 個實心語意色按鈕與 1 個預設按鈕讀取 `getComputedStyle(el).getPropertyValue(...)`，涵蓋 `--el-button-bg-color`、`--el-button-hover-bg-color`、`--el-button-active-bg-color`、`--el-button-outline-color`，並讀取 `<html>` 的 `--el-color-primary`。數值須等於 morandi token 經 Element Plus 引用後的解析結果（例如 `--el-button-bg-color` 由 Element Plus 的 `.el-button--primary` 定義為 `var(--el-color-primary)`，morandi.css 本身不定義；hover／active／outline 則由 morandi.css 直接覆寫）（primary 的 bg／hover／active 分別為 `#616f72`／`#4e595b`／`#3f484a`），逐項記錄在本 task 下方。
  - 結果（2026-10-09，使用者以既有 Admin 帳號登入並已切換主辦單位；以 `$router.push` 逐頁巡覽，攔截 `console.warn`，MutationObserver 觀察 `.el-loading-mask`）：
    - 買家：首頁、活動詳情、登入、我的訂單、訂單詳情。後台：場館、活動列表、建立活動、訂單列表、訂單詳情、銷售報表、驗票、主辦方審核。全部正常渲染，`Failed to resolve component/directive` 為 0；另以整頁載入 `/admin/venues` 與 `/` 並讀取 console，同樣沒有 Vue warning 或錯誤。
    - `v-loading`：首頁、活動詳情、買家訂單詳情、後台活動列表、後台訂單列表都觀察到 loading mask；其餘頁面資料回應太快或該頁未使用 v-loading。
    - `ElMessage`：後台活動列表展開票種時，把 ticket-types 請求暫時導向不存在的路徑（不寫入任何資料），出現 `.el-message--error`「API 請求失敗（狀態碼 404）」。驗票頁不使用 ElMessage；送出全零 Ticket ID 時改由頁面結果區塊呈現。
    - 色票（首頁動態建立按鈕後讀 computed style，21 項全部符合）：`<html>` `--el-color-primary` = #616f72。primary bg/hover/active/outline = #616f72/#4e595b/#3f484a/#616f72；success = #61714b/#4e5a3c/#3f4931/#61714b；warning = #87672d/#6c5224/#58431d/#87672d；danger = #975d4f/#794a3f/#623c33/#975d4f；info = #726b63/#5b564f/#4a4640/#726b63；預設按鈕 outline = #616f72。hover 期望值取自 `<html>` 的 `--el-color-*-dark-2` 解析結果，active 為 morandi.css 的字面值。
- [x] 4.4 dev server 下以全新分頁開首頁，用 `performance.getEntriesByType('resource')` 確認沒有請求任何 `/src/pages/admin/`、`/src/layouts/AdminLayout.vue`、`jsqr` 模組；進入驗票頁後才出現 `jsqr`。production chunk 層級由 BS-002／BS-003 負責，不另開 `vite preview`（compose 未映射 preview port，不為此改 port 設定）。
  - 結果：全新分頁開首頁，31 筆 resource，`/src/pages/` 只有 `buyer/EventListPage.vue`，沒有 admin 頁、AdminLayout 或 jsqr。巡覽後台場館、活動、建立活動、訂單、審核、銷售報表、訂單詳情期間都沒有 jsqr；進入 `/admin/redeem` 後才出現 `/node_modules/.vite/deps/jsqr.js`。

## 5. 文件

- [x] 5.1 `docs/ui-ux-review-2026-10-08.md`：§5.1 與 §0 P0 第 2 項標記「✅ 已修正（bundle-splitting）」，附 4.1 的實測數字；註明 CSS 仍為全量（使用者決定延後）。
  - 結果：§0 第 2 項加註已修正與首屏數字；§5.1 開頭加修正說明（151,517 B vs 390,894 B、61 chunks、CSS 全量原因），原表格保留為修正前紀錄。§0 第 1 項（色彩）不在本 change 範圍，未改。
- [x] 5.2 依實作結果同步 design.md 偏差（若有）；archive 時把 spec Purpose 補上（不留 TBD）。
  - 結果：design.md 同步 4 處——決策 4 `importedCss` 改記在 chunk 上；決策 3 補 BS-007 排除偽元素與支援跳脫序列；決策 6 註明 4 個既有導航測試只改等待方式與 timeout（斷言未動）、BS-012 用 `expect.soft`、`parseCssBlocks` 抽到 `src/styles/cssBlocks.ts` 共用；Risks 補 4.1 實測數字。spec Purpose 留待 archive 時處理。
