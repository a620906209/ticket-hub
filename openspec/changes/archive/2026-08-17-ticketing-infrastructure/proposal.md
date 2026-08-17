## Why

`ticketing-core-domain`（已歸檔）只有 Domain + Application 層，完全沒有資料庫持久化——功能做完了卻無法真的儲存資料。這次先做最技術風險最高、最需要獨立驗證的一塊：EF Core 持久化，以及座位鎖定在真實併發下的悲觀鎖機制。API 層（後台管理、買家端、後台訂單查看、背景清理）刻意拆到下一個 change，等這裡的持久化與鎖定機制先驗證穩定，再疊上去。

## What Changes

- 新增 EF Core 持久化：`Venue`、`SeatMap`、`Seat`、`Event`、`EventSeat`、`TicketType`、`Order`、`OrderItem` 八個 Entity 的資料庫對應與 migration。Domain 不新增任何業務欄位（不含下一個 change 才會加入的 `Order.BuyerId`），但 `Order`／`TicketType` 會新增給 EF Core 物化用的 `private` 建構子，純粹是技術性調整，不開放任何新的 public setter。
- 座位鎖定/售出狀態變更（`EventSeat.Hold`/`ConfirmSold`/`ReleaseHold`）新增資料庫層級的悲觀鎖保證：對目標 `EventSeat` 列使用 `SELECT ... FOR UPDATE`，確保同一時間只有一筆交易能修改特定座位的狀態，取代原本只在應用程式流程內保證循序、非資料庫交易的假設。
- 新增 `IUnitOfWork`，包裝資料庫交易的 Begin/Commit/Rollback；售票 Repository 的寫入會由下一個 change 的協調服務透過 `IUnitOfWork` 開啟交易，並在 `CommitAsync` 時落地（不影響會員系統既有的寫入方式）。
- `ProjectC.Domain` 新增六個 Repository 介面，涵蓋八個售票 Entity 的聚合存取（`Seat`、`OrderItem` 依附各自聚合根，不單獨開介面；Entity 數量超過 CLAUDE.md 直接注入 `DbContext` 的門檻），`ProjectC.Infrastructure` 提供對應實作，目的是供**下一個 change** 的 Application 協調服務使用、移除「呼叫端需自行載入物件」這個限制；**這次 Application 層本身不改用 Repository、不新增協調服務，既有三個 Handler（`CreateOrderHandler`/`ConfirmOrderHandler`/`CancelOrderHandler`）簽章與行為完全不變**。

本次不包含：任何對外 WebApi 端點、`Order.BuyerId`／登入串接、後台管理與買家端 API、後台訂單查看、逾時訂單背景清理——這些留給下一個 change（`ticketing-api`，暫定名稱），建立在這次驗證過的持久化與鎖定機制之上。

## Capabilities

### New Capabilities
（無——這次不新增任何對外可觀察的能力，純粹是持久化與並發控制的技術基礎建設）

### Modified Capabilities
- `seat-reservation`：新增「座位鎖定在資料庫層以悲觀鎖保證並發安全性」的需求，明確化原本只隱含在應用程式流程假設中的並發保證。

## Impact

- 新增 `ProjectC.Infrastructure` 內售票相關的 EF Core 配置、migration、六個 Repository 實作、`IUnitOfWork` 實作。
- `ProjectC.Domain` 新增六個 Repository 介面（`IVenueRepository`、`ISeatMapRepository`、`IEventRepository`、`IEventSeatRepository`、`ITicketTypeRepository`、`IOrderRepository`）。
- `ProjectC.Application.Common.Interfaces` 新增 `IUnitOfWork`。
- 不影響現有的 `CreateOrderHandler`/`ConfirmOrderHandler`/`CancelOrderHandler` 簽章（維持純協調邏輯、不做 I/O）。
- 依 CLAUDE.md 規則，悲觀鎖需要繞過 EF Core 標準查詢改用 Raw SQL（`FromSqlInterpolated`），需在實作前確認理由充分（EF Core 無對應悲觀鎖 API）。
