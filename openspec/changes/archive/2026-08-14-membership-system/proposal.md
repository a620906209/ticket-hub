## Why

目前 ProjectC 僅有 Clean Architecture 骨架，尚無任何使用者帳號、登入或權限機制。後續所有需要「識別使用者身份」或「依角色限制存取」的功能都依賴此能力，因此須先建立會員系統與認證機制作為基礎建設。

## What Changes

- 新增使用者註冊、會員資料查詢與更新功能
- 新增登入 / 登出機制，採 JWT Bearer Token 作為 API 認證方式
- 新增密碼管理：註冊時雜湊儲存（BCrypt/Argon2）、忘記密碼／重設密碼流程
- 新增角色權限機制（RBAC）：使用者可被指派角色，Controller/Endpoint 透過 Authorization Policy 限制存取
- 新增帳號狀態管理：啟用 / 停用會員帳號
- WebApi 層新增全域 JWT Authentication middleware 與對應 Authorization Policy 設定

## Capabilities

### New Capabilities
- `member-management`: 會員帳號生命週期管理，包含註冊、會員資料查詢/更新、帳號啟用停用
- `authentication`: 登入、登出、JWT 簽發與驗證、密碼雜湊與重設、角色權限（RBAC）驗證

### Modified Capabilities
（無，此為全新能力，尚無既有 spec 需要修改）

## Impact

- **Domain**：新增 `Member`、`RefreshToken`、`PasswordResetToken` Entity（狀態一律 `private set`，透過方法變更）；不建立 Repository 介面（詳見 design.md 決策）
- **Application**：新增 `RegisterMemberHandler`、`LoginHandler`、`RefreshTokenHandler`、`LogoutHandler`、`RequestPasswordResetHandler`、`ResetPasswordHandler` 等 use case；定義 `IApplicationDbContext`、`IPasswordHasher`、`ITokenService`、`IDateTimeProvider` 介面（詳見 design.md 決策，此三個服務介面放在 Application 而非 Domain）
- **Infrastructure**：`AppDbContext`（實作 `IApplicationDbContext`）、`BCryptPasswordHasher`、`JwtTokenService`、`SystemDateTimeProvider` 實作，EF Core Migration
- **WebApi**：新增 `AuthController`、`MembersController`（會員自助）、`AdminMembersController`（管理員操作），DI 註冊 JWT Authentication、Authorization Policy、FluentValidation、`JwtOptions` 啟動時驗證（fail-fast）
- **資料庫**：新增 `Members`、`RefreshTokens`、`PasswordResetTokens` 資料表（PostgreSQL，透過 EF Core Migration）
- **設定**：新增 JWT 簽章金鑰等機敏設定至 `appsettings.Development.json` / 環境變數（不進版本控制）
