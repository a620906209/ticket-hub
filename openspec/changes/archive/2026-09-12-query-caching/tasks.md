## 1. 快取抽象與 Infrastructure 實作

- [x] 1.1 於 `ProjectC.Application/Common/Interfaces` 新增 `IQueryCache`（`GetAsync<T>(string key, CancellationToken cancellationToken)`／`SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken)`／`RemoveAsync(string key, CancellationToken cancellationToken)`），比照 `IDistributedLock` 的介面風格——包含其每個方法都接受並宣告 `CancellationToken cancellationToken`（見 `IDistributedLock.cs:8,10`），對應 CLAUDE.md「非同步程式設計」節「公開的非同步方法一律接受並向下傳遞 `CancellationToken`」的強制規則；所有呼叫端（2.1、3.1、4.1、4.2、5.1、5.2、5.3 等）皆將自己方法本身收到的 `cancellationToken` 向下傳遞，不建立新的 `CancellationToken.None`
- [x] 1.2 於 `ProjectC.Infrastructure` 新增 `RedisQueryCache`，以既有 `IConnectionMultiplexer` Singleton + `System.Text.Json` 實作三個方法，錯誤處理比照 `RedisDistributedLock`：`RedisConnectionException`/`RedisTimeoutException` 時記錄 `LogWarning`（訊息含快取 key），`GetAsync` 回傳「未命中」、`SetAsync`/`RemoveAsync` 靜默視為完成，不拋出例外；`GetAsync` 另外用第二個 catch 分支處理 `System.Text.Json.JsonException`（快取值存在但反序列化失敗——具體是「不合法 JSON」或「JSON 值與目標型別不相容」導致 `JsonException`；單純新增/缺少/多出欄位這類 DTO 演進預設不會觸發此例外，不要誤植成「DTO 欄位異動」都會走到這個分支），同樣記錄 `LogWarning`、回傳「未命中」，不拋出例外
- [x] 1.3 於 `Program.cs` 註冊 `IQueryCache` → `RedisQueryCache` 為 Singleton
- [x] 1.3a 新增 `QueryCacheOptions`（`ProjectC.Application/Common`），比照 `PurchaseQueueOptions.cs` 寫法：`EventListTtlSeconds`／`TicketTypesTtlSeconds` 兩個屬性皆加 `[Range(1, int.MaxValue)]`，不給 C# 層級預設值（缺漏時維持 `0`，會被 `[Range]` 擋下）；`Program.cs` 比照 `PurchaseQueueOptions` 的註冊寫法：`builder.Services.AddOptions<QueryCacheOptions>().Bind(builder.Configuration.GetSection(QueryCacheOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart()`，並解包成一般 class 註冊為 Singleton 供 `RedisQueryCache` 建構子注入（見 design.md 決策 6a）
- [x] 1.4 於 `appsettings.json` 新增 `QueryCache:EventListTtlSeconds`（預設 `30`）、`QueryCache:TicketTypesTtlSeconds`（預設 `10`）
- [x] 1.4a 整合測試 QC-TTL-CONFIG-001／002／003（透過 `WebApplicationFactory` 啟動完整應用程式，非單元測試，被測主體：應用程式啟動流程，比照既有 `PurchaseQueueOptions` fail-fast 驗證的既定測試手法；觸發：分別以下列設定值啟動 `WebApplicationFactory`）
  - `EventListTtlSeconds`／`TicketTypesTtlSeconds` 皆為正整數（如 `30`／`10`）：斷言應用程式正常啟動（`CreateClient()` 可成功發出請求）
  - 任一值設為 `0`：斷言應用程式啟動時拋出例外（`ValidateOnStart` 驗證失敗），不得正常啟動
  - 任一值設為負數（如 `-1`）：斷言應用程式啟動時拋出例外
  - 任一值設為無法解析為整數的字串（如 `"abc"`）：斷言應用程式啟動時拋出例外（`.Bind(...)` 本身失敗）
  - 任一值完全缺漏（設定檔中不提供該 key）：斷言應用程式啟動時拋出例外（缺漏時維持 C# 預設值 `0`，被 `[Range]` 擋下）
- [x] 1.5 單元測試（`RedisQueryCache`，被測主體：`RedisQueryCache` 本身，用假的/可控的 `IConnectionMultiplexer` 或指向測試用 Redis 容器）：
  - Redis 正常時，`SetAsync` 寫入後 `GetAsync` 能讀回相同物件（反序列化正確）
  - Redis 正常時，`RemoveAsync` 後 `GetAsync` 回傳未命中
  - Redis 正常時，`SetAsync` 寫入的 key 實際帶有指定的 TTL（可用 Redis 的 `TTL`/`PTTL` 指令斷言，或驗證呼叫 `StringSetAsync` 時傳入的 expiry 參數）
- [x] 1.6 單元測試（`RedisQueryCache`，Redis 故障情境，用會拋出例外的假連線）：`RedisConnectionException`（連線失敗）與 `RedisTimeoutException`（逾時）**兩種例外型別都要各自測過**——兩者在 `RedisQueryCache` 內部走同一個 `catch (Exception exception) when (exception is RedisConnectionException or RedisTimeoutException)` 分支（比照 `RedisDistributedLock.cs` 既有寫法），但仍須各自建構會拋出對應例外型別的假連線，不能只測其中一種就視為兩者都覆蓋：
  - `GetAsync` 分別遇到 `RedisConnectionException`／`RedisTimeoutException` 時，皆回傳未命中（不對外拋出例外），且記錄一筆 Warning log
  - `SetAsync` 分別遇到 `RedisConnectionException`／`RedisTimeoutException` 時，皆方法正常回傳（不拋出例外），且記錄一筆 Warning log
  - `RemoveAsync` 分別遇到 `RedisConnectionException`／`RedisTimeoutException` 時，皆方法正常回傳（不拋出例外），且記錄一筆 Warning log
- [x] 1.6a 單元測試（`RedisQueryCache`，被測主體：`RedisQueryCache` 本身；前置：Redis 連線正常，但該 key 存的內容會讓 `System.Text.Json` 真正拋出 `JsonException` 的兩種情況——(a) 不合法 JSON 語法（例如截斷或格式錯誤的字串），(b) 合法 JSON 但某欄位值與目標型別 `T` 不相容（例如 `T` 的某屬性是 `Guid`，快取裡卻是不成 GUID 格式的字串）；**不要**用「單純多/缺欄位」這種預設不會拋例外的情境當前置條件，那樣測不出 catch 分支真的被觸發；觸發：呼叫 `GetAsync<T>`）
  - 斷言回傳未命中（不對外拋出 `JsonException`），且記錄一筆 Warning log

## 2. 活動列表查詢快取整合

- [x] 2.1 修改 `GetEventsHandler.HandleAsync`：查詢前先呼叫 `IQueryCache.GetAsync<IReadOnlyList<EventDto>>("query-cache:events:list")`；命中則直接回傳該結果、不查 `IEventRepository`；未命中則照既有邏輯查資料庫，取得結果後呼叫 `SetAsync` 寫入快取（TTL 取 `QueryCache:EventListTtlSeconds`），再回傳
- [x] 2.2 單元測試 QC-EVT-001（被測主體：`GetEventsHandler`；前置：mock `IQueryCache.GetAsync` 回傳未命中；觸發：呼叫 `HandleAsync`）
  - 斷言 `IEventRepository.GetAllAsync` 被呼叫恰好一次
  - 斷言 `IQueryCache.SetAsync` 被呼叫恰好一次，且傳入的 key 為 `query-cache:events:list`、value 內容與資料庫查詢結果一致、ttl 等於設定的 `EventListTtlSeconds`
  - 斷言方法回傳值與資料庫查詢結果一致
- [x] 2.3 單元測試 QC-EVT-002（被測主體：`GetEventsHandler`；前置：mock `IQueryCache.GetAsync` 回傳一份預先準備好的快取內容；觸發：呼叫 `HandleAsync`）
  - 斷言 `IEventRepository.GetAllAsync` **未被呼叫**（Moq/NSubstitute 的 `Verify(..., Times.Never)`）
  - 斷言 `IQueryCache.SetAsync` **未被呼叫**
  - 斷言方法回傳值與快取內容完全一致
- [x] 2.4 單元測試（回應格式一致性；觸發：分別以「快取未命中」與「快取命中且內容為同一組資料」兩種前置條件呼叫 `HandleAsync`）
  - 斷言兩次回傳的 `EventDto` 列表在欄位值與順序上完全相同
- [x] 2.5 單元測試 QC-EVT-003（被測主體：`GetEventsHandler`；前置：mock `IQueryCache.GetAsync` 回傳未命中，`IEventRepository.GetAllAsync` 回傳空列表；觸發：呼叫 `HandleAsync`）
  - 斷言方法回傳空列表（不拋例外、不回傳 null）
  - 斷言 `IQueryCache.SetAsync` 仍被呼叫恰好一次，且傳入的 value 為空列表（而非略過寫入）
本節與第 3、8 節的 HTTP 層元件測試，「驗證 Repository 只被查詢一次」統一採用以下觀測手法：透過 `WebApplicationFactory.WithWebHostBuilder` 的 `ConfigureTestServices`，把 `IEventRepository`／`ITicketTypeRepository` 註冊成一個**裝飾器（decorator）**——建構子注入真正的 `EventRepository`／`TicketTypeRepository`，每個方法內部直接轉呼叫真正的實作（行為完全不變，是真的查資料庫），額外用一個執行緒安全的計數器記錄每個方法被呼叫的次數，測試斷言時讀取這個計數器。這跟第 7 節「MUST 使用真正註冊的 `IQueryCache`，不得以 mock/fake 取代」的規則不衝突——裝飾器不是假物件，底層行為完全委派給真正的實作，只是額外掛了一層可觀測的呼叫計數，資料庫查詢本身沒有被繞過或模擬。

- [x] 2.6 元件測試 QC-EVT-001／002（HTTP 層補充，被測主體：`GET /api/events` 透過 `WebApplicationFactory.CreateClient()` 真實發送請求，不直接呼叫 Handler——QC-EVT-001／002 的 Scenario WHEN 子句本身寫的是「呼叫 `GET /api/events`」而非「呼叫 `GetEventsHandler`」，2.2／2.3 只驗證了 Handler 內部邏輯，本任務補上端到端層級：真正的路由、序列化、狀態碼；前置：真實 Redis／真實資料庫，`IEventRepository` 註冊為上述計數裝飾器；觸發：依序呼叫兩次 `GET /api/events`）
  - 斷言第一次呼叫回應 `200`，Body 為合法 JSON，欄位與既有 `EventDto` 格式一致
  - 斷言第二次呼叫（快取命中）回應內容與第一次完全相同，資料庫只被查詢一次（第二次未再查詢）

## 3. 票種列表查詢快取整合

- [x] 3.1 修改 `GetTicketTypesHandler.HandleAsync`：查詢前（在確認活動存在之後或之前皆可，但快取讀取與活動存在性檢查的順序須避免對不存在的活動做快取查詢——先檢查活動存在，再讀快取）先呼叫 `IQueryCache.GetAsync<IReadOnlyList<TicketTypeDto>>($"query-cache:ticket-types:event:{eventId}")`；命中則直接回傳；未命中則照既有邏輯查 `ITicketTypeRepository`，取得結果後 `SetAsync` 寫入（TTL 取 `QueryCache:TicketTypesTtlSeconds`）
- [x] 3.2 單元測試 QC-TT-001（被測主體：`GetTicketTypesHandler`；前置：活動存在，mock `IQueryCache.GetAsync` 回傳未命中；觸發：呼叫 `HandleAsync(eventId)`）
  - 斷言 `ITicketTypeRepository.GetByEventIdAsync` 被呼叫恰好一次，且傳入的 `eventId` 正確
  - 斷言 `IQueryCache.SetAsync` 被呼叫恰好一次，key 為 `query-cache:ticket-types:event:{eventId}`（帶入實際 eventId）、value 與資料庫查詢結果一致、ttl 等於 `TicketTypesTtlSeconds`
- [x] 3.3 單元測試 QC-TT-002（被測主體：`GetTicketTypesHandler`；前置：活動存在，mock `IQueryCache.GetAsync` 回傳預先準備好的快取內容；觸發：呼叫 `HandleAsync(eventId)`）
  - 斷言 `ITicketTypeRepository.GetByEventIdAsync` **未被呼叫**
  - 斷言回傳值與快取內容一致
- [x] 3.4 單元測試 QC-TT-003（被測主體：`GetTicketTypesHandler`；前置：mock `IQueryCache.GetAsync` 依傳入的 key 回傳不同結果——活動 A 的 key 回傳已快取內容，活動 B 的 key 回傳未命中；觸發：先查活動 A、再查活動 B）
  - 斷言查活動 B 時 `ITicketTypeRepository.GetByEventIdAsync` 被以活動 B 的 `eventId` 呼叫恰好一次
  - 斷言查活動 B 的過程沒有讀寫到活動 A 的快取 key（驗證 `GetAsync`/`SetAsync` 呼叫參數中的 key 各自對應正確的 eventId）
- [x] 3.5 單元測試 QC-TT-004（被測主體：`GetTicketTypesHandler`；前置：`IEventRepository.GetByIdAsync` 回傳 null（活動不存在）；觸發：呼叫 `HandleAsync(eventId)`）
  - 斷言回傳 `Result.Failure` 且錯誤類型為 `NotFound`
  - 斷言 `IQueryCache.GetAsync` 與 `SetAsync` 皆**未被呼叫**（確認活動存在性檢查在快取查詢之前）
- [x] 3.6 單元測試 QC-TT-005（被測主體：`GetTicketTypesHandler`；前置：活動存在，mock `IQueryCache.GetAsync` 回傳未命中，`ITicketTypeRepository.GetByEventIdAsync` 回傳空列表；觸發：呼叫 `HandleAsync(eventId)`）
  - 斷言回傳空列表（不拋例外、不回傳 null）
  - 斷言 `IQueryCache.SetAsync` 仍被呼叫恰好一次，value 為空列表
- [x] 3.7 元件測試 QC-TT-001／002（HTTP 層補充，被測主體：`GET /api/events/{id}/ticket-types` 透過 `WebApplicationFactory.CreateClient()` 真實發送請求，理由同 2.6；前置：真實 Redis／真實資料庫，活動存在，`ITicketTypeRepository` 註冊為第 2 節開頭定義的計數裝飾器；觸發：依序呼叫兩次同一活動的 `GET /api/events/{id}/ticket-types`）
  - 斷言第一次呼叫回應 `200`，Body 為合法 JSON，欄位與既有 `TicketTypeDto` 格式一致
  - 斷言第二次呼叫（快取命中）回應內容與第一次完全相同，資料庫只被查詢一次

## 4. 活動列表快取失效整合

- [x] 4.1 修改 `CreateEventHandler`：在其資料庫交易 `CommitAsync` 成功之後、方法回傳前，呼叫 `IQueryCache.RemoveAsync("query-cache:events:list")`
- [x] 4.2 修改 `SetEventQueueModeHandler`：在其資料庫交易 `CommitAsync` 成功之後、方法回傳前，呼叫 `IQueryCache.RemoveAsync("query-cache:events:list")`
本節與第 5 節的整合測試皆須額外驗證「失效呼叫確實發生在交易提交之後、不是之前」——只驗證「最終快取被清除、下一次查詢看到新資料」不足以排除實作把 `RemoveAsync` 誤放在 `CommitAsync` 之前的情況（若真的放反，這種寫法的交易提交前有一段本可避免、卻被放大的並發讀到未提交資料的視窗）。驗證手法：讓共用的記憶體版 `IQueryCache` fake 的 `RemoveAsync` 帶一個回呼（callback），在回呼內用**另一個獨立的資料庫連線／`DbContext`** 直接查詢受影響的資料列，斷言查到的已經是**異動後的新值**——如果 `RemoveAsync` 真的被誤放在 `CommitAsync` 之前呼叫，這個回呼內查到的會是異動前的舊值，斷言會失敗。

- [x] 4.3 整合測試 QC-EVT-INV-001（被測主體：`CreateEventHandler` + `GetEventsHandler`，共用同一個簡易的記憶體版 `IQueryCache` fake——用真的會儲存/刪除資料的 `Dictionary` 實作，不是只記錄呼叫次數而不改變狀態的 mock；前置：先呼叫一次 `GetEventsHandler.HandleAsync`，讓 `query-cache:events:list` 真的被寫入舊的活動列表；觸發：呼叫 `CreateEventHandler.HandleAsync` 建立一場新活動，交易提交成功）
  - 斷言呼叫後 fake 內部的 `query-cache:events:list` entry 確實已被移除（不只斷言 `RemoveAsync` 被呼叫，而是實際查該 key 已不存在）
  - 斷言接著再呼叫一次 `GetEventsHandler.HandleAsync`：確實重新查詢資料庫（`IEventRepository.GetAllAsync` 再次被呼叫），且回傳的活動列表包含剛建立的新活動
  - 依上述回呼手法，斷言 `RemoveAsync` 被呼叫的當下，用獨立連線查資料庫已經能查到新建立的活動（證明 commit 確實先於 invalidation）
- [x] 4.4 整合測試 QC-EVT-INV-002（被測主體：`SetEventQueueModeHandler` + `GetEventsHandler`，共用同一個記憶體版 `IQueryCache` fake；前置：活動存在，先呼叫一次 `GetEventsHandler.HandleAsync` 讓快取寫入舊的 `IsQueueModeEnabled` 值；觸發：呼叫 `SetEventQueueModeHandler.HandleAsync` 開啟或關閉該活動的 `IsQueueModeEnabled`，交易提交成功）
  - 斷言呼叫後 fake 內部的 `query-cache:events:list` entry 確實已被移除
  - 斷言接著再呼叫一次 `GetEventsHandler.HandleAsync`：確實重新查詢資料庫，且回傳的 `EventDto.IsQueueModeEnabled` 反映本次異動後的新值
  - 依上述回呼手法，斷言 `RemoveAsync` 被呼叫的當下，用獨立連線查資料庫已經能查到更新後的 `IsQueueModeEnabled`
- [x] 4.5 單元測試（負向：驗證失敗時不觸發失效）：`CreateEventHandler` 因驗證失敗（缺少必要欄位）回傳 `Failure` 時，斷言 `IQueryCache.RemoveAsync` **未被呼叫**；`SetEventQueueModeHandler` 對不存在的活動 Id 呼叫、回傳 `NotFound` 時，斷言 `IQueryCache.RemoveAsync` **未被呼叫**

## 5. 票種列表快取失效整合

- [x] 5.1 修改 `CreateTicketTypeHandler`：在其資料庫交易 `CommitAsync` 成功之後、方法回傳前，呼叫 `IQueryCache.RemoveAsync($"query-cache:ticket-types:event:{eventId}")`（`eventId` 取自請求參數）
- [x] 5.2 修改 `OrderService` 建立訂單方法（`CreateOrderHandler.Handle` 成功後、`transaction.CommitAsync` 之後，程式碼行號約 307）：**只有當本次下單的 `quantitySelections`（純計數票種項目）非空時**，才呼叫 `IQueryCache.RemoveAsync($"query-cache:ticket-types:event:{eventId}")`（`eventId` 取自該次下單請求）——純座位制訂單（`quantitySelections` 為空、只有 `seatSelections`）不會呼叫 `TicketType.Reserve`、不變更 `AvailableQuantity`，不應觸發票種列表快取失效；這個判斷條件在 `PlaceOrderAsync` 呼叫 `CreateOrderHandler.Handle` 前後皆可取得（`quantitySelections` 本身就是傳入 `Handle` 的參數），比照 `ConfirmOrderAsync` 傳入 `invalidatesTicketTypeCache = false` 的同一個原則：不變更庫存的操作不觸發失效（見 design.md 決策 4 訂正）
- [x] 5.3 修改 `OrderService.ChangeOrderStatusAsync`：新增參數 `bool invalidatesTicketTypeCache`；當該參數為 `true` 且 `transaction.CommitAsync` 成功後，呼叫 `IQueryCache.RemoveAsync($"query-cache:ticket-types:event:{eventId}")`（`eventId` 取自 `order.EventId`）。呼叫端明確傳值：`CancelOrderAsync`（買家主動取消）與 `CancelExpiredOrderAsync`（背景逾時清理）傳入 `true`；`ConfirmOrderAsync`（確認付款，不變更 `AvailableQuantity`）傳入 `false`，不觸發失效（見 design.md 決策 4／付款不變更庫存的說明）
本節同樣須驗證「失效呼叫確實發生在交易提交之後」，手法與第 4 節相同（`RemoveAsync` 回呼內用獨立連線查資料庫確認已是新值）。

- [x] 5.4 整合測試 QC-TT-INV-001（被測主體：`CreateTicketTypeHandler` + `GetTicketTypesHandler`，共用同一個簡易的記憶體版 `IQueryCache` fake；前置：先呼叫一次 `GetTicketTypesHandler.HandleAsync(eventId)` 讓該活動的票種列表快取寫入舊內容（不含即將新增的票種）；觸發：呼叫 `CreateTicketTypeHandler.HandleAsync` 為該活動建立一個新票種，交易提交成功）
  - 斷言呼叫後 fake 內部對應該活動的 `query-cache:ticket-types:event:{eventId}` entry 確實已被移除
  - 斷言接著再呼叫一次 `GetTicketTypesHandler.HandleAsync(eventId)`：確實重新查詢資料庫，且回傳的票種列表包含剛建立的新票種
  - 依本節開頭的回呼手法，斷言 `RemoveAsync` 被呼叫的當下，用獨立連線查資料庫已經能查到新票種
- [x] 5.5 整合測試 QC-TT-INV-002（被測主體：`OrderService` 建立訂單方法 + `GetTicketTypesHandler`，共用同一個記憶體版 `IQueryCache` fake；前置：先呼叫一次 `GetTicketTypesHandler.HandleAsync(eventId)` 讓快取寫入扣減前的 `AvailableQuantity`；觸發：呼叫建立訂單方法，訂單內含至少一個純計數票種項目，`Reserve` 成功，交易提交成功）
  - 斷言呼叫後 fake 內部對應該活動的快取 entry 確實已被移除
  - 斷言接著再呼叫一次 `GetTicketTypesHandler.HandleAsync(eventId)`：確實重新查詢資料庫，且回傳的 `AvailableQuantity` 已反映扣減後的新數量
  - 依本節開頭的回呼手法，斷言 `RemoveAsync` 被呼叫的當下，用獨立連線查資料庫已經能查到扣減後的新數量
- [x] 5.5a 單元測試（負向：純座位制訂單不觸發票種列表快取失效）：被測主體 `OrderService` 建立訂單方法；前置：訂單只含座位項目（`seatSelections` 非空、`quantitySelections` 為空），不含任何純計數票種項目；觸發：呼叫建立訂單方法，交易提交成功
  - 斷言 `IQueryCache.RemoveAsync` **未被呼叫**（純座位制訂單不會呼叫 `TicketType.Reserve`，沒有東西需要失效）
- [x] 5.6a 整合測試 QC-TT-INV-003a（被測主體：`OrderService.CancelOrderAsync` + `GetTicketTypesHandler`，共用同一個簡易的記憶體版 `IQueryCache` fake——用真的會儲存/刪除資料的 `Dictionary` 實作，不是只記錄呼叫次數的 mock；前置：訂單為 `Pending` 且含純計數票種項目，先呼叫一次 `GetTicketTypesHandler.HandleAsync(eventId)` 讓該活動的快取寫入歸還前的 `AvailableQuantity`；觸發：買家呼叫取消（`CancelOrderAsync`），交易提交成功）
  - 斷言呼叫後 fake 內部對應該活動的 `query-cache:ticket-types:event:{eventId}` entry 確實已被移除
  - 斷言接著再呼叫一次 `GetTicketTypesHandler.HandleAsync(eventId)`：確實重新查詢資料庫，且回傳的 `AvailableQuantity` 已反映歸還後的新數量
  - 依本節開頭的回呼手法，斷言 `RemoveAsync` 被呼叫的當下，用獨立連線查資料庫已經能查到歸還後的新數量
- [x] 5.6b 整合測試 QC-TT-INV-003b（被測主體：`OrderService.CancelExpiredOrderAsync` + `GetTicketTypesHandler`，同樣的共用 fake 手法；前置：訂單已逾時（`HeldUntilUtc` 已過）且含純計數票種項目，先呼叫一次 `GetTicketTypesHandler.HandleAsync(eventId)` 讓快取寫入歸還前的數量；觸發：以 `requestingBuyerId = null` 呼叫清理，交易提交成功）
  - 斷言呼叫後對應快取 entry 確實已被移除，且再次呼叫 `GetTicketTypesHandler.HandleAsync(eventId)` 會重新查詢資料庫、回傳歸還後的新 `AvailableQuantity`
  - 依本節開頭的回呼手法，斷言 `RemoveAsync` 被呼叫的當下，用獨立連線查資料庫已經能查到歸還後的新數量
  - 明確驗證此路徑與 5.6a 是兩個不同的呼叫入口（`CancelOrderAsync` vs `CancelExpiredOrderAsync`），但都經過同一個 `ChangeOrderStatusAsync` 觸發失效——測試需分別以各自的公開方法呼叫，不可只測其中一個就視為兩者皆覆蓋
- [x] 5.7 整合測試 QC-TT-INV-004（被測主體：跨兩個活動的整合層測試，`OrderService` 建立訂單方法 + `GetTicketTypesHandler`，共用記憶體版 `IQueryCache` fake；前置：活動 A、活動 B 皆各自先呼叫一次 `GetTicketTypesHandler.HandleAsync` 讓兩者的票種列表快取都真的寫入；觸發：對活動 A 建立一筆包含純計數票種項目的訂單，觸發 `TicketType.Reserve` 扣減庫存，交易提交成功——**觸發條件對應 spec.md QC-TT-INV-004 的「活動 A 發生扣減庫存的異動」，不是建立新票種**）
  - 斷言 fake 內部活動 A 對應的快取 entry 已被移除
  - 斷言活動 B 對應的快取 entry **仍然存在、未被觸碰**（不只斷言 `RemoveAsync` 沒有以活動 B 的 key 被呼叫，而是接著呼叫一次 `GetTicketTypesHandler.HandleAsync`（活動 B 的 eventId），斷言直接命中快取、`ITicketTypeRepository.GetByEventIdAsync` 完全未被呼叫）
- [x] 5.8 單元測試（負向：`CreateOrderHandler.cs:77` 的記憶體內補償回滾不觸發失效）：構造一個會在第二個計數票種項目扣減時失敗、整筆訂單建立最終回傳 `Failure` 的情境
  - 斷言 `transaction.CommitAsync` 未被呼叫（或交易確實 rollback）
  - 斷言 `IQueryCache.RemoveAsync` **未被呼叫**
- [x] 5.9 單元測試（負向：確認付款不觸發票種列表快取失效）：被測主體 `OrderService.ConfirmOrderAsync`；前置：訂單為 `Pending` 且含純計數票種項目；觸發：呼叫 `ConfirmOrderAsync`，交易提交成功
  - 斷言訂單狀態成功轉為 `Paid`
  - 斷言 `IQueryCache.RemoveAsync` **未被呼叫**（確認付款不應觸發任何票種列表快取失效，因為 `AvailableQuantity` 在這一步沒有變化）

## 6. TTL 安全網驗證

- [x] 6.1 單元測試 QC-TTL-001（已由 2.2／3.2 的 ttl 參數斷言涵蓋，同一組前置/觸發條件）（被測主體：`GetEventsHandler`／`GetTicketTypesHandler`；觸發：快取未命中時查資料庫）
  - 斷言 `IQueryCache.SetAsync` 呼叫時傳入的 `ttl` 參數等於設定檔中 `EventListTtlSeconds`／`TicketTypesTtlSeconds` 轉換後的 `TimeSpan`（兩個 Handler 各自驗證各自對應的設定值，不可混用）
- [x] 6.2a 整合測試 QC-TTL-002a（被測主體：`GetEventsHandler` + 真實/測試用 Redis 容器；前置：將 `EventListTtlSeconds` 設定覆寫為極短值（如 1 秒），先呼叫一次 `HandleAsync` 使其查詢資料庫並寫入快取；觸發：等待略超過 TTL 後，於等待期間變更資料庫內容（例如新增一場活動），再呼叫一次 `HandleAsync`）
  - 斷言第二次呼叫確實重新查詢了資料庫（可用查詢次數計數，或直接斷言回傳結果包含等待期間新增的活動——若快取未過期就不會看到新活動）
  - 斷言第二次呼叫後快取已被重新寫入（第三次呼叫命中快取、不再查資料庫）
- [x] 6.2b 整合測試 QC-TTL-002b（被測主體：`GetTicketTypesHandler` + 真實/測試用 Redis 容器；前置：將 `TicketTypesTtlSeconds` 設定覆寫為極短值（如 1 秒），先呼叫一次 `HandleAsync(eventId)` 使其查詢資料庫並寫入快取；觸發：等待略超過 TTL 後，於等待期間變更該活動的票種資料（例如新增一個票種），再呼叫一次 `HandleAsync(eventId)`）
  - 斷言第二次呼叫確實重新查詢了資料庫（回傳結果包含等待期間新增的票種）
  - 斷言第二次呼叫後快取已被重新寫入（第三次呼叫命中快取、不再查資料庫）
  - 斷言本測試使用的是 `TicketTypesTtlSeconds`，與 6.2a 的 `EventListTtlSeconds` 是兩個獨立設定值，互不影響
- [x] 6.2c 整合測試 QC-TTL-003（被測主體：`GetEventsHandler`／`GetTicketTypesHandler` 各自搭配真實/測試用 Redis 容器；前置：將對應 TTL 設定覆寫為一個明確、夠長的值（例如 5 秒），先呼叫一次 `HandleAsync` 使其查詢資料庫並寫入快取；觸發：在明確小於該 TTL 的時間內（例如 TTL 設 5 秒、等待 1 秒），於等待期間變更底層資料庫內容，再呼叫一次 `HandleAsync`）
  - 斷言第二次呼叫仍是快取命中：不重新查詢資料庫，回傳內容仍是**變更前**的舊內容（證明未到期前確實持續命中，不因時間流逝而提前失效）
- [x] 6.2d 整合測試（TTL 邊界，被測主體同 6.2c；**不要求精確命中「剛好等於 TTL」這個時間點**——對真實 Redis 而言,被動/主動淘汰的實際時序本身沒有可由測試精確控制的時鐘,無法保證測試執行到「剛好等於」的那一毫秒;改為驗證一個合理的容忍範圍：前置：TTL 設一個明確值（例如 2 秒）；觸發：分別在「明顯小於 TTL」（例如 0.5 秒，覆蓋 6.2c 已驗證的「未到期持續命中」）與「明顯大於 TTL、但仍在合理容忍範圍內」（例如 TTL + 0.5 秒，而非 6.2a/6.2b 用的「略超過」這種模糊說法，這裡明確定義為 TTL 加一個具體緩衝值）呼叫）
  - 斷言在容忍範圍內、超過 TTL 後的查詢，最終都會視為未命中並重新查詢資料庫——只要求「超過 TTL 後在合理緩衝時間內必定失效」，不要求命中/未命中的邊界精確落在 TTL 那一毫秒本身
- [x] 6.2e 整合測試 QC-TTL-004（被測主體：`GetTicketTypesHandler` + `CreateTicketTypeHandler` + 真實 Redis；**MUST 用可控制的同步機制決定性地構造這個時序，不得依賴時序巧合**——比照既有 `purchase-queue-leader-election` PQLE-003 的 `TaskCompletionSource` 手法，分明確階段）：
  1. 在 `ITicketTypeRepository.GetByEventIdAsync` 上掛一個可控制的同步點（例如 `TaskCompletionSource`），讓呼叫在「已讀到資料庫結果、尚未回傳給 Handler」的瞬間卡住
  2. 啟動一次 `GetTicketTypesHandler.HandleAsync(eventId)`（稱為呼叫 R），此時快取為空，R 查詢資料庫並卡在步驟 1 的同步點——R 讀到的是**尚未包含新票種**的舊資料
  3. R 仍卡住期間，呼叫 `CreateTicketTypeHandler.HandleAsync` 為同一活動建立一個新票種：交易提交成功、`RemoveAsync` 完整執行完畢（此時快取仍是空的，本來就沒有東西可清）
  4. 釋放 R 的同步點，讓 R 完成剩餘流程——R 把步驟 2 讀到的舊資料（不含新票種）寫入快取，帶著全新的 TTL
  5. 立即呼叫一次 `GetTicketTypesHandler.HandleAsync(eventId)`（稱為呼叫 S）：斷言 S 命中快取、拿到的是 R 寫入的舊資料（不含新票種）——證明這個競態確實會發生，這是本次改動接受的既知限制，不是要消除的 bug
  6. 等待該筆快取的 TTL（R 寫入時起算）到期後，再呼叫一次：斷言此時重新查詢資料庫、回傳的票種列表包含新票種——證明即使發生這個競態，陳舊的快取仍然會在有限時間內自然過期，不會無限期陳舊
- [x] 6.3 單元測試（`RedisQueryCache` 底層 TTL 到期行為，補充驗證，不能取代 6.2a/6.2b/6.2c/6.2d/6.2e 的 Handler 層完整流程）：用可控制系統時間或可控制 TTL 的測試 Redis，不依賴真實等待
  - 寫入一個 TTL 極短（如 1 秒）的快取項目，等待略超過該 TTL 後呼叫 `GetAsync`
  - 斷言回傳未命中，且該 key 在 Redis 中已不存在（用 `EXISTS` 指令確認），驗證的是 TTL 到期後的自動失效行為，而非程式邏輯主動清除

## 7. Fail-open 整合驗證

以下所有情境的「Redis 無法連線」設置方式統一比照既有 `purchase-queue-leader-election` 的 PQLE-007／PQLE-010 測試慣例（見 `openspec/changes/archive/2026-09-03-purchase-queue-leader-election/tasks.md` 5.3／5.5——**不是 5.1**：5.1 是用假的 `IDistributedLock`、不連真實 Redis 的分支行為單元測試，不符合本節要的「真實 Redis 故障」場景；5.3 才是該檔案自己的追溯表對應 PQLE-007 的服務層整合測試，5.5 對應 PQLE-010）：以 Testcontainers 啟動一個 Redis 容器後立即關閉容器或阻斷連線，取得一個真實但無法連線的 `host:port`，透過 `WebApplicationFactory` 的 `ConfigureAppConfiguration` 覆寫 `ConnectionStrings:Redis` 指向該 endpoint。**MUST 使用真正註冊的 `IQueryCache`（`RedisQueryCache`），不得以 mock／fake `IQueryCache` 取代其註冊、也不得直接讓假的 `IQueryCache` 對外拋出 `RedisConnectionException`**——`IQueryCache` 這個抽象本身的契約就是「呼叫端永遠拿到未命中或正常完成，不會收到 Redis 例外」（見 design.md 決策 1），`RedisConnectionException` 只應該在 `RedisQueryCache` 內部被捕捉；若測試讓假的 `IQueryCache` 直接把例外拋給 `GetEventsHandler`／`CreateEventHandler` 等呼叫端，等於測試了一個規格沒有要求、Handler 也未設計要處理的情境（Handler 可能因此意外收到未捕捉的例外、回應變成 `500`），無法證明 fail-open 這件事真的發生在正確的層級。這樣設置能讓 `GetEventsHandler`/`CreateEventHandler` 等呼叫端 → 真正的 `RedisQueryCache`（捕捉例外、記錄 Warning、回傳未命中/正常完成）→ 呼叫端照常運作的完整鏈路被真實觸發，而不是只驗證假物件的預先寫死行為。`RedisQueryCache` 本身「捕捉例外並轉換為未命中/正常完成」的單元層驗證已由 1.6 涵蓋，本節聚焦驗證這個轉換結果傳到 Handler／Controller 層之後,系統整體行為確實符合 fail-open。本節以下所有情境皆透過真實斷開連線觸發 `RedisConnectionException`，不另外用 Testcontainers／網路層工具模擬 `RedisTimeoutException`（逾時）——因為 `RedisQueryCache` 對兩種例外走的是同一個 `catch (... when exception is RedisConnectionException or RedisTimeoutException)` 分支，例外型別本身不影響後續 fail-open 處理邏輯，`RedisTimeoutException` 的等價覆蓋已由 1.6 在單元層驗證過；重新用整合測試模擬真實逾時（需要額外的網路層工具如 toxiproxy）對本節要驗證的「Handler／Controller 層是否正確吃到 fail-open 結果」沒有增量價值。

- [x] 7.1a 元件測試 QC-FAIL-001a（被測主體：`GetEventsHandler` 透過 `EventsController.GetEvents` 端到端；前置：如上述 Redis 無法連線設置，`IQueryCache` 為真正的 `RedisQueryCache`；觸發：呼叫 `GET /api/events`）
  - 斷言 HTTP 回應為 `200`，且內容與直接查詢資料庫的結果一致
  - 斷言記錄了至少一筆 Warning 等級的結構化 log（來源為 `RedisQueryCache` 捕捉 `RedisConnectionException` 後記錄的那一筆，非測試自行植入）
- [x] 7.1b 元件測試 QC-FAIL-001b（同上設置，被測主體改為 `GetTicketTypesHandler` 透過 `GET /api/events/{id}/ticket-types`）
  - 斷言 HTTP 回應為 `200`，且內容與直接查詢資料庫的結果一致
  - 斷言記錄了至少一筆 Warning 等級的結構化 log
- [x] 7.1c 元件測試 QC-FAIL-003a（被測主體：`GetEventsHandler` 透過 `EventsController.GetEvents` 端到端；前置：Redis 連線正常（與 7.1a/7.1b 的「無法連線」設置不同），但預先把 `query-cache:events:list` 這個 key 直接寫入一段無法反序列化為 `IReadOnlyList<EventDto>` 的損壞內容（例如不合法的 JSON 字串，透過真實 `IConnectionMultiplexer` 直接 `StringSetAsync` 寫入，不經過 `IQueryCache.SetAsync`）；觸發：呼叫 `GET /api/events`）
  - 斷言 HTTP 回應為 `200`，且內容與直接查詢資料庫的結果一致（`IEventRepository.GetAllAsync` 確實被呼叫，證明真的走了資料庫 fallback，不是碰巧命中什麼快取）
  - 斷言記錄了至少一筆 Warning 等級的結構化 log（來源為 `RedisQueryCache.GetAsync` 捕捉 `JsonException` 後記錄的那一筆）
- [x] 7.1d 元件測試 QC-FAIL-003b（同上設置，被測主體改為 `GetTicketTypesHandler` 透過 `GET /api/events/{id}/ticket-types`；損壞內容寫入該活動對應的 `query-cache:ticket-types:event:{eventId}` key）
  - 斷言 HTTP 回應為 `200`，且內容與直接查詢資料庫的結果一致（`ITicketTypeRepository.GetByEventIdAsync` 確實被呼叫）
  - 斷言記錄了至少一筆 Warning 等級的結構化 log
- [x] 7.2a 元件測試 QC-FAIL-002a（被測主體：`POST /api/admin/events`，透過 `WebApplicationFactory.CreateClient()` 真實發送請求，不直接呼叫 `CreateEventHandler`；前置：如上述 Redis 無法連線設置，`IQueryCache` 為真正的 `RedisQueryCache`；帶有效 Admin 角色 JWT（`AdminEventsController` 類別層級 `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`，見 `AdminEventsController.cs:12`）；觸發：以合法的建立活動請求呼叫該端點）
  - 斷言 HTTP 回應為 `201 Created`，Body 含新建立的活動 `id`
  - 斷言活動確實寫入資料庫（可用獨立查詢確認）
  - 斷言記錄了至少一筆 Warning 等級的結構化 log（來源為 `RedisQueryCache.RemoveAsync` 捕捉例外後記錄的那一筆），且沒有任何例外往外拋出中斷回應（HTTP 層沒有變成 `500`）
- [x] 7.2b 元件測試 QC-FAIL-002b（被測主體：`POST /api/admin/events/{eventId}/ticket-types`，透過 `WebApplicationFactory.CreateClient()`，不直接呼叫 `CreateTicketTypeHandler`；前置同 7.2a（Redis 無法連線、帶 Admin JWT）；觸發：以合法的建立票種請求呼叫該端點）
  - 斷言 HTTP 回應為 `201 Created`，Body 含新建立的票種 `id`
  - 斷言票種確實寫入資料庫；記錄 Warning log；沒有例外中斷回應
- [x] 7.2c 元件測試 QC-FAIL-002c（被測主體：`PATCH /api/admin/events/{id}/queue-mode`，透過 `WebApplicationFactory.CreateClient()`，不直接呼叫 `SetEventQueueModeHandler`；前置同 7.2a；觸發：以合法的 `{ "enabled": true }` Body 呼叫該端點）
  - 斷言 HTTP 回應為 `204 No Content`（比照既有 `PATCH /api/admin/tickets/{id}/redeem` 的回應慣例）
  - 斷言 `IsQueueModeEnabled` 確實在資料庫中更新；記錄 Warning log；沒有例外中斷回應
- [x] 7.2d 元件測試 QC-FAIL-002d（被測主體：訂單建立導致 `TicketType.Reserve`；同上設置）
  - 斷言訂單成功建立、`AvailableQuantity` 確實在資料庫中被扣減；記錄 Warning log
- [x] 7.2e 元件測試 QC-FAIL-002e（被測主體：買家主動取消訂單 `CancelOrderAsync` 導致 `TicketType.Release`；同上設置）
  - 斷言取消成功、`AvailableQuantity` 確實在資料庫中被歸還；記錄 Warning log
  - **註**：本情境（Redis 無法連線）下 `RemoveAsync` 一律失敗，無法用「回呼內查資料庫」的手法驗證呼叫順序（因為呼叫本身沒有真的寫入任何東西可供比對）；`RemoveAsync` 相對 `CommitAsync` 的呼叫順序是同一段程式碼、同一個呼叫位置，不因 Redis 當下是否可連線而改變，其正確性已由 5.6a 在 Redis 正常時的回呼驗證證明——7.2e 的職責只需要確認「這條程式碼路徑在 Redis 故障時仍會被執行到、且不中斷主流程」，不需要重複驗證順序本身
- [x] 7.2f 元件測試 QC-FAIL-002f（被測主體：背景逾時清理 `CancelExpiredOrderAsync`／`ExpiredOrderCleanupService` 導致 `TicketType.Release`；同上設置）
  - 斷言清理成功、`AvailableQuantity` 確實在資料庫中被歸還；記錄 Warning log；明確與 7.2e 分開驗證，因為觸發入口與呼叫上下文不同（背景服務 vs 買家 API 請求）
  - 呼叫順序的說明同 7.2e，正確性已由 5.6b 在 Redis 正常時的回呼驗證證明

## 8. 存取範圍與快取隔離驗證

- [x] 8.1 元件測試 QC-ACCESS-001：未帶任何身份驗證資訊呼叫 `GET /api/events`／`GET /api/events/{id}/ticket-types`，斷言回應為 `200`（不要求 `401`），確認本次改動未新增授權檢查
- [x] 8.2a 元件測試 QC-ACCESS-002（被測主體：`EventsController.GetEvents` 透過真實 HTTP pipeline，MUST 使用 `WebApplicationFactory.CreateClient()` 實際發送請求，不可直接呼叫 Handler——Handler 本身不接觸 HTTP Header，直接呼叫 Handler 無法證明不同呼叫者經過真實 API pipeline 後行為一致；前置：準備三種呼叫情境：完全未帶 `Authorization` Header（匿名）、帶有效買家角色 JWT、帶有效 Admin 角色 JWT；`IEventRepository` 註冊為第 2 節開頭定義的計數裝飾器；觸發：三種情境依序呼叫 `GET /api/events`）
  - 斷言三次呼叫的 HTTP 回應內容完全相同（欄位、順序皆一致）
  - 斷言只有第一次呼叫真正查詢了資料庫（例如以 spy repository 或計數斷言 `IEventRepository.GetAllAsync` 僅被呼叫一次），後續兩次呼叫皆命中快取——證明快取確實在不同身份的呼叫者之間共用同一份內容，而不是各自建立獨立的快取
- [x] 8.2b 元件測試 QC-ACCESS-002（被測主體：`EventsController.GetTicketTypes` 透過真實 HTTP pipeline，手法與前置同 8.2a（`ITicketTypeRepository` 註冊為計數裝飾器）；觸發：三種情境依序呼叫同一活動的 `GET /api/events/{id}/ticket-types`——**MUST 獨立驗證，不可因 8.2a 已通過就視為本端點也一併覆蓋**，兩者是不同的 Controller Action 與不同的快取 key）
  - 斷言三次呼叫的 HTTP 回應內容完全相同（欄位、順序皆一致）
  - 斷言只有第一次呼叫真正查詢了資料庫（`ITicketTypeRepository.GetByEventIdAsync` 僅被呼叫一次），後續兩次呼叫皆命中快取
- [x] 8.3 元件測試 QC-ACCESS-003（範圍縮小為只驗證 HTTP 路由層級的拒絕行為，不驗證「repository/cache 零互動」——那是 ASP.NET Core `{id:guid}` 路由約束的框架保證，不匹配路由樣板的請求連 Controller/Action 都不會解析，屬於框架本身的責任，不需要在應用層再用 spy/mock 額外證明一次；若要驗證此點需要用 `ConfigureTestServices` 替換 scoped 依賴為 spy，但這會跟 7.1a/7.1b 等其他元件測試「MUST 使用真正註冊的服務、不得替換」的既有規則衝突，兩者混用容易誤導，故不採用）：呼叫 `GET /api/events/{id}/ticket-types` 時 `id` 帶入非 GUID 格式字串（如 `"abc"`）
  - 斷言 HTTP 回應為 `404`

## 9. 整合驗證與文件同步

- [x] 9.1 於容器內執行完整測試套件（`docker compose exec api dotnet test`），確認全數通過
- [x] 9.2 手動驗證：透過真實 API 呼叫，依序驗證「首次查詢（未命中）→ 再次查詢（命中）→ 觸發異動 → 查詢看到最新資料 → 等待 TTL 到期 → 確認即使沒有異動也會重新查詢」的完整流程，針對活動列表與票種列表各跑一輪
- [x] 9.3 archive 階段同步更新 `docs/project-scope.md` 的 Could 項目狀態
