## 1. Domain 層

- [x] 1.1 建立 `Member` Entity（`Email`、`DisplayName`、`PasswordHash`、`Role` enum(`Member`/`Admin`)、`IsActive`，全部 `private set`）
- [x] 1.2 `Member` 提供行為方法：`Register`（靜態工廠）、`ChangeDisplayName`、`ChangePasswordHash`、`Activate`、`Deactivate`，狀態變更僅能透過方法
- [x] 1.3 建立 `RefreshToken` Entity：`TokenHash`、`MemberId`、`ExpiresAt`、`Status` enum(`Active`/`Used`/`Revoked`)、`PreviousTokenId`（Rotation 鏈），提供 `MarkAsUsed`、`Revoke` 方法（`Expired` 不落地為狀態，由 `IsActive(nowUtc)` 動態判斷；樂觀並發改用 Infrastructure 端 Npgsql `xmin` shadow property 實作，不在 Domain 額外放 `RowVersion` 屬性，見 design.md 決策 7）
- [x] 1.4 建立 `PasswordResetToken` Entity（`TokenHash`、`MemberId`、`ExpiresAt`、`IsUsed`），提供 `MarkAsUsed` 方法
- [x] 1.5 單元測試：`Member` 狀態轉換（對應 spec `member-management` — 更新顯示名稱成功、Activate/Deactivate 狀態轉換）
- [x] 1.6 單元測試：`RefreshToken` 狀態機（`Active` → `Used` / `Revoked` 的合法與不合法轉換、`IsActive` 過期判斷），對應 design.md 決策 7

## 2. Application 層 — 共用介面 (Ports)

> 依 design.md 決策 5、6：這些介面放在 `Application`（非 `Domain`），因為 Domain Entity 本身不呼叫它們。

- [x] 2.1 定義 `IApplicationDbContext`（`DbSet<Member>`、`DbSet<RefreshToken>`、`DbSet<PasswordResetToken>`、`SaveChangesAsync`）
- [x] 2.2 定義 `IPasswordHasher`（`HashPassword` / `VerifyPassword`）
- [x] 2.3 定義 `ITokenService`（`GenerateAccessToken`、`GenerateOpaqueToken`、`HashOpaqueToken` — 後兩者同時服務 Refresh Token 與 Password Reset Token 的產生/雜湊）
- [x] 2.4 定義 `IDateTimeProvider`（`UtcNow`，供 Handler 判斷 Token 是否過期，避免直接呼叫 `DateTime.UtcNow` 造成測試不可控）

## 3. Application 層 — 會員管理 (member-management)

- [x] 3.1 `RegisterMemberHandler` + `RegisterMemberRequestValidator`（FluentValidation：Email 格式、密碼強度，密碼規則抽成共用 `MustBeStrongPassword` 擴充方法）
- [x] 3.2 `GetMyProfileHandler`（回傳不含密碼雜湊的會員資料 DTO）
- [x] 3.3 `UpdateMyProfileHandler`（Request DTO 僅含 `DisplayName`，結構上即無法傳入 `Role`/`IsActive`）
- [x] 3.4 `ActivateMemberHandler`（授權檢查留在 WebApi `[Authorize(Roles = "Admin")]`，Handler 本身不重複判斷角色）
- [x] 3.5 `DeactivateMemberHandler`（同一次 SaveChanges 內同步撤銷該會員所有非 `Revoked` 狀態的 Refresh Token，見 design.md 決策 8）
- [x] 3.6 單元測試：`RegisterMemberHandler` — 對應 scenario「使用未重複的 Email 註冊成功」「使用已存在的 Email 註冊失敗」「密碼不符強度規則」
- [x] 3.7 單元測試：`GetMyProfileHandler` / `UpdateMyProfileHandler` — 對應 scenario「查詢自己的會員資料」「更新顯示名稱成功」（「未登入呼叫查詢端點」「嘗試修改角色或帳號狀態遭拒」為結構/中介層保證，改於 6.11 整合測試驗證）
- [x] 3.8 單元測試：`ActivateMemberHandler` / `DeactivateMemberHandler` — 對應 scenario「管理員停用會員帳號（含撤銷 Refresh Token）」（「非管理員嘗試停用會員帳號」屬授權中介層行為，於 6.11 整合測試驗證）

## 4. Application 層 — 認證 (authentication)

- [x] 4.1 `LoginHandler`（驗證帳密、檢查 `IsActive`、核發 Access Token + Refresh Token；密碼錯誤與 Email 不存在回傳同一則訊息避免帳號枚舉）
- [x] 4.2 `RefreshTokenHandler`（驗證 Refresh Token 狀態與效期、重新查詢 `Member.IsActive`、Rotation、Reuse Detection、`DbUpdateConcurrencyException` 併發衝突處理，見 design.md 決策 7、8）
- [x] 4.3 `LogoutHandler`（撤銷對應 Refresh Token，找不到則視為冪等成功）
- [x] 4.4 `RequestPasswordResetHandler`（產生一次性、具時效性（15 分鐘）的重設 Token，雜湊後存 DB；回傳的明文 Token 僅供未來 Email 整合使用，**WebApi 不得回傳給呼叫端**）
- [x] 4.5 `ResetPasswordHandler`（驗證重設 Token、更新密碼雜湊、撤銷該會員所有 Refresh Token）
- [x] 4.6 單元測試：`LoginHandler` — 對應 scenario「帳密正確且帳號啟用時登入成功」「密碼錯誤時登入失敗」「Email 不存在時登入失敗」「已停用帳號嘗試登入」
- [x] 4.7 單元測試：`RefreshTokenHandler` — 對應 scenario「使用有效且未使用過的 Refresh Token 換發成功」「使用已過期的 Refresh Token」「偵測 Refresh Token 重複使用」「帳號已停用時使用 Refresh Token 換發失敗」
- [x] 4.8 單元測試：`RefreshTokenHandler` 並發刷新 — 以 `FakeApplicationDbContext` 模擬 `SaveChangesAsync` 拋出 `DbUpdateConcurrencyException`，驗證該請求收到 401 且不誤觸發全面撤銷（對應 design.md 決策 7）
- [x] 4.9 單元測試：`LogoutHandler` — 對應 scenario「登出成功」（含未知 Token 的冪等成功）
- [x] 4.10 單元測試：`RequestPasswordResetHandler` / `ResetPasswordHandler` — 對應 scenario「申請密碼重設」「使用有效重設 Token 設定新密碼成功」「使用過期或已使用過的重設 Token」

## 5. Infrastructure 層

- [x] 5.1 `ApplicationDbContext : DbContext, IApplicationDbContext`（沿用既有骨架的類別名稱，未另取 `AppDbContext`），EF Core Fluent API 設定 `Member`（唯一索引 `Email`）、`RefreshToken`（唯一索引 `TokenHash`，樂觀並發改用 Postgres `xmin` shadow property，非額外 `RowVersion` 屬性）、`PasswordResetToken`（唯一索引 `TokenHash`）
- [x] 5.2 建立 EF Core Migration（`InitialCreate`，已產生於 `src/ProjectC.Infrastructure/Persistence/Migrations/`）。**尚未套用至 PostgreSQL** — 此開發環境沒有可連線的本機 Postgres（localhost:5432 無回應），需你在有 Postgres 的環境執行 `dotnet ef database update --project src/ProjectC.Infrastructure --startup-project src/ProjectC.WebApi`
- [x] 5.3 `IPasswordHasher` 實作（`BCryptPasswordHasher`，`BCrypt.Net-Next`）
- [x] 5.4 `ITokenService` 實作（`JwtTokenService`：簽發含 `sub`/`email`/`role`/`jti`/`iat`/`exp` Claim 的 JWT，讀取 `JwtOptions`；`GenerateOpaqueToken`/`HashOpaqueToken` 同時服務 Refresh Token 與 Password Reset Token）
- [x] 5.5 `IDateTimeProvider` 實作（`SystemDateTimeProvider`，回傳 `DateTime.UtcNow`）

## 6. WebApi 層

- [x] 6.1 `AuthController`：`POST /api/auth/register`、`/login`、`/refresh`、`/logout`、`/password-reset/request`、`/password-reset/confirm`
- [x] 6.2 `MembersController`（會員自助，路由前綴 `/api/members`）：`GET /me`、`PUT /me`
- [x] 6.3 `AdminMembersController`（管理員操作，路由前綴 `/api/admin/members`，`[Authorize(Policy = "AdminOnly")]`）：`POST /{id}/activate`、`POST /{id}/deactivate`
- [x] 6.4 定義 `JwtOptions`（`Issuer`/`Audience`/`SigningKey`/`AccessTokenExpirationMinutes`），`AuthOptions`（`RefreshTokenExpirationDays`/`PasswordResetTokenExpirationMinutes`）綁定設定檔並於啟動時驗證（`ValidateDataAnnotations` + `ValidateOnStart`）。**已手動驗證 Fail Fast**：拿掉 `Jwt` 設定值後啟動應用程式，確認會直接拋出 `OptionsValidationException` 中止啟動；補上設定後正常啟動
- [x] 6.5 註冊 JWT Bearer Authentication middleware（`Program.cs` / DI，`MapInboundClaims = false` 避免 claim type 被重新映射）
- [x] 6.6 註冊 Authorization Policy（`AdminOnly`），套用至 `AdminMembersController`
- [x] 6.7 註冊 FluentValidation 至 DI（`AddValidatorsFromAssemblyContaining`）；驗證呼叫點在各 Handler 內部主動呼叫，而非 Controller 層自動驗證（配合 CLAUDE.md「可預期業務失敗優先用 Result 型別」規則，統一走 `Result` 回傳而非例外/自動 400）
- [x] 6.8 DI 註冊：`ApplicationDbContext` 同時綁定為 `IApplicationDbContext`（Scoped）；`IDateTimeProvider` 依 CLAUDE.md DI 判準表註冊為 **Singleton**（表中明確列為 Singleton 範例）；`IPasswordHasher`/`ITokenService` 註冊為 Transient；`AuthOptions` 解析後的值註冊為 Singleton
- [x] 6.9 確認例外一律經全域 `IExceptionHandler`（既有 `GlobalExceptionHandler`）轉換為 `ProblemDetails`；Handler 的預期業務失敗改走 `Result`/`ResultExtensions.ToActionResult`，Controller 內不散落 try-catch
- [x] 6.10 整合測試：`AuthController`（`WebApplicationFactory<Program>` + Testcontainers 啟動獨立 Postgres 容器，不連線開發用的 `db` compose 服務）— 對應 scenario「帳密正確且帳號啟用時登入成功」「密碼錯誤時登入失敗」「Email 不存在時登入失敗」「使用有效且未使用過的 Refresh Token 換發成功」「偵測 Refresh Token 重複使用」（以「重複使用剛輪替過的 Token」驗證，涵蓋同一條防護邏輯）「帳號已停用時使用 Refresh Token 換發失敗」（經登入失敗間接驗證）「登出成功」「申請密碼重設」「使用有效重設 Token 設定新密碼成功」「使用過期或已使用過的重設 Token」（以未知 Token 驗證同一分支）
- [x] 6.11 整合測試：`MembersController` / `AdminMembersController` — 對應 scenario「查詢自己的會員資料」「未登入呼叫查詢端點」「更新顯示名稱成功」「嘗試修改角色或帳號狀態遭拒」（以多送 `role` 欄位驗證模型繫結會忽略）「管理員停用會員帳號（含撤銷 Refresh Token）」「非管理員嘗試停用會員帳號」「已停用帳號嘗試登入」
- [x] 6.12 整合測試：Authorization Policy — 對應 scenario「具備所需角色時允許存取」「不具備所需角色時拒絕存取」「未攜帶或攜帶無效 Access Token」（涵蓋於 6.10/6.11 各端點的 401/403 斷言）
- [x] 6.13 整合測試：`JwtOptions` 缺少 `SigningKey` 時應用程式啟動失敗（`WebApplicationFactory` 覆寫空白設定，驗證拋出 `OptionsValidationException`，對應 design.md 決策 9）

## 7. 設定與機敏資訊

- [x] 7.1 於 `appsettings.Development.json`（已確認 gitignore 排除）新增 `Jwt`/`Auth` 設定區塊（含開發用 SigningKey）
- [x] 7.2 於 `appsettings.json` 保留 `Jwt`/`Auth` 設定結構，`SigningKey` 留空（不含實際金鑰值），正式環境須改用 Secret Manager / Azure Key Vault 或環境變數注入（尚未另外補 README，見完成後通知）

## 8. Spec 同步確認

- [x] 8.1 實作完成後比對 `specs/member-management/spec.md`、`specs/authentication/spec.md` 與實際行為是否一致 — 兩份 spec 的所有 Requirement/Scenario 均已對應實作與測試，無需更新 spec 文字；發現的實作範圍外缺口（無角色指派/管理員建立機制）已記錄於完成通知，非本次 spec 涵蓋範圍
