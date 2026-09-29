## Context

`event-management-organizer-scoping` 已通盤盤點 `event-management` 底下以既有 `EventId` 為輸入的端點並補上歸屬核對（`CreateTicketTypeHandler`）。本次盤點發現同一份 `AdminEventsController` 底下還有另一個以既有 `eventId` 為輸入、對既有活動執行**寫入**操作的端點：`SetQueueMode`（`SetEventQueueModeHandler`，屬 `purchase-queue` 能力），尚未處理。

## Decisions

### 1. 補上 `SetEventQueueModeHandler` 的歸屬核對
**現況核對與缺口**：核對原始碼確認 `SetEventQueueModeHandler` 只檢查 `@event is null`（活動是否存在），完全沒有核對該活動的 `OrganizerId` 是否等於呼叫端目前 Organizer。若只把這個端點的授權屬性從 `AdminOnly` 改為 `RequireOrganizerContext`，卻不補上這個核對，會造成新的 IDOR 缺口：任何已核准的 Organizer 成員都能透過猜測或枚舉 `eventId`，對其他 Organizer 名下的既有活動任意切換其熱門搶購模式——這是授權模型收斂**主動引入**的風險（原本純 Admin-only 時，因為只有一種全域角色、沒有租戶邊界，不存在「屬於別人」這個概念，自然不會有這個問題；改成多租戶後，任何操作既有租戶資料的端點都需要重新檢視）。

**修正後設計**：
- `SetEventQueueModeHandler` 在 `GetForUpdateAsync` 之後，將既有的 `@event is null` 檢查改為 `@event is null || @event.OrganizerId != organizerId`（比照 `CreateTicketTypeHandler`、`GetEventSalesReportHandler`），不一致時回傳 `Error.NotFound`，不變更 `IsQueueModeEnabled`、不 commit、不清除快取。
- **「視同找不到」MUST 與真正不存在的回應完全相同**（比照已歸檔 `order-report-redemption-organizer-scoping` design.md Decision 1）：不只 HTTP 狀態碼，回應 body 也 MUST 逐字相同——兩種情況共用同一個分支與同一句 `Event '{id}' was not found.`，不得另寫「不屬於你」之類可區分的訊息。
- **檢查順序**：(1) `enabled` 驗證（既有行為，不查資料庫，400）→ (2) `GetForUpdateAsync` 鎖定並載入活動 → (3) 不存在或不屬於呼叫端 Organizer，回 404。驗證失敗在查詢之前，不論活動屬於誰都回相同的 400，不洩漏資訊。歸屬核對放在鎖定之後，沿用既有「交易內鎖定後才判斷」的單一路徑；跨租戶呼叫只會短暫持有他人活動的列鎖且隨即 rollback，與票券核銷的既有取捨相同。
- **早退路徑的 rollback 與列鎖釋放**：不另外在 Handler 手動呼叫 `RollbackAsync`，依賴既有 `IUnitOfWork` 契約——`IUnitOfWorkTransaction` 的 XML doc 明定「`DisposeAsync` 前未呼叫過 `CommitAsync`／`RollbackAsync`，MUST 自動回滾」，`UnitOfWorkTransaction.DisposeAsync` 依此實作，並由既有 `tests/ProjectC.Infrastructure.Tests/UnitOfWorkTests.cs` 的 `DisposeAsync_WithoutCommitOrRollback_AutomaticallyRollsBack` 驗證。Handler 以 `await using var transaction` 持有交易，因此「活動不存在」「不屬於呼叫端 Organizer」「`CancellationToken` 取消」「任何例外」四種離開路徑都必經 `DisposeAsync` → rollback；PostgreSQL 的 `FOR UPDATE` 列鎖在交易結束（commit 或 rollback）時釋放，不會延續到請求之後。「依賴契約」只是推論，Npgsql 在命令執行中被取消時的連線處理（取消請求、connector 是否標記為 broken、回到連線池前是否確實結束交易）屬於驅動程式行為而非語言行為，因此以 tasks.md 4.8（404 早退）、4.15a（鎖定序列化）、4.15b（取得列鎖後取消）、4.15c（取得列鎖後例外）四個整合測試實際驗證「交易結束後，獨立連線可在固定時間內重新鎖定並更新同一活動」
- `Event.OrganizerId` 為 `NOT NULL`，不存在「關聯資料缺失」的分支，不需要另外處理資料不一致。
- Controller 比照同檔其他 action，以 `User.TryGetOrganizerId` 取出 claim，失敗時 `Forbid()`（fail-closed）。

**通盤複查結論**：這是最後一個需要改為 Organizer 授權的既有 `AdminOnly` 端點。目前 WebApi 仍掛 `AdminOnly` 的只剩三處：
- `AdminEventsController.SetQueueMode`：本次處理。
- `AdminMembersController`：不受本系列變更影響，維持平台層級的 `MemberRole.Admin` 授權。
- `AdminOrganizersController`：Organizer 申請審核屬平台管理職責，維持 `AdminOnly`（見 `organizer-management`「平台管理員可以審核 Organizer 申請」）。

其餘 `AdminVenuesController`、`AdminOrdersController`、`AdminTicketsController` 與 `AdminEventsController` 的其他 action，已由前兩個變更改為 `RequireOrganizerContext`；其中 `AdminVenuesController` 依 `event-management-organizer-scoping` 的 Decision 2（Venue／SeatMap 不歸屬 Organizer）不需要個別資源的歸屬核對。

**替代方案**：只做前幾個變更涵蓋的訂單/核銷/報表/票種查詢與寫入端點，不處理這個既有活動的寫入端點。
**選擇補上的理由**：這是比查詢缺口更嚴重的**寫入**類 IDOR（能竄改其他租戶的資料，不只是讀取），且成因與前幾個變更完全相同，沒有理由只修一半。
**Trade-off**：增加 `SetEventQueueModeHandler` 的修改與對應測試，同樣沒有新增資料庫欄位或遷移成本。

### 2. 停權延遲曝險視窗比照既知取捨
本端點套用同一個 `RequireOrganizerContext` Policy，它只驗證 `OrganizerId` claim 格式、不即時查表（見 `organizer-management` 主 spec 與已歸檔 `event-management-organizer-scoping` design.md Decision 4）。因此 Organizer 被停權後，其成員手上停權前已核發、尚未過期的 Access Token，在自然到期（`AccessTokenExpirationMinutes`，現行預設 30 分鐘）前仍可開關該 Organizer 名下活動的熱門搶購模式。

**選擇沿用而不額外即時查表的理由**：與前兩個變更相同（Rule 2 Simplicity First、維持 Claim 換發模式的一致性）；停權後的換發與切換已由 `organizer-management` 的 `ORG-REFRESH-003`／`ORG-SUSPEND-001` 立即阻擋，曝險上限即 Access Token 效期。影響範圍只限該 Organizer 自己名下的活動，且開關可逆，影響小於已接受的核銷延遲視窗。若日後需要「停權立即生效」，應在 Policy 層統一處理，另開變更。

**風險接受**：專案負責人已於 2026-09-29 確認接受此延遲視窗。接受範圍為停權後、Access Token 自然到期前（現行預設 30 分鐘）仍可開關該 Organizer 名下活動的熱門搶購模式。

以 `PQ-ADMIN-009` 明確記錄並測試這個延遲視窗，避免被誤認為 Policy 漏檢查。

## 安全確認（CLAUDE.md 安全強制規則）

**輸入驗證**
- `enabled` 沿用既有 `SetEventQueueModeRequestValidator`（FluentValidation，Application 層）與 `bool?` model binding；路由 `{id:guid}` 限制格式；無 SQL／shell 拼接

**權限**
- 「已切換至一個 Approved Organizer」的檢查在 WebApi 層以既有 `RequireOrganizerContext` Policy 執行，Controller 另有 fail-closed 分支
- 「這個活動是否屬於呼叫端目前 Organizer」在 Application 層的 `SetEventQueueModeHandler` 內執行，與活動存在性檢查合併為同一分支
- 未切換 Organizer 的 `Admin` 角色會被拒絕（403），不保留平台 Admin 的跨租戶操作權限，與前兩個變更一致

**資料庫**
- 歸屬核對只讀取既有單筆 `GetForUpdateAsync` 已載入物件的 `Event.OrganizerId`，EF Core 參數化查詢，不構成 N+1 風險

## Risks / Trade-offs

- [既有測試依賴「Admin 角色即可呼叫此端點」的假設，或直接呼叫 `SetEventQueueModeHandler.HandleAsync` 舊簽章] → tasks.md 3.1–3.4 逐檔列出受影響測試（含 `ProjectC.Infrastructure.Tests` 的 `QueryCacheEventInvalidationOrderingTests`，Handler 簽章變更會使其編譯失敗）
- [跨租戶 404 早退會先短暫鎖住他人活動的列] → 列鎖持有時間只到該請求的交易 rollback 為止（見 Decision 1「早退路徑的 rollback 與列鎖釋放」），不跨請求；tasks.md 4.8 驗證早退後合法寫入不被阻塞
- [停權延遲視窗內仍可開關搶購模式] → 見 Decision 2，屬已知、有界（Access Token 效期）的取捨，以 `PQ-ADMIN-009` 明確記錄
- [平台 Admin 未切換 Organizer 即失去此操作能力] → BREAKING，見 proposal.md；後台前端目前沒有此操作的 UI，只影響直接呼叫 API 者
