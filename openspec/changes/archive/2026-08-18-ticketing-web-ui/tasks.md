## 1. 專案骨架與 Docker 整合

- [x] 1.1 用 `npm create vite@latest` 建立 `web/`（Vue 3 + TypeScript 模板），設定 `tsconfig.json`、ESLint（沿用 Vue 3 官方推薦規則）
- [x] 1.2 安裝 Vue Router、Pinia、Element Plus，設定 `src/main.ts` 掛載三者
- [x] 1.3 `docker-compose.yml` 新增 `web` service（設計文件決策 2）：bind mount 原始碼、`node_modules` named volume、`WEB_HOST_PORT` 對外 port、`depends_on: api`
- [x] 1.4 `web/Dockerfile`（開發模式，執行 `npm run dev -- --host 0.0.0.0`）
- [x] 1.5 `vite.config.ts` 設定 `server.proxy['/api'] = { target: 'http://api:8080', changeOrigin: true }`（設計文件決策 2，取代原本的 `VITE_API_BASE_URL` 做法，避免瀏覽器跨源呼叫被 CORS 擋下），確認 `docker compose up -d web` 後瀏覽器可開啟 Vite 預設頁面
- [x] 1.5b **驗收**：`docker compose up -d web api` 後，於瀏覽器或用 `curl -i http://localhost:${WEB_HOST_PORT:-5173}/api/events` 呼叫任一既有公開端點（如 `GET /api/events`），確認回應狀態碼是該端點本身的正常回應（如 200），**不是** 307/301 redirect、也不是 502/504，驗證設計文件決策 2 對 `UseHttpsRedirection()` 的查證結論在實際 proxy 路徑上成立

## 2. 共用架構：型別產生與 API service 層

- [x] 2.1 安裝 `openapi-typescript`，加入 `npm run generate:api-types` script，來源固定寫死 `http://api:8080/openapi/v1.json`（查證後修正：本專案用 .NET 內建 OpenAPI，路徑是 `/openapi/v1.json`，不是 `/swagger/v1/swagger.json`；且這支指令要在 `web` 容器內執行，不是 host，用 compose service name `api` 而非 `localhost`，理由與內部固定 port 見設計文件決策 4），產生 `src/types/api.generated.ts`（此檔禁止手動編輯，見 2.2 之後的 `ui.ts` 分工）
- [x] 2.1b **驗收**：`docker compose up -d web api` 後執行 `docker compose exec web npm run generate:api-types`，確認指令成功結束（非逾時/連線失敗）、`src/types/api.generated.ts` 確實產生且內容非空，驗證設計文件決策 2／4 關於「容器對容器呼叫 `http://api:8080` 不受 `UseHttpsRedirection()` 影響」的查證結論在實際環境成立，不只是程式碼推論
- [x] 2.2 實作 `src/api/httpClient.ts`：`baseURL` 固定為相對路徑 `/api`（交由 1.5 的 dev server proxy 轉發）、統一帶入 Authorization Header、統一解析後端 `ProblemDetails` 錯誤格式
- [x] 2.3 實作 `src/api/auth.ts`（登入、註冊、Refresh、`GET /api/members/me`；登入與 Refresh 呼叫走獨立路徑，不掛 3.2 的 401 攔截器，見設計文件決策 5）、`src/api/events.ts`（活動/座位查詢）、`src/api/orders.ts`（下單/確認/取消）、`src/api/admin.ts`（場館/座位圖/活動/票種建立、Admin 訂單查詢；場館/座位圖/活動本輪只有建立與既有的 `GET /api/events`，沒有場館/座位圖查詢端點，見設計文件 Non-Goals）
- [x] 2.3b 新增 `src/types/ui.ts`：定義純前端本地狀態型別（場館/座位圖的 session 暫存清單項目、座位選擇 UI 狀態），與 `src/types/api.generated.ts` 分開存放、不得同名（設計文件決策 4）
- [x] 2.3c 新增 `src/types/apiResponses.ts`（手寫，非 generated）：實作階段查證後發現 `api.generated.ts` 只有 Request 型別、沒有 Response 型別（後端用 `IActionResult` 回傳，OpenAPI 反推不出 schema），與使用者確認後（2026-08-17）手寫這 7 個 Response DTO 對應的 TS interface（設計文件決策 4）
- [x] 2.4 `httpClient` 單元測試（Vitest）：驗證 Authorization Header 有正確帶入、`ProblemDetails` 錯誤格式有正確轉換為前端錯誤物件

## 3. 共用架構：Auth 狀態管理與路由守衛

- [x] 3.1 實作 `src/stores/auth.ts`（Pinia）：`accessToken`（記憶體）、`refreshToken`（`localStorage`）、`member`（含角色，來自 `GET /api/members/me`）、login/logout/refresh actions；login/refresh 成功後緊接著呼叫 `GET /api/members/me` 寫入 `member`（設計文件決策 5）
- [x] 3.2 `httpClient` 加上 401 攔截，實作 **single-flight** 換發：維護模組層級的「進行中換發 Promise」，第一個 401 建立並實際呼叫 refresh，之後在它完成前抵達的其他 401 共用同一個 Promise、不重複呼叫；Promise 完成後所有等待中的請求一起用新 access token 重放，失敗則一起清空登入狀態（設計文件決策 5，查證 `RefreshTokenHandler` 後確認：refresh token 一次性＋輪替，重複送出已用過的 token 會觸發後端撤銷該會員所有 token，不能讓多個並發 401 各自呼叫 refresh）
- [x] 3.2b 防遞迴與重試邊界（設計文件決策 5）：refresh 端點呼叫本身不經過這個 401 攔截器（避免 refresh 收到 401 遞迴呼叫自己）；每個業務請求標記是否已「換發後重試」，換發後重試仍 401 就直接判定登入失效、不再觸發第二次 refresh；登出 API 呼叫失敗（含 401）只記 log，不阻擋本地清空登入狀態並導向登入頁
- [x] 3.3 `auth` store 單元測試（Vitest）：登入成功寫入 token 與 member／登出清空 token／refresh 成功更新 access token／refresh 失敗清空登入狀態（對應 spec `買家可透過介面註冊與登入`、`Access Token 過期時前端自動換發，換發失敗導回登入頁` 兩個 Requirement 的核心邏輯）
- [x] 3.4 `httpClient` single-flight 換發單元測試（Vitest）：模擬兩個以上的請求同時收到 401，驗證只呼叫一次 refresh API、所有請求最終都用新 access token 重放成功（對應 3.2 的併發正確性，這是查證後發現的真實風險，需要獨立測試覆蓋，不能只靠 3.3 的單一換發情境測試）
- [x] 3.5 設定 Vue Router 路由表：買家端路由、`/admin/*` 路由，全域 `beforeEach` 守衛依 `auth` store 角色導向（設計文件決策 3）；guard 建立在「`bootstrapAsync()`（見 3.7）已經跑完」的前提上，只同步讀 store 狀態，不自行 await 任何非同步流程
- [x] 3.6 路由守衛單元測試（Vitest）：未登入進入需登入頁面導向登入頁、一般會員進入 `/admin/*` 導向買家端首頁、Admin 登入後可進入後台（對應 `未登入使用者存取需登入頁面時導向登入頁`、`Admin 後台路由僅限 Admin 角色進入` 兩個 Requirement）
- [x] 3.7 `main.ts` bootstrap 流程（設計文件決策 5）：建立 Pinia／Router 後、`app.mount()` 之前，`await authStore.bootstrapAsync()`——無 `localStorage` refreshToken 視為未登入；有則呼叫 Refresh 換新 access token 並接著呼叫 `GET /api/members/me` 寫入 `member`。錯誤分類：Refresh 回傳 401，或 `GET /api/members/me` 回傳 401／404（查證 `GetMyProfileHandler` 後確認只會是這兩種，見設計文件決策 5）→ **預期錯誤**，清空 `localStorage` 視為未登入；網路錯誤／逾時／5xx → **非預期錯誤**，**不清空** `localStorage`，設定 `authStore.bootstrapError = true`。兩種情況都不拋出例外中斷 mount、都不主動導頁（`isAuthenticated` 皆為 `false`，導頁交給 router guard）；`index.html` 內建靜態 loading 畫面，`app.mount()` 完成後移除
- [x] 3.8 `bootstrapAsync` 單元測試（Vitest）：無 refreshToken 時直接視為未登入／有 refreshToken 且換發成功寫入 member／Refresh 或 `/members/me` 回傳 401／404 時清空 `localStorage` 且視為未登入／網路錯誤或 5xx 失敗時**保留** `localStorage` 的 refreshToken 且 `bootstrapError = true`，兩種失敗情境皆不拋出例外（對應 3.7 的正確性，這是查證後發現原本沒有明確定義的初始化流程，且必須區分預期／非預期錯誤，需要獨立測試覆蓋兩種分支）
- [x] 3.9 `App.vue` 根層級全域錯誤 banner（設計文件決策 5）：讀 `authStore.bootstrapError`，顯示「無法確認登入狀態，請檢查網路連線」並提供「重試」按鈕（呼叫 `authStore.bootstrapAsync()`），不侷限於登入頁，放在 `<router-view>` 外層讓 guard 導向任何頁面都看得到

## 4. 買家端頁面

- [x] 4.1 `LoginPage.vue`／`RegisterPage.vue`：串接 `auth` API，登入成功依角色導向、登入失敗顯示錯誤訊息
- [x] 4.2 `EventListPage.vue`：串接活動列表 API
- [x] 4.3 `EventDetailPage.vue`：顯示座位圖與載入／手動刷新取得的最新可售狀態、座位選擇與送出下單、下單失敗（座位已被搶）顯示錯誤訊息，成功後導向訂單結果頁並帶上訂單 Id
- [x] 4.4 `OrderResultPage.vue`：下單成功導向的訂單結果頁，顯示下單回應的訂單 Id；持有到期時間用「下單當下時間 + 10 分鐘」推算顯示，不是解析回應欄位（查證後發現 `PlaceOrder` 成功回應只有 `{ id }`，10 分鐘對照後端 `CreateOrderHandler.HoldDuration` 常數寫死，見設計文件），提供確認／取消操作，呼叫既有確認/取消訂單 API；不呼叫任何查詢 API（對應 spec `買家可在下單結果頁確認或取消剛下的訂單`）
- [x] 4.5 `MyOrdersPage.vue`／`OrderDetailPage.vue`：**只做空狀態頁面**，顯示「功能開發中，待後端支援後上線」提示文字，不呼叫任何訂單查詢 API（對應 spec `「我的訂單」列表與明細本輪僅顯示空狀態，待後端補齊查詢 API 後再串接`；查詢 API 本身的新增待未來 OpenSpec 變更）
- [x] 4.6 手動於瀏覽器驗證買家端完整購票流程（註冊 → 登入 → 瀏覽活動 → 選位下單 → 於訂單結果頁確認訂單 → 開啟「我的訂單」確認為空狀態畫面），涵蓋 `buyer-web-ui` spec 的 UI 呈現與導頁行為——**用 claude-in-chrome 實際跑過真瀏覽器**：登入 → 活動列表 → 活動詳情頁選位（座位標籤正確顯示 Available/已選狀態與總金額）→ 送出訂單成功導向 `/order-result/:id`（正確顯示訂單 Id 與「下單當下 + 10 分鐘」的持有到期時間）→ 點「確認訂單」顯示「已確認」→ 開啟「我的訂單」正確顯示空狀態文字、確認網路請求裡沒有任何訂單查詢 API 呼叫。過程中發現並修正一個嚴重 bug（見下方）

## 5. Admin 後台頁面

- [x] 5.1 `AdminLayout.vue`：後台共用版面（導覽列、登出）
- [x] 5.2 `VenueListPage.vue`／建立場館表單、建立座位圖表單：後端無場館/座位圖查詢 API（見設計文件 Non-Goals），新增 Pinia store `stores/adminVenueCache.ts` 存放**本次瀏覽器分頁 session 內**建立過的場館/座位圖（僅記憶體狀態，重新整理即消失，不呼叫任何查詢 API，也不落地 `localStorage`）；`VenueListPage.vue` 直接讀這個 store 顯示列表，不在元件內另外維護重複的本地狀態；建立成功後明顯顯示新 Id（含複製按鈕），供 5.3 手動貼上使用
- [x] 5.3 `EventListPage.vue`（Admin 版，重用既有公開 `GET /api/events`，`VenueId`／`SeatMapId` 欄位顯示 GUID，無名稱可查，見設計文件 Non-Goals）／建立活動表單（場館 Id、座位圖 Id 改為手動輸入文字欄位＋前端 GUID 格式驗證，不驗證是否存在，交由後端建立失敗的錯誤訊息呈現）、建立票種表單
- [x] 5.4 `AdminOrderListPage.vue`：顯示所有訂單與載入／手動刷新取得的最新狀態
- [x] 5.5 `AdminOrderDetailPage.vue`：顯示單筆訂單的座位項目明細
- [x] 5.6 手動於瀏覽器驗證 Admin 後台完整流程（Admin 登入 → 建立場館/座位圖並複製 Id → 建立活動時手動貼上場館/座位圖 Id → 建立票種 → 查看訂單列表與明細；另外驗證一般會員帳號無法進入 `/admin/*`），涵蓋 `admin-web-ui` spec 的 UI 呈現與權限行為——**用 claude-in-chrome 實際跑過真瀏覽器**：Admin 登入導向 `/admin/venues` → 建立場館成功顯示 Id → 選場館建立座位圖（2 個座位）成功 → 活動管理頁手動貼上場館/座位圖 Id 建立活動成功、列表正確顯示 → 建立票種成功（過程中發現並修正一個會讓表單永遠送不出去的 bug，見下方）→ 訂單管理頁正確顯示訂單列表與明細 → 登出後改用一般會員帳號直接輸入 `/admin/venues` 網址，正確被導回買家端首頁、看不到後台內容

### 4.6／5.6 過程中發現並修正的 3 個真實 bug（非設計討論項目，是實測抓到的程式碼缺陷）

1. **`main.ts` 的 `app.use(router)` 時機錯誤（嚴重，影響「重新整理頁面後維持登入」這個核心功能）**：原本寫法是 `app.use(router)` 在 `bootstrapAsync()` 之前，實測發現 Vue Router 在 `app.use(router)` 當下就會非同步開始解析初始路由，不等 `app.mount()`；結果是重新整理一個受保護頁面時，即使 refresh token 有效，guard 會在 bootstrap 完成前就把畫面導去 `/login`，之後 bootstrap 換發成功也不會自動導回來，使用者會被卡在登入頁。修法：`app.use(router)` 延後到 `bootstrapAsync()` 確定完成之後才做（見 `main.ts`、design.md 決策 5）。
2. **`AdminLayout.vue` 的登出項目缺少 Element Plus `el-menu-item` 必要的 `index` prop**：造成瀏覽器 console 一直跳警告。改成把登出改成 `el-menu` 外的獨立 `el-button`，不再借用 menu-item 語意。
3. **`EventListPage.vue`（Admin）建立票種表單永遠送不出去**：`el-input-number` 只設了 `:min="0.01"`、沒設 `:step`，瀏覽器原生 HTML5 number 輸入的 step 驗證預設是整數步進（`0.01, 1.01, 2.01...`），輸入「300」會被瀏覽器判定為不合法值，導致表單連 submit 事件都不會觸發（不是 Vue/Element Plus 的驗證邏輯擋下，是瀏覽器原生驗證先擋住，畫面上完全沒有任何錯誤訊息，只有靜默不submit）。這個 bug 只有真的用瀏覽器點過表單才抓得到，curl 測 API、Vitest 單元測試都測不到。修法：補上 `:step="0.01"`。
4. 順帶修正 `vite.config.ts`：Docker Desktop on Windows 的 bind mount 常漏掉檔案變更事件，Vite HMR 因此常常失效（改完檔案容器內還是舊版，要重啟容器才生效）；加上 `server.watch.usePolling: true` 解決（見 design.md 決策 2）。

### 4.7 使用者驗收後的 UI 迭代（非本輪原始任務，是實作完成、使用者實際看過畫面後的直接回饋）

- [x] 新增 `BuyerLayout.vue`：買家端共用 Header（品牌／「活動」導覽、未登入顯示「登入／註冊」、已登入顯示「我的訂單」+ 會員名稱下拉選單含「登出」），`/`、`/login`、`/register`、`/events/:id`、`/events/:id/purchase`、`/order-result/:id`、`/orders`、`/orders/:id` 全部包在這個 layout 底下
- [x] ~~拆分活動詳情頁與購票流程為兩個路由~~（第一版做法，**已在 4.8 改回單頁**，見下方）
- [x] `EventListPage.vue` 從純表格改成卡片格線排版，作為買家端「首頁」的呈現
- [x] 新增 `web/src/styles/morandi.css`：全站套用莫蘭迪色系（低飽和灰調），覆蓋 Element Plus 的 `--el-color-*`／文字／邊框／背景 CSS variables，一次套用即涵蓋買家端與 Admin 後台
- [x] 用 claude-in-chrome 實際驗證：Header 在已登入/未登入狀態下正確切換內容、登出後導回登入頁且 Header 變回登入/註冊、未登入直接點「立即購票」正確導去登入頁並帶 `redirect` query、登入後自動回到購票頁面、活動列表卡片與莫蘭迪配色實際渲染正確
- [x] `npm run lint`／`vue-tsc --noEmit`／`npm run test`（21 個測試全過，路由重構後仍全過）皆通過

### 4.8 第二輪 UI 迭代：活動頁左右版面、批次座位、大量座位效能（使用者直接提出的需求，含一筆後端 schema 變更）

- [x] **後端新增 `Event.Description`／`Event.PosterUrl` 兩個欄位**（`string?`，選填）：這是本次唯一真的動後端的地方——前端無論如何都生不出這兩筆資料，跟先前那些「查證後發現的落差」不同，是真的缺欄位。改動範圍：`Event` domain entity、`EventConfiguration`（`MaxLength` 2000／500）、`CreateEventRequest`／`CreateEventRequestValidator`、`EventDto`、`GetEventsHandler`、`CreateEventHandler`，新增 EF migration `AddEventDescriptionAndPoster` 並 `dotnet ef database update`；`docker compose restart api` 讓 dotnet watch 讀到新 schema（同樣是 hot reload 對這類結構性變更常常不可靠，見前面已知的環境問題）。後端 232 個既有測試（Domain/Application/Infrastructure/WebApi）全過，沒有新增/修改測試涵蓋這兩個新欄位（純資料傳遞，沒有業務邏輯）
- [x] 重新產生 `api.generated.ts`（`CreateEventRequest` schema 變了），手寫的 `EventSummary`（`apiResponses.ts`）補上 `description`／`posterUrl`
- [x] Admin「建立活動」表單加上「活動說明」（textarea，選填）、「海報網址」（文字輸入，選填，貼圖片網址，不做檔案上傳）
- [x] **把買家端活動頁改回單頁、左右版面**：左側海報／標題／時間／說明／票種表，右側選位；不再拆成兩個路由——點座位時才檢查登入狀態，未登入導去 `/login?redirect=/events/:id`，登入後導回同一頁即可繼續選位，不需要獨立的 `/purchase` 路由（移除 `EventPurchasePage.vue`）
- [x] Admin「建立座位圖」表單加「批次產生」：輸入分區代碼＋起始號碼＋結束號碼，一次展開成整批座位（例如 A 區 1~100 → 100 個座位），不用再一列一列手動新增；原本的單列手動新增保留給少量、非連號的座位用
- [x] 種了一組 5 區、每區 100（共 500）位的測試場館／活動／票種資料（透過 API 直接建立，不是點 UI 一列一列建，UI 本來就不該拿來建這麼多筆）
- [x] **實測抓到一個真效能問題並修正**：500 個座位一開始用 `<el-tag>` 平鋪 + 每個座位的 `isSelected()` 都對 `selectedSeats` 陣列做 `.some()` 線性掃描，瀏覽器在真的載入這個頁面時明顯卡住（`Page.captureScreenshot` 直接 timeout，等於整個 renderer 卡死了幾秒）。修法：① 選位狀態改用 `Set` 做 O(1) 查找、② 座位改用輕量 `<button>` 取代 `el-tag`、③ 依分區分組渲染，不再是一整片攤平的 500 個元素。修完後同一頁面載入/選位都順暢，這是只有真的塞了大量測試資料進去實際點過才抓得到的問題，Vitest 或小筆測試資料都不會暴露
- [x] `npm run lint`／`vue-tsc --noEmit`／`npm run test`（21 個測試全過）／`npm run build`／後端 `dotnet test`（232 個測試全過）皆通過

### 4.9 第三輪迭代：每筆訂單限購張數 + 區域隨選購票（使用者直接提出的需求，含業務規則、非純資料欄位）

- [x] **後端新增 `Event.MaxTicketsPerOrder`**（`int?`，選填，正整數，未設定代表不限制）：`Event` domain entity 建構子驗證 `<= 0` 直接丟例外、`EventConfiguration`、`CreateEventRequest`／`CreateEventRequestValidator`（`GreaterThan(0).When(...)`）、`EventDto`、`GetEventsHandler`、`CreateEventHandler`，新增 EF migration `AddEventMaxTicketsPerOrder`
- [x] **`OrderService.PlaceOrderAsync` 加上超額下單檢查**：用第一個選位座位所屬的活動查 `MaxTicketsPerOrder`，`request.Selections.Count` 超過就回 `Validation` 錯誤、不建立訂單——這是後端真正的把關，前端的提示只是體驗層。跟「選位橫跨多場活動」這個既有驗證分開處理，理由見程式碼註解
- [x] 補測試：Domain 層（`Event` 建構子拒絕非正整數限購張數、`null` 代表不限制）、Application 層（`OrderService`：選位超過限購張數回 Validation 且不建訂單／剛好等於上限成功／沒設定限購張數不限制張數，`CreateEventHandler`：限購張數為 0 回 Validation／正整數成功建立），後端測試從 232 增加到 240 個，全過
- [x] 重新產生 `api.generated.ts`，`EventSummary`（`apiResponses.ts`）補上 `maxTicketsPerOrder`
- [x] Admin「建立活動」表單加上「每筆訂單限購」數字輸入（選填正整數，留空代表不限制）
- [x] 買家端活動詳情頁：① 顯示「每筆訂單限購 N 張」提示（活動有設定時才顯示）② 選位時前端也擋一次，達到上限再點新座位顯示提示訊息、不加入已選清單 ③ 新增「區域隨選」：選分區＋張數，點「自動選位」自動挑該分區內還沒被選的 Available 座位補滿，張數受「該分區剩餘可售數」與「限購剩餘額度」兩者取小值限制，未登入時行為跟點單一座位一致（導去登入頁）
- [x] 用 claude-in-chrome 實際驗證（把「莫蘭迪音樂節」測試活動的限購張數用 SQL 設成 2，示範用）：手動選滿 2 張後再點第 3 個座位，正確顯示限購提示、不加入；取消已選後用「區域隨選」選 B 區 2 張，正確自動選出 2 個座位、金額同步更新；Admin 建立活動表單填限購張數 3 送出，資料庫確認正確存進 `MaxTicketsPerOrder` 欄位
- [x] `npm run lint`／`vue-tsc --noEmit`／`npm run test`（21 個測試全過）／`npm run build`／後端 `dotnet test`（240 個測試全過）皆通過
- [x] 同步更新 `buyer-web-ui`／`admin-web-ui` 兩份 spec：買家端新增「區域隨選」Requirement、既有「選位並送出訂單」補上限購張數的 Scenario；Admin 端「管理活動與票種」補上限購張數欄位的 Scenario

### 4.10 第四輪迭代：區域隨選改成「全部區域隨機＋一鍵送出」、測試資料改階梯式分區（使用者直接提出的需求，純前端）

- [x] 「區域隨選」的分區下拉選單新增「全部區域」選項並改為**預設值**；選「全部區域」時抽選池 SHALL 涵蓋所有分區的可售座位，不是只能先選單一分區
- [x] 抽選邏輯從「依陣列順序取前 N 個」改成**真隨機**（Fisher–Yates shuffle 後取前 N 個），不論是單一分區還是全部區域都適用
- [x] 「自動選位」按鈕改成「自動選位並送出訂單」：點下去後不只把座位加入已選清單，**直接呼叫 `handleSubmit()` 送出訂單**、導向訂單結果頁，不需要買家再手動滾到頁面最下面按「送出訂單」；重複使用既有 `handleSubmit()` 的下單/導頁/錯誤處理邏輯，沒有另外寫一份送出流程。手動點座位＋底部「送出訂單」按鈕的流程維持不變，給想自己挑特定座位的買家用
- [x] 種了一組新的測試活動「莫蘭迪音樂節（VIP 分區）」，座位圖改成階梯式分佈：A 區 20 位／500 元（最貴最少）→ B 區 60 位／400 元 → C 區 100 位／300 元 → D 區 140 位／200 元 → E 區 180 位／100 元（最便宜最多，共 500 位），限購張數維持 2 張示範用；透過 API 直接建立（原因同 4.8：500 個座位不該用 UI 一列一列點）
- [x] 用 claude-in-chrome 實際驗證：開啟新活動頁，分區下拉預設顯示「全部區域」；設張數 2、維持「全部區域」點「自動選位並送出訂單」，**一次點擊直接導向訂單結果頁**（沒有多一步送出訂單的手動確認）；資料庫確認這筆訂單的兩個座位單價分別是 300／100（來自不同分區，證實「全部區域隨機」真的橫跨多個分區抽選，不是固定選同一區）
- [x] `npm run lint`／`vue-tsc --noEmit`／`npm run test`（21 個測試全過）／`npm run build` 皆通過（純前端改動，後端測試不受影響、未重跑）
- [x] 同步更新 `buyer-web-ui` spec：「買家可用區域隨選快速選位」Requirement 改名為「...快速下單」，內容與 Scenario 改寫成反映「全部區域預設＋隨機抽選＋直接送出訂單」的行為

## 6. 收尾

- [x] 6.1 `npm run build` 確認 production build 無型別錯誤（即使本輪不部署，仍作為 CI 前的基本健檢）
- [x] 6.2 檢查所有頁面的 API 呼叫錯誤情境（401/403/404/驗證錯誤）皆有對應的畫面呈現，不吞掉未處理的錯誤：review 時發現一個缺口並修正——`authorizedRequest` 換發失敗只會清空 store 狀態，不會自動導頁，使用者可能停在已失效的受保護頁面上；`App.vue` 補上 `watch(authStore.isAuthenticated)`，登入狀態變 false 且目前頁面需要登入時主動導向登入頁
- [x] 6.3 檢查所有表單（登入/註冊、建立場館、建立座位圖、建立活動、建立票種）都有對應的前端格式/長度/必填驗證，並確認後端回傳的驗證錯誤（400）有對應畫面呈現，不是只靠前端擋（設計文件 Security 段落）：review 時發現 `LoginPage.vue` 誤用註冊用的密碼強度規則（後端 `LoginRequestValidator` 只要求 `NotEmpty`），已修正為只驗證必填
- [x] 6.4 撰寫 `web/README.md`：專案啟動方式（`docker compose up -d web`）、`generate:api-types` 執行指令與何時需要重新產生、Vitest 執行方式（設計文件決策 4）
