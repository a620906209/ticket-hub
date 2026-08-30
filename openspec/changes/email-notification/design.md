## Context

`OrderService.ConfirmOrderAsync` → `ChangeOrderStatusAsync`（共用交易骨架，含 Confirm/Cancel）→ `ConfirmOrderHandler.Handle` 這條路徑已經完整實作：付款（`IPaymentGateway`）、座位確售、電子票券產出（`Ticket` entity，在 `ConfirmOrderHandler.Handle` 內以 `_ticketRepository.Add(...)` 建立，隨交易一併提交）。買家的 Email 存在 `Member.Email`（透過 `IApplicationDbContext.Members` 查詢，`Member` 沒有獨立的 Domain Repository，既有 `GetMyProfileHandler`/`GetAdminEventsHandler` 皆用這個方式查會員資料）——`IApplicationDbContext` 直接暴露 `DbSet<Member>` 是既有的架構妥協（`IApplicationDbContext.cs`），不是本次新增的問題；本次沿用既有慣例透過它查會員資料，**不**為了這次通知功能新增 `MemberRepository`，因為既有會員查詢路徑（`GetMyProfileHandler`/`GetAdminEventsHandler`）已經確立了這條邊界，本次沒有理由單獨改變它。

`IPaymentGateway`/`MockPaymentGateway`（`ProjectC.Domain.Payments`/`ProjectC.Infrastructure.Payments`）是本專案既有、唯一一個「用 Mock 展示外部服務介面抽象化」的前例，`MockPaymentGatewayOptions.AlwaysSucceed` 提供切換成功/失敗的設定。本次 `IEmailNotificationService`/`MockEmailNotificationService` 比照同一套模式。

`ILogger<T>` 目前只在 `ProjectC.WebApi.BackgroundServices`（`ExpiredOrderCleanupService`）使用過，Application 層完全沒有引入過。`ProjectC.Application.csproj` 目前只明確宣告 `Microsoft.EntityFrameworkCore`、`FluentValidation` 兩個 `PackageReference`，`Microsoft.Extensions.Logging.Abstractions` 目前僅透過 EF Core 的 transitive dependency 間接可用——**外部審查抓到**：不應該讓 Application 層對 `ILogger<T>` 的直接依賴，靠著「EF Core 恰好帶進來」這個間接關係存在，這在套件升級後可能失效、也讓 csproj 看不出真正的依賴意圖。本次會在 `ProjectC.Application.csproj` 明確新增這個 `PackageReference`（並在 `Directory.Packages.props` 新增對應 `PackageVersion`，版本對齊目前專案使用的 .NET 10 / `Microsoft.Extensions.*` 系列），這不是 `ProjectReference`，也不會讓 Application 依賴 ASP.NET Core。

## Goals / Non-Goals

**Goals:**
- 訂單確認付款成功、票券產出後，透過 Email 通知買家
- 通知服務本身不需要真實 SMTP server；`IEmailNotificationService` 介面直接以 Email 為抽象邊界（見決策 1，不假裝管道無關），`MockEmailNotificationService` 是唯一實作
- 通知失敗 MUST NOT 影響訂單確認本身回報給買家的結果（買家已付款、票已出，這是既成事實）

**Non-Goals:**
- 真實寄信（SMTP 串接、寄信重試佇列、寄信失敗告警）——留待未來若要串接真實 ESP（SendGrid/AWS SES 等）再開提案
- 取消訂單通知——project-scope 範圍只講「票券產出後通知買家」
- 通知歷史記錄查詢（買家或 Admin 查看「這封通知寄了沒」）——本次沒有任何通知相關的持久化資料
- 通知內容客製化／多語系模板——Mock 只記錄結構化 log，不產生真正的信件內容
- 跨請求的通知冪等性／防止重複通知（例如同一張訂單被重複觸發確認流程時是否會通知兩次）——`ChangeOrderStatusAsync` 的訂單狀態機保證同一張訂單只會成功 Confirm 一次（第二次呼叫會因狀態已非 Pending 而失敗），因此單次成功的 Confirm 呼叫只會觸發一次通知；但跨請求層級的去重（例如 HTTP retry 在到達伺服器前就重複發送、且兩次都命中同一個 Pending 狀態的競態視窗）不在本次範圍內

## Decisions

**1. 介面命名為 `IEmailNotificationService`（不是管道無關的 `INotificationService`），方法簽章：`NotifyTicketsIssuedAsync(string toEmail, string eventTitle, Guid orderId, int ticketCount, CancellationToken)`，回傳 `Task`（失敗用例外表達，不是 `Result` 型別）**
**外部審查抓到**：先前草稿宣稱 `INotificationService` 是「不綁定特定通知管道」的通用抽象，但方法簽章直接包含 `toEmail`（字串型別、Email 格式的收件位址），這個宣稱與實際簽章矛盾——它其實就是 Email 抽象，不是管道無關的通知抽象。改成誠實命名：`IEmailNotificationService`，承認這是 Email 專屬介面。理由：以目前只有 Email、且不做真實寄信的範圍來看，為「未來可能有簡訊/App 推播」這種假設性需求先做管道無關設計是過度設計（CLAUDE.md Rule 2）——真正要加其他管道時，屆時再依實際需求決定是否要抽出共用抽象（例如收件對象改成 `Member`/`MemberId` 而非管道專屬的 `toEmail`），現在無法預先猜對正確的抽象邊界。
這個方法故意不回傳 `Result`/`Result<T>`——`IPaymentGateway.ChargeAsync` 回傳 `PaymentResult` 是因為呼叫端**必須**依成功/失敗分流業務邏輯（付款失敗訂單就不能確認）；但通知服務的成敗不影響任何後續業務決策，呼叫端只需要「盡力嘗試，失敗就記錄下來」，這跟一個真實的 Email SDK（例如 `SmtpClient.SendMailAsync`）在失敗時丟例外的行為一致，也讓「best-effort、失敗即吞」這個語意單純靠呼叫端的 `try/catch` 表達，不需要額外的回傳型別分支。
**替代方案 A**：讓介面回傳 `bool`/`Result` 表示成功與否——放棄，這樣呼叫端還是要自己決定「回傳 false 時要不要拋例外」，並沒有比直接用例外更簡單，反而多一種呼叫端可能忽略回傳值、誤以為通知一定確定成功的風險。
**替代方案 B**：改用管道無關的 `NotifyTicketsIssuedAsync(TicketIssuedNotification notification, CancellationToken)`，`TicketIssuedNotification` 內含收件者與通知資料——放棄，這只是把矛盾往下推一層：`TicketIssuedNotification` 內部的收件者欄位最終還是要決定型別（Email？`MemberId`？兩者都要？），本次唯一實作只用得到 Email，先做這一層間接不會讓設計更誠實，只是把「其實只有 Email」這個事實藏得更深。

**2. 通知呼叫的位置：`OrderService.ConfirmOrderAsync`，在 `ChangeOrderStatusAsync` 交易提交成功之後，用獨立的 `try/catch` 包住，記錄失敗但不重新拋出**
不放進 `ConfirmOrderHandler.Handle`（交易內的純邏輯），理由：
- `ConfirmOrderHandler.Handle` 目前是同步交易內的邏輯（付款除外，付款已是刻意接受的技術債，見該檔案註解），通知是純粹的交易後副作用，跟座位鎖定完全無關，混進交易範圍內只會不必要地延長持有鎖的時間
- `ChangeOrderStatusAsync` 是 Confirm／Cancel 共用的骨架，把通知邏輯放進共用路徑會需要額外參數判斷「這次是不是 Confirm」，比直接放在 `ConfirmOrderAsync`（Confirm 專屬的公開方法）多繞一層
`ConfirmOrderAsync` 在交易提交成功後，用 `orderId` 重新查詢 `Order`（`IOrderRepository.GetByIdAsync`，此時已是 `Paid` 狀態且 `Items` 完整）、`Event`（`IEventRepository.GetByIdAsync(order.EventId)` 取得 `Title`）、買家 `Member`（`IApplicationDbContext.Members`，取得 `Email`），組出通知內容；`ticketCount` = `order.Items.Sum(i => i.Quantity)`（跟 `ConfirmOrderHandler` 內金額計算「座位項目 Quantity 固定 1、計數項目可能代表多張」的既有語意一致）。

這三次重新查詢使用的是呼叫 `ChangeOrderStatusAsync` 交易時的同一個 scoped `DbContext`（EF Core identity map 的行為）：`_orderRepository.GetByIdAsync` 實際上會回傳交易內同一個已追蹤的 `Order` instance，不是一個全新的資料庫往返結果；這裡明確記錄這個行為，而不是只依賴「理論上一定必然存在」——重新查詢的真正目的不是「確保拿到全新資料」，而是維持跟 Cancel 路徑一致的呼叫形狀、且不需要改動 `ChangeOrderStatusAsync` 的回傳簽章（見下方替代方案）。

**外部審查抓到**：先前草稿用 `order!`/`@event!`/`buyer!` 三個 null-forgiving operator 假設資料必然存在，理由是「FK 完整性保證」——但 FK 只保證資料庫層級的參照完整性，不保證查詢一定成功、也不保證 `Member.Email` 不是空字串／whitespace（例如未來若有資料修復流程或既有 legacy 資料留下不完整的 Email）。查不到時 `!` 會讓失敗原因表現為一個訊息毫無意義的 `NullReferenceException`，跟「通知服務本身失敗」在 log 上難以區分，不利於事後追查。改為：把「組裝通知內容＋驗證資料完整」抽成一個獨立、可被單元測試覆蓋的純函式 `TicketIssuedNotificationContentFactory.Create(...)`（`ProjectC.Application.Orders`），資料缺失時丟出訊息明確的 `InvalidOperationException`，而不是讓 `!` 產生的 `NullReferenceException` 含糊帶過：

```csharp
public sealed record TicketIssuedNotificationContent(string ToEmail, string EventTitle, Guid OrderId, int TicketCount);

public static class TicketIssuedNotificationContentFactory
{
    public static TicketIssuedNotificationContent Create(Guid orderId, Order? order, Event? @event, Member? buyer)
    {
        if (order is null)
            throw new InvalidOperationException($"Order '{orderId}' was not found when preparing ticket-issued notification.");
        if (@event is null)
            throw new InvalidOperationException($"Event '{order.EventId}' was not found when preparing ticket-issued notification for order '{orderId}'.");
        if (buyer is null || string.IsNullOrWhiteSpace(buyer.Email))
            throw new InvalidOperationException($"Buyer email is missing when preparing ticket-issued notification for order '{orderId}'.");

        return new TicketIssuedNotificationContent(buyer.Email, @event.Title, orderId, order.Items.Sum(i => i.Quantity));
    }
}
```

這個函式是 `public static`（不依賴 DbContext、不需要 `InternalsVisibleTo`——本專案目前沒有這個慣例，見 tasks.md 6.3 的單元測試可以直接用 `Member.Register(...)`/`Event` 的既有公開建構方式在記憶體中組出 `null`/合法資料兩種情況，不需要碰資料庫），讓「資料缺失」這個失敗模式有明確、可單元測試的訊息與行為，同時仍然落在 `ConfirmOrderAsync` 的同一個 `catch (Exception exception)` 分支（`InvalidOperationException` 也是 `Exception`），行為上跟「通知服務本身失敗」一致——不需要在 `catch` 內額外分支處理。

```csharp
public async Task<Result> ConfirmOrderAsync(Guid orderId, Guid requestingBuyerId, CancellationToken cancellationToken)
{
    var result = await ChangeOrderStatusAsync(orderId, requestingBuyerId, _confirmOrderHandler.Handle, cancellationToken);
    if (!result.IsSuccess)
        return result;

    try
    {
        var order = await _orderRepository.GetByIdAsync(orderId, cancellationToken);
        var @event = order is null ? null : await _eventRepository.GetByIdAsync(order.EventId, cancellationToken);
        var buyer = order is null ? null : await _dbContext.Members.FirstOrDefaultAsync(m => m.Id == order.BuyerId, cancellationToken);
        var content = TicketIssuedNotificationContentFactory.Create(orderId, order, @event, buyer);

        await _emailNotificationService.NotifyTicketsIssuedAsync(
            content.ToEmail, content.EventTitle, content.OrderId, content.TicketCount, cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // 只有「這次呼叫傳入的 cancellationToken 本身被觸發」才視為呼叫端主動取消（例如連線中斷），
        // 不記錄為 Error——比照既有 ExpiredOrderCleanupService 對 OperationCanceledException 的既有
        // 處理慣例。任何其他來源的 OperationCanceledException（例如 Email provider 自己的 timeout、
        // provider 內部用了另一個 token）不滿足這個 when 條件，會繼續往下落入 catch (Exception)，
        // 視為真正的通知失敗記錄下來（外部審查抓到：先前版本無條件捕捉所有 OperationCanceledException，
        // 會把這些真正的失敗誤判為「呼叫端取消」而漏記）。訂單確認結果此時已經確定為成功，直接放行，
        // 不重新拋出。
    }
    catch (Exception exception)
    {
        // 這個 catch 同時涵蓋「重新查詢 Order/Event/Member 失敗或缺漏」（含上面 Factory 丟出的
        // InvalidOperationException）與「呼叫通知服務本身失敗」兩種情況（見決策 3 對這個合併邊界的
        // 說明），log 訊息刻意不區分兩者——多加一層判斷失敗發生在查詢或寄送哪個階段，對「best-effort、
        // 記錄後即放行」這個語意沒有額外價值，例外本身（含 Factory 丟出的明確訊息）加上 `{OrderId}`
        // 已足以讓人之後手動追查。
        _logger.LogError(exception, "Failed to prepare or send ticket-issued notification for order {OrderId}.", orderId);
    }

    return result;
}
```
**替代方案**：改造 `ChangeOrderStatusAsync` 讓它把 `Order` 一併回傳，省掉重新查詢——放棄，`ChangeOrderStatusAsync` 是 Confirm/Cancel 共用的骨架，Cancel 不需要這個回傳值，為了 Confirm 一個呼叫端改共用方法的簽章不值得；重新查詢一次 `Order`（含 `Items`）的成本遠低於改動共用交易骨架的風險與複雜度。

**3. `try/catch` 吞掉例外但記錄，不是「靜默失敗」——CLAUDE.md「禁止吞掉例外」規則的合理例外，且是刻意的 post-commit best-effort boundary**
CLAUDE.md 禁止的是空 `catch` 或「僅 log 後不處理而導致靜默失敗」；這裡的處理方式是「log 後刻意不處理（不重試、不進佇列），因為這一層合理判斷『不處理』就是正確的業務行為」——訂單確認本身已經成功，沒有任何『處理』動作是這裡能做、且不影響已經回報給買家的結果的（重試或補償屬於未來若要做真實寄信才需要考慮的範圍，見 Non-Goals）。結構化 log（`{OrderId}`）確保問題可被追查，不是真正意義上的「靜默」。這個 catch 邊界明確只涵蓋「交易已提交之後」的程式碼（決策 2 的 `try` 區塊），交易提交之前的任何失敗（包含 `ChangeOrderStatusAsync` 內部）仍然正常往外拋出或回傳 `Result.Failure`，不受這個決策影響。

**測試可驗證性**：這個決策要求通知呼叫 MUST 在交易提交之後才發生，但只驗證「`SpyEmailNotificationService` 被呼叫時參數正確」無法偵測「有人把呼叫搬回交易內」這種回歸（呼叫時序上看起來一樣，測試仍會通過）。因此新增一項測試（tasks.md 6.4.9）：`SpyEmailNotificationService` 被呼叫時，用**另一個獨立的 `DbContext`／連線**重新查詢該筆 `Order`，斷言查到的 `Status` 為 `Paid`——PostgreSQL 預設 Read Committed 隔離等級下，若通知呼叫真的發生在交易提交前，這個獨立連線只會看到交易前的舊狀態（`Pending`），測試會確定性地失敗，而不是依賴呼叫時序的巧合。

`OperationCanceledException` 分支同樣需要獨立測試（tasks.md 6.4.5），區別於一般例外的 6.4.4：`SpyEmailNotificationService.ExceptionToThrow` 設為「由這次呼叫本身的 `cancellationToken` 觸發」的 `OperationCanceledException` 時，斷言測試 logger **沒有**收到 `LogLevel.Error` 紀錄——只驗證「`ConfirmOrderAsync` 仍回報成功」不足以證明這個分支真的區分了兩種例外（兩種情況下 `IsSuccess` 皆為 `true`，唯一可觀察的差異是有沒有記錄 Error log）。

新增的 `when (cancellationToken.IsCancellationRequested)` 條件本身也需要獨立測試（tasks.md 6.4.6），不能只靠 6.4.5：6.4.5 讓 `SpyEmailNotificationService` 拋出的 `OperationCanceledException` 剛好對應「呼叫端真的取消了這次請求」；6.4.6 則要驗證**不**滿足 `cancellationToken.IsCancellationRequested` 的 `OperationCanceledException`（例如 Spy 用一個完全不相關、獨立建立的 `CancellationTokenSource` 拋出的 `OperationCanceledException`，測試呼叫 `ConfirmOrderAsync` 時傳入的是另一個、未被取消的 token）仍然會落入 `catch (Exception)`、記錄為 Error——否則 `when` 條件形同虛設，任何 `OperationCanceledException` 都會被誤判為呼叫端取消而漏記真正的失敗。

新增的 `TicketIssuedNotificationContentFactory.Create(...)` 也需要獨立的純單元測試（tasks.md 6.3，不需要 Testcontainers）：`order`/`@event` 為 `null`、`buyer` 為 `null`、`buyer.Email` 為 `null`/空字串/純 whitespace，各自驗證丟出 `InvalidOperationException` 且訊息包含對應的識別資訊（`orderId`）；合法輸入驗證回傳的 `TicketIssuedNotificationContent` 內容正確。這填補了原本只能靠「理論上必然存在、FK 保證」帶過的資料完整性防呆，讓這個防呆邏輯本身有明確的測試覆蓋，而不需要在整合測試裡刻意繞過 FK 製造出真正缺資料的 `Order`/`Event`/`Member`（那種測試的維護成本與真實性都不划算，見下方替代方案的類似考量）。

**4. `MockEmailNotificationServiceOptions.AlwaysSucceed`（`bool`，預設 `true`），比照 `MockPaymentGatewayOptions` 命名與預設值慣例**
`AlwaysSucceed = false` 時 `MockEmailNotificationService` 拋出例外（模擬寄信失敗），用來讓測試能確定性地驗證決策 2、3 的「通知失敗不影響訂單確認結果」；`Program.cs` 的 DI 註冊比照 `MockPaymentGatewayOptions` 的既有寫法（`Configure<T>` + 解包成一般 class 注入），不需要 `ValidateOnStart`（有安全預設值，比照 `MockPaymentGatewayOptions`/`OrderCleanupOptions`）。

**5. `MockEmailNotificationService` 用結構化 `ILogger` 記錄，不寫入資料庫、不建立通知歷史；收件信箱 MUST 遮蔽後才寫入 log**
記錄內容：收件信箱（遮蔽格式）、活動名稱、訂單 Id、票券張數，皆用具名 placeholder（`{ToEmail}`/`{EventTitle}`/`{OrderId}`/`{TicketCount}`），比照既有 `ExpiredOrderCleanupService` 的結構化 log 慣例。
**外部審查抓到**：先前草稿主張「Email 不是密碼/Token，不算機敏資訊，不需要遮蔽」——這個判斷與 CLAUDE.md 錯誤處理與例外規範明確衝突：「Log 規範：...且**不得記錄敏感資訊**（密碼、token、**個資**需遮蔽）」，`Member.Email` 是個人資料，不因為它同時也是登入識別碼就豁免遮蔽義務（既有 JWT claims 內含 Email 是簽發給使用者自己瀏覽器的憑證，跟寫進伺服器端 log 檔案的曝險層級不同，不能類比）。
改為：`MockEmailNotificationService` 傳給 logger 的 `{ToEmail}` 使用遮蔽格式（例如 `a***@example.com`：保留網域與收件人第一個字元），而不是完整 Email；`IEmailNotificationService` 介面本身與呼叫端（`OrderService`）仍然傳遞、驗證完整 Email——遮蔽只發生在 `MockEmailNotificationService` 要寫入 log 的那一步，不影響介面契約或測試對「完整 Email 有沒有正確傳入介面」的驗證（見 tasks.md 6.1.1）。

**外部審查抓到**：遮蔽格式只示範了「正常 Email」（`local@domain`，local part 至少 2 字元）這一種輸入，沒有定義邊界輸入的行為；若遮蔽函式用天真的字串切割（例如直接取 `local[0]` 加後續字元），遇到 `a@example.com`（local part 只有 1 字元）、不含 `@` 的不合法字串、`null`/空字串時，可能自己因為 substring/index 操作拋出例外——這會讓「記錄通知失敗」這個 best-effort 動作本身變成新的失敗來源，本末倒置。遮蔽函式（`ProjectC.Infrastructure.Notifications` 內的一個小型 helper，例如 `EmailMasker.Mask(string email)`）的契約明確定義為：
- 合法 Email（`local@domain`，`local` 長度 ≥ 1）：保留 `local` 的第一個字元 + `***` + `@domain`（`local` 只有 1 字元時，`a@example.com` → `a***@example.com`，跟多字元情況行為一致，不需要特殊分支）
- `email` 為 `null`／空字串／純 whitespace／不含 `@`（不合法格式）：一律回傳固定字串 `"[redacted]"`，不嘗試對這些輸入做字串切割
- 遮蔽函式本身 MUST NOT 對任何輸入拋出例外（`Mask` 方法內部不需要、也不應該有會拋例外的路徑）
- 大小寫／前後 whitespace 不特別處理（不影響遮蔽格式，也不是本次要驗證的商業邏輯）

## Risks / Trade-offs

- **[風險] 通知呼叫失敗被吞掉例外後，除了 log 沒有任何其他方式讓人發現「這個買家沒收到通知」** → Mitigation：這是刻意的範圍取捨（見 Non-Goals），本次沒有通知歷史查詢功能；log 是唯一的可觀測性來源，屬於 Mock 展示 DIP 用途的合理簡化，真實寄信串接時才需要補上重試/告警機制（例如 Transactional Outbox + 背景 dispatcher，見下方最後一項風險）
- **[風險] 決策 2 在交易提交後又發出三次讀取查詢（`Order`/`Event`/`Member`），對單筆訂單確認流程增加額外延遲** → Mitigation：三次查詢皆為單筆主鍵查詢（無 N+1），且發生在交易外（不持有任何鎖），對高併發搶購情境（座位鎖定/扣庫存）沒有影響；若未來效能量測顯示這是瓶頸，可考慮把通知改為背景佇列非同步處理，屬於超出本次範圍的優化
- **[風險] Application 層首次引入 `ILogger<T>`，可能被誤解為「以後 Application 層都可以自由塞入橫切關注點」** → Mitigation：這是刻意記錄在本文件的單一決策（決策 2、3），不是全面性的架構調整；`ILogger` 只用於這一處「best-effort 副作用失敗記錄」的情境，不代表 Application 層從此可以任意記錄業務邏輯的一般性 log
- **[風險] `OrderService` 建構子依賴數量持續增加**（新增 `IEmailNotificationService`/`IApplicationDbContext`/`ILogger<OrderService>` 後達 15 個）→ Mitigation：`OrderService` 本身職責就是訂單流程協調器（PlaceOrder/Confirm/Cancel/背景取消），本次通知是 Confirm 的 post-commit side effect，繼續由它協調是合理的；但這代表它已接近需要拆分的邊界，本次是**最後一次**允許以「多塞幾個依賴」的方式擴充 `OrderService`——下一個會再往 `OrderService` 建構子加依賴的 change（例如通知重試、通知歷史、背景佇列 dispatcher），MUST NOT 繼續增加建構子參數，而是要把通知（或該次新增的關注點）拆成獨立的 application use case 或 outbox dispatcher。本次不做這個重構（CLAUDE.md Rule 2，不為假設性需求先做抽象），但把這條界線明確寫在這裡，避免未來每次都用「這次只多幾個依賴」的理由持續累積複雜度
- **[長期可靠性取捨]** 目前「commit 後直接呼叫、失敗即吞」的做法，在通知服務本身不穩定時會直接遺失通知，且沒有補償機制。這是刻意的短期範圍控制（見 Non-Goals 的真實寄信），不是完整的通知架構；未來若要做到可靠通知，方向是 Transactional Outbox（訂單確認交易內連帶寫入一筆 outbox 紀錄）+ 背景 dispatcher（讀取 outbox、呼叫真實通知服務、支援 retry/dead-letter/監控），而不是在 request 內原地重試
