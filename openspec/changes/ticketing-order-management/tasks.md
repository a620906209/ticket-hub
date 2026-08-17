## 1. Infrastructure：Repository 擴充

- [x] 1.1 `IOrderRepository` 新增 `GetAllAsync(CancellationToken cancellationToken) : Task<IReadOnlyList<Order>>`，`OrderRepository` 實作 MUST 一併 `Include(o => o.Items)`（見 design.md 決策 4）
- [x] 1.2 `IOrderRepository` 新增 `GetExpiredPendingOrderIdsAsync(DateTime now, CancellationToken cancellationToken) : Task<IReadOnlyList<Guid>>`，`OrderRepository` 實作為標準 EF Core LINQ 查詢（`Status == Pending && HeldUntilUtc <= now`，`Select(o => o.Id)`），不加鎖（見 design.md 決策 3）

## 2. Application：查詢 Handler、OrderService 擴充、清理設定

- [x] 2.1 `OrderService` 建構子新增 `IDateTimeProvider` 依賴（Singleton，見 design.md 決策 1）；私有方法 `ChangeOrderStatusAsync` 的 `requestingBuyerId` 參數改成 `Guid?`；`null` 時略過本人驗證，改為 MUST 驗證 `_dateTimeProvider.UtcNow >= order.HeldUntilUtc`（不成立回 `Error.Conflict`，不開交易）；`ConfirmOrderAsync`/`CancelOrderAsync` 呼叫端不變（傳非 null 值，不受新的逾時檢查影響）；同步更新 `tests/.../OrderServiceTests.cs`、`tests/.../OrderServiceConcurrencyTests.cs`（`ticketing-purchase` 留下）建構 `OrderService` 時補上新參數
- [x] 2.2 新增 `OrderService.CancelExpiredOrderAsync(Guid orderId, CancellationToken cancellationToken) : Task<Result>`：呼叫 `ChangeOrderStatusAsync(orderId, requestingBuyerId: null, _cancelOrderHandler.Handle, cancellationToken)`
- [x] 2.3 新增 `OrderSummaryDto(Guid Id, Guid EventId, Guid BuyerId, string Status, DateTime HeldUntilUtc)` 與 `GetOrdersHandler.HandleAsync(CancellationToken) : Task<IReadOnlyList<OrderSummaryDto>>`（**不包 `Result`，比照 `GetEventsHandler` 慣例**）：`IOrderRepository.GetAllAsync` → 依 `IDateTimeProvider` 的 `now` 呼叫 `order.GetStatus(now)` 轉字串 → 回傳列表（見 design.md 決策 4）
- [x] 2.4 新增 `OrderDetailDto(Guid Id, Guid EventId, Guid BuyerId, string Status, DateTime HeldUntilUtc, IReadOnlyList<OrderItemDto> Items)` / `OrderItemDto(Guid Id, Guid EventSeatId, decimal UnitPrice)` 與 `GetOrderByIdHandler.HandleAsync(Guid orderId, CancellationToken) : Task<Result<OrderDetailDto>>`：`IOrderRepository.GetByIdAsync` → `null` 回 `Error.NotFound` → 回傳明細，**`Status` 一樣用 `order.GetStatus(now)`（不是持久化欄位），跟 `GetOrdersHandler` 語意一致**（見 design.md 決策 4）
- [x] 2.5 新增 `OrderCleanupOptions`（`public int IntervalSeconds { get; set; } = 300;`），比照 `AuthOptions`（不加驗證標註，有安全預設值，不需要 `ValidateOnStart`；跟 `JwtOptions` 不同，見 design.md 決策 2）

## 3. WebApi：Controller、背景服務、DI 註冊

- [x] 3.1 新增 `AdminOrdersController`（`api/admin/orders`，`[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`，比照 `AdminEventsController`）：`GET /` 呼叫 `GetOrdersHandler`，直接 `return Ok(await _getOrdersHandler.HandleAsync(ct));`（不經過 `Result`）；`GET /{id:guid}` 呼叫 `GetOrderByIdHandler`，`var result = await _getOrderByIdHandler.HandleAsync(id, ct); return result.ToActionResult(Ok);`
- [x] 3.2 新增 `ExpiredOrderCleanupService : BackgroundService`（依 design.md 決策 2 的流程）：建構子注入 `IServiceScopeFactory`、`IDateTimeProvider`（Singleton，直接注入，不透過 Scope）、`OrderCleanupOptions`（非 `IOptions<OrderCleanupOptions>`，見 2.5）、`ILogger<ExpiredOrderCleanupService>`；`ExecuteAsync` 啟動後**先執行一輪清理、完成後才開始等待**（不是先等後清），迴圈邏輯：
  ```csharp
  while (!stoppingToken.IsCancellationRequested)
  {
      try
      {
          await CleanupOnceAsync(stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
          break;
      }
      catch (Exception exception)
      {
          _logger.LogError(exception, "Expired order cleanup cycle failed; will retry next interval.");
      }

      await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds)), stoppingToken);
  }
  ```
  （**`Task.Delay` MUST 傳入 `stoppingToken`**，否則關閉時無法即時停止；**MUST 用 `Math.Max(1, ...)` 夾住秒數下限**，否則 `IntervalSeconds` 被設成 0 或負數時 `Task.Delay` 會拋 `ArgumentOutOfRangeException` 讓整個應用程式當掉；**外層 `try/catch` 保護整次 `CleanupOnceAsync` 呼叫（掃描、開 Scope、取得服務等所有非單筆訂單的例外）**，記錄後留給下一輪自然重試，不讓資料庫暫時性問題連帶停掉整個應用程式，見 design.md 決策 2「整輪清理失敗的處理方式」）；**`CleanupOnceAsync` MUST 是 `public`**（供 `ProjectC.WebApi.Tests` 直接呼叫，這個專案沒有設定 `InternalsVisibleTo`，見 design.md 決策 2）；`CleanupOnceAsync(CancellationToken cancellationToken)` 內部邏輯：開 Scope 取 `IOrderRepository` 呼叫 `GetExpiredPendingOrderIdsAsync(_dateTimeProvider.UtcNow, cancellationToken)` → 用 `foreach`**依序**（不是 `Task.WhenAll` 平行）處理每筆訂單 Id，**每筆開始前先 `cancellationToken.ThrowIfCancellationRequested()`**，再各自開新 Scope 呼叫 `OrderService.CancelExpiredOrderAsync(orderId, cancellationToken)`；單筆處理的 `try/catch` **MUST 排除 `OperationCanceledException`**（先 `catch (OperationCanceledException) { throw; }` 或用 `when (exception is not OperationCanceledException)`），只吞真正的基礎設施例外，`Result.Failure` 是正常回傳值不算例外、foreach 本來就會繼續跑下一筆，兩種情況都要記 log
- [x] 3.3 於 `Program.cs`：註冊 `GetOrdersHandler`/`GetOrderByIdHandler` 為 `AddScoped`；比照 `AuthOptions` 現有寫法 `builder.Services.Configure<OrderCleanupOptions>(builder.Configuration.GetSection("OrderCleanup"))` + `builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<OrderCleanupOptions>>().Value)`；**`AddHostedService<ExpiredOrderCleanupService>()` MUST 排除 `Testing` 環境**（`if (!builder.Environment.IsEnvironment("Testing")) { builder.Services.AddHostedService<ExpiredOrderCleanupService>(); }`，比照既有 `if (app.Environment.IsDevelopment())` 的條件註冊慣例），否則 `ProjectC.WebApi.Tests` 底下所有用到 `CustomWebApplicationFactory` 的既有測試類別都會連帶啟動真實背景服務，見 design.md 決策 5
- [x] 3.4 `appsettings.json` 補上 `OrderCleanup:IntervalSeconds` 區段（見 design.md Migration Plan）

## 4. 測試

- [x] 4.1 `ProjectC.Application.Tests`：`OrderService.CancelExpiredOrderAsync`（用 Fake Repository，比照既有 `OrderServiceTests` 慣例）成功取消已逾時的 Pending 訂單並釋放座位，且不需要任何買家身份參數；**訂單尚未逾時時回 `Error.Conflict`、不變更訂單或座位狀態**（見 design.md 決策 1 的防禦性檢查，這是防止未來誤用這個方法繞過買家授權的回歸測試）；**補上 `now == HeldUntilUtc` 邊界案例，驗證視為已逾時、成功取消**（跟 `Order.GetStatus` 的 `now >= HeldUntilUtc` 判斷邊界一致，避免之後有人不小心把檢查條件從 `<` 改成 `<=`）
- [x] 4.2 `ProjectC.Application.Tests`：`GetOrdersHandler`（回傳所有訂單，`Status` 反映即時推導值，例如已逾時但持久化仍 Pending 的訂單回報 Expired）
- [x] 4.3 `ProjectC.Application.Tests`：`GetOrderByIdHandler`（成功回傳明細含 Items、訂單不存在回 NotFound）
- [x] 4.4 `ProjectC.Infrastructure.Tests`：`GetExpiredPendingOrderIdsAsync`（Testcontainers 整合測試）只回傳「Pending 且已逾時」的訂單 Id，不含尚未逾時的 Pending 訂單、不含 Confirmed/Cancelled 訂單，**含 `HeldUntilUtc` 恰好等於查詢時間 `now` 的邊界案例（視為已逾時，應被回傳）**，對應 spec「尚未逾時的 Pending 訂單不受影響」「已是終態的訂單不受影響」兩個 Scenario
- [x] 4.5 `ProjectC.WebApi.Tests`（Testcontainers 整合測試）：**直接 `new ExpiredOrderCleanupService(...)`（不透過 DI 容器解析，`AddHostedService<T>` 不會把 `T` 註冊成可解析的具體型別）**，四個建構子依賴用 `factory.Services.GetRequiredService<IServiceScopeFactory>()`/`GetRequiredService<IDateTimeProvider>()`/`new OrderCleanupOptions()`/`NullLogger<ExpiredOrderCleanupService>.Instance` 組出，呼叫 `public` 的 `CleanupOnceAsync(CancellationToken)`（不必真的等待 `Task.Delay`）：驗證一輪清理能正確取消多筆逾時訂單並釋放座位，對應 spec「逾時的 Pending 訂單被背景清理」Scenario
- [x] 4.6 `ProjectC.WebApi.Tests`：**不透過 Mock**（`OrderService` 是 `sealed class` 無介面，這個專案也沒有 Moq），改用真實情境重現「一筆失敗、其餘繼續」：種兩筆都逾時的 Pending 訂單 A、B，對 B 的 `EventSeat` 直接用 `DbContext` 把 `_soldByOrderId` 改成 B 自己的訂單 Id（比照 `CancelOrderHandlerTests.Handle_WhenSeatWasSoldByThisSameOrder_ReturnsFailureAsInconsistentState` 的既有手法，模擬座位已由本訂單售出但訂單仍是 Pending 的不一致狀態，讓 `CancelOrderHandler.Handle` 對 B 回 `Error.Conflict`）→ 呼叫 `CleanupOnceAsync`，驗證 A 被正確清理成 Cancelled、B 維持 Pending 不變，清理沒有因為 B 中途中斷，對應 spec「單筆訂單清理失敗不影響其餘訂單」Scenario；`try/catch` 保護「真正的基礎設施例外」（非 `OperationCanceledException`，那個由 4.7 測）的部分不在這次自動化測試範圍內，是刻意接受的測試缺口（見 design.md Risks）
- [x] 4.7 `ProjectC.WebApi.Tests`：驗證取消訊號不會被誤判成訂單清理失敗——種兩筆逾時的 Pending 訂單，建立一個先取消的 `CancellationTokenSource`，呼叫 `CleanupOnceAsync(cts.Token)`，驗證拋出 `OperationCanceledException`（用 `act.Should().ThrowAsync<OperationCanceledException>()`），且兩筆訂單皆維持 Pending、未被處理，對應 spec「應用程式關閉時清理程序正常停止，不當成單筆失敗處理」Scenario。**注意這個測試實際觸發的是掃描步驟（`GetExpiredPendingOrderIdsAsync`）在呼叫前就已取消而拋出，不會走到 foreach 內單筆訂單的 `try/catch`**（那段程式碼在呼叫前已取消的情況下根本不會執行到）；「取消訊號剛好在處理某一筆訂單『途中』抵達，單筆 `try/catch` 正確排除 `OperationCanceledException` 不吞掉它」這個更精確的時間點，沒有獨立測試覆蓋——要做到這點需要能在 foreach 迴圈跑到一半時插入取消動作的測試 hook，這次選擇不為了測這個而加這種複雜度（見 design.md Risks 的對應說明）。
- [x] 4.8 `ProjectC.WebApi.Tests`：驗證 `Testing` 環境下沒有註冊真實的 `ExpiredOrderCleanupService` 背景服務——`factory.Services.GetServices<IHostedService>()` 不含任何 `ExpiredOrderCleanupService` 實例，對應 design.md 決策 5（這是回歸測試，避免之後有人不小心把 `if (!builder.Environment.IsEnvironment("Testing"))` 這層條件拿掉，讓所有既有 WebApi 整合測試意外連帶啟動真實背景服務）
- [x] 4.9 `ProjectC.WebApi.Tests`：`AdminOrdersControllerTests` 涵蓋 spec「查看訂單需要 Admin 角色」（Admin 成功、非 Admin 403、未登入 401）、「查詢所有訂單列表」、「查詢單筆訂單明細」（含查詢不存在的訂單 404）全部 Scenario

## 5. 收尾檢查

- [x] 5.1 確認 `ProjectC.Domain.csproj` 未新增任何 `<ProjectReference>`
- [x] 5.2 確認 `ConfirmOrderAsync`/`CancelOrderAsync` 呼叫 `ChangeOrderStatusAsync` 時仍正確傳入非 null 的買家 Id，本人驗證行為未被決策 1 的簽章調整意外破壞
- [x] 5.3 確認 `ExpiredOrderCleanupService` 沒有直接注入 Scoped 服務到建構子（只能注入 `IServiceScopeFactory`/`IDateTimeProvider`/`OrderCleanupOptions`/`ILogger` 等 Singleton 相容的依賴），避免 DI 生命週期驗證失敗
- [x] 5.4 確認 `OrderService.CancelExpiredOrderAsync` 在 `requestingBuyerId is null` 分支確實驗證了訂單已逾時，沒有被漏掉（見 design.md 決策 1 的風險提示）
- [x] 5.5 確認 `CleanupOnceAsync` 是 `public`、`ExecuteAsync` 的 `Task.Delay` 有傳入 `stoppingToken`、`IntervalSeconds` 換算 `TimeSpan` 時有用 `Math.Max(1, ...)` 夾住下限（見 design.md 決策 2）
- [x] 5.6 確認 `ExecuteAsync` 啟動後是先執行一輪清理才開始等待（不是先等後清）；外層 `try/catch` 確實只包住整次 `CleanupOnceAsync` 呼叫（含掃描、開 Scope 等非單筆例外，留給下一輪），沒有跟單筆訂單內層的 `try/catch` 混在一起；log 訊息是「cleanup cycle failed」不是「cleanup scan failed」；單筆訂單的 `try/catch` 確實排除了 `OperationCanceledException`（見 design.md 決策 2）
- [x] 5.7 確認 `AddHostedService<ExpiredOrderCleanupService>()` 確實排除 `Testing` 環境（見 design.md 決策 5），避免既有 WebApi 整合測試連帶啟動真實背景服務
- [x] 5.8 執行全部測試（`docker compose exec api dotnet test`），確認通過
- [x] 5.9 比對 tasks 完成狀況與 `order-administration` spec 的全部 11 個 Scenario，確認皆有對應測試
- [x] 5.10 主動告知 spec 同步狀態：`order-administration` 是全新能力，archive 時需要建成新的 `openspec/specs/order-administration/spec.md`
