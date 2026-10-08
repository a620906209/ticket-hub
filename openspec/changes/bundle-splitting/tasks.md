## 1. 相依套件與 bundle 報告

- [ ] 1.1 `docker compose exec -T web npm install -D unplugin-vue-components@32.1.0`。核對安裝後的型別定義：`ElementPlusResolver` 的 `importStyle: false`、`directives` 預設值、`Components` 的 `dts` 選項。核對 Vite 8／rolldown 的 `generateBundle` chunk 物件確實有 `moduleIds`、`isDynamicEntry`、`facadeModuleId`、`imports`、`dynamicImports`、`viteMetadata.importedCss`，並確認 `moduleIds` 是否包含被完全 tree-shake 掉的模組（若包含，BS-006／BS-011 改用 `modules[id].renderedLength > 0` 判定，同步更新 design 決策 4）。結果記錄在本 task 下方；任何一項不符就停下回報，並更新 design 決策 2／4。
- [ ] 1.2 在 `web/vite.config.ts` 內聯 bundle 報告外掛（design 決策 4）：
  - 只在 `process.env.BUNDLE_REPORT === '1'` 時註冊；
  - 預設輸出 `web/node_modules/.tmp/bundle-report.json`，`BUNDLE_REPORT_PATH` 有值時改寫到該路徑；
  - 缺欄位就 throw。
- [ ] 1.3 先寫失敗測試：
  - 新增 `web/vitest.bundle.config.ts`（node 環境，`include: ['build-checks/**/*.test.ts']`）。
  - 主設定 `test.exclude` 設為 `[...configDefaults.exclude, 'build-checks/**']`，保留 Vitest 預設排除。
  - `package.json` 新增 `test:bundle` script。
  - `tsconfig.node.json` 的 `include` 加入 `build-checks/**/*.ts` 與 `vitest.bundle.config.ts`。
- [ ] 1.4 新增 `web/build-checks/bundle-splitting.test.ts`，實作以下測試：
  - helper `loadBundleReport(reportPath, distIndexPath)`，報告不存在，或報告的 entry `fileName` 未出現在 `dist/index.html` 中時 throw，訊息含 `npm run test:bundle`（不比較 mtime，見 design 決策 5）；
  - helper `collectStaticClosure(report, startChunks)`；
  - BS-001、BS-002、BS-003、BS-004、BS-006、BS-007、BS-008、BS-009、BS-011、BS-013。
    - BS-006 實作使用集合掃描（與 BS-011 共用標籤對照表，另加具名 import 對照表）與允許集合遞迴走訪（讀 `node_modules/element-plus/es` 的相對 `.mjs` import）；spec 列出的子項目各自寫成獨立的 `it`。
    - BS-013 以 `child_process` 在暫存目錄跑三次 `vite build`（移除變數、`0`、`1`），不碰預設報告與 `dist/`。
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
- [ ] 1.5 在目前尚未切割的程式碼執行 `docker compose exec -T web npm run test:bundle`。確認 BS-001、002、003、004（取出 0 個動態 import 路徑）、006 的「bundle ⊆ 允許集合」、「6 個列舉元件不在 bundle」、「main.ts 不含 app.use」三個案例（全量註冊）失敗，失敗訊息指向正確原因；BS-006 的「無個別元件樣式」案例，以及 BS-007、008、009、011、013 通過（全量註冊下所有元件目錄都在，BS-011 的失敗情境由 4.2 證明）。結果記錄在本 task 下方，這是「測試能在失敗情境下失敗」的證據。

## 2. 路由延遲載入

- [ ] 2.1 先寫失敗測試：新增獨立檔 `web/src/router/lazyRoutes.test.ts`（BS-005）。既有 `web/src/router/index.test.ts` 的守衛測試會導航並把解析後元件寫回 route record，所以 BS-005 不放進該檔；以 `vi.resetModules()` 後動態 import router 取得全新實例。在現況（全部靜態）執行，確認因延遲載入 route 數量為 0 而失敗。
- [ ] 2.1a 在同一檔新增 BS-014：依目前（尚未改動）的 `web/src/router/index.ts` 逐項寫出預期路由樹與元件對照表。在現況執行，確認通過（這是改動前的基準）；再暫時把 `my-orders` 的 `meta.requiresAuth` 刪掉、把 `admin-redeem` 的元件換成另一頁，各自確認 BS-014 失敗，然後還原。結果記錄在本 task 下方。
- [ ] 2.2 `web/src/router/index.ts`：18 個頁面與 `AdminLayout` 改為 `() => import('...')`，`BuyerLayout` 維持靜態（design 決策 1）。path、name、meta、redirect、children 不動。
- [ ] 2.3 執行 `docker compose exec -T web npm run test`。確認 BS-005、BS-014 通過，`router/index.test.ts`、`App.test.ts`、`BuyerLayout.test.ts`、`AdminLayout.test.ts`（BS-010）通過。若有失敗，只能調整等待方式（例如 `await router.isReady()`／`flushPromises`），不得放寬斷言；有調整就記錄在本 task 下方。

## 3. Element Plus 按需引入

- [ ] 3.0 先寫失敗測試：新增 `web/src/pages/elementPlusOnDemand.test.ts`（BS-012），沿用 `pages/buyer/EventListPage.test.ts` 與 `pages/admin/EventListPage.test.ts` 的 API mock 方式，`global.plugins` 不含 `ElementPlus`；依 spec BS-012 列出的 6 個檢查點建立情境，loading 檢查以手動控制的 deferred promise 在 resolve 前斷言，不靠計時。後台頁含 `<router-link>`，須提供 router 或 stub `RouterLink`，避免非 Element Plus 的 resolve warning 造成誤失敗；`el-tag` 在表格列內，resolve 後 `flushPromises` 再斷言。在尚未加入 resolver 時執行，確認因 `Failed to resolve component` 而失敗。
- [ ] 3.1 `web/vite.config.ts` 加入 `Components({ resolvers: [ElementPlusResolver({ importStyle: false })], dirs: [], dts: false })`（design 決策 2）。
- [ ] 3.2 `web/src/main.ts` 移除 `import ElementPlus from 'element-plus'` 與 `app.use(ElementPlus)`。保留 `import 'element-plus/dist/index.css'`，並維持在 `morandi.css`、`style.css` 之前（design 決策 3）。`await authStore.bootstrapAsync()` 之後才 `app.use(router)` 的順序不動。
- [ ] 3.3 執行 `docker compose exec -T web npm run test`。全部通過，含 `morandi.contrast.test.ts` 與 BS-012。
- [ ] 3.4 執行 `docker compose exec -T web npm run build`（含 `vue-tsc -b`）通過。

## 4. 驗證

- [ ] 4.1 `docker compose exec -T web npm run test:bundle`：BS-001～004、006～009、011、013 全部通過。在本 task 下方記錄：
  - 首屏 gzip 總量；
  - entry 與首頁 chunk 清單；
  - 各 chunk 大小；
  - 與 baseline 394.23 kB 的比較。
  BS-001 若未過，停下回報，不調整門檻。
- [ ] 4.2 反向驗證（測試真的會失敗）：
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
- [ ] 4.3 瀏覽器實測（https://localhost:5173，dev server）：
  - 買家：首頁、活動詳情、登入、我的訂單、訂單詳情。
  - 後台（先在 /organizers 切換主辦單位）：場館、活動列表、建立活動、訂單列表、銷售報表、驗票頁。
  - 每一頁確認：畫面正常渲染；console 沒有 `Failed to resolve component`／`Failed to resolve directive`；`v-loading` 有出現；`ElMessage` 可觸發。
  - 色票不退化（補充 BS-007 的靜態近似）：在首頁以 JS 對 5 個實心語意色按鈕與 1 個預設按鈕讀取 `getComputedStyle(el).getPropertyValue(...)`，涵蓋 `--el-button-bg-color`、`--el-button-hover-bg-color`、`--el-button-active-bg-color`、`--el-button-outline-color`，並讀取 `<html>` 的 `--el-color-primary`。數值須等於 morandi token 經 Element Plus 引用後的解析結果（例如 `--el-button-bg-color` 由 Element Plus 的 `.el-button--primary` 定義為 `var(--el-color-primary)`，morandi.css 本身不定義；hover／active／outline 則由 morandi.css 直接覆寫）（primary 的 bg／hover／active 分別為 `#616f72`／`#4e595b`／`#3f484a`），逐項記錄在本 task 下方。
- [ ] 4.4 dev server 下以全新分頁開首頁，用 `performance.getEntriesByType('resource')` 確認沒有請求任何 `/src/pages/admin/`、`/src/layouts/AdminLayout.vue`、`jsqr` 模組；進入驗票頁後才出現 `jsqr`。production chunk 層級由 BS-002／BS-003 負責，不另開 `vite preview`（compose 未映射 preview port，不為此改 port 設定）。

## 5. 文件

- [ ] 5.1 `docs/ui-ux-review-2026-10-08.md`：§5.1 與 §0 P0 第 2 項標記「✅ 已修正（bundle-splitting）」，附 4.1 的實測數字；註明 CSS 仍為全量（使用者決定延後）。
- [ ] 5.2 依實作結果同步 design.md 偏差（若有）；archive 時把 spec Purpose 補上（不留 TBD）。
