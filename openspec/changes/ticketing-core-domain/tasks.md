## 1. 共用基礎

- [x] 1.1 ~~在 `ProjectC.Domain` 定義 `IDateTimeProvider` 介面~~ **[2026-08-15 改為]** 合併 `master`（`feature/membership-system`）後改用其已存在、被 53 個檔案使用的 `ProjectC.Application.Common.Interfaces.IDateTimeProvider`，不重複定義；Domain Entity 方法仍一律直接接收 `DateTime now` 參數，不持有此介面
- [x] 1.2 ~~在測試專案建立假時間提供者~~ **[2026-08-15 改為]** 沿用 `master` 既有的 `tests/ProjectC.Application.Tests/TestSupport/FakeDateTimeProvider.cs`（無參數建構、`UtcNow` 屬性可寫），本次三個 Handler 測試改用物件初始設定式 `new FakeDateTimeProvider { UtcNow = now }`
- [x] 1.3 ~~在 `ProjectC.Application` 定義通用 `Result<T>` 型別~~ **[2026-08-15 改為]** 沿用 `master` 既有、被 53 個檔案使用的 `Result`/`Result<T>`（`Error` + `ErrorType`：Validation/NotFound/Conflict/Unauthorized/Forbidden），三個 Handler 依情境對應 `Error.Validation`／`Error.NotFound`／`Error.Conflict`
- [x] 1.4 在 `ProjectC.Domain` 定義領域例外（例如 `SeatAlreadyHeldException`、`SeatNotHeldException`、`SeatAlreadySoldException`、`OrderAlreadyConfirmedException`），供 Entity 守衛方法拋出

## 2. Event Catalog（活動 / 場地 / 票種 / EventSeat）

- [x] 2.1 建立 `Venue`、`SeatMap` Entity
- [x] 2.2 建立 `Seat` Entity（座位樣板：分區代碼、座位編號，不含任何鎖定/售出狀態），`SeatMap` 新增座位時驗證「分區代碼＋座位編號」組合唯一
- [x] 2.3 建立 `Event` Entity（標題、開始時間、場地、指定使用的 `SeatMap`），內部狀態 `private set`，建立時驗證必要欄位
- [x] 2.4 建立 `EventSeat` Entity（`EventId`、`SeatId`、`SoldByOrderId`、`HeldByOrderId`、`HeldUntilUtc`）；建立活動時依 `SeatMap` 的每個 `Seat` 逐一產生對應的 `EventSeat`，初始狀態 Available，由建立流程本身保證「同一活動內每個座位樣板只對應一筆 `EventSeat`」，不做事後去重
- [x] 2.5 確認 `EventSeat` 為獨立於 `Event` 的 Entity（不做成 `Event.Seats` 之類的整包子集合），操作時以單筆或指定的少量 `EventSeat` 存取，不透過 `Event` 載入
- [x] 2.6 建立 `TicketType` Entity（關聯座位分區代碼、票價），票價須 > 0，且對應分區代碼必須存在於該活動的 `SeatMap`
- [x] 2.7 撰寫 `event-catalog` spec 對應單元測試於 `ProjectC.Domain.Tests`，涵蓋 spec 內所有 Scenario（含「同一座位圖被兩場活動使用時庫存互不影響」「EventSeat 唯一性」）

## 3. Seat Reservation（EventSeat 狀態機）

- [x] 3.1 在 `EventSeat` 實作 `GetStatus(DateTime now)`：Sold（`SoldByOrderId != null`）優先於時間推導；否則依 `HeldByOrderId` + `HeldUntilUtc` + `now` 推導 Held / Available
- [x] 3.2 實作 `EventSeat.IsAvailableForHold(DateTime now)`，結果須與 `GetStatus(now) == Available` 一致
- [x] 3.3 實作 `EventSeat.IsHeldBy(Guid orderId, DateTime now)`：回傳「未售出 且 `HeldByOrderId == orderId` 且 `now < HeldUntilUtc`」，不符合時回傳 false、不拋例外；此方法是 Application 層判斷座位歸屬的唯一入口
- [x] 3.4 實作 `EventSeat.Hold(orderId, heldUntilUtc, now)`：Sold 或未逾時的 Held 狀態下呼叫 MUST 拋領域例外；若目前為 Available（含既有暫扣已逾時的情況），直接覆寫 `HeldByOrderId`／`HeldUntilUtc` 為新的鎖定資訊
- [x] 3.5 實作 `EventSeat.ConfirmSold(orderId, now)`：驗證鎖定訂單一致且暫扣未逾時，成功後設定 `SoldByOrderId` 並清除暫扣欄位
- [x] 3.6 實作 `EventSeat.ReleaseHold(orderId)`：`SoldByOrderId != null` 時 MUST 拋領域例外；`HeldByOrderId != orderId` 時視為無操作（不拋例外、不變更欄位）；`HeldByOrderId == orderId` 時才清除暫扣欄位並轉為 Available
- [x] 3.7 確認 `EventSeat` 內部欄位不對外暴露可直接讀取判斷可售性的成員，僅公開 `GetStatus` / `IsAvailableForHold` / `IsHeldBy` 與上述操作方法
- [x] 3.8 撰寫 `seat-reservation` spec 對應單元測試於 `ProjectC.Domain.Tests`，涵蓋 spec 內所有 Scenario（含「逾時暫扣被新訂單覆寫」「以非持有訂單編號釋放為無操作」「已售出座位釋放拋例外」「`IsHeldBy` 各種不符合情況皆回傳 false」）

## 4. Ticket Ordering（訂單）

- [x] 4.1 建立 `Order` Entity：狀態欄位僅允許 Pending/Confirmed/Cancelled 三值（不可寫入 Expired）、單一 `HeldUntilUtc` 欄位、`GetStatus(DateTime now)` 計算方法（`Status == Pending && now >= HeldUntilUtc` 時回傳 Expired，其餘回傳內部 `Status`）
- [x] 4.2 建立 `OrderItem` Entity：關聯 `EventSeat`（`EventSeatId`，非 `SeatId`），建立時快照 `TicketType.Price` 為自己的 `UnitPrice`
- [x] 4.3 在 `ProjectC.Application` 實作 `CreateOrderHandler`：流程為「產生 `OrderId` → 決定 `heldUntilUtc`（暫扣時長政策，先寫死如 10 分鐘）→ 依序 `EventSeat.Hold(orderId, heldUntilUtc, now)` → 全部成功才建立 `Order`＋`OrderItem`（快照票價，`OrderItem.EventSeatId` 對應鎖定的座位）」；任一 `Hold` 失敗，對本次已成功 `Hold` 的座位呼叫 `ReleaseHold(orderId)`（限定持有者）復原；對外回傳 `Result<OrderId>`
- [x] 4.4 實作 `ConfirmOrderHandler`：先完整驗證（`Order.Status == Pending`、`now < Order.HeldUntilUtc`、每筆 `OrderItem` 對應的 `EventSeat` 呼叫 `IsHeldBy(Order.Id, now)` 為 true、每筆 `OrderItem.EventSeatId` 皆能解析到存在的 `EventSeat`），**驗證一律呼叫 `EventSeat` 的狀態查詢方法，不得直接比較 `HeldByOrderId` 等原始欄位**；全部通過才依序呼叫 `EventSeat.ConfirmSold` 並將 `Order.Status` 設為 Confirmed；任一驗證失敗回傳失敗的 `Result`，不變更任何座位或訂單狀態
- [x] 4.5 實作 `CancelOrderHandler`：接受任何內部 `Status == Pending` 的訂單（不論 `GetStatus(now)` 是 Pending 或 Expired），對訂單內每筆 `OrderItem` 對應的 `EventSeat` 呼叫 `ReleaseHold(order.Id)`（已被其他訂單重新鎖定的座位不釋放），並將 `Order.Status` 設為 Cancelled；`Status == Confirmed` 的訂單回傳失敗 `Result`。此 Handler 同時涵蓋「使用者主動取消」與「查詢後發現已逾時、需要清理」兩種呼叫情境，不另外開 `ExpireOrderHandler`
- [x] 4.6 撰寫 `ticket-ordering` spec 對應單元測試：`Order`／`OrderItem` 單一 Entity 規則（含 `GetStatus` 推導、`Status` 從不被寫入 Expired）於 `ProjectC.Domain.Tests`；三個 Handler 的跨 Entity 協調邏輯（含部分鎖定失敗復原限定 orderId、`ConfirmOrderHandler` 的四項前置驗證、`CancelOrderHandler` 對逾時訂單只釋放仍持有的座位、Result 回傳值）於 `ProjectC.Application.Tests`，涵蓋 spec 內所有 Scenario

## 5. 收尾檢查

- [x] 5.1 確認 `ProjectC.Domain.csproj` 未新增任何 `<ProjectReference>`（維持不依賴其他專案）
- [x] 5.2 確認 Domain 領域例外只在 Domain 內部守衛邏輯使用，未穿透 Application 對外的 `Result<T>` 邊界（三個 Handler 的公開方法簽章不拋出領域例外）
- [x] 5.3 執行全部測試並確認通過。**[2026-08-15 更新]** 合併 `feature/membership-system` 後，`master` 上 `docker-compose.yml` 已存在（`api`/`db` 服務齊全），但本次驗收修復仍延續先前的例外，直接用本機 `dotnet test` 執行（容器未啟動，尚未實際切換）；下次有機會時應改回 `docker compose exec api dotnet test` 驗證，確認容器環境下行為一致。目前全 repo 測試：Domain.Tests 51、Application.Tests 43、WebApi.Tests 23（WebApi.Tests 為會員系統既有測試，非本 change 產出），全數通過
- [x] 5.4 比對 tasks 完成狀況與三份 spec 的 Scenario，皆有對應測試。**[2026-08-15 驗收修正，第一輪]** 驗收時發現並補上的缺口：`CancelOrderHandler` 未攔截 `ReleaseHold` 拋出的 `SeatAlreadySoldException`（可能造成部分釋放與例外穿透）、`Cancel()` 允許對已取消訂單重複取消、`Confirm()`/`Cancel()` 未使用具名領域例外、`CreateOrderHandler` 未驗證座位/票種是否同屬一場活動、未擋重複選取同一座位、`TicketType` 建立時未核對座位圖確實屬於該活動——以上皆已修正並補上對應測試（見 design.md Decision 17-20）。**[2026-08-15 驗收修正，第二輪]** 第一輪修的 `CancelOrderHandler` 預檢邏輯本身跟 Decision 10 的設計意圖矛盾（把「座位已被別人合法賣掉」也當成整單失敗，導致逾時清理永遠卡住）；改為只在「座位已由本訂單自己售出」時才拒絕，「被別人合法售出」則略過該座位、繼續完成取消（新增 `EventSeat.IsSoldBy`，見 design.md Decision 20 第二版）。另外確認 `ConfirmOrderHandler` 的 `ConfirmSold` 迴圈刻意不包 try/catch 是有意的設計決定，非遺漏（見 design.md Decision 21：Sold 不可逆、沒有補償操作，包 try/catch 反而會製造假象）。目前全 repo 測試：Domain.Tests 51、Application.Tests 44、WebApi.Tests 23，全數通過。唯一仍保留的已知缺口：`ticket-ordering` 的「票種事後調整票價不影響既有訂單」情境未被獨立測試覆蓋——因 `TicketType` 本階段未提供改價方法（Price 唯讀），該不變條件目前是由「快照為 decimal、`OrderItem` 不持有 `TicketType` 參照」的結構保證，而非透過實際改價後斷言驗證；待之後若替 `TicketType` 加上改價能力，需補上對應測試
