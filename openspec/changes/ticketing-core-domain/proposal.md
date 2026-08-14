## Why

售票平台最容易出錯、也最需要先想清楚的部分是「選位到付款完成前的座位鎖定」——這條規則沒設計好，之後接 API、接付款都會建立在錯誤的基礎上。因此第一階段先只做核心 Domain / Application 邏輯，把活動、票種、座位、訂單的不變條件與座位鎖定機制用單元測試釘死，之後接 Infrastructure（EF Core）與 WebApi 只是機械性的串接工作。

**[2026-08-15 更新]** 本 change 開始時，專案的確只有空的 Clean Architecture 骨架；但實作過程中，另一條已完成的 `feature/membership-system`（會員/JWT 系統，含完整 Infrastructure/WebApi 實作與 Docker 環境）被合併回 `master`，所以此時 `master` 已不再是空骨架。這不影響本 change 的範圍——售票平台的 Domain/Application 仍是全新增量，與會員系統是不同的業務領域——但下方「Impact」一節原本「Infrastructure/WebApi 不受影響、維持空殼」的敘述已經過時，予以更正。

## What Changes

- 新增 `Event`、`Venue`、`SeatMap`、`Seat`（座位樣板）、`TicketType` 等 Domain Entity，描述一場需要選位的表演活動與其座位圖
- 新增 `EventSeat`：**每場活動專屬的可售座位庫存**，建立活動時依場地座位圖為每個座位產生一筆 `EventSeat`；座位鎖定/售出狀態掛在 `EventSeat` 上，不掛在共用的 `Seat` 樣板上，避免同一場地兩場活動誤共用庫存
- 新增座位狀態機（於 `EventSeat`）：可售（Available）→ 暫扣（Held）→ 已售出（Sold），Sold 為明確標記（不可被時間推導覆蓋），Held 由 `HeldByOrderId` + `HeldUntilUtc` 搭配目前時間推導，對外一律透過 `GetStatus(now)` 存取
- 新增 `Order`、`OrderItem` Entity，代表買家選定座位後建立的訂單，狀態機：建立中（Pending）→ 已確認（Confirmed）/ 已取消（Cancelled）/ 已逾時（Expired）；**整筆訂單只有一個到期時間**（`Order.HeldUntilUtc`），不逐座位判斷逾時；`OrderItem` 建立時快照 `TicketType` 當下票價
- 新增 Application 層的訂單協調邏輯（`CreateOrderHandler` / `ConfirmOrderHandler` / `CancelOrderHandler`）：先產生 `OrderId` 再逐一鎖定所選 `EventSeat`，任一鎖定失敗則復原本次已鎖定的座位、不建立訂單；此邏輯需要查詢多個 Entity，故放在 Application 而非 Domain。本階段的「原子性」僅為記憶體內操作順序保證，非資料庫交易
- 業務失敗回傳契約：Domain 的守衛方法（不變條件違反，如「座位已被鎖定」）拋領域例外；Application 的 Handler 對外一律攔截並轉譯為 `Result<T>`，不讓例外做流程控制
- 不包含：EF Core 實作（Infrastructure）、Controller/API（WebApi）、付款串接、主辦方後台 UI／權限、買家身分（`Order` 暫不含 `BuyerId`）、暫扣續命（Extend）。這些留待後續 change

## Capabilities

### New Capabilities
- `event-catalog`：活動、場地座位圖、票種的資料模型與建立規則；建立活動時同步建立該活動專屬的 `EventSeat` 庫存（不含後台 CRUD API，僅 Domain 層規則）
- `seat-reservation`：`EventSeat` 的鎖定（暫扣）、逾時釋放、售出確認、防止重複鎖定的核心不變條件
- `ticket-ordering`：訂單建立與狀態機，依賴 `seat-reservation` 完成 `EventSeat` 鎖定後才能建立/確認訂單

### Modified Capabilities
（無，`event-catalog`／`seat-reservation`／`ticket-ordering` 皆為全新 capability，不修改既有的 `authentication`／`member-management` spec）

## Impact

- **受影響專案**：`src/ProjectC.Domain`（新增 Entity 與狀態機）、`src/ProjectC.Application`（新增座位鎖定/訂單協調邏輯）、`tests/ProjectC.Domain.Tests`、`tests/ProjectC.Application.Tests`（新增對應單元測試）
- **不受影響**：`src/ProjectC.WebApi`（此 change 不新增 Controller/端點）；`src/ProjectC.Infrastructure` 就本 change 新增的售票網域而言不受影響（沒有新增 EF Core 設定/Migration），但該專案本身已因合併 `feature/membership-system` 而含有會員系統的持久化實作，不再是空殼
- **依賴**：`ProjectC.Domain`／`ProjectC.Application` 未新增套件；`Result`/`Error`/`IDateTimeProvider` 沿用 `feature/membership-system` 已建立、被 53 個檔案使用的既有型別，不重複定義（見 design.md Decision 7、12）
