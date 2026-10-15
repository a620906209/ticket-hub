# Design

## Context

- 後台活動列表 `web/src/pages/admin/EventListPage.vue` 以 `getAdminEvents()` 載入，`AdminEventSummary` 只帶 `venueId`、`seatMapId`，沒有名稱，所以第 163–164 行直接顯示 GUID。場館不分主辦方（`Venue` 沒有 `OrganizerId`），`getVenues()`（`GET /api/admin/venues`）會回傳所有場館的 Id 與名稱，可以用來對照。
- 座位圖在 Domain（`SeatMap`）只有 `Id`、`VenueId`、座位，沒有名稱或建立時間。`SeatMapRepository` 以 `Where(m => m.VenueId == venueId).ToListAsync()` 查詢，**沒有 `OrderBy`**，所以 `getVenueById` 回傳的 `seatMaps` 順序沒有保證。
- 座位圖出現在兩個畫面：建立活動頁的下拉選單（`EventCreatePage.vue:220`，顯示 Id 前 8 碼）與場館管理頁的座位圖摘要表（`VenueListPage.vue:234`，顯示完整 Id）。
- 捲動：`web/src/router/index.ts:22` 的 `scrollBehavior` 在只改 `step` 時回 `false`，其餘回 `savedPosition ?? { top: 0 }`。已確認安裝的 vue-router 為 5.2.0，其 `handleScroll`（`node_modules/vue-router/dist/vue-router.js:1469-1474`）會 `await` `scrollBehavior` 的回傳值，並在捲動前檢查 `to === currentRoute.value`：導覽被新的導覽取代時，舊的 Promise 結果不會捲動。目前頁面在 `onMounted` 才開始查 API，導覽完成時內容還沒渲染，`savedPosition` 超出可捲範圍，瀏覽器就捲到當下的最低處。
- 頁面捲動容器是 `window`（`web/src` 沒有設定 `overflow-y: auto` 或 `100vh` 的捲動容器）。

## Goals / Non-Goals

**Goals:**

- 活動列表、建立活動頁的下拉選單不再顯示 GUID。
- 同一張座位圖在兩個畫面的標籤一致且不隨 API 回傳順序跳動。
- 上一頁／下一頁回到非同步載入的頁面時，還原到離開前的位置。

**Non-Goals:**

- 不改後端、API、資料庫。座位圖不加名稱欄位（使用者已選定只改前端）。
- 不在活動列表顯示座位圖資訊（需逐場館查明細，N 次請求）。
- 不處理各頁面自己的捲動或焦點邏輯（例如活動詳情頁確認步驟的焦點），也不改 `step` query 的不捲動規則。
- 不處理以拖曳捲軸方式捲動時的中止判斷（見 Risks）。

## Decisions

### 決策 1：活動列表以場地查詢 API 對照場館名稱，兩個查詢分開處理失敗

`onMounted` 同時啟動兩個**各自獨立的** async 函式：既有的 `loadEvents`（不變，自己 try/catch，失敗設 `listError`）與新增的 `loadVenueNames`（自己 try/catch）。兩者不用 `Promise.all`／`allSettled` 串在一起，也不互相 await。`loadVenueNames` 成功時把結果存成 `venueNamesById`（`Map<string, string>`），失敗時設 `isVenueLookupFailed`。它**不影響**活動列表的顯示，也不把場地查詢的錯誤混進 `listError`。

- **回應順序**：兩個函式寫入不同的 ref，「場館」欄是由 `events` 與 `venueNamesById` 推導出的顯示值。所以不論哪個回應先到，最後畫面都一樣。場館名稱還沒回來時，「場館」欄顯示「載入中…」。
- **重複載入**：兩個函式都只在 `onMounted` 呼叫一次，頁面沒有手動重新整理，建立票種也只刷新票種清單，不重載活動列表（已核對 `EventListPage.vue:34`）。因此不存在同一頁面內舊回應覆蓋新結果的情況，不另加過期回應防護。日後若加入重新整理功能，需在當時的 change 補上。
- **活動列表查詢失敗**：沿用既有行為，顯示 `listError`、表格為空。場地查詢的結果不影響這個行為。「場館」欄顯示規則：

- 查詢成功且有對應：場館名稱。
- 查詢成功但找不到：「未知場館」。
- 查詢失敗：「無法載入場館名稱」。

「座位圖 Id」欄直接移除。場館名稱以 template 插值顯示，不用 `v-html`。

替代方案：後端 `AdminEventSummaryDto` 加 `VenueName`。可一次查詢、不需前端對照，但要改後端與測試，使用者已選定只改前端。

### 決策 2：座位圖標籤由共用函式產生，依 Id 字串排序後編號

新增 `web/src/utils/seatMapLabel.ts`，提供把同一場館的 `SeatMapSummary[]` 轉成「依 Id 遞增排序、附標籤」清單的純函式。排序用字串的 `<`／`>` 比較 API 回傳的 Id 原字串（不轉大小寫；不用 `localeCompare`，避免受執行環境語系影響。API 一律回傳小寫 GUID，兩頁使用同一個 API，所以順序一致），標籤為「座位圖 {序號}（{座位數} 個座位）」。

- `EventCreatePage.vue`：下拉選項改用這個清單，選項順序即序號順序，`value` 仍是座位圖 Id。
- `VenueListPage.vue`：座位圖摘要表的列改用這個清單的順序，新增「座位圖」欄顯示標籤，保留「座位圖 Id」與「座位數」欄（管理頁需要 Id 供對照，維持既有規格）。

替代方案：照 API 回傳順序編號。因後端沒有 `OrderBy`，同一張座位圖的序號可能在兩次查詢間改變，否決。

### 決策 3：捲動還原改為等待頁面高度足夠，最多 3 秒

新增 `web/src/router/scrollRestoration.ts`，匯出 `waitForRestorableScrollPosition(position)`，回傳 `Promise<ScrollPosition | false>`。模組內同一時間最多只有一個等待中的 waiter：

1. **高度檢查**：每個 animation frame 檢查 `document.documentElement.scrollHeight - window.innerHeight >= position.top`，成立就 resolve。animation frame 只負責高度檢查，不負責逾時。
2. **逾時**：開始等待時另以 `setTimeout(…, 3000)` 設獨立計時器，觸發時 resolve（交由瀏覽器停在可捲到的最低處）。逾時不依賴 animation frame 是否執行，所以畫面不更新（背景分頁、裝置忙碌）也不會延長等待。
3. **使用者中止**：等待期間收到 `wheel`、`touchmove` 或 `keydown` 事件，視為使用者自行捲動，resolve `false`。
4. **新導覽取代**：匯出 `cancelPendingScrollRestoration()`，把目前等待中的 waiter 以 `false` resolve。`waitForRestorableScrollPosition` 開始新等待前也先呼叫它。
5. **清理**：上述任一結果發生時，同時移除三個事件監聽、`clearTimeout`、`cancelAnimationFrame`，並清除模組的 waiter 參照；之後同一個 waiter 的其他觸發來源不再有作用（每個 waiter 只 resolve 一次）。
6. **回傳值契約**：高度足夠或逾時時 resolve **原本傳入的 `position` 物件本身**（同一個參照，不重建、不刪減欄位），`left`、`behavior` 等欄位原樣交給 vue-router 的 `scrollToPosition`。本 change 只以 `top` 判斷高度是否足夠；水平方向不做等待（頁面沒有橫向捲動內容）。

`scrollBehavior` 改為：**每次呼叫都先呼叫 `cancelPendingScrollRestoration()`**，之後只改 `step` → `false`（不變）；沒有 `savedPosition` → `{ top: 0 }`（不變）；有 `savedPosition` → 回傳 `waitForRestorableScrollPosition(savedPosition)`。

**舊導覽競態**：vue-router 5.2.0 在每次導覽完成時（`finalizeNavigation`，`vue-router.js:1393`）呼叫一次 `scrollBehavior`。push 到目前所在位置的重複導覽也會呼叫（`vue-router.js:1303`，`savedPosition` 為 null，結果是回頁首）。被 guard 中止或改導向的導覽不會完成，因此不會呼叫，使用者仍停在等待中的頁面，讓該頁繼續還原是正確的。所以「新導覽生效」等同「下一次 `scrollBehavior` 呼叫」。舊 waiter 在新導覽的 `scrollBehavior` 一開始就被 resolve 為 `false`，vue-router 拿到 `false` 不會呼叫 `scrollTo`。阻止舊位置捲動的責任在本專案程式碼，不依賴 vue-router。vue-router 5.2.0 的 `handleScroll`（`node_modules/vue-router/dist/vue-router.js:1473`）另外會檢查 `to === currentRoute.value`，只作為第二層保護，不列為正確性前提。多次快速導覽時，每次導覽都會取消前一個 waiter，只有最後一次導覽的 waiter 可能 resolve 為捲動位置。

選 animation frame 而非 `ResizeObserver`／`MutationObserver`：只需比較一個數字，最多跑 3 秒約 180 次，邏輯最簡單。Vitest 4 的 fake timers 預設會模擬 `setTimeout`、`requestAnimationFrame` 及對應的 clear／cancel，測試可用 `vi.advanceTimersByTime` 精確推進，並用 `vi.getTimerCount()` 斷言計時器與 frame 都已清除。

3 秒是取捨：等太短，慢一點的 API 回應來不及；等太久，逾時後的捲動會讓使用者覺得畫面突然跳動。沒有量測依據，tasks 的瀏覽器量測會記錄實際的等待時間，若明顯不足再調整。

替代方案：各頁面載入完成後主動通知路由「內容已就緒」。精準但要改每個非同步頁面，且新頁面容易漏接，否決。

## Risks / Trade-offs

- **新增座位圖後序號改變**：依 Id 排序，新座位圖可能插在既有座位圖之前，使既有座位圖序號 +1。規格已列為已知限制。實務上一個場館通常只有一張座位圖，影響小；若日後要穩定命名，需後端加名稱欄位，另開 change。
- **拖曳捲軸不會中止還原**：拖曳捲軸不會觸發 `wheel`／`touchmove`／`keydown`。若使用者在 3 秒等待內拖曳捲軸，內容載入後仍可能被捲回記錄位置。發生機率低（需在等待的短時間內拖曳），且結果只是回到原位置，不擴大實作範圍處理。
- **頁面內容本身比離開時短**：例如回來時訂單已被刪除或篩選結果變少，頁面永遠不夠高，等 3 秒後捲到最低處。與目前行為相同，只是晚 3 秒發生。
- **背景分頁的計時器節流**：逾時改用獨立的 `setTimeout`，不再受 animation frame 暫停影響。但瀏覽器對背景分頁的 `setTimeout` 仍會節流，Chrome 會把觸發時間對齊到每秒一次，所以背景中實際觸發時間可能晚到約 4 秒。分頁在背景時使用者看不到畫面，這個延遲沒有可觀察的影響，因此不另外處理。
- **任何按鍵都中止還原**：`keydown` 不分按鍵（含 Tab、修飾鍵）都視為使用者自行捲動。可能在使用者只是按 Tab 時放棄還原，結果是停在目前位置，與修改前的行為相近，不會把使用者拉走，為求邏輯簡單接受。
- **影響全站**：捲動規則由路由統一處理，後台頁面也一併套用。這是預期行為，規格已明寫。

## 安全確認

- 外部輸入：無新增。場館名稱來自既有 API，以 template 插值顯示，不用 `v-html`，無 XSS 風險。
- 資料庫、權限、後端：不變。`getVenues()` 沿用既有的 `RequireOrganizerContext` 授權與統一攔截器帶的 Auth Header。

## AC 測試對應

所有 AC 都以 Vitest 單元或元件測試涵蓋，無例外。真實瀏覽器驗證（tasks 1.1 前置閘門、5.1／5.2 完成閘門）不判定 AC，但未通過時本 change 不得完成。通過條件與紀錄格式見 tasks.md 開頭。

## Migration Plan

純前端變更，隨前端部署生效，無資料遷移。回滾即還原前端版本。

## Open Questions

（無）
