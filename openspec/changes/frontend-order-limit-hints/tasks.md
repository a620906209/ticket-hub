# Tasks

> 所有指令都在容器內執行（`docker compose exec web npm run test`、`docker compose exec web npm run build`、`docker compose exec web npm run lint`）。本 change 純前端，不改後端、不跑 dotnet。
>
> 本 change 沒有 AC 測試對應例外：所有 AC 都以 vitest 元件測試涵蓋。

## AC 與測試對應

| AC | 測試檔／方法 | 任務 |
| --- | --- | --- |
| BW-ORDER-005 | `web/src/pages/buyer/EventDetailPage.test.ts`：`[BW-ORDER-005] seatClick_WhenNoEventLimitAndFiftySelected_BlocksWithFiftyMessage`（未設限購、51 個可售座位，逐一點選；斷言前 50 個點選後顯示「已選 50 個座位」、第 51 次顯示含「每筆訂單最多購買 50 張」的訊息、已選數仍為 50）。既有「活動未設定限購張數，可選任意數量座位」（選 2 個）保留，作為未達 50 不受限的對照 | 2.2 |
| BW-ORDER-013 | `EventDetailPage.test.ts`：既有「活動未設定限購張數時，計數票種的輸入上限僅受可售總量限制」改名為 `[BW-ORDER-013] countInput_WhenNoEventLimitAndStockBelowFifty_ClampsToStock`（可售 5、輸入 100 → 5，斷言不變）；可售大於 50 的分支由 BW-ORDER-LIMIT-004 涵蓋 | 2.2 |
| BW-ORDER-LIMIT-001 | `EventDetailPage.test.ts`：`[BW-ORDER-LIMIT-001] limitHint_WhenNoEventLimit_ShowsSystemLimit`（斷言 `.limit-hint` 文字為「每筆訂單限購 50 張」） | 2.2 |
| BW-ORDER-LIMIT-002 | `EventDetailPage.test.ts`：`[BW-ORDER-LIMIT-002] limitHint_WhenEventLimitBelowSystemLimit_ShowsEventLimit`（限購 3；斷言提示為「每筆訂單限購 3 張」、對可售 10 的計數票種輸入 10 時被限制為 3）。既有限購 1／2 的測試維持通過，作為限購行為未退化的回歸 | 2.2 |
| BW-ORDER-LIMIT-003 | `EventDetailPage.test.ts`：`[BW-ORDER-LIMIT-003] eventLimitAboveSystemLimit_ShowsAndEnforcesFifty`（限購 80、計數票種可售 100、輸入 80；斷言提示為「每筆訂單限購 50 張」、輸入值為 50、頁面不含錯誤狀態） | 2.2 |
| BW-ORDER-LIMIT-004 | `EventDetailPage.test.ts`：`[BW-ORDER-LIMIT-004] countInput_WhenNoEventLimit_ClampsToFifty`（未設限購、可售 80、輸入 100；斷言輸入值為 50、不含「已達每筆訂單限購張數」或「最多購買」訊息）。與 BW-ORDER-013 為同一前置條件的兩個分支 | 2.2 |
| BW-ORDER-LIMIT-005 | `EventDetailPage.test.ts`：`[BW-ORDER-LIMIT-005] nextStep_WhenNoEventLimitAndTotalExceedsFifty_ShowsErrorAndStays`（沿用 `[BW-CONFIRM-012]` 同步區段手法：未設限購、1 個座位、計數可售 60，同步點座位後填 50；先斷言結帳列為「已選 1 個座位、50 張計數票券」以防假通過，再按下一步；斷言訊息含「每筆訂單最多購買 50 張」、未進入確認步驟、`placeOrder` 呼叫 0 次） | 2.2 |
| BW-ORDER-LIMIT-006 | `EventDetailPage.test.ts`：`[BW-ORDER-LIMIT-006] quickPick_WhenNoEventLimitAndCountExceedsRemaining_ShowsErrorWithoutSelecting`（未設限購、60 個可售座位、計數票種可售 60；區域隨選張數設 30，再於計數輸入 30；按區域隨選前先斷言區域隨選輸入值仍為 30，以防前置條件不成立而假通過；斷言訊息含「每筆訂單最多購買 50 張」、已選座位數為 0、`placeOrder` 呼叫 0 次）。另斷言未設限購時對區域隨選輸入 100 會被夾為 50 | 2.2 |
| AWU-EVENT-LIMIT-001 | `web/src/pages/admin/EventCreatePage.test.ts`：`[AWU-EVENT-LIMIT-001] maxTicketsPerOrder_WhenInputExceedsFifty_ClampsAndSubmitsFifty`（真實 `el-input-number`，輸入 80 後觸發 change；斷言輸入值為 50、無欄位錯誤、送出後 `createEvent` 收到 `maxTicketsPerOrder: 50`） | 1.1、3.1 |
| 限購張數填寫非正整數 | `EventCreatePage.test.ts`：`maxTicketsPerOrder_WhenInputBelowOne_ClampsToOne`（輸入 0 後觸發 change；斷言輸入值為 1、無欄位錯誤、送出後 `maxTicketsPerOrder: 1`） | 1.1 |
| 建立活動時不填限購張數 | `EventCreatePage.test.ts`：`maxTicketsPerOrder_WhenLeftEmpty_SubmitsUndefined`（斷言 `createEvent` 收到的 `maxTicketsPerOrder` 為 `undefined`） | 3.1 |
| AWU-EVENT-LIMIT-002 | `EventCreatePage.test.ts`：`[AWU-EVENT-LIMIT-002] maxTicketsPerOrder_ShowsSystemLimitHint`（斷言限購欄位的說明文字含「最多 50 張」與「留空則套用系統上限 50 張」） | 3.1 |

## 1. 現況量測（後台限購欄位夾值行為）

- [ ] 1.1 在 `EventCreatePage.test.ts` 新增 `maxTicketsPerOrder_WhenInputBelowOne_ClampsToOne` 與 `[AWU-EVENT-LIMIT-001]`，在修改元件前先執行 `docker compose exec web npm run test -- src/pages/admin/EventCreatePage.test.ts`。預期結果：小於 1 的測試**通過**（現有 `:min="1"` 已會夾值，證明 design 決策 3 的假設成立），超過 50 的測試**失敗**（尚未設定 `:max`，輸入值仍為 80）。若小於 1 的測試失敗（實際是顯示驗證錯誤而不是夾值），就停下來回報，因為 admin delta「限購張數填寫非正整數」Scenario 要改寫。若超過 50 的測試修改前就通過，代表測試沒有測到夾值，必須先修正測試。

## 2. 買家頁有效限購

- [ ] 2.1 新增 `web/src/utils/orderLimits.ts`，內容為 `export const SYSTEM_MAX_TICKETS_PER_ORDER = 50`，註解寫明必須與 `src/ProjectC.Application/Orders/PlaceOrder/PlaceOrderLimits.cs` 一致，後端改動時要一起改（比照 `orderHold.ts`）。驗證方式：`docker compose exec web npm run build` 通過。
- [ ] 2.2 依 design 決策 1 修改 `EventDetailPage.vue`：`maxTicketsPerOrder` 改為回傳 `Math.min(event.maxTicketsPerOrder ?? SYSTEM_MAX_TICKETS_PER_ORDER, SYSTEM_MAX_TICKETS_PER_ORDER)`，移除 `remainingCapacity` 的 `Infinity` 分支、`isWithinOrderLimit` 的 null 判斷、`.limit-hint` 的 `v-if`，以及區域隨選 `:max` 的 `Number.isFinite` 判斷（design 決策 5），並更新上方「null 代表不限制」的註解。加入 AC 表 BW-ORDER-005、BW-ORDER-LIMIT-001～006 的測試，並將既有測試改名。驗證方式：先確認新測試在修改前失敗（BW-ORDER-LIMIT-002 除外，它是回歸對照），修改後 `docker compose exec web npm run test -- src/pages/buyer` 全數通過。

## 3. 後台限購欄位

- [ ] 3.1 依 design 決策 3 修改 `EventCreatePage.vue`：`el-input-number` 加上 `:max="SYSTEM_MAX_TICKETS_PER_ORDER"`，說明文字改為「選填，最多 50 張；留空則套用系統上限 50 張」（數字由常數插值），並更新 `web/src/utils/validators.ts` `optionalPositiveIntegerRule` 註解中「代表不限制」的描述。加入 AC 表 AWU-EVENT-LIMIT-002 與「不填限購張數」的測試。驗證方式：1.1 原本失敗的 `[AWU-EVENT-LIMIT-001]` 改為通過，`docker compose exec web npm run test -- src/pages/admin/EventCreatePage.test.ts` 全數通過。

## 4. 整合驗證與文件

- [ ] 4.1 `docker compose exec web npm run test`、`npm run build`、`npm run lint` 全數通過。若有既有 flaky 測試失敗，要記錄測試名稱，並在 HEAD 重跑確認是否原本就會失敗，不得忽略。
- [ ] 4.2 以 claude-in-chrome 在真實瀏覽器驗證：未設限購的活動顯示「每筆訂單限購 50 張」、計數輸入 100 被夾為 50；後台限購欄位輸入 80 離開後變為 50，說明文字可見。以 DB 或 API 回應確認建立的活動 `maxTicketsPerOrder` 為 50。
- [ ] 4.3 `docs/project-scope.md` §8 待辦 ⑤ 標記 ✅，註明由 `frontend-order-limit-hints` 處理、400 中文化列為 Non-Goal。驗證方式：`openspec validate frontend-order-limit-hints` 通過。

## Workflow follow-up

- 經使用者指示後才 commit；歸檔時同步 `buyer-web-ui`、`admin-web-ui` 主 spec。
