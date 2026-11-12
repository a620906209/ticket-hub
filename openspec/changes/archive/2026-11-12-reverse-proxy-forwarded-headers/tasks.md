# Tasks

> 紀錄欄位依 CLAUDE.md「測試規範」：RED／GREEN／重構／還原驗證貼實際輸出。指令皆在容器內執行（`docker compose exec api dotnet test …`）。

## 1. 轉送標頭還原（client-address-resolution）

- [x] 1.1 `ForwardedHeaders` 設定類別與 CIDR 解析（對應 CAR-005、CAR-006、CAR-007）
  - 測試先行：`ForwardedHeadersSettingsTests`（單元）——預設網段內容；自訂網段取代預設；非法 CIDR、空陣列拋出含 `ForwardedHeaders:TrustedNetworks` 的例外。
  - 空陣列與缺漏的區分：以設定鍵是否存在（`GetSection(...).Exists()`）判斷；空字串項目視為非法。實作時對照已安裝套件驗證陣列綁定行為。
  - 預期 RED：先建立最小骨架使測試可編譯，斷言失敗（預設網段為空、未驗證非法值）。
  - GREEN：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ForwardedHeadersSettings"` 全過。
  - 實作前先 build 並檢查已安裝套件的 `ForwardedHeadersOptions` 實際屬性（`KnownIPNetworks`／`KnownNetworks`），以實際為準。
  - 紀錄（1.1）：
    - 套件核對：.NET 10 `ForwardedHeadersOptions` 以 `KnownIPNetworks`（`IList<System.Net.IPNetwork>`）為準，`KnownNetworks` 已過時；實作見 1.2。`System.Net.IPNetwork.TryParse` 要求含 `/` 前綴且主機位為 0（`10.0.0.0`、`10.0.0.0/33` 皆非法）。JSON `"TrustedNetworks": []` 解析後設定鍵存在（`Exists()` 為 true）、無子項，被判為明確空陣列而拋出（測試 `FromConfiguration_WithExplicitEmptyArrayInJson_Throws` 通過為證）。
    - RED（正式碼僅有回傳空網段的骨架 `ForwardedHeadersSettings.FromConfiguration => new()`，測試可編譯、因目標行為不符而失敗）：
      命令：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ForwardedHeadersSettings"`
      ```
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersSettingsTests.FromConfiguration_WhenKeyMissing_TrustsPrivateNetworksAndLoopback(address: "172.31.255.254")
         Expected IsTrusted(settings, address) to be True, but found False.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersSettingsTests.FromConfiguration_WhenKeyMissing_TrustsPrivateNetworksAndLoopback(address: "::1")
         Expected IsTrusted(settings, address) to be True, but found False.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersSettingsTests.FromConfiguration_WhenKeyMissing_TrustsPrivateNetworksAndLoopback(address: "127.0.0.1")
         Expected IsTrusted(settings, address) to be True, but found False.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersSettingsTests.FromConfiguration_WhenKeyMissing_TrustsPrivateNetworksAndLoopback(address: "172.16.0.1")
         Expected IsTrusted(settings, address) to be True, but found False.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersSettingsTests.FromConfiguration_WhenKeyMissing_TrustsPrivateNetworksAndLoopback(address: "192.168.1.5")
         Expected IsTrusted(settings, address) to be True, but found False.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersSettingsTests.FromConfiguration_WhenKeyMissing_TrustsPrivateNetworksAndLoopback(address: "10.2.3.4")
         Expected IsTrusted(settings, address) to be True, but found False.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersSettingsTests.FromConfiguration_WithCustomNetworks_ReplacesDefaults
         Expected IsTrusted(settings, "10.1.2.3") to be True, but found False.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersSettingsTests.FromConfiguration_WithInvalidEntry_DoesNotSilentlySkipItAndKeepTheRest
         Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersSettingsTests.FromConfiguration_WithInvalidEntry_ThrowsNamingTheConfigurationKey(invalidEntry: "10.0.0.0")
         Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersSettingsTests.FromConfiguration_WithInvalidEntry_ThrowsNamingTheConfigurationKey(invalidEntry: "10.0.0.0/33")
         Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersSettingsTests.FromConfiguration_WithInvalidEntry_ThrowsNamingTheConfigurationKey(invalidEntry: "")
         Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersSettingsTests.FromConfiguration_WithInvalidEntry_ThrowsNamingTheConfigurationKey(invalidEntry: "not-a-cidr")
         Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersSettingsTests.FromConfiguration_WithInvalidEntry_ThrowsNamingTheConfigurationKey(invalidEntry: "   ")
         Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersSettingsTests.FromConfiguration_WithExplicitEmptyArrayInJson_Throws
         Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersSettingsTests.FromConfiguration_WithEmptyStringValue_Throws
         Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown.
      Failed!  - Failed:    15, Passed:     3, Skipped:     0, Total:    18, Duration: 82 ms - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - GREEN（實作後同一命令）：
      ```
      Passed!  - Failed:     0, Passed:    18, Skipped:     0, Total:    18, Duration: 61 ms - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - 重構：N/A（實作一次到位）。
- [x] 1.2 `UseForwardedHeaders` 掛入 pipeline 並在啟動時解析設定（對應 CAR-001～004、CAR-006～013）
  - 測試先行：整合測試以測試 middleware 模擬連線來源（受信任／非信任），帶 `X-Forwarded-For`／`X-Forwarded-Proto`，經由回報 `RemoteIpAddress`／`Scheme` 的測試端點觀察；`X-Forwarded-For: a, b` 取最後一個；`/health/live` 帶 `X-Forwarded-Proto: http` 不被轉址；不帶 `X-Forwarded-For` 時維持連線位址（CAR-004）；非信任來源帶標頭被忽略（CAR-002、CAR-010 非信任部分）；受信任來源 `X-Forwarded-Proto: https` 還原 Scheme（CAR-010）；受信任來源最後值無法解析的 `X-Forwarded-For`（`garbage`、空字串、超長字串、`203.0.113.7, garbage`）不失敗且 `RemoteIpAddress` 維持連線位址、落入同一限流分區（CAR-009）；`garbage, 203.0.113.7` 與 `203.0.113.7:8080` 先實測框架行為再定預期（CAR-011）；CAR-008 與 CAR-013 的測試須設定 HTTPS 轉址連接埠（沿用 `HealthCheckHttpsRedirectionTests` 的 `HttpsPort = "443"` 設定），並含對照斷言：無標頭／非信任來源對 `GET /api/events` 確實被轉址、`/health/live` 帶 `X-Forwarded-Proto: http` 不被轉址，避免未設連接埠時空轉；`X-Forwarded-Host` 不改 `Request.Host`（CAR-012）；自訂網段下 `192.168.1.5` 來源標頭被忽略（CAR-006）；以 `WithWebHostBuilder` 驗證非法 CIDR／空陣列啟動失敗（CAR-007）；記錄 IPv4-mapped IPv6 來源（`::ffff:10.x.x.x`）是否視為受信任的實測結果。
  - 預期 RED：未註冊 middleware 時，受信任來源的標頭未被還原（Expected `203.0.113.7` but found 模擬連線位址）。
  - GREEN：相關測試通過；`ResolveEagerlyValidatedOptions` 啟動即解析（非法設定啟動失敗，由 1.1 覆蓋）。
  - 紀錄（1.2）：
    - 套件核對：`ForwardedHeadersOptions.KnownIPNetworks`（.NET 10，容器 SDK/runtime 10.0.12）可用並採用；實作先 `KnownProxies.Clear()`、`KnownIPNetworks.Clear()` 再加入設定網段。設定以 `AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>(...)` 延遲到選項解析時讀取（取得最終合併設定，與測試以 `ConfigureAppConfiguration` 覆寫的方式相容），`ResolveEagerlyValidatedOptions` 強制解析使非法設定啟動即失敗。
    - 實測框架行為（測試先以預期值撰寫，GREEN 通過即為實測結果）：CAR-011 `garbage, 203.0.113.7` 還原為 `203.0.113.7`；`203.0.113.7:8080` 還原為 `203.0.113.7`；兩者與 spec 預期一致，不需修改 CAR-011。CAR-009 的 `garbage`、空字串、5000 字元字串、`203.0.113.7, garbage` 皆回 200 且 `RemoteIpAddress` 維持連線位址（`10.0.0.5`）。IPv4-mapped IPv6 來源 `::ffff:10.0.0.5` 被視為受信任（可還原標頭）。
    - 測試設施：`ForwardedHeadersWebApplicationFactory`（以 `IStartupFilter` 在 `UseForwardedHeaders` 之前依測試標頭設定 `Connection.RemoteIpAddress`，並以 `ApplicationPart` 加入回報 `RemoteIpAddress`／`Scheme`／`Host` 的測試端點），不在正式碼暴露開關。
    - RED（正式碼未呼叫 `UseForwardedHeaders`、未註冊設定；測試檔與測試設施已存在且可編譯）：
      命令：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ForwardedHeadersMiddlewareTests"`
      ```
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersMiddlewareTests.Request_FromTrustedSourceWithMultipleForwardedFor_UsesOnlyTheLastValue
         Expected probe.RemoteIpAddress to be a match with the expectation, but it differs at index 0:
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersMiddlewareTests.CreatingHost_WithInvalidTrustedNetworksEntry_FailsNamingTheConfigurationKey(entry: "not-a-cidr")
         Expected a <System.Exception> to be thrown, but no exception was thrown.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersMiddlewareTests.CreatingHost_WithInvalidTrustedNetworksEntry_FailsNamingTheConfigurationKey(entry: "")
         Expected a <System.Exception> to be thrown, but no exception was thrown.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersMiddlewareTests.Request_FromIpv4MappedIpv6TrustedSource_IsTreatedAsTrusted
         Expected probe.RemoteIpAddress to be a match with the expectation, but it differs at index 0:
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersMiddlewareTests.HttpsRedirection_ForTrustedSourceWithForwardedProtoHttps_IsNotRedirected
         Expected IsRedirect(response) to be False, but found True.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersMiddlewareTests.Request_FromTrustedSourceWithAddressAndPort_RestoresTheAddress
         Expected probe.RemoteIpAddress to be a match with the expectation, but it differs at index 0:
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersMiddlewareTests.CreatingHost_WithExplicitlyEmptyTrustedNetworks_FailsNamingTheConfigurationKey
         Expected a <System.Exception> to be thrown, but no exception was thrown.
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersMiddlewareTests.Request_FromTrustedSourceWithLeadingGarbageAndValidLastValue_RestoresTheLastValue
         Expected probe.RemoteIpAddress to be a match with the expectation, but it differs at index 0:
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersMiddlewareTests.Request_FromTrustedSourceWithForwardedFor_RestoresClientAddress
         Expected probe.RemoteIpAddress to be a match with the expectation, but it differs at index 0:
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersMiddlewareTests.Request_WithCustomTrustedNetworks_IgnoresHeaderFromDefaultNetworkOutsideTheCustomList
         Expected inside.RemoteIpAddress to be a match with the expectation because 對照：自訂網段內的來源仍會被還原, but it differs at index 0:
        Failed ProjectC.WebApi.Tests.ForwardedHeaders.ForwardedHeadersMiddlewareTests.Request_FromTrustedSourceWithForwardedProtoHttps_RestoresScheme
         Expected probe.Scheme to be "https" with a length of 5, but "http" has a length of 4, differs near "p" (index 3).
      Failed!  - Failed:    11, Passed:    11, Skipped:     0, Total:    22, Duration: 1 s - ProjectC.WebApi.Tests.dll (net10.0)
      （上列「differs at index 0」的實際值／預期值，以 `Request_FromTrustedSourceWithForwardedFor_RestoresClientAddress` 為例：actual `"10.0.0.5"`、expected `"203.0.113.7"`；其餘同型。）
      ```
      另有 11 項在未註冊 middleware 時即通過（CAR-002、CAR-004、CAR-009、CAR-012、CAR-010 非信任部分、CAR-008 兩個探針、轉址對照組；共 11 項），屬防護性斷言，不產生 RED。
    - GREEN（實作後；含 1.1 的設定測試）：命令 `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ForwardedHeaders"`
      ```
      Passed!  - Failed:     0, Passed:    40, Skipped:     0, Total:    40, Duration: 3 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - 重構：N/A。

## 2. 限流（api-rate-limiting）

- [x] 2.1 `PublicReadRateLimitingOptions` 與 `public-read` policy（對應 PRL-005、PRL-006）
  - 測試先行：options 預設 120／60、`Range` 驗證擋 0／負數、啟動時 `OptionsValidationException`（比照 `CaptchaRateLimitingOptions` 既有測試）。
  - 預期 RED：型別不存在／預設值與驗證不符。
  - GREEN：對應測試通過；`appsettings.json` 補 `PublicReadRateLimiting` 預設區段。
  - 紀錄（2.1）：
    - RED（正式碼僅有無預設值、無 `Range`、未註冊的 `PublicReadRateLimitingOptions` 骨架）：命令 `docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~PublicReadRateLimitingOptions"` 與 `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~CreatingHost_WithNonPositivePublicRead"`
      ```
        Failed ProjectC.Application.Tests.Common.PublicReadRateLimitingOptionsTests.Defaults_ArePermitLimit120AndWindow60Seconds
         Expected options.PermitLimit to be 120, but found 0 (difference of -120).
        Failed ProjectC.Application.Tests.Common.PublicReadRateLimitingOptionsTests.Validation_WithNonPositiveValues_Fails(permitLimit: 120, windowSeconds: 0)
         Expected isValid to be False, but found True.
        Failed ProjectC.Application.Tests.Common.PublicReadRateLimitingOptionsTests.Validation_WithNonPositiveValues_Fails(permitLimit: -1, windowSeconds: 60)
         Expected isValid to be False, but found True.
        Failed ProjectC.Application.Tests.Common.PublicReadRateLimitingOptionsTests.Validation_WithNonPositiveValues_Fails(permitLimit: 120, windowSeconds: -1)
         Expected isValid to be False, but found True.
        Failed ProjectC.Application.Tests.Common.PublicReadRateLimitingOptionsTests.Validation_WithNonPositiveValues_Fails(permitLimit: 0, windowSeconds: 60)
         Expected isValid to be False, but found True.
      Failed!  - Failed:     5, Passed:     1, Skipped:     0, Total:     6, Duration: 52 ms - ProjectC.Application.Tests.dll (net10.0)
        Failed ProjectC.WebApi.Tests.Startup.RateLimitingOptionsFailFastTests.CreatingHost_WithNonPositivePublicReadRateLimitingValues_ThrowsOptionsValidationException(permitLimit: -1, windowSeconds: 60)
         Expected a <System.Exception> to be thrown, but no exception was thrown.
        Failed ProjectC.WebApi.Tests.Startup.RateLimitingOptionsFailFastTests.CreatingHost_WithNonPositivePublicReadRateLimitingValues_ThrowsOptionsValidationException(permitLimit: 120, windowSeconds: 0)
         Expected a <System.Exception> to be thrown, but no exception was thrown.
        Failed ProjectC.WebApi.Tests.Startup.RateLimitingOptionsFailFastTests.CreatingHost_WithNonPositivePublicReadRateLimitingValues_ThrowsOptionsValidationException(permitLimit: 0, windowSeconds: 60)
         Expected a <System.Exception> to be thrown, but no exception was thrown.
        Failed ProjectC.WebApi.Tests.Startup.RateLimitingOptionsFailFastTests.CreatingHost_WithNonPositivePublicReadRateLimitingValues_ThrowsOptionsValidationException(permitLimit: 120, windowSeconds: -1)
         Expected a <System.Exception> to be thrown, but no exception was thrown.
      Failed!  - Failed:     4, Passed:     0, Skipped:     0, Total:     4, Duration: 542 ms - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - GREEN（實作：`Range(1, int.MaxValue)`、預設 120／60、`AddRateLimiting` 註冊選項與 `public-read` policy、`ResolveEagerlyValidatedOptions` 強制解析、`appsettings.json` 補預設區段）：
      ```
      Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 21 ms - ProjectC.Application.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 252 ms - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - 重構：N/A。`public-read` policy 於本 task 一併註冊（無 controller 使用前不影響行為），套用見 2.2。
- [x] 2.2 `EventsController` 四個 GET 套用 `public-read`（對應 PRL-001～004）
  - 測試先行：整合測試（低額度測試設定）——超額 429＋`Retry-After`＋`ProblemDetails`；不同 `X-Forwarded-For`（受信任模擬）各自計數；超額後 `GET /api/captcha`、`POST /api/auth/login`、`/health/live` 不受影響；四個端點共用額度（列表打滿後詳情／seats／ticket-types 也 429）；429 時查詢未執行（以 fake／spy 的呼叫次數斷言，不讀快取）。
  - 預期 RED：未套用 policy 時超額請求仍回 200（Expected 429 but found 200）。
  - GREEN：相關測試通過；既有 `EventsController` 測試不退步。
  - 紀錄（2.2）：
    - RED（`EventsController` 尚未套用 policy；共用 factory 設 `PublicReadRateLimiting` 3 次／600 秒，各測試用互不重複的用戶端 IP）：命令 `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~PublicReadRateLimitingTests"`
      ```
        Failed ProjectC.WebApi.Tests.Events.PublicReadRateLimitingTests.OtherPublicEventEndpoints_AfterListExhaustedQuota_ShareTheSameQuota(endpoint: "detail")
         Expected response.StatusCode to be HttpStatusCode.TooManyRequests {value: 429}, but found HttpStatusCode.NotFound {value: 404}.
        Failed ProjectC.WebApi.Tests.Events.PublicReadRateLimitingTests.OtherPublicEventEndpoints_AfterListExhaustedQuota_ShareTheSameQuota(endpoint: "seats")
         Expected response.StatusCode to be HttpStatusCode.TooManyRequests {value: 429}, but found HttpStatusCode.NotFound {value: 404}.
        Failed ProjectC.WebApi.Tests.Events.PublicReadRateLimitingTests.OtherPublicEventEndpoints_AfterListExhaustedQuota_ShareTheSameQuota(endpoint: "ticket-types")
         Expected response.StatusCode to be HttpStatusCode.TooManyRequests {value: 429}, but found HttpStatusCode.NotFound {value: 404}.
        Failed ProjectC.WebApi.Tests.Events.PublicReadRateLimitingTests.GetEvents_OverThePermitLimit_ReturnsProblemDetailsWithRetryAfter
         Expected response.StatusCode to be HttpStatusCode.TooManyRequests {value: 429}, but found HttpStatusCode.OK {value: 200}.
        Failed ProjectC.WebApi.Tests.Events.PublicReadRateLimitingTests.GetEvents_WhenAnotherClientIpIsExhausted_StillServesDifferentClientIp
         Expected exhausted.StatusCode to be HttpStatusCode.TooManyRequests {value: 429}, but found HttpStatusCode.OK {value: 200}.
        Failed ProjectC.WebApi.Tests.Events.PublicReadRateLimitingTests.GetEventSeats_WhenRateLimited_DoesNotQueryTheRepository
         Expected limited.StatusCode to be HttpStatusCode.TooManyRequests {value: 429}, but found HttpStatusCode.NotFound {value: 404}.
      Failed!  - Failed:     6, Passed:     2, Skipped:     0, Total:     8, Duration: 1 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - 測試設計註記：詳情端點走列表快取，Repository 呼叫次數不能觀察；「429 時查詢未執行」改以 `/seats`（每次都查 `IEventRepository.GetByIdAsync`）斷言前後呼叫次數相同。首次 RED 曾因測試 factory 未初始化容器而回 500（環境問題，非目標行為失敗），已修正為共用 `IClassFixture` factory 後重跑，上列為有效 RED。
    - GREEN（`EventsController` 加 `[EnableRateLimiting("public-read")]`）：同命令與 `--filter "FullyQualifiedName~Events"`（既有 Events 測試不退步）：
      ```
      Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8, Duration: 1 s - ProjectC.WebApi.Tests.dll (net10.0)
      Passed!  - Failed:     0, Passed:   186, Skipped:     0, Total:   186, Duration: 2 m 10 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - 重構：N/A。
- [x] 2.3 login／captcha 在轉送還原後依用戶端 IP 各自計數（對應 LRL-011、LRL-012）
  - 測試先行：整合測試——受信任來源下兩個 `X-Forwarded-For` 各打滿登入額度互不觸發 429；非信任來源每次帶不同標頭仍落入同一分區並在超額後 429；captcha 同。
  - 預期 RED：此 task 排在 1.2 之後，RED 以暫時移除 1.2 的 middleware 註冊驗證測試確實會失敗（Expected 第二個用戶端 200 but found 429），貼 `mutate`／stash 輸出。
  - GREEN：相關測試與既有 `LoginRateLimitingTests`、`CaptchaRateLimitTests` 全過。
  - 既有 LRL 追溯（回歸，GREEN 須重跑並貼原文）：LRL-001 `Login_WithRequestsUnderTheLimit_AllSucceedWithoutBeingRateLimited`；LRL-002 `Login_WithExactlyThePermitLimitRequests_AllSucceedWithoutBeingRateLimited`；LRL-003 `Login_WithCorrectCredentialsAsTheRequestOverTheLimit_IsStillRejectedWithoutIssuingTokens`；LRL-005 `Login_AfterTheWindowResets_RequestsAreAllowedAgain`；LRL-006 `OrdersRateLimitingTests.PlaceOrderAndLogin_UsageOnPlaceOrderDoesNotAffectLogin`；LRL-010 `Login_WhenRateLimited_ReturnsProblemDetailsWithRetryAfterHeader`；LRL-004 由本 task 新增的 LRL-011 測試（兩個不同用戶端 IP 各自計數）與 `LoginRateLimiterPartitioningTests` 覆蓋（TestServer 來源 IP 固定，既有整合測試無法驗證不同 IP）。實作時若發現任一對應不成立，補測試。
  - LRL-012 為回歸防護（現況即通過，不產生 RED）；LRL-011 以上述 mutate 取得 RED。
  - 還原驗證（核心安全邏輯）：依 CLAUDE.md 以 `bash scripts/verify-test-evidence.sh restore`／`mutate` 執行並貼原文。
  - 紀錄（2.3）：
    - 測試：`tests/ProjectC.WebApi.Tests/Auth/ClientIpRateLimitingTests.cs`（login／captcha 各兩情境：受信任代理後不同用戶端各自計數＝LRL-011；非信任來源偽造 `X-Forwarded-For` 仍同一分區＝LRL-012），factory 為 `ClientIpRateLimitedWebApplicationFactory`（login／captcha 3 次／600 秒，共用 `IClassFixture`、各測試用互不重複的 IP）。
    - 先確認測試在目前程式碼下通過（LRL-012 為回歸防護，現況即通過、不產生 RED）：
      ```
      Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 2 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - RED（mutate：暫時註解 `Program.cs` 的 `app.UseForwardedHeaders();`，一次一個改動），命令 `bash scripts/verify-test-evidence.sh mutate --file src/ProjectC.WebApi/Program.cs --from "    app.UseForwardedHeaders();" --to "    // app.UseForwardedHeaders();" -- docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ClientIpRateLimitingTests"`，退出碼 0（如預期失敗）；輸出原文（含 LRL-012 兩個測試仍通過，符合預期）：
      ```
      mutation:     app.UseForwardedHeaders(); ->     // app.UseForwardedHeaders();
      $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~ClientIpRateLimitingTests
        Determining projects to restore...
        All projects are up-to-date for restore.
        ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
        ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
        ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
        ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
        ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
      Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
      A total of 1 test files matched the specified pattern.
      [xUnit.net 00:00:04.39]     ProjectC.WebApi.Tests.Auth.ClientIpRateLimitingTests.Request_ThroughTrustedProxy_CountsEachClientIpSeparately(endpoint: "login") [FAIL]
      [xUnit.net 00:00:04.41]     ProjectC.WebApi.Tests.Auth.ClientIpRateLimitingTests.Request_ThroughTrustedProxy_CountsEachClientIpSeparately(endpoint: "captcha") [FAIL]
        Failed ProjectC.WebApi.Tests.Auth.ClientIpRateLimitingTests.Request_ThroughTrustedProxy_CountsEachClientIpSeparately(endpoint: "login") [1 s]
        Error Message:
         Expected other.StatusCode not to be HttpStatusCode.TooManyRequests {value: 429}, but it is.
        Stack Trace:
           at FluentAssertions.Primitives.EnumAssertions`2.NotBe(TEnum unexpected, String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.Auth.ClientIpRateLimitingTests.Request_ThroughTrustedProxy_CountsEachClientIpSeparately(String endpoint) in /src/tests/ProjectC.WebApi.Tests/Auth/ClientIpRateLimitingTests.cs:line 66
      --- End of stack trace from previous location ---
        Failed ProjectC.WebApi.Tests.Auth.ClientIpRateLimitingTests.Request_ThroughTrustedProxy_CountsEachClientIpSeparately(endpoint: "captcha") [15 ms]
        Error Message:
         Expected other.StatusCode not to be HttpStatusCode.TooManyRequests {value: 429}, but it is.
        Stack Trace:
           at FluentAssertions.Primitives.EnumAssertions`2.NotBe(TEnum unexpected, String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.Auth.ClientIpRateLimitingTests.Request_ThroughTrustedProxy_CountsEachClientIpSeparately(String endpoint) in /src/tests/ProjectC.WebApi.Tests/Auth/ClientIpRateLimitingTests.cs:line 66
      --- End of stack trace from previous location ---
      
      Failed!  - Failed:     2, Passed:     2, Skipped:     0, Total:     4, Duration: 1 s - ProjectC.WebApi.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
    - 還原驗證（restore：以 `git stash` 暫時移開 `Program.cs` 的未 commit 修改，即移除 `AddClientAddressResolution` 與 `UseForwardedHeaders`），命令 `bash scripts/verify-test-evidence.sh restore --path src/ProjectC.WebApi/Program.cs -- docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ClientIpRateLimitingTests"`，退出碼 0；輸出原文（`git stash list` 確認 stash 存在後才 pop，pop 成功、`UseForwardedHeaders` 已還原）：
      ```
      $ git stash push -u -- src/ProjectC.WebApi/Program.cs
      Saved working directory and index state WIP on feature/reverse-proxy-forwarded-headers: 84f87ef spec：tasks 2.3 補上既有 LRL 情境與測試的追溯對照（外審 B-001）
      $ git stash list
      stash@{0}: WIP on feature/reverse-proxy-forwarded-headers: 84f87ef spec：tasks 2.3 補上既有 LRL 情境與測試的追溯對照（外審 B-001）
      $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~ClientIpRateLimitingTests
        Determining projects to restore...
        All projects are up-to-date for restore.
        ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
        ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
        ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
        ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
        ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
      Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
      A total of 1 test files matched the specified pattern.
      [xUnit.net 00:00:04.26]     ProjectC.WebApi.Tests.Auth.ClientIpRateLimitingTests.Request_ThroughTrustedProxy_CountsEachClientIpSeparately(endpoint: "login") [FAIL]
      [xUnit.net 00:00:04.27]     ProjectC.WebApi.Tests.Auth.ClientIpRateLimitingTests.Request_ThroughTrustedProxy_CountsEachClientIpSeparately(endpoint: "captcha") [FAIL]
        Failed ProjectC.WebApi.Tests.Auth.ClientIpRateLimitingTests.Request_ThroughTrustedProxy_CountsEachClientIpSeparately(endpoint: "login") [1 s]
        Error Message:
         Expected other.StatusCode not to be HttpStatusCode.TooManyRequests {value: 429}, but it is.
        Stack Trace:
           at FluentAssertions.Primitives.EnumAssertions`2.NotBe(TEnum unexpected, String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.Auth.ClientIpRateLimitingTests.Request_ThroughTrustedProxy_CountsEachClientIpSeparately(String endpoint) in /src/tests/ProjectC.WebApi.Tests/Auth/ClientIpRateLimitingTests.cs:line 66
      --- End of stack trace from previous location ---
        Failed ProjectC.WebApi.Tests.Auth.ClientIpRateLimitingTests.Request_ThroughTrustedProxy_CountsEachClientIpSeparately(endpoint: "captcha") [15 ms]
        Error Message:
         Expected other.StatusCode not to be HttpStatusCode.TooManyRequests {value: 429}, but it is.
        Stack Trace:
           at FluentAssertions.Primitives.EnumAssertions`2.NotBe(TEnum unexpected, String because, Object[] becauseArgs)
         at ProjectC.WebApi.Tests.Auth.ClientIpRateLimitingTests.Request_ThroughTrustedProxy_CountsEachClientIpSeparately(String endpoint) in /src/tests/ProjectC.WebApi.Tests/Auth/ClientIpRateLimitingTests.cs:line 66
      --- End of stack trace from previous location ---
      
      Failed!  - Failed:     2, Passed:     2, Skipped:     0, Total:     4, Duration: 1 s - ProjectC.WebApi.Tests.dll (net10.0)
      結果：測試如預期失敗
      $ git stash pop
      On branch feature/reverse-proxy-forwarded-headers
      Changes not staged for commit:
        (use "git add <file>..." to update what will be committed)
        (use "git restore <file>..." to discard changes in working directory)
      	modified:   openspec/changes/reverse-proxy-forwarded-headers/tasks.md
      	modified:   src/ProjectC.WebApi/Controllers/EventsController.cs
      	modified:   src/ProjectC.WebApi/Extensions/EagerOptionsValidationExtensions.cs
      	modified:   src/ProjectC.WebApi/Extensions/RateLimitingServiceCollectionExtensions.cs
      	modified:   src/ProjectC.WebApi/Program.cs
      	modified:   src/ProjectC.WebApi/appsettings.json
      	modified:   tests/ProjectC.WebApi.Tests/Startup/RateLimitingOptionsFailFastTests.cs
      
      Untracked files:
        (use "git add <file>..." to include in what will be committed)
      	src/ProjectC.Application/Common/PublicReadRateLimitingOptions.cs
      	src/ProjectC.WebApi/Common/ForwardedHeadersSettings.cs
      	src/ProjectC.WebApi/Extensions/ForwardedHeadersServiceCollectionExtensions.cs
      	tests/ProjectC.Application.Tests/Common/PublicReadRateLimitingOptionsTests.cs
      	tests/ProjectC.WebApi.Tests/Auth/ClientIpRateLimitingTests.cs
      	tests/ProjectC.WebApi.Tests/Events/PublicReadRateLimitingTests.cs
      	tests/ProjectC.WebApi.Tests/ForwardedHeaders/
      	tests/ProjectC.WebApi.Tests/TestSupport/ClientIpRateLimitedWebApplicationFactory.cs
      	tests/ProjectC.WebApi.Tests/TestSupport/ForwardedHeadersWebApplicationFactory.cs
      	tests/ProjectC.WebApi.Tests/TestSupport/PublicReadRateLimitedWebApplicationFactory.cs
      
      no changes added to commit (use "git add" and/or "git commit -a")
      Dropped refs/stash@{0} (c317e5e721ad4c1136225dcace5426e52700f965)
      ```
    - GREEN（還原後）：新測試與既有 LRL 回歸（LRL-001／002／003／005／010 在 `LoginRateLimitingTests`；LRL-006 在 `OrdersRateLimitingTests`；`CaptchaRateLimitTests`；`LoginRateLimiterPartitioningTests`）。命令 `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ClientIpRateLimitingTests|FullyQualifiedName~LoginRateLimitingTests|FullyQualifiedName~CaptchaRateLimit|FullyQualifiedName~OrdersRateLimitingTests|FullyQualifiedName~LoginRateLimiterPartitioningTests"`：
      ```
      Passed!  - Failed:     0, Passed:    27, Skipped:     0, Total:    27, Duration: 17 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - 重構：N/A。

## 3. 部署設定

- [x] 3.1 `deploy/nginx/default.conf` 轉送標頭＋`k8s/50-web.yaml` 的 `externalTrafficPolicy: Local`
  - TDD：N/A（設定檔，無單元測試層）；改以 3.2 的腳本斷言取代：先補斷言，改設定前新斷言失敗（貼輸出），改後通過。
  - 紀錄：
    - 先補斷言再改設定（RED，改設定前執行，只做靜態檢查、未動叢集）：
      ```
      FAIL [10] nginx 未以 $remote_addr 覆蓋 X-Forwarded-For; nginx 未傳遞 X-Forwarded-Proto; web Service 未設 externalTrafficPolicy: Local; （未對叢集做任何操作）
      SUMMARY: 0 passed, 1 failed, 0 unverified
      exit=1
      ```
    - 改後：`deploy/nginx/default.conf` 的 `location /api/` 加 `proxy_set_header X-Forwarded-For $remote_addr;` 與 `X-Forwarded-Proto $scheme;`；`k8s/50-web.yaml` 的 web Service 加 `externalTrafficPolicy: Local`。GREEN 見 3.2。
- [x] 3.2 `scripts/verify-k8s-manifests.sh` 補斷言：nginx 設定含 `X-Forwarded-For $remote_addr` 與 `X-Forwarded-Proto`；`web` Service `externalTrafficPolicy: Local`；既有斷言仍通過
  - 驗證：在 kind 叢集執行腳本，貼各斷言摘要行；並實測經 NodePort 帶偽造 `X-Forwarded-For` 的請求，API 看到的來源是 nginx 覆蓋後的值（觀察限流或日誌）。通過判準為「偽造標頭被 nginx 覆蓋」，不要求看到特定外部 IP（Docker Desktop 的 kind 經 NodePort 進入時來源可能被 NAT 成閘道位址），紀錄須註明實測環境限制。
  - 紀錄：
    - 環境：docker-desktop 叢集（只有此 context；kind 未裝），Namespace `projectc-verify`、NodePort 30081。Docker Desktop 經 NodePort 進入時來源位址可能被 NAT 成閘道位址，故 10d 的通過判準為「偽造值被覆蓋」，不要求看到外部 IP。
    - 為避免探針與斷言打到公開讀取額度，腳本在驗證 Namespace 以 `kubectl patch configmap projectc-config` 把 `PublicReadRateLimiting__PermitLimit` 設為 100000（僅驗證用，不動 `k8s/` 的 manifest）。
    - 第一次完整執行（10d 取 8 次請求）：10d FAIL，8 次皆 400、無 429。原因：api 為 2 個副本、各自在記憶體計數（每副本 5 次），8 次不足以讓任一副本超額；非 nginx 未覆蓋。已改 12 次後重跑。第一次輸出末段原文：
      ```
      UNVERIFIED [8c] 本機 CNI 不執行 NetworkPolicy（deny-all 保護的 Pod 仍可連通），無法驗證不可信 workload 不可達；見 docs/archive/2026-11-10-networkpolicy-enforcement-todo.md
      PASS [9] api 為 ClusterIP、僅 web 為 NodePort（30081）、api／web／migrator 皆 runAsNonRoot、OpenAPI／SwaggerUI 不可經 web 取得
      FAIL [10d] 8 次登入請求各帶不同偽造 X-Forwarded-For，沒有出現 429（偽造標頭未被覆蓋？）：400 400 400 400 400 400 400 400
      SUMMARY: 15 passed, 1 failed, 1 unverified
      UNVERIFIED: 8c
      exit=1
      ```
    - 第二次完整執行（GREEN）全文（斷言 7 的日誌區塊略去）：
      ```
      PASS [10] nginx 以 $remote_addr 覆蓋 X-Forwarded-For 並傳遞 X-Forwarded-Proto；web Service externalTrafficPolicy: Local
      PASS [0] manifest 覆寫為 Namespace projectc-verify、NodePort 30081
      PASS [1] db／redis／seq／api／web 就緒（套用 NetworkPolicy 後仍就緒），migrator Complete，migration 筆數 16（預期 16），api 最大 restartCount=0
      PASS [2a] 探針路徑：liveness=/health/live readiness=/health/ready startup=/health/live
      PASS [2b] db 停止後 api Service endpoints 在 90 秒內清空（readiness 轉為 NotReady）
      PASS [2c] db 恢復後 api endpoints 在 150 秒內回到 Ready
      PASS [3a] GET http://localhost:30081/ → 200
      PASS [3b] GET /api/events → 200（2xx，非 3xx）
      PASS [4] 刪除 Pod api-57769dcd57-9x92l 期間 22 次請求全為 2xx，Deployment 回到 2 個 Ready
      PASS [5] rollout restart 期間 45 次請求全為 2xx
      PASS [6] 5 個隨機機敏值未出現在 logs、describe、k8s/、版控；Secret 鍵名不在 ConfigMap
      PASS [7] 缺 Jwt__SigningKey 時 api 進入 CrashLoopBackOff，上次結束碼=139
      PASS [8a] 經 web 取不到 /health/live、/health/ready、/api/health/*（回應不是 Healthy）
      PASS [8b] kubectl port-forward 到 api 可取得 /health/live（維運來源可達）
      UNVERIFIED [8c] 本機 CNI 不執行 NetworkPolicy（deny-all 保護的 Pod 仍可連通），無法驗證不可信 workload 不可達；見 docs/archive/2026-11-10-networkpolicy-enforcement-todo.md
      PASS [9] api 為 ClusterIP、僅 web 為 NodePort（30081）、api／web／migrator 皆 runAsNonRoot、OpenAPI／SwaggerUI 不可經 web 取得
      PASS [10d] 12 次登入請求各帶不同偽造 X-Forwarded-For，仍被限流（狀態碼：400 400 400 400 400 400 400 400 400 429 429 429）
      SUMMARY: 16 passed, 0 failed, 1 unverified
      UNVERIFIED: 8c
      exit=3
      ```
    - exit=3 為既有行為（8c 在本機 CNI 為 UNVERIFIED，與本 change 無關，見 K8s 指南）。
- [x] 3.3 檢查 `loadtest/` 腳本是否會被 `public-read` 限流；必要時於壓測設定覆寫額度，不改預設值
  - TDD：N/A（壓測腳本／設定）；以 `docker compose config` 或腳本 dry-run 確認覆寫生效。
  - 紀錄：`grep -rn "api/events" loadtest --include=*.js`：只有 `admin-api.js:91/100/104`（setup，各 1 次）與 `load-test.js:190-191`（`runVerify`，各 1 次），迴圈中的買家流程不呼叫 `/api/events*`。每次壓測對公開讀取端點總共約 6 次請求，遠低於預設 120 次／60 秒，不需覆寫，預設值未改。`docker compose config` 驗證不適用（未新增覆寫）。

## 4. 文件與同步

- [x] 4.1 更新 `docs/api-conventions.md`（限流 policy、分區鍵為轉送還原後 IP、新設定區段）、K8s 指南（`externalTrafficPolicy: Local` 限制）、`docs/review/TODO.md`（C-2、M-3 標記完成）
  - TDD：N/A（純文件）。驗證：`bash scripts/check-doc-paths.sh`。
  - 紀錄：已更新 `docs/api-conventions.md`（新增公開讀取限流條目：policy、預設 120／60、`PublicReadRateLimiting` 與 `ForwardedHeaders:TrustedNetworks` 設定、分區鍵為還原後用戶端 IP、nginx 須覆寫 XFF）、`docs/2026-11-12-k8s-guide.md`（斷言 10／10d、額度調整、Docker Desktop NAT 限制）、`docs/review/TODO.md`（C-2、M-3 標題標 ✅，註明待歸檔；未動優先順序表）。`bash scripts/check-doc-paths.sh` 輸出：`OK: 所有 docs 路徑引用都存在`。
- [x] 4.2 spec 同步檢查：`openspec validate reverse-proxy-forwarded-headers`；封存前確認 `api-rate-limiting` MODIFIED 區塊與主 spec 逐字一致（除部署前提段落與新增 Scenario）
  - 紀錄：`openspec validate reverse-proxy-forwarded-headers` 輸出：`Change 'reverse-proxy-forwarded-headers' is valid`。MODIFIED「登入端點的請求頻率限制」與主 spec 逐行 diff（去除空行與 CR）只有兩處差異：部署前提段落改寫（預期），與新增 LRL-011、LRL-012 兩個 Scenario（預期），其餘逐字一致。

## 5. 收尾

- [x] 5.1 hardener 最終檢查（新增的 middleware／設定解析屬外部輸入處理：CIDR 解析、設定錯誤 fail-fast、日誌不洩漏；取消傳遞不適用）。結果記錄於此。
  - 紀錄：依 hardener 清單逐項對照本次變更（`ForwardedHeadersSettings`、`ForwardedHeadersServiceCollectionExtensions`、`PublicReadRateLimitingOptions`、`public-read` policy）。結論：未發現缺漏，未因此修改程式碼。
    - 參數驗證／外部輸入：`ForwardedHeaders:TrustedNetworks` 每一項以 `IPNetwork.TryParse` 解析，空字串、空白、非法 CIDR 一律丟 `InvalidOperationException`、不略過；明確設定為空清單亦啟動失敗（`ForwardedHeadersSettingsTests` 涵蓋）。`PublicReadRateLimiting` 以 DataAnnotations 驗證正數、缺漏採預設（PRL-005/006 測試涵蓋）。
    - fail-fast：`ResolveForwardedHeadersOptions` 於啟動時解析選項，非法設定在 `Program` 啟動階段失敗（`RateLimitingOptionsFailFastTests` 新增 Theory 涵蓋）。
    - 信任邊界：`ForwardLimit=1`、先清空 `KnownProxies`／`KnownIPNetworks` 再加入設定網段；非信任來源的 `X-Forwarded-For` 被忽略（CAR 測試、LRL-012 測試涵蓋）。
    - 日誌遮蔽：例外訊息只含 CIDR 設定值（非機敏）；新增程式碼不記錄 header 內容或用戶端位址。
    - 取消傳遞／並發／交易：不適用（無 I/O、無共享可變狀態；限流器由框架管理）。
- [x] 5.2 全套測試（Domain／Application／Infrastructure／WebApi）各專案摘要行貼原文；`graphify update .`
  - 紀錄：全套測試各專案摘要行原文（`docker compose exec -T api dotnet test tests/ProjectC.<X>.Tests`）：
    ```
    Passed!  - Failed:     0, Passed:   153, Skipped:     0, Total:   153, Duration: 147 ms - ProjectC.Domain.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   632, Skipped:     0, Total:   632, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   303, Skipped:     0, Total:   303, Duration: 1 m 2 s - ProjectC.Infrastructure.Tests.dll (net10.0)
    ```
    - WebApi 第一次完整執行：
      ```
      Failed ProjectC.WebApi.Tests.Startup.ServiceRegistrationSnapshotTests.ServiceRegistrations_MatchCommittedBaseline [73 ms]
      Expected (missing.Count + unexpected.Count) to be 0 because DI 圖與基準檔不一致。缺少的項目：；多出的項目：Singleton | Microsoft.Extensions.Options.IConfigureOptions`1[ProjectC.Application.Common.PublicReadRateLimitingOptions] | instance || Singleton | Microsoft.Extensions.Options.IOptionsChangeTokenSource`1[ProjectC.Application.Common.PublicReadRateLimitingOptions] | instance || Singleton | Microsoft.Extensions.Options.IValidateOptions`1[ProjectC.Application.Common.PublicReadRateLimitingOptions] | instance || Singleton | ProjectC.Application.Common.PublicReadRateLimitingOptions | factory, but found 4 (difference of 4).
      Failed!  - Failed:     1, Passed:   921, Skipped:     0, Total:   922, Duration: 5 m - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - 原因：新註冊 `PublicReadRateLimitingOptions` 使 DI 圖多 4 筆（task 清單外的必要更新）。以 `UPDATE_SERVICE_REGISTRATION_SNAPSHOT=1` 更新 `ServiceRegistrationSnapshot.baseline.txt`，diff 僅新增 4 行 `PublicReadRateLimitingOptions` 相關 Singleton（`IConfigureOptions`、`IOptionsChangeTokenSource`、`IValidateOptions`、選項本體 factory）。
    - WebApi 更新基準後重跑：
      ```
      Passed!  - Failed:     0, Passed:   922, Skipped:     0, Total:   922, Duration: 4 m 30 s - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - 既有整合測試未被預設額度（120／60 秒）影響：全套通過，未新增 `CustomWebApplicationFactory` 覆寫。
    - `graphify update .`：`Code graph updated.`
