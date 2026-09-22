## 1. Domain

- [ ] 1.1 新增 `OrganizerStatus` enum（`Pending`／`Approved`／`Rejected`／`Suspended`）
- [ ] 1.2 新增 `Organizer` entity 基本屬性與建構子（`Id`、`Name`、`Status`、`CreatedByMemberId`、`CreatedAtUtc`、`ReviewedByMemberId`?、`ReviewedAtUtc`?）；`Name` 建構時驗證去除頭尾空白後長度須為 1～100 字元
- [ ] 1.2a `Organizer.Approve()`／`Reject()` 方法：驗證僅 `Pending` 可轉換，不合法轉換拋出領域例外；成功時記錄 `ReviewedByMemberId`／`ReviewedAtUtc`
- [ ] 1.2b `Organizer.Suspend()` 方法：驗證僅 `Approved` 可轉換，不合法轉換拋出領域例外
- [ ] 1.3 新增 `OrganizerMemberRole` enum（目前僅 `Owner`）
- [ ] 1.4 新增 `OrganizerMember` entity（`OrganizerId`、`MemberId`、`Role`）
- [ ] 1.5 `RefreshToken` entity 新增可為 null 的 `OrganizerId` 屬性；`Issue` 靜態工廠方法新增可選 `organizerId` 參數（供輪替時延續／清空使用，見 design.md Decision 2）
- [ ] 1.5a `RefreshToken` 新增 `UpdateOrganizerContext(Guid organizerId)` 方法，供切換操作情境時原地更新目前有效的 Refresh Token 記錄，不觸發 Token 輪替

## 2. Application

- [ ] 2.1 `ApplyForOrganizerHandler`：已登入 Member 申請建立 Organizer，建立 `Organizer`（`Pending`）與對應 `OrganizerMember`（`Owner`）
- [ ] 2.2 `GetMyOrganizersHandler`：查詢呼叫者所屬的所有 `OrganizerMember` 關聯的 Organizer 清單
- [ ] 2.2a `GetPendingOrganizersHandler`：Admin 查詢目前所有 `Pending` 狀態的 Organizer 及其申請人資訊
- [ ] 2.3 `ReviewOrganizerHandler`：Admin 核准／駁回指定 Organizer，呼叫 `Organizer.Approve()`／`Reject()`，記錄審核者與審核時間
- [ ] 2.4 `SuspendOrganizerHandler`：Admin 停權指定 Organizer，呼叫 `Organizer.Suspend()`
- [ ] 2.5 `SwitchOrganizerContextHandler` 驗證層：Refresh Token 欄位未提供或空白時回傳 400 驗證錯誤（FluentValidation／DataAnnotations，比照既有必填欄位驗證慣例），不進入 Handler、不查詢資料庫
- [ ] 2.5a `SwitchOrganizerContextHandler` Token 查核：欄位有值時，以 `TokenHash` 精確查出對應的 `RefreshToken` 資料列，驗證其存在、`MemberId` 與呼叫者一致、狀態為 `Active` 且未過期，不符任一條件回傳 401（不視為驗證錯誤，是「這個 Token 不被信任」的授權層判斷）
- [ ] 2.5b `SwitchOrganizerContextHandler` 資格查核：Token 查核通過後，驗證呼叫者是目標 Organizer 的成員且該 Organizer 狀態為 `Approved`，不符回傳對應的 403／404／狀態衝突（見 `organizer-management` spec 的三種區分狀態碼）
- [ ] 2.5c `SwitchOrganizerContextHandler` 寫入與併發處理：驗證全數通過後呼叫**這一筆**（且僅這一筆）Refresh Token 記錄的 `UpdateOrganizerContext`，並回傳供 WebApi 換發 Access Token 所需的資訊（含 `OrganizerId`）；比照既有 `RefreshTokenHandler.cs:68-72` 的既定模式，`SaveChanges` 時 MUST 捕捉 `DbUpdateConcurrencyException`（見 design.md「邊界情況：切換與換發併發競爭同一筆 Refresh Token」），**明確回傳 401**（不建立新 Token、不更新任何 Refresh Token 記錄），不得讓例外往上拋給全域 `IExceptionHandler` 變成非預期的 500
- [ ] 2.6 既有 `RefreshTokenHandler` 新增 `OrganizerId` 重新驗證邏輯（見 design.md Decision 2）：讀取 `existingToken.OrganizerId`，非空時查詢對應 `OrganizerMember`／`Organizer` 是否仍存在、呼叫者仍是成員、且該 Organizer 仍為 `Approved`；驗證通過則呼叫 `RefreshToken.Issue` 時傳入相同 `organizerId`，且產生的新 Access Token 帶 `OrganizerId` claim；驗證未通過則 `Issue` 的 `organizerId` 傳 null，新 Access Token 不帶該 claim

## 3. Infrastructure

- [ ] 3.1 新增 `OrganizerConfiguration`／`OrganizerMemberConfiguration`（EF Core Fluent API，對應 CLAUDE.md 的 EF ctor-binding 慣例，`private set` 屬性需明確 `Property()` 綁定）；`OrganizerMemberConfiguration` 對 `(OrganizerId, MemberId)` 建立複合唯一索引，防止重複成員列（防禦性最佳實踐，本次交付範圍內雖無任何路徑會產生重複，仍於資料庫層保底）
- [ ] 3.1a 更新既有 `RefreshTokenConfiguration` 加入 `OrganizerId` 欄位映射（nullable，外鍵指向 `Organizers`，刪除行為採 `NoAction`／`Restrict`，不因 Organizer 被刪除而級聯刪除 Refresh Token）
- [ ] 3.2 新增 EF Core migration：建立 `Organizers`、`OrganizerMembers` 資料表；`RefreshTokens` 新增可為 null 的 `OrganizerId` 欄位與外鍵——**本次不觸碰 `Events` 資料表**
- [ ] 3.3 既有 `ITokenService` 介面（`src/ProjectC.Application/Common/Interfaces/ITokenService.cs`）的 `GenerateAccessToken(Member member)` 簽章同步新增可選 `organizerId` 參數，`JwtTokenService.GenerateAccessToken` 實作跟著更新，帶值時額外加入 `OrganizerId` claim；Application 層 Handler 依 DIP 僅依賴此介面，未同步更新介面簽章會導致 Handler 端編譯失敗

## 4. WebApi

- [ ] 4.1 新增 `OrganizerController`：`POST /api/organizers`（申請，呼叫 2.1）
- [ ] 4.1a `OrganizerController`：`GET /api/organizers/mine`（我的清單，呼叫 2.2）
- [ ] 4.1b `OrganizerController`：`POST /api/organizers/{id}/switch-context`（請求 body 需帶呼叫者目前持有的 Refresh Token 明文，見 design.md Decision 1；切換成功呼叫 `ITokenService` 換發帶 `OrganizerId` claim 的新 Access Token，呼叫 2.5～2.5c）
- [ ] 4.2 新增 Admin 審核端點（沿用 `/api/admin/` 前綴慣例）：`GET /api/admin/organizers?status=Pending`，限 `MemberRole.Admin`，呼叫 2.2a
- [ ] 4.2a `PATCH /api/admin/organizers/{id}/approve`，限 `MemberRole.Admin`（呼叫 2.3）
- [ ] 4.2b `PATCH /api/admin/organizers/{id}/reject`，限 `MemberRole.Admin`（呼叫 2.3）
- [ ] 4.2c `PATCH /api/admin/organizers/{id}/suspend`，限 `MemberRole.Admin`（呼叫 2.4）
- [ ] 4.3 新增共用擴充方法 `ClaimsPrincipalExtensions.TryGetOrganizerId(this ClaimsPrincipal principal, out Guid organizerId)`：取出 `OrganizerId` claim 並驗證可解析為合法、非 `Guid.Empty` 的 `Guid`；claim 不存在、空字串、無法解析、或為 `Guid.Empty` 皆回傳 `false`，不拋出例外
- [ ] 4.3a 新增 Authorization Policy `RequireOrganizerContext`，其 Handler 呼叫 4.3 的 `TryGetOrganizerId` 判定是否通過（不得只用 `HasClaim` 檢查存在性），驗證失敗一律回傳 403（fail-closed，不因解析例外變成 500）——**本次僅定義 Policy，不套用至任何既有 Controller**；套用是後續依賴本次的變更（`event-management-organizer-scoping` 等）的範圍
- [ ] 4.4 確認既有 `/api/auth/refresh` 端點呼叫更新後的 `RefreshTokenHandler`（2.6），回傳的 `AuthTokensDto` 型別不變，不需調整 Controller 簽章

## 5. 前端（admin-web-ui）

- [ ] 5.1 新增「申請建立主辦方」頁面與表單（含名稱長度前端驗證）
- [ ] 5.2 新增「我的主辦方」清單頁：顯示所屬 Organizer 與狀態，`Approved` 項目提供切換操作
- [ ] 5.3 導覽列新增目前切換所在 Organizer 名稱顯示與前往「我的主辦方」清單頁入口；Access Token 的 `OrganizerId` claim 僅帶 GUID，前端 SHALL 於 bootstrap／換發後呼叫既有 `GET /api/organizers/mine`（回傳含 `Id`／`Name`／`Status`）比對出對應名稱顯示，不需新增後端能力
- [ ] 5.4 新增平台管理員專用的 `/admin/organizers` 審核清單頁（核准／駁回操作），沿用既有「Admin 角色可進入後台」規則，不需新增路由守衛邏輯
- [ ] 5.5 申請成功後自動嘗試切換（預期因 `Pending` 被拒絕，顯示「待審核」訊息並導向清單頁，見 spec.md 對應 Scenario）
- [ ] 5.6 前端呼叫切換操作情境端點時，一併傳入目前持有的 Refresh Token（既有 `/api/auth/refresh` 流程本來就有儲存，直接複用，不新增儲存機制）

## 6. 測試輔助工具

- [ ] 6.1 更新測試共用 fixture／`AuthTestHelper`，新增建立已核准 Organizer 並取得帶 `OrganizerId` claim 的測試用 Access Token 的輔助方法（供後續依賴本次的變更重用）

## 7. 測試 — organizer-management

測試類型：單元測試（xUnit + Moq/NSubstitute，Handler 邏輯與狀態機驗證）為主；7.25／7.26（併發競爭）為整合測試（Testcontainers Postgres，需要真實資料庫交易，mock 無法驗證樂觀併發權杖的真實行為）。被測主體：`ApplyForOrganizerHandler`／`GetMyOrganizersHandler`／`GetPendingOrganizersHandler`／`ReviewOrganizerHandler`／`SuspendOrganizerHandler`／`SwitchOrganizerContextHandler`／`RefreshTokenHandler`（`OrganizerId` 重新驗證邏輯與併發處理）／`Organizer` Entity 狀態機方法／`ClaimsPrincipalExtensions.TryGetOrganizerId`／`RequireOrganizerContext` Policy Handler。**`ORG-APPLY-002`／`ORG-REVIEW-004`／`ORG-REVIEW-006`／`ORG-SUSPEND-003` 這四條「未登入／非 Admin 呼叫端點」的授權邊界不在本節，因為 Application 層目前、且依 design.md「安全確認-權限」設計也不會有任何角色判斷分支（角色檢查在 Controller 層由 `[Authorize]` 攔截），Handler 單元測試無法實際驗證這些邊界；改在第 9 節以 `ProjectC.WebApi.Tests` 元件測試涵蓋，比照既有 `AdminEventsControllerTests`／`MembersControllerTests` 慣例。**

- [ ] 7.1 [ORG-APPLY-001] 已登入會員申請建立 Organizer 成功，狀態為 `Pending`，申請人成為 `Owner`
- [ ] 7.3 [ORG-APPLY-003] 申請時未提供名稱回傳驗證錯誤，不建立任何 Organizer
- [ ] 7.4 [ORG-APPLY-004] 申請時名稱超過 100 字元長度上限回傳驗證錯誤，不建立任何 Organizer
- [ ] 7.4a [ORG-APPLY-001] 邊界值：名稱去除頭尾空白後剛好 1 字元、剛好 100 字元皆申請成功（對應 `Organizer` entity 建構子驗證，tasks.md 1.2）
- [ ] 7.5 [ORG-APPLY-005] `Pending` 狀態的 Organizer 無法被其成員切換
- [ ] 7.6 [ORG-REVIEW-001] `GetPendingOrganizersHandler` 查詢待審核清單，僅回傳 `Pending` 狀態
- [ ] 7.7 [ORG-REVIEW-002] 平台管理員核准申請，狀態轉為 `Approved` 並記錄審核者與時間
- [ ] 7.8 [ORG-REVIEW-003] 平台管理員駁回申請，狀態轉為 `Rejected` 並記錄審核者與時間
- [ ] 7.10 [ORG-REVIEW-005] 重複審核已處理過的申請（非 `Pending`）回傳狀態衝突，不改變狀態
- [ ] 7.11 [ORG-SUSPEND-001] 平台管理員停權 `Approved` 的 Organizer，狀態轉為 `Suspended`；停權後其成員無法再切換進入該 Organizer
- [ ] 7.12 [ORG-SUSPEND-002] 停權非 `Approved` 狀態的 Organizer 回傳狀態衝突，不改變狀態
- [ ] 7.14 [ORG-LIST-001] 已登入會員查詢自己所屬 Organizer 清單；斷言結果 MUST NOT 包含其他 Member 所屬、呼叫者未加入的 Organizer（建立至少一筆屬於其他 Member 的 Organizer 作為對照資料）
- [ ] 7.15 [ORG-LIST-002] 尚未加入任何 Organizer 時查詢清單回傳空清單，不視為錯誤
- [ ] 7.16 [ORG-SWITCH-001] 提供有效 Refresh Token，切換到自己所屬且 `Approved` 的 Organizer 成功換發帶 `OrganizerId` claim 的 Access Token，且**只有本次提供的那一筆** Refresh Token 記錄的 `OrganizerId` 同步更新；斷言回應內容 MUST NOT 包含新的 Refresh Token
- [ ] 7.16a [ORG-SWITCH-007] 切換成功後，斷言資料庫中該 Member 名下的 `RefreshToken` 資料列筆數不變（未新增任何輪替記錄）；原本持有的 Refresh Token 明文於切換前後相同；切換後立即以該筆 Refresh Token 呼叫既有 `/api/auth/refresh` 端點，斷言仍能成功換發
- [ ] 7.17 [ORG-SWITCH-002] 切換到非自己所屬的 Organizer 回傳 **403**，不換發任何 Token，不更新 Refresh Token 記錄
- [ ] 7.18 [ORG-SWITCH-003] 切換到狀態非 `Approved`（`Pending`／`Rejected`／`Suspended`）的 Organizer 回傳**狀態衝突**，不換發任何 Token，不更新 Refresh Token 記錄
- [ ] 7.19 [ORG-SWITCH-004] 切換到不存在的 Organizer 回傳**找不到（404）**，不換發任何 Token，不更新 Refresh Token 記錄
- [ ] 7.20 [ORG-SWITCH-005] 切換時未提供 Refresh Token（欄位留空）回傳 **400 驗證錯誤**，不換發任何 Token，不更新任何 Refresh Token 記錄，且不查詢資料庫（驗證層攔截）
- [ ] 7.21 [ORG-SWITCH-006] 切換時提供的 Refresh Token 不存在、已非 Active、已過期、或屬於另一個 Member，皆回傳 **401**，不換發任何 Token，不更新任何 Refresh Token 記錄
- [ ] 7.22 [ORG-REFRESH-001] 同一 Member 同時持有兩筆有效 Refresh Token（模擬兩台裝置），對其中一筆呼叫切換成功後，斷言另一筆 Refresh Token 記錄的 `OrganizerId` 維持不變（不受影響）
- [ ] 7.23 [ORG-REFRESH-002] 換發 Access Token 時，若目前 Refresh Token 記錄的 `OrganizerId` 非空、對應 Organizer 仍為 `Approved` 且呼叫者仍是成員，新 Access Token 保留相同 `OrganizerId` claim，新產生的 Refresh Token 記錄延續同一個 `OrganizerId`
- [ ] 7.24 [ORG-REFRESH-003] 換發 Access Token 時，若目前 Refresh Token 記錄的 `OrganizerId` 對應的 Organizer 已被停權，新 Access Token 不帶 `OrganizerId` claim，新產生的 Refresh Token 記錄的 `OrganizerId` 為空
- [ ] 7.24a [ORG-REFRESH-004] 換發 Access Token 時，若目前 Refresh Token 記錄的 `OrganizerId` 非空，但直接以測試手法刪除／不建立對應的 `OrganizerMember` 記錄（模擬成員資格不存在），斷言新 Access Token 不帶 `OrganizerId` claim，新產生的 Refresh Token 記錄的 `OrganizerId` 為空
- [ ] 7.25 [ORG-CONCURRENCY-001] 整合測試：使用兩個並行資料庫交易，交易 A 先對某筆 `RefreshToken` 執行切換操作情境的更新並提交，交易 B（在交易 A 提交前已讀取同一筆記錄的舊版本）之後才嘗試以該記錄執行 Refresh Token 換發並提交，斷言交易 B 拋出 `DbUpdateConcurrencyException` 並被 `RefreshTokenHandler` 捕捉、回傳 401，不建立新的 Refresh Token 記錄；交易 A 寫入的 `OrganizerId` 不被覆寫
- [ ] 7.26 [ORG-CONCURRENCY-002] 整合測試：使用兩個並行資料庫交易，交易 A 先對某筆 `RefreshToken` 執行 Refresh Token 換發（`MarkAsUsed` 並新增輪替記錄）並提交，交易 B（在交易 A 提交前已讀取同一筆記錄的舊版本）之後才嘗試對該記錄執行切換操作情境並提交，斷言交易 B 拋出 `DbUpdateConcurrencyException` 並被 `SwitchOrganizerContextHandler` 捕捉、回傳 401，不換發新 Access Token、不更新任何 Refresh Token 記錄的 `OrganizerId`；交易 A 產生的新 Refresh Token 記錄不受影響
- [ ] 7.27 [ORG-POLICY-001] `TryGetOrganizerId`／`RequireOrganizerContext` Policy Handler 單元測試：`ClaimsPrincipal` 帶有效非空 `OrganizerId` claim 時，`TryGetOrganizerId` 回傳 `true` 並輸出正確的 `Guid`，Policy Handler 判定通過。此為 Policy Handler 自身邏輯的單元測試，不透過真實 HTTP pipeline（本次未套用至任何 Controller，見 4.3a）
- [ ] 7.28 [ORG-POLICY-002] `ClaimsPrincipal` 不含 `OrganizerId` claim 時，`TryGetOrganizerId` 回傳 `false`，Policy Handler 判定不通過（不呼叫 `context.Succeed`）
- [ ] 7.29 [ORG-POLICY-003] `ClaimsPrincipal` 的 `OrganizerId` claim 值為空字串或非 Guid 格式文字時，`TryGetOrganizerId` 回傳 `false`、不拋出例外，Policy Handler 判定不通過
- [ ] 7.30 [ORG-POLICY-004] `ClaimsPrincipal` 的 `OrganizerId` claim 值為 `Guid.Empty` 字串表示時，`TryGetOrganizerId` 回傳 `false`，Policy Handler 判定不通過

## 8. 測試 — admin-web-ui（新畫面）

測試類型：元件測試（Vitest + Vue Test Utils）。被測主體：`ApplyOrganizerPage`、`MyOrganizersPage`、`AdminOrganizerReviewPage`、導覽列（Navbar）元件。

- [ ] 8.1 [AWU-APPLY-001][AWU-APPLY-003] 申請建立主辦方表單：成功送出、名稱留空的驗證錯誤
- [ ] 8.1a [AWU-APPLY-002] 申請成功後元件自動帶目前持有的 Refresh Token 呼叫切換操作情境端點；stub 後端回傳「狀態衝突」（模擬 `Pending` 被拒絕），斷言元件仍顯示「申請已送出，待平台審核」並導向「我的主辦方」清單頁，**不**顯示錯誤訊息、**不**導向一般後台頁面
- [ ] 8.1b [AWU-APPLY-004] 申請成功後自動切換呼叫改為 stub 網路逾時／5xx 等非預期失敗（而非狀態衝突），斷言元件仍顯示「申請已送出，待平台審核」並導向「我的主辦方」清單頁，**不**顯示錯誤訊息
- [ ] 8.2 [AWU-LIST-001][AWU-LIST-002][AWU-LIST-003][AWU-LIST-004] 「我的主辦方」清單頁：顯示清單與狀態、`Approved` 項目可切換（斷言呼叫切換端點時帶入目前持有的 Refresh Token）並導向後台首頁、`Pending`／`Rejected`／`Suspended` 三種非 `Approved` 狀態皆分別驗證不提供切換操作、空清單顯示提示
- [ ] 8.3 [AWU-REVIEW-001][AWU-REVIEW-002][AWU-REVIEW-003] 平台管理員審核清單頁：查看待審核清單、核准／駁回後清單即時刷新
- [ ] 8.4 [AWU-LIST-002] 導覽列元件：stub `GET /api/organizers/mine` 回傳含目標 Organizer 的清單，切換成功（`OrganizerId` claim 更新）後斷言導覽列依 claim 中的 GUID 比對出並顯示對應的 Organizer 名稱（見 5.3 的名稱解析邏輯）

## 9. 測試 — WebApi 授權邊界（元件測試）

測試類型：元件測試（`ProjectC.WebApi.Tests`，使用 `CustomWebApplicationFactory` 對真實 HTTP pipeline 送請求，驗證 `[Authorize]` 屬性實際生效），比照既有 `AdminEventsControllerTests`／`MembersControllerTests` 慣例，需搭配 6.1 的 `AuthTestHelper` 輔助方法。被測主體：`OrganizerController` 各端點的 `[Authorize]`／`[Authorize(Policy = AuthorizationPolicies.AdminOnly)]` 屬性。

- [ ] 9.1 [ORG-APPLY-002] 未帶 Access Token 呼叫 `POST /api/organizers` 回傳 401，不建立任何 Organizer
- [ ] 9.2 [ORG-REVIEW-004] 非 Admin 角色使用者呼叫 `PATCH /api/admin/organizers/{id}/approve`／`reject` 回傳 403，該 Organizer 狀態不變
- [ ] 9.3 [ORG-REVIEW-006] 非 Admin 角色使用者呼叫 `GET /api/admin/organizers?status=Pending` 回傳 403，不回傳任何清單資料
- [ ] 9.4 [ORG-SUSPEND-003] 非 Admin 角色使用者呼叫 `PATCH /api/admin/organizers/{id}/suspend` 回傳 403，該 Organizer 狀態不變
