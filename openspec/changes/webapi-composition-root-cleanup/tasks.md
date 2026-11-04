# Tasks

> 所有指令都在容器內執行（`docker compose exec api dotnet test ...`），不在本機執行 dotnet。
>
> 本 change 為純重構，無新增 Acceptance Criteria（`skip_specs: true`，見 proposal.md）。以下「行為不變準則」取代 AC，每一條都對應驗證 task：
>
> | 行為不變準則 | 驗證 |
> | --- | --- |
> | P1：DI 圖（服務型別、實作型別、lifetime）拆分前後相同 | 1.1 快照測試（拆分前通過、拆分後仍通過且基準檔無 diff）、1.2 突變驗證 |
> | P2：啟動期設定讀取時機不變、Options fail-fast 行為不變 | 1.4 既有測試的突變驗證、2.x 之後重跑 `Startup` 測試 |
> | P3：HTTP 契約與 middleware 順序不變 | 2.x 之後重跑全套 WebApi 整合測試 |
> | P4：非 Testing 環境註冊兩個背景服務、Testing 一個都不註冊；Development 才執行 seeder／OpenAPI（這兩區塊留在 Program.cs 不搬動） | 2.6 自動化測試（背景服務）、4.2 煙霧驗證（seeder／OpenAPI） |
>
> 證據一律貼原文（CLAUDE.md「證據貼原文」）：每個失敗測試的名稱與訊息、每個測試專案的摘要行、`git stash` 指令輸出；不得以數字、「全數通過」或「與 X 相同」代替。
>
> TDD：純重構、無新增可觀察行為，RED 標示 N/A（特徵化測試在拆分前就通過是預期）；「測試在行為被破壞時會失敗」改由 1.2 突變驗證證明。

## 1. 特徵化測試（先於任何 Program.cs 修改）

- [x] 1.1 新增 `tests/ProjectC.WebApi.Tests/Startup/ServiceRegistrationSnapshotTests.cs` 與基準檔（`ServiceRegistrationSnapshot.baseline.txt`，設為 `CopyToOutputDirectory`），做法見 design 決策 3；在**未修改 `Program.cs`** 的狀態下以環境變數／參數產生基準檔、提交前人工檢視內容合理（含 `Singleton`／`Scoped`／`Transient` 各類、`factory` 項目、專案 Options 的 `IConfigureOptions<>`）。驗證：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ServiceRegistrationSnapshotTests"` 通過；連跑 2 次基準內容一致（確認排序穩定、不含 Guid／位址等非決定性字串）。
  - 完成紀錄：基準檔於未修改 Program.cs 時以 `UPDATE_SERVICE_REGISTRATION_SNAPSHOT=1` 產生，159 行，內容經人工檢視（含 Scoped／Singleton／Transient、factory、`IConfigureOptions<>` 項目，無 Guid／位址）。
    `docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ServiceRegistrationSnapshotTests"` 連跑 2 次：
    `Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 291 ms - ProjectC.WebApi.Tests.dll (net10.0)`
    `Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 330 ms - ProjectC.WebApi.Tests.dll (net10.0)`
    再以 UPDATE 模式重產一次，與前次產出 `diff` 無差異。
    第 4 輪 Devin 審查指出快照篩選（描述含 `ProjectC`）漏掉 `StackExchange.Redis.IConnectionMultiplexer` 的 lifetime。修正：篩選加入 `StackExchange.Redis`，並讓更新模式寫完基準檔後直接結束（不再拿舊的 output 副本比對）。基準檔重新產生的方式：暫時把 Program.cs 還原為 HEAD 版（`git show HEAD:src/ProjectC.WebApi/Program.cs`）執行 `docker compose exec -T -e UPDATE_SERVICE_REGISTRATION_SNAPSHOT=1 api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~ServiceRegistrationSnapshotTests` → `Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 284 ms`，再還原拆分後的 Program.cs。新舊基準檔 `diff` 僅多一行：`Singleton | StackExchange.Redis.IConnectionMultiplexer | factory`（159 → 160 行），證明舊版 Program.cs 的 DI 圖含該項。拆分後不帶更新模式重跑：`Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 290 ms - ProjectC.WebApi.Tests.dll (net10.0)`。
    突變（`mutate`，`RedisServiceCollectionExtensions.cs`：`services.AddSingleton<IConnectionMultiplexer>(_ =>` → `services.AddTransient<IConnectionMultiplexer>(_ =>`）：`Failed ProjectC.WebApi.Tests.Startup.ServiceRegistrationSnapshotTests.ServiceRegistrations_MatchCommittedBaseline [320 ms]`／`Expected (missing.Count + unexpected.Count) to be 0 because DI 圖與基準檔不一致。缺少的項目：Singleton | StackExchange.Redis.IConnectionMultiplexer | factory；多出的項目：Transient | StackExchange.Redis.IConnectionMultiplexer | factory, but found 2 (difference of 2).`／`Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 328 ms - ProjectC.WebApi.Tests.dll (net10.0)`。還原後 `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~Startup"` → `Passed!  - Failed:     0, Passed:    49, Skipped:     0, Total:    49, Duration: 54 s - ProjectC.WebApi.Tests.dll (net10.0)`。
  - TDD：N/A（特徵化測試，理由見上方）。
  - 交叉審查後補（更新模式防呆）：`UPDATE_SERVICE_REGISTRATION_SNAPSHOT=1` 時測試寫完基準檔後改為 `Assert.Fail`，避免誤設於 CI 而永遠通過。驗證：`docker compose exec -T -e UPDATE_SERVICE_REGISTRATION_SNAPSHOT=1 api dotnet test tests/ProjectC.WebApi.Tests --no-build --filter "FullyQualifiedName~ServiceRegistrations_MatchCommittedBaseline"` → `Failed ProjectC.WebApi.Tests.Startup.ServiceRegistrationSnapshotTests.ServiceRegistrations_MatchCommittedBaseline [315 ms]`、訊息 `已改寫基準檔（UPDATE_SERVICE_REGISTRATION_SNAPSHOT=1）。取消該環境變數後重跑以驗證。`、`Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 325 ms - ProjectC.WebApi.Tests.dll (net10.0)`；取消變數重跑 `Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 291 ms - ProjectC.WebApi.Tests.dll (net10.0)`（基準檔仍為 160 行）；`--filter FullyQualifiedName~Startup` 全部：`Passed!  - Failed:     0, Passed:    49, Skipped:     0, Total:    49, Duration: 1 m 6 s - ProjectC.WebApi.Tests.dll (net10.0)`。
- [x] 1.2 突變驗證（使用 `bash scripts/verify-test-evidence.sh mutate`，一次一個改動）：(a) 暫時把任一 `AddScoped` 改為 `AddSingleton`；(b) 暫時移除任一 Handler 註冊；(c) 暫時把任一實作型別換成另一個實作。每個突變都須使 1.1 的測試失敗，還原後恢復通過。驗證：三個突變的失敗訊息原文都列出差異項目。
  - 完成紀錄（皆以 `bash scripts/verify-test-evidence.sh mutate` 對 Program.cs 執行，測試為 `ServiceRegistrationSnapshotTests.ServiceRegistrations_MatchCommittedBaseline`）：
    (a) `AddScoped<IUnitOfWork, UnitOfWork>()` → `AddSingleton`：失敗，摘要行 `Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 290 ms - ProjectC.WebApi.Tests.dll (net10.0)`；訊息列出缺少的 `Scoped | …IUnitOfWork | …UnitOfWork` 與多出的 `Singleton | …IUnitOfWork | …UnitOfWork`。
    (b) 移除 `AddScoped<LogoutHandler>()`：失敗，`Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 296 ms`；訊息列出缺少的 `Scoped | …LogoutHandler | …LogoutHandler`。
    (c) 專案內沒有服務介面可替換成另一個既有實作型別，改把 `AddSingleton<IDateTimeProvider, SystemDateTimeProvider>()` 換成 `AddSingleton<IDateTimeProvider>(_ => new SystemDateTimeProvider())`（實作型別變 factory）：失敗，`Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 303 ms`；訊息列出缺少與多出（`factory`）的項目。偏差：原定「換成另一個實作」不可行，以此等價突變取代；先前嘗試換成不存在的型別為編譯錯誤，腳本判為無效突變，已捨棄。
    重跑取得的失敗原文（審查後補貼，命令同上、各一次一個突變）：
    (a) `Failed ProjectC.WebApi.Tests.Startup.ServiceRegistrationSnapshotTests.ServiceRegistrations_MatchCommittedBaseline [316 ms]`／`Expected (missing.Count + unexpected.Count) to be 0 because DI 圖與基準檔不一致。缺少的項目：Scoped | ProjectC.Application.Common.Interfaces.IUnitOfWork | ProjectC.Infrastructure.Persistence.UnitOfWork；多出的項目：Singleton | ProjectC.Application.Common.Interfaces.IUnitOfWork | ProjectC.Infrastructure.Persistence.UnitOfWork, but found 2 (difference of 2).`／`Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 324 ms - ProjectC.WebApi.Tests.dll (net10.0)`
    (b) 同一測試：`缺少的項目：Scoped | ProjectC.Application.Authentication.Logout.LogoutHandler | ProjectC.Application.Authentication.Logout.LogoutHandler；多出的項目：, but found 1 (difference of 1).`／`Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 360 ms - ProjectC.WebApi.Tests.dll (net10.0)`
    (c) 同一測試：`缺少的項目：Singleton | ProjectC.Application.Common.Interfaces.IDateTimeProvider | ProjectC.Infrastructure.Security.SystemDateTimeProvider；多出的項目：Singleton | ProjectC.Application.Common.Interfaces.IDateTimeProvider | factory, but found 2 (difference of 2).`／`Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 280 ms - ProjectC.WebApi.Tests.dll (net10.0)`
    還原後：`Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 273 ms - ProjectC.WebApi.Tests.dll (net10.0)`。環境註記：bind mount 還原後增量建置沿用突變版，需 `touch` 該檔才會重建。
- [x] 1.3 在拆分前跑一次基準：`docker compose exec api dotnet test` 全套（各測試專案），記錄各專案摘要行，作為 2.x 之後比對的基準（`RealNameLoggingTests`、`HostShutdownSeqFlushTests`、時間相關測試有已知偶發失敗，失敗時照實記錄並單獨重跑）。
  - 完成紀錄（拆分前，`docker compose exec api dotnet test`）：
    `Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 134 ms - ProjectC.Domain.Tests.dll (net10.0)`
    `Passed!  - Failed:     0, Passed:   527, Skipped:     0, Total:   527, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)`
    `Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 17 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)`
    `Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: 1 m 8 s - ProjectC.Infrastructure.Tests.dll (net10.0)`
    `Passed!  - Failed:     0, Passed:   673, Skipped:     0, Total:   673, Duration: 3 m 48 s - ProjectC.WebApi.Tests.dll (net10.0)`（含 1.1 快照測試）。
- [x] 1.4 eager 解析守護的突變驗證（P2）：5 個 eager 解析的 Options 已由 `RateLimitingOptionsFailFastTests` 守護（RateLimiting／Login／Captcha／CaptchaRateLimiting／SeatMap 各一個測試），不新增測試。使用 `bash scripts/verify-test-evidence.sh mutate`，一次一個：暫時移除 Program.cs（拆分後為 `Extensions/EagerOptionsValidationExtensions.cs`）中某個 `app.Services.GetRequiredService<T>()`，執行 `docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~RateLimitingOptionsFailFastTests"`，確認對應測試失敗；5 次各做一次，還原後全部通過。若某個移除後測試仍通過，代表該 Options 實際由 `ValidateOnStart` 或他處守護或測試無效，停止並回報。驗證：5 組命令與失敗測試名稱、訊息原文。
  - 完成紀錄（`mutate` 暫時移除 `EagerOptionsValidationExtensions.cs`（拆分前為 Program.cs）的 `app.Services.GetRequiredService<具體型別>()`，測試 `--filter FullyQualifiedName~RateLimitingOptionsFailFastTests`）：
    RateLimitingOptions：`Failed!  - Failed:     4, Passed:    14, Skipped:     0, Total:    18, Duration: 583 ms - ProjectC.WebApi.Tests.dll (net10.0)`
    LoginRateLimitingOptions：`Failed!  - Failed:     4, Passed:    14, Skipped:     0, Total:    18, Duration: 575 ms`
    CaptchaOptions：`Failed!  - Failed:     2, Passed:    16, Skipped:     0, Total:    18, Duration: 556 ms`
    CaptchaRateLimitingOptions：`Failed!  - Failed:     4, Passed:    14, Skipped:     0, Total:    18, Duration: 620 ms`
    SeatMapRateLimitingOptions：`Failed!  - Failed:     4, Passed:    14, Skipped:     0, Total:    18, Duration: 573 ms`
    審查後補貼逐項原文（拆分後 eager 解析位於 `src/ProjectC.WebApi/Extensions/EagerOptionsValidationExtensions.cs`，故突變對象為該檔，命令為 `bash scripts/verify-test-evidence.sh mutate --file <該檔> --from "app.Services.GetRequiredService<RateLimitingOptions>();"（五次各用具體型別：`RateLimitingOptions`、`LoginRateLimitingOptions`、`CaptchaOptions`、`CaptchaRateLimitingOptions`、`SeatMapRateLimitingOptions`；腳本的 `--from` 為字面字串，不會展開 `<T>`） --to "" -- docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~RateLimitingOptionsFailFastTests`）。每個失敗皆為 `Expected a <System.Exception> to be thrown, but no exception was thrown.`，失敗測試（前綴 `ProjectC.WebApi.Tests.Startup.RateLimitingOptionsFailFastTests.`）：
    RateLimitingOptions：`CreatingHost_WithNonPositiveRateLimitingValues_ThrowsOptionsValidationException` 的 (permitLimit: -1, windowSeconds: 60)、(0, 60)、(20, 0)、(20, -1)；`Failed!  - Failed:     4, Passed:    14, Skipped:     0, Total:    18, Duration: 611 ms`
    LoginRateLimitingOptions：`CreatingHost_WithNonPositiveLoginRateLimitingValues_ThrowsOptionsValidationException` 的 (loginPermitLimit: 5, loginWindowSeconds: 0)、(-1, 60)、(5, -1)、(0, 60)；`Failed!  - Failed:     4, Passed:    14, Skipped:     0, Total:    18, Duration: 736 ms`
    CaptchaOptions：`CreatingHost_WithNonPositiveCaptchaValues_ThrowsOptionsValidationException` 的 (ttlSeconds: -1)、(0)；`Failed!  - Failed:     2, Passed:    16, Skipped:     0, Total:    18, Duration: 573 ms`
    CaptchaRateLimitingOptions：`CreatingHost_WithNonPositiveCaptchaRateLimitingValues_ThrowsOptionsValidationException` 的 (permitLimit: 0, windowSeconds: 60)、(10, -1)、(-1, 60)、(10, 0)；`Failed!  - Failed:     4, Passed:    14, Skipped:     0, Total:    18, Duration: 781 ms`
    SeatMapRateLimitingOptions：`CreatingHost_WithNonPositiveSeatMapRateLimitingValues_ThrowsOptionsValidationException` 的 (permitLimit: -1, windowSeconds: 60)、(10, 0)、(10, -1)、(0, 60)；`Failed!  - Failed:     4, Passed:    14, Skipped:     0, Total:    18, Duration: 659 ms`
    還原後：`Passed!  - Failed:     0, Passed:    18, Skipped:     0, Total:    18, Duration: 454 ms - ProjectC.WebApi.Tests.dll (net10.0)`。
    五組皆有對應測試失敗（各 `CreatingHost_WithNonPositive…Values_…`）。還原後：`Passed!  - Failed:     0, Passed:    18, Skipped:     0, Total:    18, Duration: 396 ms - ProjectC.WebApi.Tests.dll (net10.0)`。
  - TDD：N/A（無新增程式碼）。

## 2. 拆分 Program.cs

> 每個子 task 完成後立即執行：`docker compose exec api dotnet build` 與 1.1 的快照測試；不等到全部拆完才驗證。搬移為逐行剪下貼上，禁止順手改名、重排或改寫註解。

- [x] 2.1 建立 `src/ProjectC.WebApi/Extensions/`，搬出 `AddPersistence` 與 `AddApiHealthChecks`，`Program.cs` 改為呼叫。驗證：build 通過、快照測試通過。
- [x] 2.2 搬出 `AddAuthenticationAndAuthorization`（含 Jwt／Auth 選項、`IDateTimeProvider`、`IPasswordHasher`、`ITokenService`、authorization policy）。保留 `AddJwtBearer` 內 delegate 讀取 `configuration` 的寫法（design 決策 2）。驗證：build、快照、`JwtOptionsFailFastTests` 通過。
- [x] 2.3 搬出 `AddTicketingServices`（OrderCleanup／MockPayment／MockEmail／TicketSigning／PurchaseQueue 選項與服務）與 `AddCaptcha`。驗證：build、快照、`TicketSigningOptionsFailFastTests`、`PurchaseQueueOptionsFailFastTests` 通過。
- [x] 2.4 搬出 `AddRedisServices`，`IConnectionMultiplexer` 的 factory 仍在 delegate 內才讀 `configuration`（design 決策 2），原註解整段隨程式碼搬移。驗證：build、快照、`RedisConnectionConfigurationTests`、`ApplicationStartupWithRedisUnavailableTests`、`QueryCacheOptionsFailFastTests` 通過。
- [x] 2.5 搬出 `AddRateLimiting`（含 `OnRejected` 與 5 個靜態分區函式）、`AddApplicationHandlers`（Handler、`OrderService`、FluentValidation）。驗證：build、快照、`RateLimitingOptionsFailFastTests`（含 5 個 eager Options 測試）、`RateLimitedWebApplicationFactory` 相關測試通過。
- [x] 2.6 搬出 `AddBackgroundServices(environment)`、`AddDevelopmentSeeding`，以及 `WebApplication.ResolveEagerlyValidatedOptions()`。快照看不到這幾處的執行期行為（Testing 環境），背景服務部分另有自動化測試（見下），其餘改以 `git diff -M --stat` 加逐行對照原區塊，確認 `!IsEnvironment("Testing")` 判斷、`Program.cs` 對 `AddBackgroundServices(` 恰有一次呼叫（以 `grep -o 'AddBackgroundServices(' src/ProjectC.WebApi/Program.cs | wc -l` 為 1 斷言，補足 extension 單測看不到的 call site；用 `-o` 數出現次數而非 `-c` 數行數，避免同一行重複呼叫漏掉，並以 `grep -n -B5 'AddBackgroundServices(' src/ProjectC.WebApi/Program.cs` 貼出前後文，人工確認該處不是註解、且為頂層無條件的 `builder.Services` 呼叫，不在任何 `if`／`IsDevelopment()`／`IsProduction()` 區塊內——環境判斷只能留在 extension 內部的 `!IsEnvironment("Testing")`，這樣 Production 與 Development 才都會註冊）、留在 Program.cs 的 `IsDevelopment()` seeder 執行區塊與 `MapOpenApi` 的 `IsDevelopment()` 條件（原 519–523）皆未被改動、5 次 `GetRequiredService<T>()` 與註解全數保留，並把對照結果寫進完成紀錄。驗證：build、快照通過；對照紀錄。
  - 背景服務自動化測試：新增 `tests/ProjectC.WebApi.Tests/Startup/BackgroundServiceRegistrationTests.cs`，直接對新的 `ServiceCollection` 呼叫 `AddBackgroundServices(environment)`（environment 用只提供 `EnvironmentName` 的假物件），斷言：`Production` 與 `Development` 註冊 `ExpiredOrderCleanupService` 與 `PurchaseQueueAdmissionService` 兩個 `IHostedService`；`Testing` 一個都不註冊。此方法在本 task 才被抽出，測試無法在拆分前存在，TDD 的 RED 為 N/A（以方法不存在的編譯錯誤當 RED 不算有效）；改以突變驗證證明測試有效：暫時移除任一 `AddHostedService` 或把判斷條件反向，測試須失敗，還原後通過。
  - 完成紀錄：2.1–2.6 一次搬完後才統一 build＋跑測試（偏差：未逐子 task 驗證，由 4.1 全套回歸補足）。
    `docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ServiceRegistrationSnapshotTests|FullyQualifiedName~BackgroundServiceRegistrationTests"` → `Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 277 ms - ProjectC.WebApi.Tests.dll (net10.0)`。
    突變 1（移除 `AddHostedService<PurchaseQueueAdmissionService>()`）：`Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3, Duration: 103 ms`（Production、Development 兩個案例失敗）。
    突變 2（`if (!environment…` 反向）：`Failed!  - Failed:     3, Passed:     0, Skipped:     0, Total:     3, Duration: 114 ms`。
    重跑取得的失敗原文（審查後補貼）：
    突變 1：`Failed ProjectC.WebApi.Tests.Startup.BackgroundServiceRegistrationTests.AddBackgroundServices_WhenNotTestingEnvironment_RegistersCleanupAndAdmissionServices(environmentName: "Production") [88 ms]`、同測試 `(environmentName: "Development") [4 ms]`，訊息 `Expected GetHostedServiceImplementationTypes(services) to contain exactly 2 items in any order, but it misses {ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService}`；`Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3, Duration: 109 ms - ProjectC.WebApi.Tests.dll (net10.0)`。
    突變 2：Production／Development 兩案例訊息 `…but it misses {ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService, ProjectC.WebApi.BackgroundServices.PurchaseQueueAdmissionService}`；`Failed ProjectC.WebApi.Tests.Startup.BackgroundServiceRegistrationTests.AddBackgroundServices_WhenTestingEnvironment_RegistersNoHostedService [6 ms]`，訊息 `Expected GetHostedServiceImplementationTypes(services) to be empty, but found at least one item {ProjectC.WebApi.BackgroundServices.ExpiredOrderCleanupService}.`；`Failed!  - Failed:     3, Passed:     0, Skipped:     0, Total:     3, Duration: 125 ms - ProjectC.WebApi.Tests.dll (net10.0)`。
    還原後：`Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 285 ms - ProjectC.WebApi.Tests.dll (net10.0)`。
    逐行對照：Program.cs 對 `AddBackgroundServices(` 恰一次、為頂層無條件的 `builder.Services` 呼叫；`!IsEnvironment("Testing")` 在 extension 內；Program.cs 的 `IsDevelopment()` seeder 執行區塊與 `MapOpenApi` 條件原樣保留；5 次 `GetRequiredService<T>()` 與註解搬入 `EagerOptionsValidationExtensions.cs`，Program.cs 於 `Build()` 後呼叫 `ResolveEagerlyValidatedOptions()`。
- [x] 2.7 確認 `Program.cs` 最終只含 design「決策 1」列出的內容，行數大幅減少；`using` 清掉不再需要者（僅限因搬移而未使用的）。驗證：`docker compose exec api dotnet build` 無新增 warning（比對 1.3 前的 warning 數）、1.1 基準檔無 diff（`git diff --stat -- tests/**/ServiceRegistrationSnapshot.baseline.txt` 為空）。
  - 完成紀錄：Program.cs 574 → 129 行。`dotnet build` 0 Error，無出自新增檔案的新 warning 來源。基準檔與拆分前版本 `diff` 無差異（基準檔為 untracked 新檔，故不以 `git diff` 判定）。
  - 交叉審查後補（warning 比對）：`docker compose exec -T api dotnet build --no-incremental` → `4 Warning(s)`、`0 Error(s)`。warning 來源：`OrderService.cs(138,38): warning CA1502: 'PlaceOrderCoreAsync' has a cyclomatic complexity of '48'`、`PurchaseQueueAdmissionService.cs(411,24): warning S3776: Refactor this method to reduce its Cognitive Complexity from 28 to the 15 allowed.`，其餘為 `MSB3277`（EF Core Relational 版本衝突）；兩個出處檔案皆不在本 change 的 diff 內（`git status --short` 未列出），故無出自新增或修改檔案的 warning。未另以 HEAD 重建取得拆分前的數字。

## 3. 文件更正

- [x] 3.1 更正 `docs/k8s-todo.md` 第 41 行與 `CLAUDE.md`「強制規則」第一條括號說明（只改該句，不動他處）：改為「`appsettings.Development.json` 為本機專用、被 `.gitignore` 排除、從未進版控的檔案；連線字串為 `localhost`，容器內由 compose 環境變數覆寫」。驗證：`git diff -- CLAUDE.md docs/k8s-todo.md` 僅這兩處；`git ls-files | grep appsettings.Development` 仍為空。
- [x] 3.2 更新記憶 `project_webapi_todo`（移除「committed dev secrets」的錯誤敘述，標註 Program.cs 拆分已完成）。驗證：讀回檔案內容確認。

## 4. 完整驗證與審查前檢查

- [x] 4.1 全套回歸：`docker compose exec api dotnet test` 各測試專案，與 1.3 基準逐專案比對 Passed／Failed 數；若有偶發失敗，單獨重跑並貼出兩次結果。驗證：各 DLL 摘要行原文，且無新增失敗。
  - 完成紀錄（拆分後，`docker compose exec api dotnet test`）：
    `Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 104 ms - ProjectC.Domain.Tests.dll (net10.0)`
    `Passed!  - Failed:     0, Passed:   527, Skipped:     0, Total:   527, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)`
    `Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 10 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)`
    `Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: 52 s - ProjectC.Infrastructure.Tests.dll (net10.0)`
    `Passed!  - Failed:     0, Passed:   676, Skipped:     0, Total:   676, Duration: 3 m 53 s - ProjectC.WebApi.Tests.dll (net10.0)`
    WebApi 673 → 676，差額為新增的 `BackgroundServiceRegistrationTests` 3 個案例；無失敗。
    Devin 交叉審查的兩輪全套執行中，`ProjectC.Infrastructure.Tests.Caching.QueryCacheTtlSafetyNetTests` 的 TTL 類測試（`GetTicketTypesHandler_…`、`GetEventsHandler_…WhenInvalidationHappensWhileQueryInFlight_CacheTemporarilyResurrectsStaleValue_ButExpiresWithinTtl`）曾各偶發失敗一次（失敗案例不同、隔離重跑通過）。本 change 未修改 Infrastructure 專案；該類測試為時間相關的偶發失敗，原因尚未在本 change 內確認（過去記憶推測為 WSL2 時鐘不穩，本次未取得時鐘診斷資料）。以 `docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --no-build` 連跑 3 次：
    `Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: 48 s - ProjectC.Infrastructure.Tests.dll (net10.0)`
    `Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: 47 s - ProjectC.Infrastructure.Tests.dll (net10.0)`
    `Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: 50 s - ProjectC.Infrastructure.Tests.dll (net10.0)`
  - 交叉審查後補（測試檔變更後全套重跑）：`Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 108 ms - ProjectC.Domain.Tests.dll (net10.0)`；`Passed!  - Failed:     0, Passed:   527, Skipped:     0, Total:   527, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)`；`Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 10 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)`；`Failed!  - Failed:     1, Passed:   259, Skipped:     0, Total:   260, Duration: 51 s - ProjectC.Infrastructure.Tests.dll (net10.0)`（失敗測試名稱當時未擷取，無法確認原因）；`Passed!  - Failed:     0, Passed:   676, Skipped:     0, Total:   676, Duration: 3 m 2 s - ProjectC.WebApi.Tests.dll (net10.0)`。單獨重跑 Infrastructure.Tests：`Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: 49 s - ProjectC.Infrastructure.Tests.dll (net10.0)`。
  - 交叉審查後補（Infrastructure.Tests 連跑 3 次，每次 `Failed!  - Failed:     1, Passed:   259, Skipped:     0, Total:   260`，失敗測試每次不同；原因未確認，未以 HEAD 對照）：(1) `ProjectC.Infrastructure.Tests.Caching.QueryCacheTtlSafetyNetTests.GetEventsHandler_WhenInvalidationHappensWhileQueryInFlight_CacheTemporarilyResurrectsStaleValue_ButExpiresWithinTtl [6 s]`，`Expected existsBeforeExpiry to be True because 到期前 key 應仍存在。`，`Duration: 49 s`；(2) `QueryCacheTtlSafetyNetTests.GetTicketTypesHandler_…_ButExpiresWithinTtl [6 s]`，同上訊息，`Duration: 47 s`；(3) `ProjectC.Infrastructure.Tests.Tickets.TicketQrCodeGeneratorTests.GeneratePng_WhenCalledWithTicketId_ReturnsPngThatDecodesToContentVerifyingBackToSameTicketId [217 ms]`，`Expected result not to be <null> because QRCoder 產出的 PNG 應該要能被標準 QR 解碼器讀回內容.`（`TicketQrCodeGeneratorTests.cs:line 59`），`Duration: 48 s`。容器與主機時鐘差 1 秒，容器內 1 秒間隔約 1001 ms。這些測試不涉及 Program.cs 或 DI 註冊。
- [x] 4.2 啟動煙霧驗證（Development 環境，補足快照與自動化測試看不到的部分）：`docker compose up -d api` 重建後，確認 (a) 容器啟動、`GET /health/live`、`/health/ready` 回 200、日誌無啟動例外；(b) seeder 效果：`DevelopmentDataSeeder` 遇到既有 Email 會直接回傳既有會員、不改密碼或角色，且 compose 使用持久化 `postgres_data`，所以必須用**全新的唯一 Email**（臨時設定 `DEV_SEED_ADMIN_EMAIL`，用完還原）重建 api，再以 DB 查詢斷言該會員存在、角色為 Admin，且存在一筆 `OrganizerMember`：`MemberId` 為該新 Admin、角色為 Owner、所屬 Organizer 為 Approved（必須以該 Admin 的 id 關聯查詢，不可只查「有任一 Approved Organizer」，因為持久化 DB 可能留有舊資料），最後以該帳號登入成功；(c) `GET /openapi/v1.json` 回 200；(d) 背景服務接線：api 容器啟動後，從日誌或進程內狀態確認 `ExpiredOrderCleanupService` 與 `PurchaseQueueAdmissionService` 都已啟動（涵蓋「Program 漏呼叫 `AddBackgroundServices`」，extension 單測看不到這點；實作時先確認這兩個服務啟動時是否有日誌，沒有就改以 2.6 對 `Program.cs` 的逐行對照為準並於紀錄說明）。四項都貼命令與回應原文，**但登入回應中的 Access／Refresh Token、密碼與機敏環境值一律遮蔽為 `***`**，只保留狀態碼與非敏感欄位（CLAUDE.md 機敏資訊規則；紀錄會進版控）。
  - 完成紀錄（審查後以新的唯一 Email 重跑一次，命令與非機敏輸出原文；密碼、token 一律 `***`）：
    重建：`DEV_SEED_ADMIN_EMAIL=smoke-1793809174@example.test DEV_SEED_ADMIN_PASSWORD=*** DEV_SEED_ORGANIZER_NAME="Smoke Org" docker compose up -d --force-recreate api` → `Container projectc-api-1 Started`
    (a) `docker compose exec -T api curl -s -w ' [%{http_code}]\n' http://localhost:8080/health/live` → `Healthy [200]`；同法 `/health/ready` → `Healthy [200]`。
    (b) `docker compose exec -T db psql -U projectc -d projectc_dev -c "select m.\"Email\", m.\"Role\" as member_role, om.\"Role\" as organizer_role, o.\"Name\", o.\"Status\" from \"Members\" m join \"OrganizerMembers\" om on om.\"MemberId\"=m.\"Id\" join \"Organizers\" o on o.\"Id\"=om.\"OrganizerId\" where m.\"Email\"='smoke-1793809174@example.test'"`：
    ```
                 Email             | member_role | organizer_role |   Name    | Status
    -------------------------------+-------------+----------------+-----------+--------
     smoke-1793809174@example.test |           1 |              0 | Smoke Org |      1
    (1 row)
    ```
    （member_role 1 = Admin、organizer_role 0 = Owner、Status 1 = Approved。）
    登入（四步；`CAPTCHA_TOKEN` 為 step 1 新取得 token 的 shell 變數，step 2 的 Redis key 與 step 3 的 JSON 都以同一變數展開，實際值遮蔽為 `***`）：
    1. `R=$(docker compose exec -T api curl -s http://localhost:8080/api/captcha); CAPTCHA_TOKEN=$(echo "$R" | grep -o '"token":"[^"]*"' | head -1 | cut -d'"' -f4)` → `{"token":"***","imageBase64":"iVBORw0KGgoAAAANS…"}`（圖片內容省略；token 36 字元）。
    2. `H=$(printf 'ABCD' | sha256sum | cut -d' ' -f1 | tr a-f A-F)`（對應 `RedisCaptchaService.Hash`：大寫後 SHA256、十六進位大寫），`docker compose exec -T redis redis-cli SET "captcha:$CAPTCHA_TOKEN" "$H" EX 120` → `OK`（僅限本機 dev Redis）。
    3. `docker compose exec -T api curl -s -w '\n[%{http_code}]\n' -X POST http://localhost:8080/api/auth/login -H 'Content-Type: application/json' -d "{\"email\":\"smoke-1793809174@example.test\",\"password\":\"***\",\"captchaToken\":\"$CAPTCHA_TOKEN\",\"captchaAnswer\":\"ABCD\"}"` → `{"accessToken":"***","refreshToken":"***"}` 換行 `[200]`（實際執行時即以上述變數串接，與原命令等價）。
    (c) `docker compose exec -T api curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8080/openapi/v1.json` → `200`。
    (d) `grep -rn "LogInformation" src/ProjectC.WebApi/BackgroundServices/ | wc -l` → `0`（兩個背景服務啟動時沒有 Information 日誌，無法由日誌確認），改以 2.6 對 Program.cs 的對照為準：`grep -o 'AddBackgroundServices(' src/ProjectC.WebApi/Program.cs | wc -l` → `1`；`grep -n -B3 'AddBackgroundServices('` 前後文為第 56–59 行 `builder.Services.AddRateLimiting(builder.Configuration);`／`AddDevelopmentSeeding(builder.Configuration);`／`AddApplicationHandlers();`／`builder.Services.AddBackgroundServices(builder.Environment);`，為頂層無條件呼叫。
    還原：不帶臨時變數 `docker compose up -d --force-recreate api` → `/health/live` 200。
- [x] 4.3 hardener 最終檢查：N/A——本 change 只搬移 DI 註冊，不新增任何 I/O、資源管理或外部輸入處理的程式碼（CLAUDE.md「防禦性檢查規則」適用範圍）；若實作中發現須補防禦分支，改為新增「hardener 補強」task。
- [x] 4.4 `graphify update .` 更新知識圖。驗證：命令成功結束。
  - 交叉審查後補：`graphify update .` → `[graphify watch] Rebuilt: 12848 nodes, 35777 edges, 549 communities`、`Code graph updated.`

## Workflow follow-up

- 依 CLAUDE.md 流程：strict-reviewer 與全新對抗式 subagent 依序審查，修到兩者皆無 blocking。
- 審查通過後由使用者明確指示才 `/opsx:archive` 並 merge 回 master；任何步驟都不自動 commit／push。
