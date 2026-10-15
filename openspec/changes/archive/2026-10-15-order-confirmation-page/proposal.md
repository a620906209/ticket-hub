## Why

2026-10-13 走查時發現，買家選完座位按「送出訂單」就立刻建立訂單、開始保留 10 分鐘，接著導到只有「付款／取消」的結果頁，中間沒有讓買家檢查座位與金額的機會（見 `docs/todo-2026-10-13.md` 第 3 項）。使用者選定的流程是在**建立訂單之前**加一個確認步驟，確認後回到活動列表，提示買家到「我的訂單」付款。

## What Changes

- 活動詳情頁結帳列的「送出訂單」按鈕改為「下一步」，按下後同頁切換到確認步驟（網址加 `?step=confirm`），**不呼叫下單 API**。
- 確認步驟列出已選座位（分區-座號、票價）、計數票種（名稱、數量、小計）、總金額與注意事項；須勾選「我已閱讀並同意注意事項」才能按「確認訂單」。提供「返回改選」，瀏覽器上一頁也回到選位，已選內容保留。
- 按「確認訂單」才呼叫既有下單 API。成功後回到活動列表（取代目前這一筆瀏覽記錄），頂端顯示可關閉的橫幅：「訂單已建立，已為你保留 10 分鐘，請在期限內完成付款」與「前往付款」連結（指向該筆訂單明細頁）。
- 下單失敗沿用既有分流（401、`403 QueueAdmissionRequired`、`RealNameRequired`、429、`409 SalesNotOpen`／`SalesClosed`、其他失敗），另外定義各情形停在確認步驟或退回選位步驟。
- **BREAKING（前端網址）**：移除「訂單結果頁」`/order-result/:id`。舊網址自動導向 `/orders/:id`。訂單明細頁本來就提供相同的「確認付款／取消訂單」（共用 `usePendingOrderActions`），功能不減少。
- 同一頁面只切換確認步驟（只改 `step` query）時不捲回頁首，改由確認畫面自行把焦點與捲動移到購票欄。
- 不改：後端、API、訂單狀態、保留 10 分鐘的規則、排隊與販售期間的判斷邏輯。

**更正**：`docs/todo-2026-10-13.md` 寫「排隊模式下建單是在結果頁等待的」，與程式不符。排隊是在活動詳情頁選位**之前**（入場後才能選位），下單 API 是同步回應。這個 change 真正要處理的排隊議題是：入場資格有效時間 `AdmissionTtlSeconds` 預設 300 秒，買家在確認步驟停留的時間也算在內（見 design.md）。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `buyer-web-ui`：
  - 新增「確認步驟」與「回到活動列表後的付款提示橫幅」兩個 Requirement，以及舊結果頁網址導向。
  - 修改「買家可選位並送出訂單」「區域隨選」「結帳列」「換頁回到頁首」四個 Requirement 中與送出、導頁、按鈕文字、捲動有關的部分，以及「未登入導向登入頁」中的「下單結果頁」用語。
  - 以下未列入 MODIFIED 的既有 Requirement 仍使用「送出訂單（按鈕）」，不逐一修改，由新增的確認步驟 Requirement 定義用語對應（「送出訂單」＝確認步驟按「確認訂單」；「送出訂單按鈕」在選位步驟＝「下一步」、在確認步驟＝「確認訂單」）。archive 時這兩個 Requirement 文字不變，用語對應段落須一併併入主 spec：
    - 「活動頁依販售期間顯示狀態並限制購票操作」
    - 「活動頁標示需實名，並引導未登記的買家登記」
  - 移除「買家可在下單結果頁確認或取消剛下的訂單」。

## Change 顆粒度

維持單一 change，不拆分：

- 只影響 1 個 capability（`buyer-web-ui`），tasks 約 20 項，未達 CLAUDE.md 的拆分門檻。
- 確認步驟、回大廳橫幅與移除結果頁是同一條購票路徑的前後段。只做確認步驟而不改成功後的去向，買家仍會落到結果頁，達不到「確認完回大廳」的需求；只移除結果頁則沒有地方提示付款。

## Impact

- Web：
  - `EventDetailPage.vue` 與其測試：結帳列按鈕、確認步驟、送出流程、錯誤分流。既有約 40 個以「送出訂單」按鈕驅動的測試（`submitButton(` 共 47 處，含按鈕停用斷言），要改成「下一步」或經過確認步驟。
  - `EventListPage.vue` 與其測試：付款提示橫幅。
  - 新增一個 Pinia store，在建單成功與活動列表之間傳遞一次性提示；新增 `orderConfirmationFlow.test.ts` 串接驗證兩頁傳遞同一個訂單 Id、`router/orderResultRedirect.test.ts` 驗證舊網址經路由到明細頁的 403／404 顯示；`LoginPage.test.ts` 補登入後導回測試。
  - `router/index.ts`：移除 `order-result` 路由、新增舊網址導向、`scrollBehavior` 判斷只切換 step 的情形；`lazyRoutes.test.ts` 同步更新。
  - 刪除 `OrderResultPage.vue`／`OrderResultPage.test.ts`；`usePendingOrderActions.ts`、`orderPageTestSupport.ts` 的註解同步更新。
- 文件：`docs/todo-2026-10-13.md` 第 3 項的排隊描述更正。
- 後端、API、DB：不動。
