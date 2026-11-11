## AC 與測試對應

| AC | 測試 task |
|----|-----------|
| CAPTCHA-MODE-001 設定缺漏為 Enforced | 1.1（預設值）、1.3（缺漏時錯誤答案仍 400） |
| CAPTCHA-MODE-002 Enforced 行為不變 | 1.3(B)（login、register、join purchase queue 三流程各一，GETDEL 照常） |
| CAPTCHA-MODE-003 AcceptAny 任意答案通過 | 1.2、1.4 |
| CAPTCHA-MODE-004 產生端點不變 | 1.4 |
| CAPTCHA-MODE-005 欄位驗證與限流不繞過 | 1.4 |
| CAPTCHA-MODE-006 Production + AcceptAny 啟動失敗 | 1.5 |
| CAPTCHA-MODE-007 非 Production 可啟用並記 Warning | 1.6 |
| CAPTCHA-MODE-008 無效值擋下 | 1.5 |
| CAPTCHA-MODE-009 Enforced 不記 Warning | 1.6 |

## 1. 實作（依 TDD：先 RED 再 GREEN，證據逐字記錄於各 task 下）

通則：RED 必須是目標行為不符的斷言失敗，不得是編譯、DI 或環境錯誤，也不得以刻意錯誤的預設值或偽造行為的空殼製造。新型別不存在而使測試無法編譯時，只先建立不含目標行為的最小契約宣告（見 1.1、1.2），測試在該宣告上因行為缺失而失敗。所有命令皆在容器內執行：`docker compose exec api dotnet test tests/<專案> --filter "<名稱>"`。

- [x] 1.1 `CaptchaOptions` 新增 `VerificationMode` 與列舉 `CaptchaVerificationMode { Enforced = 1, AcceptAny = 2 }`；`CaptchaOptionsTests.Defaults_VerificationModeIsEnforced`。
  - 契約宣告：先只宣告列舉與屬性（`public CaptchaVerificationMode VerificationMode { get; set; }`，不寫初始值），列舉成員以非零值定義，使未設定的 `default` 不等於任何有效模式。
  - 預期 RED：`new CaptchaOptions().VerificationMode` 預期 `Enforced`，實際為 `0`（未定義、未設定預設）；斷言失敗，原因是「缺漏時預設 Enforced」尚未實作。
  - GREEN：屬性加上初始值 `Enforced`；`Application.Tests` 過濾 `CaptchaOptionsTests` 全數通過。
  - RED（還原驗證：以 mutation 移除屬性初始值，等同「契約宣告不寫初始值」）：
    ```
    mutation: { get; set; } = CaptchaVerificationMode.Enforced; -> { get; set; }
    $ docker compose exec api dotnet test tests/ProjectC.Application.Tests --filter CaptchaOptionsTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.41]     ProjectC.Application.Tests.Common.CaptchaOptionsTests.Defaults_VerificationModeIsEnforced [FAIL]
      Failed ProjectC.Application.Tests.Common.CaptchaOptionsTests.Defaults_VerificationModeIsEnforced [70 ms]
      Error Message:
       Expected options.VerificationMode to be CaptchaVerificationMode.Enforced {value: 1}, but found CaptchaVerificationMode.0 {value: 0}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Common.CaptchaOptionsTests.Defaults_VerificationModeIsEnforced() in /src/tests/ProjectC.Application.Tests/Common/CaptchaOptionsTests.cs:line 25
       at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
       at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)

    Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2, Duration: 115 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    （全文為命令實際輸出，僅 `verify-test-evidence.sh` 依其設計濾掉建置 warning 行，含堆疊追蹤）
  - GREEN（還原後 touch 重建）：
    ```
    $ docker compose exec api dotnet test tests/ProjectC.Application.Tests --filter CaptchaOptionsTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 30 ms - ProjectC.Application.Tests.dll (net10.0)
    ```
- [x] 1.2 新增 `AcceptAnyCaptchaService`（Infrastructure，建構子注入 `RedisCaptchaService`）；`AcceptAnyCaptchaServiceTests`（`RedisCaptchaService` 為 `sealed class` 無法直接 mock，一律以 mock `IConnectionMultiplexer`／`ICaptchaImageGenerator` 建構真實的 `RedisCaptchaService`）：(a) `VerifyAsync` 以任意 token 與答案預期通過，且內部 `RedisCaptchaService` 與 `IConnectionMultiplexer` 皆未被呼叫（「不碰 Redis」只針對 `VerifyAsync`）；(b) `GenerateAsync` 回傳的 `CaptchaChallenge` 與內部服務產生的是同一個結果，且內部 `GenerateAsync` 恰被呼叫一次。
  - 契約宣告：先只建立實作 `ICaptchaService` 的類別與建構子，`VerifyAsync`、`GenerateAsync` 方法本體皆為 `throw new NotImplementedException()`（不偽造任何行為）。
  - 預期 RED：(a) 預期通過、實際拋出 `NotImplementedException`；(b) 預期回傳內部服務的結果、實際拋出 `NotImplementedException`（目標行為尚未實作）。
  - GREEN：`VerifyAsync` 恆回通過；`GenerateAsync` 委派內部服務；`Infrastructure.Tests` 過濾 `AcceptAnyCaptchaServiceTests` 全數通過。
  - RED（還原驗證，一次一個改動：兩個方法各自改為 `throw new NotImplementedException()`，等同契約宣告）：
    ```
    mutation: => _innerService.GenerateAsync(cancellationToken); -> => throw new NotImplementedException();
    $ docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter AcceptAnyCaptchaServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.Infrastructure.Tests -> /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll
    Test run for /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.63]     ProjectC.Infrastructure.Tests.Captcha.AcceptAnyCaptchaServiceTests.GenerateAsync_DelegatesToInnerServiceOnce [FAIL]
      Failed ProjectC.Infrastructure.Tests.Captcha.AcceptAnyCaptchaServiceTests.GenerateAsync_DelegatesToInnerServiceOnce [15 ms]
      Error Message:
       System.NotImplementedException : The method or operation is not implemented.
      Stack Trace:
         at ProjectC.Infrastructure.Captcha.AcceptAnyCaptchaService.GenerateAsync(CancellationToken cancellationToken) in /src/src/ProjectC.Infrastructure/Captcha/AcceptAnyCaptchaService.cs:line 16
       at ProjectC.Infrastructure.Tests.Captcha.AcceptAnyCaptchaServiceTests.GenerateAsync_DelegatesToInnerServiceOnce() in /src/tests/ProjectC.Infrastructure.Tests/Captcha/AcceptAnyCaptchaServiceTests.cs:line 48
    --- End of stack trace from previous location ---

    Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2, Duration: 291 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
    結果：測試如預期失敗

    mutation: => Task.FromResult(true); -> => throw new NotImplementedException();
    $ docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter AcceptAnyCaptchaServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.Infrastructure.Tests -> /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll
    Test run for /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.62]     ProjectC.Infrastructure.Tests.Captcha.AcceptAnyCaptchaServiceTests.VerifyAsync_WithArbitraryTokenAndAnswer_ReturnsTrueWithoutTouchingRedis [FAIL]
      Failed ProjectC.Infrastructure.Tests.Captcha.AcceptAnyCaptchaServiceTests.VerifyAsync_WithArbitraryTokenAndAnswer_ReturnsTrueWithoutTouchingRedis [237 ms]
      Error Message:
       System.NotImplementedException : The method or operation is not implemented.
      Stack Trace:
         at ProjectC.Infrastructure.Captcha.AcceptAnyCaptchaService.VerifyAsync(String token, String answer, CancellationToken cancellationToken) in /src/src/ProjectC.Infrastructure/Captcha/AcceptAnyCaptchaService.cs:line 19
       at ProjectC.Infrastructure.Tests.Captcha.AcceptAnyCaptchaServiceTests.VerifyAsync_WithArbitraryTokenAndAnswer_ReturnsTrueWithoutTouchingRedis() in /src/tests/ProjectC.Infrastructure.Tests/Captcha/AcceptAnyCaptchaServiceTests.cs:line 31
    --- End of stack trace from previous location ---

    Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2, Duration: 308 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    （全文為命令實際輸出，僅 `verify-test-evidence.sh` 依其設計濾掉建置 warning 行，含堆疊追蹤）
  - GREEN：
    ```
    $ docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter AcceptAnyCaptchaServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.Infrastructure.Tests -> /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll
    Test run for /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 352 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
    ```
- [x] 1.0 測試基礎：新增 `CaptchaModeTestWebApplicationFactory`（`tests/ProjectC.WebApi.Tests/TestSupport`）。既有 `CustomWebApplicationFactory` 預設以 `FakeCaptchaService` 覆寫 `ICaptchaService`，`CaptchaComponentTestWebApplicationFactory` 則強制註冊 `RedisCaptchaService`，兩者都會繞過依模式選實作的工廠，不得用於 1.3(B)、1.4 與 1.2 以外的模式相關整合測試（否則測到的是替身，不是模式選擇）。新工廠 MUST 保留 `AddCaptcha` 的工廠註冊（移除 Fake 後以同一個註冊方法重新註冊，必要時把該註冊抽成共用擴充方法），並以 `UseSetting("Captcha:VerificationMode", …)` 控制模式。此 task 為測試基礎設施，TDD N/A（無獨立可觀察行為），由 1.3、1.4 的測試使用並驗證。
- [x] 1.3 `AddCaptcha` 以工廠依模式選實作。兩組測試：
  - (A) 選擇行為（RED 來源）：`CaptchaServiceSelectionTests`，以 `ServiceCollection` 加 `AddCaptcha` 後建立 scope 解析 `ICaptchaService`：`VerificationMode=AcceptAny` 預期為 `AcceptAnyCaptchaService`（其 `GenerateAsync` 的委派行為由 1.2(b) 與 1.4(b) 驗證）；`Enforced` 與設定缺漏預期為 `RedisCaptchaService`。
  - (B) Enforced 回歸保護：整合測試（Testing 環境，真實 Redis 容器）針對 **login、register、join purchase queue 三個流程各一**，設定缺漏與顯式 `Enforced` 時，以不存在的 token 或錯誤答案呼叫，預期 400 `CaptchaInvalid`，且 Redis 對應 key 已被 `GETDEL` 刪除（先寫入 token、用錯誤答案呼叫、事後該 key 不存在為斷言）。
  - 預期 RED（只針對 A）：factory 尚未實作時 `AddCaptcha` 固定註冊 `RedisCaptchaService`，`AcceptAny` 案例預期型別 `AcceptAnyCaptchaService`、實際為 `RedisCaptchaService`（斷言失敗）。
  - B 的 RED：N/A——這些是既有 Enforced 行為的回歸保護，在 factory 實作前即通過，不存在「目標行為缺失」的失敗；改以額外 mutation 驗證其敏感度（不取代 A 的 RED）：暫時把預設實作換成 `AcceptAnyCaptchaService`（`bash scripts/verify-test-evidence.sh mutate`，一次一個改動），預期三個流程各自因「預期 400 `CaptchaInvalid`、實際非 400」失敗，隨後還原並重跑通過，命令與輸出記錄於下。
  - GREEN：工廠依模式選擇且預設 `Enforced`；A、B 全數通過（`WebApi.Tests` 過濾 `CaptchaServiceSelection`、`CaptchaVerificationMode` 相關測試）。
  - RED（A）（還原驗證：工廠條件改為 `(CaptchaVerificationMode)99`）：
    ```
    mutation: options.VerificationMode == CaptchaVerificationMode.AcceptAny -> options.VerificationMode == (CaptchaVerificationMode)99
    $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter CaptchaServiceSelectionTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:01.10]     ProjectC.WebApi.Tests.Captcha.CaptchaServiceSelectionTests.ResolveCaptchaService_WhenModeIsAcceptAny_ReturnsAcceptAnyCaptchaService [FAIL]
      Failed ProjectC.WebApi.Tests.Captcha.CaptchaServiceSelectionTests.ResolveCaptchaService_WhenModeIsAcceptAny_ReturnsAcceptAnyCaptchaService [105 ms]
      Error Message:
       Expected type to be ProjectC.Infrastructure.Captcha.AcceptAnyCaptchaService, but found ProjectC.Infrastructure.Captcha.RedisCaptchaService.
      Stack Trace:
         at FluentAssertions.Types.TypeAssertions.Be(Type expected, String because, Object[] becauseArgs)
       at FluentAssertions.Primitives.ReferenceTypeAssertions`2.BeOfType(Type expectedType, String because, Object[] becauseArgs)
       at FluentAssertions.Primitives.ReferenceTypeAssertions`2.BeOfType[T](String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Captcha.CaptchaServiceSelectionTests.ResolveCaptchaService_WhenModeIsAcceptAny_ReturnsAcceptAnyCaptchaService() in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaServiceSelectionTests.cs:line 39
       at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
       at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)

    Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3, Duration: 608 ms - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    （全文為命令實際輸出，僅 `verify-test-evidence.sh` 依其設計濾掉建置 warning 行，含堆疊追蹤）
  - mutation（B）：
    ```
    mutation: : redisCaptchaService; -> : new AcceptAnyCaptchaService(redisCaptchaService);
    $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter CaptchaEnforcedModeTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:09.30]     ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.JoinPurchaseQueue_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(mode: "Enforced") [FAIL]
      Failed ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.JoinPurchaseQueue_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(mode: "Enforced") [3 s]
      Error Message:
       Expected response.StatusCode to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.Created {value: 201}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.AssertCaptchaInvalidAsync(HttpResponseMessage response) in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaEnforcedModeTests.cs:line 28
       at ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.JoinPurchaseQueue_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(String mode) in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaEnforcedModeTests.cs:line 85
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:10.92]     ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.JoinPurchaseQueue_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(mode: null) [FAIL]
      Failed ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.JoinPurchaseQueue_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(mode: null) [1 s]
      Error Message:
       Expected response.StatusCode to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.Created {value: 201}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.AssertCaptchaInvalidAsync(HttpResponseMessage response) in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaEnforcedModeTests.cs:line 28
       at ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.JoinPurchaseQueue_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(String mode) in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaEnforcedModeTests.cs:line 85
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:11.79]     ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.Login_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(mode: null) [FAIL]
      Failed ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.Login_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(mode: null) [859 ms]
      Error Message:
       Expected response.StatusCode to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.OK {value: 200}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.AssertCaptchaInvalidAsync(HttpResponseMessage response) in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaEnforcedModeTests.cs:line 28
       at ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.Login_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(String mode) in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaEnforcedModeTests.cs:line 47
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:12.61]     ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.Login_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(mode: "Enforced") [FAIL]
    [xUnit.net 00:00:13.06]     ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.Register_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(mode: "Enforced") [FAIL]
    [xUnit.net 00:00:13.51]     ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.Register_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(mode: null) [FAIL]
      Failed ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.Login_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(mode: "Enforced") [821 ms]
      Error Message:
       Expected response.StatusCode to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.OK {value: 200}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.AssertCaptchaInvalidAsync(HttpResponseMessage response) in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaEnforcedModeTests.cs:line 28
       at ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.Login_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(String mode) in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaEnforcedModeTests.cs:line 47
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.Register_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(mode: "Enforced") [449 ms]
      Error Message:
       Expected response.StatusCode to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.Created {value: 201}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.AssertCaptchaInvalidAsync(HttpResponseMessage response) in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaEnforcedModeTests.cs:line 28
       at ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.Register_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(String mode) in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaEnforcedModeTests.cs:line 63
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.Register_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(mode: null) [450 ms]
      Error Message:
       Expected response.StatusCode to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.Created {value: 201}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.AssertCaptchaInvalidAsync(HttpResponseMessage response) in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaEnforcedModeTests.cs:line 28
       at ProjectC.WebApi.Tests.Captcha.CaptchaEnforcedModeTests.Register_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(String mode) in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaEnforcedModeTests.cs:line 63
    --- End of stack trace from previous location ---

    Failed!  - Failed:     6, Passed:     0, Skipped:     0, Total:     6, Duration: 8 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    還原後第一次重跑曾失敗（容器內建置未偵測到還原的檔案時間），`touch` 該檔後通過。
  - GREEN：
    ```
    $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter CaptchaServiceSelectionTests|CaptchaEnforcedModeTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 11 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
- [x] 1.4 整合測試（`AcceptAny`，非 Production）：(a) login、register、join purchase queue 以未曾產生的 token 與任意非空答案通過核對。三個流程的其餘欄位 MUST 使 Validator 通過（Handler 先跑 Validator 才呼叫 `VerifyAsync`，Validator 失敗時回 `Validation`，不含 `CaptchaInvalid` 但也未證明核對通過），並斷言驗證碼核對之後的成功結果，不得只斷言「不是 `CaptchaInvalid`」：login 以正確帳密回 200；register 以未使用的有效 Email、顯示名稱與合規密碼註冊成功，且 DB 可查到該會員；join purchase queue 以有效會員、已開啟佇列模式的有效活動／票種送出，成功建立排隊項目且 DB 可查到。fixture 沿用既有測試資料（`tests/ProjectC.WebApi.Tests/Captcha/PurchaseQueueCaptchaTests.cs`、`TestSupport/RealNameTestData.cs` 等已能讓 Validator 通過的請求建構），不另造。成功斷言 MUST 取自「同一次 HTTP 請求」的回應（成功狀態碼，且回應不是 `Validation` 或 `CaptchaInvalid`），再以 DB 查證其副作用；不得以事前 setup 資料存在代替。同一組請求在 `Enforced` 下以同樣的錯誤 token 呼叫會回 400 `CaptchaInvalid`，作為對照以證明差異來自模式；(b) `GET /api/captcha` 仍回新 token 與 base64 圖片，Redis 寫入帶 TTL 的雜湊，其剩餘 TTL 大於 0 且不超過 `Captcha:TtlSeconds`；(c) token 空白或超過 64 字元、答案空白或超過 16 字元仍回 400 `Validation`（含逐欄位 `errors`）；(d) 同一來源 IP 連續請求 `GET /api/captcha`，前 N 次（N 為 `CaptchaRateLimiting:PermitLimit`）回 200，第 N+1 次回 429（以測試設定縮小額度與視窗）。
  - 預期 RED（只針對 (a)）：在 1.3 完成後、`AcceptAnyCaptchaService` 尚未被工廠選用前，(a) 預期不回 `CaptchaInvalid`、實際回 400 `CaptchaInvalid`。
  - (b)(c)(d) 的 RED：N/A——它們保護的是既有行為，在 `AcceptAny` 接線前即通過，沒有「目標行為缺失」的失敗；但須先於 `AcceptAny` 接線前加入並執行，記錄基線通過的命令與輸出（證明測試對既有行為有效），GREEN 接線後重跑仍通過。
  - 否證驗證（額外 mutation，`bash scripts/verify-test-evidence.sh mutate`，一次一個改動，還原後重跑）：(b) 讓 `GetCaptchaHandler` 不寫 Redis 雜湊 → 預期 (b) 因「Redis 無對應 key」失敗；(c) 暫時移除 token 長度上限規則 → 預期 (c) 因「預期 400 `Validation`、實際非 400」失敗；(d) 暫時關閉 `CaptchaRateLimiting` → 預期 (d) 因「預期 429、實際 200」失敗。各項命令與失敗輸出記錄於下。
  - GREEN：`AcceptAny` 接線後 (a)–(d) 全數通過；`WebApi.Tests` 過濾 `CaptchaAcceptAny` 相關測試通過。
  - RED（a）與基線（b）（c）（d）（偏離：1.3 已先完成工廠接線，以 mutation「工廠不選用 AcceptAny」重現接線前狀態；同一次執行中 (b)(c)(d) 的 8 個案例通過即基線）：
    ```
    mutation: options.VerificationMode == CaptchaVerificationMode.AcceptAny -> options.VerificationMode == (CaptchaVerificationMode)99
    $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter CaptchaAcceptAnyModeTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:06.59]     ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.Register_WithNeverGeneratedTokenAndAnyAnswer_Returns201AndCreatesMemberUnderAcceptAnyButCaptchaInvalidUnderEnforced [FAIL]
      Failed ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.Register_WithNeverGeneratedTokenAndAnyAnswer_Returns201AndCreatesMemberUnderAcceptAnyButCaptchaInvalidUnderEnforced [1 s]
      Error Message:
       Expected acceptAnyResponse.StatusCode to be HttpStatusCode.Created {value: 201}, but found HttpStatusCode.BadRequest {value: 400}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.Register_WithNeverGeneratedTokenAndAnyAnswer_Returns201AndCreatesMemberUnderAcceptAnyButCaptchaInvalidUnderEnforced() in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaAcceptAnyModeTests.cs:line 68
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:10.03]     ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.JoinPurchaseQueue_WithNeverGeneratedTokenAndAnyAnswer_Returns201AndCreatesEntryUnderAcceptAnyButCaptchaInvalidUnderEnforced [FAIL]
      Failed ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.JoinPurchaseQueue_WithNeverGeneratedTokenAndAnyAnswer_Returns201AndCreatesEntryUnderAcceptAnyButCaptchaInvalidUnderEnforced [3 s]
      Error Message:
       Expected acceptAnyResponse.StatusCode to be HttpStatusCode.Created {value: 201}, but found HttpStatusCode.BadRequest {value: 400}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.JoinPurchaseQueue_WithNeverGeneratedTokenAndAnyAnswer_Returns201AndCreatesEntryUnderAcceptAnyButCaptchaInvalidUnderEnforced() in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaAcceptAnyModeTests.cs:line 93
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:12.54]     ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.Login_WithNeverGeneratedTokenAndAnyAnswer_Returns200UnderAcceptAnyButCaptchaInvalidUnderEnforced [FAIL]
      Failed ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.Login_WithNeverGeneratedTokenAndAnyAnswer_Returns200UnderAcceptAnyButCaptchaInvalidUnderEnforced [720 ms]
      Error Message:
       Expected acceptAnyResponse.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.BadRequest {value: 400}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.Login_WithNeverGeneratedTokenAndAnyAnswer_Returns200UnderAcceptAnyButCaptchaInvalidUnderEnforced() in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaAcceptAnyModeTests.cs:line 52
    --- End of stack trace from previous location ---

    Failed!  - Failed:     3, Passed:     8, Skipped:     0, Total:    11, Duration: 7 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    （全文為命令實際輸出，僅 `verify-test-evidence.sh` 依其設計濾掉建置 warning 行，含堆疊追蹤）
  - mutation（b）（c）（d）（各一次一個改動；(b) 改動在 `RedisCaptchaService`，因 `GetCaptchaHandler` 不寫 Redis；(d) 以移除端點 `[EnableRateLimiting("captcha")]` 實作「關閉限流」）：
    ```
    mutation: await database.StringSetAsync(KeyPrefix + token, answerHash, TimeSpan.FromSeconds(_options.TtlSeconds)); -> await Task.CompletedTask;
    $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter CaptchaAcceptAnyModeTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:12.02]     ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.GetCaptcha_UnderAcceptAny_ReturnsTokenAndImageAndWritesKeyWithTtl [FAIL]
      Failed ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.GetCaptcha_UnderAcceptAny_ReturnsTokenAndImageAndWritesKeyWithTtl [162 ms]
      Error Message:
       Expected a value because Redis 必須有對應 key 且帶 TTL.
      Stack Trace:
         at FluentAssertions.Primitives.NullableSimpleTimeSpanAssertions`1.HaveValue(String because, Object[] becauseArgs)
       at FluentAssertions.Primitives.NullableSimpleTimeSpanAssertions`1.NotBeNull(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.GetCaptcha_UnderAcceptAny_ReturnsTokenAndImageAndWritesKeyWithTtl() in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaAcceptAnyModeTests.cs:line 116
    --- End of stack trace from previous location ---

    Failed!  - Failed:     1, Passed:    10, Skipped:     0, Total:    11, Duration: 7 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗

    mutation: .MaximumLength(64) -> .MaximumLength(6400)
    $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter CaptchaAcceptAnyModeTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:11.19]     ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.Login_UnderAcceptAnyWithInvalidCaptchaFields_Returns400ValidationWithFieldErrors(token: "tttttttttttttttttttttttttttttttttttttttttttttttttt"···, answer: "ABCD", expectedField: "captchaToken") [FAIL]
      Failed ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.Login_UnderAcceptAnyWithInvalidCaptchaFields_Returns400ValidationWithFieldErrors(token: "tttttttttttttttttttttttttttttttttttttttttttttttttt"···, answer: "ABCD", expectedField: "captchaToken") [209 ms]
      Error Message:
       Expected response.StatusCode to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.Unauthorized {value: 401}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.Login_UnderAcceptAnyWithInvalidCaptchaFields_Returns400ValidationWithFieldErrors(String token, String answer, String expectedField) in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaAcceptAnyModeTests.cs:line 139
    --- End of stack trace from previous location ---

    Failed!  - Failed:     1, Passed:    10, Skipped:     0, Total:    11, Duration: 7 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗

    mutation: [EnableRateLimiting("captcha")] -> /* removed */
    $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter CaptchaAcceptAnyModeTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:10.70]     ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.GetCaptcha_UnderAcceptAnyBeyondPermitLimit_Returns429 [FAIL]
      Failed ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.GetCaptcha_UnderAcceptAnyBeyondPermitLimit_Returns429 [635 ms]
      Error Message:
       Expected (client.GetAsync("/api/captcha")).StatusCode to be HttpStatusCode.TooManyRequests {value: 429}, but found HttpStatusCode.OK {value: 200}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Captcha.CaptchaAcceptAnyModeTests.GetCaptcha_UnderAcceptAnyBeyondPermitLimit_Returns429() in /src/tests/ProjectC.WebApi.Tests/Captcha/CaptchaAcceptAnyModeTests.cs:line 167
    --- End of stack trace from previous location ---

    Failed!  - Failed:     1, Passed:    10, Skipped:     0, Total:    11, Duration: 7 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
  - GREEN（各 mutation 還原後 touch 重建）：
    ```
    $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter CaptchaAcceptAnyModeTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11, Duration: 7 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
- [x] 1.5 `CaptchaOptionsValidator`（`IValidateOptions<CaptchaOptions>`，WebApi）；啟動測試（比照 `RateLimitingOptionsFailFastTests`）：(a) Production + `AcceptAny` 啟動拋 `OptionsValidationException`，訊息含 Production；(b) 環境名稱 `production`（小寫）同樣失敗；(c) `Captcha:VerificationMode=99` 啟動失敗；(d) 拼錯名稱（`acceptany1`）啟動失敗（斷言一般例外，不是 `OptionsValidationException`：ConfigurationBinder 在無驗證器時本就會拋例外，故 (d) 不作為 RED 依據，只作為回歸保護）；(e) Production + `Enforced` 正常啟動（防止驗證器誤擋）。
  - 預期 RED：驗證器未註冊時 (a)(b) 預期拋 `OptionsValidationException`、實際正常啟動；(c) 預期啟動失敗、實際以預設值啟動。
  - GREEN：註冊驗證器後 (a)–(e) 全數通過；`WebApi.Tests` 過濾 `CaptchaOptionsFailFast` 相關測試通過。
  - RED（還原驗證：註冊驗證器的一行改為註解；(d)(e) 通過符合預期：(d) 本就由 ConfigurationBinder 擋下、(e) 為防誤擋）：
    ```
    mutation: services.AddSingleton<IValidateOptions<CaptchaOptions>, CaptchaOptionsValidator>(); -> /* removed */
    $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter CaptchaOptionsFailFastTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:02.18]     ProjectC.WebApi.Tests.Startup.CaptchaOptionsFailFastTests.CreatingHost_InProductionWithAcceptAny_ThrowsOptionsValidationMentioningProduction(environmentName: "Production") [FAIL]
    [xUnit.net 00:00:02.35]     ProjectC.WebApi.Tests.Startup.CaptchaOptionsFailFastTests.CreatingHost_InProductionWithAcceptAny_ThrowsOptionsValidationMentioningProduction(environmentName: "production") [FAIL]
    [xUnit.net 00:00:02.50]     ProjectC.WebApi.Tests.Startup.CaptchaOptionsFailFastTests.CreatingHost_WithUndefinedNumericVerificationMode_ThrowsOptionsValidationException [FAIL]
      Failed ProjectC.WebApi.Tests.Startup.CaptchaOptionsFailFastTests.CreatingHost_InProductionWithAcceptAny_ThrowsOptionsValidationMentioningProduction(environmentName: "Production") [1 s]
      Error Message:
       Expected a <System.Exception> to be thrown, but no exception was thrown.
      Stack Trace:
         at FluentAssertions.Specialized.DelegateAssertionsBase`2.<>c__DisplayClass8_0`1.<ThrowInternal>b__0(AssertionChain chain)
       at FluentAssertions.Specialized.DelegateAssertionsBase`2.ThrowInternal[TException](Exception exception, String because, Object[] becauseArgs)
       at FluentAssertions.Specialized.DelegateAssertions`2.Throw[TException](String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Startup.CaptchaOptionsFailFastTests.CreatingHost_InProductionWithAcceptAny_ThrowsOptionsValidationMentioningProduction(String environmentName) in /src/tests/ProjectC.WebApi.Tests/Startup/CaptchaOptionsFailFastTests.cs:line 51
       at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
       at System.Reflection.MethodBaseInvoker.InvokeDirectByRefWithFewArgs(Object obj, Span`1 copyOfArgs, BindingFlags invokeAttr)
      Failed ProjectC.WebApi.Tests.Startup.CaptchaOptionsFailFastTests.CreatingHost_InProductionWithAcceptAny_ThrowsOptionsValidationMentioningProduction(environmentName: "production") [168 ms]
      Error Message:
       Expected a <System.Exception> to be thrown, but no exception was thrown.
      Stack Trace:
         at FluentAssertions.Specialized.DelegateAssertionsBase`2.<>c__DisplayClass8_0`1.<ThrowInternal>b__0(AssertionChain chain)
       at FluentAssertions.Specialized.DelegateAssertionsBase`2.ThrowInternal[TException](Exception exception, String because, Object[] becauseArgs)
       at FluentAssertions.Specialized.DelegateAssertions`2.Throw[TException](String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Startup.CaptchaOptionsFailFastTests.CreatingHost_InProductionWithAcceptAny_ThrowsOptionsValidationMentioningProduction(String environmentName) in /src/tests/ProjectC.WebApi.Tests/Startup/CaptchaOptionsFailFastTests.cs:line 51
       at InvokeStub_CaptchaOptionsFailFastTests.CreatingHost_InProductionWithAcceptAny_ThrowsOptionsValidationMentioningProduction(Object, Span`1)
       at System.Reflection.MethodBaseInvoker.InvokeWithOneArg(Object obj, BindingFlags invokeAttr, Binder binder, Object[] parameters, CultureInfo culture)
      Failed ProjectC.WebApi.Tests.Startup.CaptchaOptionsFailFastTests.CreatingHost_WithUndefinedNumericVerificationMode_ThrowsOptionsValidationException [64 ms]
      Error Message:
       Expected a <System.Exception> to be thrown, but no exception was thrown.
      Stack Trace:
         at FluentAssertions.Specialized.DelegateAssertionsBase`2.<>c__DisplayClass8_0`1.<ThrowInternal>b__0(AssertionChain chain)
       at FluentAssertions.Specialized.DelegateAssertionsBase`2.ThrowInternal[TException](Exception exception, String because, Object[] becauseArgs)
       at FluentAssertions.Specialized.DelegateAssertions`2.Throw[TException](String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Startup.CaptchaOptionsFailFastTests.CreatingHost_WithUndefinedNumericVerificationMode_ThrowsOptionsValidationException() in /src/tests/ProjectC.WebApi.Tests/Startup/CaptchaOptionsFailFastTests.cs:line 63
       at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
       at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)

    Failed!  - Failed:     3, Passed:     2, Skipped:     0, Total:     5, Duration: 1 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    （全文為命令實際輸出，僅 `verify-test-evidence.sh` 依其設計濾掉建置 warning 行，含堆疊追蹤）
  - GREEN（含 `CaptchaServiceSelectionTests`，已補 `IHostEnvironment`）：
    ```
    $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~CaptchaOptionsFailFastTests|FullyQualifiedName~CaptchaServiceSelectionTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8, Duration: 1 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
- [x] 1.6 `ResolveEagerlyValidatedOptions` 於 `AcceptAny` 記一筆 Warning；測試以記憶體 logger provider 擷取：Development 與 Testing + `AcceptAny` 正常啟動且恰有一筆 Warning（訊息含「驗證碼核對已停用」）；`Enforced` 與缺漏時無該 Warning。
  - 預期 RED：尚未記錄時，`AcceptAny` 案例預期恰 1 筆 Warning、實際 0 筆。
  - GREEN：記錄後全數通過，`Enforced`／缺漏案例仍為 0 筆。
  - 註：測試第一版以自訂 `ILoggerProvider` 擷取，因專案以 Serilog 取代預設 provider 而收不到日誌，改用既有 DI `ILogEventSink`（`InMemoryLogEventSink`）。
  - RED（還原驗證：Warning 條件改為 `false`）：
    ```
    mutation: captchaOptions.VerificationMode == CaptchaVerificationMode.AcceptAny -> false
    $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~CaptchaAcceptAnyWarningTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:02.61]     ProjectC.WebApi.Tests.Startup.CaptchaAcceptAnyWarningTests.StartingHost_InNonProductionWithAcceptAny_LogsExactlyOneDisabledWarning(environmentName: "Development") [FAIL]
    [xUnit.net 00:00:02.68]     ProjectC.WebApi.Tests.Startup.CaptchaAcceptAnyWarningTests.StartingHost_InNonProductionWithAcceptAny_LogsExactlyOneDisabledWarning(environmentName: "Testing") [FAIL]
      Failed ProjectC.WebApi.Tests.Startup.CaptchaAcceptAnyWarningTests.StartingHost_InNonProductionWithAcceptAny_LogsExactlyOneDisabledWarning(environmentName: "Development") [1 s]
      Error Message:
       Expected warnings to contain a single item, but the collection is empty.
      Stack Trace:
         at FluentAssertions.Collections.GenericCollectionAssertions`3.ContainSingle(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Startup.CaptchaAcceptAnyWarningTests.StartingHost_InNonProductionWithAcceptAny_LogsExactlyOneDisabledWarning(String environmentName) in /src/tests/ProjectC.WebApi.Tests/Startup/CaptchaAcceptAnyWarningTests.cs:line 56
       at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
       at System.Reflection.MethodBaseInvoker.InvokeDirectByRefWithFewArgs(Object obj, Span`1 copyOfArgs, BindingFlags invokeAttr)
      Failed ProjectC.WebApi.Tests.Startup.CaptchaAcceptAnyWarningTests.StartingHost_InNonProductionWithAcceptAny_LogsExactlyOneDisabledWarning(environmentName: "Testing") [68 ms]
      Error Message:
       Expected warnings to contain a single item, but the collection is empty.
      Stack Trace:
         at FluentAssertions.Collections.GenericCollectionAssertions`3.ContainSingle(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Startup.CaptchaAcceptAnyWarningTests.StartingHost_InNonProductionWithAcceptAny_LogsExactlyOneDisabledWarning(String environmentName) in /src/tests/ProjectC.WebApi.Tests/Startup/CaptchaAcceptAnyWarningTests.cs:line 56
       at InvokeStub_CaptchaAcceptAnyWarningTests.StartingHost_InNonProductionWithAcceptAny_LogsExactlyOneDisabledWarning(Object, Span`1)
       at System.Reflection.MethodBaseInvoker.InvokeWithOneArg(Object obj, BindingFlags invokeAttr, Binder binder, Object[] parameters, CultureInfo culture)

    Failed!  - Failed:     2, Passed:     2, Skipped:     0, Total:     4, Duration: 2 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
    （全文為命令實際輸出，僅 `verify-test-evidence.sh` 依其設計濾掉建置 warning 行，含堆疊追蹤）
  - GREEN：
    ```
    $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~CaptchaAcceptAnyWarningTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 2 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
- [x] 1.7 重構後重跑 1.1–1.6 相關測試（或標 N/A 並說明）。
  - 重構：有（把 `CaptchaModeTestData` 與 `PurchaseQueueCaptchaTests` 重複的私有佇列活動 seed／會員 client helper 合併，`PurchaseQueueCaptchaTests` 改用共用 `CaptchaModeTestData`）。重構後重跑全部 Captcha 相關測試（命令與輸出原文，僅濾掉建置 warning 行）：
    ```
    $ docker compose exec api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~Captcha
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:    25, Skipped:     0, Total:    25, Duration: 249 ms - ProjectC.Application.Tests.dll (net10.0)

    $ docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter FullyQualifiedName~Captcha
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.Infrastructure.Tests -> /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll
    Test run for /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:    22, Skipped:     0, Total:    22, Duration: 2 s - ProjectC.Infrastructure.Tests.dll (net10.0)

    $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~Captcha
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:    56, Skipped:     0, Total:    56, Duration: 26 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
## 2. 同步與收尾

- [x] 2.1 更新 `tests/ProjectC.WebApi.Tests/Startup/ServiceRegistrationSnapshot.baseline.txt`（`ICaptchaService` 註冊、新增驗證器）；快照測試通過。
  - 既有快照測試（TDD N/A：非新行為，基準檔隨 DI 變更同步）。
  - 基準檔更新前（還原驗證：`verify-test-evidence.sh restore` 暫時移開基準檔修改）：
    ```
    $ git stash push -u -- tests/ProjectC.WebApi.Tests/Startup/ServiceRegistrationSnapshot.baseline.txt
    warning: in the working copy of 'tests/ProjectC.WebApi.Tests/Startup/ServiceRegistrationSnapshot.baseline.txt', LF will be replaced by CRLF the next time Git touches it
    Saved working directory and index state WIP on feature/captcha-test-mode: f19c06d captcha-test-mode：依 spec-reviewer warning 修正 MODE-001 措辭、1.2 mock 做法與 tasks 數量
    warning: in the working copy of 'tests/ProjectC.WebApi.Tests/Startup/ServiceRegistrationSnapshot.baseline.txt', LF will be replaced by CRLF the next time Git touches it
    $ git stash list
    stash@{0}: WIP on feature/captcha-test-mode: f19c06d captcha-test-mode：依 spec-reviewer warning 修正 MODE-001 措辭、1.2 mock 做法與 tasks 數量
    $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~ServiceRegistrationSnapshot
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:01.04]     ProjectC.WebApi.Tests.Startup.ServiceRegistrationSnapshotTests.ServiceRegistrations_MatchCommittedBaseline [FAIL]
      Failed ProjectC.WebApi.Tests.Startup.ServiceRegistrationSnapshotTests.ServiceRegistrations_MatchCommittedBaseline [516 ms]
      Error Message:
       Expected (missing.Count + unexpected.Count) to be 0 because DI 圖與基準檔不一致。缺少的項目：Scoped | ProjectC.Application.Common.Interfaces.ICaptchaService | ProjectC.Infrastructure.Captcha.RedisCaptchaService；多出的項目：Scoped | ProjectC.Application.Common.Interfaces.ICaptchaService | factory || Scoped | ProjectC.Infrastructure.Captcha.RedisCaptchaService | ProjectC.Infrastructure.Captcha.RedisCaptchaService || Singleton | Microsoft.Extensions.Options.IValidateOptions`1[ProjectC.Application.Common.CaptchaOptions] | ProjectC.WebApi.Extensions.CaptchaOptionsValidator, but found 4 (difference of 4).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Startup.ServiceRegistrationSnapshotTests.ServiceRegistrations_MatchCommittedBaseline() in /src/tests/ProjectC.WebApi.Tests/Startup/ServiceRegistrationSnapshotTests.cs:line 40
       at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
       at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)

    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 525 ms - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    $ git stash pop
    On branch feature/captcha-test-mode
    Changes not staged for commit:
      (use "git add <file>..." to update what will be committed)
      (use "git restore <file>..." to discard changes in working directory)
    	modified:   docs/todo/2026-11-10-captcha-test-switch-todo.md
    	modified:   docs/todo/2026-11-10-deployment-env-vars.md
    	modified:   openspec/changes/captcha-test-mode/tasks.md
    	modified:   src/ProjectC.Application/Common/CaptchaOptions.cs
    	modified:   src/ProjectC.WebApi/Extensions/CaptchaServiceCollectionExtensions.cs
    	modified:   src/ProjectC.WebApi/Extensions/EagerOptionsValidationExtensions.cs
    	modified:   tests/ProjectC.Application.Tests/Common/CaptchaOptionsTests.cs
    	modified:   tests/ProjectC.WebApi.Tests/Captcha/PurchaseQueueCaptchaTests.cs
    	modified:   tests/ProjectC.WebApi.Tests/Startup/ServiceRegistrationSnapshot.baseline.txt

    Untracked files:
      (use "git add <file>..." to include in what will be committed)
    	src/ProjectC.Infrastructure/Captcha/AcceptAnyCaptchaService.cs
    	src/ProjectC.WebApi/Extensions/CaptchaOptionsValidator.cs
    	tests/ProjectC.Infrastructure.Tests/Captcha/AcceptAnyCaptchaServiceTests.cs
    	tests/ProjectC.WebApi.Tests/Captcha/CaptchaAcceptAnyModeTests.cs
    	tests/ProjectC.WebApi.Tests/Captcha/CaptchaEnforcedModeTests.cs
    	tests/ProjectC.WebApi.Tests/Captcha/CaptchaServiceSelectionTests.cs
    	tests/ProjectC.WebApi.Tests/Startup/CaptchaAcceptAnyWarningTests.cs
    	tests/ProjectC.WebApi.Tests/Startup/CaptchaOptionsFailFastTests.cs
    	tests/ProjectC.WebApi.Tests/TestSupport/CaptchaModeTestData.cs
    	tests/ProjectC.WebApi.Tests/TestSupport/CaptchaModeTestWebApplicationFactory.cs

    no changes added to commit (use "git add" and/or "git commit -a")
    Dropped refs/stash@{0} (24106a179f2cba0f3abdd5148bac23d9b24cd472)
    ```
    （全文為命令實際輸出，僅 `verify-test-evidence.sh` 依其設計濾掉建置 warning 行，含堆疊追蹤）
  - 更新後：
    ```
    $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~ServiceRegistrationSnapshot
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 391 ms - ProjectC.WebApi.Tests.dll (net10.0)
    ```
- [x] 2.2 更新 `docs/todo/2026-11-10-deployment-env-vars.md`（`Captcha__VerificationMode`：僅限非 Production、部署預設不設）；執行 `bash scripts/check-doc-paths.sh`。
  - 輸出原文：
    ```
    $ bash scripts/check-doc-paths.sh
    OK: 所有 docs 路徑引用都存在
    [exit 0]
    ```
- [x] 2.3 將 `docs/todo/2026-11-10-captcha-test-switch-todo.md` 前兩項標為完成（第三項 K8s 3.3 重跑另做）。
- [x] 2.4 hardener 最終檢查：本 change 只動設定驗證、DI 註冊與無 I/O 的 `AcceptAnyCaptchaService`，預期 N/A；實作完成後確認並記錄理由。
  - 結果：N/A。變更只含設定列舉與驗證器（無 I/O，純同步檢查）、DI 工廠選擇、以及 `AcceptAnyCaptchaService`（`VerifyAsync` 恆回傳 true、`GenerateAsync` 委派既有 `RedisCaptchaService`，後者的 I/O 防禦分支未改動）。Redis 呼叫、交易、鎖、外部輸入處理皆未新增；`docs/api-conventions.md` 無設定鍵清單，不需同步。
- [x] 2.5 全套測試（各測試專案）逐字貼摘要行。
  - 重構後全套測試（命令與輸出原文，僅濾掉建置 warning 行）：
    ```
    $ docker compose exec api dotnet test tests/ProjectC.Domain.Tests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Domain.Tests -> /src/tests/ProjectC.Domain.Tests/bin/Debug/net10.0/ProjectC.Domain.Tests.dll
    Test run for /src/tests/ProjectC.Domain.Tests/bin/Debug/net10.0/ProjectC.Domain.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:   153, Skipped:     0, Total:   153, Duration: 153 ms - ProjectC.Domain.Tests.dll (net10.0)

    $ docker compose exec api dotnet test tests/ProjectC.Application.Tests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:   625, Skipped:     0, Total:   625, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)

    $ docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.Infrastructure.Tests -> /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll
    Test run for /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:   282, Skipped:     0, Total:   282, Duration: 57 s - ProjectC.Infrastructure.Tests.dll (net10.0)

    $ docker compose exec api dotnet test tests/ProjectC.LoadTest.Seeder.Tests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.LoadTest.Seeder -> /src/tools/ProjectC.LoadTest.Seeder/bin/Debug/net10.0/ProjectC.LoadTest.Seeder.dll
      ProjectC.LoadTest.Seeder.Tests -> /src/tests/ProjectC.LoadTest.Seeder.Tests/bin/Debug/net10.0/ProjectC.LoadTest.Seeder.Tests.dll
    Test run for /src/tests/ProjectC.LoadTest.Seeder.Tests/bin/Debug/net10.0/ProjectC.LoadTest.Seeder.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 12 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)

    $ docker compose exec api dotnet test tests/ProjectC.WebApi.Tests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.

    Passed!  - Failed:     0, Passed:   842, Skipped:     0, Total:   842, Duration: 4 m 30 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
- [x] 2.6 `graphify update .`。

## 外部交叉審查紀錄

- 第 1 輪：Devin CLI（smart，GPT-6 Luna）審 c4d05f8 → FAIL，B-001（證據濾掉建置 warning）。自行驗證：濾除為 `verify-test-evidence.sh` 既有設計，被濾行僅 MSB3277（與 captcha 無關），無可重現的失敗情境，判定不成立；已於 design.md 記錄，commit cfe265f。
- 第 2 輪：Devin CLI 審 cfe265f（附 B-001 核對結果）→ PASS，無 blocking。
- 第 3 輪：Claude Code Opus（strict-reviewer）審 cfe265f → PASS，無 blocking；建議：Production 以外環境別名（如 Staging）可開 AcceptAny（design 已接受）、validator 白名單為可選 hardening（不採用，威脅模型外）。

