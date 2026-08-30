## 1. Domain

- [x] 1.1 `ProjectC.Domain.Orders` 新增 `OrderItemSalesGroup` record（`Guid? TicketTypeId`、`int ItemCount`、`int QuantitySold`、`decimal Revenue`），純資料投影、不含行為；`TicketTypeId` 為 `null` 代表分組依 `TicketTypeId` 為 null 的項目（是否算「無法歸類」由 Application 層依 design.md 決策 2、3 判斷，Domain 層只回傳分組本身，見 design.md 決策 1）
- [x] 1.2 `IOrderRepository` 新增 `GetPaidItemSalesByEventIdAsync(Guid eventId, CancellationToken)`（單一查詢依 `TicketTypeId` 分組，`null` 自成一組，取代原本拆成「依票種」「總數」兩個方法的設計，見 design.md 決策 1、3）；XML Doc MUST 明確寫出 design.md 決策 1 列出的六項契約（範圍條件、分組唯一性、null 分組、空清單非 null、`ItemCount` 是筆數非張數、不判斷票種所屬活動）

## 2. Infrastructure

- [x] 2.1 `OrderRepository` 實作 `GetPaidItemSalesByEventIdAsync`：**MUST** 從 `_dbContext.Orders` 出發（`Where(o => o.EventId == eventId && o.Status == Paid).SelectMany(o => o.Items).GroupBy(item => item.TicketTypeId)`），**不可**寫成 `oi.Order.EventId`——`OrderItem` 沒有指向 `Order` 的 navigation property（`OrderId` 是 shadow FK，關聯設定在 `OrderConfiguration.HasMany(o => o.Items)`），從 `OrderItem` 端無法反向導覽（見 design.md 決策 1「重要修正」）
- [x] 2.2 Infrastructure 整合測試（`ProjectC.Infrastructure.Tests`，Testcontainers）：
  - [x] 2.2.1 混合座位制/計數制票種、皆有已付款訂單 → 依票種分組數字正確（對應 Scenario「查詢混合座位制與計數制票種的活動」）
  - [x] 2.2.2 存在 `Pending`/`Cancelled` 訂單 → 不計入任何分組（對應 Scenario「Pending 與 Cancelled 訂單不計入報表」）
  - [x] 2.2.3 存在 `TicketTypeId IS NULL` 的已付款項目（模擬舊資料）→ 獨立成一組（`TicketTypeId = null`），`ItemCount`/`QuantitySold`/`Revenue` 正確（對應 Scenario「已付款訂單存在 TicketTypeId 為 null 的舊資料項目」）
  - [x] 2.2.4 活動完全沒有已付款訂單 → 回傳空清單（對應 Scenario「尚未有任何銷售的活動」）
  - [x] 2.2.5 建立資料異常情境：`Order.EventId = Event-A`、其 `OrderItem.TicketTypeId` 指向屬於 `Event-B` 的 `TicketType`、`Order.Status = Paid` → 查詢 `GetPaidItemSalesByEventIdAsync(Event-A)` 仍 MUST 回傳這個分組（`TicketTypeId` 非 null，`ItemCount`/`QuantitySold`/`Revenue` 正確），驗證方法本身不判斷 `TicketTypeId` 是否屬於呼叫的 `eventId`（見 design.md 決策 1 的介面契約），這個判斷是 Handler 的責任，不是這個方法的責任（對應 Scenario「已付款訂單存在指向其他活動票種的資料異常項目」；此測試建立資料時 MUST 繞過 `OrderService.PlaceOrderAsync` 直接操作 Repository/DbContext，因為正常下單流程本身就會擋下這種跨活動組合）

## 3. Application

- [x] 3.1 新增 `ProjectC.Application.Orders.GetEventSalesReport` 命名空間：
  - `SalesReportDto`（`EventId`、`EventTitle`、`TotalRevenue`、`TotalTicketsSold`、`ByTicketType: IReadOnlyList<TicketTypeSalesDto>`、`UnclassifiedItemCount`、`UnclassifiedTicketsSold`、`UnclassifiedRevenue`）
  - `TicketTypeSalesDto`（`TicketTypeId`、`ZoneCode`、`RequiresSeat`、`QuantitySold`、`Revenue`）——非 nullable，因為只用於已分類的票種
- [x] 3.2 `GetEventSalesReportHandler`：
  - 呼叫 `IEventRepository.GetByIdAsync` 驗證活動存在，不存在回傳 NotFound 結果
  - 呼叫 `ITicketTypeRepository.GetByEventIdAsync` 取得活動全部票種（用於左外連接補「0 銷售」票種，同時作為「這個分組是否真的屬於本活動」的判斷依據，見 design.md 決策 2）
  - 呼叫 `IOrderRepository.GetPaidItemSalesByEventIdAsync` 取得單一查詢的全部分組結果
  - 分組分類邏輯（見 design.md 決策 3）：對每個分組，若 `TicketTypeId != null` **且** 該 `TicketTypeId` 存在於上一步取得的本活動票種清單中 → 併入 `ByTicketType`（與票種清單左外連接補「0 銷售」票種）；**其餘情況**（`TicketTypeId == null`，**或** `TicketTypeId` 有值但不在本活動票種清單中）→ 一律併入 `UnclassifiedItemCount`/`UnclassifiedTicketsSold`/`UnclassifiedRevenue` 的加總，**不得靜默捨棄**
  - `TotalRevenue`/`TotalTicketsSold` = 所有分組（含無法歸類分組）加總，**不**另外查詢——同一次 `GetPaidItemSalesByEventIdAsync` 結果即可算出，確保與明細數字必然一致（見 design.md 決策 3）
- [x] 3.3 Application 單元測試（`ProjectC.Application.Tests`，使用 `FakeOrderRepository.PaidItemSalesGroups` 直接設定查詢結果，不透過 `Data` 推導，見 design.md 決策 8）：
  - [x] 3.3.1 活動不存在 → Handler 回傳 NotFound（對應 spec Scenario「查詢不存在的活動」）
  - [x] 3.3.2 有銷售記錄的活動 → 回傳正確總營收/總張數/依票種明細（對應 Scenario「查詢有銷售記錄的活動」）
  - [x] 3.3.3 混合座位制與計數制票種 → 明細分別列出、總數為加總（對應 Scenario「查詢混合座位制與計數制票種的活動」）
  - [x] 3.3.4 尚未有任何銷售的活動 → 總數為 0，不視為錯誤（對應 Scenario「尚未有任何銷售的活動」）
  - [x] 3.3.5 某票種尚未賣出任何張數 → 該票種仍出現在明細中，數字為 0（對應 Scenario「活動的票種尚未賣出任何張數」）
  - [x] 3.3.6 存在 Pending/Cancelled 訂單 → 不計入任何數字（對應 Scenario「Pending 與 Cancelled 訂單不計入報表」）
  - [x] 3.3.7 `FakeOrderRepository.PaidItemSalesGroups` 設定一筆 `TicketTypeId = null` 的分組 → `ByTicketType` 排除該項目，但 `TotalRevenue`/`TotalTicketsSold` 計入，`UnclassifiedItemCount`/`UnclassifiedTicketsSold`/`UnclassifiedRevenue` 正確反映該分組（對應 Scenario「已付款訂單存在 TicketTypeId 為 null 的舊資料項目」）
  - [x] 3.3.8 `FakeOrderRepository.PaidItemSalesGroups` 設定一筆 `TicketTypeId` 有值、但該 Id 不在 `ITicketTypeRepository.GetByEventIdAsync` 回傳清單中的分組 → 同 3.3.7 的處理方式，**不得**從任何數字中消失（對應 Scenario「已付款訂單存在指向其他活動票種的資料異常項目」）
  - [x] 3.3.9 `PaidItemSalesGroups` 全部分組的 `TicketTypeId` 皆存在於本活動票種清單中 → `UnclassifiedItemCount`/`UnclassifiedTicketsSold`/`UnclassifiedRevenue` 皆為 0（對應 Scenario「沒有無法歸類的項目」）
  - [x] 3.3.10 活動存在、`ITicketTypeRepository.GetByEventIdAsync` 回傳空清單、`GetPaidItemSalesByEventIdAsync` 回傳空清單 → 所有數字為 0，`ByTicketType` 為空陣列（對應 Scenario「活動存在但沒有任何票種也沒有任何訂單」）

## 4. WebApi

- [x] 4.1 `AdminEventsController` 新增 `GET /api/admin/events/{eventId:guid}/sales-report` action，沿用 `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`，constructor injection 注入 `GetEventSalesReportHandler`（比照既有其他 action 的既定模式）
- [x] 4.2 `Program.cs` 新增 `builder.Services.AddScoped<GetEventSalesReportHandler>();`（比照既有 Handler 註冊區塊，緊鄰 `GetAdminEventsHandler`/`GetOrdersHandler` 等既有登錄；遺漏會在第一次呼叫端點時造成 DI resolution error，見 design.md 決策 5、風險段落）
- [x] 4.3 WebApi 測試（`ProjectC.WebApi.Tests`）：
  - [x] 4.3.1 Admin 角色成功呼叫 → 200（對應 Scenario「Admin 成功查詢銷售報表」；此測試會實際透過 DI 容器解析 Controller，若 4.2 的註冊遺漏會直接測試失敗）
  - [x] 4.3.2 非 Admin 角色呼叫 → 403（對應 Scenario「非 Admin 會員查詢銷售報表」）
  - [x] 4.3.3 未帶 Token 呼叫 → 401（對應 Scenario「未帶 Token 查詢銷售報表」）
  - [x] 4.3.4 查詢不存在的活動 Id → 404（對應 Scenario「查詢不存在的活動」）
  - [x] 4.3.5 成功回應的 JSON body 逐欄位驗證（`JsonDocument` 直接檢查駝峰命名的欄位是否存在與數值正確：`totalRevenue`/`totalTicketsSold`/`byTicketType`/`unclassifiedItemCount` 等），補上 DTO → ASP.NET JSON serialization 的整合驗證缺口（2026-08-30 使用者審查後新增，原本 4.3.1 只驗證 HTTP 200，未驗證回應內容）

## 5. 測試替身與既有實作同步

- [x] 5.1 `tests/ProjectC.Application.Tests/TestSupport/FakeOrderRepository.cs` 新增 `GetPaidItemSalesByEventIdAsync` 實作：**不**從 `Data`（既有 `Order` 聚合根清單）推導——`OrderItem` 公開建構子要求 `ticketTypeId` 為非 null，無法透過正常 Domain API 建出 `TicketTypeId = null` 或指向其他活動的測試資料；改為新增一個可直接設定的欄位 `PaidItemSalesGroups: IReadOnlyList<OrderItemSalesGroup>`（預設空清單），方法直接回傳這個欄位的值，Application 單元測試在 Arrange 階段直接指定要回傳的分組（見 design.md 決策 8）
- [x] 5.2 確認擴充 `IOrderRepository` 後沒有其他實作/替身遺漏（搜尋 `IOrderRepository` 的全部實作，逐一確認 `OrderRepository`／`FakeOrderRepository` 之外是否還有其他測試替身）

## 6. 前端

- [x] 6.1 `web/src/types/apiResponses.ts` 新增 `SalesReport`（對應 `SalesReportDto`）、`TicketTypeSales`（對應 `TicketTypeSalesDto`）型別，含 `unclassifiedItemCount`/`unclassifiedTicketsSold`/`unclassifiedRevenue` 欄位
- [x] 6.2 `web/src/api/admin.ts` 新增 `getEventSalesReport(eventId): Promise<SalesReport>` API 呼叫
- [x] 6.3 新增 `web/src/pages/admin/AdminSalesReportPage.vue`：顯示總營收、總售出張數、依票種明細表格；總數為 0 時顯示「尚無銷售」提示（對應 Scenario「尚未有任何銷售的活動」「活動存在但沒有任何票種也沒有任何訂單」）**且依票種明細表格仍然顯示**（不因總數為 0 就隱藏，讓 Admin 仍能看到活動有哪些票種，2026-08-30 使用者審查後修正）；`unclassifiedItemCount > 0` 時顯示「含 N 筆無法歸類的項目」提示（N 取自 `unclassifiedItemCount`；措辭刻意不寫「舊資料」——無法歸類的成因有兩種，一種是舊資料 `TicketTypeId IS NULL`，另一種是 `TicketTypeId` 指向其他活動的資料異常，不全是舊資料，2026-08-30 使用者審查後修正；**不**用總數減明細加總反推，見 spec.md「依票種明細排除無法歸類票種的已付款項目...」Requirement）
- [x] 6.4 `web/src/router/index.ts` 新增對應路由（比照既有 Admin 路由命名慣例）
- [x] 6.5 `EventListPage.vue` 每筆活動新增「銷售報表」連結，導向對應活動的報表頁面
- [x] 6.6 前端測試（Vitest）：
  - [x] 6.6.1 報表頁面正確渲染總營收/總張數/依票種明細（對應 Scenario「查詢有銷售記錄的活動」）
  - [x] 6.6.2 總數為 0 時顯示「尚無銷售」提示，不誤判為載入失敗（對應 Scenario「尚未有任何銷售的活動」）
  - [x] 6.6.3 `unclassifiedItemCount > 0` 時顯示提示，且顯示的筆數等於 API 回傳的 `unclassifiedItemCount`（對應 Scenario「已付款訂單存在 TicketTypeId 為 null 的舊資料項目」）
  - [x] 6.6.4 `unclassifiedItemCount = 0` 時不顯示提示（對應 Scenario「沒有無法歸類的項目」）
  - [x] 6.6.5 有票種但完全沒有銷售時，仍顯示依票種明細表格（含 0 銷售的列），不因「尚無銷售」提示而隱藏（2026-08-30 使用者審查後新增）

## 7. 驗證與收尾

- [x] 7.1 容器內執行完整後端測試套件（`docker compose exec api dotnet test`）確認全數通過（Domain 97、Application 184、Infrastructure 51、WebApi 144，皆 0 失敗）
- [x] 7.2 容器內執行前端測試（`docker compose exec web npm run test`）確認全數通過（11 檔案、93 測試，另跑 `npm run build`／`npm run lint` 確認型別檢查與 lint 皆無錯誤）
- [x] 7.3 真實 API 手動驗證（`docker compose exec db psql` 直接比對資料庫內容，非僅信任 API 回應）：建立活動＋座位制/計數制混合票種＋付款訂單，確認總營收 1400、總張數 4、依票種明細與資料庫 `OrderItems` 實際內容完全一致；另外植入一筆 `TicketTypeId IS NULL` 的已付款舊資料，確認總數變為 1900/5、`unclassifiedItemCount`/`unclassifiedTicketsSold`/`unclassifiedRevenue` 正確反映為 1/1/500、`byTicketType` 不受影響；另驗證 404/403/401 皆正確。額外用 claude-in-chrome 實際登入瀏覽器、點擊活動列表頁的「銷售報表」連結，確認 `AdminSalesReportPage.vue` 真實渲染畫面：總營收/總張數、依票種明細表格、無法歸類提示文字（「含 1 筆無法歸類的舊資料項目（金額 NT$ 500、1 張...）」）皆與後端數字一致；另建立一個沒有訂單的活動確認「尚無銷售」空狀態正確顯示、無錯誤訊息。驗證資料（含瀏覽器測試用活動/會員）已全數清除，不殘留於開發資料庫
