# Tasks

> 所有指令都在容器內執行（例如 `docker compose exec web npm run test`），不在本機執行 node／npm。
>
> 本 change 無 AC 測試對應例外：所有 AC 皆以 Vitest 涵蓋，AC 的通過與否只由 Vitest 判定。
>
> 瀏覽器驗證的角色：1.1 是**前置閘門**，問題重現不了就停止 2.2、2.3。5.1、5.2 是**完成閘門**，任一項未達通過條件，本 change 不得進入 5.3 審查與歸檔。它們不取代任何 AC 測試。紀錄一律寫進本檔對應 task 下方，格式為「步驟｜量測值或畫面文字｜通過條件｜結果（PASS／FAIL）」表格，並附操作時的視窗尺寸與網路節流設定。

## AC 與測試對應

| AC | 測試檔／方法 | 任務 |
| --- | --- | --- |
| AWU-SEATMAP-LABEL-001 | `utils/seatMapLabel.test.ts`：`buildSeatMapOptions_WithDifferentInputOrders_ReturnsSameNumbering`（同一組 3 張座位圖以兩種順序輸入，斷言輸出順序與標籤完全相同，且標籤為「座位圖 {n}（{座位數} 個座位）」）、`buildSeatMapOptions_WithEmptyList_ReturnsEmpty`、`buildSeatMapOptions_WithMixedCaseIds_SortsByCodeUnitNotLocale`（小寫 a 與大寫 B 開頭的 Id，斷言依字元碼排序為 B、a，而非語系排序）；`EventCreatePage.test.ts`：`seatMapOptions_WhenApiReturnsUnsortedSeatMaps_ShowsNumberedLabelsWithoutId`（斷言選項文字依序為座位圖 1、2，且不含任何座位圖 Id 的前 8 碼）；`VenueListPage.test.ts`：`selectVenue_WhenApiReturnsUnsortedSeatMaps_ShowsSameLabelsAsCreatePage`（同一組 fixture，斷言列順序與「座位圖」欄文字與建立活動頁相同，Id 欄仍顯示完整 Id） | 2.1、3.2、3.3 |
| AWU-EVENT-VENUE-001 | `EventListPage.test.ts`（admin）：`mount_WhenVenuesLoaded_ShowsVenueNameWithoutIds`（斷言「場館」欄為場館名稱；表頭不含「座位圖」「Id」；表格文字不含場館 Id、座位圖 Id 及其前 8 碼；整頁文字不含「原始 Id」提示）；`mount_WhenVenuesRespondAfterEvents_ShowsLoadingThenVenueName`（`getVenues` 以手動控制的 deferred Promise 延後 resolve：活動列先顯示且「場館」欄為「載入中…」，resolve 後變為場館名稱）；`mount_WhenVenuesRespondBeforeEvents_ShowsVenueName`（反向順序，斷言最終文字相同） | 3.1 |
| AWU-EVENT-VENUE-002 | `EventListPage.test.ts`（admin）：`mount_WhenVenueLookupFails_StillShowsEventsWithFallbackText`（`getVenues` reject；斷言活動列仍顯示、「場館」欄為「無法載入場館名稱」、不含場館 Id、活動列表的錯誤提示未出現）；`mount_WhenEventListFailsButVenuesLoad_ShowsListErrorOnly`（`getAdminEvents` reject、`getVenues` 成功；斷言顯示活動列表錯誤提示、表格無資料列、不出現「無法載入場館名稱」，證明兩個查詢的錯誤互不混用） | 3.1 |
| AWU-EVENT-VENUE-003 | `EventListPage.test.ts`（admin）：`mount_WhenVenueNotInList_ShowsUnknownVenue`（斷言「未知場館」、不含場館 Id） | 3.1 |
| BW-NAV-RESTORE-001 | `router/scrollRestoration.test.ts`（`vi.useFakeTimers()`，可變的 `scrollHeight`／`clientHeight`）：`waitForRestorableScrollPosition_WhenRangeIsExactlyOnePixelShort_ResolvesOnFirstFrame`（可捲範圍 899、記錄位置 900，斷言第一個 frame 就 resolve，容差邊界）；`waitForRestorableScrollPosition_WhenHeightBecomesEnough_ResolvesSavedPosition`（中間狀態可捲範圍 500、scrollHeight 1000 已超過 top 900，斷言仍未 resolve；高度不足時以 `vi.advanceTimersToNextFrame()` 推進數個 frame，斷言尚未 resolve；把高度調到足夠後推進一個 frame，斷言 resolve 值 `toBe` 傳入的同一個物件 `{ left: 30, top: 900, behavior: 'instant' }`（同一參照），並 `toStrictEqual({ left: 30, top: 900, behavior: 'instant' })`（`left`、`behavior` 都原樣保留），且 `vi.getTimerCount() === 0`）；`waitForRestorableScrollPosition_WhenSavedAtFractionalBottom_ResolvesOnFirstFrame`（`scrollHeight - clientHeight` 為 1234、記錄位置 `top: 1234.5`，斷言第一個 frame 就 resolve，不等到逾時；容差拿掉時此測試會失敗）；`router/index.test.ts`：`scrollBehavior_WithSavedPosition_WaitsForContentBeforeRestoring`（呼叫 `scrollBehavior` 取得 Promise，高度足夠後 resolve 為同一個 `savedPosition` 物件） | 2.2、2.3 |
| BW-NAV-RESTORE-002 | `router/scrollRestoration.test.ts`：`waitForRestorableScrollPosition_WhenHeightNeverEnough_ResolvesSavedPositionAfter3Seconds`（高度始終不足；`vi.advanceTimersByTime(2999)` 後斷言尚未 resolve，再推進 1 毫秒後斷言 resolve 為同一個 `position` 物件，且 `vi.getTimerCount() === 0`（逾時計時器與 animation frame 皆已清除）、三個事件監聽已移除（以 `removeEventListener` spy 斷言））；`waitForRestorableScrollPosition_WhenFramesNeverRun_StillTimesOutAt3000ms`（以 `vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })` 只模擬計時器，並把 `requestAnimationFrame` 換成永不呼叫 callback 的 stub，模擬背景分頁；推進 3000 毫秒後斷言 resolve。逾時若改掛在 animation frame 內，此測試會失敗） | 2.2 |
| BW-NAV-RESTORE-003 | `router/scrollRestoration.test.ts`：`waitForRestorableScrollPosition_WhenUserScrollsWhileWaiting_ResolvesFalse`（`wheel`、`touchmove`、`keydown` 各一案例；事件後斷言 resolve 為 `false`、`vi.getTimerCount() === 0`、監聽已移除；之後把高度調足並推進 3000 毫秒，斷言 Promise 結果仍為 `false`，沒有被第二次 resolve 改變）；`waitForRestorableScrollPosition_WhenCalledAgainWhileWaiting_ResolvesPreviousFalse`（等待中再次呼叫，斷言前一個 resolve 為 `false`、只剩最新等待的 2 個計時器，且最新等待在高度足夠時 resolve 其位置）；`cancelPendingScrollRestoration_WhileWaiting_ResolvesFalseAndCleansUp`（等待中呼叫取消，斷言 resolve 為 `false` 且 `vi.getTimerCount() === 0`）；`router/index.test.ts`：`scrollBehavior_WhenNextNavigationCompletesWhileWaiting_ResolvesPreviousToFalse`（依序：① `scrollBehavior(A, X, savedA)` 取得 `p1`，高度不足所以仍在等；② 呼叫 `scrollBehavior(B, A, null)`，斷言同步回傳 `{ top: 0 }`；③ 斷言 `p1` resolve 為 `false`；④ 把高度調足並推進 frame 與 3000 毫秒，斷言 `p1` 仍為 `false`。把 `scrollBehavior` 開頭的取消呼叫拿掉，③ 就會失敗）；`scrollBehavior_WhenNavigatingRapidlyWithSavedPositions_OnlyLatestRestores`（依序以 `savedA`、`savedB`、`savedC` 呼叫三次，斷言 `p1`、`p2` 為 `false`；高度調足後 `p3` resolve 為 `savedC`，且 `vi.getTimerCount() === 0`）；`scrollBehavior_WhenOnlyStepChangesWhileWaiting_ResolvesPreviousToFalse`（等待中呼叫只改 `step` 的導覽，斷言回傳 `false` 且前一個 waiter resolve 為 `false`）；`scrollBehavior_WhenNavigatingToCurrentPageWhileWaiting_ResolvesPreviousToFalse`（模擬 vue-router 重複導覽的呼叫方式 `handleScroll(from, from, true)`：① `scrollBehavior(A, X, savedA)` 取得 `p1`；② `scrollBehavior(A, A, null)`，斷言同步回傳 `{ top: 0 }`；③ 斷言 `p1` resolve 為 `false`；④ 把高度調足並推進 3000 毫秒，斷言 `p1` 仍為 `false`） | 2.2、2.3 |
| 換頁回到頁首（既有） | `router/index.test.ts`：`scrollBehavior_WithOrWithoutSavedPosition_RestoresSavedOrScrollsToTop` 改寫：無 `savedPosition` 仍同步回傳 `{ top: 0 }`；有 `savedPosition` 改為斷言回傳 Promise，並 resolve 為同一個 `savedPosition` 物件 | 2.3 |
| BW-NAV-STEP-001（既有） | `router/index.test.ts`：`scrollBehavior_WhenOnlyStepQueryChanges_DoesNotScroll` 保留 `false` 斷言；反例中有 `saved` 的案例改為 `await` 後斷言等於 `saved` | 2.3 |

## 1. 修改前量測

- [x] 1.1 實作捲動修改（2.3）之前，以 claude-in-chrome 重現捲動問題：以有足夠訂單的買家帳號開啟「我的訂單」（訂單不足以捲動時，以縮小視窗高度等方式讓頁面可捲動，並記錄所用方式），捲到 `scrollY ≥ 600`，記錄 `scrollY`；點進一筆訂單明細，再按瀏覽器上一頁，等 API 回應完成後記錄 `scrollY`。驗證：記錄兩個數值；若返回後 `scrollY` 與離開前相差不到 50px（問題無法重現），停止 2.2、2.3 並回報使用者。此紀錄同時是 5.1 量測的反向驗證：證明同一量測在未修正時會判定失敗。
  - 完成紀錄（2026-10-15，修改前程式碼）：帳號 `seed-admin-test@example.com` 原只有 3 筆訂單，頁面可捲範圍不足（窄視窗下最多 486px），經使用者同意，以同一 session 呼叫下單 API 對「河濱冬日音樂祭／草坪自由站位」（計數制、不佔座位）建立 12 筆數量 1 的訂單並立即取消，共 15 筆訂單、頁高 934px。視窗外框 1000×400（viewport 984×215），網路不節流。
    | 步驟 | 量測值 | 通過條件 | 結果 |
    | --- | --- | --- | --- |
    | 捲動後點入訂單明細前 | `scrollY` = 650（可捲上限 719） | ≥ 600 | PASS |
    | 上一頁返回，300 ms～3.1 秒間每 300 ms 取樣 | `scrollY` 始終 = 65，頁高 934 | 與離開前相差 ≥ 50px 即視為重現 | 重現（差 585px） |
    - 另在 viewport 984×515 時量到 419 → 65，同樣重現，但 `scrollY` 未達 600，不列為正式紀錄。

## 2. 共用函式與捲動還原

- [x] 2.1 新增 `web/src/utils/seatMapLabel.ts`（純函式，依 Id 字串遞增排序後產生「座位圖 {序號}（{座位數} 個座位）」）與 `seatMapLabel.test.ts`。驗證：AWU-SEATMAP-LABEL-001 的單元測試通過；把排序拿掉（照輸入順序編號）時 `WithDifferentInputOrders` 測試會失敗。
  - 完成紀錄：2 tests 通過；反向驗證已執行，移除 `.sort` 後 `WithDifferentInputOrders` 失敗，還原後通過。
- [x] 2.2 新增 `web/src/router/scrollRestoration.ts`（design.md 決策 3：animation frame 只檢查高度；獨立 `setTimeout` 3000 毫秒逾時；`wheel`／`touchmove`／`keydown` 中止；匯出 `cancelPendingScrollRestoration()`；任一結果都清除監聽、計時器、frame 與 waiter 參照；resolve 原 `position` 物件）與 `scrollRestoration.test.ts`。`scrollRestoration.test.ts` 與 `router/index.test.ts` 的 `afterEach` 都要呼叫 `cancelPendingScrollRestoration()` 與 `vi.useRealTimers()`，並還原 `scrollHeight`／`clientHeight`／`requestAnimationFrame` 的 stub，避免模組層級的 waiter 在案例之間互相汙染 `vi.getTimerCount()` 的斷言。驗證：BW-NAV-RESTORE-001～003 的單元測試通過。反向驗證：把等待改成立即 resolve 時，RESTORE-001 的「尚未 resolve」斷言會失敗；把逾時改掛在 animation frame 內時，`WhenFramesNeverRun_StillTimesOutAt3000ms` 會失敗。
  - 完成紀錄：`scrollRestoration.test.ts` 8 tests 通過（5.3 審查後補案例，現為 10 tests）（wheel／touchmove／keydown 以 `it.each` 各一案例）。反向驗證已執行並還原：改成立即 resolve 時 RESTORE-001「尚未 resolve」等 6 個測試失敗；逾時改掛在 animation frame 內時 `WhenFramesNeverRun_StillTimesOutAt3000ms`（與 `WhenHeightNeverEnough`）失敗；另將容差改為 0 時 `WhenSavedAtFractionalBottom` 失敗。
- [x] 2.3 修改 `web/src/router/index.ts` 的 `scrollBehavior`：每次呼叫先呼叫 `cancelPendingScrollRestoration()`；有 `savedPosition` 且非只改 `step` 時回傳 2.2 的 Promise；其餘回傳值不變。同步更新第 18–19 行「實際會停在頁首附近……收尾階段不另外處理」的註解。依上表改寫 `router/index.test.ts` 既有兩個測試，並新增五個測試。驗證：`docker compose exec web npm run test -- router` 通過；反向驗證：拿掉開頭的取消呼叫時，`WhenNextNavigationCompletesWhileWaiting`、`WhenOnlyStepChangesWhileWaiting`、`WhenNavigatingToCurrentPageWhileWaiting` 會失敗（`WhenNavigatingRapidly` 三次都帶 `savedPosition`，前一個 waiter 已由 `waitForRestorableScrollPosition` 自行取消，見 design 決策 3 第 4 點，此案例驗證的是「最後一次導覽勝出」）。
  - 完成紀錄：`router` 相關 4 檔 50 tests 通過。既有兩個 scroll 測試移到新的 `router scrollBehavior` describe（需要 fake timers 與高度 stub）並依上表改寫，新增五個測試。**偏差**：拿掉開頭取消呼叫時，失敗的是 `WhenNextNavigationCompletesWhileWaiting`、`WhenOnlyStepChangesWhileWaiting`、`WhenNavigatingToCurrentPageWhileWaiting`；`WhenNavigatingRapidly` 仍通過，因為三次呼叫都帶 `savedPosition`，design 決策 3 第 4 點要求 `waitForRestorableScrollPosition` 開始等待前也先取消前一個 waiter。原本 task 的反向驗證預期與 design 不一致；經使用者同意（2026-10-15）已改寫上方驗證敘述以 design 為準，並在 `WhenNavigatingRapidly` 測試註解說明。

## 3. 後台畫面

- [x] 3.1 `web/src/pages/admin/EventListPage.vue`：`onMounted` 另外啟動獨立的 `loadVenueNames()`（自己 try/catch，不與 `loadEvents` 用 `Promise.all` 串接），「場館 Id」欄改為「場館」，並依 design.md 決策 1 顯示「載入中…」／名稱／「未知場館」／「無法載入場館名稱」，移除「座位圖 Id」欄；移除頁首提示「活動列表的場館／座位圖欄位顯示原始 Id，無法顯示名稱。」（第 126 行，改完後與新行為矛盾）；新增 AWU-EVENT-VENUE-001～003 測試。驗證：測試通過；AWU-EVENT-VENUE-001 測試另斷言整頁文字不含「原始 Id」；既有斷言 Id 欄的測試一併改寫並在完成紀錄列出。
  - 完成紀錄：新增 6 個 AWU-EVENT-VENUE 測試，`EventListPage.test.ts` 21 tests 通過。既有測試沒有斷言場館／座位圖 Id 欄，無需改寫；頁首提示與其 `.hint` 樣式一併移除。
- [x] 3.2 `web/src/pages/admin/EventCreatePage.vue`：座位圖下拉選項改用 2.1 的函式；新增或改寫測試。驗證：AWU-SEATMAP-LABEL-001 的 `EventCreatePage.test.ts` 測試通過，既有「選擇場館後座位圖下拉選單隨之更新」「切換場館後清除已選座位圖」測試仍通過。
- [x] 3.3 `web/src/pages/admin/VenueListPage.vue`：座位圖摘要表的列改用 2.1 的排序，新增「座位圖」欄；新增 `VenueListPage.test.ts`（目前沒有此測試檔）。驗證：AWU-SEATMAP-LABEL-001 的 `VenueListPage.test.ts` 測試通過。
  - 完成紀錄：`EventCreatePage.test.ts` 21 tests（含新增 1 個）與 `VenueListPage.test.ts` 1 test 通過；兩檔使用相同的座位圖 fixture 與相同的預期標籤。

## 4. 全套驗證

- [x] 4.1 容器內執行 `npm run test`、`npx eslint src`、`npm run build`、`openspec validate frontend-minor-polish --strict`。驗證：全部通過，記錄測試檔數與測試數，並確認沒有 skipped。
  - 完成紀錄：`npm run test` 47 個測試檔、654 tests 全數通過，無 skipped；`npx eslint src` exit 0；`npm run build`（含 vue-tsc）成功；`openspec validate frontend-minor-polish --strict` 通過。

## 5. 修改後量測、走查與審查

- [x] 5.1 修改後重複 1.1 的步驟，另以 DevTools 將網路節流設為 Slow 4G 再做一次。驗證：兩次返回後的 `scrollY` 與離開前相差都不超過 50px，並記錄從按上一頁到捲動完成的大約時間。另做一次：Slow 4G 下按上一頁後、API 回應前以滑鼠滾輪捲動一次，斷言之後頁面沒有被捲回記錄位置（BW-NAV-RESTORE-003）。通過條件：兩次返回的 `scrollY` 差值與滾輪中止這三項全部成立。量到的時間只做紀錄，供 design 決策 3 評估 3 秒是否足夠，不列入通過條件。原因：這個時間從按上一頁算起，包含路由載入元件與頁面 API 的耗時，和 spec 的「最多 3 秒」（從 `scrollBehavior` 開始等待算起）不是同一個量。3 秒上限由 BW-NAV-RESTORE-002 的 Vitest 判定（2999 毫秒尚未 resolve、3000 毫秒 resolve，以及 animation frame 不執行時仍在 3000 毫秒逾時）。
  - 完成紀錄（2026-10-15，修改後程式碼）：環境同 1.1（`seed-admin-test@example.com`、15 筆訂單、頁高 934px、viewport 984×215）；DevTools 以獨立視窗開啟，不影響 viewport。量測方式：`/orders` 捲到 650 → 點畫面內第一筆訂單連結 → 明細頁停留後 `history.back()` → 取樣 `scrollY`。
    | 項目 | 量測值 | 通過條件 | 結果 |
    | --- | --- | --- | --- |
    | 不節流 | 離開前 650，返回後 650（每 300ms 取樣 12 次皆 650） | 差 ≤ 50px | 通過（差 0） |
    | Slow 4G（探測請求 592ms，確認節流生效） | 離開前 650；返回後前兩次取樣頁高 280、`scrollY` 0，第三次起頁高 934、`scrollY` 650，至約 7.5 秒皆維持 | 差 ≤ 50px | 通過（差 0） |
    | Slow 4G 滾輪中止 | 返回後 217ms 以真實滑鼠滾輪向下 1 格（當下頁高 280，API 尚未回應）；約 700ms 頁高變為 934，`scrollY` 停在 65，持續取樣至 4000ms（超過 3 秒逾時）未被捲回 650 | 未捲回記錄位置 | 通過 |
  - 從按上一頁到捲動完成的時間（僅記錄）：不節流 ≤ 312ms（第一次取樣即到位）；Slow 4G 約 600–900ms（取樣間隔 300ms）。都遠低於 3 秒。
  - 備註：第一次不節流量測作廢。瀏覽紀錄殘留 1.1 的頁面，上一頁回到另一筆訂單明細，不是列表頁。重新載入 `/orders` 後重量，上表為重量結果。
- [x] 5.2 以 claude-in-chrome 走查後台：活動列表「場館」欄顯示名稱、無 GUID；建立活動頁選擇「城市音樂廳」後，座位圖選項為「座位圖 1（N 個座位）」；場館管理頁同一場館的座位圖摘要顯示相同標籤。另在活動詳情頁進出確認步驟，確認 BW-NAV-STEP-001 的不捲動行為不變。驗證：記錄每一項的實際畫面文字。通過條件：活動列表與建立活動頁下拉選單的畫面文字不含走查資料中任何場館 Id、座位圖 Id 的前 8 碼（先以 API 回應取得實際 Id 清單，再逐一以字串包含檢查 `document.body.innerText` 對應區塊，避免以通用 regex 誤判純數字內容）；兩頁同一張座位圖的標籤文字完全相同。確認步驟元件本身會移焦點並捲動，`scrollY` 變化不全來自路由，所以確認步驟的走查只記錄 `scrollY`，不列入通過條件；BW-NAV-STEP-001 由 Vitest 判定。
  - 完成紀錄（2026-10-15，Slow 4G 節流下，viewport 984×215）：先在「我的主辦方」切換至「Ticket-Hub 示範主辦方」。Id 清單取自場館管理頁（直接渲染 `getVenues`／`getVenueById` 的回應）：場館 城市音樂廳 `4c31ee75…`、河濱戶外展演場 `2e6c3c4e…`；座位圖 `a9e8d527…`（350 座）、`7ae0c42a…`（40 座）。
    | 項目 | 實際畫面文字 | 通過條件 | 結果 |
    | --- | --- | --- | --- |
    | 活動列表 | 欄位：活動名稱／開始時間／販售期間／場館／建立者／建立時間／售票狀況／操作；5 筆「場館」依序為 城市音樂廳、河濱戶外展演場、城市音樂廳、城市音樂廳、河濱戶外展演場 | `document.body.innerText` 不含上列 4 個 Id 前 8 碼 | 通過（0 筆命中） |
    | 建立活動頁 | 選「城市音樂廳」後座位圖下拉選項：「座位圖 1（350 個座位）」 | 下拉選項、表單與 `document.body.innerText` 不含 4 個 Id 前 8 碼 | 通過（0 筆命中） |
    | 場館管理頁 | 城市音樂廳：「座位圖 1（350 個座位）／a9e8d527-e18d-4b1b-8a6d-9fcec7597993／350」；河濱戶外展演場：「座位圖 1（40 個座位）／7ae0c42a-70d5-49da-aa1a-e1774b867366／40」 | 與建立活動頁同一張座位圖標籤完全相同 | 通過 |
    | 確認步驟（只記錄） | 河濱冬日音樂祭選 1 張草坪自由站位：按「下一步」前 `scrollY` 466 → `?step=confirm` 時 55（焦點 H2「確認訂單內容」）→ 瀏覽器上一頁後 55（焦點「下一步」按鈕，未被路由還原到 466） | 不列入通過條件 | 記錄 |
- [x] 5.3 strict-reviewer 與 adversarial 子代理審查，修正到通過。本 change 只改前端，不適用 hardener。驗證：最後一輪兩者皆 PASS，之後若有修改需再跑一輪。
  - 完成紀錄（2026-10-15，第 1 輪）：strict-reviewer PASS（容器內 47 檔／654 tests 全過，`vue-tsc --noEmit` 無錯誤）；adversarial 子代理 PASS（讀 vue-router 5.2.0 原始碼核對 Promise 處理、`currentRoute` 檢查與 `scrollRestoration = 'manual'`；突變 11 種皆被測試抓到，突變後已還原並比對）。兩者皆無 blocking。未被抓到的 3 種突變（`localeCompare` 排序、兩個查詢改串行、`removeEventListener` 傳不同參照）列為建議，未修改。
  - 第 2 輪（補 a/b/c 三個弱測試、改 2.3 敘述後）：兩者 PASS；adversarial 另發現 waitFor 內部取消呼叫無測試 → 補 `WhenCalledAgainWhileWaiting_ResolvesPreviousFalse`。
  - 第 3 輪：strict-reviewer PASS；adversarial FAIL（B1：三個測試檔的 fixture Id 順序剛好等於座位數遞減，依座位數排序的突變抓不到）。修正：`seatMapLabel.test.ts` 三筆 fixture 座位數改為不隨 Id 單調增減（同時抓遞增與遞減突變），兩個頁面測試的兩筆 fixture 改為依 Id 排序時座位數遞增（0a… 40、f0… 120，抓依座位數遞減的突變；兩筆無法同時防兩方向，遞增突變由單元測試抓）；另補 S1（listener 移除比對 capture 選項）、S2（容差邊界 range = top − 1 的案例）。S3（舊還原在新導覽「完成」時才放棄）經使用者確認維持現行為，spec BW-NAV-RESTORE-003 改寫為「導覽完成」，design 決策 3 補上取捨並更正「改導向不會完成」的錯誤敘述。四種突變皆確認會被抓到。
  - 第 4 輪：strict-reviewer PASS；adversarial FAIL（B1：`hasRestorableHeight` 忽略 clientHeight 的突變抓不到；B2：spec Requirement 本文仍是「又發生任何導覽」，與 Scenario 003 矛盾）。修正：RESTORE-001 中間狀態改為可捲範圍 500（scrollHeight 1000 ≥ top 900 但可捲範圍不足），突變確認會被抓到；spec 本文改為「又有任何導覽完成（被 guard 中止的不算）」；另補 S1（測試改名 `…WhenNextNavigationCompletesWhileWaiting…`）、S2（design Context 寫明 `currentRoute` 切換後才受保護）、S3（更正第 3 輪 fixture 敘述）、S4（design Risks 補按鍵重複／觸控板慣性滾動）。
  - 第 5 輪：strict-reviewer PASS；adversarial PASS（18 個突變中 17 個被抓到，存活的 M4「無條件清除 waiter 參照」為等價突變）。建議處理：AC 對應表補上後續新增的 3 個測試、2.2 測試數更新；「場館名稱為空字串會留白」不處理，因 Domain `Venue` 建構子以 `IsNullOrWhiteSpace` 拒絕、`CreateVenueRequestValidator` 亦 `NotEmpty`，不會出現空名稱。主 spec `buyer-web-ui/spec.md` 的「已知限制」句於歸檔同步時移除。本輪後僅修改 tasks.md 紀錄，未改程式碼與測試。
