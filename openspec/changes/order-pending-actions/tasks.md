## 1. 前端 — 共用模組

- [x] 1.0 實作前載入 `modern-web-guidance` skill，確認本次使用的 Vue／Element Plus 寫法無過時模式

- [x] 1.1（design.md 決策 3）新增 `web/src/utils/statusLabels.ts`：`getOrderStatusLabel`、`getTicketStatusLabel`，未知值回傳原字串；另提供對應的 `el-tag` type 對照（design.md 決策 5：Pending→warning、Paid→success、Cancelled／Expired→info；Issued→success、Redeemed→info；未知值→info），並新增共用元件（例如 `OrderStatusTag.vue`／`TicketStatusTag.vue`）以文字插值渲染標籤
- [x] 1.2（design.md 決策 2）新增 composable（例如 `usePendingOrderActions`）：載入明細、確認、取消、操作中旗標、操作後一律重新查詢且失敗訊息於重新查詢後設定；供 `OrderResultPage` 與 `OrderDetailPage` 共用；API 呼叫經 `api/orders.ts`（`authorizedRequest`）。另依 design.md 決策 5：取消前以 `ElMessageBox.confirm` 確認（按鈕「取消訂單」danger／「保留訂單」，不開 `dangerouslyUseHTMLString`，使用者拒絕時的 reject 視為正常流程、不顯示錯誤、不呼叫 API）；成功時 `ElMessage.success`；區分首次載入與操作後重新查詢，後者不清空目前資料。另依 design.md 決策 2：以 request version 只採用最新一次明細回應、查詢進行中「重新整理」disabled；`onBeforeUnmount` 遞增 version 並呼叫 `ElMessageBox.close()`；確認／取消 API 回應 429 時失敗訊息固定為「請求過於頻繁，請稍後再試」

## 2. 前端 — 頁面

- [x] 2.1 `OrderResultPage.vue`：改用 1.2，進入即查詢明細，顯示訂單 Id、中文狀態、Pending 時的「保留至」與操作；移除本地狀態機與 `heldUntilUtc` 路由參數讀取；按鈕文字改為「確認付款」
- [x] 2.2 移除 `utils/orderHold.ts`；`EventDetailPage.vue` 下單成功導頁不再帶 `heldUntilUtc` query（目前沒有測試引用 `orderHold`，無需刪改）；於 `EventDetailPage.test.ts` 既有「選擇可售座位並成功下單」補斷言 `router.push` 目標為 `/order-result/{id}` 且不帶 `heldUntilUtc` query
- [x] 2.3 `OrderDetailPage.vue`：以狀態標籤元件顯示訂單／票券狀態；Pending 時顯示確認付款（primary）／取消訂單（預設樣式）操作（1.2）；改用 composable 後不再於重新查詢時把 `order` 設為 `null`；以 `watch` 監看路由參數 `id`，變更時以新 `orderId` 重新載入（design.md 決策 2，`OrderResultPage` 同樣處理）
- [x] 2.4 `MyOrdersPage.vue`：狀態改用狀態標籤元件
- [x] 2.5 `AdminOrderListPage.vue`：狀態改用狀態標籤元件、持有到期時間僅 Pending 顯示
- [x] 2.6 `AdminOrderDetailPage.vue`：狀態改用狀態標籤元件、持有到期時間僅 Pending 顯示
- [x] 2.7（design.md 決策 4）`BuyerLayout.vue` 會員下拉選單：`authStore.isAdmin` 時顯示「主辦方審核」導向 `/admin/organizers`

## 3. 前端測試（vitest，mock API 層）

測試共通做法（design.md 決策 5）：`ElMessageBox.confirm` 與 `ElMessage.success` 以 `vi.spyOn(ElMessageBox, 'confirm')`／`vi.spyOn(ElMessage, 'success')` 替換（專案無 `vi.mock('element-plus')` 先例，維持 `plugins: [ElementPlus]` 掛載）；使用者拒絕以 `mockRejectedValue('cancel')`／`('close')` 模擬。

- [x] 3.0 共用 composable 的單元測試（錯誤處理／防禦性測試，不直接對應功能 Scenario，依據為 design.md 決策 5 與 CLAUDE.md「禁止靜默失敗」）：`ElMessageBox.confirm` 以 `'cancel'`／`'close'` 以外的值 reject（例如 `new Error('boom')`）時，例外向外拋出、不被吞掉，且 `cancelOrder` 未被呼叫
- [x] 3.1 `statusLabels.test.ts`：四種訂單狀態、兩種票券狀態的文字與 tag type 對照正確；未知值（含 `Voided`）回傳原字串與 `info` [BW-MYORDER-DISPLAY-002 的單元層]
- [x] 3.2 `OrderResultPage.test.ts`（新檔）：
  - [BW-RESULT-004]：明細 mock 依序回 Pending → Paid；點「確認付款」後斷言 `confirmOrder` 以訂單 Id 呼叫一次、`ElMessage.success` 以「付款成功，票券已出票」呼叫、明細 API 共呼叫 2 次、畫面顯示「已付款」、兩個操作按鈕不存在
  - [BW-RESULT-005]：`ElMessageBox.confirm` 回傳手動控制的 Promise；resolve 前斷言對話框以「取消訂單」／「保留訂單」按鈕文字被呼叫、`cancelOrder` 尚未被呼叫（驗證先確認後呼叫的時序），resolve 後斷言 `cancelOrder` 呼叫一次、顯示「已取消」、`ElMessage.success` 以「訂單已取消」呼叫；另一案例 confirm 以 `'cancel'` reject 時 `cancelOrder` 未被呼叫且無錯誤訊息
  - 結果頁的共通規定（與明細頁共用 composable，但結果頁 requirement 亦明訂）：`confirmOrder` 未 resolve 時兩按鈕皆 disabled；重新查詢未 resolve 時訂單 Id 仍顯示
  - [BW-RESULT-001]：明細直接回 Paid，斷言顯示「已付款」、無操作按鈕、無「保留至」
  - [BW-RESULT-002]：明細回 Pending，斷言顯示訂單 Id、「待付款」，「保留至」的時間等於 mock 的 `heldUntilUtc` 格式化結果；並斷言元件不讀取路由 query `heldUntilUtc`（帶入不同值的 query 不影響顯示）
  - [BW-RESULT-003]：以 `it.each` 覆蓋重新查詢回 Expired 與 Cancelled 兩種：`confirmOrder` reject（409），斷言畫面顯示「已逾時」／「已取消」與失敗訊息、無「保留至」、無操作按鈕。順序驗證：讓第二次明細請求以手動控制的 Promise 延遲 resolve，resolve 前後各斷言一次，確認失敗訊息在重新查詢完成後仍存在（未被載入流程清除）
  - [BW-RESULT-006]：`confirmOrder` 以 `ApiError` status 409 reject、明細第二次仍回 Pending；斷言顯示「待付款」、「保留至」、失敗訊息，且「確認付款」「取消訂單」存在且未 disabled；再點「確認付款」（第二次 `confirmOrder` resolve、明細回 Paid），斷言 `confirmOrder` 共呼叫 2 次、顯示「已付款」
  - [BW-RESULT-007]：以 `it.each` 覆蓋 `/order-result/{不存在的 GUID}` 與 `/order-result/not-a-guid` 兩種掛載，明細 API mock 以 `ApiError` status 404 reject；斷言明細 API 以路由原值被呼叫（前端不做格式檢查）、顯示「找不到這筆訂單」與返回「我的訂單」連結、無訂單 Id、無操作按鈕
  - [BW-RESULT-008]：明細 API 以 403 reject；斷言顯示「你沒有權限查看這筆訂單」與返回「我的訂單」連結、無訂單資料、無操作按鈕
  - [BW-RESULT-009]：`confirmOrder` 成功、第二次明細請求 reject；斷言顯示錯誤訊息與「重新整理」、無「確認付款」「取消訂單」、無「保留至」
  - [BW-RESULT-010]（結果頁的 `watch` 接線）：以 `/order-result/A` 掛載，未 resolve 時 `router.push('/order-result/B')`；先 resolve B 為 Paid、再 resolve A 為 Pending，斷言畫面為 B 的訂單 Id 與「已付款」、無「確認付款」「取消訂單」
- [x] 3.3 `OrderDetailPage.test.ts` 新增：
  - [BW-PENDING-001]：明細依序回 Pending（無票）→ Paid（含 1 張 Issued 票）；斷言 `confirmOrder` 呼叫一次、明細呼叫 2 次、顯示「已付款」、出現「查看 QR Code」、無操作按鈕、無「保留至」
  - [BW-PENDING-002]：`ElMessageBox.confirm` 回傳手動控制的 Promise，明細依序回 Pending → Cancelled；resolve 前斷言 `cancelOrder` 尚未被呼叫，resolve 後斷言 `cancelOrder` 呼叫一次、`ElMessage.success` 以「訂單已取消」呼叫、顯示「已取消」、無「保留至」、無操作按鈕
  - [BW-PENDING-003]：以 `it.each` 覆蓋重新查詢回 Expired 與 Cancelled 兩種：`confirmOrder` reject，以手動控制 Promise 驗證失敗訊息在重新查詢完成後仍顯示（同 3.2 BW-RESULT-003 手法），並斷言顯示「已逾時」／「已取消」、無「保留至」、無操作按鈕
  - [BW-PENDING-004]：Paid／Cancelled／Expired 三種各一，斷言無「確認付款」「取消訂單」
  - [BW-PENDING-005]：兩個案例——`confirmOrder` 回傳未 resolve 的 Promise；以及 `ElMessageBox.confirm` resolve 後 `cancelOrder` 回傳未 resolve 的 Promise——皆斷言兩按鈕 disabled
  - [BW-PENDING-006]：mock `ElMessageBox.confirm` reject（模擬選「保留訂單」與關閉兩種），斷言 `cancelOrder` 未被呼叫、狀態仍為「待付款」、無錯誤訊息
  - [BW-PENDING-007]：確認付款成功後，讓第二次明細請求以手動控制 Promise 延遲；resolve 前斷言原訂單內容（訂單 Id、項目）仍在畫面上且無整頁載入狀態
  - [BW-PENDING-001] 補斷言 `ElMessage.success` 以「付款成功，票券已出票」呼叫
  - [BW-PENDING-008]：confirm resolve，`cancelOrder` reject（409），明細第二次回 Paid；斷言顯示「已付款」與失敗訊息、無操作按鈕
  - [BW-PENDING-009]：`confirmOrder` 成功、第二次明細請求 reject（網路錯誤）；斷言顯示錯誤訊息與「重新整理」按鈕、無「確認付款」「取消訂單」、無「保留至」；點「重新整理」後、第三次明細請求（手動控制 Promise）resolve 前斷言「重新整理」為 disabled，resolve 為 Paid 後斷言顯示「已付款」；另一案例重新整理回 Pending，斷言「保留至」與兩個操作按鈕恢復顯示
  - [BW-PENDING-010]：明細 API 依 id 回傳手動控制 Promise；以 id=A 掛載，未 resolve 時 `router.push('/orders/B')`；斷言明細 API 以 B 被呼叫；先 resolve B 為 Paid、再 resolve A 為 Pending，斷言畫面顯示 B 的訂單 Id 與「已付款」、無「確認付款」「取消訂單」
  - [BW-PENDING-011]：`confirmOrder` 以 `ApiError` status 429 reject、明細第二次回 Pending；斷言明細呼叫 2 次、失敗訊息為「請求過於頻繁，請稍後再試」；另一案例 confirm 對話框 resolve 後 `cancelOrder` 以 429 reject，斷言同上
  - [BW-PENDING-012]：`confirmOrder` 以 `ApiError` status 409 reject、明細第二次仍回 Pending；斷言顯示「待付款」、「保留至」、失敗訊息，且兩個操作按鈕存在且未 disabled；再點「確認付款」成功（明細回 Paid），斷言 `confirmOrder` 共呼叫 2 次、顯示「已付款」
  - [BW-PENDING-013]：以 id=A（Pending）掛載並點「確認付款」，`confirmOrder` 回傳手動控制 Promise；未 resolve 時 `router.push('/orders/B')`，B 明細回 Paid；之後 resolve A 的 `confirmOrder`（另一案例 reject 409）；斷言 `ElMessage.success` 未被呼叫、無失敗訊息、明細 API 未再以 A 呼叫、畫面仍為 B 的訂單 Id 與「已付款」
  - [BW-MYORDER-INVALID-ID-001]：以 `/orders/not-a-guid` 掛載，明細 API 以 404 reject；斷言明細 API 以 `not-a-guid` 被呼叫、顯示「找不到這筆訂單」與返回「我的訂單」連結
  - 卸載（design.md 決策 2，實作細節無 Scenario）：兩個案例——(a) `confirmOrder` 未 resolve 時 unmount，之後 resolve；斷言不 throw、`ElMessage.success` 未被呼叫；(b) 取消確認對話框開啟中（confirm 回傳手動控制 Promise）unmount，斷言 `ElMessageBox.close` 被呼叫，之後以 `'close'` reject，斷言 `cancelOrder` 未被呼叫、無未處理例外
  - 票券標籤：已出票訂單的 Issued 票券渲染 `el-tag--success` 且文字「已出票」、Redeemed 票券渲染 `el-tag--info` 且文字「已核銷」
  - 既有 [開啟訂單明細頁查看已出票訂單]／[開啟尚未出票訂單的明細頁] 改斷言中文狀態（「已付款」「已出票」「待付款」）；既有 [點選查看 QR Code]、[直接以網址開啟不存在的訂單明細頁]、[直接以網址開啟非本人的訂單明細頁] 的測試 MUST 保持通過（改用 composable 後最容易退化的部分）
- [x] 3.4 `MyOrdersPage.test.ts`：[BW-STATUS-TAG-001] Pending／Paid／Cancelled 三筆訂單分別渲染 `el-tag--warning`／`el-tag--success`／`el-tag--info` 且標籤文字為「待付款」「已付款」「已取消」；[開啟我的訂單列表頁] 改斷言中文狀態、Pending 顯示保留時間而 Paid 不顯示；[BW-MYORDER-DISPLAY-002] 未知狀態原樣顯示；斷言列表頁無確認付款／取消按鈕
- [x] 3.5 新增 `AdminOrderListPage.test.ts`：[AWU-ORDER-LIST-001]（中文狀態，Pending／Paid 分別渲染 `el-tag--warning`／`el-tag--success`）、[AWU-ORDER-HOLD-001] 列表部分
- [x] 3.6 `AdminOrderDetailPage.test.ts`：[AWU-ORDER-HOLD-001] 詳情部分（Pending 顯示、Paid 不顯示）、中文狀態與對應 `el-tag` class；[AWU-ORDER-DETAIL-001] 既有 hotfix 測試保持通過
- [x] 3.7 新增 `BuyerLayout.test.ts`（`el-dropdown` 選單須先觸發開啟才會渲染，否則「看不到」會空洞通過）：[BW-ADMIN-ENTRY-002] 先開啟下拉選單並斷言「登出」「我的主辦方」存在，再斷言「主辦方審核」不存在；[BW-ADMIN-ENTRY-001] 整合層測試，比照 `AdminLayout.test.ts` 使用真實 `router`（不 mock `router.push`，讓 `beforeEach` 守衛實際執行）：以根層 `RouterView`（或 `App.vue`）掛載，`vi.mock('../api/organizers')` 讓 `getPendingOrganizers` 回傳一筆待審主辦方；以 Admin 角色、未帶 OrganizerId claim 的 token 登入並導向買家端首頁 → 開啟會員下拉選單 → 點「主辦方審核」→ `flushPromises` 後斷言 `router.currentRoute.value.path === '/admin/organizers'`，且畫面渲染審核頁內容（該筆待審主辦方名稱）

## 4. 驗證與收尾

- [x] 4.1 容器內執行前端 `npm run test`、`npm run lint`、`vue-tsc`；後端無變更，仍執行一次 `docker compose exec api dotnet test` 確認未受影響
- [x] 4.2 以 claude-in-chrome 實機驗證：下單 → 離開結果頁 → 從我的訂單進入明細付款成功並看到 QR Code；結果頁重新整理後狀態正確；Admin 未切換 Organizer 從下拉選單進入審核頁；取消確認對話框開啟時切換頁面，對話框不殘留；直接開啟 `/order-result/not-a-guid` 與 `/orders/not-a-guid` 顯示「找不到這筆訂單」（實機驗證後端路由約束確實回 404，前端單元測試只 mock 此行為）
- [x] 4.3 呼叫 strict-reviewer（無 Application／Repository 變更，hardener 不適用）
- [ ] 4.4 歸檔時同步主 spec，並更新 `docs/project-scope.md` 第 8 節「已知前端缺口」（①②③ 標記完成，④ 指向 `order-display-enrichment`）與第 9 節快照註記
