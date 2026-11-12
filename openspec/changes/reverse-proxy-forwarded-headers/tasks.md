# Tasks

> 紀錄欄位依 CLAUDE.md「測試規範」：RED／GREEN／重構／還原驗證貼實際輸出。指令皆在容器內執行（`docker compose exec api dotnet test …`）。

## 1. 轉送標頭還原（client-address-resolution）

- [ ] 1.1 `ForwardedHeaders` 設定類別與 CIDR 解析（對應 CAR-005、CAR-006、CAR-007）
  - 測試先行：`ForwardedHeadersSettingsTests`（單元）——預設網段內容；自訂網段取代預設；非法 CIDR、空陣列拋出含 `ForwardedHeaders:TrustedNetworks` 的例外。
  - 空陣列與缺漏的區分：以設定鍵是否存在（`GetSection(...).Exists()`）判斷；空字串項目視為非法。實作時對照已安裝套件驗證陣列綁定行為。
  - 預期 RED：先建立最小骨架使測試可編譯，斷言失敗（預設網段為空、未驗證非法值）。
  - GREEN：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ForwardedHeadersSettings"` 全過。
  - 實作前先 build 並檢查已安裝套件的 `ForwardedHeadersOptions` 實際屬性（`KnownIPNetworks`／`KnownNetworks`），以實際為準。
  - 紀錄：RED／GREEN（待實作時填寫）
- [ ] 1.2 `UseForwardedHeaders` 掛入 pipeline 並在啟動時解析設定（對應 CAR-001～004、CAR-006～013）
  - 測試先行：整合測試以測試 middleware 模擬連線來源（受信任／非信任），帶 `X-Forwarded-For`／`X-Forwarded-Proto`，經由回報 `RemoteIpAddress`／`Scheme` 的測試端點觀察；`X-Forwarded-For: a, b` 取最後一個；`/health/live` 帶 `X-Forwarded-Proto: http` 不被轉址；不帶 `X-Forwarded-For` 時維持連線位址（CAR-004）；非信任來源帶標頭被忽略（CAR-002、CAR-010 非信任部分）；受信任來源 `X-Forwarded-Proto: https` 還原 Scheme（CAR-010）；受信任來源最後值無法解析的 `X-Forwarded-For`（`garbage`、空字串、超長字串、`203.0.113.7, garbage`）不失敗且 `RemoteIpAddress` 維持連線位址、落入同一限流分區（CAR-009）；`garbage, 203.0.113.7` 與 `203.0.113.7:8080` 先實測框架行為再定預期（CAR-011）；CAR-008 與 CAR-013 的測試須設定 HTTPS 轉址連接埠（沿用 `HealthCheckHttpsRedirectionTests` 的 `HttpsPort = "443"` 設定），並含對照斷言：無標頭／非信任來源對 `GET /api/events` 確實被轉址、`/health/live` 帶 `X-Forwarded-Proto: http` 不被轉址，避免未設連接埠時空轉；`X-Forwarded-Host` 不改 `Request.Host`（CAR-012）；自訂網段下 `192.168.1.5` 來源標頭被忽略（CAR-006）；以 `WithWebHostBuilder` 驗證非法 CIDR／空陣列啟動失敗（CAR-007）；記錄 IPv4-mapped IPv6 來源（`::ffff:10.x.x.x`）是否視為受信任的實測結果。
  - 預期 RED：未註冊 middleware 時，受信任來源的標頭未被還原（Expected `203.0.113.7` but found 模擬連線位址）。
  - GREEN：相關測試通過；`ResolveEagerlyValidatedOptions` 啟動即解析（非法設定啟動失敗，由 1.1 覆蓋）。
  - 紀錄：RED／GREEN／重構（待填）

## 2. 限流（api-rate-limiting）

- [ ] 2.1 `PublicReadRateLimitingOptions` 與 `public-read` policy（對應 PRL-005、PRL-006）
  - 測試先行：options 預設 120／60、`Range` 驗證擋 0／負數、啟動時 `OptionsValidationException`（比照 `CaptchaRateLimitingOptions` 既有測試）。
  - 預期 RED：型別不存在／預設值與驗證不符。
  - GREEN：對應測試通過；`appsettings.json` 補 `PublicReadRateLimiting` 預設區段。
  - 紀錄：（待填）
- [ ] 2.2 `EventsController` 四個 GET 套用 `public-read`（對應 PRL-001～004）
  - 測試先行：整合測試（低額度測試設定）——超額 429＋`Retry-After`＋`ProblemDetails`；不同 `X-Forwarded-For`（受信任模擬）各自計數；超額後 `GET /api/captcha`、`POST /api/auth/login`、`/health/live` 不受影響；四個端點共用額度（列表打滿後詳情／seats／ticket-types 也 429）；429 時查詢未執行（以 fake／spy 的呼叫次數斷言，不讀快取）。
  - 預期 RED：未套用 policy 時超額請求仍回 200（Expected 429 but found 200）。
  - GREEN：相關測試通過；既有 `EventsController` 測試不退步。
  - 紀錄：（待填）
- [ ] 2.3 login／captcha 在轉送還原後依用戶端 IP 各自計數（對應 LRL-011、LRL-012）
  - 測試先行：整合測試——受信任來源下兩個 `X-Forwarded-For` 各打滿登入額度互不觸發 429；非信任來源每次帶不同標頭仍落入同一分區並在超額後 429；captcha 同。
  - 預期 RED：此 task 排在 1.2 之後，RED 以暫時移除 1.2 的 middleware 註冊驗證測試確實會失敗（Expected 第二個用戶端 200 but found 429），貼 `mutate`／stash 輸出。
  - GREEN：相關測試與既有 `LoginRateLimitingTests`、`CaptchaRateLimitTests` 全過。
  - 既有 LRL 追溯（回歸，GREEN 須重跑並貼原文）：LRL-001 `Login_WithRequestsUnderTheLimit_AllSucceedWithoutBeingRateLimited`；LRL-002 `Login_WithExactlyThePermitLimitRequests_AllSucceedWithoutBeingRateLimited`；LRL-003 `Login_WithCorrectCredentialsAsTheRequestOverTheLimit_IsStillRejectedWithoutIssuingTokens`；LRL-005 `Login_AfterTheWindowResets_RequestsAreAllowedAgain`；LRL-006 `OrdersRateLimitingTests.PlaceOrderAndLogin_UsageOnPlaceOrderDoesNotAffectLogin`；LRL-010 `Login_WhenRateLimited_ReturnsProblemDetailsWithRetryAfterHeader`；LRL-004 由本 task 新增的 LRL-011 測試（兩個不同用戶端 IP 各自計數）與 `LoginRateLimiterPartitioningTests` 覆蓋（TestServer 來源 IP 固定，既有整合測試無法驗證不同 IP）。實作時若發現任一對應不成立，補測試。
  - LRL-012 為回歸防護（現況即通過，不產生 RED）；LRL-011 以上述 mutate 取得 RED。
  - 還原驗證（核心安全邏輯）：依 CLAUDE.md 以 `bash scripts/verify-test-evidence.sh restore`／`mutate` 執行並貼原文。
  - 紀錄：（待填）

## 3. 部署設定

- [ ] 3.1 `deploy/nginx/default.conf` 轉送標頭＋`k8s/50-web.yaml` 的 `externalTrafficPolicy: Local`
  - TDD：N/A（設定檔，無單元測試層）；改以 3.2 的腳本斷言取代：先補斷言，改設定前新斷言失敗（貼輸出），改後通過。
  - 紀錄：（待填）
- [ ] 3.2 `scripts/verify-k8s-manifests.sh` 補斷言：nginx 設定含 `X-Forwarded-For $remote_addr` 與 `X-Forwarded-Proto`；`web` Service `externalTrafficPolicy: Local`；既有斷言仍通過
  - 驗證：在 kind 叢集執行腳本，貼各斷言摘要行；並實測經 NodePort 帶偽造 `X-Forwarded-For` 的請求，API 看到的來源是 nginx 覆蓋後的值（觀察限流或日誌）。通過判準為「偽造標頭被 nginx 覆蓋」，不要求看到特定外部 IP（Docker Desktop 的 kind 經 NodePort 進入時來源可能被 NAT 成閘道位址），紀錄須註明實測環境限制。
  - 紀錄：（待填）
- [ ] 3.3 檢查 `loadtest/` 腳本是否會被 `public-read` 限流；必要時於壓測設定覆寫額度，不改預設值
  - TDD：N/A（壓測腳本／設定）；以 `docker compose config` 或腳本 dry-run 確認覆寫生效。
  - 紀錄：（待填）

## 4. 文件與同步

- [ ] 4.1 更新 `docs/api-conventions.md`（限流 policy、分區鍵為轉送還原後 IP、新設定區段）、K8s 指南（`externalTrafficPolicy: Local` 限制）、`docs/review/TODO.md`（C-2、M-3 標記完成）
  - TDD：N/A（純文件）。驗證：`bash scripts/check-doc-paths.sh`。
  - 紀錄：（待填）
- [ ] 4.2 spec 同步檢查：`openspec validate reverse-proxy-forwarded-headers`；封存前確認 `api-rate-limiting` MODIFIED 區塊與主 spec 逐字一致（除部署前提段落與新增 Scenario）
  - 紀錄：（待填）

## 5. 收尾

- [ ] 5.1 hardener 最終檢查（新增的 middleware／設定解析屬外部輸入處理：CIDR 解析、設定錯誤 fail-fast、日誌不洩漏；取消傳遞不適用）。結果記錄於此。
  - 紀錄：（待填）
- [ ] 5.2 全套測試（Domain／Application／Infrastructure／WebApi）各專案摘要行貼原文；`graphify update .`
  - 紀錄：（待填）
