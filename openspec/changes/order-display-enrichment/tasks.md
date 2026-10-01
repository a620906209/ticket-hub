## 0. 前置條件

- [x] 0.1 確認 `order-pending-actions` 已歸檔並合併至 master（2026-09-30：歸檔 e507342，合併至 master 725cb47）（本 change 的 `buyer-web-ui`／`admin-web-ui` delta 以其歸檔後的主 spec 為基準）；從最新 master 開分支

## 1. Domain / Infrastructure

- [x] 1.1（design.md 決策 1）`IEventRepository` 新增 `GetByIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken)`，`EventRepository` 以單次 `Where(Contains)` 查詢實作（`AsNoTracking`，EF Core 參數化）；空清單時不發出查詢、直接回傳空清單（比照 `IEventSeatRepository.GetByIdsAsync`）；`FakeEventRepository` 同步實作並記錄呼叫次數與傳入的 Id 清單，供 3.x 測試斷言
- [x] 1.2 更新 `tests/ProjectC.WebApi.Tests/TestSupport/OrganizerScopingFaultInjectionWebApplicationFactory.cs` 內實作 `IEventRepository` 的 `MissingEventRepository` decorator：實作 `GetByIdsAsync`，並讓 `MissingEventIds` 中的 Id 從結果中剔除（否則 1.1 後編譯失敗，且 3.2 整合測試無從注入）
- [x] 1.2a 其餘實作 `IEventRepository` 的測試替身一併補上 `GetByIdsAsync`（否則 1.1 後編譯失敗）：`tests/ProjectC.WebApi.Tests/TestSupport/CountingEventRepository.cs`、`tests/ProjectC.Infrastructure.Tests/TestSupport/InterceptingEventRepository.cs`，以及測試檔內的 private 類別——`QueryCacheTtlSafetyNetTests.AlwaysThrowingEventRepository`、`JoinPurchaseQueueHandlerIntegrationTests.GetByIdInterceptingEventRepository`、`OrderServiceQueueModeLinearizationTests.GetByIdInterceptingEventRepository`、`PurchaseQueueAdmissionServiceLeaderElectionTests.BlockingEventRepository`、`PurchaseQueueAdmissionServiceTests.DisablingAfterGetForUpdateEventRepository`、`PurchaseQueueMirrorReconciliationTests.ThrowingForEventEventRepository`／`CancelOnTargetEventGetForUpdateEventRepository`。decorator 類轉呼叫 inner、不改變各自原本的攔截行為；實作前再以 `grep ": IEventRepository"` 確認清單未增加
- [x] 1.2b（design.md 決策 1）`ISeatMapRepository` 新增 `GetSeatsByIdsAsync(IReadOnlyList<Guid> seatIds, CancellationToken)`，`SeatMapRepository` 以 `Seats` 單次 `Where(Contains)`、`AsNoTracking` 實作，不載入 `SeatMap`；空清單時不發出查詢；`FakeSeatMapRepository` 同步實作並記錄呼叫次數與傳入 Id；新增 Infrastructure 整合測試（Testcontainers）：座位圖含多個座位時只回傳要求的 Id、不含同座位圖其他座位，空清單時回傳空清單且不發出查詢（查詢次數測試抓不到「讀取過多列」，此方法的目的正是限制讀取量，須直接驗證）
- [x] 1.2c 讓 3.6、3.7 的呼叫次數斷言與 2.6 的 token 斷言可執行：`FakeEventRepository.GetByIdAsync`、`FakeSeatMapRepository.GetByIdAsync`、`FakeEventSeatRepository.GetByIdsAsync`、`FakeTicketTypeRepository.GetByEventIdAsync` 記錄呼叫次數與最後一次收到的 `CancellationToken`（目前這些 Fake 都沒有計數）；`FakeOrderRepository` 的 `GetByIdAsync`／`GetByBuyerIdAsync`／`GetByOrganizerIdAsync` 與 `FakeTicketRepository.GetByOrderItemIdsAsync` 同樣記錄 token（2.6 要涵蓋 Handler 的訂單查詢與票券查詢）；1.1、1.2b、1.6 新增的 Fake 方法同樣記錄 token，寫法一致
- [x] 1.3 新增整合測試用的 EF Core `DbCommandInterceptor`（計算 `ReaderExecuting` 次數，並保留每次的 `CommandText`，可依 SQL 中出現的帶引號資料表名稱（如 `"Orders"`、`"Tickets"`、`"Events"`、`"TicketTypes"`、`"EventSeats"`、`"Seats"`；以引號比對避免 `"Orders"` 誤中 `"OrderItems"`）產出「資料表 → 查詢次數」對照表），可在測試用 `WebApplicationFactory` 註冊並依請求讀取／重設，供 3.6a、3.9a、4.1a 使用
- [x] 1.4（design.md 決策 2）擴充 `OrganizerScopingFaultInjectionWebApplicationFactory`：比照既有 `MissingEventRepository` decorator，新增 `MissingEventSeatIds`（包裝 `IEventSeatRepository`，`GetByIdsAsync` 結果剔除指定 Id）、`MissingTicketTypeIds`（包裝 `ITicketTypeRepository`，`GetByEventIdAsync` 結果剔除指定 Id）、`MissingSeatIds`（包裝 `ISeatMapRepository`，`GetSeatsByIdsAsync` 結果剔除指定 Id）、`MissingBuyerIds`（包裝 1.6 的 `IMemberDisplayNameReader`，結果剔除指定 Id）；其餘方法一律委派真實實作，集合為空時行為與真實 Repository 相同（真實 DB 有 FK，無法建立這些損毀資料，理由同既有 decorator 註解）
- [x] 1.5（design.md 決策 3）`SeatMapTests` 新增 `AddSeat_WhenZoneCodeOrSeatNumberIsBlank_ThrowsArgumentException`（`[Theory]`：zoneCode 為 null／空字串／空白，seatNumber 同），固定「座位範本不會只缺分區或號碼」的不變式；目前只有重複座位的測試
- [x] 1.6（design.md 決策 1）`ProjectC.Domain/Members` 新增 `IMemberDisplayNameReader`（`GetDisplayNamesByIdsAsync(IReadOnlyList<Guid> memberIds, CancellationToken)` → `IReadOnlyDictionary<Guid, string>`），Infrastructure 以 `Members` 單次 `Where(Contains)`、`AsNoTracking`、只投影 `Id`／`DisplayName` 實作，空清單不發查詢，註冊為 Scoped（持有 DbContext）；Application.Tests 新增 `FakeMemberDisplayNameReader`（記錄呼叫次數與傳入 Id）；新增 Infrastructure 整合測試：只回傳要求的會員、空清單不查詢
- [x] 1.7（design.md 決策 4）Infrastructure 整合測試：`EventRepository.GetByIdsAsync`、`SeatMapRepository.GetSeatsByIdsAsync`、1.6 的 `IMemberDisplayNameReader` 實作，以非空 Id 清單與已取消的 token 呼叫，斷言丟出 `OperationCanceledException`（不回傳空集合）
- [x] 1.8 擴充 `tests/ProjectC.WebApi.Tests/TestSupport/RecordingLogger<T>`：除等級外另記錄每筆 log 的 `Exception` 與格式化後訊息（既有只記等級的用法不受影響）；`OrganizerScopingFaultInjectionWebApplicationFactory` 以此取代 `ILogger<GlobalExceptionHandler>` 並對外公開，供 3.5a、4.2a 斷言

## 2. Application

- [x] 2.1 `MyOrderSummaryDto` 新增 `EventTitle`；`GetMyOrdersHandler` 在 0 筆訂單時直接回傳空清單，否則對不重複 `EventId` 呼叫一次 `GetByIdsAsync`，任一 `EventId` 查不到時丟出 `InvalidOperationException`，訊息含訂單 Id 與查不到的 `EventId`（design.md 決策 2，由全域例外處理轉 500）
- [x] 2.2 `MyOrderDetailDto` 新增 `EventTitle`；`MyOrderItemDto` 新增 `SeatZoneCode`、`SeatNumber`、`TicketTypeName`（皆 nullable）
- [x] 2.3 `GetMyOrderDetailHandler`：本人／存在檢查維持在最前面；通過後依 design.md 決策 1 以 `IEventRepository.GetByIdAsync`、`ITicketTypeRepository.GetByEventIdAsync` 組裝；僅在有座位項目時呼叫 `IEventSeatRepository.GetByIdsAsync` 與 `ISeatMapRepository.GetSeatsByIdsAsync`（不呼叫 `ISeatMapRepository.GetByIdAsync`）；決策 2 列出的查不到情形一律丟 `InvalidOperationException`，訊息含訂單 Id 與查不到的關聯 Id；`EventSeatId`／`TicketTypeId` 為 null 時對應欄位回傳 null
- [x] 2.4 `OrderSummaryDto` 新增 `BuyerDisplayName`；`GetOrdersHandler` 注入 1.6 的 `IMemberDisplayNameReader`，對不重複 `BuyerId` 呼叫一次；查不到時不回 null（與 `GetAdminEventsHandler` 對可為 null 的 `CreatedByMemberId` 處理不同）——任一 `BuyerId` 查不到時丟 `InvalidOperationException`，訊息含訂單 Id 與 `BuyerId`；確認回應中不含 Email
- [x] 2.5 所有新增的非同步呼叫皆傳遞 `CancellationToken`；2.1、2.3、2.4 的批次查詢皆先對 Id `Distinct()`，再以結果 Id 建 `HashSet` 逐一比對缺失（design.md 決策 4，不以筆數比較）；不 catch 技術例外與 `OperationCanceledException`、不建立新的 `CancellationTokenSource`
- [x] 2.6（design.md 決策 4）Handler 單元測試：`GetMyOrdersHandler`、`GetMyOrderDetailHandler`（含座位項目的訂單，涵蓋全部顯示資訊查詢）、`GetOrdersHandler` 以一個特定 `CancellationToken` 呼叫，斷言每個被呼叫的 Fake 方法（含訂單查詢與 `Tickets` 查詢，見 1.2c）收到的都是同一個 token；另一案例 Fake 丟出 `OperationCanceledException` 時，Handler 原樣往外拋，不回傳空結果、`Result.Failure` 或 `InvalidOperationException`
- [x] 2.7 單元測試：訂單內重複的 `EventId`／`EventSeatId`／`BuyerId` 傳給 Fake 時已去重；以可覆寫回傳值的 stub（既有 Fake 依傳入 Id 過濾，做不出此回傳；可在該 Fake 加上測試用的回傳覆寫，或於測試內以 decorator 包裝）讓回傳筆數與需求相同但缺少其中一個 Id（多回一個無關 Id）時，仍丟 `InvalidOperationException` 並指出缺少的 Id

## 3. 後端測試 — buyer-order-query

測試類型：2.x 的單元測試（xUnit + 既有 `TestSupport` Fake，不碰 DB）；整合測試（`WebApplicationFactory` + Testcontainers）驗證 500 回應與序列化欄位。

- [x] 3.1 [BOQ-LIST-TITLE-001] `GetMyOrdersHandlerTests`：會員於兩個活動各有訂單，每筆 `EventTitle` 正確；並斷言 `FakeEventRepository.GetByIdsAsync` 只被呼叫一次、傳入不重複的兩個 Id（驗證不隨訂單筆數成長）
- [x] 3.2 [BOQ-LIST-TITLE-002] 單元測試：`EventId` 查不到時丟 `InvalidOperationException`，且訊息包含該訂單 Id 與 `EventId`；整合測試：比照 `OrganizerScopingDataCorruptionTests` 以 `MissingEventIds` 讓 `GET /api/orders` 回 500，body 不含訂單資料
- [x] 3.3 [BOQ-DETAIL-DISPLAY-001] `GetMyOrderDetailHandlerTests`：混合座位項目與計數項目，斷言 `EventTitle`、座位項目的 `SeatZoneCode`／`SeatNumber`／`TicketTypeName`、計數項目兩個座位欄位為 null 且 `TicketTypeName` 正確；整合測試：`GET /api/orders/{id}` 回應 JSON 包含上述欄位名稱（驗證序列化）
- [x] 3.4 [BOQ-DETAIL-DISPLAY-002] 單元測試：項目 `TicketTypeId` 為 null 時 `TicketTypeName` 為 null，其餘欄位正常、不丟例外
- [x] 3.5 [BOQ-DETAIL-DISPLAY-003] 單元測試四個案例，皆斷言丟 `InvalidOperationException` 且訊息包含訂單 Id 與查不到的關聯 Id：訂單 `EventId` 查不到、`EventSeatId` 查不到、`TicketTypeId` 查不到、`EventSeat.SeatId` 對應座位範本查不到
- [x] 3.5a [BOQ-DETAIL-DISPLAY-003] 整合測試（`OrganizerScopingDataCorruptionTests`，`[Theory]` 四個案例）：seed 一筆含座位項目與計數項目的訂單，分別以 `MissingEventIds`、`MissingEventSeatIds`、`MissingTicketTypeIds`、`MissingSeatIds`（1.4）注入四種損毀，呼叫 `GET /api/orders/{id}`；每案例斷言 500、以既有 `AssertGlobalExceptionProblemDetailsAsync` 證明來自全域例外處理、body 不含 `items`／`eventTitle`，且不含被注入的 Id、訂單 Id 與例外訊息文字（ProblemDetails 不洩漏內部錯誤）；並以 1.8 的 logger 斷言該請求記錄了一筆 `Error` 等級 log，附帶的例外為 `InvalidOperationException`，其訊息包含訂單 Id 與被注入的關聯 Id，log 訊息內的 TraceId 與回應的 `traceId` 相同（大聲失敗、可定位、不外洩三者同時成立）
- [x] 3.6 [BOQ-DETAIL-DISPLAY-004] 單元測試：分別以 1 個與 5 個座位項目的訂單呼叫，斷言各 Fake repository 方法的呼叫次數兩次相同且皆為 1（`IEventSeatRepository.GetByIdsAsync`、`ISeatMapRepository.GetSeatsByIdsAsync` 皆以單次呼叫傳入全部 Id），且 `ISeatMapRepository.GetByIdAsync` 未被呼叫；另一案例只有計數項目時，兩個座位查詢皆未被呼叫
- [x] 3.6a [BOQ-DETAIL-DISPLAY-004] 整合測試（使用 1.3 的 interceptor，暖機方式比照 4.1a；單元層無法驗證 repository 實作內部不逐筆查詢）三組，每組比較「1 個項目」與「5 個項目」兩筆訂單的明細請求（混合組成至少須 1 座位＋1 計數，改以「2 個項目」對「5 個項目」比較）。每組皆以「資料表 → 查詢次數」對照表斷言（不只比總數，避免某表減少、另一表增加而總數剛好相同）：
  - 兩次請求的對照表完全相同（每一張出現的資料表次數都一樣，包含身分驗證等與訂單無關的固定查詢）
  - 白名單：請求中出現的資料表只能是 spec 列出的 `Orders`、`OrderItems`、`Tickets`、`Events`、`TicketTypes`、`EventSeats`、`Seats`，加上以暖機請求實測列出的身分驗證固定查詢表（實作時寫成明確清單，例如 `Members`，不以「訂單無關」模糊判斷）；出現任何不在白名單的資料表即失敗，特別是 `SeatMaps`（誤用 `ISeatMapRepository.GetByIdAsync` 會載入整張座位圖）。各表次數不超過 spec 上限（`OrderItems` 隨 `Orders` 的 join 出現，不另計）
  - 純座位：`EventSeats`、`Seats` 各 1 次
  - 純計數：`EventSeats`、`Seats` 皆 0 次
  - 混合：`EventSeats`、`Seats` 各 1 次
- [x] 3.7 既有 [BOQ-DETAIL-003]、[BOQ-DETAIL-004] 測試：補斷言在 403／404 路徑下不呼叫任何顯示資訊查詢（本人／存在檢查先於顯示資訊查詢，spec 明訂）
- [x] 3.8 MODIFIED 後文字未變、但因 Requirement 整段改寫而重新納入的既有 scenario，逐條對應測試（既有測試因建構子或 seed 不足失敗時補 seed 活動、座位圖、票種，不得刪除或放寬既有斷言）：
  - [BOQ-LIST-001 查詢自己的訂單列表] `GetMyOrdersHandlerTests.HandleAsync_WhenBuyerHasOrders_ReturnsOnlyThatBuyersOrderSummaries`：seed 買家 A 兩筆、買家 B 一筆訂單（各自活動已 seed），以 A 呼叫；斷言只回 A 的兩筆且不含 B 的訂單 Id，並補斷言每筆 `EventTitle`；整合層 `OrdersControllerTests.GetMyOrdersAndDetail_WhenBuyerOwnsConfirmedOrder_Returns200` 維持
  - [BOQ-LIST-002 尚未有任何訂單] `GetMyOrdersHandlerTests.HandleAsync_WhenBuyerHasNoOrders_ReturnsEmptyList`：無訂單的買家呼叫，斷言回傳空清單（3.9 另補不呼叫 `GetByIdsAsync`）
  - [BOQ-DETAIL-001 查詢自己的訂單明細（已出票）] `GetMyOrderDetailHandlerTests.HandleAsync_WhenBuyerOwnsPaidOrderWithIssuedTickets_ReturnsItemsWithTicketStatuses`：seed 本人 Paid 訂單與 Issued 票券；斷言訂單狀態 `Paid`、每筆項目的票券 Id 與狀態 `Issued`，並補斷言 `EventTitle`
  - [BOQ-DETAIL-002 查詢自己尚未確認付款的訂單明細] `GetMyOrderDetailHandlerTests.HandleAsync_WhenBuyerOwnsPendingOrderWithoutTickets_ReturnsEmptyTicketList`：seed 本人 Pending 訂單、無票券；斷言狀態 `Pending`、每筆項目票券清單為空、結果為 Success
  - [BOQ-DETAIL-003 非本人查詢他人訂單明細] `GetMyOrderDetailHandlerTests.HandleAsync_WhenCallerDoesNotOwnOrder_ReturnsForbidden`：以非買家 Id 查詢；斷言 `ErrorType.Forbidden`（3.7 補斷言未呼叫顯示資訊查詢）；新增整合測試 `OrdersControllerTests.GetMyOrderDetail_ByNonBuyer_Returns403`：斷言 403 且 body 不含 `items`／`eventTitle`
  - [BOQ-DETAIL-004 查詢不存在的訂單] `GetMyOrderDetailHandlerTests.HandleAsync_WhenOrderDoesNotExist_ReturnsNotFound`：以不存在的 Id 查詢；斷言 `ErrorType.NotFound`（3.7 同上）；新增整合測試 `OrdersControllerTests.GetMyOrderDetail_WithNonExistentOrder_Returns404`
- [x] 3.9 [BOQ-LIST-002 尚未有任何訂單] 補斷言：0 筆訂單時回傳空清單，且 `GetMyOrdersHandler` 不呼叫 `GetByIdsAsync`（1.1 的「空清單不查詢」為 repository 層的額外防線）
- [x] 3.9a [BOQ-LIST-TITLE-003] 整合測試（使用 1.3 的 interceptor，暖機方式比照 4.1a）：兩位買家分別擁有「1 筆訂單／1 個活動」與「3 筆訂單／3 個不同活動」，各呼叫一次 `GET /api/orders`，斷言兩次請求的查詢次數相同

## 4. 後端測試 — order-administration

- [x] 4.1 [ORD-LIST-002] `GetOrdersHandlerTests`（以 1.6 的 `FakeMemberDisplayNameReader` seed 會員名稱）：兩位不同買家，`BuyerDisplayName` 正確；整合測試 `AdminOrdersControllerTests`：`GET /api/admin/orders` 回應 JSON 含 `buyerDisplayName`、不含任何 `email` 欄位
- [x] 4.1a [ORD-LIST-004] 整合測試（使用 1.3 的 interceptor）：分別 seed「1 筆訂單／1 位買家」與「3 筆訂單／3 位不同買家」（兩組資料放在兩個不同 Organizer 名下，各自以同一種方式建立、已登入並切換的 client 呼叫，且每次計數前先以同一 client 暖機一次，排除授權／會員解析等固定成本查詢的差異），各呼叫一次 `GET /api/admin/orders`，斷言兩次請求的查詢次數相同。單元層的 MockQueryable `DbSet` 無法區分單次 `Contains` 與逐筆查詢，故此保證只在整合層驗證
- [x] 4.2 [ORD-LIST-003] 單元測試：`BuyerId` 查不到時丟 `InvalidOperationException`，且訊息包含該訂單 Id 與 `BuyerId`
- [x] 4.2a [ORD-LIST-003] 整合測試（`OrganizerScopingDataCorruptionTests`）：seed 兩位買家各一筆訂單，以 1.4 的 `MissingBuyerIds` 注入其中一位，已切換 Organizer 的 client 呼叫 `GET /api/admin/orders`；斷言 500、以 `AssertGlobalExceptionProblemDetailsAsync` 證明經全域例外處理、body 不含 `buyerDisplayName`／`buyerId`／任一訂單 Id（不回傳部分列表），且不含被注入的 `BuyerId` 與例外訊息文字；log 斷言同 3.5a（`Error` 等級、例外訊息含訂單 Id 與 `BuyerId`、TraceId 與回應一致）
- [x] 4.3 [ORD-LIST-001] `GetOrdersHandlerTests.HandleAsync_ReturnsCallerOrganizerOrdersWithLiveStatus`（單元）與 `AdminOrdersControllerTests.GetOrders_ReturnsOnlyCallerOrganizerOrders`（整合）：seed Organizer A、B 名下活動各一筆訂單，以 A 的 context 呼叫；斷言只回 A 的訂單、不含 B 的訂單 Id，並補斷言 `BuyerDisplayName`；因建構子變更失敗時補 seed 會員，不得放寬既有斷言

## 5. 前端

- [x] 5.1 更新 `types/apiResponses.ts`：`MyOrderSummary.eventTitle`、`MyOrderDetail.eventTitle`、`MyOrderItem.seatZoneCode`／`seatNumber`／`ticketTypeName`、`OrderSummary.buyerDisplayName`
- [x] 5.2 `OrderResultPage.vue`：顯示活動名稱
- [x] 5.3 `OrderDetailPage.vue`：顯示活動名稱、每筆項目的票種名稱與座位標示「{分區}-{座位號碼}」（null 顯示「—」）
- [x] 5.4 `MyOrdersPage.vue`：新增活動名稱欄
- [x] 5.5 `AdminOrderListPage.vue`：買家欄改顯示 `buyerDisplayName`，移除買家 GUID 欄

## 6. 前端測試（vitest，mock API 層）

- [x] 6.1 `OrderResultPage.test.ts`：[BW-RESULT-002] 補斷言顯示 mock 的活動名稱；另一案例 `eventTitle` 為 `<img src=x onerror=alert(1)>`，斷言原樣以文字顯示且頁面不存在 `img` 元素
- [x] 6.1a 結果頁 Requirement 整段改寫而重新納入的 scenario，逐條對應 `OrderResultPage.test.ts` 既有測試（不得刪除或放寬斷言，明細 mock 補 `eventTitle` 欄位）：[BW-RESULT-001] 明細回 Paid → 顯示「已付款」、無操作按鈕、無「保留至」；[BW-RESULT-003] `it.each` Expired／Cancelled → 對應中文狀態、失敗訊息於重新查詢後顯示、無操作；[BW-RESULT-004] 確認付款 → `confirmOrder` 一次、成功提示、明細查詢 2 次、顯示「已付款」；[BW-RESULT-005] 取消先確認、選「保留訂單」不呼叫 API；[BW-RESULT-006] 409 仍 Pending → 失敗訊息、保留至、按鈕可再操作；[BW-RESULT-007] `it.each` 不存在 GUID／非 GUID → 404 提示與返回連結；[BW-RESULT-008] 403 → 無權限提示；[BW-RESULT-009] 重新查詢失敗 → 錯誤、重新整理、無操作與保留至；[BW-RESULT-010] 切換訂單 → 舊回應不覆蓋
- [x] 6.2 `OrderDetailPage.test.ts`：[BW-MYORDER-DISPLAY-001] 座位項目顯示「A」「A-12」，計數項目顯示「站票」「—」；另一案例 `ticketTypeName` 為 null 時顯示「—」；[BW-MYORDER-DETAIL-001 開啟訂單明細頁查看已出票訂單] 補斷言活動名稱與項目資訊；另一案例 `eventTitle`、`ticketTypeName`、`seatZoneCode` 皆含 HTML 標籤，斷言原樣以文字顯示且頁面不存在對應元素
- [x] 6.2a 「我的訂單」明細相關的重新納入 scenario，逐條對應 `OrderDetailPage.test.ts` 既有測試（mock 補新欄位，不得刪除或放寬斷言）：[BW-MYORDER-DETAIL-001 開啟訂單明細頁查看已出票訂單] Paid＋Issued → 「已付款」「已出票」「查看 QR Code」、無保留至；[BW-MYORDER-QR-001 點選查看 QR Code] 以票券 Id 取 Blob、Object URL 顯示並於切換／卸載釋放；[BW-MYORDER-DETAIL-002 開啟尚未出票訂單的明細頁] Pending 無票 → 「待付款」、保留至、「尚未出票」、無 QR 操作；[BW-MYORDER-NOTFOUND-001 直接以網址開啟不存在的訂單明細頁] 404 → 找不到提示與返回連結；[BW-MYORDER-FORBIDDEN-001 直接以網址開啟非本人的訂單明細頁] 403 → 無權限提示；[BW-MYORDER-INVALID-ID-001] 非 GUID 以原值查詢、依 404 處理；[BW-MYORDER-DISPLAY-002] 未知訂單／票券狀態原樣顯示為中性色
- [x] 6.3 `MyOrdersPage.test.ts`：[BW-MYORDER-LIST-001 開啟我的訂單列表頁] 既有測試（四種狀態中文標籤、僅 Pending 顯示保留至、空清單提示）mock 補 `eventTitle` 並補斷言每筆活動名稱；另一案例 `eventTitle` 含 HTML 標籤，斷言原樣以文字顯示且表格內不存在對應元素
- [x] 6.4 `AdminOrderListPage.test.ts`：[AWU-ORDER-LIST-001] 補斷言顯示買家顯示名稱、畫面不含 mock 的買家 GUID；另一案例 `buyerDisplayName` 為 `<img src=x onerror=alert(1)>`，斷言畫面文字原樣包含該字串且表格內不存在 `img` 元素（design.md 安全確認「前端」）
- [x] 6.4a `AdminOrderDetailPage.test.ts`／`AdminOrderListPage.test.ts` 重新納入的 scenario（mock 補新欄位，不得刪除或放寬斷言）：[AWU-ORDER-DETAIL-001] 既有「混合座位項目與計數項目」測試（座位 Id、「計數票」、數量、貨幣格式）加上 scenario 標記；[AWU-ORDER-HOLD-001] 列表與詳情既有測試：Pending 顯示持有到期時間、Paid 不顯示

## 7. 驗證與收尾

- [x] 7.1 容器內執行全部後端測試（`docker compose exec api dotnet test`）與前端 `npm run test`、`npm run lint`、`vue-tsc`
- [x] 7.2 以 claude-in-chrome 實機驗證：混合座位與計數票的訂單明細顯示正確；後台訂單列表顯示買家名稱
- [x] 7.3 套用 hardener 檢查清單於 2.1–2.4 變更的 Handler，再呼叫 strict-reviewer
- [ ] 7.4 歸檔時同步主 spec，並更新 `docs/project-scope.md` 第 8 節「已知前端缺口」④ 與第 9 節快照註記為已完成
