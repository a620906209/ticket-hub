## 1. 前置調查

- [ ] 1.1 確認 EF Core 既有 `IEventSeatRepository.GetForUpdateAsync` 的實作方式（`FromSqlInterpolated` + `FOR UPDATE`，或既有慣例），作為新增 `ITicketTypeRepository.GetForUpdateAsync` 的參考範本

## 2. Domain 層：TicketType 支援 RequiresSeat

- [ ] 2.1 `TicketType` 新增 `RequiresSeat`（bool）、`AvailableQuantity`（int?）屬性
- [ ] 2.2 建構邏輯依 `RequiresSeat` 分流驗證（`true`：`ZoneCode` 須存在於座位圖分區、`AvailableQuantity` 須為 null；`false`：`ZoneCode` 免驗證分區存在性、`AvailableQuantity` 須為正整數），對應 design.md 決策 1
- [ ] 2.3 新增 `Reserve(int quantity)` 方法：`AvailableQuantity` 不足時拋 `DomainException`，成功則扣減；新增 `Release(int quantity)` 方法：無條件歸還，對應 design.md 決策 3
- [ ] 2.4 `Event.CreateTicketType` 簽章擴充以支援兩種模式的建立參數（或新增對應的計數模式建立方法），維持既有座位模式呼叫端不需大幅修改
- [ ] 2.5 xUnit 單元測試：`TicketType` 建構子在兩種模式下的驗證規則（對應 event-management spec 新增的「建立純計數票種成功／未提供可售總量／綁座位票種提供可售總量」三個 Scenario）
- [ ] 2.6 xUnit 單元測試：`Reserve`／`Release` 的邊界情況（庫存剛好足夠、不足、歸還後回到原數量）

## 3. Domain 層：OrderItem 支援計數行項

- [ ] 3.1 `OrderItem` 新增 `TicketTypeId`（domain 建構子層要求必填，DB 欄位維持 nullable）、`Quantity`（必填）屬性，`EventSeatId` 改為可為 null
- [ ] 3.2 建構邏輯驗證兩種形狀互斥：座位行項（`EventSeatId` 有值、`Quantity = 1`）、計數行項（`EventSeatId = null`、`Quantity >= 1`），對應 design.md 決策 2
- [ ] 3.3 xUnit 單元測試：兩種合法形狀建構成功、非法組合（例如同時有 `EventSeatId` 又 `Quantity > 1`，或兩者皆空）被拒絕

## 4. Infrastructure：Migration 與 Repository

- [ ] 4.1 EF Core migration：`TicketType` 新增 `RequiresSeat`（`NOT NULL DEFAULT true`）、`AvailableQuantity`（nullable）
- [ ] 4.2 EF Core migration：`OrderItem` 新增 `TicketTypeId`（nullable，既有資料不回填，見 design.md Migration Plan）、`Quantity`（`NOT NULL DEFAULT 1`）、`EventSeatId` 改 nullable
- [ ] 4.3 `TicketTypeConfiguration`／`OrderItemConfiguration`（若無則新增）同步反映上述欄位與 nullable 設定
- [ ] 4.4 `ITicketTypeRepository` 新增 `GetForUpdateAsync(IReadOnlyList<Guid> ticketTypeIds, CancellationToken)`——**MUST 逐字比照 `IEventSeatRepository.GetForUpdateAsync` 的三個關鍵屬性**（外部審查抓到，design.md 決策 3 已修正原本「永遠單列」的錯誤前提）：(1) 接受一組 ID 而非單一 ID；(2) 方法內部自行 `Distinct()` 去重，不信任呼叫端；(3) 用單一 `FromSqlInterpolated` 陳述式搭配 `ORDER BY "Id" FOR UPDATE`，不得逐筆迴圈個別鎖定。同時比照既有方法在無進行中交易時 fail fast（`_dbContext.Database.CurrentTransaction is null` 檢查）
- [ ] 4.5 **`TicketTypeRepository.GetByIdAsync` 加上 `.AsNoTracking()`**（外部審查抓到的阻斷問題，design.md 決策 3 修正段落）：這是目前 `OrderService.PlaceOrderAsync` 交易前存在性檢查的唯一呼叫端，若不改為 no-tracking，交易內 `GetForUpdateAsync` 對同一主鍵的查詢會被 EF Core identity resolution 擋下、回傳鎖前的舊追蹤物件，`Reserve()` 會依舊值誤判庫存足夠，實質等於沒有鎖——這個修正是本次規劃第四輪才發現的根本問題，比先前「不可誤用 ticketTypesById」的說法更關鍵，沒有這個修正光靠「呼叫哪個變數」是無法保證正確性的

## 5. Application 層：建立票種

- [ ] 5.1 `CreateTicketTypeRequest` 新增 `RequiresSeat`（bool，**record 主建構子宣告預設值 `= true`**，見 design.md 決策 1 API 相容性段落）、`AvailableQuantity`（int?）
- [ ] 5.2 `CreateTicketTypeRequestValidator` 依 `RequiresSeat` 分流驗證 `AvailableQuantity`（`true` 時必須為 null，`false` 時必須為正整數），`ZoneCode`／`Price` 規則不變
- [ ] 5.3 `CreateTicketTypeHandler` 依 `RequiresSeat` 分流：`true` 沿用現行「查座位圖、驗證分區存在」邏輯；`false` 跳過座位圖分區驗證，改呼叫新的計數模式建立邏輯
- [ ] 5.4 FluentValidation／Handler 整合測試：對應 event-management spec 新增與既有的全部 Scenario（分區不存在、票價無效、活動不存在、純計數成功、純計數未提供總量、綁座位提供總量）
- [ ] 5.4a **API 相容性測試（外部審查第四輪抓到的阻斷問題）**：用本次變更前的既有 JSON payload 格式（只有 `zoneCode`／`price`，不含 `requiresSeat`）呼叫建立票種端點，驗證系統視為 `RequiresSeat = true` 並依既有分區驗證規則成功建立，對應 event-management spec 新增的「建立票種時未提供 RequiresSeat（既有客戶端相容）」Scenario——這條測試 MUST 直接用原始 JSON 字串／匿名物件送出請求，不能用已經帶有 `RequiresSeat` 屬性的強型別 Request 物件建構，否則測不出「欄位缺失」這個情境
- [ ] 5.5 `TicketTypeDto`（`src/ProjectC.Application/Tickets/GetTicketTypes/TicketTypeDto.cs`）新增 `RequiresSeat`，`RequiresSeat = false` 時額外附帶 `AvailableQuantity`；`GetTicketTypesHandler` 同步調整建構邏輯，對應 ticket-purchase spec「瀏覽活動與座位可售狀態」修改後的 Requirement（審查第三輪補充，原本的任務規劃遺漏這個買家端查詢端點，呼叫端原本無從得知票種是否需要指定座位）
- [ ] 5.6 xUnit 測試：查詢票種列表時，`RequiresSeat = true`／`false` 的票種都正確帶出 `RequiresSeat`，`false` 的票種額外帶出當下 `AvailableQuantity`

## 6. Application 層：建立訂單支援計數項目

- [ ] 6.1 `PlaceOrderSelectionRequest` 由 `(Guid EventSeatId, Guid TicketTypeId)` 擴充為 `(Guid? EventSeatId, Guid TicketTypeId, int Quantity)`，**`Quantity` 在 record 主建構子宣告預設值 `= 1`**（對應 design.md 決策 4 API 相容性段落，外部審查第四輪抓到的阻斷問題）
- [ ] 6.2 `PlaceOrderRequestValidator` 新增結構層驗證：`Quantity >= 1`、`TicketTypeId` 不可為空；並修正既有「座位不可重複選取」規則（`Distinct().Count() == selections.Count`）——`EventSeatId` 改為可為 null 後，需先過濾掉 null 再比對，否則兩筆不同計數項目（皆為 null）會被誤判成重複選位（design.md 決策 4 審查後補充）；**新增「計數項目的 `TicketTypeId` 不可重複」規則**：對 `EventSeatId = null` 的選購項目，`TicketTypeId` 之間 MUST 互不重複，拒絕、不自動合併（審查第三輪補充，見 design.md 決策 4）
- [ ] 6.3 `SeatSelection` 新增或並存一個計數版 DTO（例如 `QuantitySelection(TicketType TicketType, int Quantity)`），供 `CreateOrderHandler` 處理
- [ ] 6.4 `OrderService.PlaceOrderAsync`：載入 `TicketType` 後，依 `RequiresSeat` 與請求是否帶 `EventSeatId` 交叉驗證一致性（純計數票種指定了座位／綁座位票種未指定座位／座位項目指定非 1 的 `Quantity` 皆 MUST 拒絕），對應 ticket-purchase spec 新增的三個 Scenario（審查後補充座位項目 `Quantity` 檢查，`SeatSelection` 目前沒有 `Quantity` 欄位，缺這個檢查會讓非法的 `Quantity` 值被靜默忽略）
- [ ] 6.5 `OrderService.PlaceOrderAsync`：新增每筆訂單限購張數檢查，依 `Quantity` 加總（座位項目固定 1）與 `Event.MaxTicketsPerOrder` 比較，對應 ticket-purchase spec 新增的「建立訂單時每筆訂單限購張數以購買數量加總計算」Requirement；**同時修正現行第 86 行 `eventSeats[0].EventId` 取得活動的寫法**——純計數訂單 `eventSeats` 可能是空清單，索引會拋例外，改用選購項目對應的 `TicketType.EventId`（`ticketTypesById` 已先載入）取得活動（審查後補充，見 design.md Risks）
- [ ] 6.6 `OrderService.PlaceOrderAsync`：現行第 75 行無條件呼叫 `IEventSeatRepository.GetForUpdateAsync(eventSeatIds, ...)` 需加防呆——純計數訂單過濾 null 後 `eventSeatIds` 為空清單，該方法對空清單會拋 `ArgumentException`，改為僅在非空時才呼叫（審查後補充，見 design.md 決策 3）
- [ ] 6.7 `OrderService.PlaceOrderAsync`：計數項目改用 `ITicketTypeRepository.GetForUpdateAsync` 鎖定對應 `TicketType`，交易內完成鎖定；**座位鎖定與票種庫存鎖定 MUST 依 design.md 決策 3 補充的兩層固定順序執行（跨資源：先鎖座位、後鎖票種；同資源：`TicketType` 之間依 `Id` 排序，見 4.4），避免死鎖**；**`TicketType.Reserve()` MUST 只對 `GetForUpdateAsync` 回傳的實例呼叫——前提是 4.5 已把 `ticketTypesById` 的查詢改為 no-tracking，否則就算呼叫「看起來對的」變數，EF Core identity resolution 仍可能讓兩者是同一個帶著舊值的物件（外部審查第四輪抓到，design.md 決策 3 已修正）**
- [ ] 6.8 `CreateOrderHandler.Handle`：擴充處理座位選購與計數選購混合的情況，座位呼叫既有 `EventSeat.Hold`，計數呼叫新增的 `TicketType.Reserve`；任一失敗時，本次已鎖定的座位與已扣減的計數庫存 MUST 全數復原
- [ ] 6.9 xUnit／整合測試：對應 ticket-ordering spec「建立訂單並原子性鎖定座位或扣減票種庫存」新增的全部 Scenario（純計數成功、純計數庫存不足、混合座位與計數項目成功），**須包含「純計數、零座位」訂單的建立測試**，驗證不會因空座位清單拋例外
- [ ] 6.9a **【關鍵】Testcontainers 整合測試，使用兩個獨立的 `DbContext`／`OrderService` instance（模擬兩個真實 HTTP request，不可用同一個 DbContext 模擬）**：`TicketType.AvailableQuantity = 1`，兩個並發請求各自對該票種下單購買 1 張，驗證恰好一筆成功、一筆因庫存不足被拒絕、最終 `AvailableQuantity = 0`——這是驗證 4.5（`AsNoTracking`）修正是否真正生效的唯一方式，單一 DbContext 內的測試無法重現這個競態（外部審查第四輪抓到的阻斷問題，見 design.md 決策 3）
- [ ] 6.9b 整合測試：一筆訂單同時選購兩個不同的計數票種（例如站票 + 停車票），兩個並發交易以相反順序選購（交易 A 先選站票、交易 B 先選停車票），驗證不會逾時或死鎖（對應 4.4 的 `ORDER BY "Id"` 修正，外部審查第四輪抓到）
- [ ] 6.10 xUnit／整合測試：對應 ticket-purchase spec 新增的限購張數四個情境（純座位超限、純計數超限、混合加總超限、混合加總未超限——特別注意最後一項「未超限」是正向案例，容易被跳過，MUST 涵蓋以防限購比較運算子寫錯 `>=`/`>` 沒被任何測試抓到），**須包含純計數（零座位）情境下限購檢查仍正確查得到活動**
- [ ] 6.11 xUnit 測試：座位項目指定非 1 的 `Quantity` MUST 被拒絕，對應 ticket-purchase spec 新增的「座位項目指定非 1 的購買數量」Scenario
- [ ] 6.12 xUnit 測試：同一計數票種在同一次請求中出現兩次以上 MUST 被拒絕，對應 ticket-purchase spec 新增的「同一計數票種在同一次請求中重複出現」Scenario（審查第三輪補充）
- [ ] 6.13 **API 相容性測試（外部審查第四輪抓到的阻斷問題）**：用本次變更前的既有 JSON payload 格式（選購項目只有 `eventSeatId`／`ticketTypeId`，不含 `quantity`）呼叫建立訂單端點，驗證系統視為 `Quantity = 1` 並成功建立訂單，對應 ticket-purchase spec 新增的「座位選購項目未提供購買數量（既有客戶端相容）」Scenario——同 5.4a，MUST 用原始 JSON 送出，不能用已經帶 `Quantity` 屬性的強型別物件

## 7. Application 層：確認訂單與取消訂單支援計數項目

- [ ] 7.1 `OrderService.ChangeOrderStatusAsync`：原本只查、鎖 `EventSeat`（現行第 201 行），改為依 `order.Items` 分流——座位行項照舊鎖 `EventSeat`，計數行項改鎖對應 `TicketType`（`GetForUpdateAsync`），兩者可能在同一筆訂單內同時發生；**座位清單為空時（純計數訂單的確認/取消/逾時清理）不得呼叫 `IEventSeatRepository.GetForUpdateAsync`（空清單會拋 `ArgumentException`，審查後補充）；同時需依 design.md 決策 3 的固定順序先鎖座位、後鎖票種**；**純計數訂單即使是走 Confirm（不寫入 `AvailableQuantity`）也 MUST 鎖 `TicketType`——這是該訂單在資料庫層唯一的序列化點，省略會讓兩個並發 Confirm 都通過、造成重複收款，見 design.md 決策 3 審查後補充的說明**
- [ ] 7.2 `ConfirmOrderHandler.Handle`：座位項目沿用既有驗證與 `ConfirmSold`；計數項目不重複扣減庫存，僅參與訂單狀態/逾時的整體驗證，對應 ticket-ordering spec「確認含計數項目的訂單不重複扣減庫存」Scenario；**同時修正現行第 49 行付款金額計算 `order.Items.Sum(i => i.UnitPrice)` 漏乘 `Quantity` 的問題，改為 `order.Items.Sum(i => i.UnitPrice * i.Quantity)`（審查後補充，見 design.md Risks）**
- [ ] 7.3 `CancelOrderHandler.Handle`：座位項目沿用既有釋放邏輯（含「已被其他訂單合法售出略過」「本訂單自己售出的不一致狀態拒絕」）；計數項目新增無條件呼叫 `TicketType.Release` 歸還數量，對應 ticket-ordering spec「取消訂單」新增/修改的 Scenario
- [ ] 7.4 xUnit 單元測試：`ConfirmOrderHandler`／`CancelOrderHandler` 針對純計數訂單、混合訂單（座位+計數同時存在）的確認與取消行為，**須包含「計數項目 `Quantity > 1`」情境下付款金額正確等於單價乘以數量**
- [ ] 7.5 整合測試：逾時清理背景服務（`OrderService.CancelExpiredOrderAsync` 路徑）對含計數項目的逾時訂單正確歸還庫存，對應 design.md Risks 小節提到的「逾時清理需涵蓋計數行項」；**須包含「純計數、零座位」逾時訂單的清理測試**，驗證不會因空座位清單拋例外
- [ ] 7.6 整合測試：兩個請求並發確認同一筆「純計數、零座位」的 Pending 訂單，驗證只有一次成功、只觸發一次 `IPaymentGateway.ChargeAsync`，對應 ticket-purchase spec「確認與取消訂單的並發一致性」新增的「並發確認同一筆純計數（不含座位）訂單」Scenario（審查後補充，原本的並發測試都只涵蓋含座位的訂單）

## 8. Application 層：訂單查詢明細同步

- [ ] 8.1 `OrderItemDto`（`src/ProjectC.Application/Orders/GetOrderById/OrderDetailDto.cs`）新增 `TicketTypeId`、`Quantity`，`EventSeatId` 改為 `Guid?`，對應 `OrderItem` 的結構變更（審查後補充，原本的任務規劃遺漏此檔案，`GetOrderByIdHandler` 目前的建構呼叫在 `EventSeatId` 改型別後會編譯失敗）
- [ ] 8.2 `GetOrderByIdHandler` 同步調整建構 `OrderItemDto` 的呼叫
- [ ] 8.3 xUnit 測試：查詢含計數項目、含混合項目的訂單明細，回傳的 `OrderItemDto` 正確帶出 `TicketTypeId`／`Quantity`／可為 null 的 `EventSeatId`

## 9. 收尾

- [ ] 9.1 `dotnet test` 全數通過（含本次新增的 Domain／Application 單元測試與整合測試）
- [ ] 9.2 以 Swagger／`dotnet-httpie` 等方式手動驗證後端 API：建立純計數票種 → 查詢票種列表確認回傳 `RequiresSeat=false` 與 `AvailableQuantity` → 以該票種下單（不指定座位，數量 > 1）→ 確認訂單（驗證付款金額為單價乘以數量、庫存不再變動）→ 另建一筆訂單後改用取消（庫存歸還）→ 查詢訂單明細確認回傳正確的 `TicketTypeId`/`Quantity`；並驗證同一訂單混合座位與計數項目的建立/確認/取消（本次無前端改動，不透過 claude-in-chrome 瀏覽器驗證）
- [ ] 9.3 同步確認 `event-management`／`ticket-ordering`／`ticket-purchase` 三份主 spec 的既有 Requirement 已依 delta 正確更新（歸檔時同步）
- [ ] 9.4 `docs/project-scope.md` §8／§9 更新：標記「② TicketType.RequiresSeat 開關」已完成，下一步為「③ 電子票券（Ticket entity）+ 核銷 API」
