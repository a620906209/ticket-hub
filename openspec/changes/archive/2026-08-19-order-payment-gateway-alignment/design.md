## Context

既有 `ticket-purchase` 確認訂單流程（`ConfirmOrderHandler.Handle`，由 `OrderService.ConfirmOrderAsync` → `ChangeOrderStatusAsync` 呼叫）目前是同步方法，驗證訂單狀態/座位持有後直接呼叫 `seat.ConfirmSold()` 與 `order.Confirm()`，沒有金流抽象、不會失敗。`OrderStatus` 列舉**目前**為 `Pending / Confirmed / Cancelled`（另有 `GetStatus(now)` 推導出的 `Expired`，不落地寫入）——`Confirmed` 是這次變更**之前**的名字，本文件從決策 5 開始一律改用變更後的名字 `Paid` 指稱同一個狀態，讀者不要把 Context 這段的 `Confirmed` 誤認為本文件其餘部分仍在用的名稱。

`OrderService.ChangeOrderStatusAsync` 是 `ConfirmOrderHandler` 與 `CancelOrderHandler` 共用的交易骨架（開交易 → 鎖座位 → 鎖後重讀訂單 → 呼叫對應 handler → commit），兩者目前共用同一個委派型別 `Func<Order, IReadOnlyDictionary<Guid, EventSeat>, Result>`（同步）。

## Goals / Non-Goals

**Goals:**
- 為確認訂單流程補上 `IPaymentGateway` 抽象，Domain 定義介面、Infrastructure 提供假實作，透過 DI 抽換
- 付款失敗時訂單維持 `Pending`、座位維持 `Held`，不落地任何狀態改變，買家可在保留時間內重試
- `OrderStatus.Confirmed` 更名為 `OrderStatus.Paid`，對齊 `docs/project-scope.md` 狀態命名規劃
- 維持既有「確認端點不接受任何付款資訊」的對外 API 契約（付款成功/失敗由伺服器端設定決定，不是請求參數）

**Non-Goals:**
- 不做真實第三方金流串接（維持 Mock，已在 `docs/project-scope.md` Won't 列表）
- 不新增 `Refunded` 狀態或退款流程（沒有退款觸發情境，超出本次範疇）
- 不變更座位鎖定機制（維持既有悲觀鎖，見 `docs/project-scope.md` 決策）
- 不變更確認訂單端點的路由或請求/回應格式——**已查證**該端點成功/失敗皆不回傳訂單狀態字串（`ConfirmOrder` 現況回應 204 No Content，見 `tests/ProjectC.WebApi.Tests/Orders/OrdersControllerTests.cs`），狀態字串改變只會透過既有 `order-administration` 查詢端點（`OrderSummaryDto`/`OrderDetailDto.Status`）間接呈現，且屬於單純原樣顯示，見 Risks 小節查證結果

## Decisions

### 決策 1：`IPaymentGateway` 介面放 Domain 層，簽章只吃 `orderId` + `amount`，不吃整個 `Order` 物件
```csharp
namespace ProjectC.Domain.Payments;

public interface IPaymentGateway
{
    Task<PaymentResult> ChargeAsync(Guid orderId, decimal amount, CancellationToken cancellationToken);
}

public enum PaymentResult { Succeeded, Declined }
```
遵循 CLAUDE.md「Repository / 外部服務介面一律定義在 Domain」規則。金額（`amount`）由呼叫端（`ConfirmOrderHandler`）從 `order.Items.Sum(i => i.UnitPrice)` 算出傳入，`IPaymentGateway` 本身不重新查價，職責單純只做「扣款」。

**考慮過的替代方案**：讓 `IPaymentGateway.ChargeAsync` 直接吃 `Order` 物件 —— 否決，會讓 Domain 服務介面依賴聚合根整個物件圖，增加不必要耦合；只傳必要的 `orderId` + `amount` 更符合介面隔離。

### 決策 2：`MockPaymentGateway` 建構子直接注入**未包裝**的 `MockPaymentGatewayOptions`（不吃 `IOptions<T>`），註冊為 Singleton
```csharp
public sealed class MockPaymentGatewayOptions
{
    public bool AlwaysSucceed { get; set; } = true;
}
```
比照既有 `AuthOptions`/`OrderCleanupOptions` 的慣例（見 `RefreshTokenHandler`/`LoginHandler`/`ExpiredOrderCleanupService` 建構子皆直接吃未包裝的 `AuthOptions`/`OrderCleanupOptions`，而非 `IOptions<T>`）：`Program.cs` 用 `services.Configure<MockPaymentGatewayOptions>(...)` 綁定設定，再用 `services.AddSingleton(sp => sp.GetRequiredService<IOptions<MockPaymentGatewayOptions>>().Value)` 把未包裝的 POCO 也註冊成可直接注入的服務；`MockPaymentGateway` 的建構子參數型別是 `MockPaymentGatewayOptions`，不是 `IOptions<MockPaymentGatewayOptions>`。無狀態、僅讀設定值，符合 CLAUDE.md DI lifetime 判準（Singleton：全域共用、無狀態、thread-safe）。預設 `AlwaysSucceed = true`，維持現有「呼叫即成功」的行為不變；要展示失敗路徑時透過設定檔切換或在測試中注入自訂假實作，**不透過 API 請求參數**控制，維持既有端點契約。

**考慮過的替代方案**：
1. 依訂單金額或買家 ID 觸發固定失敗（例如金額尾數規則）——否決，會把測試邏輯藏進業務規則裡，讓行為難以預期；設定檔開關更明確、更容易在整合測試中控制。
2. `MockPaymentGateway` 建構子直接吃 `IOptions<MockPaymentGatewayOptions>`（.NET Options 模式的預設寫法）——否決，這個 codebase 目前所有 Options 消費端（`AuthOptions`/`OrderCleanupOptions` 的消費者）一律吃未包裝的 POCO，直接吃 `IOptions<T>` 會破壞既有慣例（Rule 11 Match Codebase Conventions），也會讓建構子簽章跟其他 Handler 不一致。

### 決策 3：`ConfirmOrderHandler.Handle` 改為 async，`OrderService.ChangeOrderStatusAsync` 的共用委派型別改為 `Func<Order, IReadOnlyDictionary<Guid, EventSeat>, CancellationToken, Task<Result>>`
`CancelOrderHandler.Handle` 維持同步不變；呼叫端在傳入 `ChangeOrderStatusAsync` 時用 `(order, seats, ct) => Task.FromResult(_cancelOrderHandler.Handle(order, seats))` 包裝（忽略 `ct`），維持兩者共用同一套交易骨架，不重複寫一份 `ChangeOrderStatusAsync`。

**實作階段修正**：委派型別比原訂多帶一個 `CancellationToken` 參數——`ConfirmOrderHandler.Handle` 內部呼叫 `IPaymentGateway.ChargeAsync` 需要一個 `CancellationToken` 才能傳進去（見決策 1 的介面簽章），而 `ChangeOrderStatusAsync` 本來就有 `cancellationToken` 參數在作用域內，讓委派帶上這個參數直接傳遞最簡單、也最符合決策 7「呼叫端可透過 `CancellationToken` 取消」的設計精神。

**考慮過的替代方案**：只讓 Confirm 走獨立、不共用的流程——否決，會複製一份「開交易 → 鎖座位 → 鎖後重讀 → commit」的樣板邏輯，違反 CLAUDE.md 單一職責與 Rule 2 Simplicity（禁止重複邏輯）。

### 決策 4：付款呼叫時機——在驗證座位持有之後、變更任何狀態之前
`ConfirmOrderHandler.Handle` 流程調整為：驗證訂單 Pending/未逾時 → 驗證座位仍由本訂單持有 → **呼叫 `IPaymentGateway.ChargeAsync`** → 成功才執行 `seat.ConfirmSold()` 與 `order.Confirm()`（更新為 `OrderStatus.Paid`）。付款失敗時直接回傳 `Result.Failure(Error.Conflict(...))`，因為尚未變更任何聚合狀態，交易本來就不會 commit（`OrderService.ChangeOrderStatusAsync` 只在 `result.IsSuccess` 時才呼叫 `transaction.CommitAsync`），不需要額外的補償/回滾邏輯。

### 決策 5：`OrderStatus.Confirmed` → `OrderStatus.Paid`，`Order.Confirm()` 方法名稱維持不變
列舉值改名對齊規劃；方法名稱不跟著改，因為「確認訂單」這個動作本身沒變（呼叫端仍是呼叫「確認」端點/方法），改變的只是確認成功後訂單進入的狀態叫什麼。同理 `ConfirmOrderHandler`、`ConfirmOrderAsync`、API 路由都不改名，縮小本次變更的影響範圍（Rule 3 Surgical Changes）。

**考慮過的替代方案**：連同 `Confirm` 相關命名（handler、方法、路由）一併改成 `Pay` 系列——否決，範圍過大且非本次目的（本次是補金流抽象與狀態命名對齊，不是重新設計整個確認流程的措辭），若之後有需要可另開提案處理。

### 決策 6：付款失敗情境只在單元測試層驗證，整合測試層不新增此情境
`ConfirmOrderHandlerTests`（單元測試）新增 `FakePaymentGateway`（放 `tests/ProjectC.Application.Tests/TestSupport/`，比照既有 `FakeDateTimeProvider` 等手寫假物件的既有慣例），建構子直接傳入 `ConfirmOrderHandler`，不透過 DI 容器。`OrdersControllerTests`（整合測試）**不**新增付款失敗情境測試，只需確認既有「確認訂單成功」測試在改名後仍通過（這已足以證明 `IPaymentGateway`/`MockPaymentGateway` 的 DI 註冊正確——註冊錯誤會讓成功路徑直接啟動失敗或 500）。

**原因**：`OrdersControllerTests` 用 `IClassFixture<CustomWebApplicationFactory>`，同一個 factory 實例（含其註冊的 `IPaymentGateway` Singleton）由整個測試類別的所有測試方法共用。若在單一測試方法內把 `MockPaymentGatewayOptions.AlwaysSucceed` 切為 `false`，會污染同個 fixture 下的其他測試（測試互相干擾、產生執行順序相依）。要避免污染，正規做法是用 `factory.WithWebHostBuilder(...)` 為該測試建立獨立的 DI 覆寫，但**目前整個 repo 沒有任何測試對共用的 `IClassFixture` factory 做過 per-test DI 覆寫**（唯一用到 `WithWebHostBuilder` 的 `JwtOptionsFailFastTests` 是另外建立一個全新的 `WebApplicationFactory<Program>()`，不是覆寫共用 fixture），引入這個新模式的複雜度不划算——付款成功/失敗的分支邏輯本身在 `ConfirmOrderHandler` 已有完整單元測試覆蓋，整合測試只需要證明「這條路由真的接得到、DI 真的兜得起來」。

**考慮過的替代方案**：在整合測試也覆蓋付款失敗情境（透過 `WithWebHostBuilder` 覆寫或直接切共用 Options）——否決，會污染共用 fixture 或引入本 repo 目前沒有先例的測試模式，效益（多一層整合測試覆蓋）不足以抵銷風險與複雜度（Rule 2 Simplicity、Rule 11 Match Codebase Conventions）。

### 決策 7：`ChargeAsync` 的契約邊界——`orderId` 僅為訂單識別值、非完整冪等鍵；例外不吞、不處理 timeout、沒有「未知」結果
- **`orderId` 只是識別值，不是本次就已解決的冪等鍵**：`orderId` 在概念上對應付款系統慣用的冪等鍵，但**本次 `MockPaymentGateway` 沒有拿它做任何真正的去重**——沒有外部狀態需要保護，`Declined` 只是單純回傳值、不會被快取，`orderId` 目前的實際作用只是傳給 `ChargeAsync` 的訂單識別值。就算要拿單一 `orderId` 當冪等鍵，保護範圍也**僅止於同一次付款嘗試的重複請求**（例如網路逾時後 client 端自動重送同一次呼叫）——這種情況下重送應該拿到跟第一次相同的結果，不能重複扣款。但如果第一次結果是 `Declined`，買家在保留時間內修正付款方式（現實中，例如換一張卡）後再次呼叫確認端點，這是**新的付款嘗試**（業務層級重試，不是同一次請求的網路重送），必須被允許重新嘗試扣款，不能被冪等機制誤判成重複請求、直接回傳快取的 `Declined` 結果卡死。**單一 `orderId` 沒有能力區分這兩種情況**，所以嚴格來說它不足以稱為一個完整的冪等鍵設計，只是刻意接受的簡化，僅適用於本次沒有真正外部副作用的 Mock。**若未來串接真實金流，必須改用複合鍵**（例如 `orderId` + 嘗試次數/nonce，或每次業務層級重試都產生新的 idempotency key），否則會出現「買家換了付款方式還是被永遠卡在舊的 Declined 結果」的錯誤行為。`IPaymentGateway.ChargeAsync` 的 XML doc MUST 明確寫出這個限制，避免未來實作者誤以為現有的單一 `orderId` 設計是可以直接照搬到真實金流的完整冪等鍵方案。
- **例外不吞**：`ChargeAsync` 若拋出例外（本次 `MockPaymentGateway` 不會拋，但介面本身不禁止實作拋例外），`ConfirmOrderHandler` MUST NOT 包 try/catch 把它轉成 `Result.Failure`，讓例外往外傳播交給既有全域 `IExceptionHandler` middleware 處理，比照 CLAUDE.md「禁止吞掉例外」規則。
- **不處理 timeout**：`MockPaymentGateway` 沒有真正的網路 I/O，不會逾時；既有的 `CancellationToken` 已經是 async 呼叫鏈的一部分（跟 DB 操作共用同一個 request-scoped token），足以應付「呼叫端取消請求」的情境，本次不新增額外的 timeout 機制。真實金流串接時才需要重新評估逾時策略（例如 `HttpClient` timeout、retry policy），列為未來範疇。
- **沒有「未知/處理中」結果**：`PaymentResult` 只有 `Succeeded`/`Declined` 兩種值，假設付款是同步、即時可知結果的，不支援真實金流常見的非同步 webhook 確認流程。若未來要支援非同步確認，`PaymentResult` 需要擴充第三種狀態並搭配訂單暫留機制，屬於另一個提案的範疇。

**考慮過的替代方案**：在 `ChargeAsync` 加上 timeout 參數或內建重試邏輯——否決，`MockPaymentGateway` 沒有真正 I/O，加這些機制沒有實際效果，只會讓介面看起來比現況複雜（Rule 2 Simplicity）；真實串接時再依實際金流商的 SLA 決定 timeout/retry 策略，不預先猜測。

## Risks / Trade-offs

- **[Risk][已知限制，本次不修復] `IPaymentGateway.ChargeAsync` 是在 `OrderService.ChangeOrderStatusAsync` 開啟的 DB transaction 內被呼叫**（鎖座位 → 鎖後重讀 → 呼叫 `ConfirmOrderHandler.Handle`（內含付款）→ commit）。對 `MockPaymentGateway` 這種沒有真正網路 I/O、瞬間回傳的假實作而言，這個設計沒有實際影響；但這是刻意接受的技術債——**若未來換成真實金流串接**（真正的 HTTP 呼叫，可能耗時數百毫秒到數秒），會導致 DB 交易（含座位的悲觀鎖）在整個外部呼叫期間都被鎖住，嚴重拖慢同場活動其他座位的鎖定/確認吞吐量，也會把「DB commit 是否成功」跟「外部金流是否成功」兩個獨立失敗模式耦合在同一個交易邊界內（例如：付款成功但 DB commit 失敗，錢已經扣了但訂單沒轉態，屬於雙寫不一致風險）。→ **Mitigation（本次）**：不修復——本次只有 Mock，沒有真正外部呼叫，風險是理論上的，且移動交易邊界屬於既有架構的重構，超出「補金流抽象、對齊命名」的本次目的（Rule 3 Surgical Changes）。

→ **Mitigation（未來，且本身只是最低要求、不是完整解法）**：真實金流串接的提案至少要處理以下三件事，缺一就沒有真正解決一致性問題：
  1. **交易外呼叫金流**：付款呼叫移到 DB 交易之外執行，避免長時間佔用悲觀鎖。
  2. **回來後重新驗證**：金流呼叫完成、要開交易寫入狀態之前，MUST 重新驗證訂單與座位狀態（訂單是否仍是 Pending、是否已逾時、座位是否仍由本訂單持有）——外部呼叫期間狀態很可能已經變化（例如逾時後被其他訂單搶走），不能沿用呼叫金流「之前」驗證過的舊結果就直接寫入。
  3. **補償機制**：若金流回報成功、但重新驗證失敗（訂單/座位狀態已經不允許轉為 Paid），代表錢已經從買家端扣了但訂單無法對應標記為已付款，MUST 有明確的補償路徑（例如自動呼叫退款 API，或寫入需要背景程序/人工處理的補償佇列），不能讓這筆「已扣款但訂單沒對應」的錢不明不白消失不追蹤。

  **這三點只是必要條件，不是完整設計**——實際要不要用 outbox/saga、重試幾次、補償失敗又該怎麼辦，都留給屆時真正動手的提案決定。**本次的「`orderId` 冪等鍵」與這裡的「移出交易外」方向都只是問題的初步暫定思路，尚未解決真實金流的一致性問題；未來若真的要串接真實金流，必須把這裡的三點當成最低門檻重新設計，不能假設現有的介面或本節文字已經涵蓋完整方案。**
- **[Risk] `OrderStatus.Confirmed` 更名為 `Paid` 是 breaking change** → **Mitigation**：已用 `rg -i "confirmed"`（不分大小寫，不只找 `OrderStatus.Confirmed`）grep 全 repo 確認確切影響範圍。**修正**：不分大小寫的結果集合其實跟只搜尋 `Confirmed`（區分大小寫）**不完全一致**——多出 `web/src/pages/buyer/OrderResultPage.vue` 一個檔案，裡面是小寫的 `status.value = 'confirmed'`。這個檔案已經在下面「已查證，非風險」那條裡單獨處理過並確認不需要改動（純前端本地 UI 狀態，不解析後端回應）；除了這一個之外，其餘不分大小寫的結果跟區分大小寫的結果確實相同，沒有再漏掉其他小寫 `confirmed`。只有 5 個測試檔含 `OrderStatus.Confirmed` 參照：`tests/ProjectC.Domain.Tests/Orders/OrderTests.cs`、`tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs`、`ConfirmOrderHandlerTests.cs`、`CancelOrderHandlerTests.cs`、`tests/ProjectC.Infrastructure.Tests/GetExpiredPendingOrderIdsAsyncTests.cs`；其中部分測試方法名稱本身也含 `Confirmed`（如 `Confirm_WhenPending_TransitionsToConfirmed`、`Cancel_WhenConfirmed_ThrowsOrderNotPendingException`、`Handle_WhenConfirmed_FailsAndDoesNotReleaseSeats`），依 CLAUDE.md 測試命名慣例（`MethodName_Scenario_ExpectedResult`）需一併改名，不只改斷言值
- **[Risk] `ConfirmOrderHandler.Handle` 改 async 可能被其他呼叫端（若有）以同步方式呼叫而編譯失敗** → **Mitigation**：`grep` 確認 `.Handle(` 方法呼叫在**生產程式碼**中唯一呼叫點只有 `OrderService.ConfirmOrderAsync`，改動範圍可控
- **[Risk] `ConfirmOrderHandler` 建構子新增 `IPaymentGateway` 參數，會讓所有直接 `new ConfirmOrderHandler(...)` 的既有測試編譯失敗，不只 `ConfirmOrderHandlerTests.cs`** → **Mitigation**：已 grep 全 repo 確認 `new ConfirmOrderHandler(` 的完整呼叫點清單，共 4 個測試檔、10 處：
  - `tests/ProjectC.Application.Tests/Orders/ConfirmOrderHandlerTests.cs`（6 處，本檔案本來就要改）
  - `tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:37`（共用的 `Fixture.CreateOrderService()` helper 內）
  - `tests/ProjectC.Application.Tests/Orders/CancelOrderHandlerTests.cs:64,103`（建立一筆已 Paid 訂單當測試前置資料，用來測「取消已付款訂單會被拒絕」）
  - `tests/ProjectC.Infrastructure.Tests/OrderServiceConcurrencyTests.cs:39`（真實 Postgres 整合測試，跟同檔案其餘依賴一致，用**真實**的 `MockPaymentGateway`，不是 Fake——該檔案目前就是用真實的 `SystemDateTimeProvider` 而非任何 Fake，`ProjectC.Infrastructure.Tests` 也已經 `ProjectReference` 到 `ProjectC.Infrastructure`，直接可用）
  前三者用新增的 `FakePaymentGateway(Succeeded)`（見決策 6），最後一個用真實 `MockPaymentGateway`（搭配預設 `AlwaysSucceed = true` 的 Options）
- **[已查證，非風險]** 原本擔心前端可能寫死 `"Confirmed"` 字串比對，已實際搜尋 `web/src` 全部程式碼確認**不需要任何前端程式碼異動**：`ConfirmOrder` 端點本身回應 204 No Content 沒有 body（見上方 Non-Goals 查證），`buyer-web-ui` 的 `OrderResultPage.vue` 裡 `status.value = 'confirmed'` 是呼叫成功後手動設定的純前端本地 UI 狀態，跟後端回應內容無關；真正會顯示 `OrderStatus` 字串的是 `admin-web-ui` 的 `AdminOrderListPage.vue`/`AdminOrderDetailPage.vue`（透過 `order-administration` 查詢端點），但兩者都只是原樣顯示 `{{ order.status }}`，沒有任何字串比對或映射邏輯，字串從 `Confirmed` 換成 `Paid` 會自動正確顯示

## Migration Plan

1. 新增 `IPaymentGateway` / `MockPaymentGateway` / `MockPaymentGatewayOptions`，DI 註冊
2. `OrderStatus` 列舉改名，編譯器會標出所有需要跟著改的參照點（`Confirmed` → `Paid`），逐一修正
3. `ConfirmOrderHandler.Handle` 改 async 並注入 `IPaymentGateway`；`OrderService` 調整委派型別與呼叫方式
4. 新增 `FakePaymentGateway`；修補所有因建構子簽章改變而編譯失敗的既有測試呼叫點（見 Risks 小節清單）；`OrderStatus.Confirmed` 相關的既有測試斷言與方法名稱一併改名為 `Paid`
5. 只在 `ConfirmOrderHandlerTests`（單元測試）新增付款失敗情境；`OrdersControllerTests`（整合測試）不新增，只確認既有成功路徑測試改名後仍通過（見決策 6）
6. 執行完整測試套件確認無遺漏

前端 `buyer-web-ui`/`admin-web-ui` **不需要**列入遷移步驟——已於 Risks 小節查證兩者皆無需程式碼異動。

已確認 `OrderConfiguration`（`src/ProjectC.Infrastructure/Persistence/Configurations/OrderConfiguration.cs:35`）對 `Status` 沒有 `HasConversion<string>()`，是以列舉底層整數值儲存。「不需要 migration」這個結論成立的**前提**是以下兩點同時成立，缺一都需要補遷移：
1. `OrderStatus` 持續以底層整數值儲存（沒有之後被改成 `HasConversion<string>()`）
2. 列舉成員的**宣告順序與底層數值**在改名前後不變——本次只能把 `Confirmed` 這個識別字換成 `Paid`（`Pending=0 → Paid=1 → Cancelled=2`，數值不動），**不能調整宣告順序、不能在既有成員中間插入新成員**。若未來要新增 `Refunded` 等狀態，新成員必須加在列舉**最後面**，否則會讓既有成員的底層數值往後推移，既有資料列會被誤解讀成錯誤的狀態

兩點目前皆成立，因此**沒有資料庫 schema 變更、不需要寫遷移腳本**，既有資料列不受影響；Task 1.2 實作時需再次核對改名前後的宣告順序逐一比對一致，不能只改名字沒檢查順序。

## Open Questions

本次範疇（Mock 金流）內沒有懸而未決的問題。以下兩點刻意留白，**不是本次要解決的問題，而是明確標記給未來串接真實金流的提案**，避免有人誤以為決策 7 / Risks 小節已經講完整套方案：

- **真實金流的一致性設計還沒有具體方案**：Risks 小節列的「交易外呼叫、回來後重新驗證、補償機制」三點只是最低門檻，實際要用 outbox/saga 還是別的模式、補償失敗要怎麼處理、要不要有人工介入的路徑，都還沒有答案，留給屆時的提案決定。
- **真實金流的冪等鍵設計還沒有具體方案**：決策 7 只指出「單一 `orderId` 不夠用」，但複合鍵要怎麼組（`orderId` + 嘗試次數？還是每次重試呼叫端自產新 key？金流商自己的 idempotency key 機制要不要直接借用？）都還沒決定，留給屆時的提案決定。
