# email-notification Specification

## Purpose
TBD - created by archiving change email-notification. Update Purpose after archive.

## Requirements

### Requirement: 訂單確認成功後通知買家票券已產出
系統 SHALL 在買家訂單確認（付款成功、電子票券產出）之後，透過 `IEmailNotificationService` 通知該訂單的買家。通知內容 SHALL 包含買家的註冊 Email、活動名稱、訂單 Id、以及本次訂單確認流程所對應的總票券張數（依訂單內所有項目的 `Quantity` 加總；座位制項目每筆固定張數為 1，計數制項目每筆可能為多張，兩者加總即為總票券張數——這個固定張數規則由 `ticket-ordering` 能力保證，本能力不重複驗證）。訂單 MUST 至少含一個項目、每個項目的 `Quantity` MUST 至少為 1（既有 `Order`/`OrderItem` 建構子不變量保證），故總票券張數 MUST 至少為 1，不存在「總張數為 0」的情況；買家 Email 由既有會員註冊流程保證為非空值，通知內容 MUST NOT 出現空白或 null 的收件信箱。本能力只驗證買家 Email 為非空值，不另外驗證其格式是否符合 RFC（例如是否含 `@`、網域格式是否合法）——格式驗證留給未來若串接真實 Email 服務供應商時再處理；即使 `Member.Email` 是非空但格式不合法的值，仍視為有效資料傳給 `IEmailNotificationService`，log 記錄時是否遮蔽由「通知 log 不得記錄未遮蔽的完整 Email」這條 Requirement 的規則決定。通知 MUST 在訂單確認交易成功提交之後才觸發，不得在交易提交前發出（避免通知了尚未真正確認成功的訂單）。

本能力不新增任何獨立的對外入口；通知流程完全依附於既有 `ticket-purchase` 能力「確認訂單」端點已完成的登入驗證與買家身分驗證（非訂單買家本人呼叫 MUST 被拒絕，見 `ticket-purchase` 能力 Requirement「買家確認自己所屬的 Pending 訂單」）、以及 `ticket-ordering` 能力的訂單狀態與座位歸屬驗證——只有這些既有驗證與交易提交全部成功之後，才會查詢買家 `Member.Email` 並觸發通知；任何未通過上述驗證的呼叫（例如未登入、非買家本人、訂單不存在、訂單狀態或歸屬驗證失敗）都落在下方「訂單確認失敗不觸發通知」Scenario 涵蓋的範圍內，MUST NOT 查詢買家 Email 或呼叫 `IEmailNotificationService`。

本次通知能力的正式實作為 Mock（`MockEmailNotificationService`）：只記錄遮蔽後的結構化 log，不架設真實 SMTP 伺服器，也不會實際投遞 Email 給買家；真實 Email 投遞能力不屬於本 change 範圍（見 Non-Goals）。

#### Scenario: 訂單確認成功觸發通知
- **WHEN** 買家的訂單確認（付款、座位確售、票券產出）全部成功並完成交易提交
- **THEN** 系統 SHALL 呼叫 `IEmailNotificationService`，通知內容包含該買家的 Email、活動名稱、訂單 Id、票券總張數

#### Scenario: 通知呼叫發生在交易提交之後
- **WHEN** 買家的訂單確認（付款、座位確售、票券產出）全部成功
- **THEN** 在呼叫 `IEmailNotificationService` 的當下，訂單確認的交易 MUST 已經提交完成——透過與確認流程無關的獨立資料庫連線查詢該訂單，SHALL 能看到訂單狀態已經是 `Paid`

#### Scenario: 混合座位制與計數制票種的訂單，票券張數為加總
- **WHEN** 訂單同時包含座位制項目（張數固定為 1）與計數制項目（`Quantity` 可能大於 1）
- **THEN** 通知的票券張數 SHALL 為所有項目 `Quantity` 的加總，而非項目筆數

#### Scenario: 訂單確認失敗不觸發通知
- **WHEN** 訂單確認因任何原因失敗（例如未登入、非訂單買家本人呼叫、付款被拒絕、座位已逾期、訂單已非 Pending 狀態、訂單不存在）
- **THEN** 系統 MUST NOT 呼叫 `IEmailNotificationService`，也 MUST NOT 查詢買家 `Member.Email`

### Requirement: 通知失敗不影響訂單確認結果
系統 SHALL 將通知發送視為訂單確認流程的 best-effort 副作用；通知流程未能完成時，MUST NOT 導致訂單確認回報給買家的結果變成失敗——訂單此時已經確認付款成功、票券已經產出，這是既成事實，不因通知系統的問題而回滾或回報錯誤。`IEmailNotificationService` 介面唯一可觀察的失敗訊號是拋出例外（介面回傳 `Task`，不是 `Result` 型別）；這裡所稱「通知失敗」，SHALL 涵蓋通知資料組裝失敗（例如重新查詢訂單/活動/會員資料失敗、或資料缺漏而拋出例外）與呼叫 `IEmailNotificationService` 時拋出例外兩種情況，兩者皆 SHALL 被記錄（結構化 log，至少含訂單 Id 與例外本身），但 MUST NOT 重試、MUST NOT 讓例外往外傳播至呼叫端。

**呼叫端主動取消請求不屬於「通知失敗」**：僅當通知流程中拋出的 `OperationCanceledException` 是由**這次 `ConfirmOrderAsync` 呼叫本身接收到的 `CancellationToken`** 觸發（即該 token 的 `IsCancellationRequested` 為 `true`）時，才視為呼叫端主動取消，系統 MAY 選擇不將其記錄為錯誤等級的失敗，但訂單確認結果仍 MUST 回報成功；任何其他來源的 `OperationCanceledException`（例如通知服務或其依賴的外部資源自行逾時、內部使用了另一個 token）不滿足這個條件，SHALL 視為一般的「通知失敗」，比照上一段被記錄（見下方 Scenario）。

#### Scenario: 通知服務拋出例外，訂單確認仍回報成功
- **WHEN** 訂單確認的付款、座位確售、票券產出皆成功，但呼叫 `IEmailNotificationService` 或組裝通知資料時拋出例外（不是由這次呼叫的 `CancellationToken` 觸發的 `OperationCanceledException`）
- **THEN** 系統 SHALL 記錄該例外（結構化 log，至少含訂單 Id），且訂單確認 MUST 仍回報成功給呼叫端，不受通知失敗影響

#### Scenario: 呼叫端取消請求不視為通知失敗
- **WHEN** 訂單確認的付款、座位確售、票券產出皆成功，但在**執行通知呼叫（重新查詢資料或呼叫 `IEmailNotificationService`）的當下**，這次 `ConfirmOrderAsync` 呼叫本身接收到的 `CancellationToken` 被觸發，導致通知流程中止
- **THEN** 系統 MAY 不將其記錄為錯誤等級的失敗 log，且訂單確認結果 MUST 仍回報成功給呼叫端

#### Scenario: 非呼叫端觸發的取消例外仍視為通知失敗
- **WHEN** 訂單確認的付款、座位確售、票券產出皆成功，但通知流程拋出 `OperationCanceledException`，且該例外並非由這次 `ConfirmOrderAsync` 呼叫本身的 `CancellationToken` 觸發
- **THEN** 系統 SHALL 將其視為通知失敗並記錄（結構化 log，至少含訂單 Id），且訂單確認 MUST 仍回報成功給呼叫端

#### Scenario: 通知服務成功，行為不受影響
- **WHEN** 訂單確認成功且通知服務正常送出
- **THEN** 訂單確認回報成功給呼叫端，行為與通知服務不存在時完全一致

### Requirement: 通知 log 不得記錄未遮蔽的完整 Email
買家 Email 屬於個人資料；系統於通知流程中所產生的任何 log（無論是通知服務本身每次嘗試記錄的內容，或訂單確認流程記錄的失敗 log），只要內容包含收件信箱欄位，MUST NOT 寫入未遮蔽的完整 Email。`IEmailNotificationService` 介面本身與呼叫端傳遞、驗證的仍是完整 Email，遮蔽只發生在寫入 log 的那一步；本 Requirement 不要求通知服務在每次成功嘗試時都必須記錄 log（是否記錄成功嘗試屬於通知服務實作的選擇），只規範「一旦記錄的 log 內容含收件信箱欄位，該欄位就必須遮蔽」。遮蔽這個動作本身 MUST NOT 因為輸入的 Email 格式不合法或為空而拋出例外——遮蔽是記錄通知失敗這個 best-effort 動作的一部分，不能自己變成新的失敗來源。

`IEmailNotificationService` 的實作在拋出例外表示通知失敗時，該例外的 `Message` 本身 MUST NOT 包含完整、未遮蔽的收件 Email——訂單確認流程記錄失敗 log 時（見上一條 Requirement）是直接記錄整個例外物件，不會、也不應該對例外訊息內容另外做字串遮蔽；避免未遮蔽 Email 流入失敗 log 的責任在拋出例外的那一方（`IEmailNotificationService` 實作），不是接收例外並記錄 log 的呼叫端。

#### Scenario: 通知服務記錄含收件信箱欄位的結構化 log 時遮蔽該欄位
- **WHEN** 通知流程中任何一筆 log 的內容包含收件信箱欄位
- **THEN** 該欄位 SHALL 為遮蔽格式，MUST NOT 是完整、未遮蔽的 Email 位址

#### Scenario: 收件信箱格式不合法或為空時，遮蔽動作本身不拋出例外
- **WHEN** 要寫入 log 的收件信箱欄位為 `null`、空字串、純 whitespace，或不符合「剛好包含一個 `@`、且 `@` 前後兩段各自 trim 前後 whitespace 後都至少包含一個非 whitespace 字元」這個合法格式定義（包含不含 `@`、含兩個以上 `@`、`@` 前面／後面為空字串、或 `@` 前面／後面雖非空字串但整段僅由 whitespace 組成等情況，例如 `a@`、`@example.com`、`a@@example.com`、`a@ `、` @example.com`）
- **THEN** 遮蔽動作 MUST NOT 拋出例外，log 欄位 SHALL 使用固定字串 `[redacted]`，MUST NOT 原樣輸出該不合法內容

#### Scenario: 通知服務拋出例外時，例外訊息本身不得包含未遮蔽的完整 Email
- **WHEN** `IEmailNotificationService` 的實作因故拋出例外
- **THEN** 該例外的 `Message` MUST NOT 包含完整、未遮蔽的收件 Email

### Requirement: 取消訂單不觸發任何通知
系統 SHALL 只在訂單確認（Confirm）成功時觸發通知；取消訂單（無論是買家主動取消，或背景清理程序取消逾時未付款的訂單）MUST NOT 觸發任何通知呼叫。

#### Scenario: 買家主動取消訂單不觸發通知
- **WHEN** 買家對自己 Pending 狀態的訂單呼叫取消
- **THEN** 系統 MUST NOT 呼叫 `IEmailNotificationService`

#### Scenario: 背景清理程序取消逾時訂單不觸發通知
- **WHEN** 背景清理程序將逾時未付款的 Pending 訂單轉為 Cancelled
- **THEN** 系統 MUST NOT 呼叫 `IEmailNotificationService`
