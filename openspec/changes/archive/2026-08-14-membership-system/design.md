## Context

ProjectC 目前僅有 Clean Architecture 專案骨架（Domain / Application / Infrastructure / WebApi），尚無任何使用者帳號、認證或授權機制。本設計為後續所有功能的身份識別基礎，需一次決定資料模型、認證機制與權限模型，避免後續功能各自為政。

限制條件（來自 CLAUDE.md）：
- 依賴方向 WebApi → Infrastructure → Application → Domain，Domain 不得依賴其他層
- Repository / 外部服務介面一律定義在 Domain，實作放 Infrastructure（本設計對此有一項明確偏離，見決策 6 說明理由）
- Domain Entity 內部狀態 `private set`，禁止 Anemic Domain Model
- 禁止 raw ADO.NET query；一律走 EF Core 參數化查詢
- 機敏資訊（JWT 簽章金鑰等）不得寫死在程式碼中

## Goals / Non-Goals

**Goals:**
- 提供帳密註冊 / 登入 / 登出
- 以 JWT Bearer Token 作為 WebApi 的無狀態認證機制
- 支援登出（因 JWT 本質無狀態，需搭配 Refresh Token 機制才能真正撤銷）
- 提供密碼雜湊儲存與密碼重設機制（產生/驗證重設 Token、更新密碼）；**實際寄送 Email 的整合不在本次範圍內**
- 提供基本角色權限（RBAC），Controller/Endpoint 可用 Authorization Policy 限制存取
- 會員資料查詢／更新、帳號啟用停用，且停用後立即生效（登入、Refresh、既有 Token 皆失效）

**Non-Goals:**
- 第三方社群登入（OAuth2 / Google / Facebook 等）
- 密碼重設 Email 實際寄送（本次僅實作重設 Token 的產生/驗證/消費機制）
- Email 註冊驗證流程（註冊後寄送驗證信）
- 多因素驗證（MFA / OTP）
- 多租戶（Multi-tenant）隔離
- 細粒度 Permission-based 授權（本次僅支援角色層級的 RBAC，非權限點層級）

## Decisions

### 1. 認證機制：JWT Bearer（Access Token + Refresh Token），不採用 ASP.NET Core Identity
- **選擇**：自行定義 `Member` Domain Entity + 輕量 Token 發放/驗證服務，不使用 `ASP.NET Core Identity` 完整框架。
- **理由**：ASP.NET Core Identity 綁定自己的 `IdentityUser`/資料表結構與 Anemic 風格的 UserManager API，與專案規範要求的 Rich Domain Model（狀態變更透過方法、非公開 setter）衝突，且目前需求（註冊/登入/角色）不需要 Identity 提供的完整功能集（Email confirmation、Lockout、External login provider 等）。
- **替代方案**：ASP.NET Core Identity — 開發速度快但耦合度高、可客製性低，不符合本專案 Clean Architecture 規範，故不採用。

### 2. Access Token + Refresh Token 雙 Token 機制
- **選擇**：Access Token（JWT，短效期，預設 30 分鐘）+ Refresh Token（隨機字串，雜湊後存 DB，長效期，預設 14 天，Rotation）。效期已與需求方確認，暫定為最終值，後續如需調整於 `JwtOptions` 修改即可。
- **理由**：純 JWT 無法在到期前撤銷（無狀態），若僅用長效 JWT，登出功能形同虛設。Refresh Token 存資料庫可支援登出時撤銷、偵測重複使用（token reuse detection）。
- **替代方案**：
  - 純 Session（Cookie + Server-side session）— 與 API-first / SPA 前端（Vue 3）分離架構不搭，且需額外處理跨網域 Cookie。
  - 純長效 JWT — 實作簡單但無法登出/撤銷，安全風險高，故不採用。

### 3. 密碼雜湊：BCrypt（`BCrypt.Net-Next`），透過 `IPasswordHasher` 介面隔離
- **選擇**：`Application` 定義 `IPasswordHasher` 介面（`HashPassword` / `VerifyPassword`），`Infrastructure` 用 BCrypt.Net-Next 實作。
- **理由**：BCrypt 是業界成熟、抗暴力破解且無需額外管理 salt 的雜湊演算法；透過介面隔離符合依賴反轉原則，未來若要換 Argon2 只需替換 Infrastructure 實作。介面放置位置說明見決策 6。

### 4. 角色權限模型：Member 直接持有單一 `Role`（enum），映射為 JWT Claim + Authorization Policy
- **選擇**：`Member` Entity 有 `Role` 屬性（enum：`Member`, `Admin`），登入時寫入 JWT Claim（`role`），WebApi 用 `[Authorize(Policy = "...")]` 或 `[Authorize(Roles = "Admin")]` 限制存取。
- **理由**：符合 CLAUDE.md「Entity 數量少時可簡化」原則，目前僅需區分一般會員與管理員，不需要多對多角色/權限資料表；若未來出現「一個使用者多個角色」或「權限點需要動態調整」的需求，再導入 `Role`/`Permission` 正規化資料表。
- **替代方案**：多對多 Role/Permission 資料表 — 彈性高但目前為過度設計，不採用。

### 5. Application 透過 `IApplicationDbContext` 抽象存取資料庫，不直接依賴 Infrastructure
- **選擇**：`Application` 定義 `IApplicationDbContext` 介面（暴露 `DbSet<Member> Members`、`DbSet<RefreshToken> RefreshTokens`、`DbSet<PasswordResetToken> PasswordResetTokens`、`SaveChangesAsync`），Use Case Handler 注入此介面查詢/寫入；`Infrastructure` 的 `AppDbContext : DbContext, IApplicationDbContext` 提供實作，WebApi 於 DI 註冊時把具體 `AppDbContext` 同時綁定為 `IApplicationDbContext`。
- **理由**：原草案「Application 直接注入 DbContext」若注入的是 Infrastructure 專案內的具體類別，會違反「Application 只能 reference Domain」的依賴方向規則。改用 Application 自己定義的介面，維持依賴反轉，同時仍符合 CLAUDE.md「Entity 數量少（< 5 個）時可直接查詢，不必每個 Entity 都建 Repository」的簡化條款——差別只在於用介面包裝查詢對象，而非省略介面。
- **替代方案**：每個 Entity 建 `IMemberRepository`、`IRefreshTokenRepository` — 本次 Entity 數量少（3 個），暫不需要，待重複查詢邏輯出現時再抽出。

### 6. `IPasswordHasher`、`ITokenService`、`IDateTimeProvider` 定義於 Application，而非 Domain
- **選擇**：這三個技術性服務介面改放在 `Application` 層，由 `Infrastructure` 實作並經 DI 注入。
- **理由**：CLAUDE.md 的預設強制規則是「Repository / 外部服務介面一律定義在 Domain」，本決策明確偏離此規則，原因如下——在本設計中，`Member`、`RefreshToken` 等 Domain Entity **完全不會呼叫**這三個介面：密碼雜湊在 Application 的 `RegisterMemberHandler`/`ResetPasswordHandler` 內完成後才把雜湊值傳入 `Member.ChangePasswordHash(hash)`；JWT 簽發只在 Application 的 `LoginHandler`/`RefreshTokenHandler` 發生；時間比較（Token 是否過期）也是 Application 在比對 `ExpiresAt` 時才需要「現在時間」，Domain Entity 本身不持有或使用時間。因此這三者本質是 Application 層的技術 Port（供 Use Case 編排使用），不是 Domain 需要依賴的抽象；CLAUDE.md 原規則的精神主要對應「Repository 代表 Domain Aggregate 的持久化」（例如若未來新增 `IMemberRepository`，仍應留在 Domain）。
- **PR Review 提醒**：這是對專案預設規則的刻意偏離，非疏漏，請 review 時參照本節說明，不要視為違規要求搬回 Domain。

### 7. Refresh Token 狀態機、Rotation、Reuse Detection 與並發刷新策略
- **狀態機**：`RefreshToken` 儲存三種明確狀態：`Active`（可用）、`Used`（已被換發消費，Rotation 後的舊 Token）、`Revoked`（被登出/停用帳號/偵測到重放而主動撤銷）。**`Expired` 不作為持久化狀態**，而是由 Application 在驗證當下用 `ExpiresAt < IDateTimeProvider.UtcNow()` 動態判斷——避免額外背景工作去同步「已過期」狀態，也避免狀態與實際時間不一致的風險。有效性判斷 = `Status == Active AND ExpiresAt > now`。
- **Rotation**：每次成功換發，舊 Token 標記為 `Used`（非刪除，保留稽核軌跡），同時核發新的 `Active` Refresh Token；新 Token 記錄「由哪個舊 Token 換發而來」（`ReplacedByTokenId` 反向指標或 `PreviousTokenId`），供 Reuse Detection 串起整條 Token 鏈。
- **Reuse Detection**：若驗證時發現送入的 Token 狀態為 `Used` 或 `Revoked`（代表已被消費過或已撤銷卻仍被使用），視為疑似遭竊，立即將該會員名下**所有** Refresh Token 標記為 `Revoked`，並回傳 401 要求重新登入。
- **並發刷新（同一 Token 被兩個請求同時送出）**：`RefreshToken` 資料表加入樂觀並發控制欄位（EF Core `RowVersion` / concurrency token）。換發流程為「讀取 Active Token → 標記為 Used → SaveChanges」，若兩個請求並發執行，其中一個會在 `SaveChangesAsync` 時因並發衝突（`DbUpdateConcurrencyException`）失敗；失敗的一方視同「Token 已被使用」，回傳 401（不觸發 Reuse Detection 的全面撤銷，因為這是合法的並發競爭，非攻擊行為），僅有先完成的一方換發成功。

### 8. 停用帳號後即時生效：登入、Refresh、既有 Token 全部失效
- **登入**：`LoginHandler` 檢查 `Member.IsActive`，為 `false` 時拒絕（既有 spec 已涵蓋）。
- **停用當下**：`DeactivateMemberHandler` 於將 `Member.IsActive` 設為 `false` 的同一交易內，撤銷（`Revoked`）該會員名下所有 `Active`/`Used` 狀態的 Refresh Token，確保停用後這些 Token 立即無法再換發。
- **Refresh 時**：`RefreshTokenHandler` 除了驗證 Token 本身狀態，**也必須重新查詢對應 `Member.IsActive`**；若帳號已停用，即使 Token 狀態仍是 `Active`（理論上不會發生，因為停用已同步撤銷，但仍作為防禦性檢查），一律拒絕並回傳 401。
- **高風險端點（如管理操作、更新個人資料）**：不能只信任 JWT 內的 Claim（Access Token 在效期內即使帳號被停用仍可能尚未過期），這類端點的 Handler 需重新查詢 `Member.IsActive` 而非只靠 `[Authorize]` 通過就視為合法；一般唯讀、低風險端點可接受依賴 Claim（效期已限制在 30 分鐘內，風險可接受）。

### 9. JWT Claims 與 `JwtOptions` 明確定義，Secret 缺失時 Fail Fast
- **Claims**（Access Token 內容）：
  - `sub`：Member Id（GUID）
  - `email`：登入 Email
  - `role`：`Member` 或 `Admin`
  - `jti`：Token 唯一 ID（供未來稽核/黑名單擴充）
  - `iat` / `exp`：簽發/到期時間（JWT 標準 Claim）
- **`JwtOptions`**（綁定自設定檔，`Options Pattern`）：
  ```
  Issuer: string
  Audience: string
  SigningKey: string   // 由 appsettings.Development.json 或環境變數提供，不進版控
  AccessTokenExpirationMinutes: int   // 預設 30
  RefreshTokenExpirationDays: int     // 預設 14
  ```
- **Fail Fast**：於 `Program.cs` 啟動階段對 `JwtOptions` 做驗證（`ValidateDataAnnotations()` + `ValidateOnStart()`，或啟動時明確檢查 `SigningKey` 非空且長度足夠），若設定缺失或不合法，應用程式**啟動時直接拋出例外中止**，而非等到第一次登入請求才失敗。

### 10. API Endpoint 草案
| 分類 | Method | Path | 授權 | 說明 |
| --- | --- | --- | --- | --- |
| Auth | POST | `/api/auth/register` | 公開 | 註冊 |
| Auth | POST | `/api/auth/login` | 公開 | 登入，回傳 Access + Refresh Token |
| Auth | POST | `/api/auth/refresh` | 公開（帶 Refresh Token） | 換發 Access Token（Rotation） |
| Auth | POST | `/api/auth/logout` | 需登入 | 撤銷目前 Refresh Token |
| Auth | POST | `/api/auth/password-reset/request` | 公開 | 產生密碼重設 Token（不寄信，本次範圍內僅產生/記錄） |
| Auth | POST | `/api/auth/password-reset/confirm` | 公開（帶重設 Token） | 用重設 Token 設定新密碼 |
| Member 自助 | GET | `/api/members/me` | 需登入 | 查詢自己的會員資料 |
| Member 自助 | PUT | `/api/members/me` | 需登入 | 更新自己的顯示名稱 |
| Admin 管理 | POST | `/api/admin/members/{id}/activate` | 需 Admin | 啟用指定會員帳號 |
| Admin 管理 | POST | `/api/admin/members/{id}/deactivate` | 需 Admin | 停用指定會員帳號（同步撤銷其 Refresh Token） |

Admin 相關端點獨立於 `AdminMembersController`（路由前綴 `/api/admin/members`），與會員自助的 `MembersController`（路由前綴 `/api/members`）分開，讓 Authorization Policy 與路由層級一目了然，避免同一 Controller 內混雜不同權限層級的動作。

## Risks / Trade-offs

- **[風險] JWT 簽章金鑰外洩** → **緩解**：金鑰放 `appsettings.Development.json`（不進版控）或環境變數；正式環境改用 Secret Manager / Azure Key Vault；啟動時 Fail Fast 確保金鑰一定存在（決策 9）。
- **[風險] Refresh Token 被竊取重放** → **緩解**：DB 僅存 Refresh Token 的雜湊值（非明文）；登入時輪替（Rotation），偵測到已使用過的舊 Token 立即撤銷該使用者所有 Token（決策 7）。
- **[風險] 並發刷新導致 Race Condition（雙重換發）** → **緩解**：以 EF Core 樂觀並發控制確保同一 Token 只能被成功消費一次（決策 7）。
- **[風險] 帳號停用後既有 Access Token 短暫仍可用** → **緩解**：Access Token 效期壓在 30 分鐘內，高風險端點另外即時檢查 `IsActive`（決策 8），可接受的風險視窗已最小化。
- **[風險] 密碼重設 Token 被猜測或重複使用** → **緩解**：使用具時效性（15 分鐘）且僅能使用一次的隨機 Token，存 DB 雜湊值並於使用後立即失效，重設成功後同步撤銷該會員所有既有 Refresh Token。
- **[Trade-off] `IPasswordHasher`/`ITokenService`/`IDateTimeProvider` 放 Application 而非 Domain** → 偏離 CLAUDE.md 預設規則，換取更準確反映「Domain 完全不依賴這些技術細節」的分層（決策 6 已附理由，PR review 時可對照）。
- **[Trade-off] 不使用 ASP.NET Core Identity** → 換取更符合 Clean Architecture / Rich Domain Model 的設計，但需自行實作密碼雜湊、Token 產生與驗證等原本 Identity 提供的功能，開發成本略增。

## Migration Plan

1. 新增 EF Core Migration：建立 `Members`、`RefreshTokens`、`PasswordResetTokens` 資料表（PostgreSQL），`RefreshTokens` 含 `RowVersion` 並發控制欄位
2. WebApi 啟動時註冊 JWT Authentication Middleware、`JwtOptions` 驗證（Fail Fast）與 Authorization Policy（不影響現有功能，因目前無其他 Controller）
3. 無既有資料需要轉移（全新功能，非既有欄位變更）
4. Rollback：若需回退，`dotnet ef database update <previous-migration>` 移除新增資料表即可，不影響其他模組

## Open Questions

（無，先前的未定事項已由需求方確認：Token 效期維持預設值；忘記密碼功能保留底層機制、不含 Email 寄送）
