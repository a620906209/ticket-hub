> 所有指令在容器內執行（`docker compose exec web npm run test` 等），不在本機執行 node／npm。
> 測試命名沿用各檔既有慣例，並在測試名稱前綴 Scenario ID（例如 `[BW-SALES-004]`）以利追溯。
>
> **測試種類**：本 change 只改前端，測試框架為 Vitest（前端既有慣例，對應 CLAUDE.md「測試規範」的 xUnit）。以下全部屬於 CLAUDE.md 定義的**單元測試**層級：API 以 mock 隔離，不碰 DB 與網路。
>
> | 任務 | 測試種類 | 隔離方式 |
> | --- | --- | --- |
> | 1.2 | Vitest API 層單元測試（修改既有 `admin.test.ts`） | `vi.stubGlobal('fetch')` |
> | 2.2 | Vitest 純函式單元測試 | 無外部依賴，以參數傳入 `nowMs` |
> | 3.1、3.7 至 3.14（含 3.13a／b／c） | Vitest Vue 元件測試（`EventDetailPage`，jsdom） | mock API 模組；時間以 `vi.useFakeTimers`／`vi.setSystemTime` 控制 |
> | 4.2 | Vitest Vue 元件測試（買家 `EventListPage`） | mock API 模組；`vi.setSystemTime` |
> | 5.2 至 5.6 | Vitest Vue 元件測試（後台 `EventCreatePage`） | mock `createEvent` |
> | 6.2 | Vitest Vue 元件測試（後台 `EventListPage`） | mock API 模組 |
>
> **不新增整合測試**：本 change 不改後端，409 `SalesNotOpen`／`SalesClosed` 的真實 API 邊界已由 `event-sales-window` 的後端整合測試涵蓋（`tests/ProjectC.WebApi.Tests/Orders/OrdersControllerTests.cs`、`Events/EventQueueControllerTests.cs`）；前後端串接改由 7.2 瀏覽器實測驗證。

## 1. 型別與 API 層

- [x] 1.1 `web/src/types/apiResponses.ts`：`EventSummary`、`AdminEventSummary` 補上 `salesStartAtUtc: string | null`、`salesEndAtUtc: string | null`
- [x] 1.2 `web/src/api/admin.ts`：`createEvent` 改為接受 `CreateEventInput` 物件（含既有 8 個欄位與 `salesStartAtUtc?`、`salesEndAtUtc?`），未提供的欄位不放進 body（design 決策 6）；同步修改 `web/src/api/admin.test.ts` 以位置參數呼叫 `createEvent` 的既有測試（AWU-EVENT-RN-001／002），改為物件形式並確認仍通過
- [x] 1.3 重新產生 `api.generated.ts`（`docker compose exec web npm run generate:api-types`），確認 `CreateEventRequest` 含兩個販售欄位；diff 若有與本次無關的變動，回報後再決定是否保留
  - 2026-10-05：含約 800 行與本次無關的既有漂移，使用者決定整份保留

## 2. 販售狀態推導（純函式；測試為 Vitest 純函式單元測試）

- [x] 2.1 新增 `web/src/utils/salesWindow.ts`：`getSalesStatus(event, nowMs)`、`getEffectiveSalesEndAtUtc(event)`（design 決策 1）
- [x] 2.2 新增 `salesWindow.test.ts`，測試 BW-SALES-001（每個測試名稱前綴 `[BW-SALES-001]`）：
  - 早於開賣（NotOpen）
  - 左閉右開邊界：
    - 等於開賣時間為 Open
    - `salesEndAtUtc` 為 null 時，等於 `startAtUtc` 為 Closed
    - `salesEndAtUtc` 有值（早於 `startAtUtc`）時，等於 `salesEndAtUtc` 為 Closed
  - 開賣為 null 且未到停售（Open）
  - null／undefined 等價（四組各自獨立的測試，每組以同一個 `nowMs` 比對 null 與 undefined 兩次呼叫的結果，並斷言預期值）。`salesEndAtUtc` 那組的 `nowMs` 刻意設為等於 `startAtUtc`：若實作只用 `!== null` 判斷而誤用 `new Date(undefined)`，比較會因 `NaN` 恆為 false 而回傳 Open，測試失敗。`salesStartAtUtc` 為 `undefined` 時，`NaN` 的比較結果碰巧與 null 相同，因此該組只鎖定契約，無法偵測這類錯誤：
    - `salesStartAtUtc` 為 null 與 undefined 時，`getSalesStatus` 結果相同（`nowMs` 早於 `startAtUtc`，預期 Open）
    - `salesEndAtUtc` 為 null 與 undefined 時，`getSalesStatus` 結果相同（`salesStartAtUtc` 固定為 null，避免先命中 NotOpen 而遮蔽錯誤；`nowMs` 等於 `startAtUtc`，預期 Closed）
    - `salesStartAtUtc` 為 null 與 undefined 時，`getEffectiveSalesEndAtUtc` 結果相同
    - `salesEndAtUtc` 為 null 與 undefined 時，`getEffectiveSalesEndAtUtc` 結果相同（皆為 `startAtUtc`）

  測試須在「把 `<` 改成 `<=`」或「忽略 `salesEndAtUtc`」時失敗。

## 3. 買家活動詳情頁（測試為 Vitest Vue 元件測試）

- [x] 3.1 既有 `EventDetailPage.test.ts` 的活動 fixture（`startAtUtc: '2026-12-31T20:00:00Z'`）改為相對於當下時間的未來時間（或固定遠未來），並補上 `salesStartAtUtc: null`、`salesEndAtUtc: null` 以符合新型別，避免日期過了之後既有測試全部變成「已停售」而失敗；確認既有測試在修改後仍全數通過
- [x] 3.2 加入 `nowMs` ref：`setInterval` 1 秒，`visibilitychange` 回到 visible 時立即更新，`onUnmounted` 清除兩者；加入 `salesStatus` computed 與 `isSalesClosedByServer`（design 決策 2、4）
- [x] 3.3 `canPurchase`、`showJoinPrompt` 併入 `salesStatus === 'Open'`（design 決策 3）；確認 `toggleSeat`、計數輸入、`handleQuickPick`、送出按鈕都經由 `canPurchase` 把關，計數輸入元件若未依 `canPurchase` 停用則補上；已登入且非販售中時，快速選位的區域下拉、數量輸入、按鈕加 `:disabled`，座位區加 locked class（`cursor: default`、無 hover 邊框），未登入者不套用（design 決策 3「視覺上也要表現為不可操作」）
- [x] 3.4 新增 `web/src/components/SalesStatusTag.vue`（比照 `OrderStatusTag.vue`）；資訊欄顯示販售期間與 `SalesStatusTag`；購票區塊最上方（「請先登入」之前）顯示狀態提示，停留期間由未開賣轉為販售中時原位改為「現在開放購票」（design 決策 5）
- [x] 3.5 `handleSubmit` 在泛用錯誤分支前攔截 409 `SalesNotOpen`／`SalesClosed`：顯示中文訊息，不 `loadData()`、不 `clearSelections()`；`SalesClosed` 設定 `isSalesClosedByServer`
- [x] 3.6 `handleJoinQueue` 攔截同樣的 409，皆顯示中文訊息並清空驗證碼輸入：`SalesNotOpen` 換發驗證碼；`SalesClosed` 設定 `isSalesClosedByServer` 後，在既有 catch 末尾 `void refreshCaptcha()` 之前 return，不換發（design 決策 4）
- [x] 3.7 測試 BW-SALES-002、BW-SALES-003：
  - 002：資訊欄出現 fixture 開賣時間與停售時間格式化後的文字，`SalesStatusTag` 為「販售中」
  - 003：販售期間顯示「即日起」與活動開始時間格式化後的文字，狀態為「販售中」；選位後送出，下單 API 被呼叫一次
- [x] 3.8 測試 BW-SALES-004、BW-SALES-005，兩種狀態**各自**執行下列全部斷言（不得只在其中一種狀態測計數輸入與快速選位）：
  - BW-SALES-004 尚未開賣：
    - 座位按鈕仍渲染（座位圖仍顯示），座位區帶 `seat-grid--locked` class
    - 點座位不選取
    - 計數輸入停用
    - 快速選位不呼叫下單 API
    - 送出按鈕 disabled
    - 快速選位的區域下拉、數量輸入、按鈕皆 disabled
    - 顯示「尚未開賣，將於 {開賣時間} 開放購票」
  - BW-SALES-005 已停售：
    - 座位按鈕仍渲染（座位圖仍顯示），座位區帶 `seat-grid--locked` class
    - 點座位不選取
    - 計數輸入停用
    - 快速選位不呼叫下單 API
    - 送出按鈕 disabled
    - 快速選位的區域下拉、數量輸入、按鈕皆 disabled
    - 顯示「本活動已停售」
- [x] 3.9 測試 BW-SALES-006：熱門搶購模式、未開賣時不顯示加入排隊畫面，且驗證碼 API **未被呼叫**，購票區塊改顯示尚未開賣提示
- [x] 3.10 測試 BW-SALES-007、BW-SALES-008、BW-SALES-017、BW-SALES-018、BW-SALES-019：以 `vi.useFakeTimers()`＋`vi.setSystemTime` 推進時間跨過邊界並推進 1 秒 interval。每個情境都須在**同一次跨越後**同時斷言標籤、提示與控制項，確認 `nowMs` 更新後整個畫面同步，而非只有閘門 computed 生效（初始載入即為該狀態的 BW-SALES-004／005 不能取代本項）：
  - BW-SALES-007（跨過開賣）：
    - `SalesStatusTag` 由「尚未開賣」變為「販售中」
    - 計數輸入與快速選位的區域下拉、數量輸入、按鈕恢復可用
    - 手動點選一個座位後，該座位呈選取狀態；點擊「送出訂單」，`placeOrder` 被呼叫一次，且送出的選購項目包含剛才點選的座位（快速選位會自行呼叫下單 API，不能取代這條手動選位後送出的路徑）
    - 熱門搶購活動：跨越前驗證碼 API 呼叫 0 次，跨越後才呼叫並顯示加入排隊畫面
  - BW-SALES-017（同一次跨過開賣）：
    - 「尚未開賣」提示原位改為「現在開放購票」——斷言跨越前後取得的提示 DOM 元素為**同一個節點**（同一 element reference），且仍是購票區塊中 `h2` 之後的第一個元素（jsdom 無法量位移，以 DOM 節點與順序代替）
    - 未登入者同樣顯示「現在開放購票」，且在此未登入設定下斷言該提示排在「請先登入」提示之前
    - 熱門搶購活動跨過開賣後同樣顯示「現在開放購票」，位於加入排隊畫面（含「請先加入排隊」警告）上方
    - 另一個載入時即為販售中的活動不顯示「現在開放購票」
  - BW-SALES-018（同一頁依序跨過開賣、停售）：未開啟熱門搶購模式的活動提示依序為「尚未開賣」→「現在開放購票」→「本活動已停售」，`SalesStatusTag` 同步變化，停售後送出訂單按鈕 disabled
  - BW-SALES-019（熱門搶購，跨過開賣後排隊）：已登入，跨過開賣後確認顯示「現在開放購票」；加入排隊、排隊狀態 API 回 `Waiting`，斷言顯示等待畫面且**不**顯示「現在開放購票」；之後排隊狀態 API 回 `Admitted` 並推進至少 `QUEUE_POLL_INTERVAL_MS`，斷言「現在開放購票」重新出現且為購票區塊中 `h2` 之後的第一個元素；手動點選座位後送出，`placeOrder` 被呼叫一次（若 `OpenedDuringStay` 旗標在 Waiting 期間被清除，放行後的斷言須失敗）
  - BW-SALES-008（跨過停售）：
    - `SalesStatusTag` 由「販售中」變為「已停售」
    - 購票區塊顯示「本活動已停售」
    - 送出訂單按鈕 disabled
    - 計數輸入與快速選位的區域下拉、數量輸入、按鈕 disabled
    - 跨越前已選的座位不會因此送出：點擊快速選位與送出訂單後，下單 API 呼叫 0 次
- [x] 3.11 測試 BW-SALES-009：設定系統時間跨過開賣但**不**推進 interval，觸發 `visibilitychange`，斷言立即解鎖：`SalesStatusTag` 顯示「販售中」，計數輸入與快速選位的區域下拉、數量輸入、按鈕不再 disabled，點座位可選取。jsdom 的 `document.visibilityState` 是唯讀 getter，須以 `Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })` 覆寫後再 `document.dispatchEvent(new Event('visibilitychange'))`，並在 `afterEach` 還原（不能只在測試本體結尾還原，否則斷言失敗時會污染同檔其他測試）；handler 須檢查 `visibilityState === 'visible'` 才更新
- [x] 3.12 測試 BW-SALES-010、BW-SALES-011。兩個 Scenario **各自獨立**建立情境（先手動選至少一個座位，並把至少一個計數票種數量設為大於 0，再送出），各自執行全部斷言，不得由其中一個個案代替另一個：
  - BW-SALES-010（下單回 409 `SalesNotOpen`）：
    - 顯示「尚未開賣，請於開賣時間後再試」，不出現英文 detail
    - 已選座位仍為選取狀態
    - 計數購買數量保留原值
    - 活動、座位、票種查詢 API **未被再次呼叫**（未執行 `loadData()`）
    - `SalesStatusTag` 仍為「販售中」
    - 送出按鈕**仍可用**，再次送出時下單 API 被再次呼叫（鎖住「不覆寫推導結果」；若誤設 `isSalesClosedByServer` 此斷言須失敗）
    - 另以**快速選位**路徑送出（`handleQuickPick` 先加入座位再送出）收到 `SalesNotOpen`，斷言抽出的座位保留
  - BW-SALES-011（下單回 409 `SalesClosed`）：
    - 頁面頂部錯誤 alert 與購票區塊販售提示**各自**含「本活動已停售」（分別以各自容器查詢，不用單一 `getByText`，避免找到兩個元素或只驗到其一）；不出現英文 detail
    - 已選座位仍為選取狀態
    - 計數購買數量保留原值
    - 活動、座位、票種查詢 API **未被再次呼叫**（未執行 `loadData()`）
    - `SalesStatusTag` 變為「已停售」
    - 送出按鈕 disabled，計數輸入與快速選位的區域下拉、數量輸入、按鈕 disabled
    - 另以**快速選位**路徑送出收到 `SalesClosed`，斷言抽出的座位保留
- [x] 3.13 測試 BW-SALES-012：加入排隊回 `SalesNotOpen`，斷言中文訊息、驗證碼輸入清空、驗證碼 API 被再次呼叫、加入畫面仍在、不顯示等待畫面
- [x] 3.13a 測試 BW-SALES-014：加入排隊回 `SalesClosed`，斷言頁面頂部錯誤 alert 與購票區塊販售提示各自含「本活動已停售」、狀態為已停售、加入畫面隱藏、**錯誤回應後驗證碼 API 呼叫次數不再增加**、不顯示等待畫面
- [x] 3.13b 測試 BW-SALES-015，未登入下「尚未開賣」與「已停售」兩種狀態各自執行：
  - 顯示對應販售提示，且排在「請先登入」提示之前
  - 計數輸入與快速選位的區域下拉、數量輸入、按鈕**不是** disabled，座位區**不帶** `seat-grid--locked` class（若視覺停用誤套用到未登入者，此斷言須失敗）
  - 點座位、調整計數數量、按下快速選位，三者各自導向 `/login` 並帶 `redirect` 指回本頁
  - 下單 API 呼叫 0 次
- [x] 3.13c 測試 BW-SALES-016：排隊狀態 API 回 `Waiting`，`vi.setSystemTime` 跨過停售時間並推進 1 秒 interval，斷言仍顯示等待畫面、不顯示「本活動已停售」；之後排隊狀態 API 回 `Admitted` 並推進至少 `QUEUE_POLL_INTERVAL_MS`（5 秒，輪詢是 `setTimeout` 而非 1 秒 interval），斷言顯示「本活動已停售」且送出按鈕 disabled
- [x] 3.14 測試卸載：spy `window.clearInterval` 與 `document.removeEventListener`，斷言卸載時以掛載時取得的 interval id 呼叫 `clearInterval`，並以同一個 handler 移除 `visibilitychange` 監聽。不使用 `vi.getTimerCount()` 歸零斷言，因為既有排隊輪詢的 `setTimeout`（`EventDetailPage.vue` 第 290–299 行）可能干擾計數

## 4. 買家活動列表（測試為 Vitest Vue 元件測試）

- [x] 4.1 `EventListPage.vue`（buyer）卡片以 `SalesStatusTag` 顯示販售狀態
- [x] 4.2 新增 buyer `EventListPage.test.ts`，測試 BW-SALES-013（三種狀態各一筆，以 `vi.setSystemTime` 固定當下時間）

## 5. 後台建立活動表單（測試為 Vitest Vue 元件測試）

- [x] 5.1 `EventCreatePage.vue` 新增開賣／停售時間欄位與說明文字；兩個欄位型別宣告為 `Date | string | null`（design 決策 6「型別」）；新增兩個跨欄位 validator，以 `new Date(x).getTime()` 比較；開始時間、停售時間變更時重新驗證相關欄位；送出時以 `CreateEventInput` 呼叫，有值者 `toISOString()`（design 決策 6）
- [x] 5.2 既有 `EventCreatePage.test.ts` 中對 `createEvent` 參數的斷言（含 AWU-EVENT-RN-001／002）同步改為物件形式，確認仍通過
- [x] 5.3 測試 AWU-EVENT-SALES-001、002：請求內容的 `salesStartAtUtc`／`salesEndAtUtc` 為帶 `Z` 字串；未填時為 `undefined`；另含「選了開賣時間後清空（v-model 為 `null`）」送出仍為 `undefined`、不是 1970 時間（design 決策 6 空值判斷）
- [x] 5.4 測試 AWU-EVENT-SALES-003、004、005，下列每種非法輸入各自一個測試案例，皆斷言顯示對應錯誤訊息，且 `createEvent` **未被呼叫**：
  - AWU-EVENT-SALES-003：停售時間晚於活動開始時間
  - AWU-EVENT-SALES-004：開賣時間等於停售時間；開賣時間晚於停售時間
  - AWU-EVENT-SALES-005：未填停售時間時，開賣時間等於活動開始時間；開賣時間晚於活動開始時間
- [x] 5.5 測試 AWU-EVENT-SALES-006、009：
  - 006：先觸發停售時間錯誤，修改開始時間後錯誤訊息消失並可送出
  - 009：先觸發開賣時間錯誤，修改停售時間後開賣時間錯誤訊息消失並可送出（若停售時間變更時未重新驗證開賣時間欄位，此測試須失敗）
- [x] 5.6 測試 AWU-EVENT-SALES-007：欄位預設為空，說明文字存在

## 6. 後台活動列表（測試為 Vitest Vue 元件測試）

- [x] 6.1 admin `EventListPage.vue` 新增「販售期間」欄（design 決策 5）
- [x] 6.2 既有 admin `EventListPage.test.ts` fixture 補上兩個欄位；新增測試 AWU-EVENT-SALES-008

## 7. 驗證與收尾

- [x] 7.1 `docker compose exec web npm run test` 全數通過；`npm run build`（含 `vue-tsc` 型別檢查）通過
- [x] 7.2 瀏覽器實測（claude-in-chrome，https://localhost:5173），以後台建立以下活動並逐一驗證買家詳情頁：
  - 開賣時間在 2 分鐘後的一般活動：驗證未開賣提示，停留到開賣後自動解鎖並成功下單
  - 開賣時間在 2 分鐘後的熱門搶購活動：驗證開賣前無驗證碼，開賣後出現驗證碼並可加入排隊
  - 已停售的活動：無法透過 API 建立過去的停售時間；以停售時間在約 1 分鐘後的活動等待停售。驗證停售後按鈕停用；停售前先選位、停售後以 DevTools 解除停用強行送出，驗證收到中文訊息且選擇保留
  - 後台表單的錯誤提示與列表欄位顯示正確
  - 期間外（已登入）座位按鈕游標為預設、滑過不變色；未登入者座位仍可點並導向登入（`seat-grid--locked` class 的套用條件已由 3.8、3.13b 自動化測試；jsdom 不計算 CSS，游標與 hover 的實際呈現於此觀察）
  - 開賣瞬間驗證碼是否成功載入；若出現 429（`loadError`），記錄情況並確認「換一張」可恢復（design Risks「開賣瞬間驗證碼請求同步化」）
  - 2026-10-05 實測結果：以上皆通過；開賣瞬間驗證碼正常載入，未遇到 429。期間外滑過不變色以 CSS 規則核對（`.seat-grid--locked .seat-btn:not(.selected):hover` 覆寫 `.seat-btn:hover`），未以滑鼠實際觀察。
  - 實測發現：收到 `SalesClosed` 時「本活動已停售」在錯誤提示與販售提示各出現一次 → 改為只由販售提示顯示（同步更新 design 決策 4 與 BW-SALES-011／014 測試）
- [x] 7.3 呼叫 strict-reviewer，依 CLAUDE.md 的審查流程修正至 PASS（本次無 Application／Repository 層變更，不套用 hardener）
- [x] 7.4 spec 同步（依序執行，完成後告知同步狀態）：
  - **歸檔前**：再次核對 `openspec/specs/event-management`、`ticket-purchase`、`purchase-queue` 主 spec 中的販售期間描述（左閉右開區間、409 `SalesNotOpen`／`SalesClosed` 的 title、加入排隊先驗驗證碼再檢查販售期間），確認與本次 delta 無衝突；有衝突則先停下回報，不歸檔
  - **歸檔時**：
    - `specs/buyer-web-ui/spec.md` 的 ADDED Requirement 合併至 `openspec/specs/buyer-web-ui/spec.md`
    - `specs/admin-web-ui/spec.md` 的 ADDED Requirement 合併至 `openspec/specs/admin-web-ui/spec.md`
    - 以 MODIFIED 版本整段取代 `buyer-web-ui`「買家可選位並送出訂單」，保留 409 `SalesNotOpen`／`SalesClosed` 的例外處理
  - **歸檔後**：以 diff 比對 MODIFIED 取代前後，確認只有 4 處已知差異（例外清單、「除上述明列例外外」前綴、括號句、新增 409 段），且主 spec 不再有「除 401 外一律清空」與 409 保留選擇並存的矛盾
