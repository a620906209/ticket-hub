## Why

`docs/project-scope.md` 第 2、4 節將「Email 通知（`INotificationService` 介面，票券產出後通知買家）」列為 Phase 2（Should）項目。訂單確認付款、電子票券產出的流程（`ConfirmOrderHandler`）已經完成，但買家目前完全不會收到任何「票已經出了」的通知，只能自己回「我的訂單」頁面查看。Phase 2 其餘項目（銷售報表、排隊機制、登入 Rate limiting）皆已完成合併，這是 Phase 2 最後一項。

## What Changes

- 新增 `IEmailNotificationService` 介面（Domain 層），代表「透過 Email 通知買家票券已產出」這個能力——介面直接以 Email 為抽象邊界（方法簽章包含 `toEmail`），本次不假裝管道無關；若未來要加其他管道（簡訊、App 推播），屆時再新增獨立介面，不強行套用同一個抽象（見 design.md 決策 1）
- 新增 `MockEmailNotificationService`（Infrastructure 層）：**不架設真實 SMTP mail server**，比照既有 `IPaymentGateway`/`MockPaymentGateway` 的作法，用結構化 log 記錄「原本會寄出的通知內容」（收件信箱以遮蔽格式記錄、活動名稱、訂單 Id、票券張數），並提供 `AlwaysSucceed` 設定切換以模擬寄送失敗情境；這是本次唯一的實作，**不具備真實寄信能力**
- `OrderService.ConfirmOrderAsync` 在訂單確認交易成功提交後呼叫通知服務；**通知失敗 MUST NOT 影響訂單確認本身的成功結果**——買家已經付款成功、票已經出了，不應該因為通知系統的問題被回報失敗，失敗以結構化 log 記錄後吞掉例外，不重試、不進佇列
- 只在**訂單確認（Confirm）**觸發通知，**取消訂單（Cancel）不觸發**任何通知——project-scope 定義的範圍是「票券產出後通知買家」，沒有涵蓋取消通知
- 不新增任何 API 端點、不新增前端頁面——這是純後端的訂單確認流程副作用，買家透過既有「我的訂單」頁面查看票券本身；不在本次範圍內建立通知歷史記錄查詢功能

## Capabilities

### New Capabilities
- `email-notification`：訂單確認付款成功後，系統透過 Email 通知買家票券已產出；通知本身的成功或失敗不影響訂單確認結果。**本次僅有 Mock 實作（結構化 log，不架設真實 SMTP server），不提供真實寄信能力**——買家實際上不會收到信，此能力目前只驗證通知邊界與流程整合，真實寄信留待未來串接 ESP 時另開提案

### Modified Capabilities
（無——`ticket-ordering`（訂單確認的既有 Requirement）行為本身不變，只是在確認成功後多一個不影響結果的副作用；不修改任何既有 Requirement 的內容）

## Impact

- 新增 Domain：`IEmailNotificationService`（`ProjectC.Domain.Notifications`）
- 新增 Infrastructure：`MockEmailNotificationService`、`MockEmailNotificationServiceOptions`（比照 `MockPaymentGatewayOptions` 的 `AlwaysSucceed` 設定慣例）、`EmailMasker`（log 遮蔽的邊界輸入防呆，見 design.md 決策 5）
- 新增 Application：`TicketIssuedNotificationContent`/`TicketIssuedNotificationContentFactory`（組裝通知內容並驗證資料完整，取代原本用 `!` 假設資料必然存在的做法，見 design.md 決策 2）
- 修改 `OrderService`：新增依賴 `IEmailNotificationService`、`IApplicationDbContext`（查買家 Email，比照 `GetAdminEventsHandler` 查會員顯示名稱的既有慣例）、`ILogger<OrderService>`（Application 層首次引入 `ILogger`，記錄通知失敗）；`ConfirmOrderAsync` 在既有交易提交後新增呼叫通知服務的邏輯
- 修改 `ProjectC.Application.csproj`：明確新增 `Microsoft.Extensions.Logging.Abstractions` 的 `PackageReference`（並在 `Directory.Packages.props` 新增對應 `PackageVersion`）——目前只透過 EF Core 間接帶入，不應依賴 transitive package 恰好存在（見 design.md Context）
- `Program.cs` 新增 `IEmailNotificationService`/`MockEmailNotificationServiceOptions` 的 DI 註冊，比照 `IPaymentGateway`/`MockPaymentGatewayOptions` 既有登錄方式
- 不需要 EF Core migration（不新增任何持久化欄位）；不影響既有訂單建立/取消/座位鎖定相關行為與既有測試
