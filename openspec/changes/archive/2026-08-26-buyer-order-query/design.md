## Context

`OrdersController`（`/api/orders`）目前只有 `POST`（下單）、`POST /{id}/confirm`、`POST /{id}/cancel` 三個買家端點，沒有任何查詢端點。訂單查詢邏輯已存在，但目前只掛在 Admin-only 的 `order-administration` 能力下：

- `GetOrdersHandler.HandleAsync()`：回傳**全部**訂單（`IOrderRepository.GetAllAsync`），無買家過濾，只適合 Admin 用途
- `GetOrderByIdHandler.HandleAsync(orderId)`：依 Id 查單筆訂單明細（`OrderDetailDto`：Id、EventId、BuyerId、Status、HeldUntilUtc、Items），同樣無身份檢查，只在 `AdminOrdersController` 底下暴露

`ITicketRepository` 目前只有 `Add` 與 `GetForUpdateAsync`（悲觀鎖定，MUST 在交易內呼叫，用於核銷）；沒有任何唯讀查詢方法，也沒有依 `OrderItemId` 批次查票券狀態的能力。`Ticket` 本身只存 `OrderItemId`，不直接存 `BuyerId`——判斷票券歸屬須經 `Ticket → OrderItem → Order.BuyerId` 這條鏈。

`ticket-issuance` 能力已完整實作依 Ticket ID 產生 HMAC 簽章 QR 內容與 PNG 圖檔的邏輯（`ITicketSigningService`、`TicketQrCodeGenerator`），但目前在 `Program.cs` 沒有註冊、沒有任何呼叫端（見該類別自身註解）——這次是它第一個真正的消費端。

`buyer-web-ui` 的「我的訂單」列表頁與明細頁路由與框架已存在，但目前規格明確要求本輪只顯示固定空狀態、不呼叫任何查詢 API（等待這次變更補齊後端）。

## Goals / Non-Goals

**Goals：**
- 買家可查詢自己的訂單列表與單筆明細，明細含每個項目對應的票券狀態
- 買家可取得自己已出票票券的 QR Code（PNG），供核銷掃碼使用
- 買家 MUST NOT 能查到或存取非自己身份的訂單/票券
- `buyer-web-ui` 我的訂單列表/明細頁改為串接真實資料

**Non-Goals：**
- 訂單列表分頁／排序／篩選（現況買家訂單量級小，比照既有 Admin 列表也未分頁，維持一致）
- 現場掃碼驗票前端頁面（`docs/project-scope.md` 列為 Could，本次範疇外；本次只提供買家「看到」QR 圖檔的能力）
- 改變 `ticket-issuance` 能力本身的 QR/簽章規則（僅新增一個消費端，規則不變）
- Email／推播等主動通知買家票券已出（Should 範疇，見 `docs/project-scope.md` 第 2 節）

## Decisions

### 1. 沿用 `OrdersController`（`/api/orders`）新增 GET 端點，不另開買家專屬 Controller
比照既有下單/確認/取消都掛在同一個 Controller，僅套用 `[Authorize]`（不額外掛角色 Policy，比照現有 `OrdersController` 既定作法，任何已登入身份皆可呼叫），呼叫者身份一律透過 `User.GetMemberId()` 取得，資源隔離完全靠比對 `Order.BuyerId`／`Ticket` 所屬訂單的 `BuyerId` 達成，不靠角色限制呼叫者（見決策 5）。新增：
- `GET /api/orders`：呼叫者自己的訂單列表
- `GET /api/orders/{id}`：呼叫者自己的單筆訂單明細（含票券狀態）

**替代方案**：另開限定一般會員角色的 `BuyerOrdersController`——否決，現有 `OrdersController` 本來就沒有角色 Policy（純粹靠 `BuyerId` 過濾做資料隔離），新增角色限制會是本次變更額外引入、既有端點沒有的新規則，資源路由語意上沿用同一個 Controller 更一致，沒有理由拆開或另加角色檢查。

### 2. 買家查詢一律在 Repository 層以 `BuyerId` 過濾，不可沿用 Admin 的 `GetAllAsync`／既有 `GetOrderByIdHandler` 明細邏輯後才在記憶體中比對
`IOrderRepository` 新增 `GetByBuyerIdAsync(Guid buyerId, CancellationToken)`（列表用，AsNoTracking，載入 `Items`）。單筆明細沿用既有 `IOrderRepository.GetByIdAsync` 查出後，在 Handler 層檢查 `order.BuyerId == callerBuyerId`，不符 MUST 回 403（見決策 5）。

**替代方案**：讓 Handler 呼叫 Admin 既有的 `GetOrdersHandler`/`GetOrderByIdHandler` 再過濾——否決，這兩個 Handler 屬於 `order-administration` 能力、語意上就是「無身份限制的管理端查詢」，買家端刻意繞過查詢層過濾、只在應用層事後過濾，一旦未來 Handler 被改動（例如接分頁但漏過濾條件），買家端會被動連帶出問題，職責混在一起。買家與管理端各自獨立的查詢路徑，安全邊界更清楚。

### 3. 新增 `ITicketRepository.GetByOrderItemIdsAsync`（批次查詢）供訂單明細頁一次取得所有票券狀態
訂單明細需要顯示每個 `OrderItem` 對應的票券（座位項目 1 張、計數項目依 `Quantity` 可能多張）。新增批次查詢方法，一次傳入該訂單所有 `OrderItemId`、一次查詢取回全部對應 `Ticket`（AsNoTracking），避免在 Handler 內對每個 `OrderItem` 各自查一次造成 N+1。

### 4. QR Code 端點以 `ticketId` 為資源鍵，獨立於 Order 明細之外：`GET /api/tickets/{ticketId}/qr-code`
比照既有 `AdminTicketsController` 把 Ticket 當作扁平資源（`/api/admin/tickets/{id}/redeem`），買家端 QR 端點同樣用扁平的 `/api/tickets/{ticketId}/qr-code`，不巢狀在 `/api/orders/{orderId}/...` 底下，避免買家端點命名跟 admin 端點的資源模型不一致。此 `TicketsController` 僅套用 `[Authorize]`，不掛 `AuthorizationPolicies.AdminOnly`——與決策 1 的 `OrdersController` 一致，任何已登入身份皆可呼叫，資料隔離同樣靠比對 `BuyerId`（見決策 5），不靠角色限制。

歸屬判斷：`ITicketRepository` 新增唯讀 `GetByIdAsync(Guid id, CancellationToken)`（AsNoTracking，不加鎖，跟 `GetForUpdateAsync` 明確區分用途——後者 MUST 在交易內呼叫，前者不需要）；`IOrderRepository` 新增 `GetByOrderItemIdAsync(Guid orderItemId, CancellationToken)`，用於從 `Ticket.OrderItemId` 反查回 Order 以取得 `BuyerId`。Handler 流程：查 Ticket（不存在 → 404）→ 查 Order（依 `OrderItemId`）→ 比對 `BuyerId`（不符 → 403）→ 呼叫既有 `TicketQrCodeGenerator.GeneratePng(ticketId)` → 回應 `image/png`。

**替代方案**：單一 JOIN 查詢直接回傳「是否為本人的 Ticket」，找不到與非本人合併回 404（隱藏資源存在性，IDOR 防護更嚴格）——列入決策 5 一併討論，最終否決，改採跟既有 confirm/cancel 端點一致的 403/404 分流（見下）。

### 5. Ownership 驗證：非本人查詢／存取回 403，資源不存在回 404，比照既有 `ticket-purchase`「非本人確認他人訂單」的既定慣例
訂單明細、票券 QR 端點皆遵循此規則。安全確認問題「是否可能讓未授權使用者透過猜測 ID 存取他人資料」：Id 皆為 GUID，實務上不可猜測；403 會揭露「這個 Id 存在、但不是你的」，理論上仍是比 404-only 更寬鬆的存在性揭露，但這是本專案既有、經過先前 `ticket-purchase` spec 審查通過的慣例（見 CLAUDE.md Rule 11：符合既有慣例優先於各自表述），本次不引入不一致的新模式。

### 6. `buyer-web-ui` 前端串接：明細頁新增票券區塊，QR 圖檔改用既有攔截器取 Blob 後以 Object URL 顯示，不直接把 `<img src>` 指向端點網址
明細頁對每個 `OrderItem` 顯示對應的票券狀態（`Issued`/`Redeemed`）；`Issued` 狀態的票券顯示「查看 QR Code」，取得圖檔顯示。由於瀏覽器 `<img src>` 原生不會帶自訂 `Authorization` Header，前端 MUST 改用既有的 API 攔截器（`fetch`/`axios`）取得圖檔 `Blob` 後轉成 `Object URL` 顯示，不可直接把 `<img src>` 指向需要驗證的端點網址（比照現有 `buyer-web-ui` API 呼叫一律經攔截器帶 Auth Header 的既有規範）。

**實作層次拆分**（釐清「頁面邏輯」與「攔截器帶 Header」分屬不同測試層級，避免混在同一個測試斷言）：
- `web/src/api/httpClient.ts` 現有的 `request<T>()` 內部寫死 `response.json()` 解析，無法回傳二進位內容；新增 `requestBlob(path, init)`，沿用與 `request()` 相同的 Authorization Header 注入／401 自動換發邏輯，但回傳 `response.blob()`（不解析 JSON）
- `web/src/api/orders.ts` 的 `getTicketQrCodeBlob(ticketId)` 呼叫 `requestBlob`，不自己另外處理 fetch/Header
- `OrderDetailPage.vue` 呼叫 `getTicketQrCodeBlob` 取得的 `Blob` 轉 Object URL 顯示，元件本身不知道、也不需要知道 Header 是怎麼帶上去的

**替代方案**：讓 `getTicketQrCodeBlob` 自己組一份獨立的 `fetch` 呼叫（不經過 `httpClient.ts`）——否決，會重複實作 Authorization Header 注入與 401 換發邏輯，違反現有「API 呼叫集中於獨立的 service/api 層」與攔截器統一帶 Header 的既有規範（見 CLAUDE.md 前端規範）。

## Risks / Trade-offs

- **[Risk]** 403 回應揭露票券/訂單 Id 存在性，理論上比統一 404 寬鬆 → **Mitigation**：延續本專案既有、已審查通過的 403/404 慣例（見決策 5），Id 空間為 GUID 實務不可窮舉猜測，風險可接受
- **[Risk]** `IOrderRepository`／`ITicketRepository` 新增多個查詢方法，介面逐漸變胖 → **Mitigation**：本次新增方法皆為單純唯讀查詢（無業務邏輯），符合既有 Repository 職責；若未來查詢方法持續增加到難以維護，屆時再評估拆分唯讀查詢介面（CQRS 風格），非本次範疇
- **[Risk]** QR 圖檔透過 Blob + Object URL 顯示，前端需記得在元件卸載時 `URL.revokeObjectURL` 釋放記憶體，遺漏會造成記憶體洩漏（單頁使用量小，非阻斷風險）→ **Mitigation**：tasks.md 內明確列出此清理步驟

## Open Questions

無——本次範疇單純（新增唯讀查詢端點 + 既有 QR 產生能力串接），關鍵技術決策已在上方定案。
