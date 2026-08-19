## 1. Domain 層

- [x] 1.1 新增 `IPaymentGateway` 介面 + `PaymentResult` enum（`src/ProjectC.Domain/Payments/`），簽章為 `Task<PaymentResult> ChargeAsync(Guid orderId, decimal amount, CancellationToken cancellationToken)`（見 design.md 決策 1）；`ChargeAsync`/`PaymentResult` 的 XML doc MUST 完整寫明決策 7 的契約邊界，不能只寫冪等鍵：
  - `orderId` 本次只是訂單識別值，不是已經解決的冪等鍵設計——Mock 沒有拿它做任何真正去重；即使要當冪等鍵用，保護範圍也僅止於「同一次請求的重複重送」，不涵蓋「`Declined` 後買家業務層級重試」的情況，單一 `orderId` 無法區分這兩者，未來串接真實金流必須改用複合鍵
  - `PaymentResult` 只有 `Succeeded`/`Declined` 兩種值，沒有「未知/處理中」狀態，介面假設付款同步、即時可知結果，不支援非同步 webhook 確認流程
  - 介面沒有內建 timeout 概念，呼叫端透過既有的 `CancellationToken` 取消，不代表真實實作不需要自己的逾時策略
- [x] 1.2 `OrderStatus.cs` 將 `Confirmed` 改名為 `Paid`（純識別字改名，不調整宣告順序、不插入新成員，底層數值維持 `Pending=0, Paid=1, Cancelled=2`）；改名後逐一核對宣告順序與改名前一致（見 design.md Migration Plan 段落的前提條件）
- [x] 1.3 `Order.cs` 的 `Confirm()` 方法內 `Status = OrderStatus.Paid`（方法名稱維持 `Confirm` 不變，見 design.md 決策 5）

## 2. Infrastructure 層

- [x] 2.1 新增 `MockPaymentGatewayOptions`（`src/ProjectC.Infrastructure/Payments/`），含 `bool AlwaysSucceed { get; set; } = true`
- [x] 2.2 新增 `MockPaymentGateway : IPaymentGateway`，建構子直接注入**未包裝**的 `MockPaymentGatewayOptions`（不是 `IOptions<MockPaymentGatewayOptions>`，比照 `AuthOptions`/`OrderCleanupOptions` 消費端慣例），依 `AlwaysSucceed` 回傳 `PaymentResult.Succeeded`/`Declined`（見 design.md 決策 2）
- [x] 2.3 更新 `tests/ProjectC.Infrastructure.Tests/OrderServiceConcurrencyTests.cs:39` 的 `new ConfirmOrderHandler(dateTimeProvider)` 呼叫，補上真實 `MockPaymentGateway`（搭配預設 `AlwaysSucceed = true` 的 Options）——比照該檔案一貫直接使用真實 Infrastructure 實作（如 `SystemDateTimeProvider`）而非 Fake 的既有慣例，否則編譯失敗（見 design.md Risks 小節）

## 3. Application 層

- [x] 3.1 `ConfirmOrderHandler` 建構子注入 `IPaymentGateway`；`Handle` 改為 `async Task<Result>`，在既有驗證（Pending/未逾時/座位歸屬）通過後、變更任何座位或訂單狀態前呼叫 `IPaymentGateway.ChargeAsync(order.Id, order.Items.Sum(i => i.UnitPrice), cancellationToken)`；`Declined` 時回傳 `Result.Failure(Error.Conflict(...))`，不呼叫 `seat.ConfirmSold()`/`order.Confirm()`（見 design.md 決策 4）；呼叫 `ChargeAsync` **不得**包 try/catch，例外 MUST 直接往外傳播交給全域 `IExceptionHandler` 處理，不吞例外（見 design.md 決策 7「例外不吞」）；在呼叫點附近留一句註解說明付款呼叫目前位於 DB transaction 內是刻意接受的技術債，見 design.md Risks 小節第一項，比照既有 codebase 慣例（如 `OrderService.cs` 對「鎖後重讀」的註解）
- [x] 3.2 `OrderService.ChangeOrderStatusAsync` 的委派型別改為 `Func<Order, IReadOnlyDictionary<Guid, EventSeat>, CancellationToken, Task<Result>>`（比原訂多帶 `CancellationToken`，供 `ConfirmOrderHandler` 傳給 `IPaymentGateway.ChargeAsync`，見 design.md 決策 3 實作階段修正）；`CancelOrderAsync`/`CancelExpiredOrderAsync` 呼叫點包一層 `(order, seats, ct) => Task.FromResult(_cancelOrderHandler.Handle(order, seats))`（忽略 `ct`），`ConfirmOrderAsync` 直接傳已改為 async 的 `_confirmOrderHandler.Handle`
- [x] 3.3 新增 `FakePaymentGateway`（`tests/ProjectC.Application.Tests/TestSupport/`，比照既有 `FakeDateTimeProvider` 等手寫假物件慣例），建構子可指定固定回傳 `PaymentResult.Succeeded`/`Declined`；額外記錄呼叫資訊供測試斷言：`CallCount`（被呼叫次數）、`LastOrderId`、`LastAmount`（最後一次呼叫收到的參數）
- [x] 3.4 單元測試 `ConfirmOrderHandlerTests`：既有 6 處 `new ConfirmOrderHandler(...)` 呼叫皆補上 `FakePaymentGateway(Succeeded)`；既有「確認成功」測試的狀態斷言改為 `OrderStatus.Paid`，測試方法名稱含 `Confirmed` 字樣者一併改名
- [x] 3.5 新增測試：付款成功時，斷言 `FakePaymentGateway.LastAmount` 等於 `order.Items.Sum(i => i.UnitPrice)`（至少一筆多座位訂單驗證加總正確）、且 `FakePaymentGateway.LastOrderId` 等於 `order.Id`（`LastOrderId` 是 3.3 新增的欄位，目前沒有任何測試斷言它，這裡補上）
- [x] 3.6 新增測試：付款失敗情境（注入 `FakePaymentGateway(Declined)`）——斷言訂單維持 Pending、座位維持 Held、不呼叫 `seat.ConfirmSold`，對應 spec `ticket-purchase`/`ticket-ordering`
- [x] 3.7 新增測試：既有的驗證失敗情境（`Handle_WhenOrderExpired_...`、`Handle_WhenOrderNotPending_...`、`Handle_WhenSeatNoLongerHeldByThisOrder_...`、`Handle_WhenSeatCannotBeResolved_...`、`Handle_WhenResolvedSeatBelongsToDifferentEvent_...`）都額外斷言 `FakePaymentGateway.CallCount` 為 0，驗證這些情境在驗證失敗時完全不會呼叫到 gateway（見 design.md 決策 4 的呼叫順序）
- [x] 3.8 更新以下既有測試檔內直接 `new ConfirmOrderHandler(...)` 的呼叫點，補上 `FakePaymentGateway(Succeeded)` 參數，否則編譯失敗（見 design.md Risks 小節完整清單）：
  - `OrderServiceTests.cs` 的 `Fixture.CreateOrderService()` helper（第 37 行）
  - `CancelOrderHandlerTests.cs` 建立已付款訂單前置資料的兩處（第 64、103 行）
- [x] 3.9 `OrderServiceTests`：讓 `Fixture` 暴露它注入 `ConfirmOrderHandler` 用的 `FakePaymentGateway` 實例；新增（或擴充既有的）非本人確認訂單（403）、確認不存在訂單（404）測試案例，斷言這兩種情境下 `FakePaymentGateway.CallCount` 為 0——因為 `ChangeOrderStatusAsync` 的買家身份/存在性檢查發生在開交易、呼叫 `ConfirmOrderHandler.Handle` 之前，不應該碰到 gateway

## 4. WebApi 層

- [x] 4.1 `Program.cs` 依既有 `AuthOptions`/`OrderCleanupOptions` 兩行慣例註冊：`services.Configure<MockPaymentGatewayOptions>(builder.Configuration.GetSection("MockPaymentGateway"))` 綁定設定，再 `services.AddSingleton(sp => sp.GetRequiredService<IOptions<MockPaymentGatewayOptions>>().Value)` 展開成未包裝的 POCO 服務；另外 `builder.Services.AddSingleton<IPaymentGateway, MockPaymentGateway>()`
- [x] 4.2 整合測試 `OrdersControllerTests`：**不**新增付款失敗情境（見 design.md 決策 6——`IClassFixture` 共用 fixture 若切換共用的 `MockPaymentGatewayOptions` 會污染同類別其他測試，付款失敗分支已由 3.6/3.7 的單元測試覆蓋）；只需確認既有「確認訂單成功」測試（`ConfirmOrder_ByBuyerOnOwnPendingOrder_Returns204AndSellsSeat`）在改名後仍通過，即已驗證 `IPaymentGateway`/`MockPaymentGateway` DI 註冊正確；此檔案已查證現有測試不含 `"Confirmed"` 字串斷言，不需要額外改名

## 5. 既有命名清查（跨既有 spec 的 Confirmed → Paid 對齊）

- [x] 5.1 改名 `OrderStatus.Confirmed` 在以下 5 個既有測試檔的參照，含測試方法名稱本身帶 `Confirmed` 字樣者一併改名（已於 design.md Risks 小節 grep 確認範圍僅此 5 檔，無遺漏）：
  - `tests/ProjectC.Domain.Tests/Orders/OrderTests.cs`
  - `tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs`
  - `tests/ProjectC.Application.Tests/Orders/ConfirmOrderHandlerTests.cs`
  - `tests/ProjectC.Application.Tests/Orders/CancelOrderHandlerTests.cs`
  - `tests/ProjectC.Infrastructure.Tests/GetExpiredPendingOrderIdsAsyncTests.cs`

前端 `buyer-web-ui`/`admin-web-ui` 已於 design.md 查證不需要任何程式碼異動，本次不列前端檢查任務。

## 6. Spec 同步

- [x] 6.1 確認本次改動與 `openspec/changes/order-payment-gateway-alignment/specs/` 下的 delta spec（`ticket-purchase`、`ticket-ordering`、`order-administration` 修改）一致，無偏差
- [x] 6.2 完成後執行歸檔（`openspec archive` 或 `opsx:archive`），同步 `openspec/specs/ticket-purchase/spec.md`、`openspec/specs/ticket-ordering/spec.md`、`openspec/specs/order-administration/spec.md`；並回頭更新 `docs/project-scope.md` 第 8 節，將此決策項目標記為已完成
