# Proposal

## Why

`place-order-request-limits` 讓後端對每筆訂單套用系統上限 50 張，建立活動的 `MaxTicketsPerOrder` 也不得超過 50，但前端還沒跟上。活動未設限購時，買家頁仍可選超過 50 張，按下「確認訂單」才收到英文 400。後台限購欄位沒有上限，填 51 以上送出也一樣收到英文 400。`buyer-web-ui` BW-ORDER-005 寫的「不受張數限制」，以及 `admin-web-ui` 限購欄位寫的「留空代表不限制」，都已經和系統行為不一致（project-scope §8 待辦 ⑤）。

## What Changes

- 買家活動詳情頁的有效限購改為「活動限購與系統上限 50 取較小值」，活動未設限購時有效限購為 50。既有的限購機制全部改用有效限購，包括限購提示、點選座位阻擋、計數輸入上限、區域隨選剩餘額度，以及「下一步」／「確認訂單」前的加總檢查，不新增另一套檢查。
- 活動未設限購時，買家頁同樣顯示「每筆訂單限購 50 張」提示，和有設限購時一致，避免買家在點第 51 個座位時才意外被擋。
- 本規則生效前建立、限購值大於 50 的既有活動，買家頁以 50 顯示並限制。
- 後台建立活動的「每筆訂單限購」輸入框上限設為 50，超過時由輸入元件夾到 50。欄位說明改為說明上限 50，以及留空時套用系統上限，避免輸入 80 被改成 50 時讓 Admin 困惑。
- 前端新增系統上限常數，註解指向後端 `PlaceOrderLimits`，作法比照既有 `ORDER_HOLD_MINUTES`。
- **Non-Goal**：不把下單或建立活動的 400 英文訊息翻成中文。前端擋下之後，這兩個 400 只會在繞過前端或前後端常數不同步時出現，和其他後端驗證錯誤一樣維持顯示後端訊息。不比對英文字串，避免和後端訊息文字耦合。
- **Non-Goal**：後端不改。系統上限與 `MaxTicketsPerOrder ≤ 50` 的驗證已由 `place-order-request-limits` 完成。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `buyer-web-ui`：「買家可選位並送出訂單」Requirement。有效限購改為與系統上限 50 取較小值，活動未設限購時也顯示限購提示。BW-ORDER-005 從「不受張數限制」改為「受系統上限 50 限制」。另外補上未設限購與限購大於 50 的既有活動兩種情境的 Scenario。
- `admin-web-ui`：「Admin 可透過介面管理活動與票種」Requirement。「每筆訂單限購張數」欄位改為 1–50 的正整數，留空的意思從「不限制」改為「套用系統上限 50」。新增超過 50 時被夾到 50 的 Scenario。

## Impact

- 前端：`web/src/pages/buyer/EventDetailPage.vue`、`web/src/pages/admin/EventCreatePage.vue`，以及新增的 `web/src/utils/orderLimits.ts`。
- 測試：`web/src/pages/buyer/EventDetailPage.test.ts`、`web/src/pages/admin/EventCreatePage.test.ts`（vitest）。
- Spec：`openspec/specs/buyer-web-ui/spec.md`、`openspec/specs/admin-web-ui/spec.md`。
- 文件：`docs/project-scope.md` §8 待辦 ⑤ 標記完成。
- 依賴：已歸檔的 `place-order-request-limits`，後端常數為 `src/ProjectC.Application/Orders/PlaceOrder/PlaceOrderLimits.cs`。
- API、後端與資料庫皆不變更。
