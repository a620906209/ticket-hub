## Why

2026-09-30 前端全面盤點（見 `docs/project-scope.md` 第 8 節「已知前端缺口」）發現：買家下單後只要離開訂單結果頁，就再也無法對該筆 Pending 訂單付款或取消，只能等 10 分鐘保留期逾時——這是 Phase 1 Must「訂單建立與結帳流程」的實際缺口。同時訂單相關頁面直接顯示英文列舉值，平台 Admin 在未切換 Organizer 時也沒有進入主辦方審核頁的介面入口。三者都只需修改前端，先行處理；需要擴充後端回應的「活動名稱、座位、票種、買家顯示名稱」另拆為 `order-display-enrichment`。

## What Changes

- 買家訂單明細頁：訂單狀態為 Pending 時提供「確認付款」「取消訂單」操作，呼叫既有 `ticket-purchase` 確認／取消 API（後端已有本人檢查，無需新增端點）；操作成功或失敗後重新查詢明細，以伺服器狀態為準
- 訂單結果頁：改為進入頁面時呼叫既有買家訂單明細 API 取得訂單目前狀態與伺服器端 `HeldUntilUtc`，取代「僅用下單當下回應＋前端本地狀態」與「前端寫死 10 分鐘推算持有到期時間」——重新整理頁面後不再錯誤回到可操作狀態；移除 `utils/orderHold.ts` 與 `heldUntilUtc` 路由參數；按鈕與狀態文字改為「確認付款」「已付款」，與統一的中文狀態標籤一致
- 訂單與票券狀態在買家「我的訂單」列表、訂單明細、訂單結果頁，以及後台訂單列表／明細一律顯示中文標籤，不再直接顯示英文列舉值
- 後台訂單列表與明細：持有到期時間僅在訂單狀態為 Pending 時顯示，比照買家端既有規則
- 互動細節：取消訂單前需確認（按鈕「取消訂單」／「保留訂單」）；操作成功顯示提示；操作後重新查詢期間不清空畫面，重新查詢失敗時隱藏操作並提供重新整理；狀態以依狀態上色的標籤呈現
- 買家端版面：角色為 `Admin` 的使用者在會員下拉選單看到「主辦方審核」入口，導向 `/admin/organizers`，不要求已切換 Organizer

## Capabilities

### New Capabilities
（無）

### Modified Capabilities
- `buyer-web-ui`：訂單結果頁改為查詢伺服器狀態（取代「不透過查詢 API」與「前端推算 10 分鐘」的既有規定）；訂單明細頁新增 Pending 訂單的確認付款／取消操作；列表與明細顯示中文狀態標籤；買家端版面新增 Admin 專屬的審核頁入口
- `admin-web-ui`：訂單列表與明細顯示中文狀態標籤、持有到期時間僅 Pending 顯示

## Impact

- **前端**：`OrderResultPage.vue`、`OrderDetailPage.vue`、`MyOrdersPage.vue`、`AdminOrderListPage.vue`、`AdminOrderDetailPage.vue`、`BuyerLayout.vue`、`EventDetailPage.vue`（下單導頁不再帶 `heldUntilUtc`）；新增狀態標籤共用模組與 Pending 訂單操作 composable；移除 `utils/orderHold.ts`
- **後端**：無變更
- **測試**：前端 vitest
- **後續**：`order-display-enrichment` 依賴本 change 先歸檔（其 delta spec 以本 change 歸檔後的主 spec 為基準）
