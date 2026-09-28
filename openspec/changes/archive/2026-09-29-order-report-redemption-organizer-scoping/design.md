## Context

`event-management-organizer-scoping` 已把活動／票種／場館的授權規則從「Admin 角色」改為「已切換至一個 Approved Organizer」。本次通盤盤點其餘既有「Admin 角色」端點，發現 `order-administration`／`ticket-redemption`／`sales-report` 三個能力仍是同一種舊授權模式，且完全沒有租戶邊界。

## Goals / Non-Goals

**Goals:**
- 訂單查詢、票券核銷、銷售報表查詢的權限邊界從「全域 Admin」收斂為「該 Organizer 的成員」
- 自助申請成為主辦方的一般 Member，切換至自己的 Organizer 後，能查看自己的訂單、核銷自己的票、看自己的銷售報表

**Non-Goals:**
- 不處理 `purchase-queue` 的授權收斂——是後續變更 `purchase-queue-organizer-scoping` 的範圍
- **不提供平台層級（跨租戶）的訂單、核銷、銷售報表查詢**：本次變更後，`Admin` 角色不再能不切換 Organizer 就看到所有租戶的訂單；Admin 若需要查看某 Organizer 的資料，必須是該 Organizer 的成員並切換過去。這是「收斂 Admin 為僅平台治理」的刻意結果，本次**移除、不提供替代**；若日後需要客服／爭議處理用的平台層級唯讀查詢，另開變更，以獨立端點與獨立 Policy 設計，不回頭放寬本次三個能力的授權

## Decisions

### 1. 訂單查詢、票券核銷、銷售報表也納入 Organizer 邊界，不維持純 Admin 存取
**現況核對與缺口**：`AdminOrdersController`／`AdminTicketsController` 目前是 `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`，`GetOrdersHandler.HandleAsync` 呼叫 `_orderRepository.GetAllAsync` 完全不做租戶過濾；核對 `sales-report` spec 後發現銷售報表查詢端點是同一種「角色為 Admin」授權模式，同樣沒有租戶邊界。若這三個能力維持純 Admin 存取，會出現兩個問題：(1) 多租戶隔離不完整；(2) 自助申請成為主辦方的一般 Member 在切換至自己的 Organizer 後，呼叫這三個能力的 API 時會因為不具備 `Admin` 角色被拒絕，導致這三個核心後台功能對本次真正的目標使用者完全不可用。

**修正後設計**：`order-administration`／`ticket-redemption`／`sales-report` 的授權規則比照 `event-management`，一併從「角色為 Admin」改為 `RequireOrganizerContext` Policy（`event-management-organizer-scoping` 已定義並套用於活動／場館端點）；並在 Application 層新增依 `OrganizerId` 過濾／核對的邏輯：
- 訂單列表查詢：只回傳 `Order.EventId` 對應的 `Event.OrganizerId` 等於呼叫端目前 `OrganizerId` 的訂單。`Order` 只以 `EventId` 純量欄位參照 `Event`、沒有 navigation（`OrderConfiguration`），Application 層無法自行 join，因此 MUST 在 `IOrderRepository` 新增 `GetByOrganizerIdAsync(organizerId)`，由 Infrastructure 以 `Orders JOIN Events` 在**資料庫端**過濾（比照 `IEventRepository.GetByOrganizerIdAsync` 的既有約定），MUST NOT 以 `GetAllAsync` 撈全部再於記憶體過濾——那樣會把其他租戶的訂單（含買家資訊）載入本次請求的記憶體。新方法 MUST 一併載入 `Order.Items`（與被取代的 `GetAllAsync` 一致）。`IOrderRepository.GetAllAsync` 在改用新方法後即無呼叫端，一併移除，避免日後被誤用為跨租戶查詢。盤點結果（2026-09-29，對整個 repo 的 `*.cs` 做 grep `GetAllAsync`，排除 `obj`／`bin`，並逐筆辨識是哪個 repository 的方法）：
  - 呼叫端：只有 `src/ProjectC.Application/Orders/GetOrders/GetOrdersHandler.cs`
  - 實作：`OrderRepository`，以及測試替身 `tests/ProjectC.Application.Tests/TestSupport/FakeOrderRepository.cs`，兩者隨介面一併移除該方法
  - 其餘 grep 命中都是 `IEventRepository`／`IVenueRepository` 的同名方法（例如 `GetEventsHandler`、`PurchaseQueueAdmissionService` 與各測試 decorator），與本次無關，不受影響
  - 實作時仍依 tasks.md 4.1 再 grep 一次，確認盤點後沒有新增的呼叫端
- 訂單明細查詢：查出的訂單若所屬活動不屬於呼叫端目前 Organizer，MUST 視同找不到（404），不回傳 403——比照既有「座位圖存在但不屬於指定場地時視同找不到」的既定慣例，避免用 403 洩漏「這筆訂單存在、只是不屬於你」這個資訊給無關租戶。歸屬核對以既有 `IEventRepository.GetByIdAsync(order.EventId)` 取得 `Event.OrganizerId`
- **「視同找不到」MUST 與真正不存在的回應完全相同**：不只 HTTP 狀態碼，回應 body（`ProblemDetails` 的 `title`／`detail` 等）也 MUST 與該端點「資源不存在」時逐字相同——實作上歸屬失敗 MUST 重用與不存在分支同一句錯誤訊息（例如 `Order '{id}' was not found.`／`Ticket '{id}' was not found.`／`Event '{id}' was not found.`），不得另寫「不屬於你」之類可區分的訊息，否則狀態碼相同仍會洩漏資訊
- **關聯資料缺失（資料不一致）一律大聲失敗**：歸屬核對需要的上游資料（訂單明細的 `Event`、核銷的 `OrderItem → Order → Event`）在資料庫層皆有 FK 約束（`Order.EventId → Events`、`Ticket.OrderItemId → OrderItems`、`OrderItem → Orders`），正常情況不可達。若仍查不到，代表資料毀損，Handler MUST 拋出 `InvalidOperationException`（交由全域 `IExceptionHandler` 轉為 500 並記錄），MUST NOT 靜默當成 404 或當成「屬於自己」放行；核銷情境下此時交易尚未 commit，Ticket 狀態不會改變
- 票券核銷：依 `Ticket.OrderItemId → Order.EventId → Event.OrganizerId` 這條既有的資料關聯鏈核對是否屬於呼叫端目前 Organizer（`Order.EventId` 已是既有欄位，不需新增資料庫欄位）。因為這段核對位於 `FOR UPDATE` 交易內（持有 Ticket 列鎖），MUST 以**單一查詢**取得答案：在 `IOrderRepository` 新增 `GetOrganizerIdByOrderItemIdAsync(orderItemId)`，以 `OrderItems JOIN Orders JOIN Events` 只投影回 `Event.OrganizerId`（查無時回傳 `null`，依上一點大聲失敗）；不沿用既有 `GetByOrderItemIdAsync` 再查一次 Event——那會在持鎖期間多兩次往返並額外載入整筆 `Order.Items`，拉長鎖持有時間。不屬於時 MUST 視同查無此票（404），不回傳 403，理由同上。**檢查順序 MUST 固定為**：(1) QR 簽章驗證（既有行為，只以路徑的 ticketId 與簽章金鑰計算，不查詢資料庫，不揭露任何票券是否存在）→ (2) `GetForUpdateAsync` 鎖定並載入 Ticket，不存在回 404 → (3) **歸屬核對**，不屬於呼叫端目前 Organizer 回 404 → (4) 既有狀態檢查，非 `Issued` 回 409。歸屬核對 MUST 在狀態檢查之前：若先做狀態檢查，Organizer A 對 Organizer B 已核銷過的票券會得到可判別的 409，等於用核銷端點刺探出「這張票存在且已被核銷」，正是本段要避免的跨租戶資訊洩漏（見 `RDM-AUTHZ-005`）。歸屬核對放在鎖定之後，是為了沿用既有「交易內鎖定 Ticket 後才判斷」的單一路徑，不另外在交易外多查一次
- 銷售報表查詢：查詢以 `eventId` 為路徑參數，只需直接核對該 `Event.OrganizerId` 是否等於呼叫端目前 `OrganizerId`（不需像訂單/票券那樣經過多層 join），不屬於時 MUST 視同找不到（404），理由同上

**替代方案**：維持訂單／核銷／銷售報表為 Admin-only，留給後續提案。
**選擇擴大範圍的理由**：維持現狀會讓自助申請主辦方這個計畫的核心情境，在最基本的「查看自己的訂單、核銷自己的票、看自己的銷售報表」上完全不可用，且與整個計畫「落實多租戶隔離」的訴求直接矛盾。
**Trade-off**：增加 `GetOrdersHandler`／`GetOrderByIdHandler`／`RedeemTicketHandler`／`GetEventSalesReportHandler` 四個 Handler 的修改、`IOrderRepository` 兩個新查詢方法（`GetByOrganizerIdAsync`／`GetOrganizerIdByOrderItemIdAsync`）與對應測試，但沒有新增資料庫欄位或遷移成本（`Order.EventId`／查詢用的 `eventId` 皆是既有欄位或既有路徑參數，`Event.OrganizerId` 是 `event-management-organizer-scoping` 已新增的欄位，join 或直接核對即可取得答案）。

### 2. 停權延遲曝險視窗比照 event-management 的既知取捨
本次三個能力套用的是同一個 `RequireOrganizerContext` Policy：它只驗證 `OrganizerId` claim 格式，不對每次請求即時查詢該 Organizer 目前狀態（見 `organizer-management` 主 spec「`RequireOrganizerContext` Authorization Policy MUST 驗證 claim 格式並 fail-closed」與已歸檔 `event-management-organizer-scoping` design.md Decision 4）。因此 Organizer 被停權後，其成員手上停權前已核發、尚未過期的 Access Token，在自然到期（`AccessTokenExpirationMinutes`，現行預設 30 分鐘）前仍可能通過本次三個能力的端點——包括查看訂單與買家資訊、核銷票券、查看銷售報表。

**選擇沿用而不額外即時查表的理由**：與 event-management 相同（Rule 2 Simplicity First、維持 Claim 換發模式的一致性與效能）；停權後的換發與切換已由 `organizer-management` 的 `ORG-REFRESH-003`／`ORG-SUSPEND-001` 立即阻擋，曝險上限即 Access Token 效期。**本次三個能力比 event-management 多了買家個資（訂單）與不可逆的狀態變更（核銷）**，延遲視窗內的影響因此較大，但仍屬同一個有界、已知的取捨；若日後需要「停權立即生效」，應在 Policy 層統一改為即時查表或引入 Token 撤銷機制，另開變更處理，不在本次各端點各自補查。

**風險接受**：專案負責人已於 2026-09-29 確認接受此延遲視窗。接受範圍包括停權後、Access Token 自然到期前（現行預設 30 分鐘）仍可查看訂單與買家資訊、查詢銷售報表、執行不可逆的票券核銷。

每個能力各自以一個 Scenario 明確記錄並測試這個延遲視窗（`ORD-AUTHZ-004`、`RDM-AUTHZ-006`、`RPT-AUTHZ-005`），避免被誤認為 Policy 漏檢查。

### 3. 前端入口一併開放給已切換 Organizer 的成員（路由、導覽選單、銷售報表入口）
`event-management-organizer-scoping` 為避免非 Admin 使用者看到「點了只會被導回首頁」的入口，把訂單、核銷頁面的導覽選單（`AdminLayout.vue`）與活動列表上的銷售報表入口（`EventListPage.vue`）限制為只對 `Admin` 顯示，銷售報表路由也維持 `requiresAdmin`。本次後端三個能力改為 `RequireOrganizerContext` 後，若前端不同步調整，自助 Organizer Owner 在正常操作流程中仍看不到、也進不去這些頁面，Goals 不成立。因此：
- **路由守衛**：訂單列表／明細、核銷、銷售報表頁面從 `requiresAdmin` 改為與活動、場館頁面相同的 `requiresOrganizerContext`
- **導覽選單**：「訂單管理」「票券核銷」比照「場館管理」「活動管理」，不再依 `isAdmin` 隱藏（進入後由路由守衛把關）；「主辦方審核」維持只對 `Admin` 顯示（審核頁仍要求 `Admin` 角色）
- **銷售報表入口**：活動列表上的「銷售報表」入口不再依 `isAdmin` 隱藏；活動列表本身已要求已切換 Organizer，且只列出目前 Organizer 名下的活動

### 4. 手動輸入核銷（不驗簽章）的信任對象從 Admin 擴大為 Organizer 成員
核銷端點的 `signature` 為選填（既有 `ticket-redemption`「核銷 API 可選驗證 QR 簽章內容」），核銷頁的手動輸入路徑固定帶 `null`、只憑 Ticket ID 核銷；既有 `admin-web-ui` 把這條路徑定義為「Admin 信任操作」。本次授權改為 `RequireOrganizerContext` 後，能走這條路徑的人從「平台 Admin」擴大為「任何已切換至 Approved Organizer 的成員」（含自助申請的一般 Member）。

**選擇接受、不額外限制的理由**：(1) 本次新增的歸屬核對讓不驗簽章的核銷也只能作用在呼叫端自己 Organizer 名下的票券，無法跨租戶；(2) 手動輸入是現場 QR Code 毀損時的必要備援，入場核銷的實際操作者本來就是主辦方人員而非平台 Admin，若限制為 Admin 才能手動輸入，主辦方在現場將失去備援手段；(3) 主辦方成員核銷自家票券屬於主辦方自己的營運責任範圍。**殘餘風險**：主辦方成員可在未持有 QR Code 的情況下核銷自家任一張已知 Ticket ID 的票（例如誤操作），此風險由主辦方自行承擔，本次不引入 Organizer 內部的角色區分（例如只有 Owner 可手動核銷）；若日後需要，另開變更處理。

因此 `admin-web-ui` 手動輸入 Requirement 與介面標示中的「Admin 信任操作」一併改為「操作人員信任操作」，避免文件與畫面仍暗示只有平台 Admin 能執行。

## 安全確認（CLAUDE.md 安全強制規則）

**輸入驗證**
- 本次不新增任何外部輸入欄位；既有路徑參數（訂單 `id`、票券 `id`、銷售報表 `eventId`）沿用 ASP.NET Core 路由的 `{id:guid}` 型別約束，格式不合法時由既有規則處理（核銷端點的既有 404 規則不變）；核銷既有的 `signature` request body 欄位驗證規則不變
- 新增的歸屬核對不接受前端輸入的 `OrganizerId`，一律取自 Access Token claim（統一透過既有 `ClaimsPrincipalExtensions.TryGetOrganizerId` 解析，比照 `event-management-organizer-scoping`：取得失敗時 Controller 回傳 `Forbid()`，fail-closed）與資料庫既有欄位
- 沒有任何輸入被拼接進 SQL 或 shell 指令

**權限**
- 「已切換至一個 Approved Organizer」的檢查在 WebApi 層以既有 `RequireOrganizerContext` Policy 執行，套用範圍新增本次的三個能力
- 「這筆訂單／票券是否屬於呼叫端目前 Organizer」需要 join `Event.OrganizerId` 才能判斷，在 Application 層的 Handler 內執行

**資料庫**
- `GetOrdersHandler` 改用新的 `IOrderRepository.GetByOrganizerIdAsync`，以單一 `JOIN Events ... WHERE Events.OrganizerId = @organizerId` 查詢在資料庫端過濾（EF Core LINQ，參數化），`Items` 以 `Include` 一併載入，不逐筆額外查詢
- `GetOrderByIdHandler`：既有單筆訂單查詢後，多一次以 Id 查單筆 Event 的既有查詢（`IEventRepository.GetByIdAsync`）
- `RedeemTicketHandler`：在既有 `GetForUpdateAsync` 之後，多**一次**投影查詢 `GetOrganizerIdByOrderItemIdAsync`（`OrderItems JOIN Orders JOIN Events`，只回傳 `OrganizerId`），位於持鎖交易內，刻意維持單次往返以縮短鎖持有時間
- `GetEventSalesReportHandler`：既有已查出的 `Event` 直接讀 `OrganizerId`，不新增查詢
- 以上皆為單筆場景，不構成迴圈內查詢，沒有 N+1 風險

**前端**
- 路由守衛擴大套用範圍、導覽選單與銷售報表入口的顯示條件調整，皆不涉及使用者輸入渲染；API 呼叫沿用既有統一攔截器帶入 Auth Header

## 既有文件用詞落差（非本次修改範圍）

`admin-web-ui` 的核銷掃碼頁、場館與座位圖、活動與票種等既有 Requirement，其 Scenario 仍以「Admin」稱呼操作者。本次只更正會與新路由規則直接矛盾的權限敘述（審核頁引用已改名的舊 Requirement 名稱）；核銷掃碼與手動輸入兩條 Requirement 因權限直接改變（見 Decision 4），本次一併改寫其操作者稱謂為「操作人員」（已切換 Organizer 的成員），並將掃描 Requirement 更名。場館、活動等其他 Requirement 則不逐一改寫其 Scenario 的操作者稱謂（比照 `event-management-organizer-scoping` design.md 同名段落的取捨）；實際可操作者以「後台路由僅限已切換 Organizer 或審核頁面的 Admin 進入」Requirement 與各能力的授權規則為準。

## Risks / Trade-offs

- [既有整合測試依賴「Admin 角色即可呼叫這三個能力端點」的假設] → 實作階段需要先盤點並更新這些測試的前置條件（已知清單見 tasks.md 4.1）
- [停權延遲視窗內可查看買家個資、執行不可逆的核銷] → 見 Decision 2，屬已知、有界（Access Token 效期）的取捨，以 Scenario 與測試明確記錄
- [不驗簽章的手動核銷開放給所有 Organizer 成員] → 見 Decision 4，以歸屬核對限制在自家票券，殘餘誤操作風險由主辦方承擔
- [平台 Admin 失去跨租戶訂單查詢] → 見 Non-Goals，刻意移除，不提供替代
- [跨租戶核銷請求會先對其他 Organizer 的票券加列鎖] → 歸屬核對刻意放在 `GetForUpdateAsync` 之後（Decision 1），因此 Organizer A 對 B 的 ticketId 呼叫核銷時，會短暫鎖住 B 的 Ticket 列再回 404；「存在但不屬於」也比「不存在」多一次查詢，理論上有時間差。兩者都需要先知道對方票券的 GUID，且鎖只持有到單次投影查詢結束，接受為已知取捨，本次不另外在交易外預先核對
