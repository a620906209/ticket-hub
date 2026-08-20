## Why

Phase 1（Must）目前只剩電子票券產出與核銷 API 尚未實作（`docs/project-scope.md` 第 9 節盤點）。訂單確認（付款成功）後，買家沒有可核銷的實體憑證，現場也沒有 API 能驗票放行——這是「建立活動 → 選票 → 下單 → 付款 → 出票 → 核銷」端到端主流程能否跑通的最後一塊，也是 Phase 1 完成、可對外展示的前提。

## What Changes

- 新增 `Ticket` Domain Entity：狀態機 `Issued`（已發放）→ `Redeemed`（已核銷）→ `Voided`（作廢，對應訂單取消）
- 訂單確認付款成功時（`ConfirmOrderHandler.Handle` 內 `order.Confirm()` 成功後），依訂單每筆 `OrderItem` 的購買數量自動出票：座位項目固定出 1 張、計數項目依 `Quantity` 出對應張數，每張票各自綁定唯一 Ticket ID
- 每張 Ticket 可**按需**產生 QR Code（QRCoder 套件），內容為 HMAC 簽章過的 Ticket ID，防止票券內容被竄改偽造；出票交易本身不呼叫此服務、不預先產生或持久化圖檔（見 design.md 決策 1、3）
- 新增核銷端點 `PATCH /api/admin/tickets/{id}/redeem`：需處理併發核銷防重複（同一張票被同時掃描兩次只能成功一次）、非法狀態轉換驗證（已核銷的票不可再次核銷、不存在的票回傳 404；`Voided` 本次無觸發路徑，不在此範疇內，見下方說明）
- `Voided` 狀態保留在狀態機定義中，但**本次不實作任何觸發路徑**——現有 `Order.Cancel()` 只允許 `Pending` 訂單取消（`src/ProjectC.Domain/Orders/Order.cs:58-64`），而 Ticket 只在訂單轉 `Paid` 後才出票，故「已出票訂單被取消」在現有 Domain 規則下不存在，屬於退款流程的範疇（`Refunded` 訂單狀態依 `docs/project-scope.md` 第 8 節待另開提案決定）。此為與 `docs/project-scope.md` 第 3 節原始盤點的落差，待該文件下次更新時一併修正
- **路由與 `docs/project-scope.md` 的落差（已修正）**：`project-scope.md` 第 2 節（Must 功能地圖）與第 4 節（外部整合表）原寫核銷端點為 `PATCH /tickets/{id}/redeem`，與本提案依既有 Admin 端點慣例（決策 5）採用的 `PATCH /api/admin/tickets/{id}/redeem` 不一致；審查後判定應提前修正而非留到歸檔前，已直接更新第 2、4 節路由文字對齊本提案
- **不含**：現場核銷掃碼前端頁面（`docs/project-scope.md` 標為 Could，本次範疇外）、第三方憑證/簽章服務（本地 HMAC 簽章，非第三方）、任何觸發 `Voided` 的流程（見上）

## Capabilities

### New Capabilities
- `ticket-issuance`：`Ticket` entity 定義、狀態機（Issued/Redeemed/Voided，`Voided` 本次無觸發路徑，見 What Changes）、訂單確認觸發自動出票的規則、依 Ticket ID 按需產生 QR Code + HMAC 簽章的規則
- `ticket-redemption`：核銷 API（`PATCH /api/admin/tickets/{id}/redeem`）的請求/回應、授權（誰可以核銷）、併發防重複核銷、非法狀態轉換的錯誤處理

### Modified Capabilities
- `ticket-purchase`：既有「透過 API 確認訂單（模擬付款）」需求的「買家確認自己的訂單成功」情境，付款成功後的系統行為需追加「觸發出票」這個後置條件（目前 spec 只描述訂單狀態轉 Paid、座位轉 Sold，未提及出票）

## Impact

- **新增程式碼**：`Domain.Tickets`（`Ticket` entity、狀態機、例外類別）、`Application.Tickets`（出票邏輯、核銷 Handler）、`Infrastructure`（`TicketConfiguration`、`ITicketRepository` 實作、EF migration、QR/HMAC 簽章服務實作）、`WebApi.Controllers.AdminTicketsController`（核銷端點）
- **既有程式碼調整**：`ConfirmOrderHandler.Handle`（訂單確認成功後掛出票邏輯）；`CancelOrderHandler` 本次不調整——`Voided` 無觸發路徑（見 What Changes），已出票訂單依現有規則不可能進入取消流程
- **新增依賴**：QRCoder 套件（NuGet）
- **DB schema**：新增 `Tickets` table，需要 EF Core migration
- **不影響**：前端（本次純後端範圍，比照 `ticket-type-requires-seat` 的做法）
