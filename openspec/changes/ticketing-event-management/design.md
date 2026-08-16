## Context

`ticketing-infrastructure` 已經把 Repository（`IVenueRepository`/`ISeatMapRepository`/`IEventRepository`/`ITicketTypeRepository`，皆只有 `GetByIdAsync`/`Add`；`IEventSeatRepository` 額外有 `AddRange`，建立活動時要用）與 `IUnitOfWork`（寫入必須包在 `BeginTransactionAsync`/`CommitAsync` 才會落地，Repository 自己不能呼叫 `SaveChangesAsync`）準備好了。這次要在上面疊 Application 層 Handler + WebApi Controller，讓 Admin 能透過 HTTP 建立活動資料。

會員系統的 `RegisterMemberHandler` 是既有的 Handler 風格參考：接 Request → FluentValidation 驗證 → 呼叫 Domain 建構子/工廠方法 → 存檔 → 回傳 `Result<T>`。差異在於會員系統直接注入 `IApplicationDbContext`（Entity 少於 5 個），這次售票的 Handler 要用 Repository + `IUnitOfWork`（`ticketing-infrastructure` 已定案的模式）。

Admin 角色驗證已經有現成機制：`AuthorizationPolicies.AdminOnly` + `[Authorize(Policy = ...)]`，`AdminMembersController` 就是這樣寫的，這次直接沿用，不需要重新設計。

## Goals / Non-Goals

**Goals:**
- Admin 建立 `Venue`、`SeatMap`（含座位樣板）、`Event`（自動產生 `EventSeat`）、`TicketType` 的 API，皆需要 Admin 角色。
- Handler 層明確驗證父資源存在（Venue/SeatMap/Event），不讓資料庫 FK 違反例外變成 API 的主要錯誤回應路徑。
- Handler 層對可預期的業務錯誤（座位重複、分區不存在、必填欄位、票價無效等）在呼叫 Domain 前主動檢查並回 `Result.Failure`；Domain 拋出的對應例外只當最後防線，不讓未預期的例外穿透到 Controller。

**Non-Goals:**
- 買家端 API（瀏覽、下單）——留給下一個 change。
- 後台訂單查看、逾時訂單背景清理——留給下一個 change。
- `Order.BuyerId`——這次不涉及訂單。
- 修改／刪除既有活動資料——這次只做建立，沒有 Update/Delete 端點。
- Event 的 `VenueId` 與 `SeatMapId` 是否互相隸屬（`SeatMap.VenueId` 是否等於 `Event.VenueId`，座位圖是否真的屬於該場地）——這是 Domain 從 `ticketing-core-domain` 就沒有的既有規則缺口，這次沿用現狀，不新增這個檢查（若要補，屬於 Domain 層變更，不在這次 API 層change 範圍）。**明確寫清楚：`CreateEventHandler` 只各自確認 `VenueId`、`SeatMapId` 存在，即使 `seatMap.VenueId != request.VenueId` 也會建立成功**，避免實作時有人「好心」補上這個檢查，跟這條 Non-Goal 打架。（這跟決策 2 提到的「`seatMap.Id != SeatMapId`」是兩件不同的事：這裡是「座位圖屬於哪個場地」，決策 2 是「傳入的座位圖物件是否等於這個活動自己記錄的 SeatMapId」，後者在這次的 API 設計下結構上不會不一致。）

## Decisions

### 1. Handler 先讀取／驗證，最後才開交易，只包住寫入
延續 `ticketing-infrastructure` 的既定分工：售票資料一律透過 Repository 存取，`IApplicationDbContext` 沒有暴露售票 `DbSet`。父資源存在性檢查（`GetByIdAsync`）是純讀取，不需要交易保護；交易只包住「確定會寫入」的那一段，比照 `RegisterMemberHandler`「先讀（查 email 是否存在）→ 最後才寫」的既有結構：

```csharp
// 1. 驗證父資源存在（Repository.GetByIdAsync，純讀取，不開交易；找不到回 Error.NotFound）
// 2. 建構 Domain 物件（捕捉預期的驗證例外，轉成 Result.Failure；純記憶體操作，也不需要交易）
// 3. 只有前面都成功，才開交易：
await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
_repository.Add(entity); // CreateEventHandler 例外：這裡是兩個 Repository.Add/AddRange，見下方說明
await tx.CommitAsync(cancellationToken);
return Result<Guid>.Success(entity.Id);
```

這樣「傳入不存在的 VenueId」這種最基本的驗證失敗，不會白開一個 DB 交易再 Rollback。步驟 1、2 之間讀到的資料（例如 `SeatMap` 物件）到步驟 3 寫入之間，理論上有極短的視窗可能被其他請求變動——但這幾個 Entity 這次沒有任何 Update/Delete API（見 Non-Goals），實務上不會發生，所以不需要在讀取階段就先鎖定。

**`CreateEventHandler` 是唯一一個交易內要寫兩個 Repository 的**：`IEventRepository.Add(event)` 與 `IEventSeatRepository.AddRange(eventSeats)` 必須在同一筆交易裡（`event.CreateEventSeats(seatMap)` 產生的 `EventSeat` 清單不會自動存檔，Handler 要自己呼叫 `AddRange`），漏掉會建出一個沒有任何座位庫存的活動。

### 2. Handler 主動預先檢查，Domain 例外只當最後防線（不是靠 catch 判斷分類）
`SeatMap.AddSeat`、`TicketType` 建構子這些既有 Domain 方法用一般 CLR 例外（`ArgumentException`/`ArgumentOutOfRangeException`/`InvalidOperationException`）表示驗證失敗，不是 `ticketing-core-domain` 那批具名的 Domain 例外（`SeatAlreadyHeldException` 這類）。**如果只靠 `catch (InvalidOperationException)` 來處理，會遇到同一個例外型別代表兩種不同情境（座位重複／分區不存在），沒辦法只憑型別分類，逼得要去解析例外訊息字串才能決定回 `Error.Conflict` 還是 `Error.Validation`，很脆弱。改成跟 `ArgumentException`（必填欄位、票價 ≤ 0）一樣的處理哲學：Handler 在呼叫 Domain 方法「之前」就先主動檢查，讓 Domain 例外變成理論上不會被觸發的最後防線**：

- **座位重複**（`CreateSeatMapHandler`）：呼叫 `seatMap.AddSeat(...)` 前，先檢查 `request.Seats` 內部是否有重複的 `(ZoneCode, SeatNumber)` 組合（例如 `GroupBy` 後檢查是否有 count > 1 的群組），有重複直接回 `Error.Conflict`，不進入 `AddSeat` 迴圈。
- **票種分區不存在**（`CreateTicketTypeHandler`）：呼叫 `event.CreateTicketType(...)` 前，先檢查載入的 `seatMap.Seats.Any(s => s.ZoneCode == request.ZoneCode)`，不存在直接回 `Error.Validation`，不呼叫工廠方法。
- **必填欄位缺漏、票價 ≤ 0**：由 FluentValidation 在 Handler 一開始就擋掉。

上述三種情況都在呼叫 Domain 方法「之前」由 Handler 自己判斷完畢，Domain 方法理論上不會再拋出對應的例外。Handler 仍然保留 `catch` 這些**特定、預期**的例外型別（`ArgumentException`/`ArgumentOutOfRangeException`/`InvalidOperationException`）作為最後一道防線（不是空泛地包一個 `catch (Exception)`）——因為每個 Handler 各自只呼叫一個會拋出 `InvalidOperationException` 的 Domain 方法（`CreateSeatMapHandler` 只呼叫 `AddSeat`，`CreateTicketTypeHandler` 只呼叫 `CreateTicketType`），`catch` 的位置本身就決定了對應到哪個 `Error`（`AddSeat` → `Error.Conflict`、`CreateTicketType` → `Error.Validation`），不需要解析例外訊息字串來分類。如果這個防線真的被觸發，代表 Handler 的預先檢查邏輯本身有漏洞，是需要回頭修的 bug，不是正常的業務流程。

**不處理 `Event.CreateEventSeats`/`Event.CreateTicketType` 內「`seatMap.Id != SeatMapId`」那個 `ArgumentException`（訊息是「座位圖不屬於此活動」）**：這個檢查在這次的 API 形狀下結構上打不到——`CreateEventHandler`/`CreateTicketTypeHandler` 用的 `SeatMap` 一律是透過 `event.SeatMapId`／請求本身的 `SeatMapId` 查出來的，不是讓呼叫端另外指定一個可能對不上的 SeatMapId，所以這個分支沒有對應的測試任務，也不用特別分類。

這個對應規則沿用既有 `ErrorType`／`ResultExtensions.ToActionResult` 的既定分類，不新增新的 `ErrorType`。

### 3. Handler 先驗證父資源存在，資料庫 FK 只當最後一道防線
建立 `SeatMap` 前先用 `IVenueRepository.GetByIdAsync` 確認 `VenueId` 存在；建立 `Event` 前確認 `VenueId`／`SeatMapId` 皆存在；建立 `TicketType` 前確認 `EventId` 存在。找不到一律回傳 `Error.NotFound`，不是讓 `ticketing-infrastructure` 新增的資料庫 FK 約束（`DeleteBehavior.Restrict`）在 `CommitAsync` 時才爆炸成未預期的例外。FK 約束仍然保留，作為「即使 Handler 邏輯有漏洞」的最後防線，但不是 API 對外的主要錯誤回應機制。

**`CreateTicketTypeHandler` 需要兩次 Repository 查詢，不是只查 `Event`**：`TicketType` 的建構子是 `internal`，只能透過 `Event.CreateTicketType(zoneCode, price, seatMap)` 這個工廠方法建立，而這個方法需要一個載好 `Seats` 的 `SeatMap` 物件（用來核對分區代碼是否存在）。流程依序是：

1. `IEventRepository.GetByIdAsync(eventId)` 確認活動存在，找不到回 `Error.NotFound`。
2. 用讀到的 `event.SeatMapId` 呼叫 `ISeatMapRepository.GetByIdAsync(event.SeatMapId)` 取得座位圖（這個方法本來就會 `Include(Seats)`，見 `ticketing-infrastructure` 決策 5）。**理論上這裡一定查得到**——`Events.SeatMapId` 有 FK 約束指向 `SeatMaps`，而這次完全沒有任何 Delete 端點（見 Non-Goals），SeatMap 建立後不可能消失。但仍然防禦性地處理：若真的查不到（代表資料不一致，不是正常業務情境），回 `Error.NotFound("找不到活動對應的座位圖。")`，不要讓後續程式碼對 `null` 解參考。
3. 檢查 `request.ZoneCode` 是否存在於 `seatMap.Seats` 內（見決策 2 的主動預先檢查），不存在回 `Error.Validation`。
4. 全部通過才呼叫 `event.CreateTicketType(request.ZoneCode, request.Price, seatMap)`。

第 2 步的 `SeatMap` 一律是從 `event.SeatMapId` 查出來的，不是讓呼叫端另外指定，所以不會發生「座位圖不是這個活動的」這種情況（呼應決策 2 最後一段）。

### 4. Controller 依資源拆成兩個：`AdminVenuesController`、`AdminEventsController`
比照 `AdminMembersController`（`api/admin/members`）的資源導向風格：
- `AdminVenuesController`：`POST /api/admin/venues`、`POST /api/admin/venues/{venueId}/seat-maps`
- `AdminEventsController`：`POST /api/admin/events`、`POST /api/admin/events/{eventId}/ticket-types`

兩者皆套用 `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`，不新增授權機制。

**成功回應用 `201 Created`，但不帶 `Location` header（不用 `CreatedAtAction`）**：這次完全沒有規劃任何 GET 端點，`CreatedAtAction`/`CreatedAtRoute` 需要對應的 GET route 才能產生 URL，這次用不了。改成 `StatusCode(StatusCodes.Status201Created, new { id })`，Body 帶新建資源的 ID。之後如果真的要補 GET-by-id 端點，再回頭補 `Location` header，不影響這次的回應格式。

### 5. Request DTO 形狀與明確的 Validator 規則
- `CreateVenueRequest(string Name)`
- `CreateSeatMapRequest(IReadOnlyList<SeatRequest> Seats)`，`SeatRequest(string ZoneCode, string SeatNumber)`；`VenueId` 來自路由，不放在 Body（避免 Body 跟路由不一致造成混淆）
- `CreateEventRequest(string Title, DateTime StartAtUtc, Guid VenueId, Guid SeatMapId)`
- `CreateTicketTypeRequest(string ZoneCode, decimal Price)`；`EventId` 來自路由

Validator 除了必填之外，**`MaxLength` 明確對齊 `ticketing-infrastructure` 的資料庫欄位上限**——不擋的話，過長字串不會在 Handler 層被擋下來，會直接留到 `CommitAsync` 才炸成未預期的 `DbUpdateException`（讓全域例外處理器接住，變成不夠精確的錯誤）：

| Request | 規則 |
| --- | --- |
| `CreateVenueRequest` | `Name` 必填，`MaxLength(200)`（對齊 `Venue.Name` 欄位上限） |
| `CreateSeatMapRequest` | `Seats` 至少一筆；每筆 `ZoneCode`/`SeatNumber` 必填、`MaxLength(50)`（對齊 `Seat` 欄位上限）；`Seats` 內部的 `(ZoneCode, SeatNumber)` 組合不可重複（見決策 2 的主動預先檢查，這條規則實際上是在 Handler 做，不是 FluentValidation，因為它是跨欄位／跨項目的檢查，FluentValidation 只擋「每筆欄位本身」） |
| `CreateEventRequest` | `Title` 必填、`MaxLength(200)`；`StartAtUtc != default(DateTime)`（對齊 `Event` 建構子自己的檢查——`DateTime` 是 non-nullable value type，請求沒帶這個欄位時 JSON 反序列化預設就是 `default`，不會是 `null`，一定要顯式檢查，不能只當作「有沒有帶」）；`VenueId`/`SeatMapId` 不可為 `Guid.Empty`（這兩個欄位在 Body 裡，不是路由參數，型別繫結不會自動擋空 Guid） |
| `CreateTicketTypeRequest` | `ZoneCode` 必填、`MaxLength(50)`；`Price > 0` |

這次刻意不驗證 `StartAtUtc` 的 `DateTimeKind` 是否為 UTC、也不擋過去的時間——`Event` 的 Domain 建構子本身沒有這兩條規則，API 層不無中生有新增 Domain 沒有的業務限制。

## Risks / Trade-offs

- **[Risk]** Handler 層的「父資源存在性檢查」與資料庫 FK 約束是兩層獨立防護，若其中一層邏輯改了、另一層沒同步更新，可能出現不一致（例如 Handler 檢查通過但 FK 還是拒絕，反之則不可能發生因為 FK 更嚴格）。→ **Mitigation**：整合測試（task 涵蓋）驗證 Handler 對缺失父資源的行為，FK 本身已在 `ticketing-infrastructure` 驗證過，這次不重複測 FK，只測 Handler 層的行為。
- **[Risk]** 沒有 Update/Delete 端點，資料一旦建錯無法透過 API 修正，只能直接動資料庫。→ **Mitigation**：這是這次刻意縮小的範圍（Non-Goals），先讓「建立」可用，管理端的修正操作留給之後視實際需求評估是否要做。
- ~~[Risk] 並發建立座位圖撞資料庫 Unique Index~~ **這條原本寫錯了，已刪除**：`(SeatMapId, ZoneCode, SeatNumber)` 的 Unique Index 是以 `SeatMapId` 為第一欄，而 `CreateSeatMapHandler` 每次都用 `new SeatMap(Guid.NewGuid(), venueId)` 建一個全新的 `SeatMap`——兩個並發請求各自產生不同的 `SeatMapId`，即使 `ZoneCode`/`SeatNumber` 剛好重疊也不會撞到同一個索引。這次也沒有「對既有 SeatMap 加座位」的端點（見 Non-Goals），所以這個情境在目前範圍內是真的打不到，不需要為它包 `DbUpdateException` 處理。

## Migration Plan

（無——這次不涉及任何資料庫 schema 變更，完全建立在 `ticketing-infrastructure` 已完成的 migration 之上。）

## Open Questions

- **同一活動內，同一個分區可以建立多個 `TicketType` 嗎？** 目前資料庫沒有 `(EventId, ZoneCode)` 的唯一性約束，Domain 的 `Event.CreateTicketType` 也不擋這件事，這次沿用現狀允許（不新增限制）。如果之後發現這其實是需要擋下來的業務規則（例如買家端會因為同分區多個票種而混淆該顯示哪個價格），需要回頭補 Domain 層的規則，不在這次的 API 層 change 範圍內處理。
- Event 的 `VenueId`／`SeatMapId` 隸屬關係若之後真的需要驗證，屬於獨立的 Domain 層決策，需要另外討論（見 Non-Goals）。
