# Design

## Context

- 買家頁 `EventDetailPage.vue` 的所有限購行為，都從 `maxTicketsPerOrder` computed（`event.maxTicketsPerOrder ?? null`）與 `remainingCapacity` computed（null 時為 `Infinity`）推導，包括以下五處：
  - 限購提示
  - 點選座位阻擋
  - 區域隨選
  - 計數輸入上限（`countMaxFor`）
  - 送出前加總檢查（`isWithinOrderLimit`）
- 後台頁 `EventCreatePage.vue` 的限購欄位是 Element Plus `el-input-number :min="1"`，搭配 `optionalPositiveIntegerRule`，欄位旁說明為「選填，留空代表不限制」。
- 後端常數 `PlaceOrderLimits.MaxTicketsPerOrder = 50`（`src/ProjectC.Application/Orders/PlaceOrder/PlaceOrderLimits.cs`）。前端無法共用 C# 常數，既有前例是 `web/src/utils/orderHold.ts` 的 `ORDER_HOLD_MINUTES`：寫死數值，再以註解指向後端來源。

## Goals / Non-Goals

**Goals:**
- 修改點集中在限購值的來源，不改動既有五處限購行為的程式碼。

**Non-Goals:**
- 400 訊息中文化（見 proposal）。
- 不讓前端自動跟著後端常數變動，例如不新增「取得系統上限」的 API。

## Decisions

### 決策 1：有效限購 = min(活動限購 ?? 50, 50)，取代原本 computed 的來源

把 `maxTicketsPerOrder` computed 改為回傳有效限購，型別由 `number | null` 變成 `number`。`remainingCapacity` 不再有 `Infinity` 分支，提示的 `v-if` 與 `isWithinOrderLimit` 的 null 判斷一併移除。

- **為什麼**：五處行為已經共用同一個來源，只改來源，就能讓「未設限購」「限購大於 50」「限購小於 50」三種情況都走同一套已測過的路徑。
- **替代方案（否決）**：保留 null 分支，另外在每個使用點加「或 50」的判斷。這樣會有五個使用點要各自記得處理，漏掉一處就會出現買家頁在某條路徑上限不一致的情況，正是這次要修的問題。
- **影響**：既有訊息「這個活動每筆訂單最多購買 N 張」在未設限購時也會出現，N 為 50。這則訊息的「這個活動」在系統上限的情況下語意仍成立，因為這個活動的上限確實是 50，所以不另外寫一則系統上限專用訊息。

### 決策 2：前端常數放 `web/src/utils/orderLimits.ts`

`export const SYSTEM_MAX_TICKETS_PER_ORDER = 50`，註解指向 `PlaceOrderLimits.MaxTicketsPerOrder`，並寫明後端改動時這裡要一起改。買家頁與後台頁共用這個常數。

- **替代方案（否決）**：從 API 取得上限。這需要後端新增欄位或端點，超出「後端不改」的範圍。而且常數很少變動，比照 `ORDER_HOLD_MINUTES` 的作法即可。

### 決策 3：後台以 `:max` 夾值，不新增「不得超過 50」的驗證規則

`el-input-number` 加上 `:max="SYSTEM_MAX_TICKETS_PER_ORDER"`，欄位說明改為「選填，最多 50 張；留空則套用系統上限 50 張」。

- **為什麼**：`el-input-number` 寫回 model 前（輸入中的 `handleInput` 與 change 時）都會先夾到 `[min, max]`，model 不會持有超過 50 的值，再加一條驗證規則永遠不會觸發（已對照 `web/node_modules/element-plus` input-number 原始碼）。這是使用者選定的方案（夾值加說明），說明文字讓 Admin 知道 80 為什麼變成 50。
- **待驗證假設**：`el-input-number` 對小於 min 的值同樣會夾回（0 → 1），所以既有 spec「填 0 或負數顯示驗證錯誤」實際上不會發生。tasks 1.1 會先寫測試量測現況。若實際行為是顯示驗證錯誤而不是夾值，就停下來回報，因為 admin delta 的對應 Scenario 要改寫。
- `optionalPositiveIntegerRule` 保留作為防禦，維持既有行為；`:precision="0"` 已讓元件取整，規則實際上不會由 UI 觸發，所以不新增對應測試。其註解「沒填視為合法（代表不限制）」改為「代表不設活動限購、套用系統上限」。

### 決策 4：測試沿用既有手法

- 買家「下一步」加總超過 50 的測試，沿用既有 `[BW-CONFIRM-012]` 的同步區段手法：先點座位，再同步填入計數，讓 max 還沒反映剛選的座位。差別是改用未設限購的活動，計數填 50 加上 1 個座位。
- 50 個座位的阻擋測試，以 `buildSeat` 產生 51 個座位，逐一點選。

### 決策 5：區域隨選的 `:max` 去掉 `Infinity` 判斷

區域隨選張數輸入的 `:max` 目前寫成 `Number.isFinite(remainingCapacity) ? remainingCapacity : undefined`。決策 1 之後 `remainingCapacity` 必為有限值，改為直接綁 `remainingCapacity`。元件的 `:max` 變動時不會改寫已填入的值，所以送出時仍要靠 `handleQuickPick` 的剩餘額度檢查（BW-ORDER-LIMIT-006 守住）。

## 安全確認

- **輸入驗證**：外部輸入為後台限購欄位與買家計數／區域隨選數量。前端由 `el-input-number` 的 min／max／precision 限制，後台另有 `optionalPositiveIntegerRule`。後端最終驗證不變：建立活動由 `CreateEventRequestValidator`（限購 ≤ 50），下單由 `PlaceOrderRequestValidator`（系統上限 50，於開啟資料庫連線前擋下）。沒有拼接 SQL 或 shell 指令。
- **資料庫**：N/A，本 change 不新增或修改資料庫讀寫，API 呼叫內容與既有相同。
- **權限**：不變更。建立活動頁沿用既有 Admin／Organizer 路由守衛與後端授權 Policy；買家下單沿用既有登入要求與 401 處理。前端限制可被繞過，但繞過後只會收到後端 400，不影響授權。
- **前端**：限購值以數字插值（`{{ }}`）渲染，不使用 `v-html`；常數為程式碼內寫死的數字，非使用者輸入。API 呼叫沿用既有 api 層與統一攔截器帶入 Auth Header，本 change 不新增 API 呼叫。

## Risks / Trade-offs

- [前後端常數不同步：後端改成 100，前端仍是 50] → 前端只會比後端嚴格，不會造成超量下單，但買家能買的張數會少於系統允許的數量。緩解方式是常數註解指向後端檔案。後端改小時，後端 400 仍是最終把關（proposal Non-Goal：顯示後端英文訊息）。
- [既有測試名稱描述過時，例如「計數票種的輸入上限僅受可售總量限制」] → 斷言在可售總量 5 時仍然成立，但名稱會誤導。tasks 會改名，並補上上限為 50 的對照測試。

## Migration Plan

純前端變更，不涉及資料移轉。回復方式為 revert commit。
