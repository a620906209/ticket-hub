## Why

既有 `ticket-purchase` 的確認訂單端點「不接受任何付款資訊，呼叫成功即視為付款完成」，直接把訂單從 `Pending` 轉為 `Confirmed`，沒有金流抽象層，也沒有失敗路徑。這與 `docs/project-scope.md` 的規劃（`IPaymentGateway` 介面展示依賴反轉、訂單狀態命名對齊 `Paid` 語意）不一致。後續要開發的電子票券出票邏輯會掛在「付款成功」這個事件上，若不先補齊這層抽象、對齊命名，電子票券功能之後勢必要跟著重工。現在（尚未有其他功能依賴付款流程）是調整成本最低的時機。

## What Changes

- 新增 `IPaymentGateway` 介面（Domain 層），定義 `ChargeAsync(orderId, amount, cancellationToken)` 回傳付款結果（成功/失敗）
- 新增 `MockPaymentGateway`（Infrastructure 層），建構子直接注入未包裝的 `MockPaymentGatewayOptions`（比照既有 `AuthOptions`/`OrderCleanupOptions` 消費端慣例，不吃 `IOptions<T>`）控制預設成功/可切換失敗，由 DI 抽換，展示依賴反轉；不改變既有「確認端點不接受任何付款資訊」的對外契約——失敗與否由伺服器端設定決定，不是呼叫端輸入
- `ConfirmOrderHandler.Handle` 改為非同步，在座位/訂單狀態轉換前呼叫 `IPaymentGateway`；付款失敗時訂單維持 `Pending`、座位維持 `Held`，回傳衝突錯誤，買家可在保留時間內重試
- **BREAKING**：`OrderStatus.Confirmed` 更名為 `OrderStatus.Paid`，對齊 `docs/project-scope.md` 規劃的狀態命名；`Order.Confirm()` 方法名稱維持不變（代表「確認並完成付款」這個動作），僅列舉值命名調整
- `Refunded` 狀態本次**不**加入，超出範疇（目前沒有退款觸發情境，待該功能規劃時再議）
- **本次僅止於 Mock 層級的介面/命名對齊，不是真實金流的一致性方案**：`orderId` 冪等鍵設計、付款呼叫位於 DB transaction 內等，都只是暫定思路，尚未解決真實金流會遇到的一致性問題（例如重新驗證、補償機制），詳細條件與未解決項目見 design.md 決策 7、Risks 小節、Open Questions

## Capabilities

### New Capabilities
(無新增使用者可見能力，本次為既有能力的內部強化)

### Modified Capabilities
- `ticket-purchase`：「透過 API 確認訂單（模擬付款）」需求改為透過 `IPaymentGateway` 處理付款，新增付款失敗情境（訂單維持 Pending、座位維持 Held、回傳衝突錯誤）；所有情境敘述中的 `Confirmed` 狀態名稱改為 `Paid`
- `ticket-ordering`：「確認訂單須驗證訂單與座位歸屬一致」「取消訂單，統一處理主動取消與逾時清理」「訂單逾時為查詢時推導，不落地寫入狀態」三條需求內文中的 `Confirmed` 狀態名稱改為 `Paid`（純命名對齊，取消規則本身不變——`Paid` 訂單仍是終態，不可重複取消）
- `order-administration`：「背景週期性清理逾時仍為 Pending 的訂單」需求內文中的 `Confirmed` 狀態名稱改為 `Paid`（純命名對齊，行為不變）

## Impact

- **程式碼**：
  - 新增 `src/ProjectC.Domain/Payments/IPaymentGateway.cs`（介面 + 回傳結果型別）
  - 新增 `src/ProjectC.Infrastructure/Payments/MockPaymentGateway.cs` + `MockPaymentGatewayOptions.cs`
  - 修改 `src/ProjectC.Domain/Orders/OrderStatus.cs`（`Confirmed` → `Paid`）
  - 修改 `src/ProjectC.Domain/Orders/Order.cs`（`Status = OrderStatus.Paid`）
  - 修改 `src/ProjectC.Application/Orders/ConfirmOrderHandler.cs`（注入 `IPaymentGateway`，改為 async）
  - 修改 `src/ProjectC.Application/Orders/OrderService.cs`（`ChangeOrderStatusAsync` 內部呼叫點需相容 async handler；`CancelOrderHandler` 維持同步）
  - `src/ProjectC.WebApi/Program.cs` 新增 `MockPaymentGateway` 的 DI 註冊
- **測試**：新增 `FakePaymentGateway` 測試假物件（`tests/ProjectC.Application.Tests/TestSupport/`）；`ConfirmOrderHandlerTests`（單元測試）新增付款失敗情境。`OrdersControllerTests`（整合測試）**不**新增付款失敗情境——`IClassFixture` 共用 fixture 若切換共用的 `MockPaymentGatewayOptions` 會污染同類別其他測試，見 design.md 決策 6，只需確認既有成功路徑測試改名後仍通過即可驗證 DI 註冊正確。`OrderTests`、`OrderServiceTests`、`ConfirmOrderHandlerTests`、`CancelOrderHandlerTests`、`GetExpiredPendingOrderIdsAsyncTests` 這 5 個既有測試檔內斷言 `OrderStatus.Confirmed` 的地方需改為 `OrderStatus.Paid`，含測試方法名稱本身帶有 `Confirmed` 字樣者一併改名（`OrdersControllerTests` 已查證不含 `Confirmed` 字串斷言，不需要改名）
  - **額外編譯期影響**：`ConfirmOrderHandler` 建構子新增 `IPaymentGateway` 參數後，`OrderServiceTests.cs`、`CancelOrderHandlerTests.cs`、`tests/ProjectC.Infrastructure.Tests/OrderServiceConcurrencyTests.cs` 內共 4 處直接 `new ConfirmOrderHandler(...)` 的呼叫點也需要補上參數才能編譯通過（前三者用 `FakePaymentGateway`，最後一個因為是真實 Postgres 整合測試改用真實 `MockPaymentGateway`），完整清單見 design.md Risks 小節
- **不受影響（無程式碼異動）**：
  - `seat-reservation`（悲觀鎖機制，見 `docs/project-scope.md` 決策維持現況）
  - `ticket-ordering` 內「建立訂單並原子性鎖定座位」「訂單暫扣快照票價」「訂單只有單一到期時間」「建立訂單須記錄買家身份」等座位鎖定/建立相關需求——注意 `ticket-ordering` 這個 capability 本身在 Modified Capabilities 中，是因為其中「確認/取消/逾時」3 條需求的**命名對齊**才列為 Modified，此處指的是同一 capability 內其餘不相關的需求不受影響，兩者並不衝突
  - `order-administration` 的背景清理程式碼本身（已 grep 確認 `src/` 內清理邏輯不比對 `Confirmed` 字面值），只有 spec 文字需要同步命名，無程式碼異動
  - 前端 `buyer-web-ui`、`admin-web-ui`：**已查證**皆不需要任何程式碼異動——`ConfirmOrder` 端點回應本來就是 204 No Content 無 body；`buyer-web-ui` 的 `OrderResultPage.vue` 顯示的「已確認」是純前端本地 UI 狀態，不解析後端回應；`admin-web-ui` 的訂單列表/明細頁只是原樣顯示 `OrderSummaryDto`/`OrderDetailDto.Status` 字串，沒有任何比對邏輯，字串換成 `Paid` 會自動正確顯示（詳見 design.md Risks 小節）
