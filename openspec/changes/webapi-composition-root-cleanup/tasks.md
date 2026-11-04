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

- [ ] 1.1 新增 `tests/ProjectC.WebApi.Tests/Startup/ServiceRegistrationSnapshotTests.cs` 與基準檔（`ServiceRegistrationSnapshot.baseline.txt`，設為 `CopyToOutputDirectory`），做法見 design 決策 3；在**未修改 `Program.cs`** 的狀態下以環境變數／參數產生基準檔、提交前人工檢視內容合理（含 `Singleton`／`Scoped`／`Transient` 各類、`factory` 項目、專案 Options 的 `IConfigureOptions<>`）。驗證：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ServiceRegistrationSnapshotTests"` 通過；連跑 2 次基準內容一致（確認排序穩定、不含 Guid／位址等非決定性字串）。
  - 完成紀錄（貼原文：命令、摘要行、基準檔行數與前 10 行）：
  - TDD：N/A（特徵化測試，理由見上方）。
- [ ] 1.2 突變驗證（使用 `bash scripts/verify-test-evidence.sh mutate`，一次一個改動）：(a) 暫時把任一 `AddScoped` 改為 `AddSingleton`；(b) 暫時移除任一 Handler 註冊；(c) 暫時把任一實作型別換成另一個實作。每個突變都須使 1.1 的測試失敗，還原後恢復通過。驗證：三個突變的失敗訊息原文都列出差異項目。
  - 完成紀錄（貼每個突變的命令、失敗測試名稱與訊息、還原後通過的摘要行）：
- [ ] 1.3 在拆分前跑一次基準：`docker compose exec api dotnet test` 全套（各測試專案），記錄各專案摘要行，作為 2.x 之後比對的基準（`RealNameLoggingTests`、`HostShutdownSeqFlushTests`、時間相關測試有已知偶發失敗，失敗時照實記錄並單獨重跑）。
  - 完成紀錄（各 DLL 摘要行原文；另記錄 `docker compose exec api dotnet build` 的 warning 數，供 2.7 比對）：
- [ ] 1.4 eager 解析守護的突變驗證（P2）：5 個 eager 解析的 Options 已由 `RateLimitingOptionsFailFastTests` 守護（RateLimiting／Login／Captcha／CaptchaRateLimiting／SeatMap 各一個測試），不新增測試。使用 `bash scripts/verify-test-evidence.sh mutate`，一次一個：暫時移除 Program.cs 中某個 `app.Services.GetRequiredService<T>()`，執行 `docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~RateLimitingOptionsFailFastTests"`，確認對應測試失敗；5 次各做一次，還原後全部通過。若某個移除後測試仍通過，代表該 Options 實際由 `ValidateOnStart` 或他處守護或測試無效，停止並回報。驗證：5 組命令與失敗測試名稱、訊息原文。
  - 完成紀錄：
  - TDD：N/A（無新增程式碼）。

## 2. 拆分 Program.cs

> 每個子 task 完成後立即執行：`docker compose exec api dotnet build` 與 1.1 的快照測試；不等到全部拆完才驗證。搬移為逐行剪下貼上，禁止順手改名、重排或改寫註解。

- [ ] 2.1 建立 `src/ProjectC.WebApi/Extensions/`，搬出 `AddPersistence` 與 `AddApiHealthChecks`，`Program.cs` 改為呼叫。驗證：build 通過、快照測試通過。
- [ ] 2.2 搬出 `AddAuthenticationAndAuthorization`（含 Jwt／Auth 選項、`IDateTimeProvider`、`IPasswordHasher`、`ITokenService`、authorization policy）。保留 `AddJwtBearer` 內 delegate 讀取 `configuration` 的寫法（design 決策 2）。驗證：build、快照、`JwtOptionsFailFastTests` 通過。
- [ ] 2.3 搬出 `AddTicketingServices`（OrderCleanup／MockPayment／MockEmail／TicketSigning／PurchaseQueue 選項與服務）與 `AddCaptcha`。驗證：build、快照、`TicketSigningOptionsFailFastTests`、`PurchaseQueueOptionsFailFastTests` 通過。
- [ ] 2.4 搬出 `AddRedisServices`，`IConnectionMultiplexer` 的 factory 仍在 delegate 內才讀 `configuration`（design 決策 2），原註解整段隨程式碼搬移。驗證：build、快照、`RedisConnectionConfigurationTests`、`ApplicationStartupWithRedisUnavailableTests`、`QueryCacheOptionsFailFastTests` 通過。
- [ ] 2.5 搬出 `AddRateLimiting`（含 `OnRejected` 與 5 個靜態分區函式）、`AddApplicationHandlers`（Handler、`OrderService`、FluentValidation）。驗證：build、快照、`RateLimitingOptionsFailFastTests`（含 5 個 eager Options 測試）、`RateLimitedWebApplicationFactory` 相關測試通過。
- [ ] 2.6 搬出 `AddBackgroundServices(environment)`、`AddDevelopmentSeeding`，以及 `WebApplication.ResolveEagerlyValidatedOptions()`。快照看不到這幾處的執行期行為（Testing 環境），背景服務部分另有自動化測試（見下），其餘改以 `git diff -M --stat` 加逐行對照原區塊，確認 `!IsEnvironment("Testing")` 判斷、`Program.cs` 對 `AddBackgroundServices(` 恰有一次呼叫（以 `grep -o 'AddBackgroundServices(' src/ProjectC.WebApi/Program.cs | wc -l` 為 1 斷言，補足 extension 單測看不到的 call site；用 `-o` 數出現次數而非 `-c` 數行數，避免同一行重複呼叫漏掉，並以 `grep -n -B5 'AddBackgroundServices(' src/ProjectC.WebApi/Program.cs` 貼出前後文，人工確認該處不是註解、且為頂層無條件的 `builder.Services` 呼叫，不在任何 `if`／`IsDevelopment()`／`IsProduction()` 區塊內——環境判斷只能留在 extension 內部的 `!IsEnvironment("Testing")`，這樣 Production 與 Development 才都會註冊）、留在 Program.cs 的 `IsDevelopment()` seeder 執行區塊與 `MapOpenApi` 的 `IsDevelopment()` 條件（原 519–523）皆未被改動、5 次 `GetRequiredService<T>()` 與註解全數保留，並把對照結果寫進完成紀錄。驗證：build、快照通過；對照紀錄。
  - 背景服務自動化測試：新增 `tests/ProjectC.WebApi.Tests/Startup/BackgroundServiceRegistrationTests.cs`，直接對新的 `ServiceCollection` 呼叫 `AddBackgroundServices(environment)`（environment 用只提供 `EnvironmentName` 的假物件），斷言：`Production` 與 `Development` 註冊 `ExpiredOrderCleanupService` 與 `PurchaseQueueAdmissionService` 兩個 `IHostedService`；`Testing` 一個都不註冊。此方法在本 task 才被抽出，測試無法在拆分前存在，TDD 的 RED 為 N/A（以方法不存在的編譯錯誤當 RED 不算有效）；改以突變驗證證明測試有效：暫時移除任一 `AddHostedService` 或把判斷條件反向，測試須失敗，還原後通過。
  - 完成紀錄（測試命令、突變失敗訊息原文、還原後通過摘要行）：
- [ ] 2.7 確認 `Program.cs` 最終只含 design「決策 1」列出的內容，行數大幅減少；`using` 清掉不再需要者（僅限因搬移而未使用的）。驗證：`docker compose exec api dotnet build` 無新增 warning（比對 1.3 前的 warning 數）、1.1 基準檔無 diff（`git diff --stat -- tests/**/ServiceRegistrationSnapshot.baseline.txt` 為空）。
  - 完成紀錄（Program.cs 行數前後、build warning 數、基準檔 diff 輸出）：

## 3. 文件更正

- [ ] 3.1 更正 `docs/k8s-todo.md` 第 41 行與 `CLAUDE.md`「強制規則」第一條括號說明（只改該句，不動他處）：改為「`appsettings.Development.json` 為本機專用、被 `.gitignore` 排除、從未進版控的檔案；連線字串為 `localhost`，容器內由 compose 環境變數覆寫」。驗證：`git diff -- CLAUDE.md docs/k8s-todo.md` 僅這兩處；`git ls-files | grep appsettings.Development` 仍為空。
- [ ] 3.2 更新記憶 `project_webapi_todo`（移除「committed dev secrets」的錯誤敘述，標註 Program.cs 拆分已完成）。驗證：讀回檔案內容確認。

## 4. 完整驗證與審查前檢查

- [ ] 4.1 全套回歸：`docker compose exec api dotnet test` 各測試專案，與 1.3 基準逐專案比對 Passed／Failed 數；若有偶發失敗，單獨重跑並貼出兩次結果。驗證：各 DLL 摘要行原文，且無新增失敗。
  - 完成紀錄：
- [ ] 4.2 啟動煙霧驗證（Development 環境，補足快照與自動化測試看不到的部分）：`docker compose up -d api` 重建後，確認 (a) 容器啟動、`GET /health/live`、`/health/ready` 回 200、日誌無啟動例外；(b) seeder 效果：`DevelopmentDataSeeder` 遇到既有 Email 會直接回傳既有會員、不改密碼或角色，且 compose 使用持久化 `postgres_data`，所以必須用**全新的唯一 Email**（臨時設定 `DEV_SEED_ADMIN_EMAIL`，用完還原）重建 api，再以 DB 查詢斷言該會員存在、角色為 Admin，且存在一筆 `OrganizerMember`：`MemberId` 為該新 Admin、角色為 Owner、所屬 Organizer 為 Approved（必須以該 Admin 的 id 關聯查詢，不可只查「有任一 Approved Organizer」，因為持久化 DB 可能留有舊資料），最後以該帳號登入成功；(c) `GET /openapi/v1.json` 回 200；(d) 背景服務接線：api 容器啟動後，從日誌或進程內狀態確認 `ExpiredOrderCleanupService` 與 `PurchaseQueueAdmissionService` 都已啟動（涵蓋「Program 漏呼叫 `AddBackgroundServices`」，extension 單測看不到這點；實作時先確認這兩個服務啟動時是否有日誌，沒有就改以 2.6 對 `Program.cs` 的逐行對照為準並於紀錄說明）。四項都貼命令與回應原文，**但登入回應中的 Access／Refresh Token、密碼與機敏環境值一律遮蔽為 `***`**，只保留狀態碼與非敏感欄位（CLAUDE.md 機敏資訊規則；紀錄會進版控）。
  - 完成紀錄：
- [ ] 4.3 hardener 最終檢查：N/A——本 change 只搬移 DI 註冊，不新增任何 I/O、資源管理或外部輸入處理的程式碼（CLAUDE.md「防禦性檢查規則」適用範圍）；若實作中發現須補防禦分支，改為新增「hardener 補強」task。
- [ ] 4.4 `graphify update .` 更新知識圖。驗證：命令成功結束。

## Workflow follow-up

- 依 CLAUDE.md 流程：strict-reviewer 與全新對抗式 subagent 依序審查，修到兩者皆無 blocking。
- 審查通過後由使用者明確指示才 `/opsx:archive` 並 merge 回 master；任何步驟都不自動 commit／push。
