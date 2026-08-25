## 1. Domain 層：Repository 介面擴充

- [x] 1.1 `IOrderRepository` 新增 `GetByBuyerIdAsync(Guid buyerId, CancellationToken)`：回傳指定買家的所有訂單（需載入 `Items`，比照既有 `GetAllAsync`/`GetByIdAsync` 的載入慣例）
- [x] 1.2 `IOrderRepository` 新增 `GetByOrderItemIdAsync(Guid orderItemId, CancellationToken)`：依 `OrderItem.Id` 反查回其所屬 `Order`（需載入 `Items`），查無資料回傳 `null`
- [x] 1.3 `ITicketRepository` 新增唯讀 `GetByIdAsync(Guid id, CancellationToken)`：不加鎖、AsNoTracking，與既有 `GetForUpdateAsync`（MUST 在交易內呼叫）明確區分用途，查無資料回傳 `null`
- [x] 1.4 `ITicketRepository` 新增 `GetByOrderItemIdsAsync(IReadOnlyList<Guid> orderItemIds, CancellationToken)`：批次依多個 `OrderItemId` 一次查回所有對應 `Ticket`（AsNoTracking），避免逐筆查詢造成 N+1；空清單輸入回傳空清單，不查詢

## 2. Infrastructure 層：Repository 實作

- [x] 2.1 `OrderRepository` 實作 `GetByBuyerIdAsync`（`AsNoTracking`，`Include`/載入 `Items`，依 `BuyerId` 過濾）
- [x] 2.2 `OrderRepository` 實作 `GetByOrderItemIdAsync`（`AsNoTracking`，依 `Items` 集合內 `Id` 反查所屬 `Order`）
- [x] 2.3 `TicketRepository` 實作 `GetByIdAsync`（`AsNoTracking`，單筆查詢，不加鎖）
- [x] 2.4 `TicketRepository` 實作 `GetByOrderItemIdsAsync`（`AsNoTracking`，`WHERE OrderItemId IN (...)` 一次查詢）
- [x] 2.5 `Program.cs` 補回 `TicketQrCodeGenerator` 的 DI 註冊（`AddTransient`，先前 `ticket-issuance-and-redemption` code review 因無呼叫端而移除，本次是它的第一個消費端）

## 3. Application 層：查詢 Handler

- [x] 3.1 新增 `GetMyOrdersHandler`：呼叫 `IOrderRepository.GetByBuyerIdAsync`，回傳訂單摘要清單 DTO（Id、EventId、狀態、`HeldUntilUtc`），比照既有 `OrderSummaryDto` 欄位但另建買家專屬 DTO（避免跟 `order-administration` 的 DTO 共用型別、之後演化互相牽動）
- [x] 3.2 新增 `GetMyOrderDetailHandler`：查訂單（`IOrderRepository.GetByIdAsync`）→ 不存在回 `Result.Failure(Error.NotFound)` → `order.BuyerId != callerBuyerId` 回 `Result.Failure(Error.Forbidden)` → 通過後依訂單所有 `Items` 的 Id 呼叫 `ITicketRepository.GetByOrderItemIdsAsync` 批次取得票券、依 `OrderItemId` 分組附加到對應項目，回傳含票券清單的明細 DTO
- [x] 3.3 新增 `GetTicketQrCodeHandler`：查票券（`ITicketRepository.GetByIdAsync`）→ 不存在回 `Result.Failure(Error.NotFound)` → 依 `ticket.OrderItemId` 查所屬訂單（`IOrderRepository.GetByOrderItemIdAsync`）→ `order.BuyerId != callerBuyerId` 回 `Result.Failure(Error.Forbidden)` → 通過後呼叫 `TicketQrCodeGenerator.GeneratePng(ticketId)` 回傳 PNG bytes（不依票券狀態限制，`Issued`/`Redeemed` 皆可取得，見 design.md 決策 4）

## 4. WebApi 層：買家端點

- [x] 4.1 `OrdersController` 新增 `GET /api/orders`：呼叫 `GetMyOrdersHandler`，回傳呼叫者自己的訂單摘要清單
- [x] 4.2 `OrdersController` 新增 `GET /api/orders/{id:guid}`：呼叫 `GetMyOrderDetailHandler`，`Result` 映射為 200／403／404
- [x] 4.3 新增 `TicketsController`（`/api/tickets`，買家專屬，`[Authorize]`，比照 `AdminTicketsController` 是獨立 Controller 但不掛 Admin Policy）：`GET /api/tickets/{id:guid}/qr-code`，呼叫 `GetTicketQrCodeHandler`，成功回傳 `image/png`（`File(bytes, "image/png")`），失敗依 `Result` 映射為 403／404

## 5. 前端：`buyer-web-ui` 串接

- [x] 5.1 `web/src/api/httpClient.ts` 新增 `requestBlob(path, init)`：沿用 `request<T>()` 相同的 Authorization Header 注入／401 自動換發邏輯，但回傳 `response.blob()`，不解析 JSON（見 design.md 決策 6「實作層次拆分」）
- [x] 5.2 `web/src/api/orders.ts` 新增 `getMyOrders()`、`getMyOrderDetail(orderId)`、`getTicketQrCodeBlob(ticketId)`（最後一個呼叫 `requestBlob`，不可自己另組 `fetch` 或用 `<img src>` 直接指向端點）
- [x] 5.3 `MyOrdersPage.vue` 移除固定空狀態，改呼叫 `getMyOrders()` 顯示訂單清單：狀態一律顯示；持有到期時間僅在訂單狀態為 Pending 時顯示為「保留至 {時間}」，其他狀態不顯示此欄位（見 spec.md「HeldUntilUtc 為原始值、不因終態而清空」的語意）；無訂單時顯示空清單提示（非錯誤）
- [x] 5.4 `OrderDetailPage.vue` 移除固定空狀態，改呼叫 `getMyOrderDetail(orderId)` 顯示訂單狀態（同上，僅 Pending 顯示「保留至 {時間}」）與每筆項目對應的票券清單；票券狀態為 `Issued`/`Redeemed` 顯示「查看 QR Code」操作，尚未出票的項目顯示「尚未出票」——`Voided` 狀態現況不可達（見 `ticket-issuance` 能力規格），本次不實作任何對應顯示邏輯
- [x] 5.5 `OrderDetailPage.vue` 實作「查看 QR Code」：呼叫 `getTicketQrCodeBlob` 取得 Blob，`URL.createObjectURL` 顯示於 `<img>`，元件卸載或切換票券時 `URL.revokeObjectURL` 釋放（見 design.md Risks）
- [x] 5.6 `OrderDetailPage.vue` 對訂單明細 API 回傳 404 顯示「找不到這筆訂單」提示、回傳 403 顯示「你沒有權限查看這筆訂單」提示，兩種情況皆提供返回「我的訂單」列表的操作、不顯示任何訂單資料

## 6. 測試：buyer-order-query（對應 specs/buyer-order-query/spec.md）

- [x] 6.1 [單元測試] `GetMyOrdersHandlerTests`：買家查自己的訂單列表，回傳僅屬於自己的訂單摘要（對應 Scenario「查詢自己的訂單列表」）
- [x] 6.2 [單元測試] `GetMyOrdersHandlerTests`：買家從未建立過訂單，回傳空清單（對應 Scenario「尚未有任何訂單」）
- [x] 6.3 [單元測試] `GetMyOrderDetailHandlerTests`：買家本人查已出票訂單，回傳訂單狀態與每筆項目的票券清單、狀態正確（對應 Scenario「查詢自己的訂單明細（已出票）」）
- [x] 6.4 [單元測試] `GetMyOrderDetailHandlerTests`：買家本人查 Pending、尚未出票的訂單，票券清單為空、不視為錯誤（對應 Scenario「查詢自己尚未確認付款的訂單明細」）
- [x] 6.5 [單元測試] `GetMyOrderDetailHandlerTests`：非本人查詢回傳 403（對應 Scenario「非本人查詢他人訂單明細」）
- [x] 6.6 [單元測試] `GetMyOrderDetailHandlerTests`：查詢不存在的訂單回傳 404（對應 Scenario「查詢不存在的訂單」）
- [x] 6.7 [單元測試] `GetTicketQrCodeHandlerTests`：買家本人對 `Issued` 票券取得 QR Code PNG（對應 Scenario「買家取得自己已出票票券的 QR Code」）
- [x] 6.8 [單元測試] `GetTicketQrCodeHandlerTests`：買家本人對 `Redeemed` 票券仍可取得 QR Code（對應 Scenario「買家取得自己已核銷票券的 QR Code」）
- [x] 6.9 [單元測試] `GetTicketQrCodeHandlerTests`：非本人取得他人票券 QR 回傳 403（對應 Scenario「非本人取得他人票券的 QR Code」）
- [x] 6.10 [單元測試] `GetTicketQrCodeHandlerTests`：查詢不存在的票券回傳 404（對應 Scenario「查詢不存在的票券」）
- [x] 6.11 [整合測試] `OrdersControllerTests`：未帶 Authorization Header 呼叫訂單列表端點回傳 401（對應 Scenario「未登入查詢訂單列表」）
- [x] 6.12 [整合測試] `TicketsControllerTests`：未帶 Authorization Header 呼叫 QR Code 端點回傳 401（對應 Scenario「未登入查詢票券 QR Code」）
- [x] 6.13 [整合測試] `OrdersControllerTests`：已登入買家呼叫 `GET /api/orders/{id}`、`GET /api/orders` 走完整 HTTP pipeline 驗證 200 情境（覆蓋 6.1/6.3，確認 Controller 層 wiring 正確，非重複 AC）
- [x] 6.14 [整合測試] `TicketsControllerTests`：已登入買家呼叫 QR Code 端點取得的回應 `Content-Type` 為 `image/png` 且 body 非空（覆蓋 6.7，確認 Controller 層檔案回應正確）

## 7. 測試：buyer-web-ui（對應 specs/buyer-web-ui/spec.md MODIFIED，比照既有 `EventListPage.test.ts`/`EventCreatePage.test.ts` 的 Vitest 慣例）

- [x] 7.1 [前端單元測試／Vitest] `MyOrdersPage.test.ts`：mock `getMyOrders` 回傳 Pending、Paid、Cancelled、Expired 訂單，斷言僅 Pending 顯示「保留至 {時間}」；另驗證空清單提示與 API 失敗錯誤提示（對應 Scenario「開啟我的訂單列表頁」）
- [x] 7.2 [前端單元測試／Vitest] `OrderDetailPage.test.ts`：mock `getMyOrderDetail` 回傳已出票（Paid）訂單，斷言渲染票券清單、`Issued` 票券顯示「查看 QR Code」操作，且不顯示持有到期時間（對應 Scenario「開啟訂單明細頁查看已出票訂單」）
- [x] 7.3 [前端單元測試／Vitest，頁面層] `OrderDetailPage.test.ts`：mock **`getTicketQrCodeBlob`（service 函式本身）**回傳一個 `Blob`，點選「查看 QR Code」後斷言以正確的 `ticketId` 呼叫該函式、建立 Object URL 並顯示於畫面，切換票券或卸載元件時呼叫 `URL.revokeObjectURL`（對應 Scenario「點選查看 QR Code」）——**此測試不驗證 Authorization Header**，Header 注入邏輯屬於 `requestBlob`，由 7.3a 驗證
- [x] 7.3a [前端單元測試／攔截器層] `httpClient.test.ts` 擴充：呼叫 `requestBlob(path)` 時斷言底層 `fetch` 請求帶正確 `Authorization` Header（比照該檔案既有 `request()` 的 Header 驗證測試手法），確認 QR Code 的 Blob 請求跟其他 API 呼叫走同一套攔截器，不是獨立實作
- [x] 7.4 [前端單元測試／Vitest] `OrderDetailPage.test.ts`：mock `getMyOrderDetail` 回傳 Pending、票券清單為空的訂單，斷言顯示「保留至 {時間}」、每筆項目顯示「尚未出票」、不顯示「查看 QR Code」操作（對應 Scenario「開啟尚未出票訂單的明細頁」）
- [x] 7.5 [前端單元測試／Vitest] `OrderDetailPage.test.ts`：mock `getMyOrderDetail` 回傳 404 錯誤，斷言顯示「找不到這筆訂單」提示與返回列表操作、不渲染任何訂單資料（對應 Scenario「直接以網址開啟不存在的訂單明細頁」）
- [x] 7.6 [前端單元測試／Vitest] `OrderDetailPage.test.ts`：mock `getMyOrderDetail` 回傳 403 錯誤，斷言顯示「你沒有權限查看這筆訂單」提示與返回列表操作、不渲染任何訂單資料（對應 Scenario「直接以網址開啟非本人的訂單明細頁」）

> **刻意不安排測試的範圍**：`Ticket.Voided` 現況於整個系統無任何觸發路徑（見 `ticket-issuance` 能力規格），本節不建立任何驗證「頁面如何顯示 `Voided` 票券」的測試任務——這是刻意排除而非遺漏，比照 `ticket-issuance-and-redemption` tasks.md 對 `Voided` 的既定處理方式。

## 8. Spec 同步確認

- [x] 8.1 實作完成後，確認 `openspec/changes/buyer-order-query/specs/` 下兩份 delta（`buyer-order-query` 新增、`buyer-web-ui` 修改）與最終實作行為一致，逐條核對授權語意（401/403/404）、票券狀態不限制 QR 存取、批次查詢無 N+1
- [ ] 8.2 實作完成後，向使用者確認並更新 `docs/project-scope.md` 第 9 節「Phase 1 Must 盤點快照」：前端 RWD 那列「買家我的訂單查詢」缺口移除，快照日期與備註更新為本次變更

## 9. Strict Review 後續追蹤

- [x] 9.1 更新 `Program.cs` 中 `TicketQrCodeGenerator` 已無呼叫路徑／未註冊 DI 的過時註解，使其與目前 QR Code 查詢功能一致。
- [x] 9.2 更新 `TicketQrCodeGenerator.cs` 中宣稱目前沒有呼叫路徑的過時註解，使其與 `GetTicketQrCodeHandler` 的使用關係一致。
- [ ] 9.3 後續提交時，評估依後端、前端與 OpenSpec 文件拆分 commit，降低單一變更集的審查範圍。
