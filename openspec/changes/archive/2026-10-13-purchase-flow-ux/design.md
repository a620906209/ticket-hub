## Context

活動詳情頁（`web/src/pages/buyer/EventDetailPage.vue`）是買家的購票主流程。目前的結構：頁面最上方是單一個 `errorMessage` alert；購票欄依序是販售提示、登入提示、實名引導、排隊畫面（或）區域隨選列、各分區座位網格、計數購票、`.summary`（文字摘要＋「送出訂單」）。

UI/UX 審視報告（`docs/ui-ux-review-2026-10-08.md`）第 1.2、1.3、4.3 節指出 U5、U6、U7、U11、A1、A2 六個問題，本 change 一併處理。使用者已在規劃階段（2026-10-10）決定：

| 問題 | 決定 |
| --- | --- |
| U11 自動選位一鍵下單 | 改為只填入座位，由買家按「送出訂單」 |
| A1 鍵盤導航 | 每個分區一個 roving tabindex |
| U6 結帳列 | 所有寬度都 sticky 固定在購票欄底部 |
| U5 錯誤位置 | 操作錯誤顯示在結帳列上方，頁面層級錯誤留在頂端 |

現有限制：
- 「未登入在活動詳情頁點選座位」Scenario 要求：不論座位是否 Available，未登入點選都要導向登入頁。因此不可選座位**不能**使用原生 `disabled`，否則點擊事件不會觸發。
- 座位以 `flex-wrap` 排列，欄數隨寬度變動，沒有固定的列／欄結構。
- jsdom 不計算版面，也沒有實作 `Element.prototype.scrollIntoView`。
- `el-alert` 本身會輸出 `role="alert"`（已確認 `node_modules/element-plus` 2.14.4 的 `alert.vue` 原始碼）。

## Goals / Non-Goals

**Goals:**
- 結帳列（已選內容、總金額、送出按鈕）在所有寬度下都固定在購票欄底部，選位時不用捲到底。
- 選位、區域隨選、送出訂單產生的錯誤，顯示在結帳列上方，也就是操作的位置附近。
- 不可選座位不只靠顏色區分；座位區有圖例。
- 螢幕閱讀器可以讀出每個座位的分區、號碼與狀態；鍵盤使用者每個分區只需按一次 Tab。
- 自動選位只填入座位，買家確認後才送出。

**Non-Goals:**
- 二維方向鍵導航（上下鍵跳到視覺上的上一列／下一列）：座位沒有固定欄數，必須從畫面座標推算，複雜度不符合效益（見決策 5）。
- 販售期間外（`isSalesLocked`）的座位不另外加上 `aria-disabled`：期間外的狀態已由販售提示 alert 告知，點選本來就不會有反應，維持既有行為。
- 手機上放大座位觸控尺寸（R4，P2）、字級與版面寬度規則（V1/V2）、活動卡片（U1）。
- `prefers-reduced-motion`：本 change 不新增任何位移或縮放動畫，所以不需要處理。
- 規格用語：「買家可選位並送出訂單」需求中的排隊相關 Scenario 寫的是「區域隨選快速下單的操作入口」。這段描述的是「停用／開放這個入口」，不受「只填入、不下單」影響；修改它就必須整段複製那個很長的 Requirement，誤改的風險反而更高，所以這次不修改這段文字。主 spec 其他地方（BW-SALES-004／005／008／015 等）仍使用「快速選位」的說法，同樣不在這次修改；因此「快速選位」與「自動選位」兩種名稱會暫時並存，指的是同一個入口。用語對照：「區域隨選」是功能名稱、「快速選位」是主 spec 既有 Scenario 的舊稱，兩者都指活動詳情頁的 `.quick-pick` 區塊；本 change 起該區塊的按鈕文字為「自動選位」，套用後主 spec 中不再有「自動選位並送出訂單」的字樣。
- 舊 Scenario 的處理：OpenSpec 的 MODIFIED 以整個 Requirement 為單位替換（含底下所有 Scenario，見已安裝套件 `dist/core/specs-apply.js` 的 MODIFIED 步驟），所以 delta 不需要也無法逐條標記 Scenario。2026-10-12 以 `buildUpdatedSpec` dry-run 確認套用後舊 Scenario 不殘留。新舊對照：「全部區域隨機選位並直接送出訂單」→ BW-QUICK-001＋003；「指定單一分區隨機選位並直接送出訂單」→ BW-QUICK-002；「區域隨選張數超過可售座位或限購剩餘額度」→ BW-QUICK-004；「未登入使用區域隨選」→ BW-QUICK-005；「已輸入純計數購買數量時，區域隨選的剩餘額度隨之減少」→ BW-QUICK-006；「純計數票種不會出現在區域隨選的分區選單與抽選池中」→ BW-QUICK-007；「使用區域隨選時一併送出已輸入的計數購買」→ BW-QUICK-008（改為兩步驟）。

## Decisions

### 決策 1：錯誤拆成頁面層級與操作層級兩個 state

`errorMessage` 保留，用於頁面層級，仍顯示在頁面頂端，並加上 class `page-error`。另外新增 `actionErrorMessage`，顯示在結帳列內、摘要上方，class 為 `action-error`。兩者都是 `el-alert`，本身就有 `role="alert"`，不需要另外加 `aria-live`。

| 來源 | 歸屬 | 理由 |
| --- | --- | --- |
| `loadData` 載入失敗 | 頁面 | 和操作無關，載入失敗時結帳列可能根本沒有內容 |
| `refreshQueueStatus` 查詢失敗 | 頁面 | 排隊畫面不會顯示結帳列 |
| `handleJoinQueue` 各種失敗（驗證碼、SalesNotOpen、其他） | 頁面 | 加入排隊畫面不會顯示結帳列 |
| 送出時 `403 QueueAdmissionRequired` | 頁面 | 收到後會切回排隊畫面，結帳列隨之隱藏 |
| `toggleSeat` 超過限購、`buildSelection` 分區沒有票種 | 操作 | 由選位觸發 |
| `handleQuickPick` 超過限購、可售座位不足 | 操作 | 由區域隨選觸發 |
| `handleSubmit` 送出前超過限購、429、送出時 `409 SalesNotOpen`、一般下單失敗 | 操作 | 由送出觸發 |

清除時機沿用現有行為：現在 `handleQuickPick`（`EventDetailPage.vue:322`）與 `handleSubmit`（`:513`）開頭會清除唯一的 `errorMessage`，其中也包含頁面層級錯誤。拆分後，這兩個函式在**原本的清除位置**同時清除 `errorMessage` 與 `actionErrorMessage`，前面的提早 return（未登入、非販售中、張數為 0、沒有選擇、送出前超過限購）位置不變；`loadData`（`:459`）與 `handleJoinQueue`（`:403`）開頭只清除 `errorMessage`。如果只清除 `actionErrorMessage`，買家被放行並重新操作後，頂端仍會留著已經過時的頁面層級錯誤，例如「排隊資格已逾時」。這次不新增「選位成功就清除錯誤」之類的規則（Rule 2）。

一般下單失敗會先呼叫 `loadData()`、再設定訊息（既有註解說明的順序）。改成兩個 state 後，`loadData()` 只會清除 `errorMessage`。如果 `loadData` 本身也失敗，頂端和結帳列會各顯示一則訊息，取代原本「下單失敗訊息蓋掉載入失敗訊息」的行為。這是刻意的：兩件事都是真的，分開顯示比較不會誤導。

### 決策 2：結帳列的結構與 sticky 位置

原本的 `.summary` 改為 `.checkout-bar`，放在購票欄的 `v-else` 分支最後面，所以排隊畫面與加入排隊畫面都不會顯示結帳列。內容依序為：

1. `actionErrorMessage` alert（有錯誤時才顯示）；
2. 已選座位清單 `.checkout-seat-list`：顯示格式為 `A-12、A-13`。超過 6 個座位時只顯示前 6 個，後面加上「等 N 個座位」（N 為座位總數）；沒有選座位時不顯示。選位的順序依加入已選清單的先後；
3. 既有摘要文字「已選 X 個座位、Y 張計數票券，總金額 Z」：保留原本的文字（既有測試依賴這段字串），所在元素加上 `aria-live="polite"`，自動選位或點選座位後，螢幕閱讀器會念出新的摘要；
4. 「送出訂單」按鈕。

CSS：`position: sticky; bottom: 0; z-index: 1`，背景使用 `--color-bg-elevated`（不透明），並加上上邊框與 `padding`。sticky 的範圍是購票欄（`section.purchase-column`）。手機單欄時，購票欄在資訊欄下方，只有捲到購票欄的範圍時結帳列才會固定，在資訊欄時不會擋住畫面。`el-select` 的下拉選單會 Teleport 到 body，不受這裡的 `z-index` 影響。

只列出前 6 個座位，是為了讓結帳列在手機上維持約 2 至 3 行高，不吃掉太多座位區的可視高度。完整清單可以從座位網格上已選取的狀態看到。

### 決策 3：區域隨選只填入座位

`handleQuickPick` 拿掉最後的 `await handleSubmit()`，改為同步函式。按鈕文字改為「自動選位」，也不再綁定 `submitting` 的 loading 狀態。抽選規則、「不足時不加入任何座位、不縮減張數」，以及共用的限購剩餘額度計算都不變。

已經在計數購票輸入的數量會留在畫面上，買家按「送出訂單」時，座位與計數項目照既有規則合併在同一次請求送出。原本「使用區域隨選時一併送出已輸入的計數購買」的行為，因此自然改由一般的送出流程涵蓋。

抽選成功後，焦點留在按鈕上，不移到結帳列，避免打斷想再抽一次的買家。新的狀態透過決策 2 的 `aria-live` 摘要告知。

### 決策 4：座位的 `aria-*` 與非色彩區分

- `aria-pressed`：`isSelected(seat)`。
- `aria-label`：`${zoneCode} 區 ${seatNumber} 號，${statusLabel}`。`statusLabel` 依序判斷：已選取為「已選」，`Available` 為「可選」，`Sold` 為「已售出」，`Held` 為「保留中」，其他值為「不可選」。`title` 也改用同一段中文，取代原本的英文狀態。
- `aria-disabled="true"`：`seat.status !== 'Available'` 時加上。不使用原生 `disabled`，理由見 Context 的第一項限制。
- 分區容器 `.seat-grid` 加上 `role="group"`，以及 `aria-label="${zoneCode} 區座位"`。
- 不可選座位（`.seat-btn.sold`；沿用 master 的判定，`.sold` 代表所有非 `Available` 的座位，包含 `Sold` 與 `Held`，所以 `.seat-btn:not(.sold)` 就是 Available 座位）在原本的背景色之外，座位號碼加上 `text-decoration: line-through`，邊框改為 `border-style: dashed`。
- 圖例 `.seat-legend` 放在第一個分區上方，有三個項目「可選／已選／不可選」，每項都有色塊 `.seat-swatch` 加上文字。色塊與座位共用同一組 CSS 規則（`.seat-btn.sold, .seat-swatch--unavailable { … }`），避免兩邊樣式不一致。色塊**不使用** `.seat-btn` class，因為既有測試用 `find('.seat-btn')` 取第一個座位。色塊加上 `aria-hidden="true"`，圖例文字本身已足以說明。
- `.seat-btn:hover` 的邊框高亮改放在 `@media (hover: hover) and (pointer: fine)` 內：觸控裝置點擊後會殘留 hover 狀態，讓座位看起來像被選取（emil-design-eng 規劃建議）。這次不加按壓縮放動畫：座位會用鍵盤操作，鍵盤觸發的操作不應有動畫。這一項是純樣式決策，不列入 AC，也沒有對應的測試。

只使用既有的色彩 token，不新增色值，因此不影響 `web-color-contrast` 的回歸檢查。

### 決策 5：每個分區一個 roving tabindex，一維移動

- `focusedSeatIdByZone`：一個 `reactive<Record<string, string>>`，記錄每個分區最近一次取得焦點或被點擊的座位。
- 每個分區的 Tab 停留點：記錄的座位如果仍在這個分區、而且是 `Available`，就用它；否則用該分區第一個 `Available` 座位。停留點的 `tabindex` 為 `0`，其他座位為 `-1`。分區內沒有可選座位時，整個分區都沒有 Tab 停留點。座位資料重新載入後（例如下單失敗刷新），會自動依這個規則回到第一個可選座位，不需要另外清除記錄。
- 座位的 `@focus` 與 `@click` 會更新記錄。
- 方向鍵在分區容器上處理 `@keydown`：`ArrowRight`／`ArrowDown` 移到下一個 `Available` 座位，`ArrowLeft`／`ArrowUp` 移到上一個，`Home`／`End` 移到第一個／最後一個；到頭或到尾就停住，不循環。只有這 6 個按鍵、而且沒有按住 Alt／Ctrl／Meta 時才處理並呼叫 `preventDefault`，避免攔截 Alt+ArrowLeft（瀏覽器上一頁）這類組合。Enter 與 Space 沿用原生 `<button>` 的行為，Tab 則照常離開分區。焦點停在不可選座位時（滑鼠點選 `tabindex="-1"` 的 Sold／Held 座位仍會取得焦點），沒有可推算方向的目前位置，所以這些按鍵都不處理、焦點不動；使用者可按 Tab 離開後再回到停留點。
- 要移動焦點的元素，用 `data-seat-id` 在事件所在的分區容器裡查找，再呼叫 `focus({ preventScroll: true })` 與 `scrollIntoView({ block: 'nearest' })`。
- 座位加上 `scroll-margin-bottom`，數值為結帳列的估計高度 `160px`。這樣 `scrollIntoView` 會讓焦點座位停在結帳列上方，不被擋住（WCAG 2.4.11）。用 Tab 進入分區時，捲動由瀏覽器處理。Chrome 的焦點捲動是否會套用 `scroll-margin`，我不確定，交給量測確認（見決策 6 與 Risks）。

上下鍵和左右鍵用同樣的一維移動：WAI-ARIA 的 toolbar／radio group 慣例允許這樣做。要做到真正的二維移動，必須用 `getBoundingClientRect` 推算視覺上的列，而列又會隨寬度改變，複雜度太高。不使用 `role="grid"`，因為它要求有 row／gridcell 結構。

### 決策 6：驗證方式

- **行為**（錯誤位置、結帳列內容、自動選位只填入、`aria-*`、roving tabindex、方向鍵）：以 Vitest 測試，寫在 `EventDetailPage.test.ts`，沿用既有的 `mountPage()` 與 mock API。鍵盤測試要用 `attachTo: document.body` 掛載，才能斷言 `document.activeElement`。jsdom 沒有 `scrollIntoView`，在 `beforeEach` 以 `vi.fn()` 補上，並斷言有以 `{ block: 'nearest' }` 呼叫。錯誤位置的斷言方式：訊息必須出現在 `.checkout-bar .action-error` 內，**而且** `.page-error` 不存在；反之亦然。這樣把操作錯誤放錯 state 的實作會失敗。
- **版面**（sticky 是否真的固定在畫面底部、焦點座位是否被擋住、不可選座位的實際樣式、窄螢幕是否溢出）：用腳本化的瀏覽器量測 `measure-purchase-flow.js`，放在 change 目錄。
  - 寬度：以同源 iframe 量測 320、390、720、721、800、1280 px，iframe 高度 700px，使用一個第一個分區至少有 400 個座位（其中至少一個 `Sold`／`Held`、一個 `Available`）、同時有座位制與計數票種、販售中、不需實名、未啟用熱門搶購模式的活動。活動由受版控的 fixture 腳本 `loadtest/fixtures/purchase-flow-ux-event.js` 每輪重新建立（見下方「量測資料」），不依賴 dev DB 既有資料。400 是依 1280px 寬時購票欄每列約 16 個座位推算：座位 36×28px、間距 6px，約 25 列、高度約 840px，大於 700px 的 iframe 高度，才能讓 1280px 的斷言 1、2 成立。量測頁面與 dev server 同源，iframe 共用 localStorage 的登入狀態。登入狀態由使用者每輪以新註冊的買家帳號人工登入（含 CAPTCHA），登出由 UI 執行，兩者都以 DOM 與 localStorage 核對；步驟與判準見 tasks.md 1.1「登入／登出前置」。分兩輪：已登入一輪量全部寬度與斷言 1 至 4（需要結帳列與計數購票列）；未登入一輪只量 320／390／720 的斷言 4，對應以未登入為前提的 BW-RESP-001、002、005。
  - 斷言 1（sticky）：捲到第一個分區頂端時，「送出訂單」按鈕完全在視窗內；而且最後一個座位的 `top` 大於視窗高度，證明頁面確實比視窗長、按鈕是因為 sticky 才看得到。
  - 斷言 2（焦點不被擋住）：先確認第一個分區最後一個 `Available` 座位一開始在視窗下緣之外；在第一個可選座位用 `dispatchEvent` 送出 `End` 鍵後，`document.activeElement` 必須**就是**這個座位，而且它的 `rect.top >= 0`、`rect.bottom` 不超過結帳列的 `rect.top`。焦點如果沒有移動，斷言就失敗，所以沒有實作 `End` 鍵處理時無法通過。
  - 斷言 3（樣式）：不可選座位的 computed `text-decoration-line` 包含 `line-through`、`border-top-style` 為 `dashed`；`.seat-swatch--unavailable` 的這兩個值也相同；`Available` 座位的 `text-decoration-line` 不包含 `line-through`，`border-top-style` 為 `solid`。這樣把樣式誤套到所有座位的實作也會失敗。
  - 斷言 4（溢出）：既有「活動詳情頁在窄螢幕不產生頁面橫向捲動」的 Scenario（BW-RESP-001～005，本 change 補上 ID；各 ID 對應的寬度、元素與子項見 tasks.md 1.1），因為結帳列結構改變，需要重跑：`scrollWidth`、grid 欄寬、單欄時購票欄位於資訊欄下方、活動標題／票價表／計數購票列／結帳列的右緣，以及 320px 時「自動選位」按鈕位於分區選單的下一行。
  - 量測資料：沿用 k6-load-test 的機制，seeder 簽發 Admin 與買家 token，k6 容器只透過 API 建立資料（不寫 SQL）。放在 `loadtest/` 而不是 change 目錄，是因為 k6 容器只掛載 `./loadtest`。腳本建立一個活動：唯一的座位分區 `A` 共 400 席、`A` 區座位制票種、`GA` 計數票種、販售開始於 1 分鐘前、不設販售結束、不需實名、不開熱門搶購模式。之後由第 1 位壓測買家下單 `A` 區第 1 席並付款，產生 `Sold`；再由第 2 位買家下單第 2 席、不付款，產生 `Held`。`Held` 會在持有期限到期後釋出，所以量測只要求 `Sold`／`Held` 至少一個，靠的是永久的 `Sold`。腳本最後以公開 API 核對上述條件，任何一項不符就以非 0 結束，並輸出 `EVENT_ID`。每輪量測前都重跑一次，新活動的標題以 `[PurchaseFlowUx]` 開頭，資料留在 dev DB，不自動清理（產品沒有刪除 API，自動清理只能用原生 SQL）；每輪註冊的 `pfux-*@example.test` 買家帳號也同樣保留。為了不無限累積，fixture 先計算既有的 `[PurchaseFlowUx]` 活動，達到 6 筆就拒絕建立並停止，由使用者依 `loadtest/README.md`「清理」段落手動處理（步驟見 tasks.md 1.0「保留上限」）。計數與建立之間不是原子操作，所以 fixture 只能經由 wrapper `loadtest/fixtures/run-purchase-flow-ux-event.sh` 執行：wrapper 先在 compose 的 redis 上取得互斥鎖（`SET NX EX`），讓兩次 fixture 無法同時通過上限檢查（tasks.md 1.0「並行保護」）。付款觸發的通知信由 `MockEmailNotificationService` 處理，不會對外寄出。
  - 所有座標在比較前都必須是有限數值；元素不存在時一律判定量測無效，不得讓兩個缺失值的比較結果通過。
  - 修改前先量一次基準值：基準狀態下斷言 1、2、3 必須失敗，以證明量測本身能抓到失敗。
  - 用 Tab 進入分區時是否被結帳列擋住（決策 5 的不確定點），改用 claude-in-chrome 送出真實的 Tab 鍵手動檢查並記錄結果。這一項不列入通過條件，只會記錄在 Risks。

## Risks / Trade-offs

- [自動選位多一個步驟] 原本想要「一鍵搶票」的買家必須多按一次「送出訂單」。→ 這是使用者明確選擇的取捨：避免誤下單鎖住座位與限購額度，比少按一次更重要。因為結帳列是 sticky，多出的這一步就在畫面上，不需要捲動。
- [結帳列占用手機可視高度] 約 2 至 3 行，在 700px 高的畫面上大約占 20%。→ 座位清單最多列 6 個，以控制高度；量測時會記錄 390px 下的實際高度。
- [Tab 進入分區時焦點可能被擋住] Chrome 的焦點捲動是否會套用 `scroll-margin`，目前不確定。→ 手動檢查並記錄。如果會被擋住，可以在 `@focus` 裡補呼叫 `scrollIntoView({ block: 'nearest' })`；但這會影響滑鼠點擊時的捲動，所以是否要改，屆時回報使用者決定，不自行加入。**已解除（2026-10-12，tasks.md 4.3(a)）**：Chrome 會套用 `scroll-margin-bottom`，390px 時座位原本在結帳列後方也會捲到結帳列上方（座位底 540 ≤ 結帳列 top 611），1280px 不會被擋；未加入 `@focus` 捲動。
- [`scroll-margin-bottom` 是估計值] 錯誤 alert 出現時結帳列會變高，焦點座位可能被部分遮住。→ WCAG 2.4.11 的要求是焦點元素「不被完全遮住」，部分遮住仍符合 AA。如果需要依實際高度計算，再另外處理。
- [瀏覽器量測沒有回歸保護] → 見下方「AC 測試對應例外」。

## Migration Plan

純前端改動，隨前端部署生效，不需要遷移資料。回滾時 revert 本 change 的 commit 即可。

## 安全確認（CLAUDE.md 安全強制規則）

觸發條件：本 change 不新增外部輸入、DB 操作、驗證／授權邏輯、檔案上傳或外部 API。檢查項目如下：

- **輸入驗證／SQL**：不新增 API 參數。區域隨選的分區與張數輸入沿用既有元件與上限。
- **資料庫**：不涉及。
- **權限**：不新增操作。下單仍由既有的 `POST /api/orders` 在後端驗證身分、排隊資格、販售期間與限購；前端的限購檢查只用來提示。
- **前端 XSS**：分區代碼與座位號碼是主辦方輸入的內容，以 Vue 文字插值與 `:aria-label`／`:title` 屬性綁定呈現，由 Vue 轉義，不使用 `v-html`。
- **Auth Header**：不新增 API 呼叫，既有呼叫經 `web/src/api/httpClient.ts` 統一注入。
- **機敏資訊**：不新增設定、密碼或 API Key。

## AC 測試對應例外

| 項目 | 內容 |
| --- | --- |
| 偏離的規則 | CLAUDE.md「OpenSpec 工作流規則 → Task 執行前」第 2 點：每條 AC 至少對應一項單元測試或整合測試 |
| 適用範圍 | 僅限 BW-FLOW-001（結帳列 sticky 固定位置）、BW-SEAT-A11Y-002 中「不可選座位的實際樣式」的部分、BW-SEAT-A11Y-007（方向鍵移動後焦點座位不被結帳列遮住），以及「活動詳情頁在窄螢幕不產生頁面橫向捲動」既有 Scenario（BW-RESP-001～005）的重新驗證。其餘 AC 一律以 Vitest 驗證，不適用此例外 |
| 替代驗證 | 決策 6 的 `measure-purchase-flow.js` 斷言 1 至 4；修改前先量基準值，基準狀態下斷言 1、2、3 必須失敗（tasks.md 1.2）。量測資料由 tasks.md 1.0 的 fixture 腳本建立 |
| 理由 | jsdom 不計算版面、不套用 sticky 與 media query，也不會算出 computed style 的實際值；只為這幾條需求引入 E2E 框架，不符合最小解法 |
| 剩餘風險 | 沒有回歸保護，之後的修改可能讓 sticky 失效或造成溢出而不被發現 |
| 解除條件 | 專案引入 E2E 框架（例如 Playwright）後，將此量測改寫為自動化測試並刪除本例外 |
| 核准狀態 | **已核准**（使用者於 2026-10-10 spec review 第 4 輪後核准，範圍如上） |
