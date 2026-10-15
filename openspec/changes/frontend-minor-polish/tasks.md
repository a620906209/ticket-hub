# Tasks

> 所有指令都在容器內執行（例如 `docker compose exec web npm run test`），不在本機執行 node／npm。
>
> 本 change 無 AC 測試對應例外：所有 AC 皆以 Vitest 涵蓋，AC 的通過與否只由 Vitest 判定。
>
> 瀏覽器驗證的角色：1.1 是**前置閘門**，問題重現不了就停止 2.2、2.3。5.1、5.2 是**完成閘門**，任一項未達通過條件，本 change 不得進入 5.3 審查與歸檔。它們不取代任何 AC 測試。紀錄一律寫進本檔對應 task 下方，格式為「步驟｜量測值或畫面文字｜通過條件｜結果（PASS／FAIL）」表格，並附操作時的視窗尺寸與網路節流設定。

## AC 與測試對應

| AC | 測試檔／方法 | 任務 |
| --- | --- | --- |
| AWU-SEATMAP-LABEL-001 | `utils/seatMapLabel.test.ts`：`buildSeatMapOptions_WithDifferentInputOrders_ReturnsSameNumbering`（同一組 3 張座位圖以兩種順序輸入，斷言輸出順序與標籤完全相同，且標籤為「座位圖 {n}（{座位數} 個座位）」）、`buildSeatMapOptions_WithEmptyList_ReturnsEmpty`；`EventCreatePage.test.ts`：`seatMapOptions_WhenApiReturnsUnsortedSeatMaps_ShowsNumberedLabelsWithoutId`（斷言選項文字依序為座位圖 1、2，且不含任何座位圖 Id 的前 8 碼）；`VenueListPage.test.ts`：`selectVenue_WhenApiReturnsUnsortedSeatMaps_ShowsSameLabelsAsCreatePage`（同一組 fixture，斷言列順序與「座位圖」欄文字與建立活動頁相同，Id 欄仍顯示完整 Id） | 2.1、3.2、3.3 |
| AWU-EVENT-VENUE-001 | `EventListPage.test.ts`（admin）：`mount_WhenVenuesLoaded_ShowsVenueNameWithoutIds`（斷言「場館」欄為場館名稱；表頭不含「座位圖」「Id」；表格文字不含場館 Id、座位圖 Id 及其前 8 碼；整頁文字不含「原始 Id」提示）；`mount_WhenVenuesRespondAfterEvents_ShowsLoadingThenVenueName`（`getVenues` 以手動控制的 deferred Promise 延後 resolve：活動列先顯示且「場館」欄為「載入中…」，resolve 後變為場館名稱）；`mount_WhenVenuesRespondBeforeEvents_ShowsVenueName`（反向順序，斷言最終文字相同） | 3.1 |
| AWU-EVENT-VENUE-002 | `EventListPage.test.ts`（admin）：`mount_WhenVenueLookupFails_StillShowsEventsWithFallbackText`（`getVenues` reject；斷言活動列仍顯示、「場館」欄為「無法載入場館名稱」、不含場館 Id、活動列表的錯誤提示未出現）；`mount_WhenEventListFailsButVenuesLoad_ShowsListErrorOnly`（`getAdminEvents` reject、`getVenues` 成功；斷言顯示活動列表錯誤提示、表格無資料列、不出現「無法載入場館名稱」，證明兩個查詢的錯誤互不混用） | 3.1 |
| AWU-EVENT-VENUE-003 | `EventListPage.test.ts`（admin）：`mount_WhenVenueNotInList_ShowsUnknownVenue`（斷言「未知場館」、不含場館 Id） | 3.1 |
| BW-NAV-RESTORE-001 | `router/scrollRestoration.test.ts`（`vi.useFakeTimers()`，可變的 `scrollHeight`／`innerHeight`）：`waitForRestorableScrollPosition_WhenHeightBecomesEnough_ResolvesSavedPosition`（高度不足時以 `vi.advanceTimersToNextFrame()` 推進數個 frame，斷言尚未 resolve；把高度調到足夠後推進一個 frame，斷言 resolve 值 `toBe` 傳入的同一個物件 `{ left: 30, top: 900, behavior: 'instant' }`（同一參照），並 `toStrictEqual({ left: 30, top: 900, behavior: 'instant' })`（`left`、`behavior` 都原樣保留），且 `vi.getTimerCount() === 0`）；`router/index.test.ts`：`scrollBehavior_WithSavedPosition_WaitsForContentBeforeRestoring`（呼叫 `scrollBehavior` 取得 Promise，高度足夠後 resolve 為同一個 `savedPosition` 物件） | 2.2、2.3 |
| BW-NAV-RESTORE-002 | `router/scrollRestoration.test.ts`：`waitForRestorableScrollPosition_WhenHeightNeverEnough_ResolvesSavedPositionAfter3Seconds`（高度始終不足；`vi.advanceTimersByTime(2999)` 後斷言尚未 resolve，再推進 1 毫秒後斷言 resolve 為同一個 `position` 物件，且 `vi.getTimerCount() === 0`（逾時計時器與 animation frame 皆已清除）、三個事件監聽已移除（以 `removeEventListener` spy 斷言））；`waitForRestorableScrollPosition_WhenFramesNeverRun_StillTimesOutAt3000ms`（以 `vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })` 只模擬計時器，並把 `requestAnimationFrame` 換成永不呼叫 callback 的 stub，模擬背景分頁；推進 3000 毫秒後斷言 resolve。逾時若改掛在 animation frame 內，此測試會失敗） | 2.2 |
| BW-NAV-RESTORE-003 | `router/scrollRestoration.test.ts`：`waitForRestorableScrollPosition_WhenUserScrollsWhileWaiting_ResolvesFalse`（`wheel`、`touchmove`、`keydown` 各一案例；事件後斷言 resolve 為 `false`、`vi.getTimerCount() === 0`、監聽已移除；之後把高度調足並推進 3000 毫秒，斷言 Promise 結果仍為 `false`，沒有被第二次 resolve 改變）；`cancelPendingScrollRestoration_WhileWaiting_ResolvesFalseAndCleansUp`（等待中呼叫取消，斷言 resolve 為 `false` 且 `vi.getTimerCount() === 0`）；`router/index.test.ts`：`scrollBehavior_WhenNextNavigationStartsWhileWaiting_ResolvesPreviousToFalse`（依序：① `scrollBehavior(A, X, savedA)` 取得 `p1`，高度不足所以仍在等；② 呼叫 `scrollBehavior(B, A, null)`，斷言同步回傳 `{ top: 0 }`；③ 斷言 `p1` resolve 為 `false`；④ 把高度調足並推進 frame 與 3000 毫秒，斷言 `p1` 仍為 `false`。把 `scrollBehavior` 開頭的取消呼叫拿掉，③ 就會失敗）；`scrollBehavior_WhenNavigatingRapidlyWithSavedPositions_OnlyLatestRestores`（依序以 `savedA`、`savedB`、`savedC` 呼叫三次，斷言 `p1`、`p2` 為 `false`；高度調足後 `p3` resolve 為 `savedC`，且 `vi.getTimerCount() === 0`）；`scrollBehavior_WhenOnlyStepChangesWhileWaiting_ResolvesPreviousToFalse`（等待中呼叫只改 `step` 的導覽，斷言回傳 `false` 且前一個 waiter resolve 為 `false`）；`scrollBehavior_WhenNavigatingToCurrentPageWhileWaiting_ResolvesPreviousToFalse`（模擬 vue-router 重複導覽的呼叫方式 `handleScroll(from, from, true)`：① `scrollBehavior(A, X, savedA)` 取得 `p1`；② `scrollBehavior(A, A, null)`，斷言同步回傳 `{ top: 0 }`；③ 斷言 `p1` resolve 為 `false`；④ 把高度調足並推進 3000 毫秒，斷言 `p1` 仍為 `false`） | 2.2、2.3 |
| 換頁回到頁首（既有） | `router/index.test.ts`：`scrollBehavior_WithOrWithoutSavedPosition_RestoresSavedOrScrollsToTop` 改寫：無 `savedPosition` 仍同步回傳 `{ top: 0 }`；有 `savedPosition` 改為斷言回傳 Promise，並 resolve 為同一個 `savedPosition` 物件 | 2.3 |
| BW-NAV-STEP-001（既有） | `router/index.test.ts`：`scrollBehavior_WhenOnlyStepQueryChanges_DoesNotScroll` 保留 `false` 斷言；反例中有 `saved` 的案例改為 `await` 後斷言等於 `saved` | 2.3 |

## 1. 修改前量測

- [ ] 1.1 實作捲動修改（2.3）之前，以 claude-in-chrome 重現捲動問題：以有足夠訂單的買家帳號開啟「我的訂單」（訂單不足以捲動時，以縮小視窗高度等方式讓頁面可捲動，並記錄所用方式），捲到 `scrollY ≥ 600`，記錄 `scrollY`；點進一筆訂單明細，再按瀏覽器上一頁，等 API 回應完成後記錄 `scrollY`。驗證：記錄兩個數值；若返回後 `scrollY` 與離開前相差不到 50px（問題無法重現），停止 2.2、2.3 並回報使用者。此紀錄同時是 5.1 量測的反向驗證：證明同一量測在未修正時會判定失敗。

## 2. 共用函式與捲動還原

- [ ] 2.1 新增 `web/src/utils/seatMapLabel.ts`（純函式，依 Id 字串遞增排序後產生「座位圖 {序號}（{座位數} 個座位）」）與 `seatMapLabel.test.ts`。驗證：AWU-SEATMAP-LABEL-001 的單元測試通過；把排序拿掉（照輸入順序編號）時 `WithDifferentInputOrders` 測試會失敗。
- [ ] 2.2 新增 `web/src/router/scrollRestoration.ts`（design.md 決策 3：animation frame 只檢查高度；獨立 `setTimeout` 3000 毫秒逾時；`wheel`／`touchmove`／`keydown` 中止；匯出 `cancelPendingScrollRestoration()`；任一結果都清除監聽、計時器、frame 與 waiter 參照；resolve 原 `position` 物件）與 `scrollRestoration.test.ts`。`scrollRestoration.test.ts` 與 `router/index.test.ts` 的 `afterEach` 都要呼叫 `cancelPendingScrollRestoration()` 與 `vi.useRealTimers()`，並還原 `scrollHeight`／`innerHeight`／`requestAnimationFrame` 的 stub，避免模組層級的 waiter 在案例之間互相汙染 `vi.getTimerCount()` 的斷言。驗證：BW-NAV-RESTORE-001～003 的單元測試通過。反向驗證：把等待改成立即 resolve 時，RESTORE-001 的「尚未 resolve」斷言會失敗；把逾時改掛在 animation frame 內時，`WhenFramesNeverRun_StillTimesOutAt3000ms` 會失敗。
- [ ] 2.3 修改 `web/src/router/index.ts` 的 `scrollBehavior`：每次呼叫先呼叫 `cancelPendingScrollRestoration()`；有 `savedPosition` 且非只改 `step` 時回傳 2.2 的 Promise；其餘回傳值不變。同步更新第 18–19 行「實際會停在頁首附近……收尾階段不另外處理」的註解。依上表改寫 `router/index.test.ts` 既有兩個測試，並新增五個測試。驗證：`docker compose exec web npm run test -- router` 通過；反向驗證：拿掉開頭的取消呼叫時，`WhenNextNavigationStartsWhileWaiting` 與 `WhenNavigatingRapidly` 會失敗。

## 3. 後台畫面

- [ ] 3.1 `web/src/pages/admin/EventListPage.vue`：`onMounted` 另外啟動獨立的 `loadVenueNames()`（自己 try/catch，不與 `loadEvents` 用 `Promise.all` 串接），「場館 Id」欄改為「場館」，並依 design.md 決策 1 顯示「載入中…」／名稱／「未知場館」／「無法載入場館名稱」，移除「座位圖 Id」欄；移除頁首提示「活動列表的場館／座位圖欄位顯示原始 Id，無法顯示名稱。」（第 126 行，改完後與新行為矛盾）；新增 AWU-EVENT-VENUE-001～003 測試。驗證：測試通過；AWU-EVENT-VENUE-001 測試另斷言整頁文字不含「原始 Id」；既有斷言 Id 欄的測試一併改寫並在完成紀錄列出。
- [ ] 3.2 `web/src/pages/admin/EventCreatePage.vue`：座位圖下拉選項改用 2.1 的函式；新增或改寫測試。驗證：AWU-SEATMAP-LABEL-001 的 `EventCreatePage.test.ts` 測試通過，既有「選擇場館後座位圖下拉選單隨之更新」「切換場館後清除已選座位圖」測試仍通過。
- [ ] 3.3 `web/src/pages/admin/VenueListPage.vue`：座位圖摘要表的列改用 2.1 的排序，新增「座位圖」欄；新增 `VenueListPage.test.ts`（目前沒有此測試檔）。驗證：AWU-SEATMAP-LABEL-001 的 `VenueListPage.test.ts` 測試通過。

## 4. 全套驗證

- [ ] 4.1 容器內執行 `npm run test`、`npx eslint src`、`npm run build`、`openspec validate frontend-minor-polish --strict`。驗證：全部通過，記錄測試檔數與測試數，並確認沒有 skipped。

## 5. 修改後量測、走查與審查

- [ ] 5.1 修改後重複 1.1 的步驟，另以 DevTools 將網路節流設為 Slow 4G 再做一次。驗證：兩次返回後的 `scrollY` 與離開前相差都不超過 50px，並記錄從按上一頁到捲動完成的大約時間。另做一次：Slow 4G 下按上一頁後、API 回應前以滑鼠滾輪捲動一次，斷言之後頁面沒有被捲回記錄位置（BW-NAV-RESTORE-003）。通過條件：兩次返回的 `scrollY` 差值與滾輪中止這三項全部成立。量到的時間只做紀錄，供 design 決策 3 評估 3 秒是否足夠，不列入通過條件。原因：這個時間從按上一頁算起，包含路由載入元件與頁面 API 的耗時，和 spec 的「最多 3 秒」（從 `scrollBehavior` 開始等待算起）不是同一個量。3 秒上限由 BW-NAV-RESTORE-002 的 Vitest 判定（2999 毫秒尚未 resolve、3000 毫秒 resolve，以及 animation frame 不執行時仍在 3000 毫秒逾時）。
- [ ] 5.2 以 claude-in-chrome 走查後台：活動列表「場館」欄顯示名稱、無 GUID；建立活動頁選擇「城市音樂廳」後，座位圖選項為「座位圖 1（N 個座位）」；場館管理頁同一場館的座位圖摘要顯示相同標籤。另在活動詳情頁進出確認步驟，確認 BW-NAV-STEP-001 的不捲動行為不變。驗證：記錄每一項的實際畫面文字。通過條件：活動列表與建立活動頁下拉選單的畫面文字不含走查資料中任何場館 Id、座位圖 Id 的前 8 碼（先以 API 回應取得實際 Id 清單，再逐一以字串包含檢查 `document.body.innerText` 對應區塊，避免以通用 regex 誤判純數字內容）；兩頁同一張座位圖的標籤文字完全相同。確認步驟元件本身會移焦點並捲動，`scrollY` 變化不全來自路由，所以確認步驟的走查只記錄 `scrollY`，不列入通過條件；BW-NAV-STEP-001 由 Vitest 判定。
- [ ] 5.3 strict-reviewer 與 adversarial 子代理審查，修正到通過。本 change 只改前端，不適用 hardener。驗證：最後一輪兩者皆 PASS，之後若有修改需再跑一輪。
