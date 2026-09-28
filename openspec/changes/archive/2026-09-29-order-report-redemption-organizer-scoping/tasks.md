## 0. Domain／Infrastructure（`IOrderRepository` 新查詢方法）

- [x] 0.1（design.md Decision 1）`IOrderRepository` 新增 `GetByOrganizerIdAsync(Guid organizerId, CancellationToken)`：Infrastructure 以 EF Core LINQ `Orders JOIN Events ON EventId WHERE Events.OrganizerId = organizerId` 在資料庫端過濾，`Include(Items)`；XML doc 註明「過濾 MUST 在資料庫端執行、MUST 一併載入 `Items`」（比照 `IEventRepository.GetByOrganizerIdAsync`）。移除改用後即無呼叫端的 `GetAllAsync`（介面、`OrderRepository` 實作、`tests/ProjectC.Application.Tests/TestSupport/FakeOrderRepository.cs`）
- [x] 0.2（design.md Decision 1）`IOrderRepository` 新增 `GetOrganizerIdByOrderItemIdAsync(Guid orderItemId, CancellationToken)`：以單一查詢 `OrderItems JOIN Orders JOIN Events` 只投影回 `Event.OrganizerId`（`Guid?`，查無回 `null`），不載入 `Order`／`Items` 實體；`FakeOrderRepository` 同步實作
- [x] 0.3 Infrastructure 整合測試（Testcontainers Postgres，`tests/ProjectC.Infrastructure.Tests/`），覆蓋 AC：[ORD-LIST-001]、[RDM-AUTHZ-004]、[RDM-AUTHZ-007] 的 repository 契約面向（各 AC 的完整對應見文末「AC 覆蓋對照」）
- [x] 0.3a [ORD-LIST-001] `GetByOrganizerIdAsync_WhenOrdersExistUnderTwoOrganizers_ReturnsOnlyCallerOrganizerOrdersWithItems`：資料庫同時有 A、B 兩個 Organizer 的訂單，只回傳 A 的訂單，且 `Items` 已載入
- [x] 0.3b [ORD-LIST-001]（資料庫端過濾）`GetByOrganizerIdAsync_DoesNotMaterializeOtherOrganizerOrders`：
    - 為什麼需要這個測試：只看回傳結果，無法區分「資料庫只查 A」和「查出 A、B 再於記憶體過濾」。後者會把 B 的訂單與買家資訊載入記憶體，違反 spec
    - 被測主體：`OrderRepository.GetByOrganizerIdAsync`；測試類型：Infrastructure 整合測試（Testcontainers Postgres）
    - **驗證方式：觀察 EF Core 實際具現化（materialize）了哪些實體，不解析 SQL**
      - spec 要防的是「其他租戶的訂單被載入應用程式記憶體」。直接觀察被建立的實體，就是在驗這件事本身
      - 用 SQL 文字推論篩選語意需要維護 SQL parser；而「有 JOIN Events 但沒拿來篩選」「參數只用在投影」這類錯誤 SQL，文字比對仍然可能誤判
      - 改用具現化觀察後，不論 SQL 怎麼寫（JOIN、EXISTS、子查詢、single 或 split query、alias、參數命名），只要 B 的訂單或明細被讀進記憶體就會失敗，也不必辨識哪一條是 root query
    - 測試夾具：新增測試專用的 `MaterializedEntityRecorder : IMaterializationInterceptor`，放在 `tests/ProjectC.Infrastructure.Tests/TestSupport/`
      - 在 `InitializedInstance` 中記錄每一個被具現化的 `Order` 與 `OrderItem` 實例
      - EF Core 7 起提供此介面，本專案為 EF Core 10.0.11；tracking 與 `AsNoTracking` 查詢都會觸發
      - 只以 `AddInterceptors` 掛在執行被測方法的那個 `ApplicationDbContext`，不影響其他測試
    - 資料：用**另一個** `DbContext` 建立 A、B 各至少兩筆訂單，每筆都有 `Items`。與執行查詢的 context 分開，避免 seed 時已追蹤的實體讓 EF 略過具現化，導致漏記
    - 斷言 1（沒有讀進 B 的訂單）：記錄到的 `Order` 全部屬於 A 名下活動（依 seed 時記下的 A 訂單 Id 集合比對），不含任何 B 的訂單
    - 斷言 2（沒有讀進 B 的明細）：記錄到的 `OrderItem` 全部屬於 A 的訂單（比對 seed 時記下的 A 明細 Id 集合），涵蓋 split query 的明細查詢
    - 斷言 3（紀錄器確實生效）：記錄到的 `Order` 數量等於 A 的訂單數。避免 interceptor 沒掛上時，因為紀錄是空集合而讓斷言 1、2 空洞通過
    - 斷言 4（最終結果正確）：回傳結果只含 A 的訂單，且每筆 `Items` 數量與資料相符，證明 Include 仍有效
    - 「不呼叫 `GetAllAsync`」不另寫執行期測試：0.1 已把 `GetAllAsync` 從 `IOrderRepository`、`OrderRepository`、`FakeOrderRepository` 移除，任何呼叫都會編譯失敗，由編譯器保證
- [x] 0.3c [RDM-AUTHZ-004][RDM-AUTHZ-007] `GetOrganizerIdByOrderItemIdAsync_WhenOrderItemExists_ReturnsEventOrganizerId`／`GetOrganizerIdByOrderItemIdAsync_WhenOrderItemNotFound_ReturnsNull`

## 1. Application

- [x] 1.1（design.md Decision 1）`GetOrdersHandler.HandleAsync` 新增 `organizerId` 參數，改呼叫 `IOrderRepository.GetByOrganizerIdAsync`，MUST NOT 呼叫全量查詢後於記憶體過濾
- [x] 1.2（design.md Decision 1）`GetOrderByIdHandler.HandleAsync` 新增 `organizerId` 參數與 `IEventRepository` 相依：以 `GetByIdAsync(order.EventId)` 取得活動，`OrganizerId` 不等於呼叫端時回傳與訂單不存在**同一句訊息**的 `Error.NotFound`（`Order '{orderId}' was not found.`），不回傳該訂單資料；活動查無時拋出 `InvalidOperationException`（ORD-DETAIL-004，資料不一致大聲失敗）
- [x] 1.3（design.md Decision 1）`RedeemTicketHandler.HandleAsync` 新增 `organizerId` 參數，在交易內 `GetForUpdateAsync` 之後以 `IOrderRepository.GetOrganizerIdByOrderItemIdAsync(ticket.OrderItemId)` 單一查詢取得歸屬：回傳 `null` 時拋出 `InvalidOperationException`（RDM-AUTHZ-007，交易未 commit、Ticket 不變）；不等於呼叫端目前 `OrganizerId` 時回傳與「查無此票」**同一句訊息**的 `Error.NotFound`（`Ticket '{ticketId}' was not found.`），不變更 Ticket 狀態，不回傳 403。檢查順序 MUST 為「簽章驗證 → `GetForUpdateAsync` 載入（不存在回 404）→ 歸屬核對（不屬於回 404）→ 既有狀態檢查（非 Issued 回 409）」（design.md Decision 1），歸屬核對 MUST 先於狀態檢查，避免以 409 洩漏其他 Organizer 票券已核銷
- [x] 1.4（design.md Decision 1）`GetEventSalesReportHandler.HandleAsync` 新增 `organizerId` 參數與歸屬核對：查出的活動若 `OrganizerId` 不等於呼叫端目前 `OrganizerId`，回傳與活動不存在**同一句訊息**的 `Error.NotFound`（`Event '{eventId}' was not found.`），且 MUST 在查詢票種與銷售彙總之前返回
- [x] 1.5 Application 單元測試（fake repository，不碰 DB）：
  - `GetOrderByIdHandler`：`HandleAsync_WhenEventNotFound_ThrowsInvalidOperationException`［ORD-DETAIL-004］；`HandleAsync_WhenOrderBelongsToOtherOrganizer_ReturnsSameNotFoundErrorAsMissingOrder`（斷言 `Error` 與訂單不存在時完全相等）
  - `RedeemTicketHandler`：`HandleAsync_WhenOrganizerLookupReturnsNull_ThrowsAndDoesNotRedeem`［RDM-AUTHZ-007］（斷言 Ticket 仍為 `Issued`、未 commit）；`HandleAsync_WhenTicketBelongsToOtherOrganizer_ReturnsSameNotFoundErrorAsMissingTicket`
  - `GetEventSalesReportHandler`：`HandleAsync_WhenEventBelongsToOtherOrganizer_ReturnsSameNotFoundErrorAsMissingEvent`

## 2. WebApi

每個 action 的取值模式 MUST 比照 `AdminEventsController.CreateEvent`（`src/ProjectC.WebApi/Controllers/AdminEventsController.cs:36-48`）；OrganizerId 一律取自 claim，不接受前端輸入（design.md「安全確認-輸入驗證」）。

- [x] 2.1 `AdminOrdersController`：
  - class 層級 `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]` 改為 `RequireOrganizerContext`
  - `GetOrders` 與 `GetOrderById` 兩個 action 各自呼叫 `User.TryGetOrganizerId(out var organizerId)`，失敗時 `return Forbid()`，成功時把 `organizerId` 傳入 `GetOrdersHandler.HandleAsync`／`GetOrderByIdHandler.HandleAsync`
- [x] 2.2 `AdminTicketsController`：
  - class 層級 `AdminOnly` 改為 `RequireOrganizerContext`
  - `Redeem` action 呼叫 `User.TryGetOrganizerId`，失敗時 `return Forbid()`，成功時把 `organizerId` 傳入 `RedeemTicketHandler.HandleAsync`
- [x] 2.3 `AdminEventsController.GetSalesReport`：
  - method 層級 `AdminOnly` 改為 `RequireOrganizerContext`（`SetQueueMode` 維持 `AdminOnly`，屬 `purchase-queue-organizer-scoping` 範圍）
  - 呼叫 `User.TryGetOrganizerId`，失敗時 `return Forbid()`，成功時把 `organizerId` 傳入 `GetEventSalesReportHandler.HandleAsync`
- [x] 2.4 fail-closed 行為的測試策略：
  - 掛上 `RequireOrganizerContext` 後，`TryGetOrganizerId` 失敗的分支不可達（Policy 已先以同一套 `TryGetOrganizerId` 規則拒絕，見 `organizer-management`「`RequireOrganizerContext` Authorization Policy MUST 驗證 claim 格式並 fail-closed」）
  - 「未帶合法 OrganizerId 一律 403」的對外行為由 5.2／6.2／7.2（`*-AUTHZ-002`）的整合測試驗證
  - 「未帶 claim 一律 403」這個對外行為由這三項驗證；但若 Policy 被換成單純 `[Authorize]`，Controller 內的 `Forbid()` 仍會回 403，這三項照樣通過——對外行為相同、沒有安全影響，因此不視為缺口。只有 `[Authorize]` 整個被拿掉時，`*-AUTHZ-003`（401）才會失敗
  - Controller 內的 `Forbid()` 分支是防日後漏掛 Policy 的第二道防線，比照已歸檔 `event-management-organizer-scoping` 的既有作法，不另寫繞過 Policy 的專用測試
  - 「`organizerId` 確實有傳進 Handler」由 5.4／5.5／6.4／7.4 的租戶過濾結果間接驗證：沒有傳入時不可能正確區分 A、B 兩個 Organizer

## 3. 前端（admin-web-ui）

- [x] 3.1 後台路由守衛調整（`web/src/router/index.ts`）：訂單列表、訂單明細、核銷、銷售報表頁面從 `requiresAdmin` 改為與活動、場館頁面相同的 `requiresOrganizerContext`；`/admin/organizers` 審核頁維持 `requiresAdmin`；同步更新已過時的路由註解（目前寫著「銷售報表本次不動」「訂單、核銷頁面本次不修改…（AWU-GUARD-006）」）（design.md Decision 3）
- [x] 3.2 導覽選單顯示條件調整（`web/src/layouts/AdminLayout.vue`）：「訂單管理」「票券核銷」不再包在 `v-if="authStore.isAdmin"` 內，比照「場館管理」「活動管理」；「主辦方審核」維持只對 Admin 顯示；同步更新相關程式碼註解（design.md Decision 3）
- [x] 3.3 銷售報表入口顯示條件調整（`web/src/pages/admin/EventListPage.vue`）：「操作」欄的銷售報表入口移除 `v-if="authStore.isAdmin"`，若元件因此不再使用 `authStore` 則一併移除該相依；同步更新相關程式碼註解（design.md Decision 3）
- [x] 3.4 手動輸入信任標示調整（`web/src/pages/admin/RedemptionScannerPage.vue`）：「Admin 信任操作，未驗證簽章」改為「操作人員信任操作，未驗證簽章」（design.md Decision 4）

## 4. 既有測試盤點與更新

- [x] 4.1 盤點所有假設「Admin 角色即可呼叫後台管理端點」的既有測試，更新其前置條件為「已切換至一個 Approved Organizer」（可沿用 `AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync`；需要同時建立測試資料的情境注意：建立資料與呼叫被測端點 MUST 使用同一個 Organizer 身份，否則會被新的歸屬核對視同找不到）。已知受影響的檔案：
  - 後端 WebApi：`tests/ProjectC.WebApi.Tests/Admin/AdminOrdersControllerTests.cs`、`Admin/AdminTicketsControllerTests.cs`、`Admin/AdminEventsControllerTests.cs`（`GetSalesReport_*`）、`Observability/SensitiveDataMaskingInStructuredPropertiesTests.cs`（核銷）
  - 後端 Application（Handler 簽章／建構子改變，會直接編譯失敗）：`tests/ProjectC.Application.Tests/Orders/GetOrders/GetOrdersHandlerTests.cs`、`Orders/GetOrderById/GetOrderByIdHandlerTests.cs`、`Orders/GetOrderById/GetOrderByIdHandlerCountingItemTests.cs`、`Tickets/RedeemTicket/RedeemTicketHandlerTests.cs`、`Orders/GetEventSalesReport/GetEventSalesReportHandlerTests.cs`、`TestSupport/FakeOrderRepository.cs`（見 0.1／0.2）
  - 後端 Infrastructure：`tests/ProjectC.Infrastructure.Tests/GetOrderByIdLegacyDataTests.cs`（直接 `new GetOrderByIdHandler`）、`RedeemTicketConcurrencyTests.cs`
  - 前端：`web/src/router/index.test.ts`（「已切換 Organizer 但非 Admin 開啟訂單頁面仍導向買家端首頁」改為放行並標記 `AWU-GUARD-007`；「Admin 登入後可進入核銷頁面」在新規則下 Admin 未切換 Organizer 會被導向選擇主辦方頁，前置條件須改為已切換 Organizer）、`web/src/layouts/AdminLayout.test.ts`（選單依角色顯示的既有測試）、`web/src/pages/admin/EventListPage.test.ts`（銷售報表入口依角色顯示的既有測試）
  - 實作時仍 MUST 以 grep `AdminOnly`／`CreateAuthenticatedAdminClientAsync`／`requiresAdmin`／`isAdmin`，以及四個 Handler 類別名稱、`GetAllAsync`、`Admin 信任操作` 複查，不以此清單為唯一依據

## 5. 測試 — order-administration（授權規則與租戶過濾）

測試類型：整合測試（xUnit + `WebApplicationFactory` + Testcontainers Postgres），驗證 `[Authorize]` Policy 與 `Event.OrganizerId` join 過濾的實際查詢結果。被測主體：`AdminOrdersController`（`GetOrdersHandler`／`GetOrderByIdHandler`）。

- [x] 5.1 [ORD-AUTHZ-001] 已切換至 Approved Organizer 的使用者可成功呼叫訂單列表／明細端點
- [x] 5.2 [ORD-AUTHZ-002] 已登入但未帶 `OrganizerId` claim（含單純 `Admin` 角色未切換）呼叫上述端點回傳 403
- [x] 5.3 [ORD-AUTHZ-003] 未帶 Token 呼叫上述端點回傳 401
- [x] 5.4 [ORD-LIST-001] 訂單列表僅回傳呼叫端目前 Organizer 名下活動的訂單，不含其他 Organizer 名下活動的訂單
- [x] 5.5 [ORD-DETAIL-003] 查詢屬於其他 Organizer 的訂單明細回傳 404，不回傳該訂單資料，不回傳 403；並以隨機不存在的訂單 ID 各呼叫一次，斷言兩者回應 body 的 `title`／`detail` 除 ID 外逐字相同（只斷言狀態碼會漏掉「訊息不同仍洩漏存在性」）
- [x] 5.6 [ORD-DETAIL-001][ORD-DETAIL-002] 查詢存在／不存在的訂單明細（既有行為，於新授權模式下重新執行確認）
- [x] 5.7 [ORD-AUTHZ-004] 核發帶 `OrganizerId` claim 的 Access Token 後將該 Organizer 停權，以同一組未過期 Token 呼叫訂單列表端點，斷言仍回傳 200 且含該 Organizer 名下訂單——驗證這是已知延遲視窗而非 Policy 漏檢查（比照已歸檔 `event-management-organizer-scoping` 的 `EVT-AUTHZ-004` 測試）
- [x] 5.8 [ORD-DETAIL-004] `GetOrderById_WhenOrderEventMissing_Returns500WithoutOrderData`：
  - 真實 DB 有 FK（`Orders.EventId → Events`），無法建立「訂單存在但活動不存在」的資料
  - 測試夾具：新增 `OrganizerScopingFaultInjectionWebApplicationFactory : CustomWebApplicationFactory`，比照 `TestSupport/CachingComponentTestWebApplicationFactory.cs` 的作法，以 `ConfigureTestServices` + `RemoveAll<IEventRepository>()` 換成包裝真實 `EventRepository` 的 decorator：只有被測試指定的 `EventId` 回傳 `null`，其餘委派給真實實作
  - 資料與請求：以真實 DB 建立屬於呼叫端 Organizer 的訂單，並讓 decorator 對該訂單的 `EventId` 回傳 `null`，再呼叫 `GET /api/admin/orders/{id}`
  - 斷言：HTTP 500；body 是 `GlobalExceptionHandler` 產生的 `ProblemDetails`；body 不含訂單 ID 以外的訂單資料（不含 `items`／`buyerId`）；**不是** 404
  - 隔離：這個 decorator 只存在於 5.8／6.6 專用的 factory，不修改 `CustomWebApplicationFactory` 的預設註冊，不影響其他測試

## 6. 測試 — ticket-redemption（授權規則與租戶過濾）

測試類型：整合測試（xUnit + `WebApplicationFactory` + Testcontainers Postgres），核銷涉及 `Ticket`→`Order`→`Event` 多層資料關聯與狀態變更，需要真實 DB 驗證。被測主體：`AdminTicketsController`（`RedeemTicketHandler`）。

- [x] 6.1 [RDM-AUTHZ-001] 已切換至 Approved Organizer、**角色非 `Admin`** 的使用者，可成功核銷自己活動的票券。帶正確簽章（掃描路徑）與不帶簽章（手動輸入路徑）各驗一次；呼叫端 MUST 用非 Admin 帳號，才能證明不再依賴 `Admin` 角色
- [x] 6.2 [RDM-AUTHZ-002] 未帶 `OrganizerId` claim 的已登入使用者核銷被拒絕，且票券不變：
  - 前置：以真實 DB 建立一張狀態為 `Issued` 的 Ticket
  - 操作：分別以「角色為 `Admin` 但未切換 Organizer」與「一般 Member 未切換 Organizer」兩種身份呼叫核銷端點，帶正確簽章（確保被拒絕的原因只有授權，不是簽章）
  - 斷言：兩種身份都回傳 403
  - 斷言：之後以**新的** `DbContext` 讀取該 Ticket，`Status` 仍為 `Issued`、`RedeemedAtUtc` 仍為 `null`
- [x] 6.3 [RDM-AUTHZ-003] 未帶 Token 核銷被拒絕，且票券不變：
  - 前置：同 6.2，建立一張狀態為 `Issued` 的 Ticket
  - 操作：分別以「不帶 Authorization Header」與「帶無效 Token」呼叫核銷端點，帶正確簽章
  - 斷言：兩種都回傳 401
  - 斷言：之後以新的 `DbContext` 讀取該 Ticket，`Status` 仍為 `Issued`、`RedeemedAtUtc` 仍為 `null`
- [x] 6.4 [RDM-AUTHZ-004] 核銷屬於其他 Organizer 的票券回傳與「查無此票」相同的 404，不變更 Ticket 狀態，不回傳 403；斷言回應 body 與對不存在 Ticket ID 核銷時除 ID 外逐字相同
- [x] 6.4a [RDM-AUTHZ-005] 票券先由其所屬 Organizer B 成功核銷（狀態 `Redeemed`），再以 Organizer A 身份對同一張票呼叫核銷端點，斷言回傳 404（不是 409）、回應 body 與查無此票時除 ID 外逐字相同，且事後查詢該票的狀態與核銷時間皆未改變
- [x] 6.4b [RDM-AUTHZ-006] 核發帶 `OrganizerId` claim 的 Access Token 後將該 Organizer 停權，以同一組未過期 Token 核銷該 Organizer 活動中狀態為 `Issued` 的票券，斷言核銷成功（204）——驗證已知延遲視窗
- [x] 6.4c [RDM-AUTHZ-004]（手動輸入路徑，design.md Decision 4）不帶 `signature`（`null`，即前端手動輸入核銷走的路徑）核銷屬於其他 Organizer、狀態為 `Issued` 的票券：
  - 斷言回傳與查無此票逐字相同的 404，且 Ticket 仍為 `Issued`
  - 目的：驗證「不驗簽章的手動核銷開放給所有 Organizer 成員」這個取捨確實只作用在自家票券，而不是只驗到帶簽章的路徑
- [x] 6.5 既有核銷相關規則（已核銷過的衝突、查無此票、路徑格式不合法、併發防重複、QR 簽章驗證，見既有 `ticket-redemption` spec）於新授權模式下重新執行，確認行為與既有 spec 一致
- [x] 6.6 [RDM-AUTHZ-007] `Redeem_WhenOrganizerLookupReturnsNull_Returns500AndTicketUnchanged`：
  - 真實 DB 有 FK（`Tickets.OrderItemId → OrderItems → Orders → Events`），無法建立「票券存在但歸屬關聯查不到」的資料
  - 測試夾具：沿用 5.8 的 `OrganizerScopingFaultInjectionWebApplicationFactory`，另外以 `RemoveAll<IOrderRepository>()` 換成包裝真實 `OrderRepository` 的 decorator：只有被指定的 `OrderItemId` 會讓 `GetOrganizerIdByOrderItemIdAsync` 回傳 `null`，其餘委派給真實實作
  - 資料與請求：以真實 DB 建立狀態為 `Issued` 的票券，再以其所屬 Organizer 的身份呼叫核銷端點
  - 斷言：HTTP 500（`GlobalExceptionHandler` 的 `ProblemDetails`），**不是** 404、也不是 204
  - 斷言：事後以新的 `DbContext` 查詢該票，狀態仍為 `Issued`、核銷時間仍為 `null`（證明交易沒有 commit）

## 7. 測試 — sales-report（授權規則與租戶過濾）

測試類型：整合測試（xUnit + `WebApplicationFactory` + Testcontainers Postgres），驗證 `[Authorize]` Policy 與 `Event.OrganizerId` 核對的實際查詢結果。被測主體：`AdminEventsController`（`GetEventSalesReportHandler`）。

- [x] 7.1 [RPT-AUTHZ-001] 已切換至 Approved Organizer 的使用者可成功查詢自己活動的銷售報表
- [x] 7.2 [RPT-AUTHZ-002] 已登入但未帶 `OrganizerId` claim（含單純 `Admin` 角色未切換）呼叫銷售報表端點回傳 403
- [x] 7.3 [RPT-AUTHZ-003] 未帶 Token 呼叫銷售報表端點回傳 401
- [x] 7.4 [RPT-AUTHZ-004] 查詢屬於其他 Organizer 的活動銷售報表回傳 404，不回傳 403；斷言回應 body 與查詢不存在的活動時除 ID 外逐字相同
- [x] 7.4a [RPT-AUTHZ-005] 核發帶 `OrganizerId` claim 的 Access Token 後將該 Organizer 停權，以同一組未過期 Token 查詢該 Organizer 活動的銷售報表，斷言仍回傳 200——驗證已知延遲視窗
- [x] 7.5 既有銷售報表計算規則（Pending/Cancelled 不計入、無法歸類項目統計、混合票種、無銷售活動等，見既有 `sales-report` spec）於新授權模式下重新執行，確認行為與既有 spec 一致

## 8. 測試 — admin-web-ui（路由守衛，訂單／核銷頁面）

測試類型：元件測試（Vitest + Vue Test Utils，路由守衛邏輯）。被測主體：路由守衛（router guard）。

路由守衛測試共通：測試檔 `web/src/router/index.test.ts`（Vitest），被測主體為 `router.beforeEach` 守衛；以 `useAuthStore` 設定 `accessToken`（`fakeAccessToken({ sub, OrganizerId? })`）與 `member.role` 後 `router.push(目標路徑)`，斷言 `router.currentRoute.value.name`。「不顯示後台頁面內容」以「最終路由名稱不是該後台頁面」斷言。既有測試沿用者，在測試名稱或註解補上 Scenario ID。

- [x] 8.1 [AWU-GUARD-007] 已切換 Organizer、角色非 Admin 的使用者可進入訂單、核銷與銷售報表頁面
  - 改寫既有「已切換 Organizer 但非 Admin 的使用者開啟訂單頁面仍導向買家端首頁」（原標記 AWU-GUARD-006）
  - 前置：`role: 'Member'`，token 帶 `OrganizerId`
  - 操作：依序 push `/admin/orders`、`/admin/orders/{id}`、`/admin/redeem`、`/admin/events/{eventId}/sales-report`
  - 斷言：路由名稱依序為 `admin-orders`、`admin-order-detail`、`admin-redeem`、`admin-sales-report`，沒有任何一個變成 `events`（買家首頁）
- [x] 8.2 [AWU-GUARD-002][AWU-GUARD-003] 五類一般後台頁面的切換規則。每個路由各驗兩種情況：未切換時導向 `my-organizers`，已切換時允許進入。

  | Task | 路由 | 未切換時（AWU-GUARD-002） | 已切換時（AWU-GUARD-003） |
  | --- | --- | --- | --- |
  | 8.2.1 | 場館 `/admin/venues` | 既有「已登入但未切換 Organizer 的使用者開啟場館頁面導向選擇主辦方頁面」（Member）→ `my-organizers` | 既有「已切換 Organizer 後可進入場館與活動頁面」→ `admin-venues` |
  | 8.2.2 | 活動 `/admin/events` | 既有「單純 Admin 角色但未切換 Organizer 開啟活動頁面導向選擇主辦方頁面」（Admin）→ `my-organizers` | 同上既有測試 → `admin-events` |
  | 8.2.3 | 訂單列表／明細 `/admin/orders`、`/admin/orders/{id}` | **新增**：Member 與 Admin 兩種身份、token 不帶 `OrganizerId` → 都是 `my-organizers` | 由 8.1 覆蓋 → `admin-orders`／`admin-order-detail` |
  | 8.2.4 | 核銷 `/admin/redeem` | **新增**：同 8.2.3 → `my-organizers` | 改寫既有「Admin 登入後可進入核銷頁面」：前置改為 Admin 且 token 帶 `OrganizerId`（原本的 `'access-token'` 不含 claim，新規則下會被導走）→ `admin-redeem`；非 Admin 由 8.1 覆蓋 |
  | 8.2.5 | 銷售報表 `/admin/events/{eventId}/sales-report` | **新增**：同 8.2.3 → `my-organizers` | 由 8.1 覆蓋 → `admin-sales-report` |

  - 8.2.3～8.2.5 的「未切換」必須同時驗 Admin，證明 Admin 角色不再能繞過 Organizer 切換（本次變更前這三頁是 Admin 直接放行）
- [x] 8.3 [AWU-NAV-001][AWU-NAV-002][AWU-NAV-004] `AdminLayout` 導覽選單（`web/src/layouts/AdminLayout.test.ts`，Vitest 元件測試）。既有 describe「AdminLayout 導覽選單依角色顯示」的兩個測試依新規則改寫，並新增一個：
  - [AWU-NAV-001] 改寫既有「已切換 Organizer 的非 Admin 成員只看到場館、活動選單」
    - 前置：非 Admin、Access Token 帶 `OrganizerId`
    - 斷言：選單標籤**完全等於** `['場館管理', '活動管理', '訂單管理', '票券核銷']`，用完全相等而非 `arrayContaining`，同時證明沒有「主辦方審核」、也不會有一般選單被誤藏
  - [AWU-NAV-002] 改寫既有「Admin 角色看得到訂單、核銷、審核選單」
    - 前置：`Admin`、Access Token 帶 `OrganizerId`
    - 斷言：選單標籤完全等於 `['場館管理', '活動管理', '訂單管理', '票券核銷', '主辦方審核']`
  - [AWU-NAV-004] 新增 `尚未切換 Organizer 的 Admin 在審核頁仍看得到一般選單與審核選單`
    - 前置：`Admin`、Access Token **不帶** `OrganizerId`，先 `router.push('/admin/organizers')` 再掛載
    - 斷言：選單標籤完全等於上述五項
    - 「點選一般項目導向選擇主辦方頁」由 `AWU-GUARD-002`（8.2）驗證，本測試不重複
  - 同步刪除既有測試裡的 `adminOnlyMenuLabels` 常數：它代表的「只給 Admin 看的三項」分組本次已不存在
- [x] 8.4 [AWU-NAV-003] `EventListPage` 元件測試：非 Admin 使用者看得到每筆活動的銷售報表入口，且連結指向對應活動的銷售報表路由
- [x] 8.5 行為本次未變更的守衛 Scenario（逐項對應既有測試，不足處補強）：
  - [x] 8.5.1 [AWU-GUARD-001] 既有「未登入直接開啟後台路由導向登入頁，不顯示後台內容」（`/admin/venues`）與「未登入直接開啟核銷頁面導向登入頁」（`/admin/redeem`）
    - 前置：未設定 `accessToken`
    - 斷言：路由名稱為 `login`；`/admin/redeem` 另斷言 `query.redirect` 為 `/admin/redeem`
    - 補上 AWU-GUARD-001 標記
  - [x] 8.5.2 [AWU-GUARD-004] 既有「一般會員開啟 /admin/organizers 導向買家端首頁」（token 不帶 `OrganizerId`）→ 斷言路由名稱為 `events`
    - **新增**已切換 Organizer 的情況：`role: 'Member'`、token 帶 `OrganizerId`，push `/admin/organizers` → 斷言 `events`
    - 新增原因：Scenario 寫明「不論是否已切換 Organizer」，既有測試只涵蓋未切換，而本次正是讓「已切換的非 Admin」可進入其他後台頁，必須證明審核頁沒有跟著被放行
  - [x] 8.5.3 [AWU-GUARD-005] 既有「Admin 登入後不需切換 Organizer 即可進入審核頁面」
    - 前置：`role: 'Admin'`，token 不帶 `OrganizerId`
    - 斷言：路由名稱為 `admin-organizers`，不是 `my-organizers`
- [x] 8.6 [AWU-ORDER-LIST-001][AWU-ORDER-DETAIL-001]「主辦方成員可查看目前 Organizer 名下的訂單列表與明細」Requirement：
  - [AWU-ORDER-LIST-001] 只顯示目前 Organizer 名下訂單：過濾由後端執行，由 [ORD-LIST-001]（0.3a／0.3b／5.4）驗證
  - [AWU-ORDER-DETAIL-001] 顯示訂單明細：由 [ORD-DETAIL-001]（5.6）驗證
  - 能否進入這兩個頁面：由 [AWU-GUARD-002]／[AWU-GUARD-003]／[AWU-GUARD-007]（8.1／8.2）驗證
  - 不新增頁面元件測試：`AdminOrderListPage`／`AdminOrderDetailPage` 本次沒有任何程式變更（只呼叫 API 並顯示結果），目前也沒有既有元件測試；前端不另做過濾（spec 明定過濾由後端執行），所以沒有可測的新前端邏輯
- [x] 8.7 核銷頁兩條 MODIFIED Requirement（「已切換 Organizer 的操作人員可透過介面掃描 QR Code 核銷票券」「掃描期間與相機不可用時皆可切換到手動輸入 Ticket ID 完成核銷」）逐條 Scenario 的測試任務，編號為 8.7.1～8.7.21。

  **共通規則（適用 8.7.1～8.7.21）：**
  - **身份前置條件：不適用**
    - 下列被測主體（`ticketRedemptionOutcome`、`ticketRedemptionParsing`、`useRedemptionScanner`、`RedemptionScannerPage` 的模式切換）都不讀取角色或 auth store，輸入只有 HTTP 回應、字串或相機狀態
    - 在這些測試裡設定「非 Admin、已切換 Organizer」不會影響任何執行路徑，這種前置條件無法讓測試失敗，屬於無效前置
    - 身份相關的驗證集中在 8.1（能否進入頁面）、8.7a（頁面在非 Admin 身份下可送出核銷）、6.1／6.4／6.4c（後端允許核銷哪些票）
  - **實作時逐項核對**：確認既有測試的斷言確實涵蓋下表「斷言」欄；不足的部分補強斷言，不能只因為測試名稱相符就視為已涵蓋
  - **標記 Scenario ID**：在測試名稱前綴補上 Scenario ID（目前只有 `MANUAL-SWITCH`、`MANUAL-FALLBACK-UNSUPPORTED`、`MANUAL-FALLBACK-RETRIABLE`、`TRUST-LABEL` 已有）
  - 測試類型：全部為 Vitest unit test；`RedemptionScannerPage.*` 為 Vitest 元件測試（Vue Test Utils）

  **掃描路徑：**

  | Task | Scenario | 被測主體／測試檔 | 觸發與操作 | 斷言 |
  | --- | --- | --- | --- | --- |
  | 8.7.1 | `ADMIN-REDEEM-SCAN-SUCCESS` | `performRedemption`／`web/src/utils/ticketRedemptionOutcome.test.ts` | mock `redeemTicket` resolve（204） | 結果分類為 `success` |
  | 8.7.2 | `ADMIN-REDEEM-SCAN-DISPATCH` | 同上 | 以合法掃描字串 `{ticketId}.{signature}` 執行 | `redeemTicket` 收到的 `id`、`signature` 與字串解析結果逐字相同，只被呼叫一次 |
  | 8.7.3 | `ADMIN-REDEEM-SCAN-CONFLICT` | 同上 | mock `redeemTicket` reject `ApiError(409)` | 結果分類為 `already-redeemed`，不是 `system-error` |
  | 8.7.4 | `ADMIN-REDEEM-SCAN-NOT-FOUND` | 同上 | mock reject `ApiError(404)` | 結果分類為 `not-found`（其他 Organizer 的票後端回傳逐字相同的 404，見 6.4／6.4c，因此前端不需另外區分） |
  | 8.7.5 | `ADMIN-REDEEM-SCAN-INVALID-SIGNATURE` | 同上 | mock reject `ApiError(400, title=InvalidTicketSignature)`；另測 400 但 title 不同 | 前者為 `invalid-signature`，後者為 `system-error` |
  | 8.7.6 | `ADMIN-REDEEM-SCAN-UNRECOGNIZED` | 解析函式／`web/src/utils/ticketRedemptionParsing.test.ts` | 輸入缺分隔符、多個分隔符、前段非 GUID、後段為空 | 每種都回傳「無法辨識」；搭配 `useRedemptionScanner.test.ts` 確認此時 `redeemTicket` 呼叫次數為 0 |
  | 8.7.7 | `ADMIN-REDEEM-SCAN-SYSTEM-ERROR` | `performRedemption`／`ticketRedemptionOutcome.test.ts` | mock reject `ApiError(5xx)`；mock reject 非 `ApiError` 的網路例外 | 兩者皆為 `system-error`，不是 `not-found`；`redeemTicket` 只被呼叫一次（不自動重試） |
  | 8.7.8 | `ADMIN-REDEEM-SCAN-RETRY-AFTER-ERROR` | `useRedemptionScanner`／`web/src/composables/useRedemptionScanner.test.ts` | 第一次核銷回 system-error → 恢復 scanning → 偵測到相同 QR 內容 | `redeemTicket` 第二次被呼叫 |
  | 8.7.9 | `ADMIN-REDEEM-SCAN-AUTO-RESUME` | 同上 | 成功結果顯示後推進 fake timer；錯誤結果後點「立即繼續掃描」 | 狀態回到 `scanning`，不需重新掛載元件 |
  | 8.7.10 | `ADMIN-REDEEM-SCAN-DEDUPE` | 同上 | 結果顯示期間持續回報相同 QR 內容 | `redeemTicket` 只被呼叫一次 |
  | 8.7.11 | `ADMIN-REDEEM-BACKGROUND-RESUME` | 同上 | scanning 中觸發 `visibilitychange` hidden → visible | hidden 時 stream track 已停止；visible 時重新呼叫 `getUserMedia`；`redeemTicket` 呼叫次數不增加 |
  | 8.7.12 | `ADMIN-REDEEM-BACKGROUND-PROCESSING-COMPLETES` | 同上 | 核銷請求進行中切到 hidden，請求 resolve 後切回 visible | 切回後顯示該次結果；`redeemTicket` 總呼叫次數為 1 |
  | 8.7.13 | `ADMIN-REDEEM-NAV-ENTRY` | `AdminLayout`＋真實 router／`web/src/layouts/AdminLayout.test.ts` 既有「導覽選單渲染出『票券核銷』項目，且連結指向 /admin/redeem」 | 前置改為**非 Admin**（`role: 'Member'`）、token 帶 `OrganizerId`（既有 `beforeEach` 目前是 Admin，須在此測試覆寫）；從 `/admin/venues` 掛載，點擊「票券核銷」選單項目 | `vi.waitFor` 等到 `router.currentRoute.value.path` 為 `/admin/redeem` 且 `name` 為 `admin-redeem`。真實 router 會跑 `beforeEach` 守衛，若守衛擋下就不會停在此路由，因此同時證明「可點選」「目標路徑正確」「非 Admin 的 Organizer 成員被守衛放行」。8.3 只驗選單標籤，不能取代本項 |

  **手動輸入路徑：**

  | Task | Scenario | 被測主體／測試檔 | 觸發與操作 | 斷言 |
  | --- | --- | --- | --- | --- |
  | 8.7.14 | `ADMIN-REDEEM-MANUAL-SWITCH` | `RedemptionScannerPage`／`web/src/pages/admin/RedemptionScannerPage.test.ts` | scanning 狀態點「改用手動輸入」 | 顯示手動輸入表單 |
  | 8.7.15 | `ADMIN-REDEEM-MANUAL-FALLBACK-UNSUPPORTED` | 同上 | 以 `unsupported` 狀態掛載 | 手動輸入表單為主體，不存在「重新嘗試相機」按鈕 |
  | 8.7.16a | `ADMIN-REDEEM-MANUAL-FALLBACK-RETRIABLE`（無相機裝置） | ① 錯誤分類：`web/src/utils/cameraScanner.test.ts` 既有「NotFoundError 分類為 camera-unavailable」「OverconstrainedError…分類為 camera-unavailable」 ② 畫面：`RedemptionScannerPage.test.ts` 既有 `it.each` 的 `camera-unavailable` 列 ③ 仍可核銷：`useRedemptionScanner.test.ts` 既有「相機本就不可用時，手動核銷完成後…」 | ① 丟入 `NotFoundError`／`OverconstrainedError` ② fake scanner 設為 `camera-unavailable` 後掛載頁面 ③ `openCameraStream` reject 且分類為 `camera-unavailable`，再呼叫 `submitManualRedemption(合法 GUID)` | ① 分類為 `camera-unavailable` ② 手動輸入 `input` 存在、「重新嘗試相機」按鈕存在、顯示「找不到可用相機」 ③ `performRedemption` 以 `(GUID, null)` 被呼叫（**補強**：既有測試只斷言狀態，須加此斷言），核銷後狀態維持 `camera-unavailable`、不自動重試相機 |
  | 8.7.16b | 同上（權限被拒） | ① `cameraScanner.test.ts` 既有「NotAllowedError 分類為 permission-denied」 ② 同上 `it.each` 的 `permission-denied` 列 ③ **新增**：將 ③ 的既有測試改為 `it.each` 參數化，加入 `permission-denied` | ① 丟入 `NotAllowedError` ② fake scanner 設為 `permission-denied` ③ 分類改為 `permission-denied`，其餘同 8.7.16a | ① 分類為 `permission-denied` ② `input`、「重新嘗試相機」存在，顯示「相機權限被拒絕」 ③ 同 8.7.16a，狀態維持 `permission-denied` |
  | 8.7.16c | 同上（初始化非預期錯誤） | ① `cameraScanner.test.ts` 既有「其他例外分類為 error」 ② 同上 `it.each` 的 `error` 列 ③ **新增**：同 8.7.16b 的 `it.each`，加入 `error` | ① 丟入一般 `Error` ② fake scanner 設為 `error` ③ 分類改為 `error`，其餘同 8.7.16a | ① 分類為 `error` ② `input`、「重新嘗試相機」存在，顯示「相機初始化發生錯誤」 ③ 同 8.7.16a，狀態維持 `error` |
  | 8.7.16d | 同上（三種原因可區分） | `RedemptionScannerPage.test.ts` **新增** | 依序以三種狀態各掛載一次，收集原因說明文字 | 三段文字兩兩不同（Requirement 要求「不得共用同一句籠統訊息」，逐列 `toContain` 無法抓到三列被改成同一句的退化） |
  | 8.7.17 | `ADMIN-REDEEM-MANUAL-SUCCESS` | `useRedemptionScanner.test.ts`（手動送出）＋ `ticketRedemptionOutcome.test.ts` | 手動送出合法 GUID，mock `redeemTicket` resolve | `redeemTicket` 以 `(id, null)` 被呼叫，結果為 `success`；頁面層級另由 8.7a 覆蓋 |
  | 8.7.18 | `ADMIN-REDEEM-MANUAL-CONFLICT` | 同上 | 手動送出，mock reject `ApiError(409)` | `redeemTicket` 以 `(id, null)` 被呼叫，結果為 `already-redeemed` |
  | 8.7.19 | `ADMIN-REDEEM-MANUAL-NOT-FOUND`／`ADMIN-REDEEM-MANUAL-SYSTEM-ERROR` | 同上 | 手動送出，分別 mock reject `ApiError(404)` 與 `ApiError(5xx)` | 前者為 `not-found`；後者為 `system-error`、不是 `not-found`、`redeemTicket` 只呼叫一次 |
  | 8.7.20 | `ADMIN-REDEEM-MANUAL-INVALID-FORMAT` | `ticketRedemptionParsing.test.ts`＋`useRedemptionScanner.test.ts` | 手動送出非 GUID 字串 | 解析回傳格式不正確（`formatValid: false`）；`redeemTicket` 呼叫次數為 0 |
  | 8.7.21 | `ADMIN-REDEEM-MANUAL-RETRY-CAMERA-STILL-FAILS` | `useRedemptionScanner.test.ts` | 初次失敗 → 點「重新嘗試相機」→ `getUserMedia` 以不同原因再次失敗 | 狀態停在手動輸入，原因更新為第二次的失敗原因，不停留在載入中；「重新嘗試相機」是否可用依新原因決定 |

  `ADMIN-REDEEM-TRUST-LABEL` 見 8.8。

  **為什麼 8.7.17～8.7.19 需要驗 `(id, null)`：** 這是 design.md Decision 4 開放給 Organizer 成員、不驗簽章的路徑。前端必須確實送出 `signature: null`，後端 6.4c 的跨租戶 404 才會涵蓋到實際走的這條路徑。目前 `useRedemptionScanner.test.ts` 若沒有「透過 `submitManualRedemption` 送出並斷言 `performRedemption`／`redeemTicket` 收到 `(id, null)`」的測試，MUST 新增，不能只靠 `ticketRedemptionOutcome.test.ts` 的結果分類測試。

  審核頁的 Scenario 見 8.9。
- [x] 8.7a 新增 `RedemptionScannerPage.test.ts`：`非 Admin 但已切換 Organizer 的操作人員可送出手動核銷`
  - 前置：以非 Admin、已設定 `organizerId` 的 auth store 掛載頁面，mock `redeemTicket`
  - 操作：在手動輸入欄填入合法 GUID 並送出
  - 斷言：`redeemTicket` 以該 ID 與 `signature: null` 被呼叫一次，並顯示成功結果
  - 目的：防止日後有人在核銷頁內部重新加入依 `isAdmin` 的隱藏或阻擋，這種退化 8.1 的路由守衛測試抓不到
  - 掃描路徑不重複測：兩條路徑共用同一個 `performRedemption`，差異只在 signature 的來源
- [x] 8.8 [ADMIN-REDEEM-TRUST-LABEL] 兩種模式的信任標示（`web/src/pages/admin/RedemptionScannerPage.test.ts`，Vitest 元件測試）：
  - 掃描模式：以 `scanning` 狀態掛載頁面
    - 斷言顯示「已驗證簽章」（`RedemptionScannerPage.vue:102` 現有文字）
    - 斷言不顯示手動模式的信任標示
    - 既有測試「掃描模式顯示『已驗證簽章』標示」已涵蓋，補上 Scenario ID 並確認斷言如上
  - 切換到手動模式：在同一個掛載的元件上點「改用手動輸入」
    - 斷言顯示「操作人員信任操作，未驗證簽章」
    - 斷言標示文字不含「Admin」，避免暗示只有平台 Admin 可操作
    - 斷言不再顯示「已驗證簽章」
    - 既有測試「手動輸入模式顯示『Admin 信任操作，未驗證簽章』標示」目前是直接以手動模式掛載，並比對舊文字；本次改寫為「由掃描模式切換過去」，並比對新文字
  - 兩種模式的語意差異：同一個測試裡斷言兩個標示字串不同，而且只有掃描模式包含「已驗證」、只有手動模式包含「未驗證」
  - 手動輸入 Requirement 的其他 Scenario 見 8.7.14～8.7.21

- [x] 8.9 審核頁「平台管理員可透過介面審核主辦方申請」MODIFIED Requirement（本次只更正權限規則的引用文字，頁面行為未變）。被測元件：`AdminOrganizersPage`；測試檔：`web/src/pages/admin/AdminOrganizersPage.test.ts`；測試類型：Vitest 元件測試（mock `organizersApi`）。三個既有測試沿用，在測試名稱補上 Scenario ID，並逐項確認斷言如下，不足時補強：
  - [x] 8.9.1 [AWU-REVIEW-001] 既有「顯示目前所有待審核的主辦方申請與申請人」
    - 觸發：mock 待審核清單 API 回傳至少兩筆 `Pending` 申請，然後掛載頁面
    - 斷言：每一筆的主辦方名稱與申請人都有顯示在畫面上
  - [x] 8.9.2 [AWU-REVIEW-002] 既有「點選核准後呼叫核准端點成功，重新查詢清單，該筆申請自清單移除」
    - 觸發：點選某一筆的「核准」；核准 API resolve，第二次清單查詢回傳不含該筆的結果
    - 斷言：核准 API 以該筆 Id 被呼叫一次
    - 斷言：清單 API 總共被呼叫兩次（初次載入 + 重新查詢）
    - 斷言：該筆已不在畫面上，其他筆仍在
  - [x] 8.9.3 [AWU-REVIEW-003] 既有「點選駁回後呼叫駁回端點成功，重新查詢清單，該筆申請自清單移除」
    - 觸發與斷言同 8.9.2，改為點選「駁回」並斷言駁回 API 被呼叫
    - 另外斷言核准 API 未被呼叫，避免兩個按鈕綁錯
  - 身份前置不適用：頁面本身不讀角色。進入權限（只有 `Admin` 可進、不需切換 Organizer）由既有 `AWU-GUARD-004`／`AWU-GUARD-005` 驗證（8.5）

## 9. AC 覆蓋對照（同一條 AC 由多個任務從不同面向驗證者）

| AC | 任務 | 驗證面向 |
| --- | --- | --- |
| ORD-LIST-001 | 0.3a | Repository 回傳只含目前 Organizer 的訂單，且 `Items` 已載入 |
| ORD-LIST-001 | 0.3b | 資料庫端隔離：其他 Organizer 的 `Order`／`OrderItem` 不會被 EF Core 具現化進記憶體 |
| ORD-LIST-001 | 5.4 | API response 只含目前 Organizer 的訂單 |
| ORD-DETAIL-003 | 1.5、5.5 | Handler 回傳與不存在時相同的 `Error`；API 回應 body 逐字相同 |
| ORD-DETAIL-004 | 1.5、5.8 | Handler 拋出例外；API 回 500 且不回訂單資料 |
| RDM-AUTHZ-004 | 0.3c、1.5、6.4、6.4c | Repository 歸屬查詢契約；Handler 回傳相同 `Error`；API 帶簽章與不帶簽章兩條路徑都回 404 |
| RDM-AUTHZ-007 | 0.3c、1.5、6.6 | Repository 查無時回 `null`；Handler 拋出例外且不核銷；API 回 500 且票券不變 |
| RPT-AUTHZ-004 | 1.5、7.4 | Handler 回傳相同 `Error`；API 回應 body 逐字相同 |
| AWU-ORDER-LIST-001 | 8.6 → 0.3a／0.3b／5.4 | 前端無新邏輯，由後端 ORD-LIST-001 驗證 |
| AWU-ORDER-DETAIL-001 | 8.6 → 5.6 | 前端無新邏輯，由後端 ORD-DETAIL-001 驗證 |
| ADMIN-REDEEM-TRUST-LABEL | 8.8 | 兩種模式的標示與語意差異 |
| AWU-NAV-004 | 8.3、8.2 | 選單在未切換的 Admin 下仍完整顯示；點選一般項目後由路由守衛導向選擇主辦方頁 |
| ADMIN-REDEEM-NAV-ENTRY | 8.7.13 | 非 Admin 的 Organizer 成員點擊選單後實際導覽並被守衛放行到核銷頁 |
| ADMIN-REDEEM-MANUAL-FALLBACK-RETRIABLE | 8.7.16a～8.7.16d | 無相機、權限被拒、初始化錯誤三種情況各自的錯誤分類、畫面（表單＋重試＋原因）、手動核銷仍可完成；三種原因文字彼此不同 |
| AWU-GUARD-001 | 8.5.1 | 未登入開啟 `/admin/venues`、`/admin/redeem` 導向登入頁 |
| AWU-GUARD-002 | 8.2.1～8.2.5 | 五類一般後台頁面未切換時導向選擇主辦方頁（訂單、核銷、銷售報表含 Admin 身份） |
| AWU-GUARD-003 | 8.2.1～8.2.5、8.1 | 五類一般後台頁面已切換後可進入 |
| AWU-GUARD-004 | 8.5.2 | 非 Admin 不論是否已切換，開啟審核頁都導向買家首頁 |
| AWU-GUARD-005 | 8.5.3 | Admin 未切換即可進入審核頁 |
| AWU-GUARD-007 | 8.1 | 已切換的非 Admin 可進入訂單列表、明細、核銷、銷售報表 |
