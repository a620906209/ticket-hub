## 1. 啟動守門（TDD）

- [x] 1.1 新增 `MockServicesOptions`／`MockServicesOptionsValidator` 並接入 `AddTicketingServices` 與 `ResolveEagerlyValidatedOptions`（對應 MOCKGUARD-001～005）
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
    - RED（正式程式碼修改前，命令 `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~MockServicesProductionGuard"`，均為目標行為不符、非編譯錯誤）：
      - `CreatingHost_InProductionWithoutFlagAndMocks_ThrowsOptionsValidationMentioningPaymentAndEmail`（`"Production", null`／`"production", null`／`"Production", "false"` 三組）：`Expected a <System.Exception> to be thrown, but no exception was thrown.`
      - `CreatingHost_InProductionWithOnlyEmailMock_ThrowsMentioningOnlyEmail`：`Expected a <System.Exception> to be thrown, but no exception was thrown.`
      - 摘要行：`Failed!  - Failed:     7, Passed:     5, Skipped:     0, Total:    12, Duration: 3 s - ProjectC.WebApi.Tests.dll (net10.0)`（另 3 筆為 1.2 的 006／006-only-email／007，見 1.2）
    - GREEN：`Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 4 s - ProjectC.WebApi.Tests.dll (net10.0)`
    - 重構：N/A（無重構）
  - 還原驗證（核心啟動守門，依 `scripts/verify-test-evidence.sh restore`，`--path` 為 `EagerOptionsValidationExtensions.cs`、`TicketingServiceCollectionExtensions.cs`、`MockServicesOptions.cs`、`MockServicesOptionsValidator.cs`，測試檔不入 stash）：
    ```
    $ git stash push -u -- src/ProjectC.WebApi/Extensions/EagerOptionsValidationExtensions.cs src/ProjectC.WebApi/Extensions/TicketingServiceCollectionExtensions.cs src/ProjectC.WebApi/Extensions/MockServicesOptions.cs src/ProjectC.WebApi/Extensions/MockServicesOptionsValidator.cs
    Saved working directory and index state WIP on feature/production-guard-for-mocks: 953260c spec：production-guard-for-mocks（Production 綁定 Mock 付款／Email 時預設拒絕啟動）
    $ git stash list
    stash@{0}: WIP on feature/production-guard-for-mocks: 953260c spec：production-guard-for-mocks（Production 綁定 Mock 付款／Email 時預設拒絕啟動）
      Failed ...CreatingHost_InProductionWithoutFlagAndMocks_ThrowsOptionsValidationMentioningPaymentAndEmail(environmentName: "Production", allowFlag: null) [225 ms]
       Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ...CreatingHost_InProductionWithoutFlagAndMocks_ThrowsOptionsValidationMentioningPaymentAndEmail(environmentName: "production", allowFlag: null) [274 ms]
       Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ...CreatingHost_InProductionWithoutFlagAndMocks_ThrowsOptionsValidationMentioningPaymentAndEmail(environmentName: "Production", allowFlag: "false") [198 ms]
       Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ...StartingHost_InProductionWithFlagAndMocks_StartsAndLogsExactlyOneWarning [104 ms]
       Expected warnings to contain a single item, but the collection is empty.
      Failed ...CreatingHost_WithNonBooleanFlag_Throws [118 ms]
       Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed ...StartingHost_InProductionWithFlagAndOnlyEmailMock_WarningListsOnlyEmail [123 ms]
       Expected warnings to contain a single item, but the collection is empty.
      Failed ...CreatingHost_InProductionWithOnlyEmailMock_ThrowsMentioningOnlyEmail [140 ms]
       Expected a <System.Exception> to be thrown, but no exception was thrown.
    Failed!  - Failed:     7, Passed:     5, Skipped:     0, Total:    12, Duration: 4 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    $ git stash pop
    Dropped refs/stash@{0} (e4c0903b253f48a42656e47b79b3fff02513fe5e)
    ```
    pop 後 `git status --short` 還原為 2 筆 `M`（兩個 Extensions 檔）＋ 3 筆 `??`（兩個新正式檔與測試檔），與 stash 前一致。

- [x] 1.2 明示放行與 Warning（對應 MOCKGUARD-006～008）
  - 測試先行（同檔或 `MockServicesAllowedWarningTests.cs`，比照 `CaptchaAcceptAnyWarningTests`）：
    - `StartingHost_InProductionWithFlagAndMocks_StartsAndLogsExactlyOneWarning`（006；Warning 內容含付款與 Email；同時是 logger 擷取機制有效的非空前提，004／005 的「無 Warning」斷言須共用同一擷取方法）
    - `CreatingHost_WithNonBooleanFlag_Throws`（007）
    - `ConfirmOrder_InProductionWithFlag_BehavesAsInDevelopment`（008；以 WebApi 整合測試分別在 Production＋旗標 true 與 Development 兩組環境呼叫確認訂單，斷言付款結果相同；手法於實作前定為此，若不可行須先回報，不得改測 `MockPaymentGateway` 本身）
  - 預期 RED：Warning 尚未記錄，006 的 `warnings.Should().ContainSingle()` 失敗（Expected …to contain a single item, but the collection is empty）
  - 最小實作：在 `ResolveEagerlyValidatedOptions` 記錄 Warning
  - GREEN 命令同 1.1 的 filter，並含新測試類別
  - 紀錄：RED／GREEN／重構（原文）：
    - RED（006／007，與 1.1 同一次執行，正式程式碼修改前；訊息原文見 1.1 還原驗證輸出）：
      - `StartingHost_InProductionWithFlagAndMocks_StartsAndLogsExactlyOneWarning`：`Expected warnings to contain a single item, but the collection is empty.`
      - `StartingHost_InProductionWithFlagAndOnlyEmailMock_WarningListsOnlyEmail`：`Expected warnings to contain a single item, but the collection is empty.`
      - `CreatingHost_WithNonBooleanFlag_Throws`：`Expected a <System.Exception> to be thrown, but no exception was thrown.`
    - GREEN：與 1.1 同一次執行，`Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 4 s - ProjectC.WebApi.Tests.dll (net10.0)`
    - 004 的「無 Warning」斷言與 006 共用 `StartHostAndCaptureGuardWarnings`，006 能抓到 Warning 即證明擷取機制有效（非空前提）。
    - 008（`Startup/MockServicesAllowedBehaviorTests.cs::ConfirmOrder_InProductionWithFlag_BehavesAsInDevelopment`）：特性描述測試（Mock 行為本來就不變，無法有目標行為 RED），RED N/A；改以「Production 無旗標時整個 host 無法建立（001）」佐證此測試對守門敏感。GREEN：`Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 6 s - ProjectC.WebApi.Tests.dll (net10.0)`
    - 重構：N/A

- [x] 1.3 更新 `ServiceRegistrationSnapshot.baseline.txt`
  - 測試先行：既有 `ServiceRegistrationSnapshotTests` 在 1.1 實作後 RED（新增註冊與 baseline 不符）；逐行核對差異僅限本 change 的註冊後更新 baseline
  - 紀錄：RED／GREEN（原文）：
    - RED（1.1 實作後、更新 baseline 前）：`ServiceRegistrations_MatchCommittedBaseline` 失敗，`Expected (missing.Count + unexpected.Count) to be 0 because DI 圖與基準檔不一致。缺少的項目：；多出的項目：Singleton | Microsoft.Extensions.Options.IConfigureOptions`1[ProjectC.WebApi.Extensions.MockServicesOptions] | instance || Singleton | Microsoft.Extensions.Options.IOptionsChangeTokenSource`1[ProjectC.WebApi.Extensions.MockServicesOptions] | instance || Singleton | Microsoft.Extensions.Options.IValidateOptions`1[ProjectC.WebApi.Extensions.MockServicesOptions] | ProjectC.WebApi.Extensions.MockServicesOptionsValidator || Singleton | ProjectC.WebApi.Extensions.MockServicesOptions | factory, but found 4 (difference of 4).`；`Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 672 ms - ProjectC.WebApi.Tests.dll (net10.0)`
    - 差異逐行核對：缺少 0 項，多出 4 項，皆為 `MockServicesOptions` 的註冊（IConfigureOptions／IOptionsChangeTokenSource／IValidateOptions→`MockServicesOptionsValidator`／unwrap factory），與 `AddTicketingServices` 新增的三段註冊一一對應；baseline 依排序插入 4 行（`git diff --stat`：4 insertions）。
    - GREEN（`--filter "FullyQualifiedName~ServiceRegistrationSnapshot|FullyQualifiedName~MockServices"`）：`Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14, Duration: 11 s - ProjectC.WebApi.Tests.dll (net10.0)`

## 2. 既有部署明示放行（對應 MOCKGUARD-009）

- [x] 2.1 `k8s/10-configmap.yaml` 加 `Mocks__AllowInProduction: "true"`（附說明註解）；`deploy/docker-compose.verify.yml` 的 api 加同名環境變數
  - 測試先行（WebApi.Tests `Startup/MockGuardDeploymentConfigTests.cs`，不需容器）：
    - `K8sConfigMap_DeclaresMocksAllowInProductionTrue`、`VerifyCompose_DeclaresMocksAllowInProductionTrue`：從 repo 根目錄讀檔，先斷言檔案存在，再斷言 `Mocks__AllowInProduction` 鍵恰好命中一次且值為 `true`（避免空結果假通過）
    - `ApiDockerfile_DefaultsToProductionEnvironment`：斷言 `deploy/api.Dockerfile` 含 `ASPNETCORE_ENVIRONMENT=Production`，鎖住「verify compose 未設環境即為 Production」的前提
  - 預期 RED：旗標尚未加入，前兩項失敗（Expected …to contain exactly one "Mocks__AllowInProduction", but found 0）；Dockerfile 測試預期直接通過，屬前提回歸保護，不作為 RED 依據
  - 最小實作：兩個設定檔各加旗標
  - GREEN：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~MockGuardDeploymentConfig"`
  - 輔助驗證（補充，不取代上述測試）：`bash scripts/verify-production-images.sh` 通過；`bash scripts/verify-k8s-manifests.sh` 通過（需 kind；不可用時如實記錄跳過並於交接說明，MOCKGUARD-009 仍由上述自動化測試涵蓋）
  - 紀錄（RED／GREEN／腳本輸出原文）：
    - RED（旗標加入前，`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~MockGuardDeploymentConfig"`）：
      - `VerifyCompose_DeclaresMocksAllowInProductionTrue`：`Expected Regex.Matches(content, Regex.Escape(FlagKey)) to contain 1 item(s) because deploy/docker-compose.verify.yml 的 Mocks__AllowInProduction 鍵必須恰好出現一次, but found 0: {empty}.`
      - `K8sConfigMap_DeclaresMocksAllowInProductionTrue`：`Expected Regex.Matches(content, Regex.Escape(FlagKey)) to contain 1 item(s) because k8s/10-configmap.yaml 的 Mocks__AllowInProduction 鍵必須恰好出現一次, but found 0: {empty}.`
      - 摘要行：`Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3, Duration: 125 ms - ProjectC.WebApi.Tests.dll (net10.0)`（`ApiDockerfile_DefaultsToProductionEnvironment` 為預期通過的前提保護）
    - GREEN（兩個設定檔各加旗標後，同命令）：`Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 116 ms - ProjectC.WebApi.Tests.dll (net10.0)`
    - `bash scripts/verify-production-images.sh`（以含守門的新 api 映像、verify compose 的旗標實際啟動 Production）：
      ```
      PASS [7] 缺 Jwt__SigningKey 時 api 以結束碼 139 退出，日誌含 OptionsValidationException 與 JwtOptions
      PASS [4] /health/live=200，/health/ready=200
      PASS [5] 經 web 的 GET /api/events 回 200
      SUMMARY: 13 passed, 0 failed
      ```
    - `bash scripts/verify-k8s-manifests.sh`：先以含守門的新程式碼重建 `projectc/api:local`，再跑腳本（Production＋ConfigMap 旗標實際在 docker-desktop 叢集啟動）：`SUMMARY: 16 passed, 0 failed, 1 unverified`；`UNVERIFIED [8c] 本機 CNI 不執行 NetworkPolicy…`（既有預期結果，與本 change 無關）。輸出中的堆疊追蹤來自腳本刻意的缺設定案例。

## 3. 文件同步（TDD：N/A，純文件）

- [x] 3.1 README 已知限制改為「Production 預設拒絕以 Mock 啟動，須 `Mocks__AllowInProduction=true` 明示放行」；`docs/review/TODO.md` C-1 標為部分緩解、④ 標已完成，並把第 38 行的「`ValidateOnStart`」更正為「啟動時強制解析驗證（`ResolveEagerlyValidatedOptions`）」；`docs/2026-11-12-k8s-guide.md` 與 `.env.example` 補旗標說明（僅限展示／驗證、不得收費）
  - 驗證：`grep -c "Mocks__AllowInProduction" .env.example docs/2026-11-12-k8s-guide.md` 兩檔皆 ≥ 1 且含「不得」收費字樣（貼輸出）；`bash scripts/check-doc-paths.sh` 通過；`openspec validate production-guard-for-mocks` 通過
  - 紀錄：TDD N/A（純文件）。輸出原文：
    ```
    .env.example:1
    docs/2026-11-12-k8s-guide.md:1
    .env.example:39:# Mocks__AllowInProduction（…僅限展示／驗證，不得用於收費）。
    docs/2026-11-12-k8s-guide.md:29:- **Mock 放行旗標**：…這個旗標僅限本機驗證／展示，**不得用於收費環境**；…
    OK: 所有 docs 路徑引用都存在
    Change 'production-guard-for-mocks' is valid
    ```

## 4. 收尾

- [x] 4.1 hardener 最終檢查：驗證器為純啟動檢查、無 I/O；確認無遺漏防禦分支（旗標無法解析、兩項皆非 Mock），結果記錄於此：`MockServicesOptionsValidator.Validate` 無 I/O、無資源管理；旗標無法解析由 `ConfigurationBinder` 拋例外（007 測試涵蓋）；兩項皆非 Mock 回 Success（004 涵蓋）；環境名稱比對使用 `IsProduction()`（不分大小寫，002 的 `production` 案例涵蓋）；解析 `IPaymentGateway`／`IEmailNotificationService` 的 DI 失敗屬容器本身錯誤、不在驗證器處理。無需補強，未修改程式碼
- [x] 4.2 全套測試（各專案摘要行貼原文）：`docker compose exec api dotnet test`（Domain／Application／Infrastructure／WebApi／LoadTest.Seeder 各專案）
  - 第一次全套（守門生效後）：WebApi 4 個既有測試失敗——`AdminBootstrapOptionsFailFastTests.CreatingHost_WithNeitherSettingConfigured_StartsNormally(environmentName: "Production")`、`ProductionAdminBootstrapStartupTests.Startup_InProductionWithBootstrapConfigured_CreatesAdmin`、`...Startup_InProductionWhenAdminAlreadyExists_DoesNotCreateSecondAdmin`、`CaptchaOptionsFailFastTests.CreatingHost_InProductionWithEnforced_StartsNormally`；原因是它們以 Production 啟動且綁 Mock，被新守門擋下（預期的連帶影響，非守門錯誤）。修正：三個測試檔的設定加 `Mocks:AllowInProduction = true`（這些測試不驗證 Mock 守門）。摘要行：`Failed!  - Failed:     4, Passed:   934, Skipped:     0, Total:   938, Duration: 6 m 47 s - ProjectC.WebApi.Tests.dll (net10.0)`
  - 修正後全套摘要行：
    - `Passed!  - Failed:     0, Passed:   153, Skipped:     0, Total:   153, Duration: 366 ms - ProjectC.Domain.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   632, Skipped:     0, Total:   632, Duration: 3 s - ProjectC.Application.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 19 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   303, Skipped:     0, Total:   303, Duration: 1 m 27 s - ProjectC.Infrastructure.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   938, Skipped:     0, Total:   938, Duration: 6 m 49 s - ProjectC.WebApi.Tests.dll (net10.0)`（Domain／Application／Infrastructure／Seeder 為修正前同次全套輸出，未受三個測試檔影響）
- [x] 4.3 `graphify update .`（輸出：`Code graph updated.`）
- [ ] 4.4 Task 完成後告知：spec 同步狀態、AC 涵蓋、重複邏輯（與 `CaptchaOptionsValidator` 的環境判斷是否值得抽共用）、命名一致性
