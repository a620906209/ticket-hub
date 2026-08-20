## Context

Phase 1 主流程最後一塊：訂單付款成功（`ConfirmOrderHandler.Handle`，`src/ProjectC.Application/Orders/ConfirmOrderHandler.cs`）後需要出票，現場/線上核銷需要一支 API 驗票放行。專案現有兩個可直接沿用的模式：
- 悲觀鎖 + 交易內序列化（`ITicketTypeRepository.GetForUpdateAsync`／`IEventSeatRepository.GetForUpdateAsync`，鎖後 `ReloadAsync` 重讀，見 `OrderService.ChangeOrderStatusAsync`）
- Repository 介面定義在 Domain、實作在 Infrastructure（`IPaymentGateway`／`MockPaymentGateway` 為近例）

角色模型目前只有 `MemberRole.Member` / `MemberRole.Admin` 兩種（`src/ProjectC.Domain/Members/MemberRole.cs`），沒有獨立的「檢票人員/主辦方」角色。

## Goals / Non-Goals

**Goals:**
- 訂單確認付款成功後，依 `OrderItem` 購買數量自動出票（座位項目 1 張、計數項目依 `Quantity` 出多張各自可核銷的票）
- 每張票可**按需**產生 QR Code（內容為 HMAC 簽章過的 Ticket ID），供離線驗證票券未被竄改；QR 內容/圖檔可隨時依 Ticket ID 重新推導，出票交易本身不預先產生或持久化
- 提供核銷 API，處理併發防重複核銷與非法狀態轉換

**Non-Goals:**
- `Voided` 狀態的觸發路徑（退款/已付款訂單取消，待未來提案）
- 現場核銷掃碼前端頁面（Could，`docs/project-scope.md`）
- 獨立的檢票人員/主辦方角色（本次核銷 API 僅開放 `Admin`，見決策 5）
- QR Code 圖檔的傳遞管道（Email 通知為 Should，本次不含）與預先產生：本次只提供依 Ticket ID **按需**產生 QR 內容與圖檔的 Infrastructure service，出票交易（決策 1）不呼叫此服務、不含發送
- **核銷 API 驗證 QR 簽章內容**：本次 `PATCH /api/admin/tickets/{id}/redeem` 直接以路徑參數 `id` 查詢資料庫狀態作為核銷依據，不要求呼叫端附帶簽章內容、也不在核銷流程中呼叫 `ITicketSigningService.TryVerify`（見決策 3 補充）

## Decisions

### 決策 1：出票時機與方式——同一交易內直接呼叫，不引入事件/佇列機制
在 `ConfirmOrderHandler.Handle` 內、`order.Confirm()` 成功後，於同一個 DB transaction 內為每筆 `OrderItem` 建立對應張數的 `Ticket` 並透過 `ITicketRepository.Add` 加入。**此步驟僅建立 `Ticket` entity（狀態 `Issued`、記錄 `IssuedAtUtc`），不呼叫 `ITicketSigningService` 或 QR 圖檔產生服務**——QR 內容/圖檔按決策 3 為按需（on-demand）產生，出票交易不預先產生或持久化，避免把無消費者的呼叫塞進付款確認交易，也避免付款確認因 QR/簽章邏輯而多一個非必要的失敗點。

**理由**：專案目前沒有 domain event bus／outbox 基礎設施，既有的「座位確認 + 訂單確認」就是同一交易內直接呼叫（見 `ConfirmOrderHandler.Handle:72-76`），出票邏輯比照辦理維持一致，避免為單一功能引入新架構模式（CLAUDE.md「不得憑直覺選擇」「避免過度設計」）。

**替代方案（不採用）**：Domain Event + 背景 Worker 非同步出票——可解耦但需新增 outbox/event bus，超出本次範疇也偏離既有模式。

### 決策 2：Ticket 與 OrderItem 的對應——`Ticket.OrderItemId`，多對一
`Ticket` 不與 `Order` 直接關聯，而是關聯到 `OrderItem`（`Ticket.OrderItemId`），因為計數項目 `Quantity` 可能 > 1，需要出多張各自獨立可核銷的票；`Ticket` 透過 `OrderItem` 間接取得所屬 `Order`／`Event`。

### 決策 3：QR Code 內容與簽章金鑰管理
- QR 內容格式：`{TicketId:D}.{Base64Url(HMAC-SHA256(TicketId, key))}`
- 簽章與驗章邏輯定義為 `Domain.Tickets.ITicketSigningService`（介面），實作 `HmacTicketSigningService` 放 `Infrastructure`，比照 `IPaymentGateway`／`MockPaymentGateway` 的既有模式
- 簽章金鑰透過 `IOptions<TicketSigningOptions>` 注入（DI 生命週期表：`IOptions<T>` 屬 Singleton），本地開發由 compose 的 env_file 注入，不寫死於程式碼或進版控設定檔（CLAUDE.md 機敏資訊管理）
- `TicketSigningOptions` 比照既有 `JwtOptions`（`Program.cs:70-75`）以 `ValidateDataAnnotations().ValidateOnStart()` 做啟動時 fail-fast 驗證：簽章金鑰欄位必填、最小長度限制（HMAC-SHA256 建議至少 32 bytes），金鑰缺失或過弱時應用程式啟動失敗，不允許執行期才發現
- QR 圖檔產生使用 QRCoder 套件，在 `Infrastructure` 層實作，`Domain`/`Application` 不依賴該套件（維持分層依賴方向）；此服務為**按需（on-demand）**——僅接受 Ticket ID 產生簽章內容與對應圖檔，本次範疇內沒有任何呼叫路徑會在出票交易中呼叫它（見決策 1），不持久化圖檔、不提供對外取得 QR 的端點。此服務本次僅要求「邏輯本身正確可用」（單元測試驗證），實際消費者（買家票券查詢、現場掃碼前端）待對應能力另開提案時才會呼叫
- **驗章的實際消費者與本次範疇的關係**：`ITicketSigningService.TryVerify` 本次只提供介面與實作、以單元測試驗證簽章/驗章邏輯本身正確，但**本次範疇內沒有任何呼叫路徑會使用它**——核銷 API（決策見 Non-Goals）直接以路徑參數 `id` 查資料庫狀態，不驗證簽章內容。設計動機：QR 簽章的防偽價值在於讓「現場掃碼前端」（Could，本次範疇外）能在不即時打 API 的情況下離線判斷 QR 內容是否為系統產出、未被竄改；本次核銷是線上 API 呼叫，資料庫狀態本身就是權威來源，即使沒有驗章，偽造一個不存在或非 `Issued` 狀態的 Ticket ID 一樣會被資料庫查詢擋下。待現場掃碼前端開發時，`TryVerify` 才會被實際整合進使用流程

### 決策 4：核銷併發控制——沿用既有悲觀鎖模式，不導入 RowVersion
新增 `ITicketRepository.GetForUpdateAsync(Guid ticketId, CancellationToken)`（單筆鎖定），核銷流程：開交易 → 依 ID 鎖定並讀取 Ticket（單筆 `SELECT ... FOR UPDATE` 已同時完成鎖定與讀取最新狀態，查無資料則回報 404，不進入後續步驟；與 `Order` 需要額外 `ReloadAsync` 不同——`Order` 是靠鎖定關聯的 `EventSeat`/`TicketType` 間接序列化，自己並未被鎖，Ticket 是直接鎖定自己，沒有這個落差）→ 檢查 `Status == Issued` → 非 `Issued`（含 `Redeemed`；`Voided` 本次不可達，但邏輯不特化排除它，避免未來上線後遺漏）則回報衝突 → 轉為 `Redeemed` → commit。

**理由**：與 `OrderService.ChangeOrderStatusAsync`「鎖定 → 檢查狀態 → 變更 → commit」的整體骨架精神一致（Rule 11：符合既有慣例優先於各自取捨），差別僅在於 `Order` 的鎖定對象是關聯的 `EventSeat`/`TicketType`（間接序列化，故需額外 `ReloadAsync` 重讀 `Order` 自己），Ticket 核銷是直接鎖定 Ticket 自己，鎖定查詢本身已是最新狀態，不需要重讀這一步；核銷是單一 Entity 操作，也不需要 `TicketType`／`Seat` 那種多實體固定順序取鎖規則。

**替代方案（不採用）**：`RowVersion` 樂觀併發（CLAUDE.md 提及為選項之一）——技術上可行，但會與既有 Order/TicketType/Seat 的悲觀鎖模式不一致，增加專案內兩套併發控制策略的認知負擔，故不採用。

### 決策 5：核銷 API 權限與路由——本次僅開放 `Admin`，比照既有 Admin 端點掛在 `/api/admin/` 前綴
`PATCH /api/admin/tickets/{id}/redeem`（獨立 `AdminTicketsController`）要求呼叫者為已登入且 `MemberRole = Admin`。路由參數採 `{id:guid}` 限制，比照全專案既有 Admin/Order 端點無一例外的慣例（`AdminOrdersController.cs:30`、`AdminEventsController.cs:43`、`AdminVenuesController.cs:58/65`、`AdminMembersController.cs:23/30`、`OrdersController.cs:28/35`），非合法 GUID 格式的路徑參數在進入 Controller 前即由路由比對失敗、統一回傳 404，與「Ticket 不存在」的 404 語意一致，不需額外分辨。核銷成功回應採 `204 No Content`，比照既有 `ConfirmOrder`／`CancelOrder`（`OrdersController.cs:28-40`，皆用 `result.ToActionResult()` 無回傳值）——本次範疇內沒有任何消費端需要核銷回應 body（現場掃碼前端不在本次範疇），故不新增回傳欄位。

**理由**：系統目前只有 `Member`／`Admin` 兩種角色，沒有獨立的「檢票人員」或「主辦方」角色；`docs/project-scope.md` 對「平台管理員」的定位是「簡化版異常訂單監控」，核銷操作性質上更接近管理端操作而非買家自助操作，暫時掛在 `Admin` 底下是最小可行做法。路由與 Controller 命名比照專案既有慣例——所有 Admin-only 端點皆獨立成 `AdminXController` 掛在 `/api/admin/x`（`AdminOrdersController`＝`/api/admin/orders`、`AdminEventsController`＝`/api/admin/events`、`AdminMembersController`＝`/api/admin/members`、`AdminVenuesController`＝`/api/admin/venues`），無例外；`Ticket` 目前沒有既有的買家端 Controller，故不存在命名空間衝突（Rule 11：符合既有慣例優先於各自取捨）。

**Open Question**：是否需要獨立的檢票人員/主辦方角色（可授權特定活動的核銷），待後續視實際需求另開提案——不在本次範疇內展開設計。

### 決策 6：`Ticket` 狀態機——本次僅實作 `Issued → Redeemed`
如 Goals/Non-Goals 所述，`Voided` 狀態本次不實作任何觸發路徑；`Ticket` entity 定義完整三態列舉（`Issued`/`Redeemed`/`Voided`）與狀態轉換方法骨架，但只有 `Redeem()` 這一個公開轉換方法會在本次範疇內被呼叫。避免定義了呼叫不到的 API 或流程，`tasks.md` 不會為 `Voided` 觸發路徑安排測試任務。

## Risks / Trade-offs

- **[風險] `Admin` 角色兼管核銷與異常訂單監控，權限粒度過粗** → 緩解：本次範疇內可接受（系統只有這一個管理角色），Open Question 已記錄，未來需要更細緻角色時再另開提案調整，不影響本次 API 設計的向後相容性（授權邏輯集中在 Controller/Policy 層，之後只需調整 Policy 定義）
- **[風險] 出票邏輯與訂單確認在同一交易內，若出票邏輯拋例外會讓整筆付款確認一併回滾** → 緩解：這是刻意接受的行為（付款成功但出票失敗，應視為整筆確認失敗要求重試，而非留下「已付款卻沒有票」的不一致狀態），比照決策 1 的一致性考量；出票交易內僅建立 `Ticket` entity（產生 Guid、寫入初始狀態），不呼叫簽章或 QR 服務（決策 1），是純記憶體運算，不涉及外部網路 I/O，失敗機率極低
- **[風險] `Voided` 狀態定義了卻沒有觸發路徑，可能被誤認為未完成功能** → 緩解：design.md 與 proposal.md 已明確記錄為 Non-Goal 並說明原因，`tasks.md` 不會建立對應此路徑的測試任務，避免「有欄位沒行為」的誤解

## Migration Plan

- 新增 EF Core migration：`Tickets` table（`Id`、`OrderItemId`、`Status`、`IssuedAtUtc`、`RedeemedAtUtc`），`OrderItemId` 外鍵至既有 `OrderItems` table
- 純新增 table，不變更既有 schema，無需資料回填，可安全套用於既有資料庫
- Rollback：`dotnet ef database update <上一個 migration>` 會直接移除整個 `Tickets` table。**僅在確認尚無需保留的 Ticket 資料時**（例如套用後尚未出票、或僅測試環境）才可直接降版；一旦系統已建立過任何 Ticket（已出票或已核銷），降版會連同出票與核銷歷程一併遺失，此時應先備份資料或改採 forward-fix（新增修正性 migration），不應直接降版
