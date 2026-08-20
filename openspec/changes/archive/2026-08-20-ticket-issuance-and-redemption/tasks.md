## 1. Domain 層：Ticket Entity 與狀態機

- [x] 1.1 建立 `TicketStatus` enum（`Issued`、`Redeemed`、`Voided`）於 `Domain.Tickets`
- [x] 1.2 建立 `Ticket` entity：公開建構子（出票用，狀態初始為 `Issued`，記錄 `IssuedAtUtc`）、EF 物化用 private 建構子（比照 `Order`/`OrderItem` 既有模式）、`OrderItemId` 關聯、`Redeem(DateTime now)` 方法（僅 `Issued` 可轉 `Redeemed`，否則拋既有模式風格的領域例外）
- [x] 1.3 建立 `TicketNotIssuedException`（核銷非 `Issued` 狀態時拋出，比照 `OrderNotPendingException` 命名與結構）
- [x] 1.4 定義 `ITicketRepository` 介面（`Add`、`GetForUpdateAsync(Guid ticketId, CancellationToken)` 單筆鎖定）——**實作階段調整**：拿掉原規劃的 `ReloadAsync`，因為 Ticket 是直接被鎖定的單一 entity（不像 `Order` 是靠鎖定關聯的 `EventSeat`/`TicketType` 間接序列化、自己需要鎖後重讀），`GetForUpdateAsync` 單筆查詢本身已同時完成「鎖定＋讀取最新狀態」，多一個 `ReloadAsync` 會是永遠呼叫不到的方法
- [x] 1.5 定義 `ITicketSigningService` 介面（`Sign(Guid ticketId) : string`、`TryVerify(string? content, out Guid ticketId) : bool`）——**審查後修正**：`content` 改為 nullable，比照 .NET Try-pattern 慣例（`Guid.TryParse` 等）對 null／空字串 MUST 回傳 `false` 而非拋例外；`HmacTicketSigningService` 與對應測試已同步補上

## 2. Infrastructure 層：持久化與簽章實作

- [x] 2.1 新增 `TicketConfiguration`（EF Fluent API），設定 `OrderItemId` 外鍵、`Status` 轉換、索引
- [x] 2.2 實作 `TicketRepository`（`GetForUpdateAsync` 沿用既有單筆悲觀鎖模式，比照 `TicketTypeRepository`/`EventSeatRepository` 的鎖定寫法）
- [x] 2.3 新增 EF Core migration（`Tickets` table），確認 `Down()` 正確可回滾——純新增 table，不像 `AddTicketTypeRequiresSeat` 那樣有 FK 回填的不安全情境，`Down()` 就是單純 `DropTable`，不需要額外 guard；已在開發資料庫實測 update → rollback → 重新 update 皆成功
- [x] 2.4 安裝 QRCoder 套件（NuGet），僅加在 `Infrastructure.csproj`
- [x] 2.5 建立 `TicketSigningOptions`（含簽章金鑰欄位，DataAnnotations 標註必填與最小長度），以 `IOptions<TicketSigningOptions>` 綁定設定並比照既有 `JwtOptions`（`Program.cs:70-75`）加上 `ValidateDataAnnotations().ValidateOnStart()`，金鑰來源為 compose env_file（不寫死），缺失或過弱時應用程式啟動失敗（fail-fast，見 design.md 決策 3）——同步補上 appsettings.json/appsettings.Development.json/docker-compose.yml/.env(.example)/`CustomWebApplicationFactory` 測試設定；新增 `TicketSigningOptionsFailFastTests`（比照既有 `JwtOptionsFailFastTests`）驗證金鑰缺失時啟動失敗，並修正 `JwtOptionsFailFastTests` 補上合法 `TicketSigning:SigningKey`——否則兩個 Options 同時 fail-fast 會讓宿主丟出包住兩個 `OptionsValidationException` 的 `AggregateException`，讓原本只測 Jwt 的斷言產生歧義（實測跑到才發現的既有測試回歸）
- [x] 2.6 實作 `HmacTicketSigningService : ITicketSigningService`（HMAC-SHA256 簽章／驗章）
- [x] 2.7 實作 QR 圖檔產生服務（輸入 Ticket ID，呼叫 `ITicketSigningService.Sign` 取得內容字串，交給 QRCoder 產生圖檔）；**按需（on-demand）產生**，QR 內容可由 Ticket ID 隨時重新推導（決策 3），本次不在出票交易中呼叫此服務（決策 1）、不持久化圖檔、不提供對外取得 QR 的端點——票券查詢/取得屬於買家訂單查詢能力（`docs/project-scope.md` 第 9 節已知缺口，待另開提案），本次僅確保產生邏輯本身正確可用（見 5.8 測試）
- [x] 2.8 DI 註冊：`ITicketRepository`（Scoped，比照其他 Repository）／`ITicketSigningService`（Singleton，比照 `IPaymentGateway`/`MockPaymentGateway` 無狀態既有模式）／`TicketQrCodeGenerator`（Transient，無狀態工具類別，比照 Validator/Mapper 判準）／`TicketSigningOptions`（`IOptions<T>` 解包為 Singleton，比照 `AuthOptions`/`OrderCleanupOptions`/`MockPaymentGatewayOptions`）

## 3. Application 層：出票與核銷邏輯

- [x] 3.1 在 `ConfirmOrderHandler.Handle`（`order.Confirm()` 成功後、同一交易內）依 `OrderItem.Quantity` 建立對應張數的 `Ticket` 並呼叫 `ITicketRepository.Add`；此步驟 MUST NOT 呼叫 `ITicketSigningService` 或 QR 圖檔服務（見 design.md 決策 1，QR 內容/圖檔為按需產生，不在出票交易內產生）——`ConfirmOrderHandler` 建構子新增 `ITicketRepository` 參數，已同步修正所有既有 `new ConfirmOrderHandler(...)` 呼叫點（`OrderServiceTests`/`ConfirmOrderHandlerTests`/`CancelOrderHandlerTests`/`OrderServiceConcurrencyTests`/`TicketTypeConcurrencyTests`），新增 `FakeTicketRepository` 測試替身
- [x] 3.2 新增 `RedeemTicketHandler`：鎖定並讀取（`GetForUpdateAsync` 單筆查詢已同時完成，不需額外 reload）→ 檢查 `Status == Issued` → 呼叫 `Ticket.Redeem()` → commit（交易骨架沿用 `IUnitOfWork.BeginTransactionAsync`，比照 `OrderService.ChangeOrderStatusAsync`／`ConfirmOrderHandler`/`CancelOrderHandler` 既有模式）
- [x] 3.3 `RedeemTicketHandler` 對不存在的 Ticket ID 回傳 `Result.Failure(Error.NotFound(...))`

## 4. WebApi 層：核銷端點

- [x] 4.1 新增 `AdminTicketsController`，`PATCH /api/admin/tickets/{id:guid}/redeem`（路由參數採 `{id:guid}` 限制，比照全專案既有 Admin/Order 端點慣例，非合法 GUID 格式由路由層直接回 404，見 design.md 決策 5），比照 `AdminOrdersController`/`AdminEventsController` 的路由與 Authorization Policy 慣例（Admin-only 端點統一掛在 `/api/admin/` 前綴、獨立 Controller），套用 `Admin` 角色的 Authorization Policy
- [x] 4.2 Controller 僅負責 HTTP 邊界轉換（呼叫 `RedeemTicketHandler`，將 `Result` 映射為對應 HTTP 狀態碼／`ProblemDetails`；成功比照 `ConfirmOrder`/`CancelOrder` 用 `result.ToActionResult()` 回傳 `204 No Content`，不含回應 body），不含業務邏輯

## 5. 測試：ticket-issuance（對應 specs/ticket-issuance/spec.md）

- [x] 5.1 [單元測試，Domain 層補充測試，非對應特定 Scenario] `Ticket` 初始狀態為 `Issued`，`Redeem()` 從 `Issued` 轉 `Redeemed` 成功，從 `Redeemed` 再次呼叫拋 `TicketNotIssuedException`——驗證 entity 自身狀態機邏輯，與 6.4/6.5 驗證 `RedeemTicketHandler` 完整流程（含鎖定/commit，Ticket 不需要 `Order` 那種鎖後重讀，見 1.4 調整說明）互補而非重複
- [x] 5.2 [單元測試] `ConfirmOrderHandlerTests`：訂單含 3 筆座位項目確認付款成功 → 建立 3 張 `Issued` Ticket，且每張 `OrderItemId` 各自對應到產生它的座位項目（對應 Scenario「座位訂單出票數量正確」）——**標籤修正**：`ConfirmOrderHandlerTests.cs` 全檔皆用 Fake repository、不碰 DB，實際是單元測試而非整合測試，原 tasks.md 標籤有誤，已一併修正 5.3-5.5
- [x] 5.3 [單元測試] `ConfirmOrderHandlerTests`：訂單含購買數量 5 的計數項目確認付款成功 → 建立 5 張獨立 Ticket，`OrderItemId` 皆對應到該計數項目（對應 Scenario「計數項目依購買數量出票」）
- [x] 5.4 [單元測試] `ConfirmOrderHandlerTests`：混合 2 筆座位項目 + 購買數量 3 的計數項目 → 共建立 5 張 Ticket，斷言其中 2 張 `OrderItemId` 分別對應各自座位項目、另 3 張 `OrderItemId` 皆對應計數項目，不得有 Ticket 關聯到錯誤的 `OrderItem`（對應 Scenario「混合項目出票數量加總正確且各自歸屬正確的項目」）
- [x] 5.5 [單元測試] `ConfirmOrderHandlerTests`：`IPaymentGateway` 回報付款失敗 → 不建立任何 Ticket（對應 Scenario「付款失敗不出票」）
- [x] 5.6 [單元測試] `HmacTicketSigningServiceTests`：對依 Ticket ID 產生的內容執行 `TryVerify` 通過並還原正確 Ticket ID（對應 Scenario「依 Ticket ID 產生的 QR 內容可驗章通過」）
- [x] 5.7 [單元測試] `HmacTicketSigningServiceTests`：竄改內容一個字元後 `TryVerify` 回傳 false（對應 Scenario「竄改後的內容驗章失敗」）
- [x] 5.8 [單元測試，補充測試，非對應特定 Scenario] QR 圖檔服務測試：給定 Ticket ID，服務產生非空 PNG bytes；**真正解碼**（`ZXing.Net` + `SixLabors.ImageSharp` 2.1.x，僅加在 `ProjectC.Infrastructure.Tests`，不進 `src`——`ImageSharp` 釘選 2.1.x 是因為 3.x 起改用 Split License 需另外註冊授權金鑰，測試用途不值得這道手續）解碼 PNG 像素還原 QR 內容字串，交給 `ITicketSigningService.TryVerify` 驗證，確認還原出的 Ticket ID 與輸入相符——第一版曾用「獨立重算 `Sign(ticketId)` 比對」規避真解碼，經審查指出這種做法就算 `GeneratePng` 邏輯壞掉（例如編碼寫死或錯誤內容）測試仍會通過，已改為真解碼

## 6. 測試：ticket-redemption（對應 specs/ticket-redemption/spec.md）

- [x] 6.1 [整合測試] `AdminTicketsControllerTests`：Admin 對 `Issued` Ticket 呼叫核銷端點成功（對應 Scenario「Admin 核銷成功」）
- [x] 6.2 [整合測試] `AdminTicketsControllerTests`：`Member` 角色呼叫核銷端點回傳 403，狀態不變（對應 Scenario「一般會員呼叫被拒絕」）
- [x] 6.3 [整合測試] `AdminTicketsControllerTests`：未帶 Authorization Header 呼叫回傳 401（對應 Scenario「未登入呼叫被拒絕」）
- [x] 6.4 [單元測試] `RedeemTicketHandlerTests`：核銷成功後 `Status` 為 `Redeemed` 且 `RedeemedAtUtc` 有值（對應 Scenario「核銷成功轉態並記錄時間」）
- [x] 6.5 [單元測試] `RedeemTicketHandlerTests`：對已 `Redeemed` 的 Ticket 再次核銷回傳衝突錯誤，狀態不變（比照 6.4 同一測試類別/層級，對應 Scenario「對已核銷票券再次核銷」）
- [x] 6.6 [單元測試] `RedeemTicketHandlerTests`：對不存在的 Ticket ID 核銷回傳 404（對應 Scenario「對不存在的票券核銷」）——**標籤修正**：`RedeemTicketHandlerTests` 用 `FakeTicketRepository`/`FakeUnitOfWork`，不碰 DB，實際是單元測試，比照 5.2-5.5 一併修正
- [x] 6.7 [整合測試] `RedeemTicketConcurrencyTests`（新增檔案，`ProjectC.Infrastructure.Tests`）使用 Testcontainers 起真實 DB，模擬兩個並發請求核銷同一張 `Issued` Ticket，斷言只有一個成功、另一個因狀態已變更被拒絕（對應 Scenario「並發核銷同一張票」，比照既有 `OrderServiceConcurrencyTests` 的並發測試手法）
- [x] 6.8 [整合測試] `AdminTicketsControllerTests`：呼叫核銷端點路徑參數為非合法 GUID 格式，回傳 404（對應 Scenario「路徑參數非合法 GUID 格式」）

> **刻意不安排測試的範圍**：`Voided` 狀態本次不實作任何觸發路徑（見 design.md 決策 6、proposal.md Non-Goals），故本節與第 5 節皆不建立任何驗證「Ticket 轉為 `Voided`」的測試任務——這是刻意排除而非遺漏，避免與「每條 Acceptance Criteria 至少對應一項測試」的規範混淆。

## 7. 測試：ticket-purchase（既有 spec 修改，對應 specs/ticket-purchase/spec.md MODIFIED）

- [x] 7.1 擴充既有「買家確認自己的訂單成功」案例，追加斷言：確認成功後依訂單項目購買數量建立對應張數、狀態皆為 `Issued` 的 Ticket——**目標檔案修正**：「買家確認自己的訂單成功」實際對應的既有測試是 `OrderServiceTests.ConfirmOrderAsync_WhenBuyerConfirmsOwnPendingOrder_Succeeds`（走完整 `OrderService.ChangeOrderStatusAsync` 交易骨架），不是 `ConfirmOrderHandlerTests`（那裡測的是 `ConfirmOrderHandler.Handle` 本身，不含買家授權檢查），原 tasks.md 指錯檔案，已在此檔案擴充斷言
- [x] 7.2 擴充既有「付款失敗」測試案例，追加斷言：不建立任何 Ticket——與 5.5 是同一支測試（`ConfirmOrderHandlerTests.Handle_WhenPaymentDeclined_FailsAndDoesNotMarkSeatsSoldOrIssueTickets`），兩條任務對應同一個斷言，不重複新增
- [x] 7.3 擴充既有「非本人確認他人訂單」測試案例，追加斷言：不建立任何 Ticket——**目標檔案修正**：同 7.1，「非本人確認他人訂單」的買家授權檢查在 `OrderService.ChangeOrderStatusAsync`，`ConfirmOrderHandler.Handle` 本身不接收 `requestingBuyerId`、測不到這個情境，實際擴充的是 `OrderServiceTests.ConfirmOrderAsync_WhenCallerIsNotTheBuyer_ReturnsForbiddenAndDoesNotChangeOrder`

## 8. Spec 同步確認

- [x] 8.1 確認 `openspec/changes/ticket-issuance-and-redemption/specs/` 下三份 delta（`ticket-issuance` 新增、`ticket-redemption` 新增、`ticket-purchase` 修改）與最終實作行為一致，無偏差——逐條需求核對過：出票數量與 `OrderItemId` 關聯正確性、QR 按需產生且出票交易不呼叫、核銷權限/狀態轉換/`204`回應/併發防護/GUID 路由格式，皆與實作一致，無需修正 spec 或程式碼
- [x] 8.2 實作完成後，向使用者確認 `docs/project-scope.md` 是否需要更新——已確認並完成：① 第 9 節「Phase 1 Must 盤點快照」電子票券產出、核銷 API 兩項狀態由 ❌ 改 ✅，快照日期與備註更新為本次變更；② 第 3 節 `Ticket` 狀態機描述的 `Voided` 修正為實際範疇（對應退款／已付款訂單取消，本次無觸發路徑，待未來提案），順帶修正同表 `Order` 那列已過時的「待調整」措辭（`Confirmed`→`Paid` 命名對齊其實已於 `order-payment-gateway-alignment` 歸檔完成，原文字沒同步更新）——第 2、4 節核銷端點路由文字已於提案審查階段提前修正，不在此待辦範圍內
