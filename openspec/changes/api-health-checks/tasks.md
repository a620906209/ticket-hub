## 1. 相依與測試支援

- [x] 1.1 新增測試 factory。`tests/ProjectC.WebApi.Tests/TestSupport/` 新增 factory（可覆寫 DB 連線字串、`HTTPS_PORT`、`Seq:ServerUrl`、`Serilog:MinimumLevel:Default`、`LoginRateLimiting:PermitLimit`，可選擇加入額度 1、視窗 1 小時的 GlobalLimiter，可選擇將 `HealthCheckService` 換成會拋出帶標記訊息例外的實作，可選擇以計數裝飾器包裝所有 health check registration 並加入未帶 tag 的探查 check，提供 `ExhaustRateLimitAsync`（呼叫指定控制端點，最多 5 次、整體期限 30 秒，未出現 429 就明確失敗），並掛 `InMemoryLogEventSink`）。覆寫連線字串時只影響 host：基底 `InitializeAsync` 的 `MigrateAsync` 仍須打在測試容器；`LoginRateLimiting:PermitLimit` 須在基底設定之後覆寫，否則會被基底的 1000 蓋掉。驗證：`docker compose exec api dotnet build` 成功。
  - TDD：N/A，只是測試基礎設施，沒有可觀察的產品行為；行為由 2.x 起的測試驗證。
  - 完成紀錄（build 摘要行原文）：`Build succeeded.; 2 Warning(s); 0 Error(s);Time Elapsed 00:00:05.06;`

## 2. 探測端點

- [x] 2.1 實作 `GET /health/live`。對應 HC-LIVE-HEALTHY、HC-LIVE-DB-DOWN、HC-LIVE-REDIS-DOWN、HC-LIVE-SEQ-DOWN、HC-LIVE-NO-CHECKS，以及 live 部分的 HC-ANONYMOUS、HC-NO-RATE-LIMIT、HC-NO-ENDPOINT-RATE-LIMIT、HC-NO-RATE-LIMIT-POLICY、HC-BODY-HEALTHY、HC-BODY-EXCEPTION。
  - 測試先行：`tests/ProjectC.WebApi.Tests/HealthChecks/HealthLiveEndpointTests.cs`，命名如 `GetHealthLive_WhenDatabaseUnavailable_ReturnsOk`。每個情境都實際呼叫 `GET /health/live`，條件與斷言如下：
    - HC-LIVE-NO-CHECKS（`GetHealthLive_Always_RunsNoHealthChecks`）：計數 factory；200，斷言所有 registration 與探查 check 的執行次數皆為 0。
    - HC-LIVE-HEALTHY：一般 factory；200。
    - HC-LIVE-DB-DOWN：連線字串指向 `127.0.0.1:1`；200。
    - HC-LIVE-REDIS-DOWN：`RedisUnavailableWebApplicationFactory`（Redis 指向 `127.0.0.1:1`）；200。
    - HC-LIVE-SEQ-DOWN：Seq URL 覆寫為無人監聽的位址，並斷言 `IConfiguration` 確實是該值；200。
    - HC-ANONYMOUS：斷言請求沒有 `Authorization` 標頭；200。
    - HC-BODY-HEALTHY：讀取回應本文，斷言完全等於 `Healthy`。
    - HC-NO-RATE-LIMIT（`GetHealthLive_WhenGlobalRateLimitExhausted_ReturnsOk`）：獨立 factory 實例啟用 GlobalLimiter；以 `ExhaustRateLimitAsync` 呼叫 `GET /api/events`，未在上限內出現 429 就失敗，不進入探測請求；再循序呼叫 50 次，斷言共收到 50 個回應，且逐筆為 200。
    - HC-NO-ENDPOINT-RATE-LIMIT（`GetHealthLive_WhenLoginRateLimitExhausted_ReturnsOk`）：獨立 factory 實例不啟用 GlobalLimiter，`LoginRateLimiting:PermitLimit` 設為 1；以 `ExhaustRateLimitAsync` 呼叫 `POST /api/auth/login`，未在上限內出現 429 就失敗，不進入探測請求；再循序呼叫 50 次，斷言同上。
    - HC-NO-RATE-LIMIT-POLICY（`HealthEndpoints_Metadata_DisableRateLimitingWithoutPolicy`）：從 `EndpointDataSource` 取得 `/health/live`，斷言端點存在、有 `DisableRateLimitingAttribute`、沒有 `EnableRateLimitingAttribute`。
    - HC-BODY-EXCEPTION：factory 換成會拋出標記例外的 `HealthCheckService`，例外訊息含標記、`Host=marker-db` 與 `marker-redis`，不含任何形似密碼的字串；500；將本文解析為 JSON，斷言欄位集合完全等於 `status`、`title`、`traceId`，`status` 為 500、`title` 完全等於 `An unexpected error occurred.`、`traceId` 符合 `^[\w:.|-]+$`；移除 `traceId` 的值後，本文不分大小寫不含標記、`marker-`、`Host=`、`Password`、`InvalidOperationException`、` at `、`database`、`redis`、`seq`、`postgres`、`npgsql`。
  - 預期 RED：路由不存在，Expected 200、Actual 404。若 DB 不可連線的測試因 host 無法啟動而失敗，這不是有效 RED，停止並回到設計（design 決策 7）。
  - 最小實作：集中定義探測路徑；`AddHealthChecks`；`MapHealthChecks` 使用 `Predicate = _ => false`、`.AllowAnonymous()` 與 `.DisableRateLimiting()`。
  - GREEN／重構：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~HealthChecks"`，全部通過；無重構時記 N/A。
  - 檢查驗證：GREEN 後暫時將 live 的 predicate 改為 `_ => true`，確認 `GetHealthLive_Always_RunsNoHealthChecks` 因次數不為 0 失敗。最後還原並重跑。
  - 限流驗證：RED 的 404 無法證明測試能抓到限流，所以 GREEN 後暫時改動正式程式碼：移除 `.DisableRateLimiting()`，執行 `GetHealthLive_WhenGlobalRateLimitExhausted_ReturnsOk`；改為 `.RequireRateLimiting("login")`，執行 `GetHealthLive_WhenLoginRateLimitExhausted_ReturnsOk`，此測試沒有全域限流，429 只可能來自端點專屬限流。兩者都須因 429 失敗。再保留 `.DisableRateLimiting()` 並加上 `.RequireRateLimiting("captcha")`，確認 `HealthEndpoints_Metadata_DisableRateLimitingWithoutPolicy` 失敗；移除 `.DisableRateLimiting()` 也須讓它失敗。最後還原並重跑。
  - 還原驗證：N/A，不是 bug fix 或核心業務邏輯。
  - 完成紀錄：
    RED（於任何正式程式碼修改前執行，`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~HealthChecks"`，摘要 `Failed!  - Failed:    24, Passed:     0, Skipped:     0, Total:    24, Duration: 24 s - ProjectC.WebApi.Tests.dll (net10.0)`；目標行為不符：路由不存在，host 在 DB 不可達時仍能啟動，符合決策 7）。（限流測試的訊息逐筆列出 50 個狀態碼，此處只保留開頭。）
    - `Failed HealthLiveEndpointTests.GetHealthLive_WhenDependenciesHealthy_ReturnsOkWithHealthyBody [396 ms]`：`Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.`
    - `Failed HealthLiveEndpointTests.GetHealthLive_WhenHealthCheckServiceThrows_ReturnsGenericProblemDetails [2 s]`：`Expected response.StatusCode to be HttpStatusCode.InternalServerError {value: 500}, but found HttpStatusCode.NotFound {value: 404}.`
    - `Failed HealthLiveEndpointTests.GetHealthLive_Always_RunsNoHealthChecks [3 s]`：`Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.`
    - `Failed HealthLiveEndpointTests.GetHealthLive_WhenRedisUnavailable_ReturnsOk [2 s]`：`Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.`
    - `Failed HealthLiveEndpointTests.GetHealthLive_WithoutAuthorizationHeader_ReturnsOk [10 ms]`：`Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.`
    - `Failed HealthLiveEndpointTests.HealthEndpoints_Metadata_DisableRateLimitingWithoutPolicy [4 ms]`：`Expected endpoint not to be <null> because /health/live 必須已對應為端點.`
    - `Failed HealthLiveEndpointTests.GetHealthLive_WhenGlobalRateLimitExhausted_ReturnsOk [3 s]`：`Expected statusCodes to contain only items matching (Convert(statusCode, Int32) == 200), but {HttpStatusCode.TooManyRequests {value: 429}, HttpStatusCode.TooManyRequests {value: 429}, HttpStatusCode.TooManyRequests {value: 429}, H`
    - `Failed HealthLiveEndpointTests.GetHealthLive_WhenSeqUnavailable_ReturnsOk [3 s]`：`Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.`
    - `Failed HealthLiveEndpointTests.GetHealthLive_WhenDatabaseUnavailable_ReturnsOk [3 s]`：`Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.`
    - `Failed HealthLiveEndpointTests.GetHealthLive_WhenLoginRateLimitExhausted_ReturnsOk [3 s]`：`Expected statusCodes to contain only items matching (Convert(statusCode, Int32) == 200), but {HttpStatusCode.NotFound {value: 404}, HttpStatusCode.NotFound {value: 404}, HttpStatusCode.NotFound {value: 404}, HttpStatusCode.NotFoun`
    GREEN：`Passed!  - Failed:     0, Passed:    24, Skipped:     0, Total:    24, Duration: 21 s - ProjectC.WebApi.Tests.dll (net10.0)`
    檢查驗證（live predicate 改 `_ => true`）：`GetHealthLive_Always_RunsNoHealthChecks`：`Expected factory.HealthCheckExecutionCounts to be empty, but found at least one item {[database, 1]}.`；還原後見 3.1 GREEN。
    限流驗證（改動同時套用於 live 與 ready；各訊息只保留開頭）：
    - 移除 `.DisableRateLimiting()`：`GetHealthLive_WhenGlobalRateLimitExhausted_ReturnsOk`：`Expected statusCodes to contain only items matching (Convert(statusCode, Int32) == 200), but {HttpStatusCode.TooManyRequests {value: 429}, ...`；`HealthEndpoints_Metadata_DisableRateLimitingWithoutPolicy`：`Expected endpoint!.Metadata.GetMetadata<DisableRateLimitingAttribute>() not to be <null>.`
    - 改為 `.RequireRateLimiting("login")`：`GetHealthLive_WhenLoginRateLimitExhausted_ReturnsOk`：`Expected statusCodes to contain only items matching (Convert(statusCode, Int32) == 200), but {HttpStatusCode.TooManyRequests {value: 429}, ...`
    - 保留 Disable 並加 `.RequireRateLimiting("captcha")`：`HealthEndpoints_Metadata_DisableRateLimitingWithoutPolicy`：`Expected endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>() to be <null>, but found Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute`
    還原後結果：見 3.1 GREEN。重構：N/A。

- [x] 2.2 實作 `GET /health/ready`。對應 HC-READY-HEALTHY、HC-READY-DB-DOWN、HC-READY-DB-MISSING、HC-READY-TIMEOUT、HC-READY-REDIS-DOWN、HC-READY-SEQ-DOWN、HC-READY-ONLY-DATABASE、HC-BODY-UNHEALTHY，以及 ready 部分的 HC-ANONYMOUS、HC-NO-RATE-LIMIT、HC-NO-ENDPOINT-RATE-LIMIT、HC-NO-RATE-LIMIT-POLICY、HC-BODY-HEALTHY、HC-BODY-EXCEPTION。
  - 測試先行：`tests/ProjectC.WebApi.Tests/HealthChecks/HealthReadyEndpointTests.cs`，命名如 `GetHealthReady_WhenDatabaseDoesNotRespond_ReturnsServiceUnavailableWithinTimeout`；HC-BODY-HEALTHY 讀取回應本文，斷言完全等於 `Healthy`；HC-BODY-UNHEALTHY 斷言回應完全等於 `Unhealthy`。HC-READY-TIMEOUT 先呼叫 `/health/live` 完成 host 啟動，再量測 `/health/ready` 從送出請求到讀完回應的時間，斷言介於 1.5 到 4 秒，並斷言 listener 收到連線；另以 `HealthCheckServiceOptions` 斷言 `database` registration 的 `Timeout` 完全等於 2 秒（`DatabaseReadinessRegistration_Timeout_IsTwoSeconds`）。
  - 其餘情境都實際呼叫 `GET /health/ready`，條件與斷言如下：
    - HC-READY-HEALTHY：一般 factory，DB 為可連線的測試容器；200。
    - HC-READY-DB-DOWN：連線字串指向 `127.0.0.1:1`；503。
    - HC-READY-DB-MISSING：測試容器的連線字串改用不存在的資料庫名稱；503。
    - HC-READY-REDIS-DOWN：`RedisUnavailableWebApplicationFactory`，Redis 指向 `127.0.0.1:1`，DB 沿用可連線的測試容器；200。
    - HC-READY-SEQ-DOWN：DB 沿用可連線的測試容器，Seq URL 覆寫為無人監聽的位址，並斷言 `IConfiguration` 確實是該值；200。ready 只檢查 DB，所以這兩個情境的 200 同時證明 DB 可連線。
    - HC-ANONYMOUS：斷言請求沒有 `Authorization` 標頭；200。
    - HC-NO-RATE-LIMIT（`GetHealthReady_WhenGlobalRateLimitExhausted_ReturnsOk`）、HC-NO-ENDPOINT-RATE-LIMIT（`GetHealthReady_WhenLoginRateLimitExhausted_ReturnsOk`）：步驟與逐筆斷言同 2.1。
    - HC-NO-RATE-LIMIT-POLICY：同 2.1，對象改為 `/health/ready`。
    - HC-BODY-EXCEPTION：同 2.1。
    - HC-READY-ONLY-DATABASE：一般 factory 從 `HealthCheckServiceOptions` 斷言 registration 只有 `database`（`HealthCheckRegistrations_Always_ContainOnlyDatabase`）；計數 factory 呼叫一次，斷言 200、`database` 執行 1 次、未帶 tag 的探查 check 執行 0 次（`GetHealthReady_Always_RunsOnlyReadyTaggedChecks`）。
  - 預期 RED：路由不存在，Expected 200 或 503、Actual 404；registration 測試因找不到 `database` 而失敗。
  - 最小實作：新增 `src/ProjectC.WebApi/HealthChecks/DatabaseReadinessHealthCheck.cs`，並為本次檢查設定連線 `Timeout`（design 決策 3）；`AddCheck<DatabaseReadinessHealthCheck>("database", tags: ["ready"], timeout: 2 秒)`；對應 `/health/ready`，只跑 `ready` tag，`.AllowAnonymous()` 與 `.DisableRateLimiting()`。
  - GREEN／重構：同 2.1 的命令，全部通過。
  - 逾時驗證：RED 的 404 無法證明測試能抓到錯誤的逾時，所以 GREEN 後依序暫時改動正式程式碼並執行逾時相關測試。實作後實際結束等待的是 Npgsql 連線 Timeout（design 決策 3），registration 的值由 registration 測試保護，因此改動分兩組：共用常數改為 5 秒，確認時間量測超過上限而失敗；改為 1 秒，確認低於下限而失敗；改為 3 秒，以及單獨將 registration 移除或改為 5、0.5、3 秒，確認 registration 測試失敗；移除連線 Timeout，確認時間量測超過上限而失敗。最後還原並重跑。
  - 檢查驗證：GREEN 後暫時將 ready 的 predicate 改為 `_ => true`，確認 `GetHealthReady_Always_RunsOnlyReadyTaggedChecks` 失敗；暫時新增一個帶 `ready` tag 的 `redis` check，確認 `HealthCheckRegistrations_Always_ContainOnlyDatabase` 失敗。最後還原並重跑。
  - 限流驗證：比照 2.1，對 ready 的端點執行同樣的改動，分別執行 `GetHealthReady_WhenGlobalRateLimitExhausted_ReturnsOk`、`GetHealthReady_WhenLoginRateLimitExhausted_ReturnsOk` 與中繼資料測試。
  - 還原驗證：N/A，不是 bug fix 或核心業務邏輯。
  - 完成紀錄：
    RED（同 2.1 的同一次執行與摘要）：（限流測試的訊息逐筆列出 50 個狀態碼，此處只保留開頭。）
    - `Failed HealthReadyEndpointTests.GetHealthReady_WhenRedisUnavailable_ReturnsOk [3 s]`：`Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.`
    - `Failed HealthReadyEndpointTests.GetHealthReady_WhenDatabaseReachable_ReturnsOkWithHealthyBody [38 ms]`：`Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.`
    - `Failed HealthReadyEndpointTests.GetHealthReady_WithoutAuthorizationHeader_ReturnsOk [5 ms]`：`Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.`
    - `Failed HealthReadyEndpointTests.HealthCheckRegistrations_Always_ContainOnlyDatabase [7 ms]`：`Expected GetRegistrations(_factory.Services).Select(registration => registration.Name) to be equal to {"database"}, but found empty collection.`
    - `Failed HealthReadyEndpointTests.GetHealthReady_WhenTargetDatabaseMissing_ReturnsServiceUnavailable [2 s]`：`Expected response.StatusCode to be HttpStatusCode.ServiceUnavailable {value: 503}, but found HttpStatusCode.NotFound {value: 404}.`
    - `Failed HealthReadyEndpointTests.GetHealthReady_WhenLoginRateLimitExhausted_ReturnsOk [2 s]`：`Expected statusCodes to contain only items matching (Convert(statusCode, Int32) == 200), but {HttpStatusCode.NotFound {value: 404}, HttpStatusCode.NotFound {value: 404}, HttpStatusCode.NotFound {value: 404}, HttpStatusCode.NotFoun`
    - `Failed HealthReadyEndpointTests.GetHealthReady_WhenHealthCheckServiceThrows_ReturnsGenericProblemDetails [2 s]`：`Expected response.StatusCode to be HttpStatusCode.InternalServerError {value: 500}, but found HttpStatusCode.NotFound {value: 404}.`
    - `Failed HealthReadyEndpointTests.HealthEndpoints_Metadata_DisableRateLimitingWithoutPolicy [6 ms]`：`Expected endpoint not to be <null> because /health/ready 必須已對應為端點.`
    - `Failed HealthReadyEndpointTests.GetHealthReady_WhenGlobalRateLimitExhausted_ReturnsOk [3 s]`：`Expected statusCodes to contain only items matching (Convert(statusCode, Int32) == 200), but {HttpStatusCode.TooManyRequests {value: 429}, HttpStatusCode.TooManyRequests {value: 429}, HttpStatusCode.TooManyRequests {value: 429}, H`
    - `Failed HealthReadyEndpointTests.GetHealthReady_WhenDatabaseRefusesConnection_ReturnsServiceUnavailableWithUnhealthyBody [3 s]`：`Expected response.StatusCode to be HttpStatusCode.ServiceUnavailable {value: 503}, but found HttpStatusCode.NotFound {value: 404}.`
    - `Failed HealthReadyEndpointTests.GetHealthReady_Always_RunsOnlyReadyTaggedChecks [2 s]`：`Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.`
    - `Failed HealthReadyEndpointTests.GetHealthReady_WhenDatabaseDoesNotRespond_ReturnsServiceUnavailableWithinTimeout [3 s]`：`Expected (client.GetAsync(LivePath)).StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.`
    - `Failed HealthReadyEndpointTests.DatabaseReadinessRegistration_Timeout_IsTwoSeconds [4 ms]`：`Expected registration not to be <null>.`
    - `Failed HealthReadyEndpointTests.GetHealthReady_WhenSeqUnavailable_ReturnsOk [1 s]`：`Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.NotFound {value: 404}.`
    GREEN 首次未通過，揭露設計假設不成立，過程記於 design 決策 3：只傳 token 時 `Expected stopwatch.Elapsed to be less than or equal to 4s, but found 15s, 1ms and 776.0µs.`；改用 `WaitAsync` 後 `Expected response.StatusCode to be HttpStatusCode.ServiceUnavailable {value: 503}, but found HttpStatusCode.InternalServerError {value: 500}.`，日誌例外 `System.InvalidOperationException: Can't close, connection is in state Connecting`。
    GREEN：`Passed!  - Failed:     0, Passed:    24, Skipped:     0, Total:    24, Duration: 21 s - ProjectC.WebApi.Tests.dll (net10.0)`
    逾時驗證：
    - 共用常數 5 秒：`Expected stopwatch.Elapsed to be less than or equal to 4s, but found 5s, 73ms and 851.6µs.`；`Expected 2s, but found 5s.`
    - 共用常數 1 秒：`Expected stopwatch.Elapsed to be greater than or equal to 1s and 500ms, but found 1s, 84ms and 541.3µs.`；`Expected 2s, but found 1s.`
    - 共用常數 3 秒：`DatabaseReadinessRegistration_Timeout_IsTwoSeconds`：`Expected 2s, but found 3s.`
    - 移除連線 Timeout：`Expected stopwatch.Elapsed to be less than or equal to 4s, but found 15s, 83ms and 256.5µs.`
    - 只改 registration：移除 `Expected 2s, but found -1ms.`；5 秒 `Expected 2s, but found 5s.`；0.5 秒 `Expected 2s, but found 500ms.`；3 秒 `Expected 2s, but found 3s.`
    檢查驗證：ready predicate 改 `_ => true`：`Expected factory.HealthCheckExecutionCounts.GetValueOrDefault(HealthCheckWebApplicationFactory.UntaggedProbeCheckName) to be 0, but found 1 (difference of 1).`；新增 `redis` check：`Expected GetRegistrations(_factory.Services).Select(registration => registration.Name) to be equal to {"database"}, but {"database", "redis"} contains 1 item(s) too many.`
    限流驗證（與 2.1 同一批改動）：移除 Disable：`GetHealthReady_WhenGlobalRateLimitExhausted_ReturnsOk` 因 429 失敗，中繼資料測試 `Expected endpoint!.Metadata.GetMetadata<DisableRateLimitingAttribute>() not to be <null>.`；改 login：`GetHealthReady_WhenLoginRateLimitExhausted_ReturnsOk` 因 429 失敗；Disable 加 captcha：`Expected endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>() to be <null>, but found Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute`。
    還原後結果：見 3.1 GREEN。重構：N/A。

## 3. 日誌等級

- [x] 3.1 依結果調整探測請求的日誌等級。對應 HC-LOG-SUCCESS-DEBUG、HC-LOG-SUCCESS-HIDDEN、HC-LOG-FAILURE-ERROR、HC-LOG-FAILURE-EXCEPTION、HC-LOG-FAILURE-NO-PASSWORD、HC-LOG-EXCEPTION-NO-PASSWORD、HC-LOG-OTHER-UNCHANGED、HC-LOG-OTHER-ERROR。
  - 測試先行：`tests/ProjectC.WebApi.Tests/HealthChecks/HealthCheckLoggingTests.cs`；篩選 `SourceContext` 為 `Serilog.AspNetCore.RequestLoggingMiddleware` 且路徑相符的事件。TraceId 關聯規則（下列「帶相同 `TraceId`」皆適用）：取值時，事件須有 `TraceId` 屬性，其值為非空白字串，否則測試失敗，不得以 null 或空字串比對；摘要先依此取得基準值，篩選只保留屬性值完全等於基準值的事件，用來證明此次請求的每筆事件同樣須通過上述檢查。HC-LOG-SUCCESS-DEBUG 將最低等級設為 Debug，呼叫兩個探測端點並斷言皆為 200，以輪詢等到各路徑的摘要事件，斷言事件存在且等級為 Debug。HC-LOG-FAILURE-NO-PASSWORD 將最低等級設為 Debug，連線字串使用專用標記密碼，使用新的 factory 實例，對 `/health/ready` 只發一次請求，斷言該路徑的摘要恰好一筆且為 Error，取其 `TraceId`；先斷言帶相同 `TraceId` 的事件含 `DefaultHealthCheckService` 的 Error 事件與 `Microsoft.EntityFrameworkCore.Database.Connection` 的事件（預期至少有 `ConnectionOpening`，不要求 `ConnectionError`）；再掃描所有擷取事件的 `RenderMessage()`、訊息樣板、`Exception.ToString()` 與每個 `Properties` 值的 `ToString()`，都不得含該標記。掃描方法另以一筆只在屬性內放標記的事件驗證會偵測到。三種前提事件若有實際不存在或未帶相同 `TraceId` 者，不得刪除或改成無關事件來通過，停止並回到設計。HC-LOG-SUCCESS-HIDDEN 依序執行：呼叫兩個探測端點並斷言皆為 HTTP 200；呼叫非探測端點並等到其摘要；最後斷言這兩個路徑都沒有摘要事件。順序不可對調，否則探測摘要可能尚未寫入就通過。HC-LOG-FAILURE-EXCEPTION 使用會拋出標記例外的 `HealthCheckService`，呼叫兩個探測端點，斷言皆為 500，且各路徑的摘要為 Error。HC-LOG-EXCEPTION-NO-PASSWORD 將最低等級設為 Debug，連線字串使用專用密碼標記 `marker-real-db-password`，使用同一個拋出例外的 `HealthCheckService` 與新的 factory 實例，對兩個探測端點各只發一次請求並斷言皆為 500；對每個端點分別斷言：回應本文的 `traceId` 等於該路徑唯一一筆 Error 摘要的 `TraceId`，且帶相同 `TraceId` 的事件含 `SourceContext` 為 `ProjectC.WebApi.ExceptionHandling.GlobalExceptionHandler`、`Exception` 含例外標記的 Error 事件；兩端點的 `TraceId` 須不同。全部確認後，再以 HC-LOG-FAILURE-NO-PASSWORD 的掃描方法掃描所有擷取事件，不得含 `marker-real-db-password`。HC-LOG-OTHER-UNCHANGED 以預設等級呼叫 `GET /api/events`，斷言 200，等到該路徑的摘要，斷言等級為 Information。HC-LOG-OTHER-ERROR 使用新的 factory 實例，連線字串指向 `127.0.0.1:1`，Redis 維持可連線的測試容器；呼叫 `GET /api/events` 前，從 DI 取得 `IQueryCache`，以 `GetEventsHandler.CacheKey` 執行 `RemoveAsync`，再以 `GetAsync` 斷言 `IsHit` 為 false，確立快取未命中；呼叫後先斷言 500，等到該路徑的摘要，斷言等級為 Error，並斷言帶相同 `TraceId` 的事件含 `Microsoft.EntityFrameworkCore.Database.Connection` 的事件，證明該請求確實查詢了 DB。
  - 預期 RED：HC-LOG-SUCCESS-DEBUG 與 HC-LOG-SUCCESS-HIDDEN 失敗，Expected Debug 或無事件、Actual Information。HC-LOG-FAILURE-ERROR、HC-LOG-FAILURE-EXCEPTION、HC-LOG-FAILURE-NO-PASSWORD、HC-LOG-EXCEPTION-NO-PASSWORD、HC-LOG-OTHER-UNCHANGED 與 HC-LOG-OTHER-ERROR 是回歸保護，修改前可能已通過，須如實記錄。
  - 最小實作：在 `UseSerilogRequestLogging` 的 options 加 `GetLevel`（見 design 決策 5），並保留既有的 `options.Logger` 設定，否則日誌只會走 bootstrap logger。
  - GREEN／重構：同 2.1 的命令，全部通過。
  - 等級驗證：回歸保護在修改前就通過，無法證明它們抓得到錯誤，所以 GREEN 後暫時將 `GetLevel` 改為一律回 Debug，確認 HC-LOG-FAILURE-ERROR、HC-LOG-FAILURE-EXCEPTION、HC-LOG-OTHER-UNCHANGED、HC-LOG-OTHER-ERROR 的測試都失敗；再暫時讓非探測端點一律回 Information，確認 HC-LOG-OTHER-ERROR 的測試失敗。密碼掃描另外驗證：暫時讓測試的拋出例外將目前連線字串放進訊息，確認 HC-LOG-EXCEPTION-NO-PASSWORD 的測試因偵測到 `marker-real-db-password` 而失敗。TraceId 關聯另外驗證：暫時從 `Program.cs` 移除 `TraceIdLoggingMiddleware`，確認 HC-LOG-FAILURE-NO-PASSWORD、HC-LOG-EXCEPTION-NO-PASSWORD、HC-LOG-OTHER-ERROR 的測試都因摘要缺少 `TraceId` 而失敗，而不是通過。最後還原並重跑。
  - 還原驗證：N/A，不是 bug fix 或核心業務邏輯。
  - 完成紀錄：
    RED（`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~HealthCheckLoggingTests|FullyQualifiedName~HealthCheckHttpsRedirectionTests"`，於 `GetLevel`、`UseWhen` 修改前執行，摘要 `Failed!  - Failed:     5, Passed:     8, Skipped:     0, Total:    13, Duration: 17 s - ProjectC.WebApi.Tests.dll (net10.0)`）：
    - `Failed HealthCheckLoggingTests.HealthEndpoints_WhenSuccessfulAtDefaultLevel_LogNoSummary [1 s]`：`Expected GetSummaries(factory, LivePath) to be empty, but found at least one item {`
    - `Failed HealthCheckLoggingTests.HealthEndpoints_WhenSuccessfulAtDebugLevel_LogSummaryAsDebug [1 s]`：`Expected summary.Level to be LogEventLevel.Debug {value: 1}, but found LogEventLevel.Information {value: 2}.`
    回歸保護 HC-LOG-FAILURE-ERROR、HC-LOG-FAILURE-EXCEPTION、HC-LOG-FAILURE-NO-PASSWORD、HC-LOG-EXCEPTION-NO-PASSWORD、HC-LOG-OTHER-UNCHANGED、HC-LOG-OTHER-ERROR 與掃描方法驗證在修改前已通過；三種前提事件皆存在且 TraceId 相同。
    GREEN：`Passed!  - Failed:     0, Passed:    37, Skipped:     0, Total:    37, Duration: 32 s - ProjectC.WebApi.Tests.dll (net10.0)`
    等級驗證，一律回 Debug（摘要 `Failed!  - Failed:     4, Passed:     0, Skipped:     0, Total:     4, Duration: 39 s`）：`GetEvents_WhenDatabaseUnavailableAndCacheMissed_LogsSummaryAsError`：`Expected summary.Level to be LogEventLevel.Error {value: 4}, but found LogEventLevel.Debug {value: 1}.`；預設等級下 Debug 摘要被濾掉，所以另三個是等不到摘要而失敗：`GetHealthReady_WhenDatabaseUnavailable_LogsSummaryAsError`：`System.TimeoutException : 00:00:10 內沒有等到 /health/ready 的請求摘要。`；`HealthEndpoints_WhenHealthCheckServiceThrows_LogSummaryAsError`：`System.TimeoutException : 00:00:10 內沒有等到 /health/live 的請求摘要。`；`GetEvents_WhenSuccessful_LogsSummaryAsInformation`：`System.TimeoutException : 00:00:10 內沒有等到 /api/events 的請求摘要。`
    非探測一律回 Information：`GetEvents_WhenDatabaseUnavailableAndCacheMissed_LogsSummaryAsError`：`Expected summary.Level to be LogEventLevel.Error {value: 4}, but found LogEventLevel.Information {value: 2}.`
    密碼掃描驗證：`HealthEndpoints_WhenHealthCheckServiceThrowsAtDebugLevel_LogExceptionWithoutPassword`：`Did not expect (logEvent.Exception?.ToString() ?? string.Empty) "System.InvalidOperationException: marker-health-exception Host=marker-db marker-redisHost=127.0.0.1;Port=1;Database=projectc_tests;Username=projectc_tests;Password=marker-real-db-password ... to contain "marker-real-db-password".`
    TraceId 關聯驗證（移除 `TraceIdLoggingMiddleware`）：`GetHealthReady_WhenDatabaseUnavailableAtDebugLevel_LogsNoPassword`、`HealthEndpoints_WhenHealthCheckServiceThrowsAtDebugLevel_LogExceptionWithoutPassword`、`GetEvents_WhenDatabaseUnavailableAndCacheMissed_LogsSummaryAsError` 皆為：`Expected traceId not to be <null> or whitespace because 關聯事件前，摘要必須帶非空白的 TraceId，否則比對沒有意義, but found <null>.`
    還原後結果：`Passed!  - Failed:     0, Passed:    37, Skipped:     0, Total:    37, Duration: 33 s - ProjectC.WebApi.Tests.dll (net10.0)`。重構：N/A。

## 4. HTTPS 轉址

- [x] 4.1 將探測端點排除於 HTTPS 轉址之外。對應 HC-NO-REDIRECT、HC-NO-REDIRECT-CASE-INSENSITIVE、HC-REDIRECT-OTHER-UNCHANGED。
  - 測試先行：`tests/ProjectC.WebApi.Tests/HealthChecks/HealthCheckHttpsRedirectionTests.cs`；factory 設定 `HTTPS_PORT`，DB 沿用可連線的測試容器，client 以 HTTP 呼叫並關閉自動轉址。每個請求都讀取實際狀態碼：
    - HC-NO-REDIRECT：分別呼叫 `GET /health/live` 與 `GET /health/ready`，各斷言為 200，且不是 3xx、沒有 `Location` 標頭。
    - HC-NO-REDIRECT-CASE-INSENSITIVE：呼叫 `GET /Health/Live`，斷言為 200，且不是 3xx、沒有 `Location` 標頭。
    - HC-REDIRECT-OTHER-UNCHANGED：呼叫 `GET /api/events`，斷言為 3xx。
  - 預期 RED：HC-NO-REDIRECT 與 HC-NO-REDIRECT-CASE-INSENSITIVE 失敗，Expected 200、Actual 3xx。HC-REDIRECT-OTHER-UNCHANGED 以 `GET /api/events` 呼叫，修改前即通過，用來證明 `HTTPS_PORT` 設定確實生效，須如實記錄。
  - 最小實作：以 `UseWhen` 只讓非探測路徑進入 `UseHttpsRedirection`。
  - GREEN／重構：同 2.1 的命令，全部通過。
  - 還原驗證：N/A，不是 bug fix 或核心業務邏輯。
  - 完成紀錄：
    RED（與 3.1 同一次執行與摘要）：
    - `Failed HealthCheckHttpsRedirectionTests.HealthEndpoints_WhenHttpsPortConfigured_DoNotRedirect(path: "/health/live") [3 s]`：`Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.RedirectKeepVerb {value: 307}.`
    - `Failed HealthCheckHttpsRedirectionTests.HealthEndpoints_WhenHttpsPortConfigured_DoNotRedirect(path: "/health/ready") [2 s]`：`Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.RedirectKeepVerb {value: 307}.`
    - `Failed HealthCheckHttpsRedirectionTests.GetHealthLive_WhenPathCaseDiffers_DoesNotRedirect [2 s]`：`Expected response.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.RedirectKeepVerb {value: 307}.`
    HC-REDIRECT-OTHER-UNCHANGED（`GetEvents_WhenHttpsPortConfigured_Redirects`）修改前即通過，證明 `HTTPS_PORT` 生效。
    GREEN：`Passed!  - Failed:     0, Passed:    37, Skipped:     0, Total:    37, Duration: 32 s - ProjectC.WebApi.Tests.dll (net10.0)`。重構：N/A。

## 5. 文件

- [x] 5.1 更新 `docs/k8s-todo.md` 第 1 項：`/health/ready` 改為只檢查 DB，記錄理由（Redis 故障不應讓所有 Pod 移出 Service）與已接受的 captcha 取捨；標註由 `api-health-checks` 處理；加入前置條件：正式部署前須完成 `docs/exception-log-masking-todo.md` 的遮蔽 change（design「已核准的安全規則例外」）；加入上線 gate：K8s 或 compose／VPS 部署前，須只讓可信來源（部署平台探針、維運人員）存取 `/health/live` 與 `/health/ready`，規則須放行探針來源並實測不可信來源無法連到，未完成前不得讓 api 暴露給不受信任的網路或工作負載（design Risks）；在「驗收方向」加入部署提案的必要 AC：已設定來源限制，且探針來源可達並使 Pod Ready、維運來源可達、不可信 workload 與外部來源不可達，並註明 VPS（compose）部署提案同樣適用；將開頭狀態段對 VPS 記憶的引用改為指向 `docs/deployment-prerequisites.md`。驗證：`git diff docs/k8s-todo.md` 只動開頭狀態段、第 1 項與「驗收方向」，且後兩處都含上述條件。本 task 只寫入 gate，不代表來源限制已完成；實際設定與連線驗證由部署 change 執行。
  - TDD：N/A，純文件。

## 6. 最終驗證

- [x] 6.1 hardener 最終檢查：程式碼全部完成後套用 `.claude/skills/hardener/SKILL.md`，涵蓋探測端點與管線變更。發現缺口時依 TDD 補 RED 測試再修改，並新增「hardener 補強」task。
  - 完成紀錄：參數驗證：端點無外部參數。Entity 存在性、狀態機：不適用。`CancellationToken`：傳入 `CanConnectAsync`。並發：每次檢查有獨立 scope 與 `DbContext`，併發壓力由部署前置條件的來源限制約束。例外邊界：未預期例外走全域 `GlobalExceptionHandler`（HC-BODY-EXCEPTION）。日誌遮蔽：HC-LOG-FAILURE-NO-PASSWORD、HC-LOG-EXCEPTION-NO-PASSWORD 涵蓋。未發現缺口，沒有修改程式碼。

- [x] 6.2 執行全套測試：`docker compose exec api dotnet test`，確認沒有回歸，尤其是日誌與 HTTPS 相關測試。
  - TDD：N/A，回歸驗證。
  - 完成紀錄（每個測試專案的摘要行原文；失敗則列出每個測試名稱與訊息）：
    - `Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 93 ms - ProjectC.Domain.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   527, Skipped:     0, Total:   527, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 9 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: 50 s - ProjectC.Infrastructure.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   582, Skipped:     0, Total:   582, Duration: 2 m 49 s - ProjectC.WebApi.Tests.dll (net10.0)`

- [x] 6.3 實機驗證：在 compose 環境從容器內呼叫兩個端點，確認皆為 200 且內容為 `Healthy`；`docker compose stop db` 後確認 ready 為 503、live 為 200；再 `docker compose start db` 確認 ready 恢復 200。
  - TDD：N/A，補充驗證真實容器環境；自動化行為由 2.x 涵蓋。
  - 完成紀錄（呼叫輸出原文，格式為 本文 [狀態碼] 耗時）：
    - 正常：live `Healthy [200] 0.003274s`；ready `Healthy [200] 0.030224s`
    - `docker compose stop db` 後：ready `Unhealthy [503] 0.015416s`；live `Healthy [200] 0.001063s`
    - `docker compose start db` 後：ready `Healthy [200] 0.012989s`；live `Healthy [200] 0.000980s`
    - 7.1 修正後補充（`docker compose pause db` 模擬運作中 DB 凍結）：ready `Unhealthy [503] 2.028365s`；`docker compose unpause db` 後 ready `Healthy [200] 0.015250s`

- [ ] 6.4 探測流程直接呼叫相依的替代驗證（design.md「已核准的驗證例外」）。對應 HC-LIVE-NO-CHECKS、HC-READY-ONLY-DATABASE 所屬 Requirement 中繞過 Health Check 機制的部分。
  - TDD：N/A，已核准的替代驗證。
  - 檢查一：`git grep -nE --untracked "IConnectionMultiplexer|IDatabase\b|IDistributedLock|IQueryCache|HttpClient|Seq|Redis" -- src/ProjectC.WebApi/HealthChecks`，須無輸出（exit code 1）。
  - 檢查二：`git diff master -- src/ProjectC.WebApi/Program.cs` 中，新增的 `Use`、`Map`、`Run`、`ResponseWriter` 行只能是兩個 `MapHealthChecks`，以及排除 HTTPS 轉址的 `UseWhen` 與其內的 `UseHttpsRedirection`；有其他新增即不通過。
  - 檢查三：`DatabaseReadinessHealthCheck` 的建構子只依賴 `ApplicationDbContext`，`CheckHealthAsync` 除了為本次檢查設定連線 `Timeout` 與 `Pooling`（design.md 決策 3）之外，只呼叫 `CanConnectAsync`。
  - 失敗情境驗證：在 `DatabaseReadinessHealthCheck` 暫時加入 `IConnectionMultiplexer` 欄位，確認檢查一有輸出；在 `Program.cs` 暫時加入一個 `app.Use`，確認檢查二不通過；還原後重跑三項。
  - `strict-reviewer` 須核對完整探測路徑（管線、端點對應與 `DatabaseReadinessHealthCheck`）符合以上三項，不得只確認檢查命令的字面結果。
  - 完成紀錄（三項檢查與失敗情境的輸出原文、還原後結果、strict-reviewer 結論）：
    - 檢查一：無輸出，`check1 exit=1`。
    - 檢查二：新增行為 `app.UseWhen(`、`branch => branch.UseHttpsRedirection());`、兩個 `app.MapHealthChecks(...)`，另有 `app.UseSerilogRequestLogging(options =>`：這是既有呼叫改為多行以加入 `GetLevel`，不是新增 middleware。
    - 檢查三：建構子只依賴 `ApplicationDbContext`；`CheckHealthAsync` 除設定連線 `Timeout` 與 `Pooling` 外只呼叫 `CanConnectAsync`。
    - 失敗情境：加入欄位後檢查一輸出 `src/ProjectC.WebApi/HealthChecks/DatabaseReadinessHealthCheck.cs:14:    private readonly StackExchange.Redis.IConnectionMultiplexer _x = null!;`，`exit=0`；加入 `app.Use` 後檢查二多出 `+    app.Use((context, next) => next());`，不通過。
    - 還原後：檢查一無輸出，`exit=1`。
    - strict-reviewer 結論：見審查迴圈，完成後補上。

## 7. 審查補強

- [x] 7.1 預熱後 DB 凍結時維持 2 秒上限（HC-READY-TIMEOUT，第 1 輪對抗式審查發現，design 決策 3）。
  - 測試先行：`GetHealthReady_WhenDatabaseFreezesAfterSuccessfulProbe_ReturnsServiceUnavailableWithinTimeout`，以 `FreezableTcpProxy` 在探測成功後凍結 DB。
  - 還原驗證：N/A，RED 即為未修改正式程式碼時的失敗；不是核心業務邏輯。
  - 完成紀錄：
    RED（`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~GetHealthReady_WhenDatabaseFreezesAfterSuccessfulProbe"`，於修改正式程式碼前執行）：`GetHealthReady_WhenDatabaseFreezesAfterSuccessfulProbe_ReturnsServiceUnavailableWithinTimeout`：`Expected stopwatch.Elapsed to be less than or equal to 4s, but found 34s, 14ms and 587.0µs.`；`Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 36 s - ProjectC.WebApi.Tests.dll (net10.0)`
    GREEN（加上 `Pooling = false`）：`Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 34 s - ProjectC.WebApi.Tests.dll (net10.0)`。重構：N/A。
- [x] 7.2 HC-READY-DNS-FAILURE：名稱無法解析時回 503（第 2 輪對抗式審查，design 決策 3 收窄 2 秒上限）。
  - TDD：N/A，規格收窄後補的既有行為驗證，正式程式碼未修改，沒有 RED。
  - 完成紀錄：見下方 7.3 的 GREEN。

- [x] 7.3 兩個端點的匿名 metadata 測試（HC-ANONYMOUS，第 2 輪對抗式審查：移除 `.AllowAnonymous()` 時現有測試全過）。
  - TDD：既有實作已符合，以暫時移除 `.AllowAnonymous()` 取代 RED；不是核心業務邏輯。
  - 完成紀錄：
    移除時：
    Expected endpoint!.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() not to be <null>.
    Failed ProjectC.WebApi.Tests.HealthChecks.HealthLiveEndpointTests.GetHealthLive_Metadata_AllowAnonymous [2 ms]
    Failed ProjectC.WebApi.Tests.HealthChecks.HealthReadyEndpointTests.GetHealthReady_Metadata_AllowAnonymous [6 ms]
    Failed!  - Failed:     2, Passed:    39, Skipped:     0, Total:    41, Duration: 38 s - ProjectC.WebApi.Tests.dll (net10.0)
    還原後 GREEN：`Passed!  - Failed:     0, Passed:    41, Skipped:     0, Total:    41, Duration: 38 s - ProjectC.WebApi.Tests.dll (net10.0)`。重構：N/A。

## Workflow follow-up

- [x] 實作後執行 `graphify update .`。
- 沒有前端變更，不需瀏覽器驗證。
