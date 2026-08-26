## Context

後端 `ticket-type-requires-seat`（已歸檔）已經讓 `TicketType` 支援 `RequiresSeat` 開關與 `AvailableQuantity` 純計數庫存，`event-management`／`ticket-ordering`／`ticket-purchase` 三個 API 能力都已就緒：

- `CreateTicketTypeRequest(ZoneCode, Price, RequiresSeat = true, AvailableQuantity = null)` — Admin 建立票種
- `TicketTypeDto(Id, ZoneCode, Price, RequiresSeat, AvailableQuantity)` — 買家端查詢票種列表
- `PlaceOrderSelectionRequest(Guid? EventSeatId, Guid TicketTypeId, int Quantity = 1)` — 建立訂單，`EventSeatId` 為 `null` 代表純計數項目

但前端完全沒有串接：
- `web/src/api/admin.ts` 的 `createTicketType(eventId, zoneCode, price)` 沒有 `requiresSeat`／`availableQuantity` 參數
- `web/src/types/apiResponses.ts` 的 `TicketType` 介面只有 `id`／`zoneCode`／`price`
- `web/src/pages/buyer/EventDetailPage.vue` 只用 `ticketTypeByZone`（依 `zoneCode` 對照）驅動座位選購，沒有任何「輸入購買數量」的操作路徑
- `web/src/api/orders.ts` 的 `PlaceOrderSelection` 只有 `eventSeatId`（必填）／`ticketTypeId`，沒有 `quantity`

本次是純前端變更，不改動任何後端程式碼或 API 契約。

## Goals / Non-Goals

**Goals:**
- Admin 建立票種表單可以建立 `RequiresSeat = false` 的純計數票種（含 `AvailableQuantity`）
- 買家在活動詳情頁可以對純計數票種輸入購買數量並送出訂單，不需要選座位
- 同一次下單可以混合座位選購與計數購買（呼叫既有支援混合項目的 `POST /api/orders`）
- 每筆訂單限購張數（`MaxTicketsPerOrder`）的前端即時提示，涵蓋座位與計數兩種選購方式的總和

**Non-Goals:**
- 不修改任何後端程式碼、資料庫、API 契約——三個相關能力（`event-management`／`ticket-ordering`／`ticket-purchase`）已完整支援本次前端需求
- 區域隨選快速下單（`buyer-web-ui` 既有能力）不擴充支援純計數票種，維持僅適用座位選購
- 不處理純計數票種的即時庫存推播；`AvailableQuantity` 顯示比照既有座位狀態的既定慣例——頁面載入或手動重新整理當下查詢結果，非即時更新

## Decisions

### 決策 1：計數購買用獨立的「數量輸入」狀態，不重用 `SelectedSeat` 陣列
`selectedSeats`／`toggleSeat` 這條既有邏輯（座位網格點選、`selectedSeatIds` Set 查找優化）是「一個座位一次可切換」的互動模型，跟純計數票種「對同一個票種輸入一個購買數量」的互動模型本質不同——後者沒有「單一項目」可以點選/取消，是連續數量輸入。

**選擇**：新增獨立的 `countQuantities` reactive 物件（`Record<ticketTypeId, number>`），純計數票種區塊改用 `el-input-number` 逐票種輸入購買數量（0 代表不購買，送出訂單時過濾掉）。座位選購邏輯完全不變。

**替代方案**：把 `SelectedSeat` 改成聯合型別（座位項目 | 計數項目），統一放進同一個陣列。放棄理由——`selectedSeatIds`（座位網格高亮判斷）、`buildSelection`（依 `zoneCode` 找票種）等既有邏輯都是針對「座位」設計，硬塞聯合型別會讓這些函式到處補型別窄化判斷，且座位網格與計數輸入框在 UI 呈現上本來就是兩塊不同區域，分開的狀態更符合實際互動模型（Rule 2 簡潔優先：不為了共用一個陣列而增加分支複雜度）。

### 決策 2：`PlaceOrderSelection` 型別擴充為 `eventSeatId: string | null` + 選填 `quantity`
比照後端 `PlaceOrderSelectionRequest(Guid? EventSeatId, Guid TicketTypeId, int Quantity = 1)`，前端型別同步改為：

```ts
export interface PlaceOrderSelection {
  eventSeatId: string | null
  ticketTypeId: string
  quantity?: number
}
```

座位選購項目送出時維持 `{ eventSeatId, ticketTypeId }`（不帶 `quantity`，比照既有行為與既有測試，後端預設視為 1）；計數項目送出 `{ eventSeatId: null, ticketTypeId, quantity }`。

### 決策 3：限購張數（`remainingCapacity`）改為座位與計數選購的合併計算
現況 `remainingCapacity = maxTicketsPerOrder - selectedSeats.length`。改為：

```ts
const countTotal = computed(() => Object.values(countQuantities).reduce((sum, n) => sum + n, 0))
const remainingCapacity = computed(() =>
  maxTicketsPerOrder.value === null ? Infinity : maxTicketsPerOrder.value - selectedSeats.value.length - countTotal.value,
)
```

座位網格點選（`toggleSeat`）、區域隨選（`handleQuickPick`）、計數輸入框的 `max` 上限都讀這個合併後的值，確保兩種購買方式共用同一份剩餘額度、不會各自超額（前端只是提示，後端 `PlaceOrderAsync` 仍是最終把關，維持既有 `buyer-web-ui` spec 對這點的既定說明）。

### 決策 4：Admin 表單的 `RequiresSeat` 開關直接控制表單欄位顯示，不做「進階選項摺疊」
建立票種表單新增 `el-switch` 對應 `RequiresSeat`（預設 `true`，維持既有行為）。切換為 `false` 時：
- `ZoneCode` 欄位標籤改為「票種名稱」（後端不驗證是否對應座位圖分區，見 `event-management` spec 決策），輸入邏輯不變（同一個文字欄位）
- 額外顯示「可售總量」`el-input-number`（必填正整數），對應 `AvailableQuantity`
- 切換回 `true` 時清空已輸入的可售總量，避免送出時殘留舊值造成後端驗證錯誤（比照既有「切換場館清除座位圖」的處理慣例）

**替代方案**：兩種模式做成分頁籤（Tab）分開的表單。放棄理由——欄位差異只有一個「可售總量」，不足以支撐兩份表單的維護成本，Switch + 條件式欄位更精簡。

### 決策 5：純計數票種的價格資訊顯示在既有票種表格，不另建區塊
`el-table` 票種清單新增「模式」欄（座位制／計數制）與「可售總量」欄（`RequiresSeat = false` 時顯示數字，否則顯示「—」）；下方購票操作區則分成「選位購票」（既有座位網格＋區域隨選）與新增的「計數購票」兩個區塊並列顯示，各自只列出對應模式的票種。

### 決策 6：計數輸入採「限制型」而非「驗證型」，不允許暫時性的超額顯示值
`el-input-number` 的 `max` 屬性會直接限制使用者能輸入的數值上限，這與「允許輸入超額值、送出前才顯示錯誤」的驗證型互動模式是兩種不同的可驗收行為，不能混用（否則規格與實作互相矛盾）。

**選擇**：計數輸入框全面採用限制型——`max` 動態設為 `min(availableQuantity, remainingCapacity + 自己目前輸入值)`，使用者永遠無法讓顯示值超過上限，因此「輸入超過可售總量」不會出現「顯示錯誤訊息」的情況（結構上被元件擋掉）。真正需要顯示「下單失敗」錯誤訊息的情況，是**送出當下**後端因庫存已於前端載入之後被其他買家變動而拒絕——這是競態條件，不是前端輸入驗證失敗，兩者在 spec 裡拆成兩個各自獨立的 Scenario（「計數輸入元件限制數量不得超過可售總量」與「送出時因庫存已變動被後端拒絕」），避免同一段需求文字同時描述兩種矛盾的 UI 行為。

**替代方案**：允許輸入超額值，送出前用獨立驗證邏輯攔截並顯示錯誤。放棄理由——`el-input-number` 原生就有 `max` 限制能力，不需要另外重造一套驗證邏輯；限制型的使用者體驗也更直覺（打字打到上限就打不動，比打完按下送出才被拒絕更早給回饋）。

### 決策 7：未登入時，調整計數購買數量比照既有「選位觸發登入」規則，於互動當下立即攔截
既有 `buyer-web-ui` 基準規格已定義「選位」是需登入才能執行的動作（`toggleSeat`、`handleQuickPick` 都在座位/選位邏輯最前面檢查 `authStore.isAuthenticated`，未登入立即導向登入頁、不套用任何選位狀態變更）。純計數票種的「輸入購買數量」是座位選購的對應購票動作，理應遵循同一條規則，而不是留到「送出訂單」才檢查。

**選擇**：計數輸入框改用 `:model-value` + `@change`（而非直接 `v-model`），`@change` handler 開頭先檢查 `authStore.isAuthenticated`；未登入時直接 `router.push` 導向登入頁（帶 `redirect` 查詢參數），不寫入 `countQuantities`，讓輸入框顯示值維持原本的 0（不套用剛剛的輸入）。因為攔截發生在狀態寫入之前，登入導回後沒有「暫存值」需要還原，行為與既有座位選位攔截邏輯完全對稱。

**替代方案**：允許未登入使用者自由輸入數量，只在點擊「送出訂單」時才檢查登入狀態並導頁。放棄理由——這與既有座位選位「一碰就導頁」的既定互動模式不一致，會讓同一個頁面對「座位」與「計數票種」兩種購票方式的登入要求時機不同，造成使用者困惑；也不符合本專案既有慣例（選位本身就是購票行為的第一步，未登入直接導去登入頁）。

### 決策 8：下單失敗一律「清空＋刷新」，不依錯誤類型（庫存衝突／驗證失敗／網路異常）做差異化處理
既有座位選購的 `handleSubmit` 錯誤處理（`catch` 區塊）目前對任何錯誤都無差別地清空 `selectedSeats` 並重新呼叫 `loadData()`，沒有依錯誤類型分流。本次新增計數購買後，若導入「庫存衝突清空、驗證失敗保留、網路異常允許重試」這種依錯誤類型分流的邏輯，會是本次變更範圍之外的新行為，且需要能可靠分辨錯誤類型（後端目前的錯誤回應格式是否足以支撐這種分類，超出本次前端變更的調查範圍）。

**選擇**：維持現況的統一處理——任何非 401 的下單失敗（座位衝突、計數庫存於送出當下被買光、後端其他驗證失敗）一律顯示錯誤訊息、清空 `selectedSeats` 與新增的 `countQuantities`、重新呼叫 `loadData()` 刷新。401（含 Refresh Token 換發失敗）**不能**指望既有 `buyer-web-ui`「Access Token 過期時前端自動換發」能力背後的 `App.vue` 全域 watcher 處理——該 watcher 只在目前路由的 meta 標示 `requiresAuth`／`requiresAdmin` 時才會導向登入頁（見 `App.vue` 實作），而活動詳情頁是公開頁（未登入也能瀏覽、選位是第一個需要登入的動作，見 buyer-web-ui 既有規格），路由 meta 沒有這個標記，全域 watcher 不會處理這裡的登入失效。實作查證後發現：若不特別處理，401 會落入這個元件的 `catch` 區塊，被當成一般下單失敗，顯示誤導訊息並清空選購狀態，不會導向登入頁。因此本次在 `handleSubmit` 的 `catch` 區塊裡新增 `error instanceof ApiError && error.status === 401` 的特判，401 時直接導向登入頁、不套用下方的統一清空+刷新處理。

**替代方案**：依錯誤類型分別處理（庫存衝突清空、驗證失敗保留輸入、網路異常允許重試）。放棄理由——這是明顯的範疇擴張（Rule 2 簡潔優先），現有座位選購從未做過這種分流，本次是純前端功能擴充而非既有錯誤處理機制的重新設計；若未來有明確需求要分流重試邏輯，應該另開提案，屆時一併重新設計座位與計數共用的錯誤處理，而不是只為計數購買加一套特例。

## Risks / Trade-offs

- **[風險] `AvailableQuantity` 顯示的可售總量是頁面載入當下的快照，買家輸入送出前庫存可能已被其他買家買光** → **緩解**：見決策 8——統一比照既有座位選購失敗的處理方式，不分錯誤類型，一律清空並刷新，不額外做樂觀鎖重試或警告彈窗
- **[風險] `PlaceOrderSelection.eventSeatId` 型別從必填改為可為 null，可能影響既有呼叫端型別檢查** → **緩解**：確認呼叫端只有 `EventDetailPage.vue` 一處，改動同一個 PR 內一併調整；送出座位項目時仍固定帶入實際的 `eventSeatId` 字串（型別放寬不代表既有座位選購行為改變），純計數項目才帶 `null`。`npm run build`（`vue-tsc -b`）納入驗收步驟，確認型別放寬後編譯無誤
- **[風險 / 已知缺口] `EventDetailPage.vue` 目前沒有任何既有測試檔（`git log` 確認自 `ticketing-web-ui` 建立以來從未有對應 `.test.ts`），本次要大幅修改其狀態管理（`remainingCapacity` 合併計算）卻沒有既有測試能驗證「修改前後既有座位選購行為是否不受影響」** → **緩解**：本次變更範圍內一併補上座位選購既有行為（選位、區域隨選、下單成功/失敗、限購張數）的基礎測試覆蓋，而非僅測試新增的計數購買行為——測試涵蓋範圍見 tasks.md 第 5 節；這是順帶補齊既有測試缺口，非本次變更引入的新風險，但若不補上，`remainingCapacity` 計算邏輯改動就完全沒有測試防線
- **[風險] 計數購買數量輸入框上限（`max`）與座位選購共用 `remainingCapacity`，若使用者先輸入計數數量、再切換去點座位，`el-input-number` 的 `max` 不會即時重新限制已輸入的值** → **緩解**：送出訂單前（`handleSubmit`）統一再檢查一次總數是否超過限購張數，超過時擋下並提示，不依賴各輸入框的 `max` 屬性作為唯一防線（見決策 6 限制型輸入 + 決策 3 送出前二次防線）
- **[風險] 決策 6 選擇限制型輸入而非驗證型，若後端在其他地方（例如未來新增的其他呼叫端）仍以「允許超額輸入＋錯誤訊息」的方式驗證，前後端行為不一致** → **緩解**：後端 `event-management`／`ticket-ordering` 本來就是在 API 層做最終驗證（回傳錯誤而非限制輸入），前端限制型只是「提前擋下」而非「取代後端驗證」，兩者本來就是不同層級的防線，不衝突；後端行為本次未變更，不受影響
- **[風險 / 已知缺口] 活動詳情頁是公開頁，`App.vue` 的全域 401 watcher 只在路由 meta 標示 `requiresAuth`／`requiresAdmin` 時才會導向登入頁，不會處理這裡的登入失效——這是既有 `ticketing-web-ui` 實作原本就有的缺口，本次因為決策 8 統一改寫 `handleSubmit` 的 `catch` 區塊而被實測發現（送出訂單當下換發失敗，401 會被當成一般下單失敗處理，顯示誤導訊息並清空選購狀態）** → **緩解**：`catch` 區塊新增 `error instanceof ApiError && error.status === 401` 的特判，401 時直接導向登入頁、不落入下方的統一清空+刷新處理；不修改 `App.vue` 全域 watcher 的判斷條件（會影響所有公開頁的既有行為，超出本次範疇）

## Migration Plan

純前端變更，無資料庫或 API migration。部署後舊有座位選購流程行為不變（不影響既有 `buyer-web-ui`／`admin-web-ui` 測試涵蓋的座位制場景）。無需 feature flag——`RequiresSeat = false` 的票種本來就不存在於現有資料（後端此開關上線以來前端從未建立過），上線即生效，不影響既有活動與訂單。
