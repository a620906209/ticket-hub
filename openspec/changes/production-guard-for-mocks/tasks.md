## 1. 啟動守門（TDD）

- [ ] 1.1 新增 `MockServicesOptions`／`MockServicesOptionsValidator` 並接入 `AddTicketingServices` 與 `ResolveEagerlyValidatedOptions`（對應 MOCKGUARD-001～005）
  - 測試先行（WebApi.Tests `Startup/MockServicesProductionGuardTests.cs`，比照 `CaptchaOptionsFailFastTests`，不需容器）：
    - `CreatingHost_InProductionWithoutFlagAndMocks_ThrowsOptionsValidationMentioningPaymentAndEmail`（001，含 `production` 小寫與旗標 `false` 的 002）
    - `CreatingHost_InProductionWithOnlyEmailMock_ThrowsMentioningOnlyEmail`（003；以替身取代 `IPaymentGateway` 註冊）
    - `CreatingHost_InProductionWithNonMockServices_StartsWithoutWarning`（004）
    - `CreatingHost_InDevelopmentOrTestingWithMocks_StartsWithoutWarning`（005）
  - 預期 RED：目前 Production + Mock 可正常啟動，故 001／002／003 的 `Should().Throw` 失敗（訊息應為 Expected a thrown exception…but no exception was thrown），不得是編譯或環境錯誤；004／005 是現況即通過的負向分支，不作為 RED 依據
  - 最小實作：options、validator（Singleton，注入 `IHostEnvironment`、`IPaymentGateway`、`IEmailNotificationService`）、註冊與 eager 解析
  - GREEN：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~MockServicesProductionGuard"` 全數通過
  - 重構後重跑：同上命令，或標 N/A（無重構）
  - 紀錄：RED／GREEN／重構（原文）：
  - 還原驗證（核心啟動守門，依 `scripts/verify-test-evidence.sh restore`）：

- [ ] 1.2 明示放行與 Warning（對應 MOCKGUARD-006～008）
  - 測試先行（同檔或 `MockServicesAllowedWarningTests.cs`，比照 `CaptchaAcceptAnyWarningTests`）：
    - `StartingHost_InProductionWithFlagAndMocks_StartsAndLogsExactlyOneWarning`（006；Warning 內容含付款與 Email；同時是 logger 擷取機制有效的非空前提，004／005 的「無 Warning」斷言須共用同一擷取方法）
    - `CreatingHost_WithNonBooleanFlag_Throws`（007）
    - `ConfirmOrder_InProductionWithFlag_BehavesAsInDevelopment`（008；以 WebApi 整合測試分別在 Production＋旗標 true 與 Development 兩組環境呼叫確認訂單，斷言付款結果相同；手法於實作前定為此，若不可行須先回報，不得改測 `MockPaymentGateway` 本身）
  - 預期 RED：Warning 尚未記錄，006 的 `warnings.Should().ContainSingle()` 失敗（Expected …to contain a single item, but the collection is empty）
  - 最小實作：在 `ResolveEagerlyValidatedOptions` 記錄 Warning
  - GREEN 命令同 1.1 的 filter，並含新測試類別
  - 紀錄：RED／GREEN／重構（原文）：

- [ ] 1.3 更新 `ServiceRegistrationSnapshot.baseline.txt`
  - 測試先行：既有 `ServiceRegistrationSnapshotTests` 在 1.1 實作後 RED（新增註冊與 baseline 不符）；逐行核對差異僅限本 change 的註冊後更新 baseline
  - 紀錄：RED／GREEN（原文）：

## 2. 既有部署明示放行（對應 MOCKGUARD-009）

- [ ] 2.1 `k8s/10-configmap.yaml` 加 `Mocks__AllowInProduction: "true"`（附說明註解）；`deploy/docker-compose.verify.yml` 的 api 加同名環境變數
  - 測試先行（WebApi.Tests `Startup/MockGuardDeploymentConfigTests.cs`，不需容器）：
    - `K8sConfigMap_DeclaresMocksAllowInProductionTrue`、`VerifyCompose_DeclaresMocksAllowInProductionTrue`：從 repo 根目錄讀檔，先斷言檔案存在，再斷言 `Mocks__AllowInProduction` 鍵恰好命中一次且值為 `true`（避免空結果假通過）
    - `ApiDockerfile_DefaultsToProductionEnvironment`：斷言 `deploy/api.Dockerfile` 含 `ASPNETCORE_ENVIRONMENT=Production`，鎖住「verify compose 未設環境即為 Production」的前提
  - 預期 RED：旗標尚未加入，前兩項失敗（Expected …to contain exactly one "Mocks__AllowInProduction", but found 0）；Dockerfile 測試預期直接通過，屬前提回歸保護，不作為 RED 依據
  - 最小實作：兩個設定檔各加旗標
  - GREEN：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~MockGuardDeploymentConfig"`
  - 輔助驗證（補充，不取代上述測試）：`bash scripts/verify-production-images.sh` 通過；`bash scripts/verify-k8s-manifests.sh` 通過（需 kind；不可用時如實記錄跳過並於交接說明，MOCKGUARD-009 仍由上述自動化測試涵蓋）
  - 紀錄（RED／GREEN／腳本輸出原文）：

## 3. 文件同步（TDD：N/A，純文件）

- [ ] 3.1 README 已知限制改為「Production 預設拒絕以 Mock 啟動，須 `Mocks__AllowInProduction=true` 明示放行」；`docs/review/TODO.md` C-1 標為部分緩解、④ 標已完成，並把第 38 行的「`ValidateOnStart`」更正為「啟動時強制解析驗證（`ResolveEagerlyValidatedOptions`）」；`docs/2026-11-12-k8s-guide.md` 與 `.env.example` 補旗標說明（僅限展示／驗證、不得收費）
  - 驗證：`grep -c "Mocks__AllowInProduction" .env.example docs/2026-11-12-k8s-guide.md` 兩檔皆 ≥ 1 且含「不得」收費字樣（貼輸出）；`bash scripts/check-doc-paths.sh` 通過；`openspec validate production-guard-for-mocks` 通過

## 4. 收尾

- [ ] 4.1 hardener 最終檢查：驗證器為純啟動檢查、無 I/O；確認無遺漏防禦分支（旗標無法解析、兩項皆非 Mock），結果記錄於此
- [ ] 4.2 全套測試（各專案摘要行貼原文）：`docker compose exec api dotnet test`（Domain／Application／Infrastructure／WebApi／LoadTest.Seeder 各專案）
- [ ] 4.3 `graphify update .`
- [ ] 4.4 Task 完成後告知：spec 同步狀態、AC 涵蓋、重複邏輯（與 `CaptchaOptionsValidator` 的環境判斷是否值得抽共用）、命名一致性
