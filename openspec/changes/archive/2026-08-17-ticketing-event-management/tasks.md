## 1. Application 層：Handler

依 design.md 決策 1：每個 Handler 都是「先讀取/驗證（不開交易）→ 全部通過才開交易 → 只把 `Repository.Add` + `CommitAsync` 包在交易裡」，不要在驗證階段就先 `BeginTransactionAsync`。

- [x] 1.1 新增 `CreateVenueHandler` + `CreateVenueRequest(string Name)` + `CreateVenueRequestValidator`（`Name` 必填、`MaxLength(200)`，對齊 `Venue.Name` 欄位上限），比照 `RegisterMemberHandler` 風格；流程：驗證 → `new Venue(...)` → 開交易 → `IVenueRepository.Add` → `CommitAsync` → 回傳 `Result<Guid>`
- [x] 1.2 新增 `CreateSeatMapHandler` + `CreateSeatMapRequest(IReadOnlyList<SeatRequest> Seats)`（`SeatRequest(string ZoneCode, string SeatNumber)`）+ Validator（`Seats` 至少一筆，每筆 `ZoneCode`/`SeatNumber` 必填、`MaxLength(50)`，對齊 `Seat` 欄位上限）；流程：確認 `VenueId`（路由參數）存在（`IVenueRepository.GetByIdAsync`，找不到回 `Error.NotFound`）→ **檢查 `request.Seats` 內部 `(ZoneCode, SeatNumber)` 組合是否重複，重複直接回 `Error.Conflict`（見 design.md 決策 2，不要依賴 `AddSeat` 拋例外）** → `new SeatMap(...)` → 逐筆 `seatMap.AddSeat(...)`（保留 `catch (InvalidOperationException)` 當最後防線，理論上不會觸發）→ 開交易 → `ISeatMapRepository.Add` → Commit
- [x] 1.3 新增 `CreateEventHandler` + `CreateEventRequest(string Title, DateTime StartAtUtc, Guid VenueId, Guid SeatMapId)` + Validator（`Title` 必填、`MaxLength(200)`；`StartAtUtc != default(DateTime)`；`VenueId`/`SeatMapId` 不可為 `Guid.Empty`）；流程：確認 `VenueId`／`SeatMapId` 皆存在（找不到回 `Error.NotFound`，`ISeatMapRepository.GetByIdAsync` 已經會 `Include(Seats)`）→ `new Event(...)` → `event.CreateEventSeats(seatMap)` → 開交易 → **同一筆交易內依序 `IEventRepository.Add(event)` 與 `IEventSeatRepository.AddRange(eventSeats)`（見 design.md 決策 1，兩者缺一都會建出資料不完整的活動）** → Commit
- [x] 1.4 新增 `CreateTicketTypeHandler` + `CreateTicketTypeRequest(string ZoneCode, decimal Price)` + Validator（`ZoneCode` 必填、`MaxLength(50)`；`Price > 0`）；流程：確認 `EventId`（路由參數）存在（找不到回 `Error.NotFound`）→ 用 `event.SeatMapId` 透過 `ISeatMapRepository.GetByIdAsync` 載入座位圖（見 design.md 決策 3 的兩步查詢；**這裡查不到理論上不會發生（`Events.SeatMapId` 有 FK、沒有 Delete 端點），但仍要防禦性回 `Error.NotFound("找不到活動對應的座位圖。")`，不要對 null 解參考**）→ **檢查 `seatMap.Seats.Any(s => s.ZoneCode == request.ZoneCode)`，不存在直接回 `Error.Validation`（見 design.md 決策 2，不要依賴 `CreateTicketType` 拋例外）** → `event.CreateTicketType(zoneCode, price, seatMap)`（保留 `catch (ArgumentOutOfRangeException)`/`catch (InvalidOperationException)` 當最後防線；`seatMap.Id != SeatMapId` 那個 `ArgumentException` 分支結構上打不到，不用攔截）→ 開交易 → `ITicketTypeRepository.Add` → Commit

## 2. WebApi：Controller

- [x] 2.1 新增 `AdminVenuesController`（`api/admin/venues`），套 `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`：`POST /` 呼叫 `CreateVenueHandler`；`POST /{venueId:guid}/seat-maps` 呼叫 `CreateSeatMapHandler`；成功回 `StatusCode(StatusCodes.Status201Created, new { id })`（不用 `CreatedAtAction`，這次沒有對應的 GET 端點，見 design.md 決策 4）
- [x] 2.2 新增 `AdminEventsController`（`api/admin/events`），套 `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`：`POST /` 呼叫 `CreateEventHandler`；`POST /{eventId:guid}/ticket-types` 呼叫 `CreateTicketTypeHandler`；成功回應格式同上
- [x] 2.3 於 `Program.cs` 註冊 4 個 Handler 為 `AddScoped`（FluentValidation Validator 已透過既有的 `AddValidatorsFromAssemblyContaining<RegisterMemberRequestValidator>` 掃描同一個 Assembly，不需要額外註冊）

## 3. 測試

- [x] 3.1 `ProjectC.Application.Tests`：`CreateVenueHandler`（成功建立、`Name` 空白時驗證失敗）
- [x] 3.2 `ProjectC.Application.Tests`：`CreateSeatMapHandler`（成功建立、座位重複回 Conflict、`VenueId` 不存在回 NotFound）
- [x] 3.3 `ProjectC.Application.Tests`：`CreateEventHandler`（成功建立且產生對應數量的 `EventSeat`、缺標題/時間驗證失敗、`VenueId`/`SeatMapId` 不存在回 NotFound）
- [x] 3.4 `ProjectC.Application.Tests`：`CreateTicketTypeHandler`（成功建立、票價 ≤ 0 驗證失敗、分區不存在回對應錯誤、`EventId` 不存在回 NotFound）
- [x] 3.5 `ProjectC.WebApi.Tests`（Testcontainers 整合測試）：`AdminVenuesControllerTests` 涵蓋 spec `event-management` 內「後台管理 API 需要 Admin 角色」與「透過管理 API 建立場地與座位圖」全部 Scenario
- [x] 3.6 `ProjectC.WebApi.Tests`：`AdminEventsControllerTests` 涵蓋 spec `event-management` 內「透過管理 API 建立活動與票種」全部 Scenario

## 4. 收尾檢查

- [x] 4.1 確認這次沒有修改任何 `ProjectC.Domain` 檔案（範圍純粹是 Application + WebApi）
- [x] 4.2 確認 4 個 Handler 沒有繞過 `IUnitOfWork` 自行呼叫 `SaveChangesAsync`，所有寫入都包在 `BeginTransactionAsync`/`CommitAsync` 內（延續 `ticketing-infrastructure` 決策 4）
- [x] 4.3 執行全部測試（`docker compose exec api dotnet test`），確認通過
- [x] 4.4 比對 tasks 完成狀況與 `event-management` spec 的全部 12 個 Scenario，確認皆有對應測試
- [x] 4.5 主動告知 spec 同步狀態：這次的 `event-management` 是全新能力，archive 時需要把 delta spec 建成新的 `openspec/specs/event-management/spec.md`（沿用 `ticketing-infrastructure` 歸檔時的做法）

**[2026-08-16 自查修正]** 主動比對實作跟 design.md 決策 2 的落差，發現兩個 Handler 承諾了「保留 catch 當最後防線」卻沒有實際寫出來：`CreateSeatMapHandler` 的 `AddSeat` 迴圈、`CreateTicketTypeHandler` 的 `CreateTicketType` 呼叫都補上對應的 try/catch。另外把 `AdminVenuesControllerTests`/`AdminEventsControllerTests` 重複的 `CreateAuthenticatedAdminClientAsync`／`CreatedResponse` 抽到共用的 `AuthTestHelper`/`TestSupport`，不重複貼三份（含既有的 `AdminMembersControllerTests`）。全部測試重跑確認 173 個依然全過。
