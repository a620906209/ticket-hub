> 所有指令都在容器內執行（例如 `docker compose exec web npm run test`），不在本機執行 node／npm。
>
> **AC 測試對應例外**：AWU-MOBILE-NAV-001、002 以瀏覽器量測取代 CLAUDE.md 要求的單元或整合測試，例外紀錄見 design.md「AC 測試對應例外」。核准狀態：**已核准（2026-10-09，核准人 RD-Hank）**。
>
> | ID | Scenario | 驗證方式 | Task |
> | --- | --- | --- | --- |
> | AWU-MOBILE-NAV-001 | 窄螢幕顯示選單按鈕且導覽列不溢出 | 瀏覽器量測腳本 | 1.2（基準值）、4.2 |
> | AWU-MOBILE-NAV-002 | 寬螢幕維持水平選單且導覽列不溢出 | 瀏覽器量測腳本 | 1.2（基準值）、4.2 |
> | AWU-MOBILE-NAV-003 | 開啟抽屜後依角色顯示選單項目與登出 | Vitest | 2.1 |
> | AWU-MOBILE-NAV-004 | 在抽屜內選取項目後導覽並關閉抽屜 | Vitest | 2.2 |
> | AWU-MOBILE-NAV-005 | 按 Esc 關閉抽屜並把焦點還給選單按鈕 | Vitest | 2.3 |
> | AWU-MOBILE-NAV-006 | 在抽屜內登出 | Vitest | 2.4 |
> | AWU-MOBILE-NAV-007 | 截斷的主辦方名稱可看到完整名稱 | Vitest | 2.5 |
> | AWU-NAV-001～004（既有） | 水平選單依角色顯示 | 既有 Vitest，不得修改斷言 | 3.5 |

## 1. 量測腳本與基準值

- [ ] 1.1 撰寫瀏覽器量測腳本（design.md 決策 5），存為 `openspec/changes/admin-mobile-nav/measure-admin-nav.js`（隨 change 歸檔，供日後重跑）。**執行方式**：使用者在瀏覽器登入後，於同一個分頁（前端 dev server 的同源頁面）以 claude-in-chrome 的 `javascript_tool` 執行腳本全文，腳本設定開頭的 `INJECT_LONG_TEXT` 常數切換是否注入長名稱，`BASELINE` 常數標示是否為修改前的基準量測（1.2 設為 `true`、4.2 設為 `false`）；`BASELINE` 為 `true` 時不檢查選單按鈕與 `.admin-nav-logout` 的命中數（design.md 決策 5），其餘無效量測條件照常檢查，輸出每筆附 `baseline` 欄位。**輸出格式**：一個 JSON 陣列，每筆對應一組（W、目標路徑、是否注入），欄位為 `W`、`targetPath`、`injectLongText`、`baseline`、`pathname`、`clientWidth`、`navMaxRight`、`overflowingElements`（selector 與兩個寬度）、`documentScrollWidth`、`menuButton`（命中數、是否可見、寬、高）、`horizontalMenu`（命中數、`display`）、`topBarLogoutButton`（`.admin-nav-logout` 命中數、`display`）、`organizerIndicator`（命中數、是否可見、文字、文字長度）、`valid`、`invalidReasons`；1.2 與 4.2 把完整 JSON 貼在該任務下方。腳本內容：同源 iframe 依序設定 W = 320／390／720／721／800／1280，`scrollbar-width: none`；對 `/admin/redeem` 與 `/admin/venues`（以及 W = 320、390 時尚未切換 Organizer 的 `/admin/organizers`）輸出：實際 `location.pathname`、`.organizer-indicator` 的命中數、可見性與文字、導覽列內元素的最大右緣、`scrollWidth > clientWidth` 的元素清單（排除主辦方名稱）、核銷頁 `documentElement.scrollWidth`、選單按鈕與水平選單的顯示狀態與外框尺寸；`INJECT_LONG_TEXT` 開啟時把主辦方名稱換成 100 字元無斷點字串再量一次，並輸出替換後名稱元素的命中數與文字長度。腳本 MUST 依 design.md 決策 5「無效量測」清單逐項檢查，任一成立即把該次量測標記為無效（未通過），並輸出原因
- [ ] 1.2 請使用者在瀏覽器以 **Admin 角色**手動登入（需通過驗證碼）。登入後尚未切換 Organizer（Access Token 不帶 OrganizerId claim），先在此狀態量測 `/admin/organizers`（W = 320、390，`.organizer-indicator` 文字須為「尚未切換主辦方」，否則該次量測無效）；再請使用者切換到一個 Approved Organizer，量測其餘項目。修改後（4.2）需要重新取得未切換狀態時，請使用者登出再登入。在修改前執行腳本，於本節記錄基準值。若使用者沒有可用的 Admin 帳號，停止並回報。**通過條件**（兩項都要重現，用以證明檢查能失敗）：W = 390 時導覽列最大右緣 > W（AWU-MOBILE-NAV-001 的失敗情境，重現 R1）；W = 721 且開啟 `INJECT_LONG_TEXT` 時導覽列溢出（AWU-MOBILE-NAV-002 的失敗情境）。任一項未重現或量測無效時停止，回報使用者。若核銷頁內容本身（非導覽列）溢出，停止並回報使用者，不擴大範圍

## 2. 行為測試（先寫測試、確認失敗）

> 寫在 `web/src/layouts/AdminLayout.test.ts`，沿用既有的真實 router、`vi.mock('../api/organizers')`、`vi.mock('../api/admin')` 與 `fakeAccessTokenWithOrganizerId` 寫法。一律以 `attachTo: document.body` 掛載並在測試結束時 `unmount`。選單按鈕以 `aria-label="開啟後台選單"` 查找；抽屜內的元素一律在 `document.body` 的 `.admin-nav-drawer` 內查找（抽屜 Teleport 到 body，且頂列有同樣文字的水平選單項目與登出鈕），每次操作前先斷言目標元素命中數為 1。「抽屜關閉」以 `.admin-nav-drawer` 不可見加上 `aria-expanded` 為 `"false"` 判定，不以元素不存在判定（關閉後內容以 `v-show` 保留在 DOM）。測試名稱沿用該檔既有的 `[Scenario ID] 情境描述` 格式（例如 `[AWU-NAV-001] …`），不採 CLAUDE.md 的 `MethodName_Scenario_ExpectedResult`：前端元件測試沒有對應的單一方法，且同一檔混用兩種格式會降低一致性（Rule 11）。完成本節後以 `docker compose exec web npm run test -- src/layouts/AdminLayout.test.ts` 執行測試，確認新測試因功能尚未實作而失敗（不是因為語法或匯入錯誤），並記錄失敗訊息。

- [ ] 2.1 [AWU-MOBILE-NAV-003] 非 Admin 與 Admin 各一案：點選單按鈕後抽屜標題為「後台選單」、`aria-expanded` 為 `"true"`，`.admin-nav-drawer .admin-nav-drawer-menu .el-menu-item` 的文字以完全相等比對（非 Admin 4 項、Admin 5 項，順序相同），`.admin-nav-drawer` 內恰有一個「登出」按鈕
- [ ] 2.2 [AWU-MOBILE-NAV-004] 三案，皆點選抽屜內的選單項目：(a) 已切換 Organizer 的非 Admin 在 `/admin/venues` 選取「票券核銷」，以 `vi.waitFor` 等待路由為 `/admin/redeem`、名稱 `admin-redeem`；(b) 同一使用者在 `/admin/venues` 選取「場館管理」，路由仍為 `/admin/venues`；(c) 尚未切換 Organizer 的 Admin 在 `/admin/organizers` 選取「場館管理」，以 `vi.waitFor` 等待路由名稱為 `my-organizers`。三案都斷言抽屜關閉、`aria-expanded` 為 `"false"`
- [ ] 2.3 [AWU-MOBILE-NAV-005] **不預先聚焦**選單按鈕（先斷言 `document.activeElement` 不是選單按鈕），直接點擊開啟抽屜，在抽屜內觸發 Esc，以 `vi.waitFor` 斷言抽屜關閉、`aria-expanded` 為 `"false"`、`document.activeElement` 是選單按鈕。只依賴 Element Plus 預設焦點還原的實作在此會失敗（design.md 決策 4）。Vue Test Utils 預設把 `<transition>` 換成 stub，`@closed`（after-leave）可能不觸發；先依序嘗試以下 Vitest 內的做法，記錄採用哪一種：(a) 掛載時設 `global.stubs: { transition: false }` 使用真實 Transition，並以 `vi.waitFor` 等待；(b) 若 (a) 仍不觸發 after-leave，對抽屜的 Transition 元件觸發 `after-leave`（例如 `findComponent({ name: 'Transition' })` 後 `vm.$emit('after-leave')` 或呼叫其 `onAfterLeave` prop）。兩者都不得改寫斷言內容，也不得直接呼叫元件內部的焦點還原函式。**若 (a)、(b) 都無法觸發，或 jsdom 無法觸發抽屜的 Esc 關閉或關閉事件**：在本任務下方記錄執行的測試命令、完整錯誤訊息或失敗斷言、已嘗試的觸發方式、受影響的 AC（AWU-MOBILE-NAV-005），以及是否需要擴大例外範圍的判斷，然後停止，回報使用者，由使用者決定是否擴大例外範圍，不得自行改用其他驗證方式
- [ ] 2.4 [AWU-MOBILE-NAV-006] 讓 `authStore.logout` 回傳一個尚未 resolve 的 Promise，開啟抽屜後點 `.admin-nav-drawer` 內的「登出」（不是頂列的登出鈕）；在 resolve 前斷言 `aria-expanded` 為 `"false"`（主要斷言，由元件狀態同步更新），並以 `vi.waitFor` 斷言抽屜不可見（先 await 登出再關抽屜的實作在此會失敗，design.md 決策 4）；resolve 後斷言 `authStore.logout` 被呼叫一次，且路由為 `/login`
- [ ] 2.5 [AWU-MOBILE-NAV-007] `getMyOrganizers` 回傳 100 字元名稱，以 `.organizer-indicator` 定位主辦方名稱入口並斷言命中數為 1，斷言其 `title` 屬性等於完整名稱

## 3. 實作

- [ ] 3.1 `AdminLayout.vue` script：以 computed 產生選單項目清單（「主辦方審核」僅 `authStore.isAdmin` 時加入），新增抽屜開關狀態；水平選單改用 `v-for` 渲染該清單，保留 `.admin-nav-menu` class（design.md 決策 2）
- [ ] 3.2 `AdminLayout.vue` template：新增選單按鈕（`el-button`、inline SVG `aria-hidden="true"`、`aria-label`、`aria-expanded`）與 `el-drawer`（`class="admin-nav-drawer"`、`direction="ltr"`、`size="280px"`、`title="後台選單"`），抽屜內為直式 `el-menu`（class `admin-nav-drawer-menu`、`router`、`default-active` 綁定目前路徑、`@select` 關閉抽屜）與「登出」；抽屜關閉後明確把焦點移回選單按鈕，一律掛 `@closed`，不得掛 `@close-auto-focus`（無 Event 參數且早於預設還原）、`@close` 或 watch（design.md 決策 4）；抽屜內登出先關抽屜再登出；主辦方名稱入口加上 `title` 屬性（design.md 決策 3、4）；頂列既有的登出鈕加上 class `admin-nav-logout`，供量測腳本定位
- [ ] 3.3 `AdminLayout.vue` style：`@media (max-width: 720px)` 隱藏水平選單與頂列登出鈕、顯示選單按鈕（≥ 44×44）；主辦方名稱 `min-width: 0`、`white-space: nowrap`、`overflow: hidden`、`text-overflow: ellipsis`，寬螢幕另設 `max-width`。抽屜內的自訂樣式不得寫在 scoped 區塊（抽屜 Teleport 到 body，不帶 data-v），另開不帶 scoped 的 `<style>` 並以 `.admin-nav-drawer` 限定範圍（design.md 決策 4）
- [ ] 3.4 `web/build-checks/bundle-splitting.test.ts` 的元件對照表加入 `'el-drawer': 'drawer'`；執行 `docker compose exec web npm run test:bundle`，確認 bundle 測試全部通過（含 BS-006 的 6 個不相關元件前提檢查、BS-011），首屏 JS gzip 仍 ≤ 200 kB；記錄首屏數值與 151,517 B 基準的差異（共用模組可能被 Rollup 重新分組而小幅變動）。BS-006 不受影響目前只是靜態推論（drawer 依賴 dialog、overlay、focus-trap、icon），以本次實際執行結果為準
- [ ] 3.5 執行 `docker compose exec web npm run test`，第 2 節新測試與既有 `AdminLayout.test.ts`（AWU-NAV-001～004、ADMIN-REDEEM-NAV-ENTRY，斷言不得修改）全部通過；記錄結果。全套若出現已知的 `EventDetailPage.test.ts` 間歇失敗，需重跑確認並記錄，不得略過不提
- [ ] 3.6 `docker compose exec web npm run lint` 與 `docker compose exec web npm run build`（含 `vue-tsc`）通過

## 4. 版面驗收與收尾

- [ ] 4.1 由使用者登入並切換 Organizer 後，以真實瀏覽器實際操作一次：390px 寬開抽屜 → 選「票券核銷」→ 抽屜關閉並進入核銷頁；再開抽屜後點遮罩關閉、再開後按 Esc 關閉，兩次都確認焦點回到選單按鈕；鍵盤 Tab 可到達選單按鈕；最後在抽屜內點「登出」，確認導向登入頁後沒有殘留遮罩、頁面可正常捲動
- [ ] 4.2 修改後重跑 1.1 腳本，於本節記錄所有寬度與 `INJECT_LONG_TEXT` 的數值。**通過條件**：AWU-MOBILE-NAV-001、002 的每一項 THEN 都成立（含 W ≤ 720 時 `topBarLogoutButton.display` 為 `none`、W ≥ 721 時不為 `none`），且所有量測的 `clientWidth` = W；任一項不成立即未完成
- [ ] 4.3 更新 `docs/ui-ux-review-2026-10-08.md`：R1 標記已修正（mobile 導覽改為抽屜）並從第 7 節「仍未實測」移除 R1；總結第 1、3 項補上已修正標記（palette-contrast-aa、mobile-layout-p0-fixes）
