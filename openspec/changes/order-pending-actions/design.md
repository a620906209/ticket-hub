## Context

- 買家確認／取消訂單的 API（`POST /api/orders/{id}/confirm`、`/cancel`）已存在，`OrderService.ChangeOrderStatusAsync` 已做買家本人檢查、並發一致性由 `ticket-purchase` 能力保證。缺的只是前端入口：目前只有下單後導向的 `OrderResultPage` 提供按鈕，而該頁面依 `buyer-web-ui` 既有規定「不透過查詢 API 取得訂單狀態」、「持有到期時間以前端寫死 10 分鐘推算」——這兩條是在 `buyer-order-query` 能力尚未存在時訂下的，如今查詢 API 已存在，限制已無必要。
- 確認 API 在訂單非 Pending 或已逾時時回 409（`ConfirmOrderHandler` → `Error.Conflict`），付款被拒同為 409；明細 API 以 `GetStatus(now)` 推導，逾時 Pending 會回報 Expired。
- 前端 `MyOrganizersPage.vue` 已有中文狀態對照表，但只限該頁使用。
- 本 change 只改前端。活動名稱、座位、票種、買家顯示名稱等需擴充後端回應的項目拆至 `order-display-enrichment`，於本 change 歸檔後進行。

## Goals / Non-Goals

**Goals:**
- 買家可在訂單明細頁對 Pending 訂單確認付款或取消；訂單結果頁以伺服器狀態為準
- 訂單與票券狀態統一以中文標籤顯示，對照表集中一處
- 平台 Admin 未切換 Organizer 時也有進入審核頁的介面入口

**Non-Goals:**
- 「我的訂單」列表頁不直接提供確認／取消按鈕，買家點入明細頁操作（見決策 1）
- 活動名稱、座位標示、票種名稱、買家顯示名稱（見 `order-display-enrichment`）
- `Voided` 票券狀態的任何顯示邏輯（`buyer-web-ui` 既有規定，本次維持）
- 付款倒數計時、到期自動刷新
- 任何後端變更

## Decisions

### 決策 1：確認／取消操作放在訂單明細頁，不放在列表頁

明細頁已有完整的載入、錯誤、403/404 處理，且操作前買家應看得到訂單內容。列表頁每列放兩顆按鈕會讓列表承擔操作錯誤處理與逐列 loading 狀態，複雜度與收益不成比例。列表頁的 Pending 訂單以狀態標籤「待付款」與「查看明細」連結引導即可。

替代方案：列表頁也提供按鈕——被否決，理由如上。

### 決策 2：操作完成（成功或失敗）後一律重新查詢明細，以伺服器狀態為準

確認付款可能失敗（Mock 付款失敗、訂單已逾時被推導為 Expired、並發下已被取消或被背景清理轉為 Cancelled）。前端不嘗試依錯誤類型推測訂單新狀態，一律重新呼叫明細 API，錯誤訊息在重新查詢**之後**設定，避免被載入流程清空（`EventDetailPage` 曾因順序錯誤導致錯誤訊息消失，見該檔案 `handleSubmit` 註解）。確認成功後明細會顯示已出票的票券與「查看 QR Code」操作，買家不需另外導頁。付款被拒（409）時 `ConfirmOrderHandler` 直接回失敗、不變更訂單，重新查詢後仍為 Pending，畫面依一般 Pending 規則重新顯示兩個操作，允許再次嘗試（BW-PENDING-012、BW-RESULT-006）。

`OrderResultPage` 共用相同模式：進入頁面即呼叫明細 API 取得狀態與 `HeldUntilUtc`，按鈕只在伺服器狀態為 `Pending` 時顯示。為避免兩頁重複實作「載入明細＋確認＋取消＋重新查詢」，抽成 composable `usePendingOrderActions`（或同等命名，實作時決定）供兩頁共用。

重新查詢本身失敗時（網路、5xx、404、403），畫面上的 Pending 內容可能已過時：顯示錯誤與「重新整理」操作，隱藏確認／取消按鈕，不讓使用者依過時狀態再次送出。首次載入失敗沿用既有的 404／403／一般錯誤提示。

同一頁面內，操作按鈕在操作中停用（BW-PENDING-005）、「重新整理」在查詢中停用（BW-PENDING-009），因此 UI 無法對同一筆訂單同時送出兩個明細請求。真正的競態來源是**同一元件實例切換 `orderId`**：瀏覽器上一頁／下一頁在 `/orders/a` 與 `/orders/b` 之間切換時，Vue Router 重用元件實例，舊訂單的明細回應可能晚於新訂單回來並覆蓋畫面。處理方式：
- 頁面以 `watch` 監看路由參數 `id`，變更時以新 `orderId` 重新執行首次載入（不採 `<router-view>` 加 `:key`，避免影響所有頁面的重用行為）；切換時遞增 version，並重設操作中旗標、錯誤與失敗訊息，使舊訂單仍在進行中的確認／取消回應及其後續重新查詢一律被丟棄（對外可觀察行為見 BW-PENDING-010、BW-PENDING-013）
- composable 以遞增的 request version 只採用最新一次請求的結果（沿用 `OrderDetailPage` 既有 `qrRequestVersion` 模式）
- 元件卸載時（`onBeforeUnmount`）同樣遞增 version，使卸載後才回來的回應不寫入狀態、不觸發 `ElMessage`；並呼叫 `ElMessageBox.close()`，避免取消確認對話框殘留到下一頁。關閉後 `confirm` 以 `'close'` reject，走「使用者拒絕」路徑，不呼叫取消 API、不顯示錯誤。`ElMessageBox.close()` 會關閉任何開啟中的 MessageBox，本專案頁面切換時不會有其他頁面的 MessageBox 需要保留，可接受。

`confirm-order` 有 rate limit：429 與其他失敗一樣走「重新查詢＋失敗訊息」流程，但失敗訊息固定為「請求過於頻繁，請稍後再試」（與 `EventDetailPage` 對 429 的文案一致），不顯示後端原始訊息。

替代方案：`OrderResultPage` 成功後直接導向明細頁、移除結果頁——改動較大且改變既有導覽流程，本次不做。

### 決策 3：狀態中文標籤集中在單一前端模組

新增 `web/src/utils/statusLabels.ts`，提供訂單狀態（Pending 待付款／Paid 已付款／Cancelled 已取消／Expired 已逾時）與票券狀態（Issued 已出票／Redeemed 已核銷）對照。遇到未知值時回傳原字串，不丟例外、不顯示空白——後端新增列舉值時畫面仍可辨識。`Voided` 不列入對照（維持 `buyer-web-ui` 既有「不為 Voided 實作顯示邏輯」規定，未知值 fallback 即原字串）。`MyOrganizersPage` 的主辦方狀態對照屬不同領域，本次不搬移。

### 決策 4：審核頁入口放在買家端會員下拉選單

平台 Admin 登入後預設落在買家端，現有下拉選單已有「我的主辦方」。在其下方新增「主辦方審核」，僅 `authStore.isAdmin` 時顯示，導向 `/admin/organizers`（該路由守衛本來就只要求 Admin 角色）。不新增頂部導覽列項目，避免一般買家版面出現後台概念。

### 決策 5：互動與呈現細節（依 `emil-design-eng` 原則檢視）

專案目前尚無 `el-tag` 或確認對話框的使用先例，本決策即為首例慣例：

- **取消訂單需二次確認，確認付款不需要**：取消會釋放座位且不可復原，以 `ElMessageBox.confirm` 確認；對話框按鈕文字為「取消訂單」（danger）與「保留訂單」，MUST NOT 使用「確定」／「取消」——在「取消訂單？」的語境下「取消」按鈕語意含糊。使用者關閉或選「保留訂單」時不呼叫 API、不顯示錯誤。確認付款是使用者主要意圖，多一層確認只增加摩擦，直接執行。`ElMessageBox` 是 DOM 元件，不是瀏覽器原生 `confirm()`，不影響 claude-in-chrome 實機驗證。composable 以 `ElMessageBox.confirm(...)` 物件方法呼叫（不在 import 時解構），測試才能以 `vi.spyOn` 替換；reject 值為 `'cancel'`／`'close'` 字串時視為使用者拒絕、正常結束，其他 reject 值屬非預期例外，MUST 重拋（不得吞掉，CLAUDE.md 禁止靜默失敗）。
- **狀態以 `el-tag` 呈現並依狀態上色**：訂單 Pending→`warning`、Paid→`success`、Cancelled／Expired→`info`；票券 Issued→`success`、Redeemed→`info`；未知值→`info` 並顯示原字串。顏色沿用 `morandi.css` 覆寫後的 Element Plus token，不另定色。標籤內一律有中文文字，顏色不是唯一辨識方式（色覺辨識障礙）。對照與 tag type 一併放在決策 3 的 `statusLabels.ts`。
- **重新查詢期間不清空畫面**：現行 `OrderDetailPage.loadOrder` 會先把 `order` 設為 `null`，操作後重新查詢會造成內容消失再出現的閃爍。改為只有首次載入顯示載入狀態，操作後的重新查詢保留目前內容直到新資料回來。
- **成功時顯示 `ElMessage.success`**：「付款成功，票券已出票」／「訂單已取消」。屬偶發操作，明確回饋讓使用者確知系統已處理；失敗訊息維持決策 2 的頁內 `el-alert`。
- **按鈕樣式**：「確認付款」為 `primary`，「取消訂單」為預設樣式（非 danger），避免頁面同時出現兩個高強調按鈕；danger 只用在確認對話框的最終按鈕。操作中被按下的按鈕顯示 loading、另一顆 disabled。
- **不新增動畫**：沿用 Element Plus 元件內建轉場；本次沒有需要以動畫說明的狀態變化，自訂動畫只會增加維護成本。

## 安全確認（CLAUDE.md 安全強制規則）

本 change 觸發「身份驗證／授權」（呼叫需驗證的 API、新增角色相關入口）條件；不新增資料庫讀寫、不新增外部輸入。逐條回答如下：

**輸入驗證**
- 本次不新增任何外部輸入：確認／取消端點沿用既有路由參數 `{id:guid}`（ASP.NET Core 路由約束擋掉非 GUID），無 request body；訂單 Id 來自路由參數，僅用於組 API 路徑；前端刻意不做 GUID 格式檢查，唯一的格式驗證層是後端路由約束 `{id:guid}`（非 GUID 值回 404，前端依既有 404 提示處理；結果頁與明細頁分別見 BW-RESULT-007、BW-MYORDER-INVALID-ID-001），避免前後端各維護一份格式規則。
- 沒有任何值拼接進 SQL 或 shell 指令（無後端變更）。

**資料庫**
- 無後端變更，沿用既有 `buyer-order-query`、`ticket-purchase` 端點的 EF Core 查詢。

**權限**
- 明細查詢：既有 `OrdersController` `[Authorize]` + `GetMyOrderDetailHandler` 本人檢查（非本人 403、不存在 404），前端依此顯示提示且不提供操作。
- 確認／取消：沿用既有 `OrderService.ChangeOrderStatusAsync` 本人檢查（`ticket-purchase` 既有 scenario「非本人確認／取消他人訂單」），前端按鈕顯示與否不構成授權。
- 審核頁入口：前端只是顯示／隱藏連結，實際授權仍由路由守衛（`requiresAdmin`）與後端 `AdminOnly` Policy 把關；非 Admin 直接輸入網址仍被擋。

**前端**
- 確認對話框使用 `ElMessageBox.confirm` 時 MUST NOT 開啟 `dangerouslyUseHTMLString`，訊息為固定文字。
- 狀態標籤、訂單 Id、錯誤訊息一律以 Vue 文字插值（`{{ }}`）或 Element Plus 元件屬性渲染（自動跳脫），MUST NOT 使用 `v-html`／`innerHTML`。
- 明細查詢、確認、取消呼叫一律經 `api/orders.ts` → `authorizedRequest`（統一注入 Authorization Header 並處理 401 換發），元件不直接呼叫 `fetch`。

## Risks / Trade-offs

- [`OrderResultPage` 進入時多一次 API 呼叫] → 僅一次查詢，換取重新整理後狀態正確，可接受。
- [`buyer-web-ui` 既有「結果頁不查詢 API」規定被推翻] → 以 MODIFIED Requirement 明確改寫並說明原因，歸檔時同步主 spec。
- [移除 `utils/orderHold.ts` 後，下單導向結果頁不再帶 `heldUntilUtc` 參數] → 結果頁改由 API 取得，舊書籤網址上殘留的參數會被忽略，無副作用。
- [本 change 歸檔前，明細頁仍無活動名稱、座位資訊] → 屬拆分後的過渡狀態，由 `order-display-enrichment` 補上。

## Migration Plan

純前端變更，無資料遷移。回滾即還原程式碼。
