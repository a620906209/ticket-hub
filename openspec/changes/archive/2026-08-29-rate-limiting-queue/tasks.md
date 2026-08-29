## 1. Domain 層

- [x] 1.1 `Event` 新增 `IsQueueModeEnabled`（`private set`），新增 `EnableQueueMode()`／`DisableQueueMode()` 方法
- [x] 1.2 新增 `PurchaseQueueEntry` Entity（`Id`、`EventId`、`MemberId`、`Status`：`Waiting`/`Admitted`/`Completed`/`Expired`、`JoinedAtUtc`、`AdmittedAtUtc`、`AdmissionExpiresAtUtc`），狀態轉換邏輯封裝為方法（`Admit(now, expiresAt)`、`Complete()`、`Expire()`，對不合法的狀態轉換拋 `DomainException`），`private set`
- [x] 1.2a 新增 `PurchaseQueueJoinConflictException`（`: DomainException`，比照既有 `SeatAlreadyHeldException`／`OrderNotPendingException` 等具體子類別的命名與繼承慣例）——供 Infrastructure 在「加入排隊的併發衝突重試仍失敗」這個極端情況下拋出，Application 層捕捉並映射為 `Error.Conflict`（見 design.md 決策 3）
- [x] 1.3 `Domain` 新增 `IPurchaseQueueRepository` 介面：
  - 依 `EventId + MemberId` 查詢「目前紀錄」（`Status IN (Waiting, Admitted, Expired)` 範圍內 `JoinedAtUtc DESC, Id DESC` 取一筆，供查詢端點使用，見 design.md 決策 3「目前紀錄」選取規則）
  - 依 `EventId + MemberId` 取得「進行中」（`Status IN (Waiting, Admitted)`）紀錄的悲觀鎖查詢（供加入排隊流程與 `OrderService.PlaceOrderAsync` 重新確認排隊資格用，兩者用途不同但查詢條件相同）
  - 依 `EventId` 篩選 `Status IN (Waiting, Admitted)` 的悲觀鎖批次查詢，依 `JoinedAtUtc ASC, Id ASC` 排序（供背景推進服務用，不含 `Completed`／`Expired` 歷史資料）
  - **`Task<PurchaseQueueEntry> AddOrGetExistingAsync(PurchaseQueueEntry newEntry, CancellationToken)`**：嘗試新增一筆排隊紀錄，若撞到 `(EventId, MemberId)` partial unique index（同一會員已有進行中紀錄）則回傳該筆既有紀錄，否則回傳新增的紀錄；介面簽章純粹是 Domain 語意，不外露任何 EF Core／Npgsql／SQL 型別或字串。**內部 MUST 用 `INSERT ... ON CONFLICT ... DO NOTHING` 讓「撞到既有紀錄」成為不拋例外的正常結果，不得用「先 `Add()`＋`SaveChangesAsync()`、捕捉 `DbUpdateException`」的寫法**——PostgreSQL 交易一旦有陳述式失敗即進入 aborted 狀態，同交易內任何後續查詢都會被拒絕，`ChangeTracker.Clear()` 只清 EF Core 端記憶體狀態、不會讓交易恢復可用，這是本次審查發現、修正前設計未處理的問題（見 design.md 決策 3）
- [x] 1.4 `IEventRepository` 新增 `GetForUpdateAsync(Guid eventId, CancellationToken)`（比照 `ITicketTypeRepository`／`IEventSeatRepository` 既有 `GetForUpdateAsync` 命名慣例，回傳單筆或 `null`），供 `OrderService.PlaceOrderAsync` 在交易內重新鎖定並讀取 `IsQueueModeEnabled` 用（見 design.md 決策 4「Queue Mode 切換的線性化時點」）。**此方法本身 MUST 宣告為 `.AsNoTracking()`**（`Events.AsNoTracking().FromSqlInterpolated(...).SingleOrDefaultAsync(...)`，`FOR UPDATE` 鎖與 no-tracking 可自由組合，見 design.md 決策 4）——這是主要防線：`OrderService` 只讀取 `IsQueueModeEnabled`、從不修改 `Event`，no-tracking 查詢不做 identity resolution、每次都直接對資料庫下 SQL，因此不受 1.5 是否有做到影響，單獨即可保證讀到鎖定後的最新值
- [x] 1.5 **次要防護（belt-and-suspenders）：`EventRepository.GetByIdAsync` 也一併改為 `.AsNoTracking()`**（`src/ProjectC.Infrastructure/Persistence/Repositories/EventRepository.cs:15-16`，目前是 tracking query）：比照 `TicketTypeRepository.GetByIdAsync` 既有的相同修法與程式碼註解。已確認 `GetByIdAsync` 現有 4 個呼叫端（`GetEventSeatsHandler`、`GetTicketTypesHandler`、`CreateTicketTypeHandler`、`OrderService.cs:219`）皆為純讀取，不依賴 tracking 存回，改動安全（design.md 決策 4）

## 2. Infrastructure 層

- [x] 2.1 新增 `PurchaseQueueEntry` 的 EF Core `IEntityTypeConfiguration`：`(EventId, Status, JoinedAtUtc, Id)` 複合索引；`(EventId, MemberId)` partial unique index（`WHERE Status IN ('Waiting', 'Admitted')`，見 design.md 決策 3）；`Status` 欄位以字串轉換儲存（`HasConversion<string>()`），合法值為 `Waiting`／`Admitted`／`Completed`／`Expired`，不使用 EF Core 預設的 integer enum 儲存（避免 migration 的 partial index 條件式與實際欄位型別不一致）
- [x] 2.2 新增 EF Core migration：`Event.IsQueueModeEnabled`（`bool`，預設 `false`）、新表 `PurchaseQueueEntries` 含上述索引；**MUST 檢視 migration 產生的實際 SQL**，確認 `Status` 欄位型別為字串（非 integer）、partial unique index 的 `WHERE` 子句字面內容，供 2.3 的 `ON CONFLICT` conflict target 逐字對照（欄位名、引號、運算子、字面值需完全一致，PostgreSQL 才能精確辨識為同一個 index；規格層級無法靜態驗證這件事，只能在實作完成後對照）
- [x] 2.3 實作 `PurchaseQueueRepository`，含 `AddOrGetExistingAsync`（見 1.3、design.md 決策 3 完整步驟）：
  1. 以 `_dbContext.Database.ExecuteSqlInterpolated($"INSERT INTO \"PurchaseQueueEntries\" (...) VALUES (...) ON CONFLICT (\"EventId\", \"MemberId\") WHERE \"Status\" IN ('Waiting', 'Admitted') DO NOTHING")`（參數化插值，避免 SQL Injection）執行插入，`ON CONFLICT` 的 conflict target 須與 2.1 的 partial unique index 定義完全一致；此呼叫**完全繞過 EF Core 的 change tracking**，不呼叫 `DbSet.Add(...)`，`newEntry` 從頭到尾不進入 `ChangeTracker`
  2. `ExecuteSqlInterpolated` 回傳受影響列數為 1：直接回傳 `newEntry`
  3. 受影響列數為 0（撞到既有進行中紀錄，`DO NOTHING` 讓這不是例外）：以 no-tracking 查詢取得該會員在該活動目前「進行中」的既有紀錄並回傳
  4. 若步驟 3 查無紀錄（理論上極端罕見），MUST NOT 無限重試；重新執行一次步驟 1（僅重試一次），第二次仍查無則拋出 `PurchaseQueueJoinConflictException`（見 1.2a）
  - `JoinPurchaseQueueHandler`（Application）呼叫這個方法時不需要任何 EF Core／SQL 相關的 `try/catch`，只需在第 4 點極端情況捕捉 `PurchaseQueueJoinConflictException` 映射為 `Error.Conflict`
- [x] 2.3a（審查後新增，防禦性修正）`AddOrGetExistingAsync` 開頭 MUST 呼叫 `_dbContext.EnsureActiveTransaction(nameof(AddOrGetExistingAsync))`，比照 `GetForUpdateAsync`／`GetForAdmissionAsync` 的既定慣例 fail fast——這個方法依賴呼叫端與任何先前的 `Expire()` 等變更位於同一交易內（見 5.1a），沒有進行中的交易時仍會插入成功但失去原子性保證，須明確擋下而非靜默允許
- [x] 2.4 實作 `EventRepository.GetForUpdateAsync`（沿用既有 `GetForUpdateAsync` 的 `FromSqlInterpolated` + `FOR UPDATE` 寫法慣例，但**加上 `.AsNoTracking()`**——與 `TicketTypeRepository`／`EventSeatRepository` 的 `GetForUpdateAsync` 不同，那兩者的結果會被 `Reserve()`／`Hold()` 修改、需要 tracking 才能讓 `SaveChangesAsync` 存回；`Event` 在本次流程中不會被修改，見 tasks.md 1.4／design.md 決策 4）

## 3. Application 層 — 新增 QueueAdmissionRequired 錯誤型別

- [x] 3.1 `ErrorType` 新增 `QueueAdmissionRequired`；`Error` 新增對應的 `Error.QueueAdmissionRequired(string message)` 靜態方法
- [x] 3.2 `ResultExtensions.CreateProblemResult` 的 switch 新增 `ErrorType.QueueAdmissionRequired => StatusCodes.Status403Forbidden`（HTTP 狀態碼與既有 `Forbidden` 相同，但 `Title` 為獨立的 `"QueueAdmissionRequired"` 字串，見 design.md 決策 4）

## 4. Application 層 — 熱門搶購模式開關與公開活動列表欄位

- [x] 4.1 新增 `SetEventQueueModeHandler`（開啟/關閉指定活動的 `IsQueueModeEnabled`；活動不存在回傳 `NotFound`；成功回傳 `Result`（無 payload），供 Controller 呼叫 `result.ToActionResult()` 產生 `204 No Content`，比照既有 `PATCH /api/admin/tickets/{id}/redeem` 的回應慣例，見 design.md 決策 2）；`SetEventQueueModeRequest.Enabled` 宣告為 `bool?`（**不得用 `bool`**，見 design.md 決策 6，否則無法區分「完全缺漏」與「明確傳 false」）；Validator 用 `RuleFor(x => x.Enabled).NotNull()` 攔截缺漏，型別錯誤（如字串）由 `[ApiController]` model binding 自動回 400
- [x] 4.1a（審查後修正）`SetEventQueueModeHandler` MUST 在交易內以 `IEventRepository.GetForUpdateAsync(eventId)` 取得並鎖定活動，直接修改鎖定後取得的實體再呼叫 `Update()`，**不得**使用交易前的 no-tracking `GetByIdAsync` 讀取後才開交易——否則兩個 Admin 幾乎同時切換同一活動時會各自基於切換前的舊快照決定寫入值，形成 read-modify-write 遺失更新（見 design.md 決策 4 新增段落）
- [x] 4.2 `EventDto`（`src/ProjectC.Application/Events/GetEvents/EventDto.cs`）新增 `IsQueueModeEnabled` 欄位；`GetEventsHandler` 對應補上映射

## 5. Application 層 — 排隊加入（含自我修復逾時紀錄與併發衝突處理，見 design.md 決策 3）

- [x] 5.1 新增 `JoinPurchaseQueueHandler`，只要求 `[Authorize]`（不限角色，`Member`／`Admin` 皆可，比照既有 `POST /api/orders`），`MemberId` 一律取自 `User.GetMemberId()`（不接受請求輸入覆寫），只依賴 `IEventRepository`／`IPurchaseQueueRepository`／`IUnitOfWork`（不持有 `DbContext`，比照既有 `OrderService` 的既定分層），於單一交易內完成：
  1. 交易前快速失敗路徑：活動不存在回傳 `NotFound`；`IsQueueModeEnabled = false` 回傳 `Conflict`（此檢查未加鎖、不具權威性，見下一步）
  2. 開啟交易後，**MUST** 立即以 `IEventRepository.GetForUpdateAsync(eventId)` 重新鎖定並讀取活動（比照決策 4 對 `OrderService.PlaceOrderAsync` 的線性化作法），以鎖定後的 `IsQueueModeEnabled` 為唯一採信依據；不存在或已關閉則回傳對應錯誤，交易不寫入任何內容（見 tasks.md 5.1a、design.md 決策 3 第 3 點，此為審查後修正的第三個問題）
  3. 以 `IPurchaseQueueRepository` 的悲觀鎖查詢取得該會員在該活動「進行中」的紀錄
  4. 若查得且為 `Admitted` 且已逾時：呼叫 `Expire()`（僅變更追蹤中的 Entity，尚未寫入），視為查無進行中紀錄，繼續下一步
  5. 若查得且仍為 `Waiting` 或未逾時 `Admitted`：`CommitAsync()` 並回傳既有紀錄
  6. 若查無：建立新 `Waiting` 紀錄，呼叫 `IPurchaseQueueRepository.AddOrGetExistingAsync(newEntry, ct)`（見 2.3，內部處理併發衝突，Handler 不需 `try/catch` unique violation），再呼叫 `CommitAsync()`
- [x] 5.1a `PurchaseQueueRepository.AddOrGetExistingAsync`（見 2.3）**MUST** 在執行 `INSERT ... ON CONFLICT` 之前先呼叫一次 `_dbContext.SaveChangesAsync(ct)`，落地本交易內任何尚未寫入的 ChangeTracker 變更（例如 5.1 步驟 4 剛呼叫過的 `Expire()`）——這段 INSERT 是繞過 ChangeTracker 的 raw SQL，不會自動看到未落地的變更；若不先 flush，資料庫裡舊紀錄仍是 `Admitted`，partial unique index 會讓 `ON CONFLICT DO NOTHING` 誤判撞到「進行中」紀錄而略過插入，導致 PQ-JOIN-003 的新 `Waiting` 紀錄從未真正建立（審查後發現的 High 問題，先前版本設計誤以為 `CommitAsync` 的 `SaveChangesAsync` 會涵蓋這個順序，見 design.md 決策 3 第 5 步修正說明）
- [x] 5.2 `JoinPurchaseQueueHandler` 捕捉 `PurchaseQueueJoinConflictException`（見 1.2a，僅在 `AddOrGetExistingAsync` 內部重試仍失敗的極端情況拋出）映射為 `Error.Conflict(ex.Message)`，比照既有 `CreateOrderHandler` 捕捉 `DomainException` 子類別映射為 `Error` 的既定模式

## 6. Application 層 — 排隊查詢

- [x] 6.1 新增 `GetMyQueueStatusHandler`，只要求 `[Authorize]`（不限角色），只回傳呼叫者本人（`User.GetMemberId()`）的排隊紀錄：活動不存在回傳 `NotFound`；依「目前紀錄」查詢規則（見 1.3）取得代表紀錄，查無則回傳「尚未加入排隊」；`Waiting` 時計算前方等待人數（`JoinedAtUtc ASC, Id ASC` 排序，早於自己的 `Waiting` 筆數）；`Admitted` 狀態須於查詢當下依 `AdmissionExpiresAtUtc` 即時推導是否已逾時，不落地寫回（此端點純查詢，落地寫回交由 5.1 的自我修復流程與背景服務處理）
- [x] 6.2 回應 DTO 新增 `queueModeEnabled` 欄位，反映查詢當下該活動的 `Event.IsQueueModeEnabled`（見 design.md 決策 6／7、purchase-queue spec PQ-STATUS-008）——即使排隊紀錄本身仍為 `Waiting`／`Admitted`，`queueModeEnabled` 仍如實反映活動目前設定，不因活動已關閉熱門搶購模式而竄改或清理排隊紀錄本身

## 7. Application 層 — 排隊入場推進（背景服務）

- [x] 7.1 新增 `RateLimitingOptions`（`PermitLimit`、`Window`，DataAnnotations 標註須為正數，缺漏時套用預設值 `PermitLimit = 20`、`Window = 1 分鐘`）；註冊寫法為 `services.AddOptions<RateLimitingOptions>().Bind(section).ValidateDataAnnotations()`（**不**鏈 `.ValidateOnStart()`）——**不可**用 `services.Configure<RateLimitingOptions>(section)` 後直接串 `.ValidateDataAnnotations()`，因為 `Configure<T>` 回傳的是 `IServiceCollection` 不是 `OptionsBuilder<T>`，無法這樣鏈接（見 design.md 決策 1 的精確寫法說明）；解包成一般 class 的方式仍比照 `OrderCleanupOptions`（`AddSingleton(sp => sp.GetRequiredService<IOptions<RateLimitingOptions>>().Value)`）
- [x] 7.2 新增 `PurchaseQueueOptions`（`MaxConcurrentAdmittedBuyers`、`AdmissionTtl`、`PollingInterval`，三者皆以 DataAnnotations 標註須為正數，比照 `JwtOptions`／`TicketSigningOptions` 使用 `ValidateOnStart` fail-fast，見 design.md 決策 3）；`appsettings` 填入決策 3 已定案的起始值：`MaxConcurrentAdmittedBuyers = 50`、`AdmissionTtl = 5 分鐘`、`PollingInterval = 5 秒`（`ValidateOnStart` 只擋「填了但不合法」，這三個值仍須實際寫進設定檔，不會像 `RateLimitingOptions` 一樣缺漏時自動補預設值）
- [x] 7.3 新增 `PurchaseQueueAdmissionService`（`IHostedService`，比照 `ExpiredOrderCleanupService`）：每個 tick 先在交易外快速掃描出 `IsQueueModeEnabled = true` 的活動 Id 清單，再對每個活動各自開交易處理；**每個活動的交易內 MUST 先以 `IEventRepository.GetForUpdateAsync(eventId)` 重新鎖定並確認 `IsQueueModeEnabled` 仍為 `true`（審查後修正，見 design.md 決策 3／4）**，不符合則直接跳過（不放行任何入場），確認通過才取得該活動 `Status IN (Waiting, Admitted)` 範圍的悲觀鎖，計算有效名額（`COUNT(Status = Admitted AND AdmissionExpiresAtUtc > now)`），依 `JoinedAtUtc ASC, Id ASC` 推進 `Waiting` 至上限，並將已逾時的 `Admitted` 標記為 `Expired`
- [x] 7.4 `Program.cs` 於非 `Testing` 環境註冊 `PurchaseQueueAdmissionService`（比照 `ExpiredOrderCleanupService` 的條件註冊）

## 8. Application 層 — 建立訂單整合排隊檢查（`OrderService.PlaceOrderAsync`）

- [x] 8.1 `OrderService.PlaceOrderAsync` 在既有 `BeginTransactionAsync` 交易內、開始任何座位/票種鎖定之前，新增 `IEventRepository.GetForUpdateAsync(eventId)` 重新鎖定並讀取活動，**以此鎖定後讀到的 `IsQueueModeEnabled` 為唯一採信依據**（不得沿用交易前 `orderEvent` 的未鎖定值）；此步驟對每筆訂單皆執行，不論該活動是否曾被判斷為未開啟排隊（見 design.md 決策 4「Queue Mode 切換的線性化時點」）
- [x] 8.2 若鎖定後讀到 `IsQueueModeEnabled = true`：以 `IPurchaseQueueRepository.GetForUpdateAsync(eventId, memberId)` 取得該會員在該活動「進行中」（`Status IN (Waiting, Admitted)`，repository 只依狀態過濾、不比較 `AdmissionExpiresAtUtc`）的排隊紀錄；`OrderService` 自行以 `AdmissionExpiresAtUtc > now` 判斷是否合格，不符合（狀態非 `Admitted`，或已逾時）則回傳 `Error.QueueAdmissionRequired(...)` 失敗，不繼續鎖定座位/票種、不呼叫 `CreateOrderHandler.Handle`。**判斷為已逾時時，`OrderService` MUST NOT 呼叫該紀錄的 `Expire()`**——狀態落地寫入統一交由 `PurchaseQueueAdmissionService`（背景）與 `JoinPurchaseQueueHandler`（自我修復）負責，`OrderService` 只讀取判斷（見 design.md 決策 4）
- [x] 8.3 鎖定順序固定為 `Event → PurchaseQueueEntry → EventSeat → TicketType`，座位/票種鎖定與 `CreateOrderHandler.Handle` 維持現有流程不變
- [x] 8.4 建立訂單成功（`CreateOrderHandler.Handle` 回傳成功）後，在同一交易內呼叫該筆 `PurchaseQueueEntry.Complete()`，與 `_orderRepository.Add(...)` 一起在 `transaction.CommitAsync` 前完成（`CreateOrderHandler` 本身不變更，仍是純記憶體邏輯，不注入排隊相關依賴）

## 9. WebApi 層 — Rate Limiting Middleware

- [x] 9.1 `Program.cs` 註冊 `AddRateLimiter`，設定兩個獨立命名的 Fixed Window 限流 policy（例如 `place-order`／`confirm-order`），分區鍵皆為 `User.GetMemberId()`，套用 `RateLimitingOptions` 設定值，兩個 policy 各自累計、不共用計數（見 design.md 決策 1）
- [x] 9.2 自訂 `OnRejected` callback，輸出比照 `GlobalExceptionHandler` 的 `ProblemDetails` 格式（`Status = 429`、`Title`、`traceId`），並從 `RateLimitLease` 的 `RetryAfter` metadata 寫入 `Retry-After` 回應標頭
- [x] 9.3 `OrdersController` 的 `PlaceOrder` 套用 `place-order` policy、`ConfirmOrder` 套用 `confirm-order` policy（`[EnableRateLimiting("...")]`）

## 10. WebApi 層 — Controller 端點

- [x] 10.1 `AdminEventsController` 新增 `PATCH /api/admin/events/{id}/queue-mode`（`[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`），成功回傳 `204 No Content`，回應對應 `NotFound`（404）／驗證失敗（400）
- [x] 10.2 新增 `EventQueueController`（或於既有 `EventsController` 擴充）：`POST /api/events/{id}/queue/entries`、`GET /api/events/{id}/queue/entries/me`，皆只標註 `[Authorize]`（不限角色，見 design.md 決策 7），回應對應 `NotFound`（404）／`Conflict`（409）
- [x] 10.3 `Program.cs` 註冊本次新增的 Handler 為 Scoped（比照既有 Handler 註冊慣例）

## 11. 前端 — 買家排隊等待畫面

- [x] 11.1 `web/src/types/apiResponses.ts`／`api.generated.ts` 相關型別新增 `isQueueModeEnabled`（活動列表回應）、`queueModeEnabled`（排隊狀態查詢回應）；`web/src/api/` 新增排隊相關 API 呼叫（加入排隊、查詢狀態）
- [x] 11.2 活動詳情頁讀取活動列表資料中的 `isQueueModeEnabled` 判斷是否為熱門搶購模式，尚未加入排隊時顯示加入排隊操作
- [x] 11.3 新增排隊等待畫面元件（顯示前方等待人數、定期輪詢狀態，輪詢間隔採 5 秒，見 design.md 決策 3 已定案的起始值），`Waiting` 期間停用座位選擇、計數購買輸入與區域隨選快速下單操作入口；`Admitted` 後關閉並開放上述三種操作與送出訂單；**每次輪詢回應的 `queueModeEnabled` 為 `false` 時，停止輪詢、關閉排隊等待畫面，開放上述三種操作**（見 BW-TOGGLE-001，不需買家等到原本的 `Admitted`）
- [x] 11.4 下單 API 失敗時依錯誤類型分流：`status === 403 && ProblemDetails.title === "QueueAdmissionRequired"` → 導回排隊等待畫面重新查詢狀態；其他 403（例如非本人訂單等既有情境）沿用一般授權錯誤處理，不導向排隊畫面；`429` → 顯示「請稍後再試」、不清空已選內容；其餘失敗 → 沿用既有清空/刷新處理

## 12. 後端測試

- [x] 12.1 單元測試：`Event.EnableQueueMode()`／`DisableQueueMode()`、`PurchaseQueueEntry` 狀態轉換方法（`Admit`／`Complete`／`Expire` 的合法與非法轉換）
- [x] 12.2 整合測試：`api-rate-limiting` 能力（RL-001~RL-009，見第 15 節追溯表）
- [x] 12.3 整合測試：`purchase-queue` 能力 — Admin 開關（PQ-ADMIN-001~PQ-ADMIN-007）
- [x] 12.4 單元測試（命名沿用既有「整合測試」分類，實際為 `JoinPurchaseQueueHandler` 層級，搭配 Fake repository，不連真實 DB）：`purchase-queue` 能力 — 加入排隊（PQ-JOIN-001~PQ-JOIN-008）；**注意**：`FakePurchaseQueueRepository` 是單執行緒、共用參考的記憶體實作，`existing.Expire()` 對它而言是立即生效的同步變更，無法重現真實 `PurchaseQueueRepository`（raw SQL `INSERT ... ON CONFLICT` 繞過 ChangeTracker）的寫入順序問題——PQ-JOIN-003 的「資料庫實際落地結果是否正確」MUST 由 12.4c 的真實 PostgreSQL 測試驗證，這裡的測試只驗證 Handler 對 repository 契約的呼叫順序/分支邏輯
- [x] 12.4a **Repository 層併發測試**：`PurchaseQueueRepository.AddOrGetExistingAsync`（對應 PQ-JOIN-004、PQ-JOIN-003 的併發面向）MUST 比照既有 `tests/ProjectC.Infrastructure.Tests/OrderServiceConcurrencyTests.cs` 的既定手法——用 `PostgresFixture`（真實 Testcontainers PostgreSQL）為兩個「並發請求」各自建立獨立的 `ApplicationDbContext`／Repository instance，用 `Task.WhenAll` 同時觸發兩次 `AddOrGetExistingAsync`，不得用同一個 `DbContext` 模擬併發；驗證兩者都成功回傳（其中一個回傳新增的紀錄、另一個回傳同一筆既有紀錄，Id 相同），資料庫最終只有一筆進行中紀錄。並驗證「輸家」那次呼叫完成後，其呼叫端能立即對同一個外層交易繼續執行其他查詢／呼叫 `transaction.CommitAsync()` 而不拋出「current transaction is aborted」之類的錯誤——這是本次改用 `ON CONFLICT DO NOTHING` 要驗證的核心行為，取代先前版本對 `ChangeTracker.Entries()` 計數的檢查（該檢查已不適用，因為 `ON CONFLICT` 寫法完全不經過 `ChangeTracker`）
- [x] 12.4d（審查後新增）**`AddOrGetExistingAsync_WithoutActiveTransaction_ThrowsInvalidOperationException`（`tests/ProjectC.Infrastructure.Tests/PurchaseQueue/AddOrGetExistingAsyncTests.cs`，比照既有 `GetForUpdateAsyncTests.cs` 的 `WithoutActiveTransaction` 慣例）**：驗證 2.3a 的 `EnsureActiveTransaction` 防禦性檢查確實生效——已用還原修正、重跑測試轉紅的方式驗證這個測試本身有效（還原前拋出的是 Postgres 外鍵違反例外，不是 `InvalidOperationException`，斷言正確失敗）
- [x] 12.4c **PQ-JOIN-003／Medium 問題 3 的真實 PostgreSQL 整合測試（`tests/ProjectC.Infrastructure.Tests/PurchaseQueue/`，比照 `OrderServiceQueueModeLinearizationTests.cs` 的手法，用真實 `EventRepository`／`PurchaseQueueRepository`／`UnitOfWork` 組裝真正的 `JoinPurchaseQueueHandler`，不用 Fake）**：(1) 預先在資料庫種一筆已逾時的 `Admitted` 紀錄，呼叫 `JoinPurchaseQueueHandler.HandleAsync`，驗證交易提交後資料庫**同時**存在舊紀錄（`Status = Expired`）與新紀錄（`Status = Waiting`）、兩筆 Id 不同、回傳值等於新紀錄的 Id——這是驗證 5.1a（`AddOrGetExistingAsync` 必須先 flush 再 INSERT）確實生效的直接手段，若 5.1a 沒有正確實作，新紀錄不會被建立，回傳值會是舊紀錄的 Id；(2) 比照 `GetByIdInterceptingEventRepository` 的攔截手法，模擬 Admin 在 5.1 步驟 1 的快速失敗檢查通過「之後」、步驟 2 的交易內鎖定「之前」關閉熱門搶購模式，驗證最終以鎖定後的最新值為準（拒絕加入排隊、不建立任何紀錄），驗證 5.1 步驟 2 的線性化確實生效
- [x] 12.4b **PQ-JOIN-009 獨立的 WebApi 整合測試（`tests/ProjectC.WebApi.Tests/`）**：呼叫加入排隊端點時，於請求 Body／Query String 額外夾帶一個看似合法的 `memberId`（指向另一個已存在會員），確認：(1) 端點的 request DTO 本身沒有可接受外部 `memberId` 的欄位（型別層級即不接受，多餘欄位被 model binding 忽略）；(2) 呼叫後查詢資料庫，新建立的 `PurchaseQueueEntry.MemberId` 等於呼叫者 JWT 的 `sub`／`GetMemberId()`，與請求中夾帶的其他會員 Id 無關
- [x] 12.5 整合測試：`purchase-queue` 能力 — 查詢排隊狀態（PQ-STATUS-001~PQ-STATUS-008）
- [x] 12.6 整合測試：`purchase-queue` 能力 — 入場推進（PQ-ADMIT-001~PQ-ADMIT-004）
- [x] 12.6a（審查後新增）**`AdvanceQueueOnceAsync_WhenQueueModeIsDisabledByAdminAfterTheInitialScanButBeforeThisEventIsProcessed_SkipsTheEventAndAdmitsNoOne`（`tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueAdmissionServiceTests.cs`，比照 `OrderServiceQueueModeLinearizationTests` 的攔截手法，改用 `IServiceScopeFactory.CreateScope()` 計數攔截）**：驗證 7.3 的交易內 `Event` 重新鎖定確認確實生效——已用還原修正、重跑測試轉紅的方式驗證（還原前該活動的 Waiting 紀錄會被錯誤放行為 Admitted）
- [ ] 12.7a（審查後新增，**刻意不寫**，記錄理由供未來覆核）：曾考慮補「兩個 Admin 同時切換同一活動 Queue Mode」的併發整合測試，但評估後判斷這個測試無法斷言出能區分修正前後行為的觀察結果——`EnableQueueMode()`／`DisableQueueMode()` 是無條件 setter，不依賴讀到的舊值，兩種寫法（修正前的no-tracking 讀取＋盲寫，或修正後的 `GetForUpdateAsync` 鎖定讀取）在 PostgreSQL 的 UPDATE 陳述式本身都會取得列鎖直到 commit，不會拋例外或死鎖，最終結果一律是「誰的 commit 在時間軸上真的最後執行就贏」，這件事本身在兩種寫法下都成立；4.1a 修正的價值在於「後到者的決策依據是否為最新值」而非「最終寫入值本身」，這需要攔截 `HandleAsync` 內部的執行時序（GetForUpdateAsync 之後、Commit 之前）才能觀察，而 `SetEventQueueModeHandler.HandleAsync` 沒有暴露可攔截的中間步驟；底層 `GetForUpdateAsync` 的鎖定序列化行為已由 `GetForUpdateAsyncTests`／`OrderServiceQueueModeLinearizationTests` 等既有測試涵蓋，不需要重複驗證同一個機制
- [x] 12.7 整合測試：`purchase-queue` 能力 — Waiting 無逾時、Admin 切換、設定驗證（PQ-WAIT-001、PQ-TOGGLE-001~002、PQ-CONFIG-001~002）
- [x] 12.8 整合測試：`purchase-queue` 能力 — 建立訂單後標記完成（PQ-COMPLETE-001~002）
- [x] 12.9 整合測試：`ticket-purchase` 能力（TP-BROWSE-001~004、TP-ORDER-001~016）；TP-ORDER-015／016 MUST 實際驗證「交易內鎖定重讀讀到最新值」這件事本身，不能只驗證最終行為剛好正確——測試需要先讓 `orderEvent` 的交易前讀取發生（進入 `PlaceOrderAsync`），接著在測試中另開一個獨立 `DbContext` 修改並提交 `IsQueueModeEnabled`，再讓原本的請求繼續往下執行到交易內的 `GetForUpdateAsync`，確認讀到的是後來提交的新值；若 1.5（`EventRepository.GetByIdAsync` 改 `AsNoTracking()`）沒有正確實作，這個測試 MUST 會失敗（讀到交易前的舊值），可作為驗收 1.5 是否確實生效的直接手段
- [x] 12.10 整合測試：`SetEventQueueModeRequest.Enabled` 為 `bool?` 的 model binding 行為（`{}` → `null` → 400；`{"enabled":false}` → 成功；`{"enabled":"false"}` → 400，對應 PQ-ADMIN-004／006／007）

## 13. 前端測試

- [x] 13.1 元件測試：`buyer-web-ui` 能力（BW-ORDER-001~016、BW-QUEUE-001~006、BW-TOGGLE-001，見第 15 節追溯表；BW-QUEUE-004／006 須驗證前端同時檢查 `status === 403` 且 `title === "QueueAdmissionRequired"` 兩者才導向排隊畫面，`title` 不符時（BW-QUEUE-006）沿用一般授權錯誤處理）

## 14. Spec 同步確認

- [x] 14.1 實作完成後比對本次 `openspec/changes/rate-limiting-queue/specs/` 與最終實作行為是否一致，如有偏差回報並更新 spec
- [x] 14.2 確認 `docs/project-scope.md` 第 9 節現有進度快照是否需要更新（Phase 2 Should 項目進度）

## 15. AC ↔ Test Traceability

> 每一列對應 spec 中一個 `#### Scenario:` 標題的 AC ID。「Test task」欄位指向本文件第 12～13 節的測試任務編號；實作該 Scenario 對應行為的任務見第 1～11 節（依 Capability 分組，不逐列重複列出）。

### api-rate-limiting（新能力，套用於下單相關端點的限流）

| AC ID | Requirement | Scenario | Test task |
|---|---|---|---|
| RL-001 | 下單相關端點的請求頻率限制 | 請求次數未超過限制 | 12.2 |
| RL-002 | 下單相關端點的請求頻率限制 | 恰好第 PermitLimit 次請求仍允許 | 12.2 |
| RL-003 | 下單相關端點的請求頻率限制 | 第 PermitLimit+1 次請求起拒絕 | 12.2 |
| RL-004 | 下單相關端點的請求頻率限制 | 兩個端點的用量互不影響 | 12.2 |
| RL-005 | 下單相關端點的請求頻率限制 | 不同會員的限流各自獨立 | 12.2 |
| RL-006 | 下單相關端點的請求頻率限制 | 時間窗重置後恢復可請求 | 12.2 |
| RL-007 | 限流拒絕回應格式統一為 ProblemDetails，並附帶 Retry-After | 限流拒絕回應格式 | 12.2 |
| RL-008 | 限流設定值須為正數，缺漏時採用明確預設值 | 設定缺漏時採用預設值 | 12.2 |
| RL-009 | 限流設定值須為正數，缺漏時採用明確預設值 | 設定值為 0 或負數時擋下 | 12.2 |

### purchase-queue（新能力，Admin 開關 / 排隊 / 入場推進）

| AC ID | Requirement | Scenario | Test task |
|---|---|---|---|
| PQ-ADMIN-001 | Admin 可針對個別活動開關熱門搶購模式 | Admin 開啟熱門搶購模式 | 12.3 |
| PQ-ADMIN-002 | Admin 可針對個別活動開關熱門搶購模式 | Admin 關閉熱門搶購模式 | 12.3 |
| PQ-ADMIN-003 | Admin 可針對個別活動開關熱門搶購模式 | 非 Admin 嘗試開關熱門搶購模式 | 12.3 |
| PQ-ADMIN-004 | Admin 可針對個別活動開關熱門搶購模式 | 請求 Body 完全缺漏 enabled 欄位 | 12.3／12.10 |
| PQ-ADMIN-005 | Admin 可針對個別活動開關熱門搶購模式 | 對不存在的活動開關熱門搶購模式 | 12.3 |
| PQ-ADMIN-006 | Admin 可針對個別活動開關熱門搶購模式 | 請求明確指定 enabled 為 false | 12.3／12.10 |
| PQ-ADMIN-007 | Admin 可針對個別活動開關熱門搶購模式 | 請求的 enabled 型別錯誤 | 12.3／12.10 |
| PQ-JOIN-001 | 買家可加入活動的購票排隊 | 首次加入排隊 | 12.4 |
| PQ-JOIN-002 | 買家可加入活動的購票排隊 | 重複加入排隊 | 12.4 |
| PQ-JOIN-003 | 買家可加入活動的購票排隊 | 資格逾時後重新加入排隊 | 12.4／12.4c |
| PQ-JOIN-004 | 買家可加入活動的購票排隊 | 兩個請求同時首次加入排隊 | 12.4a |
| PQ-JOIN-005 | 買家可加入活動的購票排隊 | 對未開啟熱門搶購模式的活動加入排隊 | 12.4 |
| PQ-JOIN-006 | 買家可加入活動的購票排隊 | 對不存在的活動加入排隊 | 12.4 |
| PQ-JOIN-007 | 買家可加入活動的購票排隊 | Admin 角色帳號也可加入排隊 | 12.4 |
| PQ-JOIN-008 | 買家可加入活動的購票排隊 | 未登入呼叫加入排隊 | 12.4 |
| PQ-JOIN-009 | 買家可加入活動的購票排隊 | 排隊紀錄的會員身份一律取自 JWT | 12.4b |
| PQ-STATUS-001 | 買家可查詢自己的排隊狀態 | 查詢時即時推導已逾時但尚未被背景服務標記的紀錄 | 12.5 |
| PQ-STATUS-002 | 買家可查詢自己的排隊狀態 | 查詢等待中的排隊狀態 | 12.5 |
| PQ-STATUS-003 | 買家可查詢自己的排隊狀態 | 查詢已入場的排隊狀態 | 12.5 |
| PQ-STATUS-004 | 買家可查詢自己的排隊狀態 | 查詢已逾時的排隊狀態 | 12.5 |
| PQ-STATUS-005 | 買家可查詢自己的排隊狀態 | 查詢尚未加入排隊的活動 | 12.5 |
| PQ-STATUS-006 | 買家可查詢自己的排隊狀態 | 查詢時僅有已完成的歷史紀錄 | 12.5 |
| PQ-STATUS-007 | 買家可查詢自己的排隊狀態 | 查詢不存在的活動的排隊狀態 | 12.5 |
| PQ-STATUS-008 | 買家可查詢自己的排隊狀態 | 查詢回應附帶當下的 queueModeEnabled | 12.5 |
| PQ-ADMIT-001 | 排隊入場名額依先後順序推進，且有名額上限 | 有剩餘名額時推進等待中的排隊 | 12.6 |
| PQ-ADMIT-002 | 排隊入場名額依先後順序推進，且有名額上限 | 名額已滿時不推進 | 12.6 |
| PQ-ADMIT-003 | 排隊入場名額依先後順序推進，且有名額上限 | 入場逾時釋放名額 | 12.6 |
| PQ-ADMIT-004 | 排隊入場名額依先後順序推進，且有名額上限 | 併發推進不超額入場 | 12.6 |
| PQ-WAIT-001 | 等待中的排隊紀錄沒有自身逾時機制 | 長時間等待不會被自動清理 | 12.7 |
| PQ-TOGGLE-001 | Admin 關閉熱門搶購模式後，既有排隊紀錄不主動清理 | 關閉熱門搶購模式後既有 Waiting 紀錄停止推進 | 12.7 |
| PQ-TOGGLE-002 | Admin 關閉熱門搶購模式後，既有排隊紀錄不主動清理 | 重新開啟熱門搶購模式後沿用既有排隊順序 | 12.7 |
| PQ-CONFIG-001 | 入場名額上限、逾時時間與推進間隔須為正數設定 | 設定值為正數時正常啟動 | 12.7 |
| PQ-CONFIG-002 | 入場名額上限、逾時時間與推進間隔須為正數設定 | 設定值為 0 或負數時啟動失敗 | 12.7 |
| PQ-COMPLETE-001 | 建立訂單成功後標記排隊紀錄為已完成，名額即時釋放 | 成功建立訂單後標記排隊紀錄完成 | 12.8 |
| PQ-COMPLETE-002 | 建立訂單成功後標記排隊紀錄為已完成，名額即時釋放 | 名額於交易提交後立即可供下一位使用 | 12.8 |

### ticket-purchase（MODIFIED，本次新增/修改部分）

| AC ID | Requirement | Scenario | Test task |
|---|---|---|---|
| TP-BROWSE-001 | 瀏覽活動與座位可售狀態 | 查詢活動列表 | 12.9 |
| TP-BROWSE-002 | 瀏覽活動與座位可售狀態 | 查詢活動座位可售狀態 | 12.9 |
| TP-BROWSE-003 | 瀏覽活動與座位可售狀態 | 查詢活動票種與價格 | 12.9 |
| TP-BROWSE-004 | 瀏覽活動與座位可售狀態 | 查詢不存在的活動 | 12.9 |
| TP-ORDER-001 | 透過 API 建立訂單並鎖定座位或扣減票種庫存 | 成功建立訂單 | 12.9 |
| TP-ORDER-002 | 透過 API 建立訂單並鎖定座位或扣減票種庫存 | 座位已被鎖定時建立訂單失敗 | 12.9 |
| TP-ORDER-003 | 透過 API 建立訂單並鎖定座位或扣減票種庫存 | 選定不存在的座位或票種 | 12.9 |
| TP-ORDER-004 | 透過 API 建立訂單並鎖定座位或扣減票種庫存 | 座位分區與票種分區不一致 | 12.9 |
| TP-ORDER-005 | 透過 API 建立訂單並鎖定座位或扣減票種庫存 | 成功建立純計數選購的訂單 | 12.9 |
| TP-ORDER-006 | 透過 API 建立訂單並鎖定座位或扣減票種庫存 | 純計數票種指定了座位 | 12.9 |
| TP-ORDER-007 | 透過 API 建立訂單並鎖定座位或扣減票種庫存 | 綁座位票種未指定座位 | 12.9 |
| TP-ORDER-008 | 透過 API 建立訂單並鎖定座位或扣減票種庫存 | 座位項目指定非 1 的購買數量 | 12.9 |
| TP-ORDER-009 | 透過 API 建立訂單並鎖定座位或扣減票種庫存 | 同一計數票種在同一次請求中重複出現 | 12.9 |
| TP-ORDER-010 | 透過 API 建立訂單並鎖定座位或扣減票種庫存 | 座位選購項目未提供購買數量（既有客戶端相容） | 12.9 |
| TP-ORDER-011 | 透過 API 建立訂單並鎖定座位或扣減票種庫存 | 熱門搶購模式下已入場的會員成功建立訂單 | 12.9 |
| TP-ORDER-012 | 透過 API 建立訂單並鎖定座位或扣減票種庫存 | 熱門搶購模式下未入場即嘗試建立訂單 | 12.9 |
| TP-ORDER-013 | 透過 API 建立訂單並鎖定座位或扣減票種庫存 | 一般活動不受熱門搶購模式影響 | 12.9 |
| TP-ORDER-014 | 透過 API 建立訂單並鎖定座位或扣減票種庫存 | 排隊資格於建立訂單處理過程中才變為逾時 | 12.9 |
| TP-ORDER-015 | 透過 API 建立訂單並鎖定座位或扣減票種庫存 | 建立訂單處理過程中熱門搶購模式才被開啟 | 12.9 |
| TP-ORDER-016 | 透過 API 建立訂單並鎖定座位或扣減票種庫存 | 建立訂單處理過程中熱門搶購模式才被關閉 | 12.9 |

### buyer-web-ui（MODIFIED，本次新增/修改部分）

| AC ID | Requirement | Scenario | Test task |
|---|---|---|---|
| BW-ORDER-001 | 買家可選位並送出訂單 | 選擇可售座位並成功下單 | 13.1 |
| BW-ORDER-002 | 買家可選位並送出訂單 | 下單時座位已被搶先鎖定 | 13.1 |
| BW-ORDER-003 | 買家可選位並送出訂單 | 送出訂單時登入狀態已失效（401） | 13.1 |
| BW-ORDER-004 | 買家可選位並送出訂單 | 已選座位數達到每筆訂單限購張數 | 13.1 |
| BW-ORDER-005 | 買家可選位並送出訂單 | 活動未設定限購張數 | 13.1 |
| BW-ORDER-006 | 買家可選位並送出訂單 | 未登入嘗試調整計數購買數量 | 13.1 |
| BW-ORDER-007 | 買家可選位並送出訂單 | 純計數票種輸入購買數量並成功下單 | 13.1 |
| BW-ORDER-008 | 買家可選位並送出訂單 | 混合座位選購與純計數購買並成功下單 | 13.1 |
| BW-ORDER-009 | 買家可選位並送出訂單 | 純計數購買數量達到每筆訂單限購張數 | 13.1 |
| BW-ORDER-010 | 買家可選位並送出訂單 | 計數輸入元件限制數量不得超過可售總量 | 13.1 |
| BW-ORDER-011 | 買家可選位並送出訂單 | 送出時因庫存已變動被後端拒絕 | 13.1 |
| BW-ORDER-012 | 買家可選位並送出訂單 | 純計數票種可售總量為 0 | 13.1 |
| BW-ORDER-013 | 買家可選位並送出訂單 | 活動未設定限購張數時的純計數購買 | 13.1 |
| BW-ORDER-014 | 買家可選位並送出訂單 | 計數購買數量為 0 時不送出對應項目 | 13.1 |
| BW-ORDER-015 | 買家可選位並送出訂單 | 送出訂單前偵測到合併總數超過限購張數 | 13.1 |
| BW-ORDER-016 | 買家可選位並送出訂單 | 已手動選取座位佔用額度後，計數購買輸入上限隨之減少 | 13.1 |
| BW-QUEUE-001 | 買家可選位並送出訂單 | 進入熱門搶購模式活動且尚未加入排隊 | 13.1 |
| BW-QUEUE-002 | 買家可選位並送出訂單 | 排隊中顯示等待畫面並輪詢狀態 | 13.1 |
| BW-QUEUE-003 | 買家可選位並送出訂單 | 排隊入場後開放選位與下單 | 13.1 |
| BW-QUEUE-004 | 買家可選位並送出訂單 | 下單時排隊資格已逾時 | 13.1 |
| BW-QUEUE-005 | 買家可選位並送出訂單 | 下單因請求頻率限制被拒絕 | 13.1 |
| BW-QUEUE-006 | 買家可選位並送出訂單 | 其他語意的 403 不視為排隊資格不足 | 13.1 |
| BW-TOGGLE-001 | 買家可選位並送出訂單 | 排隊等待期間 Queue Mode 被關閉 | 13.1 |
