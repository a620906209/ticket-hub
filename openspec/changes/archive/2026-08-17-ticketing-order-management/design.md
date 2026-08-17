## Context

`ticketing-purchase`（已歸檔）讓買家能建立/確認/取消訂單，但完全沒有後台可見度：Admin 只能透過 pgAdmin 直接查資料庫。同時，逾時的 Pending 訂單目前完全沒有主動清理機制——`Order.GetStatus(now)` 只在查詢時把逾時的 Pending 訂單「推導」成 Expired 顯示，資料庫裡持久化的 `Status` 欄位永遠停留在 Pending；座位實際上會在下一個買家嘗試鎖定同一批座位時，被 `EventSeat.Hold` 的覆寫邏輯間接釋放（見 `seat-reservation` 能力「座位暫扣逾時後可被覆寫鎖定」），但訂單本身從未真的轉為 Cancelled。這種被動、依賴「剛好有人來搶」的清理方式，會讓大量從未被重新鎖定的座位/訂單永遠卡在不一致的中間狀態。

`OrderService` 已經有 `ConfirmOrderAsync`/`CancelOrderAsync` 建立好的「鎖座位 → 鎖後重讀 → 呼叫 pure Handler → Commit」骨架，這次背景清理要重用同一套骨架，差別只在於呼叫端從「HTTP 請求 + 買家身份」換成「背景排程 + 沒有呼叫者身份」。

## Goals / Non-Goals

**Goals:**
- Admin（需 Admin 角色）能查詢所有訂單列表與單筆訂單明細。
- 系統以固定週期背景執行，找出逾時仍為 Pending 的訂單，主動取消並釋放座位，讓資料庫的持久化狀態真正反映訂單已終結。
- 背景清理重用既有 `CancelOrderHandler`/`GetForUpdateAsync`/`ReloadAsync` 的併發安全機制，不重新發明一套清理邏輯。
- 單筆訂單清理失敗不得中斷其餘訂單的清理。

**Non-Goals:**
- 訂單列表的搜尋/篩選/分頁——先做最簡單的全量列表，之後有實際需求（例如訂單量大到列表無法接受）再擴充。
- 清理週期的動態調整 API（例如讓 Admin 在執行期間改設定）——週期只透過設定檔（`appsettings.json`/環境變數）調整，改了要重啟服務。
- 清理失敗的重試機制——這次只做「這輪失敗，下一輪清理再次掃到同一筆訂單、自然重試」，不做指數退避或死信佇列這類進階重試策略。
- 通知買家「你的訂單已因逾時被系統取消」——這次只處理訂單/座位狀態本身，不涉及通知機制。

## Decisions

### 1. `OrderService.CancelExpiredOrderAsync`：重用 `ChangeOrderStatusAsync`，把本人驗證改成可選，並在 Service 層自己防禦「未逾時仍被系統取消」
既有的私有方法 `ChangeOrderStatusAsync(orderId, requestingBuyerId, handle, ct)` 已經是 `ConfirmOrderAsync`/`CancelOrderAsync` 共用的核心骨架。把簽章的 `requestingBuyerId` 參數改成 `Guid? requestingBuyerId`：
- 買家發起的 `ConfirmOrderAsync`/`CancelOrderAsync` 一律傳入非 null 的買家 Id，本人驗證邏輯不變。
- 新增的 `CancelExpiredOrderAsync(orderId, ct)` 傳入 `null`，`ChangeOrderStatusAsync` 內部 `requestingBuyerId is not null && order.BuyerId != requestingBuyerId` 才檢查——`null` 代表「系統呼叫，不需要驗證身份」，直接略過這一步。

**`requestingBuyerId is null` 時 MUST 額外驗證訂單確實已逾時，才能繼續**——這個檢查跟本人驗證是互斥的兩個分支，佔用流程裡同一個位置（`GetByIdAsync` 之後、開交易之前），不是「先驗證本人、再驗證逾時」這種先後關係：`requestingBuyerId` 不是 null 時只做本人驗證（維持既有行為，不做逾時檢查）；是 null 時只做逾時檢查（不做本人驗證，因為沒有本人可驗證）。逾時檢查本身：`_dateTimeProvider.UtcNow < order.HeldUntilUtc` 就回 `Error.Conflict`，不開交易。這裡讀的是第一次 `GetByIdAsync` 拿到、還沒 `ReloadAsync` 過的 `order.HeldUntilUtc`，這樣做是安全的：`HeldUntilUtc` 建立後不可變更（沒有對外開放的 setter），不像 `Status` 會被其他交易改變，不需要等鎖後重讀才能信任這個值——跟本人驗證（`order.BuyerId`）用第一次讀到的值就夠是同一個理由。原本這裡打算省略這個檢查，理由是「`CancelOrderHandler.Handle` 本身不檢查是否逾時，呼叫時機交給決策 3 的 Repository 查詢條件負責，`HeldUntilUtc` 不可變、時間只會前進，不會有『掃描到的訂單到實際處理時反而還沒到期』這種競態」——這個推論本身沒錯（見下面 Risks 第三點），但它只回答了「跟 Repository 掃描之間的競態安不安全」，沒回答另一個問題：**`CancelExpiredOrderAsync` 這個方法本身，一旦繞過買家驗證，就等於是一把「任何人都能取消任何 Pending 訂單」的內部工具，只是恰好現在唯一的呼叫端是背景清理**。如果之後有人（例如未來要做的 Admin 手動取消功能）看到這個方法名字裡有「Expired」，以為呼叫它一定安全、直接拿來重用，卻沒注意到它並不會自己核對訂單是否真的到期，就會在無意間繞開買家授權去取消一筆還沒到期的訂單。加上這個檢查，才讓方法名字（`CancelExpiredOrderAsync`）與實際行為（只取消真的逾時的訂單）一致，`requestingBuyerId is null` 分支不再是單純「跳過驗證」，而是「用『訂單本身已逾時』取代『呼叫者是買家本人』作為另一種同樣站得住腳的授權依據」。

為了做這個檢查，`OrderService` 建構子新增 `IDateTimeProvider` 依賴（先前沒有直接依賴，只有內部的 `CreateOrderHandler`/`ConfirmOrderHandler`/`CancelOrderHandler` 各自持有）；`IDateTimeProvider` 已註冊為 Singleton，注入不受任何生命週期限制。既有的 `OrderServiceTests`/`OrderServiceConcurrencyTests`（`ticketing-purchase` 留下）在建構 `OrderService` 時需要同步補上這個新參數。

### 2. 背景清理服務：`BackgroundService` + 每筆訂單獨立 DI Scope
新增 `ExpiredOrderCleanupService : BackgroundService`，註冊為 `AddHostedService`。`BackgroundService` 本身是 Singleton 生命週期，不能直接注入 Scoped 服務（`OrderService`/`IOrderRepository`/`DbContext`），需要透過注入的 `IServiceScopeFactory` 自行開 Scope；建構子直接注入 `IDateTimeProvider`（Singleton，不受此限制，不需要透過 Scope 拿）、`IServiceScopeFactory`、`OrderCleanupOptions`（見下方設定段落）、`ILogger<ExpiredOrderCleanupService>`。

**啟動時機**：Hosted service 啟動後 MUST 立即執行第一輪清理，完成後才開始等待設定的間隔時間，不是先等一個週期才清理——這點 Migration Plan 段落已經依賴這個行為（「部署後背景服務會立即開始運作」），這裡明確訂為設計決策，不只是隱含在程式碼順序裡。

**每一輪清理（`CleanupOnceAsync(CancellationToken cancellationToken)`）**：
1. 開一個 Scope，取得 `IOrderRepository`，呼叫 `GetExpiredPendingOrderIdsAsync(_dateTimeProvider.UtcNow, cancellationToken)` 拿到這一輪要處理的訂單 Id 清單，Scope 隨即釋放（這個查詢只讀，不需要保留交易；`now` 用建構子注入的 `IDateTimeProvider`，不需要從 Scope 內另外拿）。**這一步本身拋出的例外（例如資料庫暫時連不上）不在下面第 3 點的單筆 `try/catch` 保護範圍內，MUST 交給呼叫端 `ExecuteAsync` 統一處理**（見下方「整輪清理失敗的處理方式」）。
2. 對清單內每一筆訂單 Id，**依序**（用一般的 `foreach`，不是 `Task.WhenAll` 平行處理）處理：先呼叫 `cancellationToken.ThrowIfCancellationRequested()`（見下方「取消語意」），**開新 Scope、取得該 Scope 的 `OrderService`、呼叫 `CancelExpiredOrderAsync(orderId, cancellationToken)` 這三步 MUST 全部包在第 3 點的單筆 `try/catch` 裡面**（而不是整輪清理共用同一個 Scope/`DbContext`，也不是把「開 Scope、解析服務」放在 `try` 外面——`CreateScope()`/`GetRequiredService<OrderService>()` 本身失敗，一樣要算單筆訂單的失敗，不能讓它跳出 `foreach`、中斷剩下的訂單）。刻意選依序而非平行：雖然每筆訂單各自獨立的 Scope/`DbContext`，理論上可以平行處理不會互相干擾，但平行處理需要另外控制併發上限（避免一輪掃到大量逾時訂單時瞬間開出過多資料庫連線），這次沒有實際的效能需求驅動這個複雜度（見 Non-Goals「先求正確、不先做效能優化」的一貫立場），先用最簡單的依序處理，之後真的是瓶頸再改。
3. 每筆訂單處理包在 `try/catch` 內，失敗只記錄結構化 log（含 correlation 用的 orderId，不含敏感資訊）並繼續下一筆，不中斷整輪清理。**這裡的 `try/catch` 只防禦「真的被拋出的例外」（例如資料庫連線問題），不是為了處理 `CancelExpiredOrderAsync` 回傳的 `Result.Failure`**——`Result.Failure` 是正常回傳值，foreach 迴圈本來就會繼續跑下一筆，不需要 `try/catch` 才能「不中斷」；`Result.Failure` 的情況一樣要記 log（方便追蹤是哪些訂單、什麼原因沒清理成功），但不算走到 `catch` 分支。**`catch` 子句 MUST 排除 `OperationCanceledException`（`catch (Exception exception) when (exception is not OperationCanceledException)`，或先寫一個 `catch (OperationCanceledException) { throw; }` 擋在前面）**：`stoppingToken` 被取消（應用程式關閉）時，`CancelExpiredOrderAsync` 內部的資料庫呼叫會拋出 `OperationCanceledException`；如果被這裡的通用 `catch (Exception)` 一併吞掉當成「這筆訂單處理失敗」記 log 後繼續下一筆，會讓背景服務在應用程式要求關閉時，還在繼續處理清單裡剩下的訂單，拖慢甚至可能阻礙關閉，跟決策 2 前面要求 `Task.Delay` 一定要能被 `stoppingToken` 中斷的精神矛盾。`OperationCanceledException` MUST 直接往外拋，讓 `foreach` 中止、`CleanupOnceAsync` 結束、`ExecuteAsync` 外層（見下方「整輪清理失敗的處理方式」）接住後自然停止。

**取消語意**：`CleanupOnceAsync` 收到的 `cancellationToken` 一路傳到底——`GetExpiredPendingOrderIdsAsync`、每筆 `CancelExpiredOrderAsync` 呼叫都要收到同一個 token；`foreach` 內每筆訂單開始處理前先呼叫 `ThrowIfCancellationRequested()`，避免應用程式關閉的請求已經送達，還繼續為下一筆訂單開新的 Scope／資料庫連線才發現要停。

**整輪清理失敗的處理方式**：`CleanupOnceAsync` 整次呼叫（不只是掃描那一步，還包含建立 Scope、取得服務等所有沒被第 3 點單筆 `try/catch` 接住的例外）若拋出例外，選擇「記錄後留給下一輪自然重試」而非「讓整個 hosted service 失敗」——這跟 Non-Goals「這輪失敗，下一輪清理再次掃到、自然重試」是同一個精神，資料庫的暫時性問題不該讓整個應用程式的其他功能（買家下單、Admin 查詢等）也一起停擺。做法：`ExecuteAsync` 的迴圈外層包一層 `try/catch`，包住整次 `CleanupOnceAsync(stoppingToken)` 呼叫（log 訊息用「cleanup cycle failed」而非「cleanup scan failed」，避免暗示只有掃描那一步才會被這層接住）：
```csharp
try
{
    await CleanupOnceAsync(stoppingToken);
}
catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
{
    break; // 應用程式要求關閉，正常結束迴圈，不算錯誤
}
catch (Exception exception)
{
    _logger.LogError(exception, "Expired order cleanup cycle failed; will retry next interval.");
}
```
這一層只保護「掃描」跟「決策 2 第 3 點以外」的例外（例如 `foreach` 本身邏輯錯誤），不會跟第 3 點單筆訂單的 `try/catch` 重複——單筆訂單的例外已經在裡面被攔下，不會傳到這一層。

`ExecuteAsync` 的 `Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds)), stoppingToken)`（`Math.Max` 的用途見下方「防禦邊界值」段落）**MUST 傳入 `stoppingToken`**，否則應用程式關閉時（例如 `docker compose down`）背景服務會等到整個延遲時間跑完才真正停止，拖慢優雅關閉的速度。

**為什麼每筆訂單各自開 Scope，而不是整輪共用一個**：這是背景服務，執行時間不像 HTTP request 有天然的上限，一輪可能要處理數十上百筆訂單；如果共用同一個 `DbContext`，change tracker 會隨著處理筆數增加持續累積已追蹤的 `Order`/`EventSeat` 實體，長時間執行的背景服務容易造成記憶體緩慢增長。每筆訂單獨立 Scope，處理完立即釋放，沒有這個問題，效能代價（多開幾次 DbContext connection）在背景排程情境下可以接受。

**清理週期設定**：新增 `OrderCleanupOptions`（`IntervalSeconds`，預設 300）。這裡比照的是 `AuthOptions` 而非 `JwtOptions` 的既有模式——`JwtOptions` 缺值會讓應用程式完全無法運作（無法簽發/驗證 Token），所以用 `[Required]` + `ValidateOnStart` fail fast；`OrderCleanupOptions` 缺值有安全的預設值（300 秒）可用，屬性不加驗證標註，也不需要 `ValidateOnStart`，這點更接近 `AuthOptions`（`RefreshTokenExpirationDays`/`PasswordResetTokenExpirationMinutes` 同樣都是有預設值、不驗證的設定）。註冊方式也直接比照 `AuthOptions` 目前的寫法：`Configure<OrderCleanupOptions>(...)` 綁定 `appsettings.json` 的 `OrderCleanup` 區段，再 `AddSingleton(sp => sp.GetRequiredService<IOptions<OrderCleanupOptions>>().Value)` 額外註冊一個「已解開包裝」的 `OrderCleanupOptions` 單例——`ExpiredOrderCleanupService` 建構子直接注入 `OrderCleanupOptions`（不是 `IOptions<OrderCleanupOptions>`），跟 `AuthOptions` 目前的消費端寫法一致。

**防禦邊界值：「不驗證設定值」不代表完全不設防線**：上一段說 `OrderCleanupOptions` 不加驗證標註、不用 `ValidateOnStart`，但這不表示 `IntervalSeconds` 可以是任意整數而不會出事——`Task.Delay` 收到零或負數的 `TimeSpan` MUST NOT 拋出例外（`Task.Delay` 對負數 `TimeSpan` 會拋 `ArgumentOutOfRangeException`，`BackgroundService.ExecuteAsync` 未攔截的例外預設會讓整個應用程式當掉，不是只有這個背景服務停止），所以 `ExecuteAsync` 換算 `TimeSpan` 時 MUST 用 `TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds))`，把 `appsettings.json` 誤設成 `0` 或負數的情況夾到最小值 1 秒，而不是直接讓應用程式崩潰。這不是要驗證設定值「合不合理」（例如設 1 秒會讓清理跑得太頻繁，這是使用者自己要承擔的後果），只是防止一個型別上合法、但會讓 `Task.Delay` 直接拋例外的邊界值搞垮整個應用程式。

**可測試性**：
- **`CleanupOnceAsync` 的方法簽章 MUST 是 `public`（不能是 `private`）**：這個方法要從 `ProjectC.WebApi.Tests`（另一個組件）呼叫做整合測試，這個專案目前沒有設定 `InternalsVisibleTo`（`internal` 也看不到），加這個設定又是引入一個這個 codebase 目前完全沒用過的新模式，不如直接讓這一個方法 `public`——它本來就是「執行一輪清理」這個有意義的操作單元，公開它不算洩漏奇怪的實作細節。
- **測試時不能透過 DI 容器解析 `ExpiredOrderCleanupService` 本身**：`AddHostedService<T>()` 只會把 `T` 註冊成 `IHostedService`，不會額外把 `T` 自己註冊成可解析的具體型別，所以 `factory.Services.GetRequiredService<ExpiredOrderCleanupService>()` 會直接失敗（找不到這個服務）。測試要像 `OrderServiceConcurrencyTests`（`ticketing-purchase` 留下的既有慣例，直接 `new` 服務、不透過 DI 容器）一樣，自己組出建構子需要的四個依賴（`IServiceScopeFactory` 用 `factory.Services.GetRequiredService<IServiceScopeFactory>()`——這個是框架內建、一定會有，不受 `AddHostedService` 影響；`IDateTimeProvider` 同理用 `GetRequiredService`；`OrderCleanupOptions` 直接 `new`；`ILogger<ExpiredOrderCleanupService>` 用 `GetRequiredService` 或 `NullLogger<ExpiredOrderCleanupService>.Instance`），`new ExpiredOrderCleanupService(...)` 之後直接呼叫 `CleanupOnceAsync`。
- **「單筆訂單清理失敗不影響其餘訂單」這個 Scenario 要怎麼測，不能用 Mock**：`OrderService` 是 `sealed class`、沒有對應介面，`ProjectC.WebApi.Tests` 也沒有 Moq（只有 `ProjectC.Application.Tests` 有），沒辦法模擬「`OrderService.CancelExpiredOrderAsync` 對某一筆訂單拋出例外」。改用真實情境重現「一筆處理失敗、其餘繼續」：比照 `CancelOrderHandlerTests.Handle_WhenSeatWasSoldByThisSameOrder_ReturnsFailureAsInconsistentState`（`ticketing-purchase` 留下）已經驗證過的既有手法，種兩筆都逾時的 Pending 訂單 A、B，對 B 的 `EventSeat` 直接用 DbContext 把 `_soldByOrderId`（shadow 私有欄位）改成 B 自己的訂單 Id、繞過 `Order.Confirm()`（模擬「座位已由本訂單售出，但訂單自己仍是 Pending」這種不一致狀態），讓 `CancelOrderHandler.Handle` 對 B 回 `Error.Conflict`（`Result.Failure`，不是例外）；驗證 A 依然被正確清理成 Cancelled、B 維持 Pending 不變，清理沒有因為 B 中途中斷。**這驗證的是「`Result.Failure` 不會讓 foreach 迴圈提早結束」，跟 `try/catch` 防禦的「真正拋出例外」是兩回事**——`try/catch` 本身沒有自動化測試覆蓋，作為對基礎設施層級例外（例如資料庫連線問題）的合理防禦措施保留，這是本次刻意接受的測試缺口，不是遺漏（見 Risks）。

### 3. `IOrderRepository.GetExpiredPendingOrderIdsAsync`：只回傳 Id，不回傳整個 Order 物件
```csharp
Task<IReadOnlyList<Guid>> GetExpiredPendingOrderIdsAsync(DateTime now, CancellationToken cancellationToken);
```
實作：`_dbContext.Orders.Where(o => o.Status == OrderStatus.Pending && o.HeldUntilUtc <= now).Select(o => o.Id).ToListAsync(...)`——標準 EF Core LINQ 查詢，不需要悲觀鎖（這只是「找出候選清單」，真正要修改狀態時，決策 1 的流程會在各自的 Scope 內重新 `GetByIdAsync` + `GetForUpdateAsync` 鎖定，這個掃描查詢本身不用鎖，也不保證掃描當下的結果到實際處理時還完全準確——這是預期內的，逾時只會增加不會減少，掃描到的訂單到實際處理時只可能「已經被別的方式終結」（例如剛好買家自己也在取消），不可能「突然又不逾時」）。只回傳 `Guid` 清單而不是完整 `Order` 物件，是因為決策 2 已經決定每筆訂單各自用新 Scope 重新載入，這裡先載入完整物件只會立刻被丟棄，浪費一次查詢的資料量。

### 4. Admin 查詢端點：`AdminOrdersController` + 兩個查詢 Handler
- `GetOrdersHandler.HandleAsync(ct) : Task<IReadOnlyList<OrderSummaryDto>>`（**不包 `Result`**——這個查詢沒有會失敗的分支，比照既有 `GetEventsHandler`（`ticketing-purchase` 留下）的慣例）：`IOrderRepository.GetAllAsync()` → 回傳 `OrderSummaryDto` 列表，欄位 `{ Id, EventId, BuyerId, Status, HeldUntilUtc }`；`Status` 用 `order.GetStatus(now)` 轉字串（`now` 來自 `IDateTimeProvider`），回報即時狀態（可能是 Expired），不是持久化欄位本身。
- `GetOrderByIdHandler.HandleAsync(orderId, ct) : Task<Result<OrderDetailDto>>`（**要包 `Result`**——訂單不存在時要能回 `Error.NotFound`）：`IOrderRepository.GetByIdAsync(orderId)` → `null` 回 `Error.NotFound` → 回傳 `OrderDetailDto`，欄位 `{ Id, EventId, BuyerId, Status, HeldUntilUtc, Items: [{ Id, EventSeatId, UnitPrice }] }`；**`Status` 欄位跟 `GetOrdersHandler` 一樣用 `order.GetStatus(now)`**，不是持久化欄位本身——對應 spec「查詢存在的訂單明細」Scenario 講的「即時狀態」，明細頁跟列表頁的 `Status` 語意 MUST 一致。
- `AdminOrdersController`（`api/admin/orders`，`[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`，比照 `AdminEventsController`）：`GET /` 因為 `GetOrdersHandler` 不回 `Result`，直接 `return Ok(await _getOrdersHandler.HandleAsync(ct));`；`GET /{id:guid}` 因為 `GetOrderByIdHandler` 回 `Result<OrderDetailDto>`，用既有 `result.ToActionResult(Ok)` 慣例。

`IOrderRepository.GetAllAsync()` 需要額外新增（`GetByIdAsync` 已經存在），簽章比照 `IEventRepository.GetAllAsync` 的既有慣例：`Task<IReadOnlyList<Order>> GetAllAsync(CancellationToken cancellationToken)`，實作 MUST 一併 `Include(o => o.Items)`（跟 `GetByIdAsync` 的既有規則一致，`OrderDetailDto` 需要 Items，`OrderSummaryDto` 雖然不需要，但共用同一個查詢方法比為了省一次 Join 另外寫一個不含 Items 的版本更簡單，訂單數量沒有大到需要在意這個差異——見 Non-Goals）。**這是目前為了重用 `GetByIdAsync` 的 `Include` 契約、換取實作簡單而做的取捨，不是長期最佳方案**：訂單量與每筆訂單的座位項目數增加後，`GetOrdersHandler`（只需要 5 個欄位）會連帶把不需要的 `Items` 一起載入，記憶體與查詢成本隨訂單量成長；等真的需要分頁或這個成本變得有感，再拆成專用的 projection 查詢（例如直接 `Select` 到 `OrderSummaryDto` 形狀，不 `Include` 整個 `Items` 集合）。

## Risks / Trade-offs

- **[Risk]** 背景清理是系統第一次有「非 HTTP 請求觸發」的資料修改路徑，如果 Scope 管理寫錯（例如忘記每筆訂單各自呼叫 `IServiceScopeFactory.CreateScope()`，把兩筆以上訂單的處理擠進同一個 Scope、共用同一個 `DbContext`），輕則違背決策 2「每筆獨立 Scope」的設計初衷（change tracker 隨處理筆數累積），重則在同一個 `DbContext` 被非執行緒安全地重疊存取時丟出例外。→ **Mitigation**：整合測試需要直接呼叫 `public` 的 `CleanupOnceAsync`（不必真的等 `Task.Delay`），驗證單次執行能正確處理多筆逾時訂單，且不影響未逾時訂單。
- **[Risk]** 清理週期若設太短，跟大量買家併發操作同一批座位的尖峰時段重疊，可能增加悲觀鎖的等待/競爭。→ **Mitigation**：預設 5 分鐘一輪，遠低於訂單 10 分鐘的暫扣時間，一輪處理的訂單數量在正常流量下不會太多；真的成為瓶頸再依實際數據調整週期或改批次處理。
- **[Risk]** `GetExpiredPendingOrderIdsAsync` 掃描與各筆訂單實際處理之間有時間差，理論上可能同一筆訂單被同時掃到兩次（例如清理週期設太短、上一輪還沒處理完下一輪又開始）。→ **Mitigation**：不需要特別處理——`CancelOrderHandler.Handle` 對「訂單已經不是 Pending」本來就會回 Conflict（見 `ticket-ordering`「嘗試取消已取消的訂單」），重複處理同一筆訂單的第二次呼叫會自然被既有邏輯拒絕，不會造成資料錯誤，只是多一次無害的失敗 log。
- **[Risk]** `CleanupOnceAsync` 內保護單筆訂單處理的 `try/catch`，有兩個分支沒有自動化測試覆蓋：(1) 「真正的基礎設施例外」（例如資料庫連線問題）——`OrderService` 是 `sealed class`、無介面，`ProjectC.WebApi.Tests` 也沒有 Moq，無法模擬拋出這類例外；(2) 「取消訊號恰好在 foreach 處理某一筆訂單『途中』抵達，單筆 `catch` 正確排除 `OperationCanceledException` 不吞掉它」這個確切時間點——task 4.7 測的是「呼叫 `CleanupOnceAsync` 前 token 就已取消」，這會讓掃描步驟先拋出例外，根本不會進到 foreach 內單筆訂單的 `try/catch`，所以沒有測到單筆 `catch` 本身排除 `OperationCanceledException` 這段程式碼是否真的正確。如果這段防禦程式碼本身寫錯（例如漏掉 `try/catch`、或單筆 `catch` 誤用了不排除 `OperationCanceledException` 的寫法），這兩種情況都不會被測試抓到。→ **Mitigation**：兩者都是刻意接受的測試缺口，靠 code review 把關；「途中取消」要能測，需要一個能在 foreach 跑到一半時插入取消動作的測試 hook，這次選擇不為了測這一個時間點而引入這種複雜度（跟避免為了測基礎設施例外而幫 `OrderService` 開介面、加 Moq 是同一個判斷）。
- **[Risk]** `CancelExpiredOrderAsync` 略過買家本人驗證，如果未來被其他呼叫端（例如某個 Admin 手動取消訂單的功能）誤以為「反正是取消訂單，用這個方法就好」而直接重用，會在不知情的狀況下繞過買家授權去取消一筆還沒到期的 Pending 訂單。→ **Mitigation**：見決策 1，`requestingBuyerId is null` 分支 MUST 額外驗證訂單確實已逾時才能繼續，讓「訂單本身已逾時」成為唯一能取代「呼叫者是買家本人」的合法授權依據，方法名字與實際行為一致；之後如果真的要做 Admin 手動強制取消（不要求已逾時），須設計成獨立的方法/端點，不能直接重用 `CancelExpiredOrderAsync`。

### 5. 整合測試環境 MUST NOT 啟動真實的背景清理服務
`ProjectC.WebApi.Tests` 底下每個用到 `CustomWebApplicationFactory` 的測試類別，都會啟動完整的 `Program`（含所有 `AddHostedService` 註冊）。如果 `AddHostedService<ExpiredOrderCleanupService>()` 無條件註冊，**現有的每一個 WebApi 整合測試類別**（不只是這次新增的、專門測 order-administration 的測試）都會連帶啟動一個真的在跑的背景清理服務，對著該測試類別自己的 Testcontainers Postgres 執行——依決策 2「啟動後立即執行第一輪清理」，每個測試類別一啟動就會真的打一次資料庫查詢，之後預設每 300 秒還會再跑一次。訂單有 10 分鐘暫扣時間，單一測試類別的執行時間通常遠低於這個門檻，實務上不太可能真的搶著取消掉測試建立的訂單，但這仍是不必要的非決定性因素（測試結果的正確性不該依賴「背景服務剛好沒跑那麼快」這種時序巧合），也違反測試互相隔離的原則。

**修法**：`CustomWebApplicationFactory` 已經用 `builder.UseEnvironment("Testing")` 標記測試環境（見 `tests/ProjectC.WebApi.Tests/TestSupport/CustomWebApplicationFactory.cs:77`）。`Program.cs` 註冊 `AddHostedService<ExpiredOrderCleanupService>()` 時 MUST 排除 `Testing` 環境：
```csharp
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<ExpiredOrderCleanupService>();
}
```
這跟既有的 `if (app.Environment.IsDevelopment()) { app.MapOpenApi(); ... }`（`Program.cs:129`）是同一種依環境條件註冊的既有慣例，不是新模式。這不影響測試的可測試性——task 4.5～4.7 本來就是直接 `new ExpiredOrderCleanupService(...)`，不透過 DI 容器解析（見決策 2「可測試性」），排除 `AddHostedService` 註冊不會讓這些測試不能跑。

## Migration Plan

- 無資料庫 schema 變更，不需要新的 Migration。
- 新增設定：`appsettings.json` 補上 `OrderCleanup:IntervalSeconds`（預設值寫在 `OrderCleanupOptions` 類別本身，設定檔缺這個區段時仍可正常運作）。
- 部署後背景服務會立即開始運作；第一輪執行前，建議先確認 `docker compose exec api dotnet ef database update` 後的資料庫沒有殘留測試用的逾時訂單（否則第一輪會清理掉，這是預期行為，不是風險）。

## Open Questions

（無——範圍與清理策略都已確認清楚。）
