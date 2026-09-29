## 1. 前端 — 共用模組

- [ ] 1.1（design.md 決策 3）新增 `web/src/utils/statusLabels.ts`：`getOrderStatusLabel`、`getTicketStatusLabel`，未知值回傳原字串
- [ ] 1.2（design.md 決策 2）新增 composable（例如 `usePendingOrderActions`）：載入明細、確認、取消、操作中旗標、操作後一律重新查詢且失敗訊息於重新查詢後設定；供 `OrderResultPage` 與 `OrderDetailPage` 共用；API 呼叫經 `api/orders.ts`（`authorizedRequest`）

## 2. 前端 — 頁面

- [ ] 2.1 `OrderResultPage.vue`：改用 1.2，進入即查詢明細，顯示訂單 Id、中文狀態、Pending 時的「保留至」與操作；移除本地狀態機與 `heldUntilUtc` 路由參數讀取；按鈕文字改為「確認付款」
- [ ] 2.2 移除 `utils/orderHold.ts`；`EventDetailPage.vue` 下單成功導頁不再帶 `heldUntilUtc` query；刪除或改寫依賴它的測試
- [ ] 2.3 `OrderDetailPage.vue`：中文訂單／票券狀態；Pending 時顯示確認付款／取消操作（1.2）
- [ ] 2.4 `MyOrdersPage.vue`：狀態改中文標籤
- [ ] 2.5 `AdminOrderListPage.vue`：狀態中文標籤、持有到期時間僅 Pending 顯示
- [ ] 2.6 `AdminOrderDetailPage.vue`：狀態中文標籤、持有到期時間僅 Pending 顯示
- [ ] 2.7（design.md 決策 4）`BuyerLayout.vue` 會員下拉選單：`authStore.isAdmin` 時顯示「主辦方審核」導向 `/admin/organizers`

## 3. 前端測試（vitest，mock API 層）

- [ ] 3.1 `statusLabels.test.ts`：四種訂單狀態、兩種票券狀態對照正確；未知值（含 `Voided`）回傳原字串 [BW-MYORDER-DISPLAY-002 的單元層]
- [ ] 3.2 `OrderResultPage.test.ts`（新檔）：
  - [在訂單結果頁確認訂單]：明細 mock 依序回 Pending → Paid；點「確認付款」後斷言 `confirmOrder` 以訂單 Id 呼叫一次、明細 API 共呼叫 2 次、畫面顯示「已付款」、兩個操作按鈕不存在
  - [在訂單結果頁取消訂單]：同上，改為 `cancelOrder` 與「已取消」
  - [BW-RESULT-001]：明細直接回 Paid，斷言顯示「已付款」、無操作按鈕、無「保留至」
  - [BW-RESULT-002]：明細回 Pending，斷言顯示訂單 Id、「待付款」，「保留至」的時間等於 mock 的 `heldUntilUtc` 格式化結果；並斷言元件不讀取路由 query `heldUntilUtc`（帶入不同值的 query 不影響顯示）
  - [BW-RESULT-003]：`confirmOrder` reject（409），明細第二次回 Expired；斷言畫面同時顯示「已逾時」與失敗訊息。順序驗證：讓第二次明細請求以手動控制的 Promise 延遲 resolve，resolve 前後各斷言一次，確認失敗訊息在重新查詢完成後仍存在（未被載入流程清除）
  - 404／403：顯示對應提示且無操作按鈕
- [ ] 3.3 `OrderDetailPage.test.ts` 新增：
  - [BW-PENDING-001]：明細依序回 Pending（無票）→ Paid（含 1 張 Issued 票）；斷言 `confirmOrder` 呼叫一次、明細呼叫 2 次、顯示「已付款」、出現「查看 QR Code」、無操作按鈕、無「保留至」
  - [BW-PENDING-002]：明細依序回 Pending → Cancelled；斷言 `cancelOrder` 呼叫一次、顯示「已取消」、無「保留至」、無操作按鈕
  - [BW-PENDING-003]：`confirmOrder` reject，明細第二次回 Expired；以手動控制 Promise 驗證失敗訊息在重新查詢完成後仍顯示（同 3.2 BW-RESULT-003 手法），並斷言顯示「已逾時」、無操作按鈕
  - [BW-PENDING-004]：Paid／Cancelled／Expired 三種各一，斷言無「確認付款」「取消訂單」
  - [BW-PENDING-005]：`confirmOrder` 回傳未 resolve 的 Promise，斷言兩按鈕皆 disabled
  - 既有 [開啟訂單明細頁查看已出票訂單]／[開啟尚未出票訂單的明細頁] 改斷言中文狀態（「已付款」「已出票」「待付款」）；既有 [點選查看 QR Code]、[直接以網址開啟不存在的訂單明細頁]、[直接以網址開啟非本人的訂單明細頁] 的測試 MUST 保持通過（改用 composable 後最容易退化的部分）
- [ ] 3.4 `MyOrdersPage.test.ts`：[開啟我的訂單列表頁] 改斷言中文狀態、Pending 顯示保留時間而 Paid 不顯示；[BW-MYORDER-DISPLAY-002] 未知狀態原樣顯示；斷言列表頁無確認付款／取消按鈕
- [ ] 3.5 新增 `AdminOrderListPage.test.ts`：[AWU-ORDER-LIST-001]（中文狀態）、[AWU-ORDER-HOLD-001] 列表部分
- [ ] 3.6 `AdminOrderDetailPage.test.ts`：[AWU-ORDER-HOLD-001] 詳情部分（Pending 顯示、Paid 不顯示）、中文狀態；[AWU-ORDER-DETAIL-001] 既有 hotfix 測試保持通過
- [ ] 3.7 新增 `BuyerLayout.test.ts`：[BW-ADMIN-ENTRY-002] 非 Admin 看不到「主辦方審核」；[BW-ADMIN-ENTRY-001] Admin 看得到，點選後 `router.push` 目標為 `/admin/organizers`；「導向後顯示審核頁內容」由既有 `router/index.test.ts` 的 [AWU-GUARD-005]（未切換 Organizer 的 Admin 可進入審核頁）與 `AdminOrganizersPage.test.ts` 的 [AWU-REVIEW-001]（頁面內容）共同涵蓋，本任務於測試註解中註明此對應，不重複測試

## 4. 驗證與收尾

- [ ] 4.1 容器內執行前端 `npm run test`、`npm run lint`、`vue-tsc`；後端無變更，仍執行一次 `docker compose exec api dotnet test` 確認未受影響
- [ ] 4.2 以 claude-in-chrome 實機驗證：下單 → 離開結果頁 → 從我的訂單進入明細付款成功並看到 QR Code；結果頁重新整理後狀態正確；Admin 未切換 Organizer 從下拉選單進入審核頁
- [ ] 4.3 呼叫 strict-reviewer（無 Application／Repository 變更，hardener 不適用）
- [ ] 4.4 歸檔時同步主 spec，並更新 `docs/project-scope.md` 第 8 節「已知前端缺口」（①②③ 標記完成，④ 指向 `order-display-enrichment`）與第 9 節快照註記
