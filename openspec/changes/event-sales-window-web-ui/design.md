## Context

- 後端 `event-sales-window` 已提供以下內容：
  - 公開 `GET /api/events` 與後台活動列表都附帶 `salesStartAtUtc`／`salesEndAtUtc` 的原始值（可為 null）。
  - 建立活動 API 接受兩個選填欄位。
  - 期間外下單、加入排隊回 409，ProblemDetails `title` 為 `SalesNotOpen`／`SalesClosed`，`detail` 是英文。
- 公開列表**刻意不回傳推導狀態**（快取可能落後最多 30 秒，推導狀態會過期）。前端必須自己用相同規則推導。
- 前端現況：
  - `EventDetailPage.vue` 的 `handleSubmit` 對未特別處理的錯誤一律 `loadData()` 加 `clearSelections()`。
  - `toErrorMessage` 回傳 `ApiError.message`，內容是 detail 或 title。
  - 排隊加入畫面在 `showJoinPrompt` 變為 true 時自動載入驗證碼，驗證碼 TTL 為 120 秒（`Captcha:TtlSeconds`）。
- 使用者已決定（AskUserQuestion）：
  - 預先停用加上到點自動解鎖。
  - 開賣時間顯示靜態文字，不做倒數。
  - 範圍包含買家列表卡片狀態與後台列表販售期間欄。

## Goals / Non-Goals

**Goals:**

- 前端推導的販售狀態與後端判斷規則一致：左閉右開，`SalesEndAtUtc ?? StartAtUtc`。
- 期間外不讓買家做白工：不能選位，按鈕停用，不載入註定逾時的驗證碼。
- 收到 409 時保留買家的選擇，並顯示中文訊息。
- 後台能設定販售期間，並在送出前得到中文的規則提示。

**Non-Goals:**

- 不做倒數計時、不做伺服器時間校正（例如讀 `Date` header）。
- 不改後端 API。
- 不改排隊等待（Waiting）畫面與輪詢行為。停售後仍在等待的買家，被放行後依本機推導停用購票；本機時鐘偏慢者送出訂單時會收到 `SalesClosed` 中文訊息。
- 買家活動列表的狀態標籤只在頁面載入時計算一次，不隨時間自動更新。列表只是導覽用，詳情頁才是操作入口。

## Decisions

### 決策 1：販售狀態推導集中成純函式 `getSalesStatus`

- 新增 `web/src/utils/salesWindow.ts`：

  ```ts
  getSalesStatus(event: { salesStartAtUtc, salesEndAtUtc, startAtUtc }, nowMs: number): 'NotOpen' | 'Open' | 'Closed'
  ```

- 判斷順序依序為：
  - `salesStartAtUtc` 有值且 `now < start`，回傳 `NotOpen`。
  - `now >= (salesEndAtUtc ?? startAtUtc)`，回傳 `Closed`。
  - 其餘回傳 `Open`。
- 另提供 `getEffectiveSalesEndAtUtc(event)`，供畫面顯示停售時間。
- **活動為 null（找不到活動）**：詳情頁的 `salesStatus` computed 在 `event` 為 null 時回傳 `null`，不呼叫 `getSalesStatus`。購票區塊與資訊欄都在既有 `<template v-if="event">` 內，此時不渲染，頁面顯示既有的「找不到這個活動」；`canPurchase`、`showJoinPrompt` 併入 `salesStatus === 'Open'` 後自然為 false。
- **無效日期字串（實作備註，非規範性契約）**：不另做防護，不寫 Scenario、不寫測試。三個欄位都來自後端 `DateTime` 序列化，必為 ISO 8601；前端為此加 fallback 等於替不存在的契約違反設計行為。若真的出現，`getTime()` 為 `NaN`，所有比較皆為 false，結果為 `Open`，仍由後端 409 把關，不會錯賣。
- `salesStartAtUtc`／`salesEndAtUtc` 為 `null` 或 `undefined` 一律視為未設定（`== null`），避免欄位缺漏時被 `new Date(undefined)` 變成 `Invalid Date` 而使比較永遠為 false。
- 理由：
  - 詳情頁、列表頁都要用。
  - 邊界（等於開賣時間為 `Open`、等於停售時間為 `Closed`）用單元測試直接鎖住，不必透過元件測試間接驗證。
- 替代方案：寫在各頁 computed 中。否決，因為規則重複且邊界難測。

### 決策 2：詳情頁以每秒更新的 `now` ref 驅動，切回前景立即重算

- 詳情頁持有 `nowMs = ref(Date.now())`：
  - `setInterval` 每 1 秒更新一次。
  - 監聽 `document` 的 `visibilitychange`，回到 visible 時立即更新。
  - 兩者都在 `onUnmounted` 清除。
- `salesStatus = computed(() => getSalesStatus(event, nowMs))`。
- 理由：
  - 背景分頁的 timer 會被瀏覽器節流（實測過隱藏分頁會延遲，見 order-pending-actions 經驗）。`visibilitychange` 確保買家切回分頁時狀態立即正確。
  - 每秒一次 `Date.now()` 加一個 computed，成本可忽略。
- 替代方案：用 `setTimeout` 排到下一個邊界時間。否決，原因有二：
  - `setTimeout` 延遲超過 2³¹−1 ms（約 24.8 天）會溢位並立即觸發，開賣在一個月後的活動會出錯，需要額外分段處理。
  - 休眠喚醒後 timer 行為不可靠。

### 決策 3：販售狀態併入既有閘門，不另開平行判斷

- `canPurchase` 改為 `既有條件 && salesStatus === 'Open'`。
  - 既有的 `toggleSeat`、計數輸入、`handleQuickPick`、送出按鈕都已依 `canPurchase` 把關，會自動生效。
- `showJoinPrompt` 改為 `既有條件 && salesStatus === 'Open'`。
  - 驗證碼由既有 watch 在 `showJoinPrompt` 變為 true 時載入。因此**未開賣時不載入驗證碼，開賣瞬間才載入**，避免 120 秒 TTL 在買家等待開賣期間逾時，導致第一次送出必定 `CaptchaInvalid`。
- 期間外，購票區塊上方顯示販售狀態提示（決策 5），座位圖仍顯示，供買家瀏覽。
- **視覺上也要表現為不可操作**（emil-design-eng 審視）：只靠 handler 提早 return 會讓元件「看起來能按、按了沒反應」。已登入且 `salesStatus !== 'Open'` 時：
  - 快速選位的區域下拉、數量輸入、「自動選位並送出訂單」按鈕加上 `:disabled`。
  - 座位區容器加上 class（例如 `seat-grid--locked`），座位按鈕改為 `cursor: default`，取消 `.seat-btn:hover` 的邊框變色。
  - 未登入者不套用：點選仍須觸發導向登入頁（見下方「未登入者」）。
- 停售後若畫面正處於排隊加入狀態，加入畫面隱藏、改顯示停售提示。
- **未登入者**：`toggleSeat`、`handleCountChange`、`handleQuickPick` 既有的「未登入先導向登入頁」檢查維持在 `canPurchase` 檢查之前，不調整順序。登入是購票前提，登入後回到本頁即依販售狀態停用；改變順序只會讓未開賣時點座位「沒反應」，對未登入者反而更不清楚。狀態提示對未登入者同樣顯示。
- **Waiting 買家**：`showQueueWaiting` 是購票區塊 v-if 的第一個分支，維持不變。停售後仍顯示等待畫面，放行後依 `canPurchase` 停用（見 Non-Goals，由 BW-SALES-016 鎖住）。
- 替代方案：個別在每個按鈕加條件。否決，容易漏掉 `handleQuickPick` 這類路徑。

### 決策 4：409 `SalesNotOpen`／`SalesClosed` 的處理

- 新增 `isSalesWindowError(error)` 與對應的中文訊息：
  - `SalesNotOpen`：「尚未開賣，請於開賣時間後再試」
  - `SalesClosed`：「本活動已停售」
  - 判斷依據為 `ApiError.status === 409` 且 `problem.title` 相符，與 `CaptchaInvalid`／`RealNameRequired` 的既有作法一致。
- **下單**（`handleSubmit`）：
  - 在泛用錯誤分支之前攔截。
  - 只設定 `errorMessage`，不呼叫 `loadData()`，也不呼叫 `clearSelections()`。後端在檢查販售期間時尚未鎖定任何座位，選擇仍然有效。
- **加入排隊**（`handleJoinQueue`）：
  - 兩者都顯示中文訊息並清空驗證碼輸入。
  - `SalesNotOpen`：換發新的驗證碼。後端先核對驗證碼再檢查販售期間，驗證碼已被消耗，而加入畫面仍顯示。
  - `SalesClosed`：**不換發**。`isSalesClosedByServer` 使 `showJoinPrompt` 變為 false、加入畫面隱藏，換發的驗證碼不會有人使用，也違反「非販售中不載入驗證碼」。既有 catch 分支末尾一律 `void refreshCaptcha()`，因此 `SalesClosed` 必須在該分支之前 return。
- **伺服器判定優先**：
  - 收到 `SalesClosed` 時設定 `isSalesClosedByServer = true`，`salesStatus` 一律視為 `Closed`。停售不可逆，買家時鐘偏慢時也能立即停用。
  - `isSalesClosedByServer` 只在元件建立時初始化為 false，`loadData()` **不重置**，與既有 `isRealNameRejectedByServer` 的作法一致。重新載入只會拿到相同的原始時間，重置後偏慢的本機時鐘又會推導為 `Open`，讓買家再撞一次 409。
  - 這是實作約束而非可觀察行為：收到 `SalesClosed` 後送出按鈕停用、快速選位提早 return，UI 已無路徑再觸發 `loadData()`（只由 `onMounted` 與下單泛用錯誤分支呼叫），因此不寫進 spec、不另寫測試。
- **與主 spec 的關係**：主 spec「買家可選位並送出訂單」規定除 401 外的下單失敗一律清空並重新載入。本 change 以 MODIFIED 改寫該 Requirement，把 `409 SalesNotOpen`／`SalesClosed` 與既有的 403、429 一起列為明列例外，避免 archive 後主 spec 同時存在兩條互斥規則。
  - 收到 `SalesNotOpen` 時不覆寫狀態。開賣是會到來的事件，若鎖住，時鐘偏快的買家就無法在真正開賣後重試。

### 決策 5：顯示格式

- **販售狀態標籤元件**：新增 `web/src/components/SalesStatusTag.vue`，比照既有 `OrderStatusTag.vue`（`el-tag` 加 `disable-transitions`），文字為尚未開賣／販售中／已停售，類型分別為 `info`／`success`／`info`。詳情頁與列表卡片共用，狀態以文字表達，顏色只是輔助。
- **詳情頁資訊欄**，在活動開始時間下方顯示：
  - 「販售期間：{開賣時間 或「即日起」} ～ {實際停售時間}」
  - `SalesStatusTag`。
- **購票區塊提示**（`el-alert`，`closable=false`），位置在購票區塊最上方，**排在既有「請先登入」提示之前**（未開賣時登入也買不了，販售狀態才是主要訊息）。呈現條件以 spec「購票區塊販售提示」表格為準：
  - 「請先登入」提示位於排隊等待／加入排隊／一般購票的 v-if 鏈之外，販售提示同樣放在鏈外，但條件加上 `!showQueueWaiting`，確保排隊等待中不顯示（BW-SALES-016）。
  - 以一個 `salesPromptKind` computed（`'NotOpen' | 'OpenedDuringStay' | 'Closed' | null`）驅動單一 `el-alert` 的 `type` 與 `title`，三種內容共用同一個 DOM 節點，切換時只更新內容。
  - `OpenedDuringStay` 由「本頁曾推導為 `NotOpen`」的旗標決定，熱門搶購活動也適用；`Closed` 優先於它。
  - 未開賣：`type="info"`，「尚未開賣，將於 {開賣時間} 開放購票」
  - 已停售：`type="info"`，「本活動已停售」
  - **開賣後保留位置**：本頁停留期間由「尚未開賣」轉為「販售中」時，提示不移除，改為 `type="success"`「現在開放購票」，持續顯示到離開本頁或轉為已停售。熱門搶購活動同樣顯示（使用者決定：所有活動規則一致）：開賣時下方內容整塊換成加入排隊畫面，提示本身擋不住這次版面變化，但保留它讓買家知道畫面為何改變。排隊等待中依表格不顯示，放行後重新出現（BW-SALES-019）。理由：若整塊提示消失，下方座位與按鈕會在開賣那一刻上移，正好是買家集中點擊的時刻，容易誤點。載入時已是「販售中」的活動不顯示此提示，避免一般情況多一條雜訊。
- **買家列表卡片**：開始時間下方放 `SalesStatusTag`。
- **後台列表**：新增「販售期間」欄，值為「{開賣 或「未設定」} ～ {停售 或「未設定（活動開始時停售）」}」，原值照實呈現，不展開 null。
- 時間一律 `new Date(x).toLocaleString()`，與既有開始時間顯示一致。
- **動態效果**：狀態切換只改變文字與按鈕停用狀態，不加位移動畫。
  - 這是罕見的一次性狀態變化，Element Plus 按鈕既有的顏色 transition 就足夠。
  - 不需要為此加入新的 transition（依 emil-design-eng：沒有明確目的就不動畫）。

### 決策 6：後台建立表單

- 新增兩個 `el-date-picker type="datetime"`：開賣時間、停售時間，皆選填並附說明文字。
  - 開賣時間說明：「留空代表建立後立即開賣」。
  - 停售時間說明：「留空代表活動開始時停售」。
  - 兩者共同說明：「建立後不可變更」。
- 前端驗證（自訂 validator，讀整個表單）：
  - 停售時間有值時，不得晚於開始時間：「停售時間不可晚於活動開始時間」。
  - 開賣時間有值時，必須早於（停售時間 ?? 開始時間）：「開賣時間須早於停售時間（未填停售時間時為活動開始時間）」。
  - 開始時間或停售時間變更時，重新驗證相關欄位（`validateField`），避免舊錯誤訊息殘留。
- 送出：有值的欄位以 `new Date(x).toISOString()` 傳送。這樣產生的字串帶 `Z`、精度為毫秒，符合後端 UTC 與整微秒規則。空值不傳（`undefined`）。
- **型別**：`el-date-picker` 未設 `value-format` 時 v-model 實際為 `Date`（清空時為 `null`），既有 `startAt` 宣告為 `string` 與實際不符。新增的兩個欄位宣告為 `Date | string | null`；validator 一律以 `new Date(x).getTime()` 比較，不直接比較物件或字串。
- **空值判斷**：`el-date-picker` 選了再清空時 v-model 為 `null`（初始值為空字串）。送出與 validator 一律以 truthy 判斷有無值，`null`、`''` 都視為未填；不得對 `null` 呼叫 `new Date()`，否則會變成 1970-01-01 並被送出。
- `createEvent` 參數已有 8 個，再加 2 個超過 CLAUDE.md 的 3 參數警戒線。本次改為接受 `CreateEventInput` 物件。
  - 只有 `EventCreatePage` 一個呼叫端，影響可控。
  - 既有測試對 `createEvent` 呼叫參數的斷言需同步改為物件形式。

## 安全確認（CLAUDE.md 觸發：外部輸入、前端 API 呼叫）

- **輸入驗證在哪一層**：
  - 前端 el-form rules 只做提前提示：停售不晚於開始、開賣早於實際停售。
  - 後端 `CreateEventRequestValidator`（FluentValidation，Application 層）是最終驗證，另外負責 UTC、整微秒、非 `MinValue`。這三項前端以 `toISOString()` 產生，天然滿足，因此不重複實作。
  - 前端檢查被繞過時，由後端回 400。
- **SQL／shell 拼接**：無。純前端變更，後端沿用既有 EF Core 參數化路徑，本次不改。
- **N+1 查詢風險**：本次不引入。
  - 不新增資料庫查詢，也不修改後端查詢或 `Include` 關係。
  - 前端只呼叫既有的公開活動列表 `GET /api/events` 與後台活動列表 API，販售期間欄位隨既有 DTO 一次回傳；詳情頁沿用既有 `loadData()` 的 `Promise.all`（活動列表、座位、票種各一次）。
  - 不新增逐筆活動或逐筆票種的 API 呼叫。後台活動列表既有的逐筆 `getTicketTypes(eventId)` 只在使用者展開該列時觸發且有快取（`EventListPage.vue` 的 `loadTicketTypesIfNeeded`），本次新增的「販售期間」欄不依賴它。
  - 後端既有查詢的 N+1 狀態不在本 change 範圍內，由 `event-sales-window` 後端 change 的測試與 review 負責。
- **權限**：
  - 建立活動頁路由 `meta.requiresOrganizerContext`，由前端 router guard 攔截。這只是體驗，不是安全邊界。
  - 真正的邊界是後端 `AdminEventsController` 的 `[Authorize(Policy = RequireOrganizerContext)]`，未切換主辦方者呼叫會得到 403。
  - 本次不改任何權限設定。
- **XSS**：新增的時間、狀態文字一律以 mustache 插值或 Element Plus 元件 prop 渲染，不使用 `v-html`。時間字串來自 API，經 `toLocaleString()` 格式化後才顯示。
- **Auth Header**：`createEvent` 沿用既有 `authorizedRequest`，由統一的 httpClient 帶入 Bearer token。公開的 `getEvents` 不需登入，不受影響。

## Risks / Trade-offs

- [買家時鐘偏慢，開賣後仍顯示未開賣] → 已知限制，已在 proposal 說明。偏差通常為秒級；嚴重偏差（分鐘級）屬使用者環境問題。後端是最終把關，不會錯賣。
- [買家時鐘偏快，提前送出] → 收到 `SalesNotOpen` 中文訊息，選擇保留，到點可重試。加入排隊會消耗一次驗證碼，已自動換發。
- [列表快取最多落後 30 秒] → 販售期間建立後不可變更，快取中的原始時間不會過期。只有「新建立的活動暫時不出現」，與既有行為相同。
- [開賣瞬間驗證碼請求同步化] → 停留在頁面等待的買家會在同一秒解鎖並同時呼叫 `GET /api/captcha`。`captcha` rate limit 依 IP 分區（`Program.cs` `CreateCaptchaPartition`），一般情況不互相影響；但同一 NAT 後方（公司、校園）的買家共用 IP，可能收到 429。`useCaptcha.refresh` 失敗只設定 `loadError`、不自動重試，且載入驗證碼的 watch 要求 `!captchaLoadError`，買家需手動按「換一張」。本次接受此風險、不加隨機延遲：受影響範圍限於共用 IP 的買家，且有既有按鈕可恢復。於 7.2 瀏覽器實測時觀察。
- [`setInterval` 每秒觸發 computed 重算] → 只有 `salesStatus` 依賴 `nowMs`，下游 computed 只有在結果改變時才觸發畫面更新，成本可忽略。
- [既有活動 `salesStartAtUtc`／`salesEndAtUtc` 皆為 null] → 規則自然退化為「活動開始即停售」，與後端一致。已開始的舊活動會顯示「已停售」，這是後端 change 已接受的業務行為變更。

## Migration Plan

純前端變更，沒有遷移。部署順序：後端 `event-sales-window` 必須先上線（已合併）。前端回滾只需回到前一版，後端欄位對舊前端無影響（舊前端忽略多出的欄位）。

## Open Questions

（無）
