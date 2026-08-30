## 1. Domain

- [ ] 1.1 新增 `ProjectC.Domain.Notifications.IEmailNotificationService`：`Task NotifyTicketsIssuedAsync(string toEmail, string eventTitle, Guid orderId, int ticketCount, CancellationToken cancellationToken)`（回傳 `Task`，失敗用例外表達，不用 `Result` 型別；介面直接以 Email 為抽象邊界，不宣稱管道無關，見 design.md 決策 1）；XML doc 明確記錄契約：實作拋出例外時，例外的 `Message` MUST NOT 包含完整、未遮蔽的收件 Email（呼叫端會直接記錄整個例外物件，不會另外遮蔽例外訊息內容，見 design.md 決策 5 第三輪外部審查段落）

## 2. Infrastructure

- [ ] 2.1 新增 `ProjectC.Infrastructure.Notifications.MockEmailNotificationServiceOptions`（`bool AlwaysSucceed = true`，比照 `MockPaymentGatewayOptions` 命名與預設值慣例）
- [ ] 2.2 新增 `ProjectC.Infrastructure.Notifications.MockEmailNotificationService : IEmailNotificationService`：注入 `MockEmailNotificationServiceOptions`、`ILogger<MockEmailNotificationService>`；`AlwaysSucceed = true` 時記錄結構化 log（`{ToEmail}`/`{EventTitle}`/`{OrderId}`/`{TicketCount}`）後正常完成；`AlwaysSucceed = false` 時拋出例外模擬寄送失敗，**例外訊息 MUST NOT 包含 `toEmail`（或任何 Email 相關內容）**，用一個固定、不帶收件人資訊的訊息（例如 `"Simulated email delivery failure."`）（見 design.md 決策 4、5，遵守 1.1 的介面契約）
- [ ] 2.3 新增 `ProjectC.Infrastructure.Notifications.EmailMasker`（`public static string Mask(string? email)`）：**合法**定義為「剛好包含一個 `@`，且 `@` 前後兩段各自 trim 前後 whitespace 後都至少包含一個非 whitespace 字元」，合法時回傳 `{local 第一個字元（未 trim 的原始字元）}***@{domain}`（`local` 只有 1 字元時行為相同，不需特殊分支）；`email` 為 `null`／空字串／純 whitespace／不含 `@`／含兩個以上 `@`／`@` 前後任一段為空字串或 trim 後為空字串（例如 `a@`、`@example.com`、`a@@example.com`、`a@ `、` @example.com`）時一律回傳固定字串 `"[redacted]"`，不對這些輸入做字串切割；此方法 MUST NOT 對任何輸入拋出例外（見 design.md 決策 5 的遮蔽函式契約——第二輪外部審查收斂了「合法」的定義，第三輪再收斂 local/domain 全為 whitespace 的邊界情況）
- [ ] 2.4 `MockEmailNotificationService` 寫入 log 前呼叫 `EmailMasker.Mask(toEmail)` 取得遮蔽後的值，不得將完整、未遮蔽的 Email 寫入 log——遮蔽只發生在 log 這一步，方法參數與行為本身仍使用完整 Email（見 design.md 決策 5，個資 log 遮蔽是 CLAUDE.md 明確規則）

## 3. Application

- [ ] 3.1 `ProjectC.Application.csproj` 明確新增 `PackageReference Include="Microsoft.Extensions.Logging.Abstractions"`（不依賴 EF Core 間接帶入的 transitive dependency），並在根目錄 `Directory.Packages.props` 新增對應 `PackageVersion`——比照專案既有 `Microsoft.EntityFrameworkCore`/`Microsoft.AspNetCore.OpenApi`/`Microsoft.AspNetCore.Authentication.JwtBearer` 這幾個隨 .NET runtime 發布、目前皆釘選 `10.0.11` 的套件慣例，先預設也用 `10.0.11`；動手新增前 MUST 實際確認這個版本號在 NuGet 上存在且可還原（例如 `docker compose exec api dotnet add package Microsoft.Extensions.Logging.Abstractions --version 10.0.11` 試跑，或查詢 NuGet），不得未經確認就照抄版本號（見 design.md Context 段落）
- [ ] 3.2 新增 `ProjectC.Application.Orders.TicketIssuedNotificationContent`（`sealed record`，欄位：`ToEmail`/`EventTitle`/`OrderId`/`TicketCount`）與 `ProjectC.Application.Orders.TicketIssuedNotificationContentFactory`（`public static class`，方法 `Create(Guid orderId, Order? order, Event? @event, Member? buyer)`）：`order`/`@event` 為 `null`、`buyer` 為 `null`、或 `buyer.Email` 為 `null`/空字串/純 whitespace 時分別丟出訊息明確的 `InvalidOperationException`（含 `orderId`，`@event` 為 `null` 時額外含 `order.EventId`）；否則回傳組好的 `TicketIssuedNotificationContent`，`TicketCount` = `order.Items.Sum(i => i.Quantity)`（見 design.md 決策 2 的「外部審查抓到」段落——取代原本用 `!` 假設資料必然存在的做法）
- [ ] 3.3 `OrderService` 建構子新增依賴：`IEmailNotificationService`、`IApplicationDbContext`（查買家 Email，比照 `GetAdminEventsHandler` 查會員顯示名稱的既有慣例）、`ILogger<OrderService>`（Application 層首次引入 `ILogger`，見 design.md Context／決策 3 的引入理由）
- [ ] 3.4 `ConfirmOrderAsync`：`ChangeOrderStatusAsync` 交易提交成功後，重新查詢 `Order`（含 `Items`）、`Event`（取 `Title`）、買家 `Member`（取 `Email`），呼叫 `TicketIssuedNotificationContentFactory.Create(...)` 組出通知內容並驗證資料完整，再呼叫 `IEmailNotificationService.NotifyTicketsIssuedAsync`；整段包在 `try/catch` 內：
  - [ ] 3.4.1 `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)`：僅當這次呼叫本身接收到的 `cancellationToken` 被觸發時才符合，不記錄為 Error（比照 `ExpiredOrderCleanupService` 既有慣例），直接放行，不重新拋出；不滿足這個 `when` 條件的 `OperationCanceledException`（例如通知服務或其依賴自己逾時、使用了另一個 token）不會被這個分支攔截，會落入下一個 `catch (Exception)`（見 design.md 決策 2 的「外部審查抓到」段落——先前版本無條件捕捉所有 `OperationCanceledException`，會誤判為呼叫端取消而漏記真正的失敗）
  - [ ] 3.4.2 `catch (Exception exception)`：涵蓋「`TicketIssuedNotificationContentFactory` 丟出的 `InvalidOperationException`」「重新查詢 Order/Event/Member 時的其他例外」「呼叫通知服務本身拋出的例外」「不滿足 3.4.1 條件的 `OperationCanceledException`」，用 `ILogger` 記錄（含 `{OrderId}`），**不重新拋出**
  - [ ] 3.4.3 兩個 catch 分支之後，最終仍回傳原本 `ChangeOrderStatusAsync` 的成功結果（見 design.md 決策 2、3 的完整程式碼範例）
- [ ] 3.5 確認 `ConfirmOrderAsync` 在 `ChangeOrderStatusAsync` 回傳失敗時直接回傳該失敗結果，不執行任何通知相關程式碼（無需修改既有邏輯，僅需確認目前的 early-return 結構已經滿足這一點）

## 4. WebApi

- [ ] 4.1 `Program.cs` 新增 `builder.Services.Configure<MockEmailNotificationServiceOptions>(builder.Configuration.GetSection(MockEmailNotificationServiceOptions.SectionName));` + `builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<MockEmailNotificationServiceOptions>>().Value);` + `builder.Services.AddSingleton<IEmailNotificationService, MockEmailNotificationService>();`，比照 `IPaymentGateway`/`MockPaymentGatewayOptions` 既有登錄方式緊鄰放置（遺漏會讓 `OrderService` 建構子 DI resolution 失敗，既有 `ConfirmOrder_ByBuyerOnOwnPendingOrder_Returns204AndSellsSeat` 等測試會直接失敗，見 4.2）

## 5. 測試工具同步

- [ ] 5.1 `tests/ProjectC.Infrastructure.Tests/TestSupport/OrderServiceTestFactory.cs`：`Create` 方法新增可選參數 `IEmailNotificationService? emailNotificationService = null`（預設 `new MockEmailNotificationService(new MockEmailNotificationServiceOptions(), NullLogger<MockEmailNotificationService>.Instance)`），`IApplicationDbContext` 直接傳入既有的 `dbContext` 參數（`ApplicationDbContext` 已實作該介面，不需新增參數），`ILogger<OrderService>` 用 `NullLogger<OrderService>.Instance`，確保既有 4 個使用這個工廠的測試檔（`OrderServiceConcurrencyTests`/`OrderServiceQueueModeLinearizationTests`/`RedeemTicketConcurrencyTests`/`TicketTypeConcurrencyTests`）不需個別修改即可編譯
- [ ] 5.2 新增 `tests/ProjectC.Infrastructure.Tests/TestSupport/SpyEmailNotificationService.cs`：`IEmailNotificationService` 的測試替身，記錄每次呼叫的參數（`ToEmail`/`EventTitle`/`OrderId`/`TicketCount`）於 `List`，並提供：
  - [ ] 5.2.1 可設定的 `ExceptionToThrow`（`Exception?`）：設定後下次呼叫時拋出，模擬通知失敗
  - [ ] 5.2.2 可選的 `OnNotifyAsync`（`Func<CancellationToken, Task>?`）：每次呼叫時（記錄參數之後、`ExceptionToThrow` 判斷之前）若有設定就先 `await` 執行，讓測試能在通知呼叫的當下執行任意檢查（例如用另一個獨立的 `DbContext` 重新查詢 DB 狀態，或主動觸發取消，見 6.4.5），不需要讓 Spy 本身依賴 EF Core 或任何測試基礎設施（見 design.md 決策 3 測試可驗證性段落與 tasks.md 6.4.9）。明確定義例外處理順序：`OnNotifyAsync` 拋出的例外直接往外傳播（視同通知服務在這次呼叫中失敗），此時**不再**檢查 `ExceptionToThrow`；`ExceptionToThrow` 只有在 `OnNotifyAsync` 未設定、或已設定但正常完成（沒有拋出例外）時才判斷是否丟出——避免這個順序留給實作者自行猜測（外部審查抓到）

## 6. 測試

- [ ] 6.1 `MockEmailNotificationService` 單元測試（`ProjectC.Infrastructure.Tests`，不需要 Testcontainers，比照 `HmacTicketSigningServiceTests`/`TicketQrCodeGeneratorTests` 的既有無 DB 測試慣例；使用可收集記錄的測試 `ILogger`，例如自訂 `ListLogger<T> : ILogger<T>` 或既有測試共用的 logger spy）：
  - [ ] 6.1.1 `AlwaysSucceed = true` → 呼叫完成不拋出例外，且 logger 收到一筆結構化 log，其具名欄位（`ToEmail`/`EventTitle`/`OrderId`/`TicketCount`）等於呼叫時傳入的參數（`OrderId`/`EventTitle`/`TicketCount` 完整比對；`ToEmail` 比對 `EmailMasker.Mask` 後的格式，驗證遮蔽邏輯確實套用，不是原樣輸出）
  - [ ] 6.1.2 `AlwaysSucceed = false` → 呼叫拋出例外
  - [ ] 6.1.3 `AlwaysSucceed = true` → logger 記錄的 `ToEmail` 欄位 MUST NOT 等於傳入的完整 Email（驗證確實被遮蔽，不是巧合通過 6.1.1 的格式比對）
  - [ ] 6.1.4 `AlwaysSucceed = false` → 拋出的例外之 `Message` MUST NOT 包含傳入的完整 `toEmail` 字串（對應 spec.md Scenario「通知服務拋出例外時，例外訊息本身不得包含未遮蔽的完整 Email」，驗證 1.1 介面契約在唯一實作上確實被遵守）
- [ ] 6.2 `EmailMasker.Mask` 單元測試（`ProjectC.Infrastructure.Tests`，不需要 DB）：
  - [ ] 6.2.1 一般 Email（例如 `buyer@example.com`）→ 回傳 `b***@example.com`
  - [ ] 6.2.2 local part 只有 1 字元（例如 `a@example.com`）→ 回傳 `a***@example.com`，不拋出例外
  - [ ] 6.2.3 `null`／空字串／純 whitespace／不含 `@` 的輸入（各自一個測試案例）→ 回傳固定字串 `"[redacted]"`，不拋出例外（對應 spec.md Scenario「收件信箱格式不合法或為空時，遮蔽動作本身不拋出例外」）
  - [ ] 6.2.4 `@` 前後任一段為空字串或含兩個以上 `@` 的邊界輸入（各自一個測試案例：`a@`、`@example.com`、`a@@example.com`）→ 回傳固定字串 `"[redacted]"`，不拋出例外、不得產生 `a***@` 這種缺 domain 的錯誤輸出（對應 design.md 決策 5「合法 Email」定義收斂後的邊界情況，第二輪外部審查抓到）
  - [ ] 6.2.5 `@` 前後任一段非空字串但 trim 後為純 whitespace 的邊界輸入（各自一個測試案例：`a@ `、` @example.com`）→ 回傳固定字串 `"[redacted]"`，不拋出例外（對應 design.md 決策 5「合法 Email」定義第三輪收斂——只檢查字串長度非 0 不足以排除純 whitespace 的 local/domain part）
- [ ] 6.3 `TicketIssuedNotificationContentFactory.Create` 單元測試（`ProjectC.Infrastructure.Tests` 或 `ProjectC.Application` 對應的測試專案，純記憶體物件，不需要 DB；`Member`/`Event` 用既有的 `Member.Register(...)`／`Event` 公開建構方式在記憶體中組出）：
  - [ ] 6.3.1 `order` 為 `null` → 丟出 `InvalidOperationException`，訊息含 `orderId`
  - [ ] 6.3.2 `@event` 為 `null` → 丟出 `InvalidOperationException`，訊息含 `orderId`
  - [ ] 6.3.3 `buyer` 為 `null` → 丟出 `InvalidOperationException`，訊息含 `orderId`
  - [ ] 6.3.4 `buyer.Email` 為空字串或純 whitespace → 丟出 `InvalidOperationException`，訊息含 `orderId`
  - [ ] 6.3.5 合法輸入 → 回傳的 `TicketIssuedNotificationContent` 各欄位正確，`TicketCount` 為所有 `OrderItem.Quantity` 加總
- [ ] 6.4 `OrderService.ConfirmOrderAsync` 通知行為整合測試（`ProjectC.Infrastructure.Tests`，Testcontainers，用 `OrderServiceTestFactory.Create(dbContext, emailNotificationService: spy)` 注入 `SpyEmailNotificationService`）：
  - [ ] 6.4.1 訂單確認成功 → `SpyEmailNotificationService.Calls` 恰有一筆，內容為該買家 Email、活動名稱、訂單 Id、正確票券張數，且 `ConfirmOrderAsync` 回傳結果 `IsSuccess` 為 `true`（同時對應 Scenario「訂單確認成功觸發通知」與 Scenario「通知服務成功，行為不受影響」——後者驗證的是「通知正常送出時，訂單確認結果與通知服務不存在時完全一致」，用同一組 Arrange/Act 就能同時斷言兩者，不需要重複的測試方法）
  - [ ] 6.4.2 訂單同時含座位制與計數制項目 → 通知的票券張數為所有項目 `Quantity` 加總（對應 Scenario「混合座位制與計數制票種的訂單，票券張數為加總」）
  - [ ] 6.4.3 訂單確認失敗（例如非買家本人呼叫、訂單已逾時）→ `SpyEmailNotificationService.Calls` 為空（對應 Scenario「訂單確認失敗不觸發通知」）
  - [ ] 6.4.4 `SpyEmailNotificationService.ExceptionToThrow` 設定為一般 `Exception` 後呼叫確認 → `ConfirmOrderAsync` 回傳結果 `IsSuccess` 仍為 `true`，且測試用的 `ILogger<OrderService>`（用可收集記錄的測試 logger 取代 `NullLogger`）收到一筆 `LogLevel.Error`、訊息包含該 `OrderId`、`Exception` 為 `SpyEmailNotificationService.ExceptionToThrow` 設定的例外（對應 Scenario「通知服務拋出例外，訂單確認仍回報成功」）
  - [ ] 6.4.5 驗證呼叫端自身 token 觸發的取消：測試建立一個**未取消**的 `CancellationTokenSource`（`cts`），把 `cts.Token` 傳給 `ConfirmOrderAsync`；設定 `SpyEmailNotificationService.OnNotifyAsync = async ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); }`（**不**使用 `ExceptionToThrow`，見 5.2.2 的例外順序定義）——`ChangeOrderStatusAsync` 交易內的所有 DB 操作在呼叫當下 `cts` 都還沒被取消，所以會正常完成、正常 commit；只有進入 post-commit 的通知步驟、Spy 真正被呼叫時，`cts` 才被取消並拋出綁定該 token 的 `OperationCanceledException`（外部審查抓到：若在呼叫 `ConfirmOrderAsync` 之前就先取消 token 再傳入，`ChangeOrderStatusAsync` 內部的 DB 操作會提早因為取消而中止，測試根本不會走到 post-commit 通知這一段，測到的會是「交易流程本身被取消」，不是本 Scenario 要驗證的「post-commit 通知階段的呼叫端取消」）→ `ConfirmOrderAsync` 回傳結果 `IsSuccess` 仍為 `true`，且測試用的 `ILogger<OrderService>` MUST NOT 收到任何 `LogLevel.Error` 的紀錄（對應 spec.md Scenario「呼叫端取消請求不視為通知失敗」，驗證 3.4.1 的 `when` 分支確實不記錄為 Error）
  - [ ] 6.4.6 `SpyEmailNotificationService.ExceptionToThrow` 設定為「用另一個、與 `ConfirmOrderAsync` 呼叫傳入的 token 無關的獨立 `CancellationTokenSource`」觸發的 `OperationCanceledException`（`ConfirmOrderAsync` 本身傳入 `CancellationToken.None` 或未取消的 token）→ `ConfirmOrderAsync` 回傳結果 `IsSuccess` 仍為 `true`，但測試用的 `ILogger<OrderService>` **SHALL** 收到一筆 `LogLevel.Error` 紀錄（對應 spec.md Scenario「非呼叫端觸發的取消例外仍視為通知失敗」；區別於 6.4.5，驗證 3.4.1 的 `when` 條件真的有在篩選，不是無條件放行所有 `OperationCanceledException`）
  - [ ] 6.4.7 買家主動取消 Pending 訂單 → `SpyEmailNotificationService.Calls` 為空（對應 Scenario「買家主動取消訂單不觸發通知」）
  - [ ] 6.4.8 背景清理程序取消逾時訂單 → 透過 `ExpiredOrderCleanupService.CleanupOnceAsync`（而非直接呼叫 `OrderService.CancelExpiredOrderAsync`）觸發，確認實際的背景服務 DI wiring 會解析到同一個帶有 Spy 的 `OrderService`，`SpyEmailNotificationService.Calls` 為空（對應 Scenario「背景清理程序取消逾時訂單不觸發通知」；直接呼叫 `CancelExpiredOrderAsync` 只驗證了方法本身的行為，無法驗證背景服務實際 wiring）
  - [ ] 6.4.9 訂單確認成功時，設定 `SpyEmailNotificationService.OnNotifyAsync`，在被呼叫的當下用 `PostgresFixture.CreateDbContext()`（比照 `OrderServiceConcurrencyTests` 既有用法，回傳一個全新連線的 `ApplicationDbContext`，不是共用測試主體的 `dbContext` 實例）重新查詢該筆 `Order`，斷言查到的 `Status` 為 `Paid`（對應 Scenario「通知呼叫發生在交易提交之後」；PostgreSQL 預設 Read Committed 隔離等級下，若通知呼叫被誤搬回交易內，這個獨立連線會查到交易前的 `Pending` 狀態，測試會確定性地失敗——見 design.md 決策 3）
- [ ] 6.5 確認既有 `ConfirmOrder_ByBuyerOnOwnPendingOrder_Returns204AndSellsSeat`（`ProjectC.WebApi.Tests`）在 DI 註冊完成後仍然通過——這是既有測試，不需新增，但作為 4.1 的 DI 註冊回歸驗證（遺漏會直接讓這個測試失敗）

## 7. 驗證與收尾

- [ ] 7.1 容器內執行完整後端測試套件（`docker compose exec api dotnet test`）確認全數通過（含既有 `OrderServiceConcurrencyTests`/`RedeemTicketConcurrencyTests` 等使用 `OrderServiceTestFactory` 的測試不受影響）
- [ ] 7.2 真實 API 手動驗證：建立活動＋票種，買家下單並確認付款，容器內查看 `api` 服務的 log 輸出，確認出現包含**遮蔽後**收件信箱／活動名稱／訂單 Id／票券張數的結構化通知 log，且完整 Email 不會出現在 log 輸出中
