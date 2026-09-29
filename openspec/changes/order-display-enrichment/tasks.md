## 0. 前置條件

- [ ] 0.1 確認 `order-pending-actions` 已歸檔並合併至 master（本 change 的 `buyer-web-ui`／`admin-web-ui` delta 以其歸檔後的主 spec 為基準）；從最新 master 開分支

## 1. Domain / Infrastructure

- [ ] 1.1（design.md 決策 1）`IEventRepository` 新增 `GetByIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken)`，`EventRepository` 以單次 `Where(Contains)` 查詢實作（`AsNoTracking`，EF Core 參數化）；空清單時不發出查詢、直接回傳空清單（比照 `IEventSeatRepository.GetByIdsAsync`）；`FakeEventRepository` 同步實作並記錄呼叫次數與傳入的 Id 清單，供 3.x 測試斷言
- [ ] 1.2 更新 `tests/ProjectC.WebApi.Tests/TestSupport/OrganizerScopingFaultInjectionWebApplicationFactory.cs` 內實作 `IEventRepository` 的 `MissingEventRepository` decorator：實作 `GetByIdsAsync`，並讓 `MissingEventIds` 中的 Id 從結果中剔除（否則 1.1 後編譯失敗，且 3.2 整合測試無從注入）
- [ ] 1.3 新增整合測試用的 EF Core `DbCommandInterceptor`（計算 `ReaderExecuting` 次數），可在測試用 `WebApplicationFactory` 註冊並依請求讀取／重設計數，供 4.1a 使用

## 2. Application

- [ ] 2.1 `MyOrderSummaryDto` 新增 `EventTitle`；`GetMyOrdersHandler` 在 0 筆訂單時直接回傳空清單，否則對不重複 `EventId` 呼叫一次 `GetByIdsAsync`，任一 `EventId` 查不到時丟出 `InvalidOperationException`（design.md 決策 2，由全域例外處理轉 500）
- [ ] 2.2 `MyOrderDetailDto` 新增 `EventTitle`；`MyOrderItemDto` 新增 `SeatZoneCode`、`SeatNumber`、`TicketTypeName`（皆 nullable）
- [ ] 2.3 `GetMyOrderDetailHandler`：本人／存在檢查維持在最前面；通過後依 design.md 決策 1 以 `IEventRepository.GetByIdAsync`、`ISeatMapRepository.GetByIdAsync`、`IEventSeatRepository.GetByIdsAsync`（僅在有座位項目時呼叫）、`ITicketTypeRepository.GetByEventIdAsync` 組裝；決策 2 列出的查不到情形一律丟 `InvalidOperationException`；`EventSeatId`／`TicketTypeId` 為 null 時對應欄位回傳 null
- [ ] 2.4 `OrderSummaryDto` 新增 `BuyerDisplayName`；`GetOrdersHandler` 注入 `IApplicationDbContext`，以不重複 `BuyerId` 單次查詢 `Members` 並只投影 `Id`、`DisplayName`；任一 `BuyerId` 查不到時丟 `InvalidOperationException`；確認回應中不含 Email
- [ ] 2.5 所有新增的非同步呼叫皆傳遞 `CancellationToken`

## 3. 後端測試 — buyer-order-query

測試類型：2.x 的單元測試（xUnit + 既有 `TestSupport` Fake，不碰 DB）；整合測試（`WebApplicationFactory` + Testcontainers）驗證 500 回應與序列化欄位。

- [ ] 3.1 [BOQ-LIST-TITLE-001] `GetMyOrdersHandlerTests`：會員於兩個活動各有訂單，每筆 `EventTitle` 正確；並斷言 `FakeEventRepository.GetByIdsAsync` 只被呼叫一次、傳入不重複的兩個 Id（驗證不隨訂單筆數成長）
- [ ] 3.2 [BOQ-LIST-TITLE-002] 單元測試：`EventId` 查不到時丟 `InvalidOperationException`；整合測試：比照 `OrganizerScopingDataCorruptionTests` 以 `MissingEventIds` 讓 `GET /api/orders` 回 500，body 不含訂單資料
- [ ] 3.3 [BOQ-DETAIL-DISPLAY-001] `GetMyOrderDetailHandlerTests`：混合座位項目與計數項目，斷言 `EventTitle`、座位項目的 `SeatZoneCode`／`SeatNumber`／`TicketTypeName`、計數項目兩個座位欄位為 null 且 `TicketTypeName` 正確；整合測試：`GET /api/orders/{id}` 回應 JSON 包含上述欄位名稱（驗證序列化）
- [ ] 3.4 [BOQ-DETAIL-DISPLAY-002] 單元測試：項目 `TicketTypeId` 為 null 時 `TicketTypeName` 為 null，其餘欄位正常、不丟例外
- [ ] 3.5 [BOQ-DETAIL-DISPLAY-003] 單元測試五個案例，皆斷言丟 `InvalidOperationException`：訂單 `EventId` 查不到、活動 `SeatMapId` 對應座位圖查不到（訂單含座位項目）、`EventSeatId` 查不到、`TicketTypeId` 查不到、`EventSeat.SeatId` 在座位圖中查不到
- [ ] 3.6 [BOQ-DETAIL-DISPLAY-004] 單元測試：分別以 1 個與 5 個座位項目的訂單呼叫，斷言各 Fake repository 方法的呼叫次數兩次相同且皆為 1（`IEventSeatRepository.GetByIdsAsync` 以單次呼叫傳入全部座位 Id）
- [ ] 3.7 既有「非本人查詢他人訂單明細」「查詢不存在的訂單」測試：補斷言在 403／404 路徑下不呼叫任何顯示資訊查詢（本人／存在檢查先於顯示資訊查詢，spec 明訂）
- [ ] 3.8 更新既有 `GetMyOrderDetailHandlerTests`／`GetMyOrdersHandlerTests`／`OrdersControllerTests` 中因建構子或 seed 資料不足而失敗的測試（需補 seed 活動、座位圖、票種）。下列 MODIFIED 中文字未變的既有 scenario，其既有測試 MUST 保持通過（不得刪除或放寬斷言）：[查詢自己的訂單列表]、[尚未有任何訂單]、[查詢自己的訂單明細（已出票）]、[查詢自己尚未確認付款的訂單明細]、[非本人查詢他人訂單明細]、[查詢不存在的訂單]
- [ ] 3.9 [尚未有任何訂單] 補斷言：0 筆訂單時回傳空清單，且 `GetMyOrdersHandler` 不呼叫 `GetByIdsAsync`（1.1 的「空清單不查詢」為 repository 層的額外防線）

## 4. 後端測試 — order-administration

- [ ] 4.1 [ORD-LIST-002] `GetOrdersHandlerTests`（以 `FakeApplicationDbContext.MemberData` seed 會員）：兩位不同買家，`BuyerDisplayName` 正確；整合測試 `AdminOrdersControllerTests`：`GET /api/admin/orders` 回應 JSON 含 `buyerDisplayName`、不含任何 `email` 欄位
- [ ] 4.1a [ORD-LIST-004] 整合測試（使用 1.3 的 interceptor）：分別 seed「1 筆訂單／1 位買家」與「3 筆訂單／3 位不同買家」（兩組資料放在兩個不同 Organizer 名下，各自以同一種方式建立、已登入並切換的 client 呼叫，且每次計數前先以同一 client 暖機一次，排除授權／會員解析等固定成本查詢的差異），各呼叫一次 `GET /api/admin/orders`，斷言兩次請求的查詢次數相同。單元層的 MockQueryable `DbSet` 無法區分單次 `Contains` 與逐筆查詢，故此保證只在整合層驗證
- [ ] 4.2 [ORD-LIST-003] 單元測試：`BuyerId` 查不到時丟 `InvalidOperationException`
- [ ] 4.3 [ORD-LIST-001] 既有測試執行確認仍通過（租戶過濾不受影響）；更新因建構子變更而失敗的既有測試

## 5. 前端

- [ ] 5.1 更新 `types/apiResponses.ts`：`MyOrderSummary.eventTitle`、`MyOrderDetail.eventTitle`、`MyOrderItem.seatZoneCode`／`seatNumber`／`ticketTypeName`、`OrderSummary.buyerDisplayName`
- [ ] 5.2 `OrderResultPage.vue`：顯示活動名稱
- [ ] 5.3 `OrderDetailPage.vue`：顯示活動名稱、每筆項目的票種名稱與座位標示「{分區}-{座位號碼}」（null 顯示「—」）
- [ ] 5.4 `MyOrdersPage.vue`：新增活動名稱欄
- [ ] 5.5 `AdminOrderListPage.vue`：買家欄改顯示 `buyerDisplayName`，移除買家 GUID 欄

## 6. 前端測試（vitest，mock API 層）

- [ ] 6.1 `OrderResultPage.test.ts`：[BW-RESULT-002] 補斷言顯示活動名稱；其餘既有 scenario 測試保持通過
- [ ] 6.2 `OrderDetailPage.test.ts`：[BW-MYORDER-DISPLAY-001] 座位項目顯示「A」「A-12」，計數項目顯示「站票」「—」；另一案例 `ticketTypeName` 為 null 時顯示「—」；[開啟訂單明細頁查看已出票訂單] 補斷言活動名稱與項目資訊
- [ ] 6.3 `MyOrdersPage.test.ts`：[開啟我的訂單列表頁] 補斷言活動名稱
- [ ] 6.4 `AdminOrderListPage.test.ts`：[AWU-ORDER-LIST-001] 補斷言顯示買家顯示名稱、畫面不含 mock 的買家 GUID

## 7. 驗證與收尾

- [ ] 7.1 容器內執行全部後端測試（`docker compose exec api dotnet test`）與前端 `npm run test`、`npm run lint`、`vue-tsc`
- [ ] 7.2 以 claude-in-chrome 實機驗證：混合座位與計數票的訂單明細顯示正確；後台訂單列表顯示買家名稱
- [ ] 7.3 套用 hardener 檢查清單於 2.1–2.4 變更的 Handler，再呼叫 strict-reviewer
- [ ] 7.4 歸檔時同步主 spec，並更新 `docs/project-scope.md` 第 8 節「已知前端缺口」④ 與第 9 節快照註記為已完成
