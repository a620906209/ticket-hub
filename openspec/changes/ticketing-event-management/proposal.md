## Why

`ticketing-infrastructure`（已合併 master）把持久化打好了——8 個 Entity 的 EF Core mapping、6 個 Repository、`IUnitOfWork`、`EventSeat` 悲觀鎖——但完全沒有對外 API，沒有任何方式能透過 HTTP 建立活動資料。這次補上後台管理端：Admin 建立 Venue、SeatMap、Event、TicketType 的 API，讓售票平台第一次能透過 API 產生可販售的資料。

## What Changes

- 新增後台管理 API（`/api/admin/venues`、`/api/admin/venues/{id}/seat-maps`、`/api/admin/events`、`/api/admin/events/{id}/ticket-types`），皆需要 Admin 角色權限（沿用既有 `AuthorizationPolicies.AdminOnly`，與 `AdminMembersController` 相同授權機制，不新增）。
- 新增 4 個 Application 層 Handler（`CreateVenueHandler`/`CreateSeatMapHandler`/`CreateEventHandler`/`CreateTicketTypeHandler`），風格比照既有 `RegisterMemberHandler`：接請求 → FluentValidation 驗證 → 呼叫 Domain 建構子/工廠方法 → 透過 Repository + `IUnitOfWork` 存檔 → 回傳 `Result<Guid>`。
- 建立活動（`CreateEventHandler`）會依 `ticketing-infrastructure` 已就緒的 Domain 行為，自動呼叫 `Event.CreateEventSeats(seatMap)` 產生對應的 `EventSeat` 庫存並一併存檔。
- 建立票種（`CreateTicketTypeHandler`）先用 `Event.SeatMapId` 查出這場活動實際使用的座位圖，確認分區代碼存在其中，再透過 `Event.CreateTicketType(zoneCode, price, seatMap)` 建立。

本次不包含：買家端瀏覽/下單 API、`Order.BuyerId`、後台訂單查看、逾時訂單背景清理——這些留給更後面的 change，建立在這次的後台管理 API 之上。

## Capabilities

### New Capabilities
- `event-management`：後台管理 API，Admin 建立與管理 `Venue`、`SeatMap`、`Event`、`TicketType`。

### Modified Capabilities
（無）

## Impact

- 新增 `ProjectC.Application` 內 4 個 Handler + 對應 Request/Validator（比照 `Members/Register` 的檔案結構）。
- 新增 `ProjectC.WebApi.Controllers` 內兩個 Controller：`AdminVenuesController`（場地與座位圖）、`AdminEventsController`（活動與票種），皆套用 `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`。
- 不影響 `ticketing-infrastructure` 已完成的 Repository/`IUnitOfWork`/Configuration/Migration，只是這次真正開始「使用」它們。
- 依 CLAUDE.md 規則，這次涉及外部輸入（請求 Body）與權限檢查（Admin 角色），實作前需先過 CLAUDE.md「安全強制規則」清單。
