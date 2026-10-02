## 0. 前置條件

- [x] 0.1 從最新 master（c886af4，含已歸檔的 `order-display-enrichment`）開分支 `feature/real-name-verification`

## 1. Domain

- [x] 1.1（design.md 決策 1）`Member` 新增 `RealName`、`NationalIdLast4`（`private set`、nullable），計算屬性 `HasRegisteredRealName`，以及 `RegisterRealName(realName, nationalIdLast4)`：已登記時回傳失敗；參數 null／空白時丟 `ArgumentException`。Domain 單元測試：
  - `RegisterRealName_WhenNotRegistered_SetsBothFields`
  - `RegisterRealName_WhenAlreadyRegistered_ReturnsFailureAndKeepsOriginalValues`
  - `RegisterRealName_WhenArgumentBlank_ThrowsArgumentException`（`[Theory]`）
  - `Deactivate_WhenRealNameRegistered_KeepsRealName`（RNV-RETAIN-001 的 Domain 部分；`Activate` 同樣不改變）
- [x] 1.2（design.md 決策 3 I1）`Event` 建構子最後新增可選參數 `bool isRealNameRequired = false` 與 getter-only 屬性 `IsRealNameRequired`（無 setter、無變更方法）。既有 `new Event(` 呼叫共 61 處、分布在 31 個檔案（src 1 處、tests 60 處），因有預設值不需修改；實作前以 `grep -rn "new Event(" src tests` 重新確認。Domain 測試：未指定時為 false、指定 true 時為 true
- [x] 1.3（design.md 決策 2）`ProjectC.Domain/Members` 新增 `IMemberRealNameRepository`（`TryRegisterAsync`、`GetAsync`）與唯讀 record `MemberRealName(string RealName, string NationalIdLast4)`
- [x] 1.4（design.md 決策 4）`IOrderRepository.GetOrganizerIdByOrderItemIdAsync` 改名並擴充為 `GetRedemptionContextByOrderItemIdAsync` → `RedemptionContext?`（`OrganizerId`、`IsRealNameRequired`、`BuyerId`）。同步更新所有實作者與呼叫者（實作前以 `grep -rn "GetOrganizerIdByOrderItemIdAsync\|IOrderRepository" src tests` 重新確認）：
  - 正式實作 `OrderRepository`（見 2.4）
  - `FakeOrderRepository`
  - `OrganizerScopingFaultInjectionWebApplicationFactory` 內的 decorator
  - `tests/ProjectC.Infrastructure.Tests/Orders/OrderRepositoryOrganizerScopingTests.cs` 第 108–126 行兩個測試：改用新方法並斷言 `OrganizerId`，測試意圖不變

## 2. Infrastructure

- [x] 2.1 EF 設定：
  - `MemberConfiguration` 新增 `RealName`（`HasMaxLength(50)`）、`NationalIdLast4`（`HasMaxLength(4)`、`IsFixedLength()`）與 check constraint「兩欄同時為 null 或同時有值」
  - `EventConfiguration` 明確加上 `Property(e => e.IsRealNameRequired).IsRequired().HasDefaultValue(false)`（建構子綁定的屬性須明確 `Property()`，見既有 ticketing-core 經驗）
- [x] 2.2 以 `docker compose exec api dotnet ef migrations add AddRealNameVerification` 產生 migration，檢查 Up 只包含上述欄位與約束。Down 依 design.md Migration Plan 第 3 點改寫：
  - 先以 `migrationBuilder.Sql` 執行固定字串的 PostgreSQL `DO` 區塊：存在任何已登記實名的會員或需實名活動時 `RAISE EXCEPTION`
  - 之後才刪除約束與欄位
  - 在 migration 檔內註解說明這是 CLAUDE.md 禁止原生 SQL 的例外與理由
- [x] 2.3（design.md 決策 2）實作 `MemberRealNameRepository`：
  - `TryRegisterAsync`：`ExecuteUpdateAsync`，條件 `Id == memberId && RealName == null`
  - `GetAsync`：`AsNoTracking`，只投影兩欄
  - DI 註冊為 Scoped（持有 DbContext）
- [x] 2.4 `OrderRepository.GetRedemptionContextByOrderItemIdAsync`：單一投影查詢 `OrderItem → Order → Event`，回傳三個欄位；查不到回 null
- [x] 2.5 Infrastructure 整合測試（Testcontainers）`MemberRealNameRepositoryTests`：
  - `TryRegisterAsync_WhenNotRegistered_WritesBothFieldsAndReturnsTrue`
  - `TryRegisterAsync_WhenAlreadyRegistered_ReturnsFalseAndKeepsOriginalValues`
  - RNV-REGISTER-004（資料庫層）`TryRegisterAsync_WhenConcurrentFirstRegistrations_ExactlyOneSucceedsAndValuesAreNotMixed`：兩個 DbContext 以 `Task.WhenAll` 並發；斷言一個 true、一個 false，DB 兩欄同屬成功者
  - `TryRegisterAsync_WhenMemberDoesNotExist_ReturnsFalse`
  - `GetAsync_WhenNotRegistered_ReturnsNull`
  - 以已取消的 token 呼叫兩個方法，斷言丟 `OperationCanceledException`
- [x] 2.6 Infrastructure 整合測試：check constraint 拒絕只有一欄有值的寫入（以 `ExecuteUpdateAsync` 只更新一欄，斷言 `DbUpdateException`／`PostgresException`）；並斷言例外的 `Message`（含 InnerException 的 Message）不含寫入的姓名或末四碼
- [x] 2.7 Infrastructure 整合測試：`GetRedemptionContextByOrderItemIdAsync` 對需實名與不需實名活動回傳正確的 `IsRealNameRequired`、`OrganizerId`、`BuyerId`；查不到時回 null
- [x] 2.8 Infrastructure 整合測試（Testcontainers，以 `IMigrator` 指定目標 migration）：
  - EVT-REALNAME-004：先 migrate 到前一個 migration、插入 Events 列，再 migrate 到最新；斷言該列 `IsRealNameRequired = false`
  - RNV-ROLLBACK-001：有一位會員已登記實名時，migrate 回前一個 migration 丟例外；斷言兩個實名欄位、`Events.IsRealNameRequired` 欄位與資料都仍存在
  - RNV-ROLLBACK-002：沒有會員登記、但有一個需實名活動時，Down 丟例外；斷言欄位與資料都仍存在
  - RNV-ROLLBACK-003：沒有任何實名資料時 Down 成功、欄位與約束已移除；再 Up 後所有會員為未登記、所有活動為 false

## 3. Application

- [x] 3.1 新增 `ErrorType.RealNameRequired`、`ErrorType.HolderVerificationRequired` 與對應的 `Error` factory；`ResultExtensions` 分別對應 403、409，並比照既有 `QueueAdmissionRequired` 寫上 Title 用途註解。這兩種錯誤訊息只能含 Id
- [x] 3.2（design.md 決策 5）新增 `RealNameMasking.MaskNationalIdLast4`（`**` + 末兩碼）
- [x] 3.3（RNV-FORMAT-*）新增 `RegisterRealNameRequest` 與 FluentValidation 驗證器：姓名 trim 後 1–50 字、不含控制字元；末四碼 `^[0-9]{4}$`；錯誤訊息不使用 `{PropertyValue}`
- [x] 3.4（RNV-REGISTER-*）新增 `RegisterRealNameHandler`：
  - 流程：以 `AsNoTracking` 載入 Member（不存在回 NotFound）→ 對未追蹤副本呼叫 `RegisterRealName` 快速路徑（已登記回 Conflict；design.md 決策 2，不可對追蹤中的實體呼叫）→ `IMemberRealNameRepository.TryRegisterAsync`（false 回 Conflict）→ 回傳 `MemberProfileDto`
  - DTO 以呼叫過 `RegisterRealName` 的未追蹤副本組成（值即本次 trim 後的輸入），不重新查詢，也不讀取 change tracker
  - Conflict／NotFound 的 `Error` 訊息只能含 Member Id：`ResultExtensions` 會把訊息原樣放進 ProblemDetails `Detail`
  - `TryRegisterAsync` 回 false 時無法區分「被搶先登記」與「會員剛被刪除」，一律回 Conflict（目前系統沒有刪除會員的路徑），在程式碼註解說明
  - log 只記 `MemberId` 與結果
- [x] 3.5（MM-PROFILE-RN-*）`MemberProfileDto` 新增 `HasRegisteredRealName`、`RealName`、`NationalIdLast4Masked`；`GetMyProfileHandler` 與 `UpdateMyProfileHandler` 的回傳同步填入；確認 `UpdateMyProfileRequest` 不含實名欄位；`MembersController` 的 `GET /me` 與實名登記 action 加上 `[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]`（design.md「回應快取」，涵蓋 action 產生的所有狀態碼）
- [x] 3.6（EVT-REALNAME-001/002）`CreateEventRequest` 新增 `bool? IsRealNameRequired`，`CreateEventHandler` 以 `?? false` 傳入 `Event` 建構子；`AdminEventSummaryDto`、`EventDto` 新增 `IsRealNameRequired`，`GetAdminEventsHandler`、`GetEventsHandler` 投影填入
- [x] 3.7（TP-RN-ORDER-*，design.md 決策 3「權威讀取時點」）`OrderService.PlaceOrderAsync`：
  - 主要檢查：`orderEvent` 不為 null 時，在跨活動檢查之後、限購檢查之前、開交易之前，若 `orderEvent.IsRealNameRequired` 且 `GetAsync(buyerId)` 為 null，回 `Error.RealNameRequired`
  - 補位檢查：`orderEvent` 為 null 而 `lockedEvent` 不為 null 時，在 `lockedEvent` 的 NotFound 判斷之後、排隊資格檢查與任何座位或庫存鎖定之前，以 `lockedEvent.IsRealNameRequired` 做同樣檢查；失敗時回 `RealNameRequired`，交易隨 `await using` 回滾
  - 不變量自我檢查：兩者都不為 null 但 `IsRealNameRequired` 不同時，丟 `InvalidOperationException`（訊息只含活動 Id）
  - 不需實名的活動不呼叫 `GetAsync`；每次請求最多呼叫一次 `GetAsync`
  - 在程式碼註解寫明 I1／I2 不變量與「未來需要重新評估的條件」
- [x] 3.8（PQ-RN-JOIN-*）`JoinPurchaseQueueHandler`：在驗證碼、活動存在、熱門搶購模式檢查之後、開交易之前做實名檢查；交易內重讀的 `lockedEvent.IsRealNameRequired` 與交易外不同時，丟 `InvalidOperationException`
- [x] 3.9（RDM-RN-*）`RedeemTicketRequest` 新增 `bool? IsHolderVerified`；`RedeemTicketHandler` 改用 `GetRedemptionContextByOrderItemIdAsync`（查不到仍丟 `InvalidOperationException`，維持 RDM-AUTHZ-007）；在狀態檢查之後新增第 (5) 步：`IsRealNameRequired && IsHolderVerified != true` 回 `Error.HolderVerificationRequired`；Controller 傳入新欄位
- [x] 3.10（RDM-HOLDER-*）新增 `GetTicketHolderHandler` 與 `TicketHolderDto`：
  - 流程：載入 Ticket（不加鎖；不存在回與核銷相同訊息的 NotFound）→ 歸屬查詢（查不到丟 `InvalidOperationException`；不屬於呼叫端回同一個 NotFound）→ 狀態不是 `Issued` 回 Conflict（訊息只含 Ticket Id）→ 需實名時以 `GetAsync(buyerId)` 取實名，null 丟 `InvalidOperationException`（訊息只含 Ticket Id 與 Member Id）
  - 不需實名時持票人欄位為 null，且不呼叫 `GetAsync`
  - 稽核日誌：成功、NotFound、Conflict 三種結果都以 Information 等級記錄 Ticket Id、呼叫者 Member Id、Organizer Id、結果，成功時另記持票人 Member Id；不含實名值
  - 查詢持票人 action 加上 `[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]`，涵蓋 200、404、409
  - 404 的訊息字串與 `RedeemTicketHandler` 共用同一個來源，避免兩邊漂移
- [x] 3.11 所有新增的非同步呼叫都傳遞 `CancellationToken`；不 catch 技術例外與 `OperationCanceledException`
- [x] 3.12 Application.Tests 新增 `FakeMemberRealNameRepository`（記錄呼叫次數、傳入的 Id 與 token；可設定 `TryRegisterAsync` 回傳 false 以模擬並發搶先）；`FakeEventRepository` 新增可分別覆寫 `GetByIdAsync` 與 `GetForUpdateAsync` 回傳值的測試掛勾，供 TP-RN-ORDER-007／008／009、PQ-RN-JOIN-007／008 模擬兩次讀取結果不同

## 4. 後端測試 — Application 單元測試

- [x] 4.1 `RealNameMaskingTests`：
  - RNV-MASK-001：`MaskNationalIdLast4_WhenGiven0912_ReturnsStarStar12`，另加一組 `[Theory]`
- [x] 4.2 `RegisterRealNameRequestValidatorTests`：
  - RNV-FORMAT-001：空字串與只有空白的姓名驗證失敗
  - RNV-FORMAT-002：51 字驗證失敗；50 字邊界通過
  - RNV-FORMAT-004：含 `\n`、`\t`、`\u0007` 的姓名驗證失敗；U+2028／U+2029 同樣失敗，前端實名頁送出前即攔下（第 4 輪對抗審查發現）
  - RNV-FORMAT-005：「123」「12345」「12a4」「１２３４」「12 4」逐一 `[Theory]` 驗證失敗
  - RNV-FORMAT-006：末四碼「98x7」驗證失敗時，所有錯誤訊息都不含「98x7」
  - RNV-FORMAT-007：只有 U+200B、含 U+200B／U+202E／U+FEFF、含補充平面格式字元 U+E0001 的姓名驗證失敗（審查後補，使用者 2026-10-02 決定）
  - RNV-FORMAT-008：50 個擴充 B 區罕用字通過、51 個失敗；Infrastructure 測試確認 50 個擴充 B 區罕用字能完整寫入 `varchar(50)`；前端實名頁同樣以字元計數（審查後補，使用者 2026-10-02 決定）
  - RNV-FORMAT-009：只有 U+3164／U+2800／U+115F／U+1160／U+FFA0、含 U+3164 的姓名驗證失敗；前端實名頁對 Cc／Cf 與這 5 個字元送出前即攔下（第 2 輪對抗審查發現，使用者 2026-10-02 決定）
  - RNV-FORMAT-010：只有 U+034F／U+FE0F／U+180B／純數字／純標點的姓名驗證失敗，含中文、英文（含連字號與撇號）、擴充 B 區罕用字的姓名通過；前端實名頁送出前同樣攔下（第 3 輪對抗審查發現，使用者 2026-10-02 決定）
- [x] 4.3 `RegisterRealNameHandlerTests`：
  - RNV-REGISTER-001：成功時回傳遮蔽值「**34」，DTO 任何字串欄位都不含「1234」
  - RNV-REGISTER-002：已登記、送出不同值時回 Conflict，`TryRegisterAsync` 呼叫次數為 0
  - RNV-REGISTER-003：已登記、送出相同值時回 Conflict，`TryRegisterAsync` 呼叫次數為 0
  - RNV-REGISTER-004（Application 層）：`TryRegisterAsync` 回 false 時回 Conflict
  - RNV-REGISTER-006：Member 不存在回 NotFound，`TryRegisterAsync` 呼叫次數為 0
  - RNV-FORMAT-003：傳給 `TryRegisterAsync` 的是 trim 後的「王小明」
  - token 轉送；`OperationCanceledException` 原樣拋出
- [x] 4.4 `GetMyProfileHandlerTests`：
  - MM-PROFILE-RN-001：未登記時三欄為 false／null／null
  - MM-PROFILE-RN-002：已登記時姓名完整、末四碼為「**34」
- [x] 4.5 `UpdateMyProfileHandlerTests`：
  - MM-UPDATE-RN-001（Application 層）：已登記會員更新顯示名稱後，實名資料不變
- [x] 4.6 `CreateEventHandlerTests`：
  - EVT-REALNAME-001：指定 true 時存入 true
  - EVT-REALNAME-002：未提供時存入 false
- [x] 4.7 `OrderService` 建立訂單測試：
  - TP-RN-ORDER-001：未登記回 `RealNameRequired`，座位與庫存 Fake 沒有任何鎖定或扣減呼叫、未開交易
  - TP-RN-ORDER-002：已登記成功
  - TP-RN-ORDER-003：不需實名活動成功，`GetAsync` 呼叫次數為 0
  - TP-RN-ORDER-004：同時違反限購時回 `RealNameRequired`
  - TP-RN-ORDER-006：未登記者送出跨活動項目時回既有跨活動驗證錯誤，非 `RealNameRequired`，`GetAsync` 呼叫次數為 0
  - TP-RN-ORDER-007：`GetByIdAsync` 回 null、`GetForUpdateAsync` 回需實名活動、未登記時，回 `RealNameRequired`；排隊 repository 的 `GetForUpdateAsync` 呼叫次數為 0，座位與庫存沒有任何鎖定或扣減呼叫，交易未提交
  - TP-RN-ORDER-008：`GetByIdAsync` 回 false、`GetForUpdateAsync` 回 true 時丟 `InvalidOperationException`，交易未提交、座位與庫存不變
  - TP-RN-ORDER-009：已登記買家，`GetByIdAsync` 回 true、`GetForUpdateAsync` 回 false 時丟 `InvalidOperationException`，交易未提交、座位與庫存不變
- [x] 4.8 `JoinPurchaseQueueHandlerTests`：
  - PQ-RN-JOIN-001：未登記回 `RealNameRequired`，未開交易、未建立紀錄
  - PQ-RN-JOIN-002：已登記建立 `Waiting`
  - PQ-RN-JOIN-003：驗證碼錯誤時回 `CaptchaInvalid`，`GetAsync` 呼叫次數為 0
  - PQ-RN-JOIN-004：不需實名時成功，`GetAsync` 呼叫次數為 0
  - PQ-RN-JOIN-005：活動不存在時回 NotFound，`GetAsync` 呼叫次數為 0
  - PQ-RN-JOIN-006：需實名但未開熱門搶購模式時回 Conflict，`GetAsync` 呼叫次數為 0
  - PQ-RN-JOIN-007：交易外 false、交易內 true 時丟 `InvalidOperationException`，未建立紀錄
  - PQ-RN-JOIN-008：已登記會員，交易外 true、交易內 false 時丟 `InvalidOperationException`，未建立紀錄
- [x] 4.9 `RedeemTicketHandlerTests`：
  - RDM-RN-001：旗標未提供時回 `HolderVerificationRequired`，Ticket 維持 Issued、核銷時間為 null
  - RDM-RN-002：旗標為 true 時成功
  - RDM-RN-003：旗標為 false 時回 `HolderVerificationRequired`，Ticket 維持 Issued
  - RDM-RN-004：不需實名時不帶旗標也成功
  - RDM-RN-005：其他 Organizer 回 NotFound，訊息與票券不存在時相同
  - RDM-RN-006：已核銷時回既有 Conflict，非 `HolderVerificationRequired`
  - RDM-RN-007：簽章無效時回簽章錯誤，Ticket 未被載入
- [x] 4.10 `GetTicketHolderHandlerTests`：
  - RDM-HOLDER-001：需實名時回傳完整姓名與末四碼
  - RDM-HOLDER-002：不需實名、買家已登記時，持票人欄位為 null，`GetAsync` 呼叫次數為 0
  - RDM-HOLDER-003：其他 Organizer 回 NotFound，訊息與不存在時相同，`GetAsync` 呼叫次數為 0
  - RDM-HOLDER-006：查詢後 Ticket 狀態不變、核銷時間仍為 null
  - RDM-HOLDER-007：需實名而 `GetAsync` 回 null 時丟 `InvalidOperationException`，訊息不含姓名或末四碼
  - RDM-HOLDER-008：`Redeemed` 票券回 Conflict，`GetAsync` 呼叫次數為 0
  - RDM-HOLDER-009：活動開始時間已過、`Issued` 票券仍回傳持票人資料
  - 歸屬查不到時丟 `InvalidOperationException`
  - token 轉送

## 5. 後端測試 — WebApi 整合測試

- [x] 5.1 `MembersControllerTests`：
  - RNV-REGISTER-001：200，回應 `nationalIdLast4Masked = "**34"`；序列化後的回應 JSON 不含「1234」
  - RNV-REGISTER-002：已登記後送出不同值回 409，DB 實名維持原值
  - RNV-REGISTER-003：已登記後送出相同值回 409
  - RNV-REGISTER-005：未帶 Access Token 回 401
  - RNV-REGISTER-006：Access Token 的會員 Id 不存在時回 404，DB 沒有新增資料
  - RNV-FORMAT-003：送出「  王小明  」後，回應與 DB 皆為「王小明」
  - RNV-FORMAT-001：姓名為空字串、另一案例為只有空白（`[Theory]`，末四碼合法），各回 400，ProblemDetails `title` 為驗證錯誤；每次請求後 DB 中該會員的 `RealName`、`NationalIdLast4` 仍為 null
  - RNV-FORMAT-002：姓名去除前後空白後為 51 字時回 400，ProblemDetails `title` 為驗證錯誤，DB 實名欄位仍為 null；另一案例姓名恰為 50 字時回 200，DB 中姓名與送出值完全相同（50 字）、末四碼正確寫入
  - RNV-FORMAT-004：姓名含換行字元時回 400，DB 未寫入
  - RNV-FORMAT-005：末四碼分別為「123」「12345」「12a4」「１２３４」「12 4」時（`[Theory]`，姓名合法），各回 400，ProblemDetails `title` 為驗證錯誤；每次請求後 DB 中該會員的 `RealName`、`NationalIdLast4` 仍為 null
  - RNV-FORMAT-006：末四碼「98x7」的 400 回應 body 不含「98x7」
  - RNV-MASK-002：已登記末四碼「1234」的會員 `GET /me`，序列化後的回應 JSON 不含「1234」
  - RNV-NOSTORE-001：已登記會員 `GET /api/members/me` 回 200，標頭 `Cache-Control` 含 `no-store`
  - RNV-NOSTORE-002：未登記會員 `GET /api/members/me` 回 200，標頭 `Cache-Control` 含 `no-store`
  - RNV-NOSTORE-003：實名登記端點的 200（成功）、409（已登記）、400（末四碼「12a4」）三種回應，標頭 `Cache-Control` 都含 `no-store`
  - RNV-ERROR-001：再次登記的 409 body 不含已登記的姓名與末四碼，也不含本次送出的值
  - MM-PROFILE-RN-001：未登記會員 `GET /me` 回 `hasRegisteredRealName = false`、另兩欄為 null
  - MM-PROFILE-RN-002：已登記會員 `GET /me` 回完整姓名與「**34」
  - MM-UPDATE-RN-001：`PUT /me` 夾帶 `realName`／`nationalIdLast4` 時只更新顯示名稱，DB 實名不變
  - design.md 決策 2：登記成功與輸掉競爭兩種情況下，請求結束時該 scope 的 DbContext ChangeTracker 中都沒有 Modified 狀態的 Member（以測試用 scoped 探針在請求結束前擷取）
- [x] 5.2 RNV-REGISTER-004（端到端）：同一會員以 `Task.WhenAll` 並發兩個不同值的登記請求；斷言狀態碼為 {200, 409}，DB 兩欄同屬回 200 的那個請求
- [x] 5.3 `AdminEventsControllerTests`：
  - EVT-REALNAME-001：建立時帶 `isRealNameRequired = true`，DB 為 true
  - EVT-REALNAME-002：建立時不帶此欄位，DB 為 false
  - EVT-REALNAME-003：後台列表中需實名與不需實名活動各回傳正確的 `isRealNameRequired`
- [x] 5.4 `EventsControllerTests`：
  - TP-BROWSE-RN-001：未登入呼叫公開列表，需實名與不需實名活動各回傳正確的 `isRealNameRequired`
- [x] 5.5 快取整合測試：
  - TP-BROWSE-RN-002：把不含 `isRealNameRequired` 的舊格式 `EventDto` 陣列 JSON 直接寫入 `query-cache:events:list`，呼叫公開列表，斷言 200 且該欄位為 false
- [x] 5.6 `OrdersControllerTests`：
  - TP-RN-ORDER-001：403、ProblemDetails `title = "RealNameRequired"`；DB 中座位為 Available、庫存不變、沒有新訂單
  - TP-RN-ORDER-002：已登記買家下單回 201，DB 有新訂單
  - TP-RN-ORDER-003：未登記買家對不需實名活動下單成功
  - TP-RN-ORDER-005：直接建立 `Admitted` 排隊紀錄後，未登記者下單 403，排隊紀錄狀態不變
  - TP-RN-ORDER-006：跨活動項目回 400 既有驗證錯誤，`title` 不是 `RealNameRequired`，DB 座位與庫存不變
  - RNV-ERROR-001：`RealNameRequired` 的 403 body 所有欄位都不含任何姓名或末四碼字樣
- [x] 5.7 `EventQueueControllerTests`：
  - PQ-RN-JOIN-001：403、`title = "RealNameRequired"`，DB 中沒有排隊紀錄
  - PQ-RN-JOIN-002：已登記會員加入成功，DB 有一筆 `Waiting`
  - PQ-RN-JOIN-003：驗證碼錯誤時回既有 `CaptchaInvalid`，`title` 不是 `RealNameRequired`
  - PQ-RN-JOIN-004：不需實名活動，未登記會員加入成功
  - PQ-RN-JOIN-005：活動不存在回 404，`title` 不是 `RealNameRequired`
  - PQ-RN-JOIN-006：需實名但未開熱門搶購模式回 409，`title` 不是 `RealNameRequired`
- [x] 5.8 `AdminTicketsControllerTests`（核銷）：
  - RDM-RN-001：不帶旗標回 409 `title = "HolderVerificationRequired"`，DB 中 Ticket 為 Issued、核銷時間為 null
  - RDM-RN-002：帶 `isHolderVerified = true` 回 200，DB 中 Ticket 為 Redeemed、有核銷時間
  - RDM-RN-003：`isHolderVerified = false` 回 409 `HolderVerificationRequired`，DB 不變
  - RDM-RN-004：不需實名活動不帶旗標回 200
  - RDM-RN-005：其他 Organizer 的實名活動票券回 404，body 與「票券不存在」逐字相同
  - RDM-RN-006：已核銷的實名活動票券回既有 409，`title` 不是 `HolderVerificationRequired`
  - RDM-RN-007：簽章被竄改、帶旗標時回既有簽章錯誤，DB 不變
  - RDM-RN-009：`isHolderVerified` 為字串 `"yes"` 與數字 `1` 時各回 400，DB 不變
  - RNV-ERROR-001：`HolderVerificationRequired` 的 409 body 不含持票人姓名與末四碼
- [x] 5.9 RDM-RN-008：兩個帶 `isHolderVerified = true` 的核銷請求以 `Task.WhenAll` 同時送出，斷言狀態碼為 {200, 409 非 Issued}，DB 只核銷一次
- [x] 5.10 RDM-AUTHZ-001～007（MODIFIED「核銷 API 需要已切換至一個 Approved Organizer」重新納入的既有情境）：既有測試在改用 `GetRedemptionContextByOrderItemIdAsync` 後全數通過、斷言本身未被修改；RDM-AUTHZ-007 的 fault-injection decorator 改為讓新方法回傳 null。逐一列出：
  - RDM-AUTHZ-001：既有「核銷自己活動的票券成功」測試通過
  - RDM-AUTHZ-002：既有「未帶 OrganizerId claim 回 403」測試通過
  - RDM-AUTHZ-003：既有「未登入回 401」測試通過
  - RDM-AUTHZ-004：既有「其他 Organizer 回 404 逐字相同」測試通過
  - RDM-AUTHZ-005：既有「其他 Organizer 已核銷票券回 404 而非 409」測試通過
  - RDM-AUTHZ-006：既有「停權前 token 在過期前仍可核銷」測試通過
  - RDM-AUTHZ-007：既有「歸屬關聯查不到回 500」測試通過（decorator 已改用新方法）
- [x] 5.11 `AdminTicketsControllerTests`（查詢持票人）：
  - RDM-HOLDER-001：自家需實名活動 `Issued` 票券回 200，含完整姓名「王小明」、末四碼「1234」與 `ticketStatus`
  - RDM-HOLDER-002：自家不需實名活動票券（買家已登記）回 200，`holderRealName` 與 `holderNationalIdLast4` 為 null
  - RDM-HOLDER-003：其他 Organizer 的票券回 404，body 與「票券不存在」逐字相同，且不含任何持票人欄位
  - RDM-HOLDER-004：已登入但 Access Token 沒有 `OrganizerId` claim 的會員（含角色為 `Admin` 者）呼叫回 403，body 不含任何持票人欄位
  - RDM-HOLDER-005：未帶 Authorization 標頭呼叫回 401，body 不含任何持票人欄位
  - RDM-HOLDER-006：查詢後 DB 中 Ticket 仍為 Issued、核銷時間為 null
  - RDM-HOLDER-008：自家 `Redeemed` 票券回 409，body 不含任何姓名或末四碼
  - RDM-HOLDER-009：活動開始時間已過、`Issued` 票券回 200 並含持票人資料
  - RDM-HOLDER-010：角色為 `Admin`、目前 `OrganizerId` 為 Organizer B 的使用者查詢 Organizer A 的票券，回 404，body 與「票券不存在」逐字相同
  - 路徑 Id 非 GUID 時回 404
  - RNV-NOSTORE-004：查詢自家需實名 `Issued` 票券回 200，標頭 `Cache-Control` 含 `no-store`
  - RNV-NOSTORE-005：查詢其他 Organizer 票券的 404、查詢自家 `Redeemed` 票券的 409，標頭 `Cache-Control` 都含 `no-store`
- [x] 5.12 RDM-HOLDER-007：以 fault-injection factory 新增 `IMemberRealNameRepository` decorator（指定的 Member Id 回傳 null），查詢持票人時斷言 500，ProblemDetails 不含任何持票人欄位，log 例外訊息只含 Ticket Id 與 Member Id
- [x] 5.13 RNV-LOG-001：以 `RecordingLogger`／Serilog 結構化屬性擷取，用獨特的姓名與末四碼（例如「實名測試甲乙丙」「8642」）依序執行四個請求：登記成功 → 再登記 409 → 格式錯誤 400 → `GET /api/members/me`（先斷言回應含該姓名，確認這條路徑真的讀到個資）；斷言四個請求期間所有 `LogEvent` 的訊息與結構化屬性都不含這兩個值
- [x] 5.14 RNV-LOG-002：同樣的擷取方式，依序執行核銷（未帶旗標）409 → 查詢持票人 → 核銷（帶旗標）成功；斷言 log 中都不含持票人的姓名與末四碼
- [x] 5.15 RDM-HOLDER-011：同樣的擷取方式，依序對自家 `Issued` 票券查詢成功、自家 `Redeemed` 票券查詢得到 409、其他 Organizer 的票券查詢得到 404；斷言：
  - 三次各有一筆 Information 稽核日誌，結構化屬性含 Ticket Id、呼叫者 Member Id、Organizer Id 與結果
  - 成功那筆另含持票人 Member Id
  - 三筆都不含持票人姓名或末四碼
- [x] 5.16 RNV-LOG-003 實名閘門日誌：
  - 已登記買家（使用獨特的姓名與末四碼）對需實名活動下單成功、加入排隊成功：閘門讀取了實名，斷言 log 不含這兩個值
  - 未登記買家下單與加入排隊被 `RealNameRequired` 擋下：斷言 log 結構化屬性中沒有名稱含 `RealName`／`NationalId` 的屬性
- [x] 5.16a 會員停用後的實名保留（以既有 `POST /api/admin/members/{id}/deactivate`、`/activate` 端點操作）：
  - RNV-RETAIN-001：已登記會員停用後查 DB，姓名與末四碼不變；重新啟用後再查一次，仍不變
  - RNV-RETAIN-002：買家付款出票後被停用，操作人員查詢該 `Issued` 票券的持票人回 200，含該買家的姓名與末四碼
  - RNV-RETAIN-003：同上，帶 `isHolderVerified = true` 核銷回 200，DB 中票券為 `Redeemed`
- [x] 5.16b RNV-CACHE-001：資料庫中活動為需實名，以測試直接把該活動寫成 `isRealNameRequired = false`（另一案例為不含此欄位的舊格式）放進 `query-cache:events:list`；斷言公開列表回傳 false（確認快取確實被命中），同時未登記會員下單與加入排隊（活動已開啟熱門搶購模式）都回 403 `title = "RealNameRequired"`
- [x] 5.17 部署前置檢查（自動化，不列為 Acceptance Criteria、沒有 Scenario ID）：對應 design.md 決策 5「禁止在任何環境的連線字串加入 Include Error Detail」的規範性禁令，目的是讓違反這條禁令的設定變更在 CI 中失敗：測試讀取 repo 內所有 `appsettings*.json`、`docker-compose*.yml`、`.env.example`，斷言連線字串設定中不含 `Include Error Detail`（不分大小寫、忽略空白）；並斷言 WebApi 測試主機實際解析出的 `ConnectionStrings` 同樣不含

## 6. 前端

- [x] 6.1 `apiResponses.ts`：`MemberProfile` 新增三個實名欄位；`Event`／後台活動摘要型別新增 `isRealNameRequired`；新增 `TicketHolder` 型別。api 層一律用 `authorizedRequest`：新增 `registerRealName`、`getTicketHolder`；`redeemTicket` 支援 `isHolderVerified`；建立活動 API 支援 `isRealNameRequired`
- [x] 6.2 新增 `RealNamePage.vue`（路由 `/me/real-name`，`requiresAuth`），依 design.md 決策 8 與 UI 細節表實作：確認對話框列出輸入值、預設焦點在「返回修改」、409 時重新查詢、redirect 規則（以 `/` 開頭且不以 `//` 開頭）
- [x] 6.3 `BuyerLayout.vue` 會員選單新增「實名資料」
- [x] 6.4 `EventDetailPage.vue`：「本活動需實名」標示；已登入時查詢個人資料，未登記則顯示引導（失敗時不顯示、不阻擋）；下單與加入排隊收到 403 `RealNameRequired` 時顯示引導，不走泛用錯誤，也不導向登入頁
- [x] 6.5 `EventCreatePage.vue`：「需實名」勾選框與說明文字，送出 `isRealNameRequired`；後台 `EventListPage.vue` 顯示「需實名」文字標示
- [x] 6.6 `RedemptionScannerPage.vue`：
  - 在 `ticketRedemptionOutcome` 新增 `HolderVerificationRequired` 分類
  - 依 admin-web-ui spec 新增確認面板狀態：暫停掃描處理、不套用自動恢復、確認時重送原簽章並帶旗標、處理中停用按鈕
  - 查詢持票人失敗時顯示「重試」「放棄」；重試只重新查詢持票人；查詢回 409 時顯示「已核銷過」
  - 掃碼與手動輸入共用同一套流程

## 7. 前端測試

- [x] 7.1 `RealNamePage.test.ts`：
  - BW-RN-PAGE-001：未登記時顯示姓名、末四碼輸入欄位與「登記後無法自行修改」
  - BW-RN-PAGE-002：按送出後對話框內容含「王小明」與「1234」，`document.activeElement` 是「返回修改」按鈕，登記 API mock 呼叫次數為 0
  - BW-RN-PAGE-003：選「返回修改」與關閉對話框兩種情況下，登記 API 呼叫次數皆為 0，輸入欄位保留原值
  - BW-RN-PAGE-004：確認登記成功後顯示成功提示、唯讀顯示「**34」、輸入欄位不存在
  - BW-RN-PAGE-005：已登記時唯讀顯示，不存在任何 input 或修改按鈕
  - BW-RN-PAGE-006：登記 API 回 409 時，個人資料 API 被再次呼叫，畫面顯示已登記狀態與「已登記過實名」
  - BW-RN-PAGE-007：`redirect=/events/{id}` 登記成功後 `router.push` 目標為 `/events/{id}`
  - BW-RN-PAGE-008：`redirect=//evil.example.com` 登記成功後未導向，留在實名頁
  - BW-RN-PAGE-010：姓名為 `<img src=x onerror=alert(1)>` 時以文字顯示，DOM 中不存在 `img`
  - BW-RN-PAGE-011：姓名只有半形空白或 U+3000 時顯示「請輸入真實姓名」，不開對話框、登記 API 呼叫次數為 0（第 5 輪對抗審查發現）
  - BW-RN-PAGE-012：姓名為前後各一個 U+3000 的「王小明」時，對話框 textContent 為「王小明」、登記 API 以「王小明」呼叫（第 5 輪對抗審查發現）；對話框開啟後把輸入改成空白／「12」再確認，API 仍以開啟當下的「王小明」／「1234」呼叫；對話框開著時改成空白再送出表單，對話框關閉並顯示必填提示（第 6 輪對抗審查發現）
- [x] 7.2 `BuyerLayout.test.ts`：
  - BW-RN-PAGE-009：會員選單有「實名資料」，點選後導向 `/me/real-name`
- [x] 7.3 `EventDetailPage.test.ts`：
  - BW-RN-EVENT-001：需實名活動顯示「本活動需實名」
  - BW-RN-EVENT-002：不需實名活動不顯示標示與登記提示
  - BW-RN-EVENT-003：未登記者看到「購票前需先登記實名」，「前往登記」連結目標含 `redirect=/events/{id}`
  - BW-RN-EVENT-004：已登記者看到標示、看不到登記提示
  - BW-RN-EVENT-005：建立訂單 API 回 403 `RealNameRequired` 時顯示引導，路由未變更為登入頁，畫面沒有泛用錯誤訊息
  - BW-RN-EVENT-006：加入排隊 API 回 403 `RealNameRequired` 時顯示引導，未進入排隊等待畫面
  - BW-RN-EVENT-007：個人資料 API 失敗時不顯示登記提示，送出訂單按鈕可用且點擊後會呼叫建立訂單 API
- [x] 7.4 `EventCreatePage.test.ts` 與 `EventListPage.test.ts`：
  - AWU-EVENT-RN-001：勾選後送出，請求 body `isRealNameRequired = true`
  - AWU-EVENT-RN-002：未勾選送出，請求 body `isRealNameRequired = false`
  - AWU-EVENT-RN-003：進入頁面時勾選框未勾選，旁邊有「建立後不可變更」說明
  - AWU-EVENT-RN-004：後台列表中需實名活動顯示「需實名」文字，不需實名活動不顯示
- [x] 7.5 `RedemptionScannerPage.test.ts`（每個測試都斷言核銷 API 與查詢持票人 API 各自的呼叫次數）：
  - AWU-REDEEM-RN-001：核銷回 `HolderVerificationRequired` 後，查詢持票人被呼叫 1 次，面板顯示姓名、末四碼、「確認核銷」「放棄」，畫面沒有錯誤或核銷失敗訊息
  - AWU-REDEEM-RN-002：按「確認核銷」後，第二次核銷請求的 path Id、`signature` 與第一次相同，`isHolderVerified = true`；成功後顯示既有成功結果
  - AWU-REDEEM-RN-003：按「放棄」後回到可掃描狀態，核銷總呼叫次數維持 1
  - AWU-REDEEM-RN-004：以 fake timers 推進超過自動恢復停留時間，面板仍顯示，核銷與查詢呼叫次數不變
  - AWU-REDEEM-RN-005：面板顯示中模擬相機偵測到同一張票與另一張票，核銷與查詢呼叫次數不變
  - AWU-REDEEM-RN-006：確認核銷請求未回應前連點兩次，核銷第二次請求只送出 1 次（總計 2 次）
  - AWU-REDEEM-RN-007：查詢持票人 mock 拋出網路錯誤時，顯示「重試」「放棄」；核銷 1 次、查詢 1 次
  - AWU-REDEEM-RN-008：手動輸入路徑回 `HolderVerificationRequired` 後顯示面板；確認時第二次核銷請求帶 `isHolderVerified = true` 且 `signature` 為 `null`（沿用既有手動核銷慣例：null 代表操作人員信任操作；實作時同步，原文「body 沒有 `signature`」）
  - AWU-REDEEM-RN-009：姓名為 `<img src=x onerror=alert(1)>` 時以文字顯示，DOM 中不存在 `img`
  - AWU-REDEEM-RN-010：不需實名票券核銷成功時直接顯示成功結果，查詢持票人呼叫次數為 0
  - AWU-REDEEM-RN-011：查詢持票人回 500 時顯示「重試」「放棄」，核銷 1 次
  - AWU-REDEEM-RN-012：查詢持票人回 404、另一個測試回 403 時，各自顯示「重試」「放棄」、不顯示面板，核銷 1 次
  - AWU-REDEEM-RN-013：查詢失敗後按「重試」、第二次查詢成功時顯示面板；核銷 1 次、查詢 2 次
  - AWU-REDEEM-RN-014：查詢失敗後按「放棄」回到可掃描狀態；核銷 1 次且未帶 `isHolderVerified`、查詢 1 次
  - AWU-REDEEM-RN-015：查詢持票人回 409 時顯示「已核銷過」、不顯示面板，核銷 1 次
  - AWU-REDEEM-RN-016：掃碼路徑放棄後相機持續讀到同一張票不呼叫任何端點；讀到另一張票正常核銷（審查後補，使用者 2026-10-02 決定）
  - AWU-REDEEM-RN-017：相機可用時切手動輸入、送出後放棄，回到手動輸入表單而非相機（審查後補，使用者 2026-10-02 決定）；composable 測試另驗證放棄時相機仍在重新初始化、再手動送出的情境不卡在 initializing（第 2 輪對抗審查發現）
  - 既有 ADMIN-REDEEM-* 測試全數維持通過，確認非實名流程未改變
- [x] 7.6 `npm run lint`、`vue-tsc` 通過

## 8. 驗證與收尾

- [x] 8.1 容器內跑後端全部測試（Domain／Application／Infrastructure／WebApi）與前端全部測試，回報實際通過數
- [x] 8.2 部署檢查清單演練（開發環境，design.md Migration Plan 第 2 點；營運驗證，非 AC 測試）：依序執行 `docker compose exec api dotnet ef database update` → `docker compose up -d --build api` → 刪除 `query-cache:events:list`；在任何查詢之前以 `redis-cli EXISTS` 確認 key 不存在，再呼叫一次公開活動列表，確認每筆都有 `isRealNameRequired`
- [x] 8.3 以 claude-in-chrome 實際操作驗證：
  - 建立需實名活動
  - 未登記買家看到引導，下單被擋
  - 登記實名後導回原活動頁並下單、付款
  - 後台核銷頁手動輸入 Ticket ID，看到確認面板；先按放棄確認票券仍未核銷，再確認核銷
  - 以 DB 查詢核對每一步的狀態，不只依畫面判斷
- [x] 8.4 依 CLAUDE.md 防禦性檢查規則，對 `RegisterRealNameHandler`、`GetTicketHolderHandler`、`RedeemTicketHandler`、`OrderService`、`JoinPurchaseQueueHandler` 的變更套用 hardener 檢查清單，再呼叫 strict-reviewer
- [ ] 8.5 歸檔時同步主 spec；保留 design.md 決策 3 的 I1／I2 不變量與「未來需要重新評估的條件」；更新 `docs/project-scope.md`：第 2 節 Could「實名制驗證」標為已完成；第 8 節剩餘 Could 項目改寫（同時修正「Queue 排隊室 Redis 資料結構重寫」的過時描述，需先與使用者確認）；第 8 節新增一條待確認事項「共用／正式環境部署前須先限制 Seq 存取並訂定日誌保存期限（實名稽核日誌與持票人查詢相關）」

## 9. Scenario 追溯表

每個 Scenario 對應的測試任務（自動由上文產生，見 9.1）。

- [ ] 9.1 實作完成前重新產生並核對本表，確認每個 Scenario 都至少對應一個測試任務、每個測試名稱都含其 Scenario 編號。member-management delta 的 MODIFIED requirement 原樣保留了 4 個沒有編號的既有情境（「查詢自己的會員資料」「未登入呼叫查詢端點」「更新顯示名稱成功」「嘗試修改角色或帳號狀態遭拒」），由既有測試涵蓋，本表不收錄，也不視為缺漏；實作後確認那些既有測試仍通過

| Scenario | Spec | 測試任務 |
| --- | --- | --- |
| AWU-EVENT-RN-001 | admin-web-ui | 7.4 |
| AWU-EVENT-RN-002 | admin-web-ui | 7.4 |
| AWU-EVENT-RN-003 | admin-web-ui | 7.4 |
| AWU-EVENT-RN-004 | admin-web-ui | 7.4 |
| AWU-REDEEM-RN-001 | admin-web-ui | 7.5 |
| AWU-REDEEM-RN-002 | admin-web-ui | 7.5 |
| AWU-REDEEM-RN-003 | admin-web-ui | 7.5 |
| AWU-REDEEM-RN-004 | admin-web-ui | 7.5 |
| AWU-REDEEM-RN-005 | admin-web-ui | 7.5 |
| AWU-REDEEM-RN-006 | admin-web-ui | 7.5 |
| AWU-REDEEM-RN-007 | admin-web-ui | 7.5 |
| AWU-REDEEM-RN-011 | admin-web-ui | 7.5 |
| AWU-REDEEM-RN-012 | admin-web-ui | 7.5 |
| AWU-REDEEM-RN-013 | admin-web-ui | 7.5 |
| AWU-REDEEM-RN-014 | admin-web-ui | 7.5 |
| AWU-REDEEM-RN-015 | admin-web-ui | 7.5 |
| AWU-REDEEM-RN-016 | admin-web-ui | 7.5 |
| AWU-REDEEM-RN-017 | admin-web-ui | 7.5 |
| AWU-REDEEM-RN-008 | admin-web-ui | 7.5 |
| AWU-REDEEM-RN-009 | admin-web-ui | 7.5 |
| AWU-REDEEM-RN-010 | admin-web-ui | 7.5 |
| BW-RN-PAGE-001 | buyer-web-ui | 7.1 |
| BW-RN-PAGE-002 | buyer-web-ui | 7.1 |
| BW-RN-PAGE-003 | buyer-web-ui | 7.1 |
| BW-RN-PAGE-004 | buyer-web-ui | 7.1 |
| BW-RN-PAGE-005 | buyer-web-ui | 7.1 |
| BW-RN-PAGE-006 | buyer-web-ui | 7.1 |
| BW-RN-PAGE-007 | buyer-web-ui | 7.1 |
| BW-RN-PAGE-008 | buyer-web-ui | 7.1 |
| BW-RN-PAGE-009 | buyer-web-ui | 7.2 |
| BW-RN-PAGE-010 | buyer-web-ui | 7.1 |
| BW-RN-PAGE-011 | buyer-web-ui | 7.1 |
| BW-RN-PAGE-012 | buyer-web-ui | 7.1 |
| BW-RN-EVENT-001 | buyer-web-ui | 7.3 |
| BW-RN-EVENT-002 | buyer-web-ui | 7.3 |
| BW-RN-EVENT-003 | buyer-web-ui | 7.3 |
| BW-RN-EVENT-004 | buyer-web-ui | 7.3 |
| BW-RN-EVENT-005 | buyer-web-ui | 7.3 |
| BW-RN-EVENT-006 | buyer-web-ui | 7.3 |
| BW-RN-EVENT-007 | buyer-web-ui | 7.3 |
| EVT-REALNAME-001 | event-management | 3.6、4.6、5.3 |
| EVT-REALNAME-002 | event-management | 4.6、5.3 |
| EVT-REALNAME-003 | event-management | 5.3 |
| EVT-REALNAME-004 | event-management | 2.8 |
| MM-PROFILE-RN-001 | member-management | 4.4、5.1 |
| MM-PROFILE-RN-002 | member-management | 4.4、5.1 |
| MM-UPDATE-RN-001 | member-management | 4.5、5.1 |
| PQ-RN-JOIN-001 | purchase-queue | 4.8、5.7 |
| PQ-RN-JOIN-002 | purchase-queue | 4.8、5.7 |
| PQ-RN-JOIN-003 | purchase-queue | 4.8、5.7 |
| PQ-RN-JOIN-004 | purchase-queue | 4.8、5.7 |
| PQ-RN-JOIN-005 | purchase-queue | 4.8、5.7 |
| PQ-RN-JOIN-006 | purchase-queue | 4.8、5.7 |
| PQ-RN-JOIN-007 | purchase-queue | 3.12、4.8 |
| PQ-RN-JOIN-008 | purchase-queue | 4.8 |
| RNV-REGISTER-001 | real-name-verification | 4.3、5.1 |
| RNV-REGISTER-002 | real-name-verification | 4.3、5.1 |
| RNV-REGISTER-003 | real-name-verification | 4.3、5.1 |
| RNV-REGISTER-004 | real-name-verification | 2.5、4.3、5.2 |
| RNV-REGISTER-005 | real-name-verification | 5.1 |
| RNV-REGISTER-006 | real-name-verification | 4.3、5.1 |
| RNV-FORMAT-001 | real-name-verification | 4.2、5.1 |
| RNV-FORMAT-002 | real-name-verification | 4.2、5.1 |
| RNV-FORMAT-003 | real-name-verification | 4.3、5.1 |
| RNV-FORMAT-004 | real-name-verification | 4.2、5.1 |
| RNV-FORMAT-005 | real-name-verification | 4.2、5.1 |
| RNV-FORMAT-006 | real-name-verification | 4.2、5.1 |
| RNV-FORMAT-007 | real-name-verification | 4.2 |
| RNV-FORMAT-008 | real-name-verification | 4.2、7.1 |
| RNV-FORMAT-009 | real-name-verification | 4.2、7.1 |
| RNV-FORMAT-010 | real-name-verification | 4.2、7.1 |
| RNV-RETAIN-001 | real-name-verification | 1.1、5.16a |
| RNV-RETAIN-002 | real-name-verification | 5.16a |
| RNV-RETAIN-003 | real-name-verification | 5.16a |
| RNV-MASK-001 | real-name-verification | 4.1 |
| RNV-MASK-002 | real-name-verification | 5.1 |
| RNV-NOSTORE-001 | real-name-verification | 5.1 |
| RNV-NOSTORE-002 | real-name-verification | 5.1 |
| RNV-NOSTORE-003 | real-name-verification | 5.1 |
| RNV-NOSTORE-004 | real-name-verification | 5.11 |
| RNV-NOSTORE-005 | real-name-verification | 5.11 |
| RNV-ERROR-001 | real-name-verification | 5.1、5.6、5.8 |
| RNV-LOG-001 | real-name-verification | 5.13 |
| RNV-LOG-002 | real-name-verification | 5.14 |
| RNV-LOG-003 | real-name-verification | 5.16 |
| RNV-ROLLBACK-001 | real-name-verification | 2.8 |
| RNV-ROLLBACK-002 | real-name-verification | 2.8 |
| RNV-ROLLBACK-003 | real-name-verification | 2.8 |
| RNV-CACHE-001 | real-name-verification | 5.16b |
| TP-BROWSE-RN-001 | ticket-purchase | 5.4 |
| TP-BROWSE-RN-002 | ticket-purchase | 5.5 |
| TP-RN-ORDER-001 | ticket-purchase | 4.7、5.6 |
| TP-RN-ORDER-002 | ticket-purchase | 4.7、5.6 |
| TP-RN-ORDER-003 | ticket-purchase | 4.7、5.6 |
| TP-RN-ORDER-004 | ticket-purchase | 4.7 |
| TP-RN-ORDER-005 | ticket-purchase | 5.6 |
| TP-RN-ORDER-006 | ticket-purchase | 4.7、5.6 |
| TP-RN-ORDER-007 | ticket-purchase | 3.12、4.7 |
| TP-RN-ORDER-008 | ticket-purchase | 4.7 |
| TP-RN-ORDER-009 | ticket-purchase | 4.7 |
| RDM-AUTHZ-001 | ticket-redemption | 5.10 |
| RDM-AUTHZ-002 | ticket-redemption | 5.10 |
| RDM-AUTHZ-003 | ticket-redemption | 5.10 |
| RDM-AUTHZ-004 | ticket-redemption | 5.10 |
| RDM-AUTHZ-005 | ticket-redemption | 5.10 |
| RDM-AUTHZ-006 | ticket-redemption | 5.10 |
| RDM-AUTHZ-007 | ticket-redemption | 3.9、5.10 |
| RDM-RN-001 | ticket-redemption | 4.9、5.8 |
| RDM-RN-002 | ticket-redemption | 4.9、5.8 |
| RDM-RN-003 | ticket-redemption | 4.9、5.8 |
| RDM-RN-004 | ticket-redemption | 4.9、5.8 |
| RDM-RN-005 | ticket-redemption | 4.9、5.8 |
| RDM-RN-006 | ticket-redemption | 4.9、5.8 |
| RDM-RN-007 | ticket-redemption | 4.9、5.8 |
| RDM-RN-008 | ticket-redemption | 5.9 |
| RDM-RN-009 | ticket-redemption | 5.8 |
| RDM-HOLDER-001 | ticket-redemption | 4.10、5.11 |
| RDM-HOLDER-002 | ticket-redemption | 4.10、5.11 |
| RDM-HOLDER-003 | ticket-redemption | 4.10、5.11 |
| RDM-HOLDER-004 | ticket-redemption | 5.11 |
| RDM-HOLDER-005 | ticket-redemption | 5.11 |
| RDM-HOLDER-006 | ticket-redemption | 4.10、5.11 |
| RDM-HOLDER-007 | ticket-redemption | 4.10、5.12 |
| RDM-HOLDER-008 | ticket-redemption | 4.10、5.11 |
| RDM-HOLDER-009 | ticket-redemption | 4.10、5.11 |
| RDM-HOLDER-010 | ticket-redemption | 5.11 |
| RDM-HOLDER-011 | ticket-redemption | 5.15 |
