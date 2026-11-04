# Design

## Context

動機見 proposal.md「Why」。現況（已讀 `Program.cs` 全文確認）：

- 服務註冊約 480 行，另有 5 次 `app.Services.GetRequiredService<T>()` 強制啟動期解析（RateLimiting／LoginRateLimiting／Captcha／CaptchaRateLimiting／SeatMapRateLimiting），以及 Development 專屬的 seeder 執行區塊（Program.cs 512–516）與 OpenAPI。
- 兩處註冊在 factory delegate 內才讀 `builder.Configuration`（`IConnectionMultiplexer`、`AddJwtBearer`），原因是 `WebApplicationFactory` 的設定覆寫在 `Build()` 當下才併入，提前讀取會讀到舊值（註解已記錄 PQLE-010 的實測踩雷）。
- Testing 環境不註冊 `ExpiredOrderCleanupService`／`PurchaseQueueAdmissionService`。
- 整合測試以 `WebApplicationFactory<Program>` 啟動，`Program` 類別宣告必須保留。
- `src/ProjectC.WebApi/appsettings.Development.json` 被 `.gitignore:487` 排除、從未進版控，compose 以環境變數覆寫其全部值（連線字串、Jwt、TicketSigning）。

本 change 設 `skip_specs: true`：HTTP 契約、設定鍵、啟動驗證與 DI 圖皆不變，沒有任何 Requirement 會新增或修改；硬寫 Scenario 只會是為了通過驗證而發明需求（proposal.md「Capabilities」）。"行為不變"的驗收範圍由 tasks.md 的 P1–P4 準則定義，各自對應可判定的驗證 task。

## Goals / Non-Goals

**Goals:**
- `Program.cs` 縮減為「啟動流程 + 擴充方法呼叫 + 管線順序」，每個關注點一個可獨立閱讀的檔案。
- 拆分前後 DI 圖（服務型別、實作型別、lifetime）逐項相同，由自動化測試證明，而非靠肉眼審查；證明範圍限快照追蹤的描述元，其餘盲區見 Risks。

**Non-Goals:**
- 不調整任何 lifetime、不合併或刪除看似重複的 Options 註冊（例如 `Configure<T>` 與 `AddOptions<T>().Bind` 兩種寫法並存）、不改 middleware 順序。
- 不把註冊搬進 Application／Infrastructure 專案；不引入組件掃描或自動註冊。
- 不處理 `Program.cs` 以外的 WebApi 結構。

## Decisions

### 決策 1：擴充方法全放 `WebApi/Extensions/`，依關注點切分

`Application` 只 reference `Domain`，要提供 `AddApplication()` 就得替它加 DI Abstractions 套件並改動 `.csproj`／compose volume；`Program.cs` 本來就是 composition root。因此全部留在 WebApi，不改產品專案的相依與結構；唯一的 `.csproj` 變更是測試專案複製 baseline 檔的 `CopyToOutputDirectory` 設定。

替代方案：每層各自一個 `AddXxx`（專案層級拆分）。否決原因：需改 `Application.csproj` 相依，而本 change 目標是零相依變動。

切分（一個 static class 一個檔案，命名 `XxxServiceCollectionExtensions`，方法皆 `IServiceCollection` 擴充；需要設定者接 `IConfiguration`）：

| 方法 | 內容（對應原 Program.cs 區塊） |
| --- | --- |
| `AddPersistence(configuration)` | `ApplicationDbContext`、`IApplicationDbContext`、`IUnitOfWork`、全部 Repository／`IMemberDisplayNameReader` |
| `AddAuthenticationAndAuthorization(configuration)` | `JwtOptions`、`AuthOptions`、`IDateTimeProvider`、`IPasswordHasher`、`ITokenService`、`AddAuthentication().AddJwtBearer`、`RequireOrganizerContextHandler`、`AddAuthorizationBuilder` |
| `AddTicketingServices(configuration)` | `OrderCleanupOptions`、`MockPaymentGatewayOptions`＋`IPaymentGateway`、`MockEmailNotificationServiceOptions`＋`IEmailNotificationService`、`TicketSigningOptions`＋`ITicketSigningService`＋`ITicketQrCodeGenerator`、`PurchaseQueueOptions` |
| `AddRedisServices(configuration)` | `IConnectionMultiplexer`、`DistributedLockOptions`＋`IDistributedLock`、`IPurchaseQueueAdmissionMirror`、`QueryCacheOptions`＋`IQueryCache` |
| `AddCaptcha(configuration)` | `CaptchaOptions`、`ICaptchaImageGenerator`、`ICaptchaService`、`GetCaptchaHandler` |
| `AddRateLimiting(configuration)` | `RateLimitingOptions`、`LoginRateLimitingOptions`、`CaptchaRateLimitingOptions`、`SeatMapRateLimitingOptions`、`AddRateLimiter`（含 5 個 policy、`OnRejected`、5 個靜態分區函式） |
| `AddApplicationHandlers()` | 全部 Handler、`OrderService`、`AddValidatorsFromAssemblyContaining` |
| `AddBackgroundServices(environment)` | 兩個 `AddHostedService`，保留 `!IsEnvironment("Testing")` 判斷 |
| `AddApiHealthChecks()` | `AddHealthChecks().AddCheck<DatabaseReadinessHealthCheck>` |
| `AddDevelopmentSeeding(configuration)` | `DevelopmentSeedOptions`、`DevelopmentDataSeeder` |

另有 `WebApplication` 擴充 `ResolveEagerlyValidatedOptions()`：原樣保留那 5 次 `GetRequiredService<T>()` 與其註解，`Program.cs` 在 `Build()` 之後呼叫一次。

Development 專屬的 seeder 執行區塊（`IsDevelopment()` 內 `await SeedAsync`）與 OpenAPI／SwaggerUI 對應區塊同樣留在 `Program.cs`，不抽出（含 `await`，屬啟動流程）。

`AddControllers`、`AddOpenApi`、`AddExceptionHandler`、`AddProblemDetails`、`ConfigureKestrel`、`UseSerilog` 與整段 pipeline 仍留在 `Program.cs`（合計不到 40 行，抽出只會增加跳轉）。

切分判準是「一起變動的註冊放一起」，不是依 Clean Architecture 層；方法名不使用 `Manager`／`Helper`／`Utils`。

### 決策 2：傳入 `builder.Configuration`，不傳快照

`AddRedisServices`、`AddAuthenticationAndAuthorization` 必須接收 `builder.Configuration`（`ConfigurationManager` 同一實例）並維持「在 factory／options delegate 內才讀取」的寫法，否則會重現 PQLE-010 記錄的「讀到 `Build()` 前的設定快照」問題。搬移時此行為由註解標明（原註解隨程式碼保留），並由 `ApplicationStartupWithRedisUnavailableTests`、`RedisConnectionConfigurationTests`、`JwtOptionsFailFastTests` 等既有 Startup 測試守住。

### 決策 3：以 `ServiceDescriptor` 快照測試證明 DI 圖不變

新增 `tests/ProjectC.WebApi.Tests/Startup/ServiceRegistrationSnapshotTests`：以 `WebApplicationFactory<Program>`（Testing 環境，沿用 `JwtOptionsFailFastTests` 的最小有效設定）在 `ConfigureServices` 中取得最終的 `IServiceCollection`，將每個描述元序列化為 `Lifetime | ServiceType | ImplementationType 或 "factory" 或 "instance"`，只保留型別字串含 `ProjectC` 或 `StackExchange.Redis` 的項目（後者是為了涵蓋 `IConnectionMultiplexer` 的 lifetime，審查中發現漏掉而補入）（涵蓋專案型別與以專案 Options 為泛型引數的 `IConfigureOptions<>`／`IValidateOptions<>` 等），排序後與提交進版控的基準檔逐行比對。

- 以排序後的多重集合比對，不比對註冊順序：拆分必然改變項目間順序，而同一服務型別的多筆註冊只有 `IHostedService`、`IAuthorizationHandler`、`IValidator<>` 這類集合型註冊對順序敏感；本專案的集合型註冊彼此之間無順序相依（兩個 hosted service 獨立，且 Testing 環境不註冊）。
- 基準檔必須由拆分前（HEAD）的 `Program.cs` 產生，拆分後測試必須通過且基準檔內容不得為了讓測試通過而修改。實作時基準檔是在拆分後，暫時還原 HEAD 版 `Program.cs` 重新產生（因為快照篩選範圍加入 `StackExchange.Redis`），尚未 commit；基準檔內容仍完全來自拆分前的程式碼。
- 這是特徵化測試（locks in current behavior），不是新行為測試：它在拆分前就會通過，因此 TDD 的 RED 為 N/A。其「會失敗」的證據改採突變驗證：暫時把某個 lifetime 改掉，確認測試失敗（見 tasks.md）。
- 描述元只抓得到 Testing 環境下的註冊，Testing 排除的兩個 hosted service 不在其中，見 Risks。

替代方案：以 `ServiceProvider` 的 `ValidateOnBuild`／`ValidateScopes` 驗證。否決原因：它只證明「可解析」，無法證明 lifetime 與實作型別不變。

### 決策 4：更正文件，不動 `appsettings.Development.json`

該檔案本機存在但被排除，修改它不會出現在 diff、也不影響他人。只更正三處錯誤敘述：`docs/k8s-todo.md` 第 41 行、`CLAUDE.md`「強制規則」第一條括號說明（改為說明它是本機專用、被 `.gitignore` 排除的檔案，連線字串為 `localhost`，容器內由環境變數覆寫）、記憶 `project_webapi_todo`。CLAUDE.md 只改該句，不順手調整其他段落。

## Risks / Trade-offs

- [快照盲區：只保留型別字串含 `ProjectC` 或 `StackExchange.Redis` 的描述元，看不到 Options 的 section 名稱（`Configure<T>(GetSection)` 只顯示 instance）、factory／Validate delegate 的內容、`DbContextOptions`、`ValidateOnStart` 的 `IConfigureOptions<StartupValidatorOptions>`（由 `*FailFastTests` 守護）、`AddRateLimiter` 的 policy／`OnRejected`、`AddJwtBearer` 的選項、`AddAuthorizationBuilder` 的 policy、`HealthCheckServiceOptions`] → 這些搬移為逐行剪貼，由 `git diff -M` 審查加既有整合測試守護（限流行為 `RateLimitedWebApplicationFactory` 系列、JWT 驗證 `AuthTestHelper` 相關測試、授權 policy 的 403／401 測試、`HealthChecks/` 測試）。
- [5 個 eager 解析的 Options 若在搬移時漏掉 `GetRequiredService<T>()`，啟動期 fail-fast 會悄悄消失] → 5 個 Options 都已有既有測試守護（`RateLimitingOptionsFailFastTests` 內 RateLimiting／Login／Captcha／CaptchaRateLimiting／SeatMap 各一個測試）；tasks 1.4 以突變驗證證明這些測試對「漏掉解析」會失敗，不新增測試。
- [快照測試看不到 Testing 環境以外的行為：兩個 hosted service 的非 Testing 註冊] → tasks 2.6 以 `BackgroundServiceRegistrationTests` 直接測 `AddBackgroundServices(environment)`（Production／Development 註冊兩個、Testing 零個），並以突變驗證證明有效。
- [Development 專屬的 seeder 執行區塊與 OpenAPI 留在 Program.cs、不被搬動] → 風險僅限於範圍外的誤改；tasks 2.6 以 `git diff` 確認這兩個區塊未動，4.2 煙霧驗證 seeder 效果與 `/openapi/v1.json`。
- [搬移時遺漏一筆註冊] → 快照多重集合比對會因缺項失敗；且既有全套整合測試會在 DI 解析處失敗。
- [設定讀取時機改變（決策 2）] → 見決策 2，由既有 Startup 測試守住。
- [快照基準檔在日後新增服務時需更新，增加維護成本] → 基準檔差異本身就是「DI 圖變了」的明確訊號，審查時可直接看到；測試失敗訊息列出新增／缺少的項目以利更新。
- [快照以字串比對，型別重新命名會讓基準檔失效] → 屬刻意為之，重新命名時同步更新基準檔即可。
