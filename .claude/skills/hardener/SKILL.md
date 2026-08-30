---
name: hardener
description: 依本專案 CLAUDE.md 慣例，為 C# async 方法補強防禦性檢查（參數驗證、Entity 存在性、狀態機驗證、並發處理、日誌、CancellationToken 傳遞）
keywords: [defensive-programming, validation, exception-handling, logging, concurrency, result-pattern]
category: code-review
---

# Hardener Skill - 防禦性編程（比照 CLAUDE.md）

## 目的

在 Coder 階段完成後，為方法加入：
- ✅ 參數驗證（入口守衛，依所在層用對應慣例，不是一律丟例外）
- ✅ Entity 存在性檢查（依所在層回 `Result.Failure(Error.NotFound(...))` 或 `null`/空集合）
- ✅ 狀態機驗證（回 `Result.Failure(Error.Conflict/Validation(...))`）
- ✅ `CancellationToken` 正確接受並向下傳遞（不發明新的 timeout）
- ✅ 並發處理（依實際需要鎖定的 Entity 設計，不是套用固定模板、也不是樂觀重試迴圈）
- ✅ 日誌點（結構化、具名 placeholder、個資／敏感資訊遮蔽、等級對應風險）
- ✅ 例外邊界（先判斷是交易必要步驟還是 best-effort 副作用，再決定要不要重新拋出）

**核心原則（CLAUDE.md 明確規定，本 Skill 的所有檢查項目都必須服從）**：
> 「例外拋出時機：僅在『無法在當層合理處理』時才拋出；可預期的業務失敗優先用 Result 型別 / 明確回傳值，而非以例外控制流程」

但這條原則**只適用於 Application 層**（Handler、`OrderService` 這類協調器）。套用本 Skill 前，先確認自己在哪一層，責任分界如下：

| 層 | 可預期的失敗（Entity 查無、參數不合法等） | 誰負責 |
| --- | --- | --- |
| **Application**（Handler / `OrderService`） | `Result`/`Result<T>` + `Error.Xxx(...)` | 呼叫端依 `IsSuccess` 判斷 |
| **Repository**（`ProjectC.Infrastructure.Persistence.Repositories`） | 回 `null`（單筆）或空集合（多筆），**不是** `Result<T>` | 由 Application 層查詢結果決定要不要回 `Result.Failure` |
| **Repository — 違反程式契約的參數**（例如對空清單呼叫 `GetForUpdateAsync`） | 可以拋 `ArgumentException`——這是呼叫端寫錯，不是業務情境 | 讓它往外拋，不用 Result 包裝 |
| **Domain Entity**（例如 `Order.Confirm()`） | 用既有 Domain exception 表達「不變量被打破」（例如 `OrderNotPendingException`） | 維持既有慣例，不要改成 Result |
| **Infrastructure 技術性失敗**（DB 連線中斷、外部服務炸掉） | 讓例外往外拋，交給呼叫端的例外邊界與全域 `IExceptionHandler` | 見第 6️⃣ 節 |

**絕對不要**把 Repository 介面改成 `Task<Result<Order>> GetByIdAsync(...)`——這個專案的 Repository 一律回傳可為 `null` 的 Entity 或集合（例如 `IOrderRepository.GetByIdAsync` 回 `Order?`），`Result` 只在 Application 層出現。套用本 Skill 若發現要改動 Repository 介面簽章才能滿足檢查清單，代表理解錯了分層，先停下來。

## 適用範圍

檢查清單第 1️⃣–8️⃣ 節不是每一節都適用每一層，套用前先確認自己在哪一層：

**Application 層**（Handler / `OrderService` 這類協調器）：第 1️⃣–8️⃣ 節全部適用。

**Repository 層**（`ProjectC.Infrastructure.Persistence.Repositories`）：只套用以下項目，**不要**加入 `Result`、權限判斷、或 Domain 狀態機邏輯：
- 回傳契約（查無資料回 `null`/空集合，不是 `Result<T>`；見上面的分層表）
- `CancellationToken` 正確傳遞（第 4️⃣ 節）
- 查詢效能與 N+1
- 交易／鎖定邊界是否清楚（`GetForUpdateAsync` 這類方法要不要求呼叫端已經在交易內，第 5️⃣ 節）
- 技術性例外不要靜默吞掉（不是第 6️⃣ 節整套「情境 A/B」判斷——Repository 通常沒有 best-effort 副作用，技術性例外一律往外拋）

**Domain Entity**（例如 `Order.Confirm()`）：只確認既有不變量與例外慣例（`OrderNotPendingException` 這類）是否維持一致，不套用 Application 層的 `Result` 規則、也不套用第 3️⃣/6️⃣/7️⃣ 節。

**不套用本 Skill**：
- Private / internal 方法（酌情——通常已經被呼叫端的 Result 檢查涵蓋）
- 簡單的 getter / setter
- 純計算邏輯（無 I/O）

## 套用前必答（CLAUDE.md 安全強制規則）

若這個方法涉及以下任一項，套用本 Skill 前**必須在 PR／commit 說明或工作紀錄裡實際寫下答案**，不能只是心裡想過、也不能自己假設「應該沒問題」：

```text
- 輸入驗證位置：（Controller/Handler 邊界用了哪個 FluentValidation Validator？還是目前完全沒有？）
- 權限檢查位置：（這個操作需要什麼權限？在哪一層執行？是否已經存在，不需要本 Skill 重複做？）
- 查詢是否有 N+1：（`Include`/`AsSplitQuery` 是否已正確使用？）
- 是否需要悲觀鎖：（是/否，理由）
- 若需要，鎖定哪個 Entity、為什麼鎖它就足以涵蓋這個操作：（見 5️⃣ 節）
```

答不出來代表還沒讀懂現有程式碼，先停下來，不要繼續套用檢查清單。

## 檢查清單

### 1️⃣ 參數驗證

| 驗證內容 | 建議位置 |
| --- | --- |
| Request 欄位格式、數值範圍、集合不可空 | FluentValidation（比照既有 `PlaceOrderRequestValidator`） |
| 簡單前置條件（`Guid.Empty`、必要服務參數 `null`） | Application 層方法一開始的 guard clause |
| 跨 Entity 的業務規則 | Application |
| 單一 Entity 不變量 | Domain（既有例外慣例，不要改成 Result） |

Application 層方法（回傳 `Result`/`Result<T>`）：

```csharp
public async Task<Result<Guid>> PlaceOrderAsync(Guid buyerId, PlaceOrderRequest request, CancellationToken cancellationToken)
{
    var validation = await _validator.ValidateAsync(request, cancellationToken);
    if (!validation.IsValid)
    {
        return Result<Guid>.Failure(Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
    }
    // ...
}
```

`request` 本身若可能為 `null`（例如方法不是只被 ASP.NET Core model binding 呼叫，還可能被其他程式碼直接呼叫），在呼叫 `_validator.ValidateAsync(request, ...)` 之前要先判斷，否則 Validator 內部可能自己先拋出 `NullReferenceException`，繞過了 Result 這條路徑：

```csharp
if (request is null)
{
    return Result<Guid>.Failure(Error.Validation("Request body is required."));
}
```

**Repository / private helper 例外**：完全不對外暴露、只在同一 assembly 內部被呼叫、違反前置條件代表「呼叫端本身寫錯」而不是業務情境時，才可以用 `ArgumentException`（這是程式錯誤，不是可預期的業務失敗；比照既有 `GetForUpdateAsync` 對空清單拋 `ArgumentException` 的慣例）。

### 2️⃣ Entity 存在性檢查

Application 層，單筆：

```csharp
var order = await _orderRepository.GetByIdAsync(orderId, cancellationToken);
if (order is null)
{
    return Result.Failure(Error.NotFound($"Order '{orderId}' was not found."));
}
```

Application 層，多筆——**必須先正規化成 distinct id 集合再比對**，否則輸入本身含重複 id（例如 `[A, A, B]`）時，資料庫只會回傳 2 筆卻要求 3 筆，會誤判為資料缺漏。比照既有 `OrderService.PlaceOrderAsync` 的實際寫法（該方法查詢前就先 `.Distinct()`）：

```csharp
var distinctSeatIds = seatIds.Distinct().ToList();
var eventSeats = await _eventSeatRepository.GetByIdsAsync(distinctSeatIds, cancellationToken);

var foundSeatIds = eventSeats.Select(seat => seat.Id).ToHashSet();
if (!distinctSeatIds.All(foundSeatIds.Contains))
{
    return Result<Guid>.Failure(Error.NotFound("One or more selected seats were not found."));
}
```

比對「實際找到的 ID 集合」而不是只比對數量，能明確驗證「缺少的到底是哪些 ID」，也不依賴「主鍵不重複所以數量一致就代表 ID 一致」這個隱含假設——這個假設在查詢邏輯改變（例如未來加了 join 或 filter）時可能不再成立。既有 `OrderService.PlaceOrderAsync` 目前是用 `Count` 比對，套用本 Skill 時可以順手升級成這個寫法，但不強制回頭改動未觸及的既有程式碼。

Repository 層：查無資料回 `null`（單筆）或空集合（多筆），不包裝成 `Result`——由呼叫端（Application 層）決定查無資料時要回什麼樣的 `Error`。

### 3️⃣ 狀態機驗證 → 回 Conflict/Validation，不丟例外（Application 層）

```csharp
if (order.BuyerId != requestingBuyerId)
{
    return Result.Failure(Error.Forbidden("You are not the buyer of this order."));
}

if (order.Status != OrderStatus.Pending)
{
    return Result.Failure(Error.Conflict($"Order '{order.Id}' is not in a Pending state."));
}
```

套用本 Skill 前，先讀 `ProjectC.Application.Common.Error`／`ErrorType` 目前實際定義的分類，沿用既有分類；找不到合適分類時，先跟既有同類情境（例如「查無資料」一律 `NotFound`、「權限不符」一律 `Forbidden`）比對，不確定就更新 spec／錯誤模型，不要自己猜一個新分類硬套。

### 4️⃣ CancellationToken：接受並向下傳遞，不發明新 timeout

CLAUDE.md：「公開的非同步方法一律接受並向下傳遞 `CancellationToken`」。**不要**自己包一層 `CancellationTokenSource(TimeSpan.FromSeconds(N))` 憑空發明 timeout——這個數字沒有任何依據，且會讓呼叫端傳入的取消語意跟這個內部 timeout 混在一起，變得難以區分「呼叫端主動取消」跟「內部逾時」（這正是 `email-notification` 這個 change 的決策 2/3 特別處理過的問題，見 `openspec/changes/archive/2026-08-31-email-notification/design.md`）。

```csharp
public async Task<Result<Order>> ConfirmOrderAsync(Guid orderId, Guid requestingBuyerId, CancellationToken cancellationToken)
{
    var order = await _orderRepository.GetByIdAsync(orderId, cancellationToken);
    // 所有 await 都用同一個呼叫端傳入的 cancellationToken，不建立新的 CancellationTokenSource
}
```

若這個方法呼叫的是外部服務、而該服務確實需要一個獨立的逾時策略，**timeout 數值與理由必須寫進對應 change 的 `design.md`**，不能在套用本 Skill 時隨手塞一個數字。

### 5️⃣ 並發處理——依實際需要鎖定的 Entity 設計，不是套固定模板

**先分清楚兩件事，這是這個檢查項目最容易被誤解的地方**：
- `IXxxRepository.GetForUpdateAsync(...)` 才是真正取得資料庫列鎖（`FOR UPDATE`）的地方
- `IOrderRepository.ReloadAsync(...)` **本身不取得任何鎖**，只是重新從資料庫讀一次 Entity 的目前值（EF Core `Entry(order).ReloadAsync()`）。兩者責任不同，`ReloadAsync` 不能取代悲觀鎖。

以既有 `OrderService.ChangeOrderStatusAsync` 為例（這是一個具體案例，不是通用模板）：

```csharp
// 真正取得列鎖的是這兩行——鎖的是 EventSeat／TicketType，不是 Order 本身
var eventSeats = await _eventSeatRepository.GetForUpdateAsync(eventSeatIds, cancellationToken);
var ticketTypes = await _ticketTypeRepository.GetForUpdateAsync(ticketTypeIds, cancellationToken);

// Order 本身從未被 GetForUpdateAsync 鎖定；這裡只是重新讀取它目前的值，
// 依賴的前提是：任何會變更這筆 Order 狀態的並發操作，也一定要先鎖定它引用的
// 同一批 EventSeat／TicketType（因為 Order.Items 指向它們）——藉由鎖定「共用的
// 相依 Entity」，間接序列化了對同一筆 Order 的並發操作，才需要在鎖定之後
// 重讀 Order，確認它有沒有已經被另一個交易改變狀態。
await _orderRepository.ReloadAsync(order, cancellationToken);
```

**套用到新方法時**：
1. 先問「這個方法真正需要序列化的是哪個 Entity？」，不要預設答案是「跟 `ChangeOrderStatusAsync` 一樣鎖 `EventSeat`/`TicketType`」
2. 如果目標 Entity 本身有 `GetForUpdateAsync`，直接鎖它；鎖定之後如果還讀了其他相關 Entity，那些讀取才叫「重讀」，本身不提供任何並發保護
3. 如果目標 Entity 沒有可以直接鎖的路徑、而是像 `Order` 一樣要靠鎖定它引用的其他 Entity 間接序列化，**必須能講清楚「為什麼鎖那些 Entity 就足以涵蓋這個操作」**，並把這個推理寫進對應 change 的 `design.md`——不能只是抄一段 `GetForUpdateAsync` + `ReloadAsync` 就宣稱已經解決並發問題
4. 如果沒有既有的鎖定機制可以比照、也想不出該鎖什麼，**不要自己發明樂觀重試迴圈**（`while (retryCount < maxRetries)` 這類）。先確認這個方法是不是真的需要處理並發（多數 Application 層查詢方法不需要），需要的話把設計決策寫進 `design.md`，而不是在套用本 Skill 時順手加一段重試邏輯

### 6️⃣ 例外邊界：先判斷是交易必要步驟還是 best-effort 副作用

**套用這一節之前，先回答一個問題**：這段程式碼失敗時，呼叫端應該回報失敗（交易必要步驟），還是這個副作用本身允許失敗而不影響主流程結果（best-effort，而且這個決策必須已經寫在對應 change 的 spec/design 裡，不是套用本 Skill 時自己決定）？

**情境 B（下面）只允許用在 post-commit、且 spec/design 已經明確定義為 best-effort 的副作用**（例如通知、分析事件、快取失效這類）。**資料庫交易本身、付款、授權/權限異動、庫存扣減、訂單狀態轉換等主流程操作，一律是情境 A，不管取消例外是不是呼叫端觸發的都要重新拋出**——這些操作的「失敗」不能被吞掉，否則會出現「API 回報成功但實際資料沒有真正完成」的不一致狀態。

**情境 A：交易必要步驟**——技術性例外真的無法在當層處理，記錄後往外拋，交給全域 Handler：

```csharp
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    throw; // 呼叫端主動取消，不視為錯誤，不記錄 Error；但仍然要重新拋出——
           // 交易必要步驟被取消，呼叫端必須知道這個操作沒有完成，不能假裝成功
}
catch (Exception exception)
{
    // 這裡是「真的無法在當層合理處理」的技術性例外（DB 連線中斷、外部服務炸掉），
    // 不是可預期的業務失敗——可預期的業務失敗在上面幾個步驟已經走 Result.Failure 提早回傳。
    _logger.LogError(exception, "Unexpected error in {MethodName} for {EntityId}.", nameof(SomeMethodAsync), entityId);
    throw; // 保留原始堆疊，不是 throw exception; 讓例外往外傳播。
}
```

**情境 B：spec/design 已明確定義為 post-commit best-effort 的副作用**（例如訂單確認 commit 之後才觸發的通知）——記錄後刻意不重新拋出，讓主流程結果不受影響。這不是「忘記加 throw」，是既有設計（見 `OrderService.ConfirmOrderAsync` 對通知服務失敗的處理，`openspec/changes/archive/2026-08-31-email-notification/design.md` 決策 2、3）：

```csharp
// 僅限 post-commit、且 spec 明確定義為 best-effort 的通知／分析事件／快取失效等副作用；
// 只有當這次呼叫本身接收到的 cancellationToken 被觸發，才視為「呼叫端主動取消、這個
// best-effort 副作用沒有機會完成」；若這次呼叫本身的 token 從未觸發、卻收到其他來源的
// OperationCanceledException（例如通知服務內部用了另一個 token），仍然要落入下面的
// catch (Exception) 分支，當成一般失敗記錄，不能無條件放行所有取消例外。
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    // 不記錄為 Error，不重新拋出
}
catch (Exception exception)
{
    // 這是刻意設計為 best-effort 的副作用失敗（見對應 change 的 design.md 決策），
    // 記錄後不重新拋出——訂單確認本身已經成功，不應該因為這個附加動作失敗而讓整個 API 回報錯誤。
    _logger.LogError(exception, "Failed to complete best-effort step for {EntityId}.", entityId);
}
```

**把情境 A 的模板套到情境 B 的程式碼上（或反過來）都是錯的**：已付款訂單的通知失敗如果被改成情境 A（重新拋出），會讓已經成功的付款 API 回報 500；反過來，若把資料庫寫入、付款、權限異動、庫存扣減、訂單狀態轉換這類主流程操作改成情境 B（吞掉不拋），會讓真正的失敗被靜默放行、造成資料不一致。**不確定屬於哪一種時，去讀對應 change 的 spec/design，不要自己猜。**

**不要**為每種失敗情境建立一個自訂例外類別（`UserNotFoundException`、`SeatNotFoundException`⋯）。能表達成失敗類型的都應該是 `Error.Type`（`ErrorType` enum），不是例外的型別階層。只有 Domain 層代表「不變量被打破、理論上不該發生」的防線（例如 `OrderNotPendingException : DomainException`）才適合用例外，而且這種例外**不應該被本層 catch 後轉成 Result**——它本來就是「不該發生」的最後防線，讓它往外拋出去，才能真正發現程式邏輯的漏洞。

**`GlobalExceptionHandler` 的實際涵蓋範圍，套用情境 A 前要知道**：
- `src/ProjectC.WebApi/ExceptionHandling/GlobalExceptionHandler.cs` **對例外型別沒有任何特殊判斷**——收到的任何 `Exception`（含 `OperationCanceledException`）一律 `LogError` 記錄、回傳通用 500 `ProblemDetails`。情境 A 重新拋出的取消例外，如果真的一路傳到這個 Handler，**會**被記錄成 Error、回報 500，不會因為「是呼叫端主動取消」就被自動放過。這通常不是問題（純粹的 HTTP client 斷線在 ASP.NET Core 框架層級通常會提早中止管線，回應根本沒機會寫出去），但如果情境 A 用的 `cancellationToken` 不是直接對應 `HttpContext.RequestAborted`（例如背景服務、佇列消費者、或測試/CLI 直接呼叫 Application 層方法），這個取消例外就會被當成一般錯誤處理，需要呼叫端自己決定要不要在更外層額外處理
- 這個 Handler 只透過 `app.UseExceptionHandler()` 掛在 **HTTP request pipeline** 上，**背景服務、Queue consumer、CLI 不會自動經過它**。這個專案的背景服務（`ExpiredOrderCleanupService`、`PurchaseQueueAdmissionService`）都是自己在 `ExecuteAsync`／逐項處理迴圈內寫 `try/catch` 記錄失敗，不依賴全域 Handler——套用本 Skill 到非 HTTP 執行路徑的方法時，比照這兩個既有背景服務的做法自建例外邊界，不要假設有全域 Handler 幫忙兜底

### 7️⃣ 日誌點分布：結構化、具名 placeholder、個資遮蔽、等級對應風險

```csharp
_logger.LogInformation("Placing order for buyer {BuyerId}, {SelectionCount} selection(s).", buyerId, request.Selections.Count);
// ...
_logger.LogWarning("Order {OrderId} confirmation rejected with error type {ErrorType}.", orderId, result.Error!.Type);
```

- 一律用具名 placeholder（`{OrderId}`），不要用字串插值 `$"..."` 組出訊息本文——結構化 log 的欄位要能被 log 系統個別查詢
- **預設只記錄 `Error.Type`，不記錄 `Error.Message`**——`Error.Message` 的內容可能包含 Email、訂單明細、使用者輸入等尚未確認可以公開記錄的資訊；只有確認過這個 `Error.Message` 的實際內容不含個資/敏感資訊時，才記錄它
- 含個資（Email、電話、地址等）的欄位 log 前必須遮蔽，比照 `ProjectC.Infrastructure.Notifications.EmailMasker` 的做法（保留可辨識的最小資訊、格式不合法或為空時回傳固定替代字串、遮蔽本身 MUST NOT 拋出例外）
- 密碼、Token、API Key 等一律不得出現在 log
- 日誌等級對應風險，不要每個分支都用同一個等級：

| 情境 | 建議等級 |
| --- | --- |
| 預期內的輸入驗證失敗（`Validation`） | 通常不需要記錄，或 `Debug`（外部輸入格式錯誤是正常流量，不是異常） |
| 查無資料（`NotFound`） | 視情境；高頻率查詢的 NotFound 不需要記錄 |
| 權限拒絕、狀態衝突（`Forbidden`/`Conflict`） | `Information` 或 `Warning`，依業務風險決定 |
| 非預期技術例外 | `Error` |

### 8️⃣ 測試要求

套用本節之前，先列出這次實際補強的防禦分支；每一個分支至少要有一項單元測試或整合測試覆蓋，缺哪個就明確標注「未覆蓋，原因：...」，不要預設全部都測過：

- 參數驗證失敗 → `Validation`，具體要包含：
  - `Guid.Empty`／必要參數為預設值的邊界案例
  - `request`（或其他複合參數）為 `null` 的案例（若方法簽章允許 `null` 進來）
  - FluentValidation 規則本身失敗的案例（欄位格式、範圍、集合不可空等）
  - **驗證失敗時 MUST NOT 查詢 DB、MUST NOT 開交易、MUST NOT 取得任何鎖**——這不只是回傳正確的 `ErrorType`，還要驗證「失敗發生在任何 I/O 之前」。比照既有 `OrderServiceTests.PlaceOrderAsync_WhenCountingSelectionsSpanDifferentEvents_ReturnsValidationErrorBeforeTakingAnyLock` 的手法，斷言 `FakeUnitOfWork.BeginTransactionCallCount` 為 0（或該方法適用的等價寫法）
- Entity 不存在 → `NotFound`（含多筆存在性檢查的重複 id 邊界情況，見 2️⃣）
- 權限不符 / 狀態不合法 → `Forbidden`/`Conflict`
- `CancellationToken` 被取消 → 正確傳遞、且不被誤記錄為 Error（若屬於情境 A/B 其中一種，兩種都要各自有測試，比照 email-notification tasks.md 6.4.5/6.4.6 的手法）
- 並發行為 → 驗證鎖定與重讀後的結果符合預期（不是只驗證「沒有拋例外」）
- 技術例外 → 依情境 A/B 驗證是重新拋出、還是記錄後放行且不影響主流程結果
- 日誌內容 → 沒有洩漏未遮蔽的個資/敏感資訊

## 何時跳過

（適用範圍見上面「適用範圍」一節，這裡只列額外的跳過條件）

- 方法已有防禦（檢查清單全部通過，尤其已經是正確的分層——Application 層是 `Result`/`Result<T>`、Repository 層是 `null`/空集合、Domain 是既有例外慣例）
- 方法尚未實現完整邏輯（先完成 Coder）

## 驗證步驟

應用此 Skill 後，容器內執行（比照 CLAUDE.md 強制規則，不在本機跑 dotnet）：

```bash
# 1. 編譯 + 靜態分析
docker compose exec api dotnet build

# 2. 執行測試
docker compose exec api dotnet test
```

若套用本 Skill 導致任何程式碼變更，完成後必須呼叫 `strict-reviewer` agent 審查（CLAUDE.md 明確規則：任何程式碼變更完成後必須呼叫）。若只是檢查過一遍、確認既有程式碼已經符合檢查清單、沒有任何修改，不需要重複呼叫。
