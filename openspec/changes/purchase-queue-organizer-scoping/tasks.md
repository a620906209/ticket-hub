## 1. Application

- [x] 1.1（design.md Decision 1）`SetEventQueueModeHandler.HandleAsync` 新增 `organizerId` 參數；將既有 `@event is null` 檢查改為 `@event is null || @event.OrganizerId != organizerId`（比照 `CreateTicketTypeHandler`），兩種情況共用同一句 `Error.NotFound($"Event '{eventId}' was not found.")`，不另寫可區分的訊息；不一致時不變更 `IsQueueModeEnabled`、不 commit 交易、不清除快取

## 2. WebApi

- [x] 2.1 `AdminEventsController.SetQueueMode` 的 `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]` 改為 `RequireOrganizerContext`
- [x] 2.2 `SetQueueMode` 比照同檔其他 action，以 `User.TryGetOrganizerId` 取出 `organizerId`，失敗時 `return Forbid()`（fail-closed，防日後漏掛 Policy），成功才呼叫 Handler
- [x] 2.3 更新 `AuthorizationPolicies.RequireOrganizerContext` 的 XML doc，將熱門搶購模式端點列入套用範圍

## 3. 既有測試盤點與更新

- [x] 3.1 `tests/ProjectC.WebApi.Tests/Admin/AdminEventsControllerTests.cs` 的既有 PQ-ADMIN 測試：目前以 `CreateAuthenticatedAdminClientAsync`（無 `OrganizerId` claim）切換**另一個** Organizer 建立的活動，改動後會全部變 403；改為由建立活動的 Organizer client 呼叫，測試名稱中的 `AsAdmin` 同步改名
- [x] 3.2 `EventQueueControllerTests.cs`、`Captcha/PurchaseQueueCaptchaTests.cs`、`Events/QueryCachingFailOpenTests.cs` 以同一個 client 建立並切換活動，預期不受影響；實作後執行確認，並評估是否改用 `CreateAuthenticatedApprovedOrganizerClientAsync`（已不需要 Admin 角色）
- [x] 3.3 更新 `AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync` 的 XML doc：移除「熱門搶購模式維持 AdminOnly」的敘述。此 helper 仍有大量其他呼叫端（`TicketsControllerTests`、`OrdersControllerTests`、`QueryCachingAccessScopeTests` 等），只改 doc、不刪除
- [x] 3.4 `tests/ProjectC.Infrastructure.Tests/Events/QueryCacheEventInvalidationOrderingTests.cs`：該檔直接 `new SetEventQueueModeHandler(...)` 並以舊簽章呼叫 `HandleAsync(eventId, request, ct)`，1.1 新增 `organizerId` 參數後會編譯失敗（連帶整個 `ProjectC.Infrastructure.Tests` 無法執行）。`SeedEventAsync` 目前在內部建立 `organizerId` 後即丟棄，需改為一併回傳 `organizerId`（或回傳 tuple），並將 `HandleAsync` 呼叫改為傳入活動所屬的 `organizerId`（`SeedEventAsync` 目前只有此一呼叫端）

## 4. 測試 — purchase-queue（開關熱門搶購模式授權規則與租戶過濾）

測試類型：整合測試（xUnit + `WebApplicationFactory` + Testcontainers Postgres），驗證 `[Authorize]` Policy 與 `Event.OrganizerId` 核對的實際端點行為。被測主體：`AdminEventsController`（`SetEventQueueModeHandler`）。4.1–4.7 為 3.1 改寫後的既有測試，4.3、4.8、4.9 含新增案例。

- [x] 4.1 [PQ-ADMIN-001] 已切換至 Approved Organizer 的使用者對自己名下活動開啟熱門搶購模式成功
- [x] 4.2 [PQ-ADMIN-002] 已切換至 Approved Organizer 的使用者對自己名下、已開啟的活動關閉熱門搶購模式成功
- [x] 4.3 [PQ-ADMIN-003] 未帶 `OrganizerId` claim 呼叫回傳 403，分別驗證一般 `Member` 與「`Admin` 角色但未切換 Organizer」兩種身分（後者為新增案例）
- [x] 4.3a [PQ-ADMIN-003a] 未登入呼叫回傳 401
- [x] 4.4 [PQ-ADMIN-004] 請求 Body 完全缺漏 `enabled` 欄位回傳 400，不得誤判為關閉
- [x] 4.5 [PQ-ADMIN-005] 對不存在的活動呼叫回傳 404
- [x] 4.6 [PQ-ADMIN-006] 請求明確指定 `enabled: false` 成功關閉，與 4.4（完全缺漏）分開驗證
- [x] 4.7 [PQ-ADMIN-007] `enabled` 型別錯誤（字串而非 boolean）回傳 400
- [x] 4.8 [PQ-ADMIN-008]（design.md Decision 1）對屬於其他 Organizer 的活動呼叫，回傳 404；以既有 `NotFoundResponseBody.ReadNormalizedAsync` 比對回應 body 與「以不存在的活動 Id 呼叫」時逐字相同（比照銷售報表的同類測試）；該活動 `IsQueueModeEnabled` 維持原值，不回傳 403。早退路徑的列鎖釋放不在此驗證（HTTP request scope 結束即 Dispose DbContext，無論 Handler 是否 rollback 鎖都會釋放，無鑑別力），改由 4.15d 驗證
- [x] 4.9 [PQ-ADMIN-009]（design.md Decision 2）停權延遲視窗，於 `AdminEventsControllerTests` 新增 `SetQueueMode_WithTokenIssuedBeforeOrganizerSuspended_IsStillAcceptedUntilExpiry`，流程與斷言：
  1. 以 `AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync` 取得 Organizer client 與 `organizerId`（此 client 的 Access Token 即「停權前核發、尚未過期」的 Token），以該 client 建立場館、座位圖與活動，確認活動 `IsQueueModeEnabled` 初始為 `false`
  2. 以平台 Admin client 呼叫 `PATCH /api/admin/organizers/{organizerId}/suspend`，斷言 `204`
  3. **沿用步驟 1 的同一個 client**（不呼叫 refresh、不呼叫切換 Organizer 端點，確保是停權前核發的原 Token）呼叫 `PATCH /api/admin/events/{eventId}/queue-mode`，Body `{ "enabled": true }`
  4. 斷言回應為 `204`
  5. 以獨立 DbContext 查詢該活動，斷言 `IsQueueModeEnabled == true`（確認狀態確實被變更，而非只有狀態碼通過）
  6. 測試註解比照 `RPT-AUTHZ-005`：說明這是 design.md Decision 2 的既知有界延遲視窗；換發／切換被拒絕的負向路徑由 `organizer-management` 的 `ORG-REFRESH-003`／`ORG-SUSPEND-001` 負責，不在此重複
- [x] 4.10 [PQ-TOGGLE-001／002] 本次只改操作者用語、行為不變；AC 各項對應既有測試如下，並補強既有測試未斷言的部分（背景服務與 DB 行為不經過 HTTP 授權層，操作者主體由 4.1–4.9 驗證）。實作後將 `tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueAdmissionServiceTests.cs` 檔頭註解中的「Admin 關閉熱門搶購模式後…」同步為新的 Requirement 名稱
  - **a.（PQ-TOGGLE-001：關閉後停止推進、不刪除紀錄）** 改寫 `tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueAdmissionServiceTests.cs` 的 `AdvanceQueueOnceAsync_WhenQueueModeIsDisabled_SkipsTheEventAndLeavesWaitingEntriesUnchanged`：既有版本直接 seed 一個「本來就關閉」的活動且只有 1 筆 `Waiting`，未模擬「開啟中 → 關閉」，也未斷言紀錄未被刪除。改為：以 `SeedQueueModeEventAsync(isQueueModeEnabled: true)` 建立活動，`SeedWaitingEntryAsync` 建立 3 筆不同 `JoinedAtUtc` 的 `Waiting` 紀錄並記下各自的 `Id`、`JoinedAtUtc` → 對該活動呼叫 `DisableQueueMode()` 並存檔 → 以 `CreateService(maxConcurrentAdmittedBuyers: 3)` 執行一次 `AdvanceQueueOnceAsync` → 斷言：該活動的 `PurchaseQueueEntry` 筆數仍為 3、3 筆 `Id` 相同、狀態皆為 `Waiting`、`JoinedAtUtc` 與記下的值逐筆相同
  - **b.（PQ-TOGGLE-001：建立訂單不再檢查排隊資格）** 對應既有 `tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs` 的 `PlaceOrderAsync_WhenQueueModeDisabled_SucceedsWithoutCheckingQueueEntry`（活動關閉時，無排隊紀錄的會員可成功下單），以及 `tests/ProjectC.Infrastructure.Tests/PurchaseQueue/OrderServiceQueueModeLinearizationTests.cs` 的 `PlaceOrderAsync_WhenQueueModeIsDisabledByAdminDuringProcessing_SucceedsUsingTheLatestValueNotTheStaleReadBeforeTheTransaction`（真實 Postgres，下單處理途中被關閉時依最新值略過檢查）；不需改寫，實作後執行確認通過，並將後者方法名與註解中的「ByAdmin」同步為中性用語（例如 `...IsDisabledDuringProcessing...`）
  - **c.（PQ-TOGGLE-002：重新開啟後沿用原順序、不重新加入、不重置時間）** 改寫 `tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueAdmissionServiceTests.cs` 的 `AdvanceQueueOnceAsync_AfterReEnablingQueueMode_ResumesAdmittingInOriginalJoinOrder`：既有版本從「關閉」直接開啟，未走完「開啟 → 關閉 → 重新開啟」，也未斷言 `JoinedAtUtc` 與紀錄身分不變。改為：開啟中的活動 seed 3 筆 `Waiting`（`JoinedAtUtc` 依序為 -30／-20／-10 分鐘）並記下 `Id`、`JoinedAtUtc` → `DisableQueueMode()` 存檔 → 執行一次 `AdvanceQueueOnceAsync`，斷言 3 筆仍為 `Waiting` → `EnableQueueMode()` 存檔 → 以 `maxConcurrentAdmittedBuyers: 1` 執行一次，斷言只有 -30 分鐘那筆 `Admitted`、另兩筆 `Waiting` → 再以 `maxConcurrentAdmittedBuyers: 2` 執行一次，斷言 -20 分鐘那筆接著 `Admitted`、-10 分鐘那筆仍 `Waiting` → 全程斷言該活動紀錄筆數維持 3、`Id` 集合不變（沒有要求重新加入而產生新紀錄）、每筆 `JoinedAtUtc` 與記下的值相同
  - **d.（PQ-TOGGLE-002：同毫秒 tie-break 規則）** 重新開啟後沿用的 tie-break 規則與一般推進完全相同，由既有 `tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueAdmissionServiceTests.cs` 的 `AdvanceQueueOnceAsync_WithSameMillisecondJoinedAtUtc_TieBreaksByEntryIdStringLexicographicOrderAndIsReproducible`（PQ-ADMIT-005）覆蓋；開關熱門搶購模式不寫入 Redis waiting zset 的排序依據，因此不另寫「重新開啟 + 同毫秒」的組合測試，實作後執行確認通過
- [x] 4.11 [QC-EVT-INV-001／QC-EVT-INV-002／QC-TT-INV-001／QC-FAIL-002] 本次只改操作者用語，快取失效行為不變；以下既有測試負責「交易提交先於快取失效」的順序保證與 fail-open，**不負責授權與歸屬**（QC-EVT-INV-002 經 HTTP 授權與歸屬的部分見 4.14）：QC-EVT-INV-001 對應 `QueryCacheEventInvalidationOrderingTests` 的 `CreateEventHandler_AfterCommit_InvalidatesEventListCache_AndCommitPrecedesInvalidation`，QC-EVT-INV-002 對應同檔的 `SetEventQueueModeHandler` 測試（3.4 改寫後，直接呼叫 Handler、不經過 HTTP），QC-TT-INV-001 對應 `ProjectC.Infrastructure.Tests` 的 `QueryCacheTicketTypeInvalidationOrderingTests.CreateTicketTypeHandler_AfterCommit_InvalidatesTicketTypesCache_AndCommitPrecedesInvalidation`，QC-FAIL-002 對應 `ProjectC.WebApi.Tests` 的 `QueryCachingFailOpenTests`（其中 `SetQueueMode_WhenRedisUnreachable_StillSucceedsAndLogsWarning` 經 HTTP 呼叫 queue-mode 端點，由建立活動的同一個已切換 Organizer client 操作自己名下活動，改動後即實際通過 `RequireOrganizerContext` 與歸屬核對，並斷言 `204`、DB 的 `IsQueueModeEnabled` 已變更與 Redis Warning log）；實作後執行確認通過
- [x] 4.12 [PQ-STATUS-008] 本次只改前置情境的操作者用語，查詢行為不變；分兩層驗證：
  - **a.（Handler 層，既有）** `tests/ProjectC.Application.Tests/PurchaseQueue/GetMyQueueStatus/GetMyQueueStatusHandlerTests.cs` 的 `HandleAsync_WhenQueueModeDisabledWhileStillWaiting_ReflectsFalseWithoutAlteringEntry`：斷言回應 `QueueModeEnabled == false`、`Status == "Waiting"`；實作後執行確認通過
  - **b.（HTTP + 真實 DB，新增）** 於 `tests/ProjectC.WebApi.Tests/Events/EventQueueControllerTests.cs` 新增 `GetMyQueueStatus_AfterOwningOrganizerDisablesQueueMode_ReturnsQueueModeDisabledAndLeavesEntryUntouched`：以 `CreateAuthenticatedApprovedOrganizerClientAsync` 取得 Organizer client，透過同檔 `SeedQueueModeEnabledEventAsync` 建立已開啟熱門搶購模式的活動 → 會員沿用同檔 `JoinQueue_AsAdminRole_Returns201AndCreatesEntry` 的加入方式加入排隊，斷言 `201` → 以獨立 DbContext 讀出該筆 `PurchaseQueueEntry` 的 `Id`、`Status`、`JoinedAtUtc`、`AdmittedAtUtc`、`AdmissionExpiresAtUtc` 作為基準 → 由**活動所屬 Organizer** client 呼叫 `PATCH /api/admin/events/{id}/queue-mode` `{ "enabled": false }`，斷言 `204` → 會員呼叫 `GET /api/events/{id}/queue/entries/me`，斷言 `200`、`queueModeEnabled == false`、`status` 與 `waitingCount` 符合基準紀錄（`Waiting`、前方 0 人）→ 以獨立 DbContext 再讀一次，斷言該筆紀錄仍存在且上述欄位與基準逐一相同（未被清理或改寫）
- [x] 4.13 [EVT-AUTHZ-001～005] `event-management` delta 只改 Requirement 說明段落中的排除敘述（熱門搶購模式開關、銷售報表查詢改為「授權規則、Scenario 與測試皆由各自能力定義，不屬於本 Requirement 範圍」），端點清單與五個 Scenario 原文不變；EVT-AUTHZ-001～005 的「任一後台管理端點」限於該 Requirement 列出的 8 個端點，**不含** `SetQueueMode`——其授權、跨 Organizer 404、404 body 逐字相同、停權延遲視窗由 4.1–4.9（PQ-ADMIN-001～009）負責；既有測試 `tests/ProjectC.WebApi.Tests/Admin/EventManagementAuthorizationMatrixTests.cs` 預期不受影響，實作後執行確認通過
- [x] 4.14 [QC-EVT-INV-002]（HTTP 層，補足 4.11 直接呼叫 Handler 無法驗證的授權與歸屬情境）於 `tests/ProjectC.WebApi.Tests/Events/QueryCachingComponentTests.cs` 新增整合測試（該檔每個測試方法使用獨立 factory 與真實 Redis，避免固定 key `query-cache:events:list` 跨測試干擾）：
  - a. `SetQueueMode_ByOwningOrganizer_InvalidatesEventListCache`：Organizer A 建立活動 → 匿名呼叫 `GET /api/events` 使快取寫入，斷言該活動 `IsQueueModeEnabled == false` 且 Redis 中 `query-cache:events:list` 存在 → Organizer A 呼叫 `PATCH /api/admin/events/{id}/queue-mode` `{ "enabled": true }`，斷言 `204` → 斷言 `query-cache:events:list` 已被清除，再次 `GET /api/events` 回傳該活動 `IsQueueModeEnabled == true`
  - b. `SetQueueMode_ByOtherOrganizer_ReturnsNotFoundAndDoesNotInvalidateCache`：同 a 建立活動並寫入快取後，改由 Organizer B 的 client 呼叫同一端點，斷言 `404`、Redis 中 `query-cache:events:list` 仍存在（1.1 規定不一致時不清除快取）、DB 中該活動 `IsQueueModeEnabled` 仍為 `false`
- [x] 4.15 [PQ-ADMIN-001／002]（design.md Decision 1，鎖定序列化）同一活動的併發切換：此行為在本次變更前即存在（Handler 註解所述的遺失更新修正），但既有測試從未覆蓋，而本次在鎖定後新增早退分支，因此補上 PostgreSQL 整合測試。新增 `tests/ProjectC.Infrastructure.Tests/Events/SetEventQueueModeHandlerConcurrencyTests.cs`（Testcontainers Postgres，比照同目錄 `QueryCacheEventInvalidationOrderingTests` 的 fixture），包含以下四個測試。4.15b／c／d 的鎖釋放檢查必須在 Handler 的 DbContext **仍存活時**執行（DbContext Dispose 會連帶結束交易並歸還連線，之後才檢查將無法偵測 Handler 漏掉 rollback）。
  **4.15a** `SetQueueMode_WhenEventRowLockedByAnotherTransaction_WaitsAndAppliesAfterCommit`：
  1. seed 一個 `IsQueueModeEnabled == false` 的活動並取得其 `organizerId`
  2. 以獨立 DbContext 開啟交易 A，透過 `EventRepository.GetForUpdateAsync` 鎖定該活動，呼叫 `EnableQueueMode()` 並 `SaveChangesAsync`，**暫不 commit**
  3. 以另一個 DbContext 建立 `SetEventQueueModeHandler`，啟動 `HandleAsync(eventId, organizerId, { enabled: false })` 但不 await
  4. 斷言該 Task 在 500ms 內**未完成**（被交易 A 的列鎖阻擋）
  5. commit 交易 A；再以 10 秒逾時 await 步驟 3 的 Task，斷言 `IsSuccess`
  6. 以獨立 DbContext 查詢，斷言最終 `IsQueueModeEnabled == false`——即 Handler 的寫入排在交易 A 提交之後，最終狀態符合實際序列化順序（A 設 `true` → Handler 設 `false`），沒有被 A 覆寫、也沒有在 A 提交前寫入
  **4.15b** `SetQueueMode_WhenCancelledAfterAcquiringRowLock_RollsBackAndReleasesLock`：
  1. seed 一個 `IsQueueModeEnabled == false` 的活動
  2. 以測試用 `IEventRepository` decorator 包住真正的 `EventRepository`：`GetForUpdateAsync` 先委派給內層（實際取得 `FOR UPDATE` 列鎖）、回傳後立即對 Handler 傳入的 `CancellationTokenSource` 呼叫 `Cancel()`，其餘方法純委派——確保取消確定發生在「已取得列鎖、尚未 commit」的時間點，不依賴計時
  3. 呼叫 `HandleAsync(eventId, organizerId, { enabled: true }, cts.Token)`，斷言拋出 `OperationCanceledException`（`CommitAsync(cancellationToken)` 內的 `SaveChangesAsync` 觀察到取消）
  4. 以另一個獨立 DbContext 開交易、`GetForUpdateAsync` 同一活動、`EnableQueueMode()`、commit，整段以 `WaitAsync(TimeSpan.FromSeconds(5))` 包住，斷言在逾時內完成（列鎖已釋放，未遺留鎖）
  5. 在步驟 4 之前先以獨立 DbContext 斷言 `IsQueueModeEnabled` 仍為 `false`（被取消的交易已 rollback，未落地）
  **4.15c** `SetQueueMode_WhenExceptionThrownAfterAcquiringRowLock_RollsBackAndReleasesLock`：
  1. 同 4.15b 的 seed
  2. decorator 的 `GetForUpdateAsync` 純委派（取得列鎖），`Update` 拋出 `InvalidOperationException`（模擬取得鎖之後、commit 之前的任意例外）
  3. 呼叫 `HandleAsync`，斷言該例外原樣往外拋出（Handler 不吞例外）
  4–5. 同 4.15b 的步驟 4–5：`IsQueueModeEnabled` 仍為 `false`，且獨立連線可在 5 秒內重新鎖定並更新同一活動
  **4.15d** `SetQueueMode_ForOtherOrganizerEventAfterAcquiringRowLock_ReturnsNotFoundAndReleasesLock`（[PQ-ADMIN-008]）：
  1. 同 4.15b 的 seed
  2. 以真正的 `EventRepository` 建立 Handler，以另一個 `organizerId` 呼叫 `HandleAsync`，斷言回傳 `ErrorType.NotFound`
  3. 在 Handler 的 DbContext 仍存活時，執行 4.15b 的步驟 4–5（驗證歸屬核對早退路徑已 rollback 並釋放列鎖）
  - decorator 只放在測試專案（`tests/ProjectC.Infrastructure.Tests/TestSupport/`），不為了測試修改正式程式碼
