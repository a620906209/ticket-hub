# Design

## Context

- 現況：`DevelopmentDataSeeder`（Infrastructure/Persistence/Seeding）只在 `app.Environment.IsDevelopment()` 時由 `Program.cs` 呼叫，建立 Admin 與 Approved Demo Organizer；Domain 沒有公開的「指派角色」流程，seeder 以 `Entry(admin).Property(m => m.Role).CurrentValue = MemberRole.Admin` 直接寫入。
- `Members.Email` 有唯一索引（`MemberConfiguration`）。註冊密碼規則在 `PasswordValidationRules.MustBeStrongPassword`（≥8 碼、含英文字母與數字），Email 規則為 FluentValidation `EmailAddress()`。
- 啟動期設定驗證的既有模式：`IValidateOptions<T>`（例：`CaptchaOptionsValidator`，放 WebApi）＋ `ResolveEagerlyValidatedOptions` 在啟動時強制解析。
- 新增服務註冊須同步更新 `tests/ProjectC.WebApi.Tests/Startup/ServiceRegistrationSnapshot.baseline.txt`。
- 動機見 proposal.md。

## Goals / Non-Goals

**Goals:**
- Production 可靠地建立首位 Admin，且不借用 Development 環境。
- 設定錯誤在啟動時就明確失敗，而非靜默略過。

**Non-Goals:**
- 不改 `DevelopmentDataSeeder`、不建立 Organizer、不提供後續 Admin 的建立／升級流程、不提供 Admin 密碼輪替。
- 不處理雲端部署。

## Decisions

1. **獨立類別 `ProductionAdminBootstrapper`（Infrastructure/Persistence/Seeding）＋ `AdminBootstrapOptions`（同資料夾，`SectionName = "AdminBootstrap"`）**，Scoped 註冊（持有 `ApplicationDbContext`）。替代案：擴充 `DevelopmentDataSeeder`——否決，語意與環境限制不同，且共識明確要求不動它。Admin 角色寫入沿用 seeder 的 EF `Property(...).CurrentValue` 做法（Domain 無指派角色流程，本 change 不新增；此一行重複列為已知技術債）。
2. **執行條件：`!IsDevelopment()`**，由 `Program.cs` 在 seeder 區塊旁呼叫。未設定 `AdminBootstrap__*` 時 bootstrapper 自行略過，故未設定的環境（如 Testing）不受影響；有設定時，非 Development 環境一律執行。理由：Development 已有 seeder，避免兩套機制在同一環境重疊。
3. **「無 Admin」判斷**：`AnyAsync(m => m.Role == MemberRole.Admin)`，不論 `IsActive`。已有 Admin 一律略過，不比對 Email、不覆寫。
3a. **Email 原樣使用**：不 Trim、不轉小寫（與既有註冊／登入一致，無正規化）；含前後空白的值不通過 `EmailAddress()` 驗證而啟動失敗。
4. **並發**：不加分散式鎖（Redis 鎖會增加啟動相依）。先檢查再 `SaveChanges`；捕捉 `DbUpdateException`（內層 `PostgresException.SqlState == "23505"`、約束為 Email 唯一索引）後，以全新查詢重新確認：若已存在 Admin → 視為成功；若仍無 Admin（即相同 Email 為非 Admin 會員）→ 拋出明確例外使啟動失敗（對應 ADMIN-BOOT-003）。其他 `DbUpdateException` 一律重拋，不吞例外。
5. **設定驗證**：`AdminBootstrapOptionsValidator : IValidateOptions<AdminBootstrapOptions>` 放 WebApi，內部用 FluentValidation（`EmailAddress()`、`MustBeStrongPassword()`）以重用註冊規則；兩項皆空白 → 成功（略過）；只有其一 → 失敗。失敗訊息只含設定項名稱與規則描述，不含值。於 `ResolveEagerlyValidatedOptions` 解析一次，使其在啟動時觸發。`DisplayName` 為選填設定，預設 `Administrator`。
6. **Migration 檢查**：沿用 seeder 做法，`GetPendingMigrationsAsync` 有未套用者 → Warning 並略過。順序為：設定驗證（啟動即失敗）→ 未設定略過 → migration 檢查 → 建立。
7. **日誌**：僅記錄 `MemberId`、設定項名稱、未套用 migration 數量；日誌與例外訊息一律不含 Email、密碼與雜湊。
8. **K8s**：`ASPNETCORE_ENVIRONMENT` 改 `Production`（同時關閉 OpenAPI／SwaggerUI）；ConfigMap 放 `AdminBootstrap__Email`，Secret 放 `AdminBootstrap__Password`；移除 `DevelopmentSeed__*`。`verify-k8s-manifests.sh` 的 `SECRET_KEYS` 與產生的 secret 同步改名。因不再有 Demo Organizer，先前 K8s 3.3 的瀏覽器流程需改走會員申請主辦方 + Admin 核准，於文件註明、不在本 change 重跑。

## 跨模組慣例對照

不適用：本 change 不新增或修改 API 端點、錯誤回應、認證／授權規則、DTO 欄位或列表端點；啟動期失敗屬於行程啟動錯誤，不經 `ProblemDetails`。

## 安全確認（CLAUDE.md 安全強制規則）

- 輸入驗證：設定值於 WebApi 的 `AdminBootstrapOptionsValidator` 驗證；無 SQL／shell 拼接。
- 資料庫：EF Core 參數化查詢；單次 `AnyAsync` + 單筆新增，無 N+1。
- 權限：非外部請求觸發，僅由部署設定（Secret）控制；Admin 建立後走既有登入與授權。無法被未授權使用者觸發。
- 機敏資訊：密碼由 Secret 注入，不進版控；不入日誌；僅以既有 `IPasswordHasher` 雜湊後儲存。
- 前端：不適用。

## Risks / Trade-offs

- [Secret 中的密碼在建立後仍留在部署設定] → 文件註明建立首位 Admin 後可移除 `AdminBootstrap__Password`（之後因已有 Admin 一律略過）。
- [Admin 全數被刪除或停用以外的情況：若所有 Admin 被刪除，下次啟動會依設定重建] → 屬預期的救援路徑，文件註明。
- [兩個實例設定了不同 Email] → 不支援；最多建立一位（先成功者），後者見已有 Admin 而略過。
- [Role 寫入方式與 seeder 重複] → 已知技術債，待 Domain 有角色指派流程時一併收斂。
- [K8s 改 Production 後 `CaptchaOptionsValidator` 禁止 AcceptAny 開始生效] → ConfigMap 未設定 `Captcha__*`（預設 Enforced），不受影響；Production 無法關閉驗證碼，故 4.3 不以腳本登入。
- [`Production` 環境可能啟用其他僅 Production 的行為（如 HTTPS 導向）] → tasks 以 `verify-k8s-manifests.sh` 重跑確認。
