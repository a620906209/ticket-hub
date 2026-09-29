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

確認付款可能失敗（Mock 付款失敗、訂單已逾時被推導為 Expired、並發下已被取消或被背景清理轉為 Cancelled）。前端不嘗試依錯誤類型推測訂單新狀態，一律重新呼叫明細 API，錯誤訊息在重新查詢**之後**設定，避免被載入流程清空（`EventDetailPage` 曾因順序錯誤導致錯誤訊息消失，見該檔案 `handleSubmit` 註解）。確認成功後明細會顯示已出票的票券與「查看 QR Code」操作，買家不需另外導頁。

`OrderResultPage` 共用相同模式：進入頁面即呼叫明細 API 取得狀態與 `HeldUntilUtc`，按鈕只在伺服器狀態為 `Pending` 時顯示。為避免兩頁重複實作「載入明細＋確認＋取消＋重新查詢」，抽成 composable `usePendingOrderActions`（或同等命名，實作時決定）供兩頁共用。

替代方案：`OrderResultPage` 成功後直接導向明細頁、移除結果頁——改動較大且改變既有導覽流程，本次不做。

### 決策 3：狀態中文標籤集中在單一前端模組

新增 `web/src/utils/statusLabels.ts`，提供訂單狀態（Pending 待付款／Paid 已付款／Cancelled 已取消／Expired 已逾時）與票券狀態（Issued 已出票／Redeemed 已核銷）對照。遇到未知值時回傳原字串，不丟例外、不顯示空白——後端新增列舉值時畫面仍可辨識。`Voided` 不列入對照（維持 `buyer-web-ui` 既有「不為 Voided 實作顯示邏輯」規定，未知值 fallback 即原字串）。`MyOrganizersPage` 的主辦方狀態對照屬不同領域，本次不搬移。

### 決策 4：審核頁入口放在買家端會員下拉選單

平台 Admin 登入後預設落在買家端，現有下拉選單已有「我的主辦方」。在其下方新增「主辦方審核」，僅 `authStore.isAdmin` 時顯示，導向 `/admin/organizers`（該路由守衛本來就只要求 Admin 角色）。不新增頂部導覽列項目，避免一般買家版面出現後台概念。

## 安全確認（CLAUDE.md 安全強制規則）

本 change 觸發「身份驗證／授權」（呼叫需驗證的 API、新增角色相關入口）條件；不新增資料庫讀寫、不新增外部輸入。逐條回答如下：

**輸入驗證**
- 本次不新增任何外部輸入：確認／取消端點沿用既有路由參數 `{id:guid}`（ASP.NET Core 路由約束擋掉非 GUID），無 request body；訂單 Id 來自路由參數，僅用於組 API 路徑。
- 沒有任何值拼接進 SQL 或 shell 指令（無後端變更）。

**資料庫**
- 無後端變更，沿用既有 `buyer-order-query`、`ticket-purchase` 端點的 EF Core 查詢。

**權限**
- 明細查詢：既有 `OrdersController` `[Authorize]` + `GetMyOrderDetailHandler` 本人檢查（非本人 403、不存在 404），前端依此顯示提示且不提供操作。
- 確認／取消：沿用既有 `OrderService.ChangeOrderStatusAsync` 本人檢查（`ticket-purchase` 既有 scenario「非本人確認／取消他人訂單」），前端按鈕顯示與否不構成授權。
- 審核頁入口：前端只是顯示／隱藏連結，實際授權仍由路由守衛（`requiresAdmin`）與後端 `AdminOnly` Policy 把關；非 Admin 直接輸入網址仍被擋。

**前端**
- 狀態標籤、訂單 Id、錯誤訊息一律以 Vue 文字插值（`{{ }}`）或 Element Plus 元件屬性渲染（自動跳脫），MUST NOT 使用 `v-html`／`innerHTML`。
- 明細查詢、確認、取消呼叫一律經 `api/orders.ts` → `authorizedRequest`（統一注入 Authorization Header 並處理 401 換發），元件不直接呼叫 `fetch`。

## Risks / Trade-offs

- [`OrderResultPage` 進入時多一次 API 呼叫] → 僅一次查詢，換取重新整理後狀態正確，可接受。
- [`buyer-web-ui` 既有「結果頁不查詢 API」規定被推翻] → 以 MODIFIED Requirement 明確改寫並說明原因，歸檔時同步主 spec。
- [移除 `utils/orderHold.ts` 後，下單導向結果頁不再帶 `heldUntilUtc` 參數] → 結果頁改由 API 取得，舊書籤網址上殘留的參數會被忽略，無副作用。
- [本 change 歸檔前，明細頁仍無活動名稱、座位資訊] → 屬拆分後的過渡狀態，由 `order-display-enrichment` 補上。

## Migration Plan

純前端變更，無資料遷移。回滾即還原程式碼。
