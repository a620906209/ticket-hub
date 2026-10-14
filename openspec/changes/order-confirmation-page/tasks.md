> 所有指令都在容器內執行（例如 `docker compose exec web npm run test`），不在本機執行 node／npm。
>
> **AC 測試對應例外**：BW-FLOW-001 的 sticky 版面斷言改用瀏覽器量測，例外紀錄見 design.md「AC 測試對應例外」。核准狀態：**已核准**（2026-10-14）。

## AC 與測試對應

| AC | 測試檔／方法 | 任務 |
| --- | --- | --- |
| BW-CONFIRM-001 | `EventDetailPage.test.ts`：`nextStep_WithSeatsAndCountSelections_ShowsConfirmStepWithoutPlacingOrder`（斷言 `placeOrder` 呼叫 0 次、座位網格與結帳列不存在）；`confirmStep_WithSeatsOnlyOrCountOnly_ShowsMatchingItemsAndTotal`（純座位、純計數各一案例：只列出有選的那一類、另一類區塊不顯示、數量 0 的計數票種不列出、總金額與結帳列摘要一致） | 2.1 |
| BW-CONFIRM-002 | `EventDetailPage.test.ts`：`confirmOrder_BeforeAgreeingNotice_IsDisabled`、`confirmOrder_AfterAgreeingNotice_PlacesOrderOnceWithSelections` | 2.1 |
| BW-CONFIRM-003 | `EventDetailPage.test.ts`：`backToSelection_FromConfirmStep_KeepsSelectionsAndFocusesNextButton`（`placeOrder` 0 次）、`nextStep_AfterReturning_ResetsNoticeAgreement`、`forward_BackToConfirmStep_ResetsNoticeAgreement` | 2.1 |
| BW-CONFIRM-004 | `EventDetailPage.test.ts`：`mount_WithConfirmStepQueryAndNoSelections_ReplacesToSelectionStep` | 2.2 |
| BW-CONFIRM-005 | `EventDetailPage.test.ts`：`confirmOrder_WhilePlacingOrder_CallsApiOnceAndDisablesBack` | 2.3 |
| BW-CONFIRM-006 | `EventDetailPage.test.ts`：`confirmOrder_When429OrSalesNotOpen_StaysOnConfirmStepWithError`（兩種錯誤各一案例；斷言 `replace` 未被呼叫、勾選與已選內容不變、座位與票種查詢 API 未再呼叫、訊息在確認步驟錯誤區且結帳列不存在） | 2.3 |
| BW-CONFIRM-007 | `EventDetailPage.test.ts`：`confirmOrder_WhenGenericFailure_ReturnsToSelectionClearsAndReloads` | 2.3 |
| BW-CONFIRM-008 | `EventDetailPage.test.ts`：`confirmOrder_WhenQueueRealNameOrSalesClosed_ReturnsToSelectionKeepingSelections`（三種錯誤各一案例；共同斷言 `replace` 移除 `step`、已選內容不變、座位查詢未再呼叫；Queue：頁面頂端訊息＋排隊狀態 API 再呼叫一次；RealName：實名引導出現、無泛用錯誤；SalesClosed：「本活動已停售」＋「下一步」停用） | 2.3 |
| BW-CONFIRM-009 | `EventDetailPage.test.ts`：`confirmOrder_When401_RedirectsToLoginWithoutStepQuery` | 2.3 |
| BW-CONFIRM-010 | `EventDetailPage.test.ts`：`confirmStep_WhenSalesEndPasses_ReplacesToSelectionAndDisablesNext` | 2.2 |
| BW-CONFIRM-011 | `EventDetailPage.test.ts`：`nextStep_EnteringConfirmStep_FocusesConfirmHeading` | 2.1 |
| BW-CONFIRM-012 | `EventDetailPage.test.ts`：`nextStep_WhenTotalExceedsLimit_ShowsErrorInCheckoutBarAndStays` | 2.1 |
| BW-ORDER-015 | 與 BW-CONFIRM-012 共用 `nextStep_WhenTotalExceedsLimit_ShowsErrorInCheckoutBarAndStays`：座位 1＋計數 2 超過限購 2；斷言 `push`／`replace` 未呼叫（網址不帶 `step`）、結帳列顯示限購提示、確認步驟不存在、`placeOrder` 呼叫 0 次。既有「送出前超過限購」測試改寫為此方法，不另保留。製造超量的方式：`ElInputNumberStub` 會把計數輸入夾在剩餘額度內，因此須在同一個同步區段內先 `element.click()` 選座位（不 await），再直接對計數輸入派發 `input` 2，之後才 `flushPromises()`；測試另斷言結帳列確實顯示合計 3 張，避免輸入被夾住而假通過 | 1.1、2.1 |
| BW-CONFIRM-013 | `EventDetailPage.test.ts`：`confirmStep_RealNameRequiredEvent_ShowsRealNameNotice`（含不需實名的反例） | 2.1 |
| BW-CONFIRM-014 | `EventDetailPage.test.ts`：`confirmOrder_WhenPageUnmountedBeforeSuccess_ShowsMessageWithoutNavigatingOrRecordingNotice`（斷言 `replace`／`push` 未呼叫、store 為 null、`ElMessage.success` 以「訂單已建立，請到「我的訂單」完成付款」呼叫） | 2.3 |
| BW-CONFIRM-015 | `EventDetailPage.test.ts`：`confirmOrder_WhenSucceeded_RecordsNoticeAndReplacesToEventList`（`placeOrder` 回傳 `{ id: 'order-123' }`；斷言 store 的 `placedOrderId` 為 `'order-123'`、`replace` 以 `{ name: 'events' }` 呼叫一次、`push` 未呼叫）；跨頁串接見下方「成功下單通知鏈」 | 2.3、3.3 |
| BW-CONFIRM-016 | `EventDetailPage.test.ts`：`confirmOrder_WhenBackPressedBeforeResponse_HandlesSuccessAnd429OnSelectionStep`（成功：store 記錄的 Id 正確且 `replace({ name: 'events' })` 被呼叫；429：`replace` 未被呼叫、已選內容不變、訊息顯示在結帳列） | 2.3 |
| BW-CONFIRM-017 | `EventDetailPage.test.ts`：`actionError_WhenSwitchingSteps_IsKeptUntilNextPrecheckPasses`（切換步驟後訊息仍在；未通過前置檢查不清除；通過後清除） | 2.3 |
| BW-LOBBY-NOTICE-001 | `EventListPage.test.ts`：`mount_AfterOrderPlaced_ShowsPaymentNoticeLinkingToOrderDetail`（預設 `placedOrderId = 'order-456'`；斷言橫幅文字、橫幅容器 `role="status"`、「前往付款」`href` 完全等於 `/orders/order-456`、掛載後 store 為 null）；跨頁串接見下方「成功下單通知鏈」 | 3.1、3.3 |
| BW-LOBBY-NOTICE-002 | `EventListPage.test.ts`：`closeNotice_WhenClicked_HidesNotice` | 3.1 |
| BW-LOBBY-NOTICE-003 | `EventListPage.test.ts`：`mount_SecondTimeAfterNoticeShown_DoesNotShowNotice`；`placedOrderNotice.test.ts`：`consumePlacedOrder_CalledTwice_ReturnsIdOnlyOnce` | 3.1、1.2 |
| BW-LOBBY-NOTICE-004 | `EventListPage.test.ts`：`mount_WithoutPlacedOrder_DoesNotShowNotice` | 3.1 |
| BW-LOBBY-NOTICE-005 | `EventListPage.test.ts`：`mount_AfterOrderPlacedAndListFails_ShowsErrorAndNotice` | 3.1 |
| BW-RESULT-REDIRECT-001 | `router/index.test.ts`：`push_OldOrderResultUrlWhenSignedIn_RedirectsToOrderDetail`；跨路由：`orderResultRedirect.test.ts` 本人訂單案例 | 4.1、4.4 |
| BW-RESULT-REDIRECT-002 | `router/index.test.ts`：`push_OldOrderResultUrlWhenSignedOut_RedirectsToLoginWithOrderDetailRedirect` | 4.1 |
| BW-RESULT-REDIRECT-003 | 導向：`router/index.test.ts`：`push_OldOrderResultUrlWithUnknownOrNonGuidId_RedirectsToOrderDetailWithSameId`（不存在的 GUID、`not-a-guid` 各一案例；斷言解析後 `name` 為 `order-detail`、`params.id` 與原值相同）。導向後顯示：沿用既有 `OrderDetailPage.test.ts` 的 BW-MYORDER-NOTFOUND-001、BW-MYORDER-FORBIDDEN-001、BW-MYORDER-INVALID-ID-001（斷言提示文字且不含「訂單 Id：」）。跨路由自動化：`web/src/router/orderResultRedirect.test.ts`（見 4.4）。6.4 僅為補充走查，不作為 AC 依據 | 4.1、4.4 |
| BW-NAV-STEP-001 | `router/index.test.ts`：`scrollBehavior_WhenOnlyStepQueryChanges_DoesNotScroll`（有、無 savedPosition 皆回傳 `false`；路徑、其他 query、hash 不同的反例維持 `savedPosition ?? { top: 0 }`） | 4.1 |
| BW-ORDER-001 | `EventDetailPage.test.ts`：既有「選擇可售座位並成功下單」（1.1 改寫）。輸入：`buildSeat()`（`seat-1`）＋座位制票種 `tt-seat-a`，`placeOrder` 回傳 `{ id: 'order-seat-1' }`。經「下一步 → 勾選注意事項 → 確認訂單」；斷言 `placeOrder` 恰呼叫一次且參數完全等於下列陣列（`toHaveBeenCalledWith` 深度相等，多出或缺少欄位都失敗）、`replace` 以 `{ name: 'events' }` 呼叫一次、`push` 未呼叫、store `placedOrderId` 等於 API 回傳的 Id：`[{ eventSeatId: 'seat-1', ticketTypeId: 'tt-seat-a' }]`；付款連結見通知鏈測試的座位案例 | 1.1、3.3 |
| BW-ORDER-007 | `EventDetailPage.test.ts`：既有「純計數票種輸入購買數量並成功下單」（1.1 改寫）。輸入：只有 `buildCountTicketType()`（`tt-count-1`、`requiresSeat: false`、可售 10），不 mock 座位、不點座位，「站立區」數量輸入 3，`placeOrder` 回傳 `{ id: 'order-count-1' }`。經「下一步 → 勾選注意事項 → 確認訂單」；斷言 `placeOrder` 恰呼叫一次且參數完全等於下列陣列（`toHaveBeenCalledWith` 深度相等，多出或缺少欄位都失敗）、`replace` 以 `{ name: 'events' }` 呼叫一次、`push` 未呼叫、store `placedOrderId` 等於 API 回傳的 Id：`[{ eventSeatId: null, ticketTypeId: 'tt-count-1', quantity: 3 }]`（`eventSeatId` 為 `null`，見下方說明）；付款連結見通知鏈測試的純計數案例 | 1.1、3.3 |
| BW-ORDER-008 | `EventDetailPage.test.ts`：既有「混合座位選購與純計數購買並成功下單」（1.1 改寫；原測試沒有成功導頁斷言，一併補上）。輸入：`seat-1`＋`tt-seat-a`、「站立區」數量 2，`placeOrder` 回傳 `{ id: 'order-mixed-1' }`。經「下一步 → 勾選注意事項 → 確認訂單」；斷言 `placeOrder` 恰呼叫一次且參數完全等於下列陣列（`toHaveBeenCalledWith` 深度相等，多出或缺少欄位都失敗）、`replace` 以 `{ name: 'events' }` 呼叫一次、`push` 未呼叫、store `placedOrderId` 等於 API 回傳的 Id：`[{ eventSeatId: 'seat-1', ticketTypeId: 'tt-seat-a' }, { eventSeatId: null, ticketTypeId: 'tt-count-1', quantity: 2 }]`；付款連結見通知鏈測試的混合案例 | 1.1、3.3 |
| BW-ORDER-003 | 由 BW-CONFIRM-009 涵蓋；既有 401 測試改經確認步驟 | 1.1、2.3 |
| BW-ORDER-002、011 | `EventDetailPage.test.ts` 既有一般失敗測試，改為經確認步驟，另斷言 `step` 被移除 | 1.1 |
| BW-QUICK-003、008 | `EventDetailPage.test.ts` 既有區域隨選送出測試，改為經確認步驟 | 1.1 |
| BW-FLOW-001 | 按鈕文字：`EventDetailPage.test.ts` 既有結帳列測試斷言「下一步」；sticky 版面：瀏覽器量測（例外） | 1.1、6.3 |
| BW-FLOW-003 | `EventDetailPage.test.ts` 既有錯誤位置測試，移除 429 案例（移到 BW-CONFIRM-006），一般失敗改經確認步驟 | 1.1 |
| BW-FLOW-004 | 既有頁面頂端錯誤測試；`QueueAdmissionRequired` 部分改經確認步驟，由 BW-CONFIRM-008 一併斷言 | 1.1、2.3 |
| BW-FLOW-006 | `EventDetailPage.test.ts` 既有排隊畫面測試，斷言不顯示「下一步」 | 1.1 |
| BW-FLOW-007 | `EventDetailPage.test.ts` 既有清除錯誤測試，改為按「下一步」後兩處錯誤都消失並進入確認步驟 | 1.1 |
| 未登入導向登入頁（修改「下單結果頁」用語） | 用語修改，行為不變；由既有 `router/index.test.ts` 的 requiresAuth 測試與 BW-RESULT-REDIRECT-002 涵蓋 | 4.1 |

delta spec 下單 Requirement 與 BW-ORDER-007／008 已明定計數項目 `eventSeatId` 為 `null`（欄位存在）、座位項目不含 `quantity`（欄位省略）；依據：`api/orders.ts` 的 `PlaceOrderSelection.eventSeatId` 型別為 `string | null`，產生的 OpenAPI 型別 `PlaceOrderSelectionRequest.eventSeatId` 為 `null | string`，既有測試也斷言 `null`。本 change 不改請求形狀，因此 BW-ORDER-007 斷言 `eventSeatId === null`，同時以深度相等排除任何非 null 的座位 Id。

**成功下單通知鏈**（BW-CONFIRM-015 ＋ BW-LOBBY-NOTICE-001 串接）：`web/src/pages/buyer/orderConfirmationFlow.test.ts`：`confirmOrder_WhenSucceeded_EventListShowsNoticeLinkingToSameOrderId`，以 `it.each` 跑三個案例（每個案例都斷言 `placeOrder` 恰呼叫一次、參數以深度相等比對）：

| 案例 | 輸入 | `placeOrder` 參數 | `orderId` | 對應 |
| --- | --- | --- | --- | --- |
| 座位 | `seat-1`＋`tt-seat-a` | `[{ eventSeatId: 'seat-1', ticketTypeId: 'tt-seat-a' }]` | `order-123` | BW-ORDER-001 |
| 純計數 | 只有 `requiresSeat: false` 的 `tt-count-1`，數量 3 | `[{ eventSeatId: null, ticketTypeId: 'tt-count-1', quantity: 3 }]` | `order-count-123` | BW-ORDER-007 |
| 混合 | `seat-1`＋`tt-seat-a`，加 `tt-count-1` 數量 2 | `[{ eventSeatId: 'seat-1', ticketTypeId: 'tt-seat-a' }, { eventSeatId: null, ticketTypeId: 'tt-count-1', quantity: 2 }]` | `order-mixed-1` | BW-ORDER-008 |

混合案例合計 3 張，活動 fixture 須不設限購或限購 ≥ 3，避免與 BW-ORDER-015 的限購攔截混淆。

兩頁共用同一個 Pinia 實例（`createPinia()`＋`setActivePinia`，不 mock store）。以下以座位案例的 `order-123` 為例，其他案例換成各自的 Id：

1. mock `placeOrder` 回傳 `{ id: 'order-123' }`；掛載 `EventDetailPage`，依案例選位及／或輸入計數數量 → 下一步 → 勾選 → 確認訂單。
2. 斷言 store `placedOrderId === 'order-123'`。
3. 斷言 router mock 的 `replace` 只呼叫一次，參數為 `{ name: 'events' }`；`push` 未呼叫。
4. 卸載 `EventDetailPage`，以同一個 Pinia 掛載 `EventListPage`（`getEvents` mock 成功回傳空列表）。
5. 斷言橫幅顯示，且「前往付款」連結 `href` 完全等於 `/orders/order-123`（不是空值、`undefined` 或其他 Id）。
6. 斷言掛載後 store `placedOrderId === null`（已被列表消費）。
7. 反例：再掛載一次 `EventListPage`，橫幅不存在。

真實瀏覽器端的同一條鏈由 6.4 驗證。

既有 BW-RN-EVENT-005、BW-SALES-010、BW-SALES-011（屬未列入 MODIFIED 的 Requirement）測試同樣改為經確認步驟送出（1.1），斷言內容依 BW-CONFIRM-006／008 的步驟規則調整。

## 既有 Scenario 回歸對應

MODIFIED Requirement 依 OpenSpec 規則須整段複製（archive 時整段取代主 spec，省略的 Scenario 會被刪除），因此下列行為未變的 Scenario 仍在 delta 中。每一條都對應到既有或新增的測試；標「1.1 改寫」者因送出路徑經過確認步驟而調整操作，斷言意圖不變；其餘測試中凡含 `submitButton(`、「送出訂單」或 `/order-result/` 者，一律由 1.1 的 grep 涵蓋改寫（即使本表未標 1.1）；完全不含者本 change 不修改，6.1 全部通過即為回歸證據。

| Scenario | 測試檔：測試名稱 | 主要斷言 | 任務 |
| --- | --- | --- | --- |
| 未登入直接進入我的訂單頁 | `router/index.test.ts`：「未登入進入需登入頁面導向登入頁」 | `name === 'login'`、`query.redirect === '/orders'` | 6.1 |
| 未登入在活動詳情頁點選座位 | 已售出座位：`EventDetailPage.test.ts`「[BW-SEAT-A11Y-004] 未登入點選已售出座位…仍導向登入頁」；可售座位：**新增** `EventDetailPage.test.ts`：`seatClick_WhenSignedOutOnAvailableSeat_RedirectsToLoginWithoutSelecting` | `push({ path: '/login', query: { redirect: '/events/event-1' } })`、已選清單為空、`placeOrder` 0 次 | 1.4 |
| 登入成功後導回原頁面 | **新增** `LoginPage.test.ts`：`submit_WithRedirectQuery_PushesRedirectAfterLogin`（含無 `redirect` 時導向首頁的反例） | 登入成功後 `push('/events/event-1')` | 1.4 |
| BW-ORDER-004 | `EventDetailPage.test.ts`「已選座位數達到每筆訂單限購張數，無法再選新座位」 | 顯示「已達每筆訂單限購張數」、新座位不在已選清單 | 6.1 |
| BW-ORDER-005 | `EventDetailPage.test.ts`「活動未設定限購張數，可選任意數量座位」 | 可選數量不受限 | 6.1 |
| BW-ORDER-006 | `EventDetailPage.test.ts`「未登入嘗試調整計數購買數量：立即導向登入頁，不套用該次變更」 | 導向登入頁、數量維持 0 | 6.1 |
| BW-ORDER-009 | `EventDetailPage.test.ts`「純計數購買數量達到每筆訂單限購張數：輸入元件限制上限…」 | 輸入上限為剩餘額度、不顯示「已達每筆訂單限購張數」 | 6.1 |
| BW-ORDER-010 | `EventDetailPage.test.ts`「計數輸入元件限制數量不得超過可售總量…」 | 上限等於可售總量、無錯誤樣式 | 6.1 |
| BW-ORDER-012 | `EventDetailPage.test.ts`「純計數票種可售總量為 0：輸入框上限為 0，顯示「已售完」」 | 上限 0、顯示「已售完」 | 6.1 |
| BW-ORDER-013 | `EventDetailPage.test.ts`「活動未設定限購張數時，計數票種的輸入上限僅受可售總量限制」 | 上限等於可售總量 | 6.1 |
| BW-ORDER-014 | `EventDetailPage.test.ts`「計數購買數量為 0 時不送出對應項目」（1.1 改寫：經確認步驟送出） | `placeOrder` 參數不含數量 0 的票種 | 1.1 |
| BW-ORDER-016 | `EventDetailPage.test.ts`「已手動選取座位佔用額度後，計數購買輸入上限隨之減少」 | 上限扣除已選座位數 | 6.1 |
| BW-QUEUE-001 | `EventDetailPage.test.ts`「BW-QUEUE-001：進入熱門搶購模式活動且尚未加入排隊…」 | 顯示加入排隊、座位網格不可選 | 6.1 |
| BW-QUEUE-002 | `EventDetailPage.test.ts`「BW-QUEUE-002：排隊中顯示等待畫面與前方等待人數…」 | 等待人數、停用入口、輪詢 | 6.1 |
| BW-QUEUE-003 | `EventDetailPage.test.ts`「BW-QUEUE-003：排隊狀態由 Waiting 轉為 Admitted 後…」（1.1 改寫：斷言「下一步」可用） | 停止輪詢、「下一步」可用 | 1.1 |
| BW-QUEUE-004 | `EventDetailPage.test.ts`「BW-QUEUE-004：下單時排隊資格已逾時…」（1.1 改寫，依 BW-CONFIRM-008） | 頁面頂端訊息、排隊狀態再查詢、已選內容保留、`step` 被移除 | 1.1 |
| BW-QUEUE-005 | `EventDetailPage.test.ts`「BW-QUEUE-005：下單因請求頻率限制被拒絕（429）…」（1.1 改寫，依 BW-CONFIRM-006） | 停在確認步驟、已選內容保留、查詢 API 未再呼叫 | 1.1 |
| BW-QUEUE-006 | `EventDetailPage.test.ts`「BW-QUEUE-006：其他語意的 403…沿用一般失敗處理」（1.1 改寫，依 BW-CONFIRM-007） | 不顯示排隊畫面、依一般失敗清空並退回選位 | 1.1 |
| BW-TOGGLE-001 | `EventDetailPage.test.ts`「BW-TOGGLE-001：排隊等待期間 Queue Mode 被關閉…」 | 停止輪詢、開放選位 | 6.1 |
| CAPTCHA-BW-QUEUE-001 | `EventDetailPage.test.ts`「CAPTCHA-BW-QUEUE-001：尚未加入排隊時顯示驗證碼圖片與輸入欄位」 | 呼叫驗證碼 API、顯示圖片與欄位 | 6.1 |
| CAPTCHA-BW-QUEUE-002 | `EventDetailPage.test.ts`「CAPTCHA-BW-QUEUE-002：驗證碼錯誤時顯示提示、清空輸入並自動換發…」 | 錯誤提示、輸入清空、換發 | 6.1 |
| BW-QUICK-001 | `EventDetailPage.test.ts`「[BW-QUICK-001] 區域隨選：維持預設「全部區域」…」（含送出與 `/order-result/` 斷言，1.1 改寫為經確認步驟、成功斷言改 `replace({ name: 'events' })`）「[BW-QUICK-001] 連續使用自動選位…」（不修改） | 抽出座位加入清單、不重複；送出的座位 Id 等於抽出的座位 | 1.1 改寫、6.1 |
| BW-QUICK-002 | `EventDetailPage.test.ts`「[BW-QUICK-002] 區域隨選：指定單一分區時…」（含送出與 `/order-result/` 斷言，1.1 改寫） | 只抽該分區 | 1.1 改寫 |
| BW-QUICK-004 | `EventDetailPage.test.ts`「區域隨選張數超過可售座位或限購剩餘額度時…」「區域隨選要求數量超過可售座位數時（未設限購）…」 | 顯示錯誤、不加入座位、`placeOrder` 0 次 | 6.1 |
| BW-QUICK-005 | `EventDetailPage.test.ts`「未登入使用區域隨選：導向登入頁…」 | 導向登入頁、未加入座位 | 6.1 |
| BW-QUICK-006 | `EventDetailPage.test.ts`「已輸入純計數購買數量時，區域隨選的剩餘額度隨之減少」 | 超過扣除後額度時顯示錯誤 | 6.1 |
| BW-QUICK-007 | `EventDetailPage.test.ts`「純計數票種不會出現在區域隨選的分區選單與抽選池中」「區域隨選：分區有可售座位但沒有對應的座位制票種時…」 | 選單與抽選池不含計數票種 | 6.1 |
| BW-FLOW-002、005 | `EventDetailPage.test.ts`「[BW-FLOW-002、005] 結帳列依選取順序列出前 6 個座位…」 | 前 6 個＋「等 7 個座位」、`aria-live="polite"` | 6.1 |
| 開啟不存在的網址 | `router/index.test.ts`：`push_WhenPathUnknown_RedirectsToEventList` | `name === 'events'` | 6.1 |
| 換頁回到頁首 | `router/index.test.ts`：`scrollBehavior_WithOrWithoutSavedPosition_RestoresSavedOrScrollsToTop`（BW-NAV-STEP-001 的反例一併涵蓋路徑不同時的行為） | 無 savedPosition 回傳 `{ top: 0 }`、有則回傳 savedPosition | 6.1、4.1 |

查證時發現兩條既有 Scenario 原本沒有測試（可售座位的未登入點選、登入後導回 `redirect`），由 1.4 補上。

## 1. 測試基礎與 store

- [ ] 1.1 先改測試基礎：`EventDetailPage.test.ts` 的 `vue-router` mock 改為可反應的 route（`params`、`query`、`path`、`fullPath`），`push`／`replace`／`back` mock 會同步更新 `query`；新增 `goToConfirmStep()`（按「下一步」→ 勾選注意事項）與 `confirmOrder()` 輔助函式。既有以「送出訂單」驅動的測試（`grep -n "submitButton(\|送出訂單\|order-result"`，約 40 個測試、`submitButton(` 47 處，含 `isDisabled(submitButton(...))` 停用斷言，一律改為「下一步」或經確認步驟）改為經確認步驟，成功斷言由 `/order-result/:id` 改為 `replace({ name: 'events' })`＋store 記錄 Id。此時執行測試應因元件尚未實作而失敗，記錄失敗數。
- [ ] 1.2 先寫 `web/src/stores/placedOrderNotice.test.ts`：`setPlacedOrder` 後 `consumePlacedOrder` 回傳 Id、第二次回傳 null；未設定時回傳 null。
- [ ] 1.3 實作 `web/src/stores/placedOrderNotice.ts`（design 決策 4），1.2 通過。
- [ ] 1.4 補既有 Scenario 缺少的兩個測試（見「既有 Scenario 回歸對應」）：`EventDetailPage.test.ts` 的 `seatClick_WhenSignedOutOnAvailableSeat_RedirectsToLoginWithoutSelecting`、`LoginPage.test.ts` 的 `submit_WithRedirectQuery_PushesRedirectAfterLogin`。兩者測既有行為，寫完應直接通過；為確認測試會失敗，暫時把斷言的 redirect 改成錯值確認失敗後還原。

## 2. 活動詳情頁確認步驟

- [ ] 2.1 先寫測試：BW-CONFIRM-001、002、003、011、012、013（見對應表）。
- [ ] 2.2 先寫測試：BW-CONFIRM-004、010（正規化與條件失效退回）。
- [ ] 2.3 先寫測試：BW-CONFIRM-005～009、014～017（斷言內容見對應表）。
- [ ] 2.4 新增前端常數 `ORDER_HOLD_MINUTES = 10`，註解寫明對應 `CreateOrderHandler.HoldDuration`（design 決策 5），供注意事項與 3.2 的橫幅共用。
- [ ] 2.5 實作 `EventDetailPage.vue`（design 決策 1、2、3、7、8）：
  - 結帳列按鈕改「下一步」
  - 確認步驟畫面
  - `step` 正規化 watch
  - `handleSubmit` 拆為「下一步」與「確認訂單」，錯誤後依決策 3 的表格處理步驟
  - 成功後 `setPlacedOrder`＋`replace`；卸載後不導頁、不記錄，改 `ElMessage.success`
  - 確認步驟錯誤區與結帳列共用 `actionErrorMessage`
  - 401 的 `redirect` 改用不含 query 的 `route.path`

  不用 `v-html`。完成後 1.1、2.1～2.3 全部通過。

## 3. 活動列表付款提示

- [ ] 3.1 先寫 `EventListPage.test.ts` 測試：BW-LOBBY-NOTICE-001～005（掛載前以 Pinia 設定或不設定 `placedOrderId`）。既有 `RouterLinkStub` 只渲染 `<a class="event-card">`、不輸出 `href`，須改為依 `to` 輸出 `href`（字串原樣、物件經 `router.resolve` 或改用真實 `RouterLink`＋測試 router），否則 `href` 斷言無法成立。
- [ ] 3.2 實作 `EventListPage.vue` 橫幅（design 決策 4）：`setup` 時 `consumePlacedOrder()`，`el-alert` 可關閉、`role="status"`，`router-link` 指向 `/orders/{id}`。3.1 通過。
- [ ] 3.3 寫 `orderConfirmationFlow.test.ts`（見對應表下方「成功下單通知鏈」的 7 個步驟），在 2.5、3.2 完成後通過。兩頁的 `RouterLink` 須輸出真實 `href`（同 3.1）。

## 4. 路由與移除結果頁

- [ ] 4.1 先寫 `router/index.test.ts` 測試：BW-NAV-STEP-001、BW-RESULT-REDIRECT-001／002／003。`lazyRoutes.test.ts` 移除 `order-result` 的元件對應，改為斷言該路徑是 redirect：`[BS-005]` 延遲載入數由 20 改 19、標題「18 個頁面」改「17 個頁面」；`[BS-014]` 的 `EXPECTED_ROUTE_TREE` 該路由 `redirect` 是函式，以 `expect.any(Function)` 比對，函式行為由 4.4 驗證。
- [ ] 4.2 實作 `router/index.ts`（design 決策 6、9）：`order-result/:id` 改為 redirect 到 `order-detail`，`scrollBehavior` 加入只切換 `step` 時回傳 `false`。4.1 通過。
- [ ] 4.4 寫 `web/src/router/orderResultRedirect.test.ts`（BW-RESULT-REDIRECT-001、003 跨路由）：用真實的 app router（`router/index.ts`）與真實 `OrderDetailPage`（以 `<RouterView />` 掛載），只 mock `api/orders`；已登入（同 `router/index.test.ts` 的 `signIn`）。每個案例 `router.push('/order-result/{Id}')` 後 `flushPromises`：
  - 共同斷言：`currentRoute.name === 'order-detail'`、`params.id` 等於原值、`getMyOrderDetail` 以原值呼叫一次。
  - 他人訂單（`getMyOrderDetail` 拒絕 `ApiError(403)`）：顯示「你沒有權限查看這筆訂單」，不含「訂單 Id：」。
  - 不存在的 GUID、`not-a-guid`（`ApiError(404)`）：顯示「找不到這筆訂單」，不含「訂單 Id：」。
  - 本人訂單（回傳 Pending 訂單）：顯示訂單明細（含「訂單 Id：」與該 Id）。

  在 4.2 完成後通過；4.2 之前執行應失敗（`order-result` 仍指向結果頁）。
- [ ] 4.3 刪除 `OrderResultPage.vue`、`OrderResultPage.test.ts`；更新 `usePendingOrderActions.ts`、`orderPageTestSupport.ts` 提到結果頁的註解。確認 `web/src` 已無 `OrderResultPage` 與 `/order-result/` 的導頁（`grep`，路由 redirect 與其測試除外）。

## 5. 文件

- [ ] 5.1 更正 `docs/todo-2026-10-13.md` 第 3 項的排隊描述：排隊在選位前，下單為同步回應，入場資格 300 秒包含確認步驟停留時間。

## 6. 驗證

- [ ] 6.1 容器內執行 `npm run test`（全部 Vitest）與 `npm run build`（含型別檢查），全部通過；記錄測試數。若有既有已知 flake，重跑確認並記錄。
- [ ] 6.2 失敗分支驗證（不改 bind mount 的原始碼，在 `git worktree` 複本中進行），逐一做以下破壞，確認對應測試失敗後還原：
  - 移除注意事項勾選對「確認訂單」的停用條件 → BW-CONFIRM-002 失敗
  - 429 改為退回選位 → BW-CONFIRM-006 失敗
  - 成功後 `replace` 改 `push` → BW-CONFIRM-015 失敗
  - `setPlacedOrder` 改傳固定字串（不用 API 回應的 `id`）→ BW-CONFIRM-015 與通知鏈測試失敗
  - 橫幅連結改用空字串 Id → BW-LOBBY-NOTICE-001 與通知鏈測試失敗
  - `consumePlacedOrder` 不清空 → BW-LOBBY-NOTICE-003 失敗
  - `scrollBehavior` 拿掉 `step` 判斷 → BW-NAV-STEP-001 失敗
  - 拿掉 `step` 正規化 → BW-CONFIRM-004 失敗
  - `order-result/:id` 的 redirect 不帶 `id`（例如固定導向 `/orders`）→ `orderResultRedirect.test.ts` 失敗
- [ ] 6.3 BW-FLOW-001 瀏覽器量測（例外已核准）：依 design.md「AC 測試對應例外」的通過／失敗條件，在 5 個寬度記錄結果於本檔。每個寬度記錄所用 selector、按鈕 `top`／`bottom`／`width`／`height`、`lastSeatTop` 原始數值與判定（通過／失敗／量測無效），不只記錄 PASS／FAIL。
- [ ] 6.4 真實瀏覽器走查（claude-in-chrome）：
  - 一般活動：選位 → 下一步 → 返回改選 → 下一步 → 勾選 → 確認 → 活動列表橫幅 → 前往付款。記錄下單 API 回應（network）的訂單 Id，斷言三者相同：橫幅「前往付款」的 `href`、點擊後的明細頁網址 `/orders/{Id}`、DB 中該買家最新一筆 Pending 訂單的 Id
  - 瀏覽器上一頁／下一頁在兩步驟間切換
  - 熱門搶購活動：入場後走完確認流程
  - 開啟舊網址 `/order-result/{id}`：本人訂單顯示明細；不存在的 GUID 與 `not-a-guid` 顯示「找不到這筆訂單」；以另一個買家帳號開啟前一人的訂單 Id 顯示「你沒有權限查看這筆訂單」，畫面與網路回應都不含訂單內容
  - 窄螢幕 390px 確認步驟版面無橫向捲動

  以 DB 查詢核對訂單確實只建立一筆。
- [ ] 6.5 strict-reviewer 與 adversarial 子代理審查，修正到通過。本 change 只改前端，不適用 hardener。
