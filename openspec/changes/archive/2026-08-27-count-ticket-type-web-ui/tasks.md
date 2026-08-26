## 1. 型別與 API 層擴充

- [x] 1.1 `web/src/types/apiResponses.ts` 的 `TicketType` 介面新增 `requiresSeat: boolean`、`availableQuantity: number | null`，對應既有後端 `TicketTypeDto(Id, ZoneCode, Price, RequiresSeat, AvailableQuantity)`
- [x] 1.2 `web/src/api/admin.ts` 的 `createTicketType` 簽章擴充為 `createTicketType(eventId, zoneCode, price, requiresSeat = true, availableQuantity?)`，對應既有後端 `CreateTicketTypeRequest(ZoneCode, Price, RequiresSeat = true, AvailableQuantity = null)`；`requiresSeat` 預設 `true` 時不带 `availableQuantity` 欄位，維持既有呼叫端（若有）不受影響
- [x] 1.3 `web/src/api/orders.ts` 的 `PlaceOrderSelection` 型別改為 `{ eventSeatId: string | null; ticketTypeId: string; quantity?: number }`，對應既有後端 `PlaceOrderSelectionRequest(Guid? EventSeatId, Guid TicketTypeId, int Quantity = 1)`；座位項目固定帶入實際 `eventSeatId` 字串，型別放寬不改變既有座位選購的送出內容
- [x] 1.4 完成 1.1–1.3 後執行 `docker compose exec web npm run build`（`vue-tsc -b`），確認型別放寬與擴充後專案編譯無型別錯誤

## 2. Admin 建立票種表單（`EventListPage.vue`）

- [x] 2.1 建立票種表單新增「是否綁座位」`el-switch`（對應 `RequiresSeat`，預設開啟）
- [x] 2.2 開關關閉時，原分區代碼欄位標籤動態改為「票種名稱」，並額外顯示「可售總量」`el-input-number`（正整數，必填）；「票種名稱」欄位不額外做長度/空白字元的前端驗證，比照既有「分區代碼」欄位本來就沒有前端格式驗證的既定行為，完全交由後端 API 驗證（design.md 已記錄此決策，不需額外測試涵蓋）
- [x] 2.3 開關關閉且可售總量未填或為 0／負數時，前端顯示驗證錯誤、不呼叫 `createTicketType`
- [x] 2.4 開關從關閉切回開啟時，清空已輸入的可售總量欄位值，送出表單不帶該欄位
- [x] 2.5 票種清單新增「模式」欄（座位制／計數制）與「可售總量」欄，依查詢 API 回傳的 `requiresSeat`／`availableQuantity` 顯示（`RequiresSeat = false` 時顯示數字，否則顯示「—」），涵蓋頁面初始載入既有票種與新建立票種兩種情況

## 3. 買家活動詳情頁：計數購票區塊（`EventDetailPage.vue`）

- [x] 3.1 依 `ticketTypes` 的 `requiresSeat` 欄位拆分票種清單：座位制票種維持既有座位網格＋區域隨選流程；計數制票種進入新的「計數購票」區塊
- [x] 3.2 「計數購票」區塊為每個 `RequiresSeat = false` 的票種顯示票名（`zoneCode` 欄位值）、票價、可售總量（`availableQuantity`），並提供數量輸入元件（預設 0，`min=0`）
- [x] 3.3 新增 `countQuantities`（`Record<ticketTypeId, number>`）reactive 狀態管理計數購買輸入值；新增 `countTotal` computed 加總目前所有計數購買數量
- [x] 3.4 `remainingCapacity` 計算改為 `maxTicketsPerOrder - selectedSeats.length - countTotal`；座位網格點選（`toggleSeat`）、區域隨選（`handleQuickPick`）皆改用此合併後的值，確保手動選位、區域隨選、計數輸入三者互相即時反映彼此佔用的額度（不限制未設定限購張數的活動）
- [x] 3.5 每個計數輸入元件改用 `:model-value` + `@change`（不直接用 `v-model`），`@change` handler 開頭先檢查 `authStore.isAuthenticated`：未登入時 `router.push` 導向登入頁（帶 `redirect` 查詢參數），不寫入 `countQuantities`（輸入框維持顯示 0）；已登入時才寫入，並將 `max` 動態計算為 `min(該票種 availableQuantity, remainingCapacity + 自己目前輸入值)`（限制型輸入——元件本身擋掉超過上限的值，不呈現為錯誤狀態）；未設定 `maxTicketsPerOrder` 時僅受 `availableQuantity` 限制；`availableQuantity = 0` 時 `max` 為 0，並顯示「已售完」提示（非錯誤樣式）
- [x] 3.6 `handleSubmit` 送出訂單前：過濾掉 `countQuantities` 中數量為 0 的票種，合併座位選購與計數購買（數量 > 0 者）組成 `PlaceOrderSelection[]`——座位項目 `{ eventSeatId, ticketTypeId }`（不帶 `quantity`），計數項目 `{ eventSeatId: null, ticketTypeId, quantity }`；送出前重新加總座位數與計數購買總量，若仍超過 `maxTicketsPerOrder` 則擋下並提示、不呼叫 API（不得只依賴各輸入元件的 `max` 屬性把關）；`handleSubmit` 由 `handleQuickPick` 共用呼叫，故區域隨選送出時會自動一併帶上已輸入的計數購買項目，不需要額外程式碼
- [x] 3.7 `handleSubmit` 的 `catch` 區塊優先判斷 `error instanceof ApiError && error.status === 401`：是則直接 `router.push` 導向登入頁（帶 `redirect` 查詢參數），不顯示下單失敗訊息、不清空/刷新；活動詳情頁是公開頁，`App.vue` 全域 401 watcher 只在 `requiresAuth`／`requiresAdmin` 路由才會導頁，不會處理這裡（design.md 決策 8 風險項，實測發現的既有缺口）。其餘失敗（座位被搶、計數庫存於送出當下已變動、或其他後端驗證失敗）一律：顯示錯誤訊息、清空 `selectedSeats` 與 `countQuantities`、重新呼叫 `loadData()` 刷新票種與座位資訊；不依錯誤類型做差異化處理
- [x] 3.9 計數輸入元件的 `max` 計算時，若遇到 `availableQuantity` 為 `null`（不應發生，後端保證 `RequiresSeat = false` 時必定回傳可售總量，屬防禦性處理、非常規 AC）：停用該票種的輸入元件並顯示「資料異常」提示，不進行 `min(availableQuantity, ...)` 運算，避免 `null` 參與計算得到非預期結果
- [x] 3.8 確認 `zoneOptions`（區域隨選分區下拉選單的選項來源）與 `handleQuickPick` 的候選座位集合（`candidates`）皆衍生自 `seats.value`（實際 `EventSeat` 記錄），純計數票種本來就沒有對應的 `EventSeat` 資料，因此結構上不會出現在分區選單或抽選候選集合中；此為既有架構的自然結果，不需新增額外過濾邏輯，僅需在 5.20 補上回歸測試防止未來改動意外破壞這個前提

## 4. 測試：admin-web-ui（對應 `specs/admin-web-ui/spec.md` MODIFIED）

> `specs/admin-web-ui/spec.md` 的「Admin 可透過介面管理活動與票種」依 OpenSpec MODIFIED 慣例完整複製了既有 Requirement 區塊（含全部既有 Scenario，避免歸檔時遺失內容），並以【本次新增】標記本次實際新增/修改的 5 個 Scenario（4.1–4.5 對應）。其餘 11 個既有 Scenario（無標記）行為未變更，現況測試覆蓋盤點如下（非本次變更範圍，僅供釐清邊界，不在本次任務中補測）：
> - 已有測試：建立活動、選擇場館後座位圖下拉選單隨之更新、切換場館後清除已選座位圖、尚未有任何場館或座位圖時無法選擇（`EventCreatePage.test.ts`）；活動列表顯示建立者與建立時間、活動列表顯示本次功能上線前建立的舊活動、活動列表顯示售票狀況橫條圖（`EventListPage.test.ts`）
> - 查無對應測試（既有缺口，非本次引入）：進入建立活動頁面、建立活動時填寫說明/海報網址/限購張數、建立活動時不填說明/海報網址/限購張數、限購張數填寫非正整數——這 4 項若要補測，建議另開獨立任務處理，不與本次純計數票種變更混在一起

- [x] 4.1 [前端單元測試／Vitest] `EventListPage.test.ts`：維持「是否綁座位」開關為開啟，建立票種送出 `RequiresSeat = true`（對應 Scenario「為活動建立座位制票種」）
- [x] 4.2 [前端單元測試／Vitest] `EventListPage.test.ts`：關閉開關並填寫票種名稱、票價、可售總量，建立票種送出 `RequiresSeat = false` 與 `AvailableQuantity`，票種清單顯示計數制與可售總量（對應 Scenario「為活動建立純計數票種」）
- [x] 4.3 [前端單元測試／Vitest] `EventListPage.test.ts`：關閉開關但可售總量留空或填 0，斷言顯示驗證錯誤、不呼叫 `createTicketType`（對應 Scenario「建立純計數票種未填可售總量」）
- [x] 4.4 [前端單元測試／Vitest] `EventListPage.test.ts`：關閉開關輸入可售總量後重新開啟開關，斷言可售總量欄位值被清空（對應 Scenario「切換回座位制時清空可售總量」）
- [x] 4.5 [前端單元測試／Vitest] `EventListPage.test.ts`：mock 查詢 API 回傳一個座位制與一個計數制的既有票種，開啟活動列表頁展開票種清單，斷言模式標籤與可售總量欄位正確顯示（對應 Scenario「開啟活動列表頁時票種清單正確顯示既有票種的模式與可售總量」）

## 5. 測試：buyer-web-ui（對應 `specs/buyer-web-ui/spec.md` MODIFIED）

> `EventDetailPage.vue` 目前沒有任何既有測試檔（自 `ticketing-web-ui` 建立以來從未有對應 `.test.ts`，`git log` 已確認）。本節新增 `EventDetailPage.test.ts`，範圍涵蓋既有座位選購行為（過去未覆蓋的技術債，見 design.md Risks）與本次新增的計數購買行為，不是「重跑既有測試」。以下任務對應 spec 裡全部 23 個 Scenario（5.16 一項涵蓋 2 個 Scenario）。

- [x] 5.1 [前端單元測試／Vitest，新建] `EventDetailPage.test.ts`：mock 純座位制活動，選擇一個或多個 Available 座位並送出，斷言下單 API 成功呼叫、導向訂單結果頁（對應既有 Scenario「選擇可售座位並成功下單」，建立基礎座位選購測試覆蓋）
- [x] 5.2 [前端單元測試／Vitest] 下單 API 回傳失敗（座位被搶），斷言顯示錯誤訊息、不導向結果頁、清空已選座位與計數輸入、重新載入資料（對應 Scenario「下單時座位已被搶先鎖定」）
- [x] 5.2a [前端單元測試／Vitest] 下單 API 回傳 401（`ApiError(401, ...)`），斷言導向登入頁（帶 `redirect` 查詢參數）、不顯示下單失敗訊息；並且已選座位與已輸入的計數購買數量原樣保留（不是「不顯示訊息」就假設沒清空，須直接斷言摘要文字仍顯示原本的座位數/計數張數）、`getEvents`／`getEventSeats`／`getTicketTypes` 皆未被重新呼叫（次數維持初始載入的 1 次）——只驗證「有導頁」不足以防止未來有人誤把 `loadData()`／`clearSelections()` 放回 401 分支（對應 Scenario「送出訂單時登入狀態已失效（401）」）
- [x] 5.3 [前端單元測試／Vitest] 活動設有限購張數，已選座位數達到上限時再點選新座位，斷言被阻擋並顯示提示（對應 Scenario「已選座位數達到每筆訂單限購張數」）
- [x] 5.4 [前端單元測試／Vitest] 活動未設定限購張數，斷言可選任意數量座位不受限（對應既有 Scenario「活動未設定限購張數」）
- [x] 5.5 [前端單元測試／Vitest] 未登入使用者嘗試調整某純計數票種的購買數量，斷言立即導向登入頁（帶 `redirect` 查詢參數）、`countQuantities` 未被寫入（對應 Scenario「未登入嘗試調整計數購買數量」）
- [x] 5.6 [前端單元測試／Vitest] mock 含 `RequiresSeat = false` 票種的活動，已登入買家對該票種輸入購買數量並送出，斷言呼叫下單 API 時該項目 `eventSeatId` 為 `null`、帶入對應 `quantity`（對應 Scenario「純計數票種輸入購買數量並成功下單」）
- [x] 5.7 [前端單元測試／Vitest] 同時選擇座位與輸入計數購買數量並送出，斷言下單 API 收到合併後的選購項目陣列，座位項目與計數項目皆存在（對應 Scenario「混合座位選購與純計數購買並成功下單」）
- [x] 5.8 [前端單元測試／Vitest] 已選座位數加計數購買數量達到 `maxTicketsPerOrder`，斷言無法再增加計數輸入或選新座位、顯示限購提示（對應 Scenario「純計數購買數量達到每筆訂單限購張數」）
- [x] 5.9 [前端單元測試／Vitest] 嘗試將計數輸入調整超過該票種當下可售總量，斷言輸入元件限制數值不超過上限、不呈現錯誤狀態（限制型，對應 Scenario「計數輸入元件限制數量不得超過可售總量」）
- [x] 5.10 [前端單元測試／Vitest] mock 送出訂單當下後端因庫存已變動而拒絕（例如回傳驗證錯誤），斷言顯示下單失敗訊息、清空已選座位與計數輸入、重新整理資料（對應 Scenario「送出時因庫存已變動被後端拒絕」）
- [x] 5.11 [前端單元測試／Vitest] mock 某計數票種 `availableQuantity = 0`，斷言該票種輸入元件上限為 0、畫面顯示「已售完」提示、不套用錯誤樣式（對應 Scenario「純計數票種可售總量為 0」）
- [x] 5.12 [前端單元測試／Vitest] 活動未設定限購張數時，計數票種的輸入上限僅受可售總量限制（對應 Scenario「活動未設定限購張數時的純計數購買」）
- [x] 5.13 [前端單元測試／Vitest] 計數票種維持輸入 0、對其他項目正常選購並送出，斷言下單請求不含該數量為 0 的項目（對應 Scenario「計數購買數量為 0 時不送出對應項目」）
- [x] 5.14 [前端單元測試／Vitest] 建構出「送出當下座位數＋計數總量超過限購張數」的情境（例如直接操作元件內部狀態繞過個別輸入元件的即時限制），斷言 `handleSubmit` 在呼叫 API 前擋下並提示（對應 Scenario「送出訂單前偵測到合併總數超過限購張數」）
- [x] 5.15 [前端單元測試／Vitest] 已手動選取座位佔用額度後，斷言計數輸入元件的可輸入上限扣除已選座位數後重新計算（對應 Scenario「已手動選取座位佔用額度後，計數購買輸入上限隨之減少」）
- [x] 5.16 [前端單元測試／Vitest] 區域隨選：全部區域與指定分區皆能隨機抽出對應數量並直接送出訂單成功（對應既有 Scenario「全部區域隨機選位並直接送出訂單」「指定單一分區隨機選位並直接送出訂單」）
- [x] 5.17 [前端單元測試／Vitest] 區域隨選張數超過可售座位數或限購剩餘額度時，斷言顯示錯誤、不加入座位、不呼叫下單 API（對應既有 Scenario「區域隨選張數超過可售座位或限購剩餘額度」）
- [x] 5.18 [前端單元測試／Vitest] 未登入使用者點選區域隨選的「自動選位並送出訂單」按鈕，斷言導向登入頁（帶 `redirect` 查詢參數）、不呼叫任何選位或下單 API（對應既有 Scenario「未登入使用區域隨選」）
- [x] 5.19 [前端單元測試／Vitest] 已輸入計數購買數量時使用區域隨選，斷言可抽選張數扣除已輸入的計數數量，超過扣除後額度時顯示錯誤、不加入座位（對應 Scenario「已輸入純計數購買數量時，區域隨選的剩餘額度隨之減少」）
- [x] 5.20 [前端單元測試／Vitest] mock 混合票種活動（同時有座位制與純計數票種），斷言區域隨選的分區下拉選單只列出座位制分區、不含任何純計數票種名稱，且抽選候選座位集合不含純計數票種（對應 Scenario「純計數票種不會出現在區域隨選的分區選單與抽選池中」）
- [x] 5.21 [前端單元測試／Vitest] 已在計數購票區塊輸入購買數量（尚未送出），接著使用區域隨選送出，斷言下單 API 收到的選購項目同時包含區域隨選抽出的座位與已輸入的計數項目（對應 Scenario「使用區域隨選時一併送出已輸入的計數購買」）

## 6. Spec 同步確認

- [x] 6.1 實作完成後，逐條核對 `specs/admin-web-ui/spec.md`、`specs/buyer-web-ui/spec.md` 兩份 delta 與最終實作行為一致：`RequiresSeat` 開關、可售總量驗證、計數購票區塊、限購張數合併計算（雙向：座位↔計數、區域隨選↔計數）、未登入攔截時機、限制型輸入、統一的下單失敗清空/刷新規則、區域隨選排除計數票種
- [x] 6.2 實作完成後，向使用者確認並更新 `docs/project-scope.md` 第 9 節「Phase 1 Must 盤點快照」：前端 RWD 那列「純計數票種的建立表單／購票 UI 也還沒做」缺口移除，快照日期與備註更新為本次變更；若此為 Phase 1 Must 最後一項缺口，於快照下方註記 Phase 1 全數完成
