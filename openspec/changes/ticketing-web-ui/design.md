## Context

後端已完成的能力（`authentication`／`member-management`／`event-catalog`／`event-management`／`ticket-purchase`／`order-administration`）目前只能透過 Swagger 或直接呼叫 API 使用。本次要建立 Vue 3 前端，**範圍是完整 MVP**：買家端涵蓋登入到下單確認的完整流程，Admin 後台涵蓋場館/活動/訂單管理的完整流程，兩者本輪都要做完，不是只做骨架（2026-08-17 與使用者確認範圍；見下方 Non-Goals 的場館/座位圖查詢限制——這部分本輪刻意縮小）。共用架構（路由分流、API service 層、Auth 狀態管理與 401 換發）是優先定案的部分，因為這些是所有頁面共用的地基；畫面細節（各頁面的欄位排版、互動微調）在實作階段仍會依實際串接情況微調，不代表本輪不做這些頁面。

專案既有的執行環境是 Docker Compose（`db`／`api` 兩個 service，本機不裝 SDK），前端也必須納入同一套 Compose 環境，不可要求本機另裝 Node.js 工具鏈來跑。

## Goals / Non-Goals

**Goals:**
- 定案前端專案結構、工具鏈、Docker 整合方式
- 定案路由分流（買家端／Admin 後台）與權限守衛機制
- 定案 API 呼叫的統一封裝方式與型別來源，避免 DTO 手動維護漂移
- 定案登入狀態／Token 存放與 401 自動換發的處理方式
- 定案 UI 元件庫選擇（Admin 後台有大量表格／表單，重工成本高，值得引入）
- 建立完整可用的買家＋Admin MVP（不只是最小骨架）：買家端涵蓋登入到下單確認的完整流程，Admin 後台涵蓋場館/活動/訂單管理的完整流程；共用架構（路由分流、API service 層、Auth 狀態管理）優先定案，畫面細節（各頁面欄位排版、互動微調）在實作階段依實際串接情況微調

**Non-Goals:**
- 不在本輪做任何後端 API 變更——所有頁面都只消費既有已完成的 API（**例外 1**：4.8 使用者直接要求活動頁要有海報／說明，後端原本完全沒有這兩個欄位，前端無法生出不存在的資料，新增 `Event.Description`／`Event.PosterUrl` 兩個選填欄位＋一支 EF migration，純新增、不改任何既有欄位或行為。**例外 2**：4.9 使用者要求「每筆訂單限購張數」——這不只是顯示用的資料欄位，是要真的擋下超額下單的業務規則，新增 `Event.MaxTicketsPerOrder`（選填 int）＋一支 EF migration，並在 `OrderService.PlaceOrderAsync` 加上張數檢查——前端檢查只是提前提示，後端這一關才是真正擋壞人的地方，兩者都做才算數。這兩個例外都純新增欄位/檢查，不改既有欄位或既有行為，後端既有測試全過，且都補上對應的新測試涵蓋新規則本身）
- 不做金流付款頁面——對應後端 Confirm 端點本身就還沒有真正金流整合（`ticket-purchase` 的既有 Non-Goal），前端 Confirm 頁沿用「視同付款成功」語意
- 不做 SSR（Server-Side Rendering）——內部售票後台工具與買家購票流程都不需要 SEO，純 SPA 即可
- 不在本輪導入 E2E 測試框架（如 Playwright）——先以 Vitest 涵蓋 service／composable 的單元測試，E2E 待畫面穩定後再評估
- 不做 Admin 角色指派 UI——對應後端 API 本身還沒做（見 memory：membership-system 已知缺口），等該後端功能完成才有 UI 可做
- **不串接「我的訂單」列表／明細查詢**——查證後確認 `OrdersController` 只有 `POST`（下單/確認/取消），買家專屬的訂單查詢（`GET`）API 不存在，現有的 `GET /api/admin/orders` 是 Admin-only，一般會員呼叫會被 403。本輪「我的訂單」頁面只建立路由與頁面框架、顯示固定空狀態，不呼叫任何查詢 API；買家對「剛下的那筆訂單」確認/取消，直接沿用下單成功回應裡的訂單 Id（見 `buyer-web-ui` spec），不依賴這支缺少的查詢 API。真正的訂單查詢功能待後端補上對應 API（新的 OpenSpec 變更）後再串接
- **不在本輪新增 Admin 場館／座位圖的查詢 API**——查證後確認 `AdminVenuesController`／`AdminEventsController` 目前只有建立用的 `POST`，沒有任何 `GET`；買家端公開的 `GET /api/events`（`event-catalog`）雖然存在且可供 Admin 活動列表頁重用，但回傳的 `EventDto` 只有 `VenueId`／`SeatMapId` 兩個 GUID，沒有場館/座位圖名稱可顯示。這是與使用者確認過的範圍縮小（2026-08-17）：本輪 Admin 場館/座位圖管理頁面**不做真正的列表查詢**——建立場館/座位圖成功後，畫面顯示新建立的 Id 供 Admin 複製，並加入新增的 Pinia store `stores/adminVenueCache.ts`（僅存在於當前瀏覽器分頁記憶體中，重新整理即消失，不落地 `localStorage`、不是持久化查詢，固定用 Pinia，不與 component 內部 state 二選一）；建立活動表單的場館/座位圖欄位改為**手動輸入 Id**（前端只做 GUID 格式驗證，不驗證該 Id 是否存在，交由後端建立時的錯誤回應呈現）。Admin 活動列表頁重用既有的 `GET /api/events`，可正常顯示活動，但場館/座位圖欄位只能顯示 GUID（無名稱可查）。真正的場館/座位圖查詢 API 待下一個 OpenSpec 變更補齊後，才把這幾個地方換成下拉選單與正常列表（見 tasks.md 5.2／5.3 的標註）
- **不做即時推播（WebSocket／SignalR）**——本文件與 spec 中提到的「即時狀態」／「即時可售狀態」，一律指「頁面載入或手動重新整理當下呼叫查詢 API 取得的最新結果」，不是伺服器主動推播的即時更新；本輪不引入任何推播機制

## Decisions

### 1. 專案結構與工具鏈：Vite + Vue 3 + TypeScript，位於 `web/`

- 新增 `web/` 目錄作為獨立前端專案（`package.json` 獨立於任何後端專案），使用 Vite 作為建置工具（HMR 快、Vue 官方鷹架預設選項）
- 採 TypeScript：後端是強型別的 C#，前端若用純 JS，DTO 形狀、Enum 值（如 `OrderStatus`）容易在雙方修改時悄悄失去同步而不會在編譯期發現。TypeScript 讓型別不一致直接變成建置錯誤
- 目錄骨架：
  ```
  web/
    src/
      api/          # API service 層（見決策 4）
      stores/        # Pinia store（見決策 5）
      router/        # 路由設定（見決策 3）
      layouts/       # BuyerLayout.vue / AdminLayout.vue
      pages/
        buyer/       # 登入、活動列表、選位下單、我的訂單
        admin/       # 場館/座位圖、活動/票種、訂單管理
      components/    # 跨頁共用元件
      types/
        api.generated.ts  # 唯一由 openapi-typescript 產生，禁止手動編輯（見決策 4）
        ui.ts              # 手寫的前端本地狀態型別，不對應任何 API DTO（見決策 4）
    vite.config.ts
    tsconfig.json
    package.json
  ```
- 考慮過的替代方案：純 JavaScript（拒絕，理由如上）；Nuxt（拒絕，SSR/檔案式路由等能力用不到，徒增複雜度，違反「不做投機性抽象」原則）

### 2. Docker 整合：新增 `web` service，跑 Vite dev server，API 請求透過 Vite dev server proxy 轉發，不直接讓瀏覽器跨源呼叫

- `docker-compose.yml` 新增 `web` service：`build.context: ./web`，開發模式執行 `npm run dev -- --host 0.0.0.0`（Vite dev server），bind mount `./web:/app` 掛載原始碼支援 HMR
- `node_modules` 用 named volume（比照現有 `nuget_packages`／各專案 `bin`/`obj` 的做法）而非 bind mount 進 `web/node_modules`，理由與既有註解一致：Windows 上大量小檔案的 bind mount I/O 效能差
- 對外 port 用 `${WEB_HOST_PORT:-5173}:5173`（Vite 預設 port），與現有 `API_HOST_PORT`/`DB_HOST_PORT` 的可覆寫慣例一致
- `depends_on: api`（不需要 `condition: service_healthy`，因為 `api` 目前沒有定義 healthcheck；前端啟動時 API 還沒就緒只會讓第一次呼叫失敗，不影響開發流程）
- **API 請求走 Vite dev server proxy，不是瀏覽器直接呼叫 `api` service**：`vite.config.ts` 設定 `server.proxy['/api'] = { target: 'http://api:8080', changeOrigin: true }`。瀏覽器全程只呼叫同源的 `/api/...`（`http://localhost:5173/api/...`），實際轉發到後端的動作發生在 `web` 容器內部，是**容器對容器**的連線，`http://api:8080` 這個 compose service name 用法完全符合 CLAUDE.md 的既有規則。
  - 查證：`Program.cs` 目前沒有設定任何 `AddCors`/`UseCors`。若讓瀏覽器直接打 `http://localhost:8080`（不同 port＝不同 origin），會被瀏覽器同源政策擋下，所有 API 呼叫都會失敗。改用 dev server proxy 後，瀏覽器端不存在跨源請求，完全不需要碰後端的 CORS 設定，也不違反 proposal.md「不新增或修改任何後端 API 行為／不影響現有後端程式碼」的範圍界線
  - 考慮過的替代方案：讓瀏覽器直接呼叫 `api` service 對外 port，後端加 `AddCors` 允許 `localhost:5173`（拒絕，這會是本次唯一一筆後端程式碼變更，且沒有必要——dev server proxy 是純前端就能解決的標準做法）
  - **查證後發現並修正**：Docker Desktop on Windows 的 bind mount 常常不會把 host 端的檔案變更事件正確傳進容器，Vite 預設用的 chokidar 原生 fs watch 因此會漏掉變更——實測發現改完 `web/src` 底下的檔案，容器內的 Vite dev server 還是舊內容，要 `docker compose restart web` 才會生效，HMR 等於沒作用。`vite.config.ts` 的 `server.watch` 加上 `usePolling: true`（`interval: 300`）換取可靠性，代價是 CPU 使用率略高，本專案規模可接受
  - 查證：`UseHttpsRedirection()` 不會擋掉這條 proxy 路徑，也不會擋掉決策 4 的 `openapi-typescript` 產生流程。`Program.cs` 無條件呼叫 `app.UseHttpsRedirection()`（不在 `IsDevelopment()` 判斷內），但 `docker-compose.yml` 的 `api` service 只設定 `ASPNETCORE_URLS: http://0.0.0.0:8080`，沒有設定任何 HTTPS endpoint 或 `ASPNETCORE_HTTPS_PORT`。ASP.NET Core 在找不到可用的 HTTPS port 時，`HttpsRedirectionMiddleware` 只會記一次警告 log（`Failed to determine the https port for redirect`），**不會真的觸發 307 redirect**，請求照常放行；`web` 容器對 `http://api:8080` 的所有請求（含 Vite proxy 與 `generate:api-types` 的容器內呼叫）都不受影響，但此結論僅為程式碼查證，仍需在 tasks.md 加入實際驗收步驟確認（見 tasks.md 1.5b、2.1b）

### 3. 路由與權限分流：Vue Router + route meta + navigation guard

- 單一 Vue Router 實例，路由表分兩區：買家端（`/`、`/events/:id`、`/orders`、`/login` 等）與 Admin 後台（`/admin/*`）
- Admin 路由統一掛 `meta: { requiresAdmin: true }`；全域 `beforeEach` 守衛檢查 Pinia `auth` store 的角色，非 Admin 導回登入頁或首頁，未登入導向 `/login`
- 買家與 Admin 共用同一個登入頁與登入 API，登入成功後依角色（取得方式見決策 5）導向對應首頁（Admin 進 `/admin`，一般會員進 `/`）
- 考慮過的替代方案：買家/Admin 用兩個獨立前端專案（拒絕，登入邏輯、API 封裝、型別完全共用，拆兩個專案是不必要的重複維護）

### 4. API service 層：統一 HTTP client 封裝 + 從 Swagger/OpenAPI 產生 TypeScript 型別

- `src/api/httpClient.ts` 封裝一個 fetch/axios 實例，`baseURL` 固定為相對路徑 `/api`（見決策 2，交由 Vite dev server proxy 轉發，不寫死任何 host/port）：統一帶入 `Authorization: Bearer <accessToken>`、統一解析後端 `ProblemDetails` 錯誤格式為前端可用的錯誤物件、統一處理 401（見決策 5 的自動 refresh）
- 每個後端 Controller 對應一個 `src/api/<resource>.ts`（如 `orders.ts`、`events.ts`），只在這一層直接呼叫 `httpClient`，元件不得直接組 URL 或處理原始 response
- DTO 型別**不手動謄寫**，改用 `openapi-typescript` 從 `api` service 產生的 OpenAPI 文件產生 `src/types/api.generated.ts`，作為型別的唯一來源，避免後端 DTO 改了、前端忘記同步的情況（既有 CLAUDE.md 對後端命名/DTO 的嚴謹度延伸到前端）
  - 查證：本專案 `Program.cs` 用的是 .NET 10 內建 `AddOpenApi()`/`MapOpenApi()`，**不是** Swashbuckle，OpenAPI JSON 實際路徑是 `/openapi/v1.json`（對照 `UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", ...))`），不是常見的 `/swagger/v1/swagger.json`
  - **產生方式與執行位置**（查證後修正：先前假設「開發者在自己電腦上手動執行」與本專案「本機不裝工具鏈、一切透過容器」的既有前提互相矛盾——`web` service 本身就是照決策 2 整個跑在容器內，`node_modules` 是 named volume，host 上不保證有 Node/npm 可用，也不該假設有）：`web/package.json` 加一個 script `generate:api-types`，來源**固定寫死** `http://api:8080/openapi/v1.json`（compose service name，容器對容器連線，符合 CLAUDE.md「連線字串一律用 service name、禁止 localhost」的既有規則；且容器內部 port 固定是 `8080`，不受 host 端 `API_HOST_PORT` 覆寫影響，不存在 P1 review 原本擔心的 port 對不上問題）。執行方式是 `docker compose exec web npm run generate:api-types`（比照專案既有的 `docker compose exec api dotnet ef database update` 模式），**不是**在 host 上直接執行 `npm run`；前提是 `web` 容器要先啟動（`docker compose up -d web`）。產生的 `src/types/api.generated.ts` 因為原始碼是 bind mount，容器內寫入後 host 端也會同步看到。對開發者要求：後端 DTO 有變動時需重新產生
  - **`api.generated.ts` 進版控**（不是 build 產出物、不加進 `.gitignore`）：理由是（1）新 clone 專案的人不用先手動跑一次產生指令，`npm run build`／型別檢查才能立刻動；（2）PR review 時能直接在 diff 看到「這次改動對應到後端哪次 DTO 變更」，比純看後端 commit 更直接；代價是若忘記重新產生，型別會過期（見 Risks 段落既有的風險說明，此風險本來就假設這個檔案是持久存在／進版控的狀態，這裡只是把假設明文寫出來）
  - `web/README.md` 需記錄：專案如何啟動（`docker compose up -d web`）、`generate:api-types` 何時要重新執行與正確指令、Vitest 如何執行；此檔案本身正常進版控
  - **實作階段查證後發現的落差**：`api.generated.ts` 的 `components.schemas` 實際上**只有 Request 型別**（如 `CreateEventRequest`、`LoginRequest`），沒有任何 Response 型別（`EventDto`／`MemberProfileDto`／`OrderSummaryDto`／`OrderDetailDto`／`TicketTypeDto`／`AuthTokensDto`／`EventSeatDto` 都不存在於產生的檔案中）。原因是後端 Controller 都回傳 `IActionResult`（如 `return Ok(events)`），.NET 的 `AddOpenApi()` 無法從這種寫法反推 Response schema，除非額外標註 `[ProducesResponseType(typeof(T), 200)]`。與使用者確認過（2026-08-17）：本輪**不修改後端程式碼**（含加註 metadata），改為在 `src/types/apiResponses.ts` 手寫這 7 個 Response DTO 對應的 TS interface——這幾個 DTO 欄位少且結構單純，手寫維護成本可接受；代價是這 7 個型別退回「手動謄寫」，後端 DTO 改欄位時前端不會在編譯期自動抓到，需要開發者自行留意。若之後後端補上 `ProducesResponseType` 標註，可以把這個檔案裁撤、改回全部由 `api.generated.ts` 產生
  - **`api.generated.ts` 與手寫型別的界線**：`src/types/api.generated.ts` 只能由 `generate:api-types` 產生，**禁止手動編輯**（下次重新產生會整檔覆蓋，手改的內容會憑空消失）。純前端本地狀態用的型別（例如座位選擇的 UI 暫存狀態、場館暫存清單這種不是任何 API DTO 的資料結構、表單草稿狀態）另外放在 `src/types/ui.ts`，命名上不得與 `api.generated.ts` 內的型別同名，避免 import 時混淆兩者來源
  - 考慮過的替代方案：手動維護對應的 TS interface（拒絕，過去 session 已多次因為手動同步的地方漂移而出過真實 bug，這裡直接用產生工具杜絕同一類問題）

### 5. Auth 狀態管理與 Token 存放：Pinia `auth` store，access token 存記憶體、refresh token 存 `localStorage`，401 觸發「single-flight」自動換發，登入後另呼叫 `/api/members/me` 取得角色

- Pinia store `useAuthStore`：持有目前登入的 `member`（含角色，來源見下方）、`accessToken`（僅存在記憶體，不落地）、以及觸發登入/登出/換發的 action
- `refreshToken` 存 `localStorage`：讓使用者重新整理頁面不用重新登入（App 啟動時若偵測到 `localStorage` 有 refresh token，先呼叫既有 Refresh 端點換一個新的 access token）；access token 刻意只存記憶體，降低 XSS 情境下 access token 被讀取利用的視窗（refresh token 本身仍有既有後端的過期/輪替機制）
- **角色取得方式**：查證 `LoginHandler`／`RefreshTokenHandler` 回傳的 `AuthTokensDto` 只有 `AccessToken`／`RefreshToken`，沒有角色欄位；角色雖然有內嵌在 JWT 的 `ClaimTypes.Role` claim 裡，但前端不解析 JWT payload 取角色（多一個跟 JWT 內部結構耦合的隱性依賴）。改為登入或换發成功後，緊接著呼叫既有 `GET /api/members/me`（回傳 `MemberProfileDto`，含 `Role` 欄位），把回傳的會員資料寫入 `auth` store，同時也讓「顯示會員名稱」等其他 UI 需求一併有資料可用
- **401 自動換發須做成 single-flight，不能每個 401 各自呼叫 Refresh**：查證 `RefreshTokenHandler` 的實作，refresh token 是**一次性＋輪替**機制——`existingToken.MarkAsUsed()` 後即失效，若同一顆已用過的 refresh token 被重複提交，後端會判定為「疑似遭竊」並呼叫 `RevokeAllTokensAsync` **撤銷該會員所有 token**。若頁面同時有多支 API 呼叫因 access token 同時過期而各自觸發 401→各自呼叫 Refresh，只有第一個換發請求會成功，其餘會因為送出「已被用過」的舊 refresh token 而觸發全面撤銷、把使用者強制登出——這是必然會在正常使用情境下出現的 bug，不是邊角案例。做法：`httpClient` 內維護一個模組層級的「進行中換發 Promise」，第一個 401 建立這個 Promise 並實際呼叫 Refresh，之後在它完成前抵達的其他 401 直接等待同一個 Promise、不重複呼叫，Promise 完成後所有等待中的請求一起用新 access token 重放；若換發失敗，所有等待中的請求一起判定為登入失效，清空 store 與 `localStorage`、導回登入頁
- **App 啟動時的 bootstrap refresh 流程**（查證後發現這個流程原本沒有明確定義，會導致重新整理頁面時路由守衛拿到不一致的登入狀態）：`main.ts` 建立 Pinia、`app.use(ElementPlus)` 後，先 `await authStore.bootstrapAsync()`，**確定完成後才 `app.use(router)`**（實作階段真的用瀏覽器測過才抓到：Vue Router 在 `app.use(router)` 當下就會非同步開始解析初始路由，不是等 `app.mount()` 才開始——如果 router 在 bootstrap 完成前就裝進 app，第一次導覽的 `beforeEach` guard 會在 `isAuthenticated` 還是 `false` 的狀態下跑完並導去 `/login`，之後就算 `bootstrapAsync()` 換發成功，畫面也不會自動導回來，因為沒有東西會重新觸發那次導覽——使用者重新整理一個受保護頁面後，即使 refresh token 仍然有效，也會被卡在登入頁）。確定 `app.use(router)` 之後才 `app.mount()`。此方法檢查 `localStorage` 有無 `refreshToken`：沒有則直接視為未登入並 resolve；有則呼叫 Refresh 端點換一次新 access token，成功後接著呼叫 `GET /api/members/me` 寫入 `member`。`bootstrapAsync()` **不能一律吞掉例外**，須區分兩種情況：
  - **預期錯誤**（Refresh 端點明確回傳 401，即 refresh token 已失效／被撤銷）→ 視為未登入：清空 `localStorage` 的 refresh token 與 store 狀態
  - **非預期錯誤**（網路中斷、逾時、5xx）→ **不清空** `localStorage`（refresh token 本身可能仍有效，只是這次請求失敗，清掉會強迫使用者不必要地重新登入）；store 狀態設為「未登入但發生連線錯誤」（`authStore.bootstrapError = true`），不是靜默當成登出處理
  - `GET /api/members/me` 這一步失敗的分類（查證 `GetMyProfileHandler` 後確認：不檢查 `IsActive`，只可能回 401（JWT 本身無效，理論上剛換發完不該發生，但仍需處理）或 404「找不到會員資料」（帳號被刪除的邊緣案例））：401／404 都歸類為**預期錯誤**，比照 Refresh 401 處理——清空 `localStorage` 並視為未登入；網路錯誤／5xx 則歸類為**非預期錯誤**，處理方式同上一點
  - 兩種情況都**不拋出例外中斷 `main.ts` 的 `await`**，差別只在清不清 `localStorage`；此階段一律**不主動導頁**（還沒開始路由導覽，導頁交給之後 router 的 `beforeEach` 判斷）
  - **非預期錯誤的顯示位置**：`app.mount()` 照常執行（不因 bootstrap 錯誤而卡住），`beforeEach` guard 因為 `isAuthenticated=false` 一律導向 `/login`（安全預設，不假設使用者已登入）；`App.vue` 根層級（`<router-view>` 外層）放一個全域錯誤 banner，讀 `authStore.bootstrapError`，顯示「無法確認登入狀態，請檢查網路連線」並提供「重試」按鈕（呼叫 `authStore.bootstrapAsync()` 重跑一次）——不侷限於登入頁本身，因為 guard 實際導向哪個頁面取決於使用者原本想開啟的網址
  - Router 的 `beforeEach` guard 建立在「`bootstrapAsync()` 已經跑完」的前提上，guard 本身只同步讀 store 狀態，不再自己 await 任何非同步流程。`index.html` 內建一個靜態 loading 畫面，`app.mount()` 完成後才移除，避免 await 期間畫面閃爍未登入態
- **401 換發／登入／登出的防遞迴與重試邊界**（查證後發現若不設邊界，refresh 呼叫本身收到 401 會遞迴呼叫自己，或換發後重試仍 401 會無限重試）：
  - Refresh 端點本身的呼叫**不經過** `httpClient` 的 401 攔截器（用底層 fetch 直接呼叫，或用不掛攔截器的獨立 client instance），避免 refresh 呼叫收到 401 時又觸發另一次 refresh，造成遞迴
  - 每個業務請求最多重試一次：請求物件上標記是否已經是「換發後重試」，若換發後重試仍收到 401，直接判定登入失效、清空狀態、導向登入頁，不再觸發第二次 refresh
  - 登入（`/auth/login`）與換發（`/auth/refresh`）**本身**回傳的 401／422 等業務錯誤（如密碼錯誤、refresh token 已失效），不觸發自動換發流程——這兩支端點的請求走獨立路徑，不掛在共用攔截器上，避免把「登入失敗」誤判成「token 過期」
  - 登出流程一律**本地狀態清空優先**：呼叫後端 Logout API 是 best-effort，該次呼叫失敗（含 401）只記 log、不阻擋清空 `accessToken`／`member`／`localStorage` 內 refresh token 並導向登入頁的動作
- 考慮過的替代方案：兩個 token 都放 `localStorage`（拒絕，XSS 風險比純記憶體存 access token 更大，且既有後端已經設計了 refresh token 輪替機制，值得在前端也做對應的風險分層）；前端解析 JWT payload 取角色（拒絕，見上）；每個 401 各自呼叫 Refresh（拒絕，見上，查證後確認會導致併發情境下的誤登出）

### 6. UI 元件庫：Element Plus

- Admin 後台有多組資料表格（場館/座位圖/活動/票種/訂單列表）與表單（建立場館、建立活動等），若每個都手刻表格排序/分頁/驗證錯誤呈現，重工成本高且容易不一致
- 選擇 Element Plus：對 Vue 3 + `<script setup>` + TypeScript 支援完整、Table/Form/Pagination 元件成熟，符合「不做投機性抽象，但重複邏輯出現就該共用」的既有專案原則（這裡是「大量重複的表格/表單需求」觸發引入，而非預先假設未來需要）
- 買家端頁面較簡單（列表、選位、下單流程），不強制使用 Element Plus 的重元件，可視畫面需求用原生元素＋少量 Element Plus 元件（如 Button、Form）
- 考慮過的替代方案：不用元件庫、全部手刻（拒絕，Admin 端表格/表單重工成本評估後不划算）；Vuetify/PrimeVue（未選用，Element Plus 對 Composition API 的文件與既有實務案例較成熟，非必要不需要比較所有選項）

### 7. 測試策略：Vitest 為主，E2E 留待後續

- `web/` 引入 Vitest（Vite 官方生態系一致的測試框架）+ `@vue/test-utils`，優先覆蓋 `src/api/`（service 層邏輯，如 401 自動 refresh 的重試邏輯）與 `src/stores/`（auth store 的狀態轉換）
- 元件測試（頁面互動）視畫面複雜度決定是否需要，非本輪強制要求
- E2E（真的開瀏覽器跑完整購票流程）留待後續：本輪先求架構與骨架可跑，畫面細節還會變動，過早投資 E2E 腳本維護成本高
- **哪些 Acceptance Criteria 只靠手動驗證，不是 Vitest 涵蓋**：Vitest 能驗證的是「邏輯」——`httpClient`／`auth` store／路由守衛的狀態轉換與呼叫行為（見 tasks.md 2.4、3.3、3.4、3.6、3.8）。以下 Requirement 的驗收 Vitest **不會**涵蓋，只靠 tasks.md 4.6／5.6 的手動瀏覽器操作驗證，本輪不補元件測試或 E2E（見上方 Non-Goals）：
  - `buyer-web-ui`：「買家可瀏覽活動列表與座位可售狀態」「買家可選位並送出訂單」「買家可在下單結果頁確認或取消剛下的訂單」「『我的訂單』列表與明細本輪僅顯示空狀態」
  - `admin-web-ui`：「Admin 可透過介面管理場館與座位圖」（含手動輸入 Id 流程）「Admin 可透過介面管理活動與票種」「Admin 可查看所有訂單列表與明細」
  - 已被 Vitest 覆蓋、不需要手動驗證邏輯部分的：「未登入使用者存取需登入頁面時導向登入頁」「買家可透過介面註冊與登入」（登入/登出/角色寫入邏輯）「Access Token 過期時前端自動換發，換發失敗導回登入頁」「Admin 後台路由僅限 Admin 角色進入」——但這些 Requirement 的畫面呈現本身仍會在 4.6／5.6 一併手動走過一次

## 實作階段查證後發現的落差：下單回應沒有 `heldUntilUtc`

- 查證 `OrdersController.PlaceOrder` 與 `OrderService.PlaceOrderAsync` 後確認：成功回應只有 `{ id }`，沒有持有到期時間欄位；實際的持有時長是 `CreateOrderHandler.HoldDuration`（寫死的 `TimeSpan.FromMinutes(10)`）內部計算後寫入 `Order`，從未回傳給呼叫端
- 與 `buyer-web-ui` spec「下單結果頁顯示訂單 Id 與持有到期時間，只用下單當下回應的資料」字面上對不上——回應裡真的沒有這個欄位可用
- 沿用「不動後端、前端手段解決」的既定方向（與 P0-1／apiResponses.ts 的落差同一類處理原則）：訂單結果頁的持有到期時間改為**前端在收到 201 回應當下用 `now + 10 分鐘` 推算顯示**，10 分鐘是對照後端 `HoldDuration` 常數寫死在前端（`OrderResultPage.vue` 內加註解說明來源），不是解析回應欄位
- 風險：後端這個常數之後若調整，前端不會自動同步，需要開發者手動對照修改；影響範圍小（只有這一個顯示用途），先接受，不在本輪加額外機制同步

## Security

> 依 CLAUDE.md 安全強制規則，本次涉及「接受外部輸入」「身份驗證/授權邏輯」，實作前需明確回答輸入驗證邊界。

- **前端／後端驗證分工**：前端對表單執行**必要的**格式、長度、必填驗證（Email 格式、密碼最短長度、場館名稱非空、活動時間格式、票種價格為正數、場館/座位圖 Id 為合法 GUID 格式——見 Non-Goals 的手動輸入 Id 情境），目的是提前給使用者清楚的錯誤訊息、減少無謂的後端往返。**後端仍是最終驗證與授權邊界，前端驗證不取代後端驗證**——既有後端 Handler 已有 FluentValidation／DataAnnotations 驗證與授權檢查（如 `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`），前端繞過或關閉 JS 驗證不會產生安全漏洞，只會被後端擋下並回傳結構化的 `ProblemDetails` 錯誤，前端統一由 `httpClient`（決策 4）解析呈現
- **請求格式**：所有 API 呼叫透過 `httpClient` 以結構化 JSON body／URL query parameter 呼叫既有端點，不拼接 SQL 或 shell 指令字串（前端本身不碰 DB／shell，重申既有 CLAUDE.md 原則，避免之後有人在這層加入字串拼接的捷徑）
- 涵蓋範圍對照：Email／密碼（`auth.ts`，登入/註冊表單，4.1）、場館名稱（建立場館表單，5.2）、活動欄位（建立活動表單，含手動輸入的場館/座位圖 GUID，5.3）、票種價格（建立票種表單，5.3）、GUID 格式（場館/座位圖 Id 手動輸入欄位，5.3；見上方 Non-Goals）

## Risks / Trade-offs

- **[風險] Access token 只存記憶體，SPA 重新整理後短暫需要等 refresh 完成才能打 API** → 緩解：App 啟動流程統一在路由守衛前先 await 一次 refresh，使用者感受到的是短暫的載入畫面，不是功能異常
- **[風險] `openapi-typescript` 產生的型別依賴後端 Swagger 文件即時性，若忘記重新產生會用到過期型別** → 緩解：型別不一致只會讓 TS 編譯失敗（欄位改名/移除時），不會是靜默的執行期錯誤；純新增欄位的情境仍可能被忽略，之後可視需要加 CI 檢查兩者是否同步，本輪先不做（Non-Goal 範圍外）
- **[風險] Element Plus 是新依賴，等於前端多一個要跟著升級的第三方套件** → 緩解：只在 Admin 後台重度使用，買家端保持輕量，未來若要換掉，影響範圍集中在 `pages/admin/`
- **[Trade-off] 前端 Docker 開發模式用 Vite dev server（非 production build）** → 這是刻意選擇：本專案目前沒有正式環境部署需求，production build/Nginx 等留到真的要上線時再處理，避免現在做用不到的設定
- **[Trade-off] Admin 場館/座位圖選擇本輪用手動輸入 Id，不是下拉選單** → 這是與使用者確認過的範圍縮小（2026-08-17，見 Non-Goals），代價是 Admin 操作體驗變差、容易貼錯 Id 導致建立活動失敗；緩解：前端做 GUID 格式驗證＋清楚的錯誤訊息，且建立場館/座位圖成功後把新 Id 明顯展示方便複製；真正解法待下一個 OpenSpec 變更補齊查詢 API 後把這幾個表單換成下拉選單

## Migration Plan

- 純新增，不影響現有 `db`／`api` service 與既有後端程式碼
- 開發者執行 `docker compose up -d`（或 `docker compose up -d web` 只啟動前端）即可在 `http://localhost:${WEB_HOST_PORT:-5173}` 存取
- 無資料庫遷移、無既有服務的破壞性變更，不需要 rollback 策略

## Open Questions

- 是否要在專案早期就導入前端 CI（lint/build/test 自動跑）？本輪先不處理，待前端有實際內容後再評估（目前專案沒有 remote/CI 環境）
- Element Plus 的語系/主題客製化本輪不深究，先用預設值，待畫面實作階段再依實際需求調整
- 下一個 OpenSpec 變更需要規劃：Admin 場館／座位圖／活動的查詢 API（例如 `GET /api/admin/venues`、`GET /api/admin/venues/{id}/seat-maps`），補齊後回頭把本輪手動輸入 Id 的表單換成下拉選單，並把場館/座位圖列表頁從「session 暫存清單」換成真正的持久化查詢
