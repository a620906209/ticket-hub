## Why

`ticketing-purchase`（已歸檔）把「後台訂單查看」與「逾時訂單背景清理」明確拆出去留給下一個 change。目前 Admin 完全看不到任何訂單資料，只能透過 pgAdmin 直接查資料庫；逾時的 Pending 訂單也沒有任何自動清理機制——座位會一直卡在 `GetStatus(now)` 推導出的 Expired 狀態，只有等下一個買家嘗試鎖定同一批座位、觸發覆寫鎖定時才會間接被釋放，資料庫裡的訂單本身永遠停留在 Pending，從未真的轉為 Cancelled。這次補上這兩塊，讓售票流程的營運可觀測性與資料衛生完整。

## What Changes

- 新增 Admin 訂單查看 API（需 Admin 角色）：`GET /api/admin/orders`（訂單列表，含即時狀態）、`GET /api/admin/orders/{id}`（訂單明細，含座位項目）。
- 新增背景清理服務（`BackgroundService`）：以固定週期（可設定，預設 5 分鐘）掃描所有 `Status = Pending` 且已逾期（`HeldUntilUtc` 已過）的訂單，逐筆鎖座位、取消訂單、釋放座位，讓資料庫真正反映訂單已終結，不再依賴「下一個買家覆寫鎖定」這種被動、間接的清理方式。
- `IOrderRepository` 新增唯讀查詢方法：`GetAllAsync`（供 Admin 列表）、`GetExpiredPendingOrderIdsAsync(now)`（供背景清理掃描，只回傳 Id 清單，避免一次把所有訂單物件連同 Items 全部載入記憶體）。
- 新增 `OrderService.CancelExpiredOrderAsync(orderId)`：跟既有 `CancelOrderAsync` 共用「鎖座位 → 鎖後重讀 → 呼叫 `CancelOrderHandler.Handle` → Commit」的核心流程，但**不做本人驗證，改為驗證訂單確實已逾時**（背景程序沒有「呼叫者」這個概念，用「訂單本身已逾時」取代「呼叫者是買家本人」作為授權依據，避免這個方法在不驗證任何條件的情況下被誤用來取消未逾時的訂單）。

本次不包含：訂單搜尋/篩選（依買家、活動、狀態查詢）、分頁、清理間隔的動態調整 API——先求核心功能正確，介面留到有實際需求時再擴充。

## Capabilities

### New Capabilities
- `order-administration`：Admin 查看所有訂單列表與明細；系統背景週期性清理逾時仍為 Pending 的訂單並釋放座位。

### Modified Capabilities
（無——這次不新增或修改任何買家端或既有 `ticket-ordering`/`ticket-purchase` 的行為規範，`CancelOrderHandler` 本身的取消規則已經在 `ticket-ordering` 定義過，背景清理只是換一個呼叫端，不是新規則。）

## Impact

- `ProjectC.Domain.Orders.IOrderRepository`：新增 `GetAllAsync`、`GetExpiredPendingOrderIdsAsync`。
- `ProjectC.Infrastructure.Persistence.Repositories.OrderRepository`：對應實作。
- `ProjectC.Application.Orders.OrderService`：新增 `CancelExpiredOrderAsync`。
- 新增 `ProjectC.Application.Orders.GetOrders`/`GetOrderById`（或等效查詢 Handler）。
- 新增 `ProjectC.WebApi.Controllers.AdminOrdersController`。
- 新增 `ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService`（或等效命名），透過 `IHostedService` 註冊；清理週期讀取設定檔（`OrderCleanup:IntervalSeconds`）。
- 依 CLAUDE.md 規則，這次涉及資料庫讀寫、身份驗證/授權（Admin 角色）、背景程序對資料的主動修改，實作前需先過 CLAUDE.md「安全強制規則」清單；背景清理程序需特別注意：它是系統內部觸發，不經過使用者請求，因此不受一般的 Request-scoped `DbContext`/`IUnitOfWork` 生命週期保護，需要自行管理 Scope。
