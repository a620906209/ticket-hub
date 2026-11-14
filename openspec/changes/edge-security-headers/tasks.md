# Tasks

## AC → 測試對照

| Scenario | 對應 task／測試 |
|---|---|
| ESH-001 首頁安全標頭 | 2.1 `verify-edge-headers.sh` |
| ESH-002 靜態資源安全標頭 | 2.1 |
| ESH-003 API 代理回應安全標頭 | 2.1（上游替身回 200／4xx／5xx） |
| ESH-004 邊界 404 安全標頭 | 2.1 |
| ESH-005 CSP 值逐字相符 | 2.1 |
| ESH-006 真實瀏覽器無 CSP 違規 | 6.1 真實瀏覽器驗證（design.md「核准例外」，已核准） |
| ESH-007 HTTPS 請求帶 HSTS | 2.1 |
| ESH-008 非 HTTPS 不帶 HSTS | 2.1 |
| ESH-009 API 直連不送 HSTS | 3.1 `EdgeBoundaryNoCorsNoHstsTests` |
| ESH-010 大型文字資源壓縮 | 2.1 |
| ESH-011 小型回應不壓縮 | 2.1 |
| ESH-012 未宣告 gzip 不壓縮 | 2.1 |
| ESH-013 無 CORS 許可標頭 | 3.1 |
| HHV-001 不在清單的 Host 被拒 | 1.1 `HostFilteringTests` |
| HHV-002 清單內 Host 正常 | 1.1 |
| HHV-003 預設設定不限制 | 1.1 |
| HHV-004 K8s 設定不使用 `*` | 1.2 `verify-k8s-manifests.sh` |
| HHV-005 已部署 API 拒絕偽造 Host | 1.2 |
| HHV-006 啟用限制後 Pod 就緒 | 1.2 |
| HHV-007 經 nginx 以合法網域存取 | 1.2 |
| HHV-008 經 nginx 以未列出網域存取 | 1.2 |
| CIP-001 外部 FROM 皆含 digest | 4.1 `verify-image-pinning.sh` |
| CIP-002 釘版後映像可建置 | 4.2 `docker build` |
| CIP-003 第三方映像含 digest | 4.1 |
| CIP-004 釘版後叢集可就緒 | 4.2（`verify-k8s-manifests.sh`） |
| CIP-005 文件涵蓋更新步驟 | 4.1 `verify-image-pinning.sh` 的文件內容斷言 |

## 1. Host 驗證（AllowedHosts）

- [x] 1.1 API 整合測試固定 Host 驗證行為（對應 HHV-001、002、003）
  - 測試先行（`tests/ProjectC.WebApi.Tests/Startup/HostFilteringTests.cs`，`WithWebHostBuilder` 覆寫 `AllowedHosts`）：
    - `GetHealthLive_WhenHostNotAllowed_Returns400`（Host `evil.example`）
    - `GetApiPath_WhenHostNotAllowed_Returns400`（Host `evil.example`，任一 `/api/...` 路徑）
    - `GetHealthLive_WhenHostAllowed_Returns200`（含帶連接埠）
    - `GetHealthLive_WhenAllowedHostsIsDefault_Returns200ForAnyHost`
  - TDD：框架既有行為的特性測試，程式碼不需變更，因此沒有「因行為不符而失敗」的 RED；以**還原驗證**取代：`bash scripts/verify-test-evidence.sh mutate` 暫時把工廠覆寫的 `AllowedHosts` 改成 `*`，確認前兩個斷言失敗，再還原。
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~HostFilteringTests"` 全數通過
  - 紀錄（原文）：
    ```text
    GREEN（$ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~HostFilteringTests"）：
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    
    Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 651 ms - ProjectC.WebApi.Tests.dll (net10.0)
    
    RED 替代（還原驗證）：$ bash scripts/verify-test-evidence.sh mutate --file tests/ProjectC.WebApi.Tests/Startup/HostFilteringTests.cs --from 'ConfiguredAllowedHosts = "app.example"' --to 'ConfiguredAllowedHosts = "*"' -- docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~HostFilteringTests"
    mutation: ConfiguredAllowedHosts = "app.example" -> ConfiguredAllowedHosts = "*"
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~HostFilteringTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:03.46]     ProjectC.WebApi.Tests.Startup.HostFilteringTests.GetApiPath_WhenHostNotAllowed_Returns400 [FAIL]
    [xUnit.net 00:00:03.59]     ProjectC.WebApi.Tests.Startup.HostFilteringTests.GetHealthLive_WhenHostNotAllowed_Returns400 [FAIL]
      Failed ProjectC.WebApi.Tests.Startup.HostFilteringTests.GetApiPath_WhenHostNotAllowed_Returns400 [640 ms]
      Error Message:
       Expected status to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.OK {value: 200}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Startup.HostFilteringTests.GetApiPath_WhenHostNotAllowed_Returns400() in /src/tests/ProjectC.WebApi.Tests/Startup/HostFilteringTests.cs:line 56
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.Startup.HostFilteringTests.GetHealthLive_WhenHostNotAllowed_Returns400 [60 ms]
      Error Message:
       Expected status to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.OK {value: 200}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Startup.HostFilteringTests.GetHealthLive_WhenHostNotAllowed_Returns400() in /src/tests/ProjectC.WebApi.Tests/Startup/HostFilteringTests.cs:line 45
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     2, Passed:     4, Skipped:     0, Total:     6, Duration: 848 ms - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    （mutate 結束後檔案已還原：ConfiguredAllowedHosts = "app.example"，git status 僅剩新檔 HostFilteringTests.cs）
    ```

- [ ] 1.2 K8s：ConfigMap 加 `AllowedHosts`、三個探針加 `Host` 標頭（對應 HHV-004～008）
  - 測試先行：先在 `scripts/verify-k8s-manifests.sh` 新增斷言——ConfigMap 有 `AllowedHosts` 且不含 `*` 且含 `api`；`Host: evil` 請求 `/health/live` 回 400；`Host: localhost` 經 web 請求 `/api/...` 不為 400；`Host: evil` 經 web 請求 `/api/...` 回 400；API Pod Ready。既有的直連 API 斷言（8b，`http://127.0.0.1:<port>/health/live`）須改帶 `Host: localhost`，否則會被新設定擋成 400；同時確認 `deploy/docker-compose.verify.yml` 與 k6 腳本直連 API 的呼叫端（走預設 `*` 者不受影響）。維運以 port-forward 存取 API 也須用清單內主機名稱，寫入 5.1 文件。
  - 預期 RED：尚未改 manifest 時，ConfigMap 斷言 FAIL 且 `Host: evil` 得 200（`Expected 400, got 200` 一類原文）。
  - 最小實作：`k8s/10-configmap.yaml` 加 `AllowedHosts: "localhost;api"`（註解說明上線前改實際網域）；`k8s/40-api.yaml` 三個探針加 `httpHeaders: [{name: Host, value: api}]`。
  - GREEN：`bash scripts/verify-k8s-manifests.sh`（本機 docker-desktop 叢集）全 PASS；需使用者環境可用 kubectl，不可用時於回報註明 UNVERIFIED。
  - 紀錄（原文）：
    - 步驟：`scripts/verify-k8s-manifests.sh` 8b 的 curl 改帶 `-H 'Host: localhost'`（原第 451 行直連）；新增斷言 11a～11d（ConfigMap AllowedHosts、三探針 Host、直連 api `Host: evil`→400／`Host: api`→200、經 web `localhost`→2xx／`evil.example`→400）。grep 其餘直連 API 的呼叫：`verify-production-images.sh`、`docker-compose.verify.yml`、k6 腳本皆用預設 `AllowedHosts=*`，不受影響；只有 8b 需要 Host。
    - 環境：docker-desktop 為 kind 模式節點（containerd 不與 docker 共用 image store），須 `docker save <image> | docker exec -i desktop-control-plane ctr -n k8s.io images import --all-platforms -` 載入 projectc/api、api-migrator、web 三個 :local 映像；本機 `localhost:30081` 不通（kind 節點未對主機開 NodePort；嘗試以 socat 容器轉發被 auto mode 分類器拒絕，未繞過）。
    - RED（改 manifest 前，節錄）：
    - ```text
      PASS [8b] kubectl port-forward 到 api 可取得 /health/live（維運來源可達）
      FAIL [11a] HHV-004 AllowedHosts 不符（預期非空、不含 *、含 api）：''
      FAIL [11b] HHV-006 探針 Host 或就緒副本不符：startupProbe 的 Host 標頭=none; livenessProbe 的 Host 標頭=none; readinessProbe 的 Host 標頭=none; （readyReplicas/replicas=2/2）
      FAIL [11c] HHV-005 直連 api 預期 Host: evil → 400、Host: api → 200，實際 evil=200 api=200
      FAIL [11d] HHV-007/008 經 web 預期 localhost → 2xx、evil.example → 400，實際 localhost=000 evil.example=000
      ```
    - GREEN（改 manifest 後，完整 PASS／FAIL 行）：
    - ```text
      PASS [10] nginx 以 $remote_addr 覆蓋 X-Forwarded-For 並傳遞 X-Forwarded-Proto；web Service externalTrafficPolicy: Local
      PASS [0] manifest 覆寫為 Namespace projectc-verify、NodePort 30081
      PASS [1] db／redis／seq／api／web 就緒（套用 NetworkPolicy 後仍就緒），migrator Complete，migration 筆數 16（預期 16），api 最大 restartCount=0
      PASS [2a] 探針路徑：liveness=/health/live readiness=/health/ready startup=/health/live
      PASS [2b] db 停止後 api Service endpoints 在 90 秒內清空（readiness 轉為 NotReady）
      PASS [2c] db 恢復後 api endpoints 在 150 秒內回到 Ready
      FAIL [3a] GET http://localhost:30081/ → 000（預期 200）
      FAIL [3b] GET /api/events → 000（預期 2xx）
      FAIL [4] recovered=1，請求總數=15（至少 20），非 2xx=15
      FAIL [5] rollout 結束碼=0，請求總數=18（至少 20），非 2xx=18
      PASS [6] 5 個隨機機敏值未出現在 logs、describe、k8s/、版控；Secret 鍵名不在 ConfigMap
      PASS [7] 缺 Jwt__SigningKey 時 api 進入 CrashLoopBackOff，上次結束碼=139
      FAIL [8a] /health/live 回應不是 SPA index.html（預期 nginx try_files 回 index.html）; 
      PASS [8b] kubectl port-forward 到 api 可取得 /health/live（維運來源可達）
      PASS [11a] HHV-004 k8s/10-configmap.yaml 的 AllowedHosts=localhost;api（非空、不含 *、含 api）
      PASS [11b] HHV-006 三個探針皆帶 Host: api，api 就緒副本 2/2
      PASS [11c] HHV-005 直連 api：Host: evil → 400，Host: api → 200
      FAIL [11d] HHV-007/008 經 web 預期 localhost → 2xx、evil.example → 400，實際 localhost=000 evil.example=000
      PASS [8c] CNI 有執行 NetworkPolicy：不可信 Pod 連不到 api／redis／db，app=web 可連 api
      PASS [9] api 為 ClusterIP、僅 web 為 NodePort（30081）、api／web／migrator 皆 runAsNonRoot、OpenAPI／SwaggerUI 不可經 web 取得
      FAIL [10d] 12 次登入請求各帶不同偽造 X-Forwarded-For，沒有出現 429（偽造標頭未被覆蓋？）：000 000 000 000 000 000 000 000 000 000 000 000
      ```
    - 判讀：11a、11b、11c 由 FAIL 轉 PASS（目標行為）。3a／3b／4／5／8a／10d／11d 失敗皆為 HTTP 000（主機連不到 localhost:30081），RED 與 GREEN 相同、與本 change 無關。**11d（HHV-007／008）UNVERIFIED**：經 web 的 Host 行為尚未在叢集內驗證。**UNVERIFIED 項目與原因（使用者 2026-11-14 決定接受、之後補驗、不轉發 30081）**：`verify-k8s-manifests.sh` 的 3a、3b、4、5、8a、10d、11d——原因皆為 docker-desktop 為 kind 模式，NodePort 30081 主機連不到（HTTP 000）。其中 11d（HHV-007／008）為本 change 的行為斷言，其餘為既有斷言。本 task 維持未勾選，補驗清單與回填位置見 `docs/todo/2026-11-14-edge-security-headers-k8s-verification-todo.md`。
    - 

## 2. nginx 安全標頭、HSTS、gzip

- [x] 2.1 `verify-edge-headers.sh` 與 nginx 設定（對應 ESH-001～005、007、008、010～012）
  - 測試先行：新增 `scripts/verify-edge-headers.sh`（建暫時 network，以 `projectc/web:local` 加簡易設定當 `api` 上游替身；對 `/`、`/assets/<檔>`、`/assets/不存在`、`/api/...`（上游 200／4xx／5xx）逐路徑斷言五個安全標頭存在且各只出現一次；CSP 值逐字比對；`X-Forwarded-Proto: https` 才有 `Strict-Transport-Security: max-age=15552000`、`http`／缺省沒有；gzip（JS 與 CSS 各驗一個、小型、無 Accept-Encoding）；`nginx -t` 與 `nginx -T` 核對 JS 的 Content-Type 在 `gzip_types` 內）。腳本結束時清除暫時容器與 network，任一斷言失敗即非 0 結束。
  - 預期 RED：以尚未修改的 `default.conf` 建置映像執行，標頭斷言以 `FAIL: / 缺少 Content-Security-Policy` 之類訊息失敗、gzip 斷言失敗（非腳本或環境錯誤）。
  - 最小實作：新增 `deploy/nginx/security-headers.conf`（五個標頭＋HSTS map 用法），`default.conf` 加 `map`、gzip 設定，並在 `/api/`、`/assets/`、`/` 三個 location 各 `include`；`deploy/web.Dockerfile` COPY snippet。
  - GREEN：`bash scripts/verify-edge-headers.sh` 全 PASS。
  - 紀錄（原文）：
    ```text
    RED（$ bash scripts/verify-edge-headers.sh；以尚未修改的 default.conf、無 security-headers.conf 執行，退出碼 1。此版腳本尚無「Dockerfile／include 靜態斷言」，那兩組斷言與 gzip 暫存路徑修正是 GREEN 前才補上）：
    FAIL [ESH-001/005] http://127.0.0.1:10389/ 缺少 Content-Security-Policy
    FAIL [ESH-001/005] http://127.0.0.1:10389/ 缺少 X-Content-Type-Options
    FAIL [ESH-001/005] http://127.0.0.1:10389/ 缺少 X-Frame-Options
    FAIL [ESH-001/005] http://127.0.0.1:10389/ 缺少 Referrer-Policy
    FAIL [ESH-001/005] http://127.0.0.1:10389/ 缺少 Permissions-Policy
    FAIL [ESH-002/005] http://127.0.0.1:10389/assets/RedemptionScannerPage-DuDr3aDd.js 缺少 Content-Security-Policy
    FAIL [ESH-002/005] http://127.0.0.1:10389/assets/RedemptionScannerPage-DuDr3aDd.js 缺少 X-Content-Type-Options
    FAIL [ESH-002/005] http://127.0.0.1:10389/assets/RedemptionScannerPage-DuDr3aDd.js 缺少 X-Frame-Options
    FAIL [ESH-002/005] http://127.0.0.1:10389/assets/RedemptionScannerPage-DuDr3aDd.js 缺少 Referrer-Policy
    FAIL [ESH-002/005] http://127.0.0.1:10389/assets/RedemptionScannerPage-DuDr3aDd.js 缺少 Permissions-Policy
    FAIL [ESH-004] http://127.0.0.1:10389/assets/does-not-exist.js 缺少 Content-Security-Policy
    FAIL [ESH-004] http://127.0.0.1:10389/assets/does-not-exist.js 缺少 X-Content-Type-Options
    FAIL [ESH-004] http://127.0.0.1:10389/assets/does-not-exist.js 缺少 X-Frame-Options
    FAIL [ESH-004] http://127.0.0.1:10389/assets/does-not-exist.js 缺少 Referrer-Policy
    FAIL [ESH-004] http://127.0.0.1:10389/assets/does-not-exist.js 缺少 Permissions-Policy
    FAIL [ESH-003/005] http://127.0.0.1:10389/api/ok 缺少 Content-Security-Policy
    FAIL [ESH-003/005] http://127.0.0.1:10389/api/ok 缺少 X-Content-Type-Options
    FAIL [ESH-003/005] http://127.0.0.1:10389/api/ok 缺少 X-Frame-Options
    FAIL [ESH-003/005] http://127.0.0.1:10389/api/ok 缺少 Referrer-Policy
    FAIL [ESH-003/005] http://127.0.0.1:10389/api/ok 缺少 Permissions-Policy
    FAIL [ESH-003] http://127.0.0.1:10389/api/client-error 缺少 Content-Security-Policy
    FAIL [ESH-003] http://127.0.0.1:10389/api/client-error 缺少 X-Content-Type-Options
    FAIL [ESH-003] http://127.0.0.1:10389/api/client-error 缺少 X-Frame-Options
    FAIL [ESH-003] http://127.0.0.1:10389/api/client-error 缺少 Referrer-Policy
    FAIL [ESH-003] http://127.0.0.1:10389/api/client-error 缺少 Permissions-Policy
    FAIL [ESH-003] http://127.0.0.1:10389/api/server-error 缺少 Content-Security-Policy
    FAIL [ESH-003] http://127.0.0.1:10389/api/server-error 缺少 X-Content-Type-Options
    FAIL [ESH-003] http://127.0.0.1:10389/api/server-error 缺少 X-Frame-Options
    FAIL [ESH-003] http://127.0.0.1:10389/api/server-error 缺少 Referrer-Policy
    FAIL [ESH-003] http://127.0.0.1:10389/api/server-error 缺少 Permissions-Policy
    PASS [ESH-002] 靜態資源同時保有長期快取的 Cache-Control
    FAIL [ESH-007] /assets/RedemptionScannerPage-DuDr3aDd.js 的 HSTS 出現 0 次、值 []
    FAIL [ESH-007] /api/ok 的 HSTS 出現 0 次、值 []
    PASS [ESH-008] /（X-Forwarded-Proto: http）沒有 HSTS
    PASS [ESH-008] /api/ok（X-Forwarded-Proto: http）沒有 HSTS
    PASS [ESH-008] /（無 X-Forwarded-Proto）沒有 HSTS
    PASS [ESH-008] /api/ok（無 X-Forwarded-Proto）沒有 HSTS
    FAIL [ESH-010(js)] RedemptionScannerPage-DuDr3aDd.js 未壓縮（Content-Encoding：[]，Content-Type：[application/javascript]）
    FAIL [ESH-010(css)] index-B9ObGmIc.css 未壓縮（Content-Encoding：[]，Content-Type：[text/css]）
    PASS [ESH-011] 帶 Content-Length 的小型回應未壓縮
    PASS [ESH-012] 未宣告 Accept-Encoding 時 RedemptionScannerPage-DuDr3aDd.js 不壓縮
    PASS [ESH-010] nginx -t 通過
    FAIL [ESH-010] nginx -T 的 gzip_types 不含 RedemptionScannerPage-DuDr3aDd.js 的 Content-Type（application/javascript）
    FAIL [ESH-010] nginx -T 的 gzip_types 不含 index-B9ObGmIc.css 的 Content-Type（text/css）
    
    結果：PASS 8、FAIL 36
    
    GREEN（$ bash scripts/verify-edge-headers.sh，退出碼 0，結束後 docker ps -a／docker network ls 無殘留 edge-verify 資源）：
    PASS [ESH-001] deploy/web.Dockerfile 複製 security-headers.conf 至 /etc/nginx/snippets/security-headers.conf
    PASS [ESH-001] default.conf 的 location /api/ 有 include security-headers.conf
    PASS [ESH-001] default.conf 的 location /assets/ 有 include security-headers.conf
    PASS [ESH-001] default.conf 的 location / 有 include security-headers.conf
    PASS [ESH-001/005] http://127.0.0.1:10442/（200）五個安全標頭各一次且值相符
    PASS [ESH-002/005] http://127.0.0.1:10442/assets/RedemptionScannerPage-DuDr3aDd.js（200）五個安全標頭各一次且值相符
    PASS [ESH-004] http://127.0.0.1:10442/assets/does-not-exist.js（404）五個安全標頭各一次且值相符
    PASS [ESH-003/005] http://127.0.0.1:10442/api/ok（200）五個安全標頭各一次且值相符
    PASS [ESH-003] http://127.0.0.1:10442/api/client-error（404）五個安全標頭各一次且值相符
    PASS [ESH-003] http://127.0.0.1:10442/api/server-error（500）五個安全標頭各一次且值相符
    PASS [ESH-002] 靜態資源同時保有長期快取的 Cache-Control
    PASS [ESH-007] /assets/RedemptionScannerPage-DuDr3aDd.js 帶 X-Forwarded-Proto: https 時 HSTS 為 max-age=15552000 且只出現一次
    PASS [ESH-007] /api/ok 帶 X-Forwarded-Proto: https 時 HSTS 為 max-age=15552000 且只出現一次
    PASS [ESH-008] /（X-Forwarded-Proto: http）沒有 HSTS
    PASS [ESH-008] /api/ok（X-Forwarded-Proto: http）沒有 HSTS
    PASS [ESH-008] /（無 X-Forwarded-Proto）沒有 HSTS
    PASS [ESH-008] /api/ok（無 X-Forwarded-Proto）沒有 HSTS
    PASS [ESH-010(js)] RedemptionScannerPage-DuDr3aDd.js 為 gzip、帶 Vary: Accept-Encoding，解壓後與原檔相同
    PASS [ESH-010(css)] index-B9ObGmIc.css 為 gzip、帶 Vary: Accept-Encoding，解壓後與原檔相同
    PASS [ESH-011] 帶 Content-Length 的小型回應未壓縮
    PASS [ESH-012] 未宣告 Accept-Encoding 時 RedemptionScannerPage-DuDr3aDd.js 不壓縮
    PASS [ESH-010] nginx -t 通過
    PASS [ESH-010] nginx -T 的 gzip_types 含 RedemptionScannerPage-DuDr3aDd.js 的 Content-Type（application/javascript）
    PASS [ESH-010] nginx -T 的 gzip_types 含 index-B9ObGmIc.css 的 Content-Type（text/css）
    
    結果：PASS 24、FAIL 0
    
    還原驗證（暫時移除 default.conf 的 location /assets/ 內 include 一行，退出碼 1；之後以備份還原，git diff --stat 仍為 17 insertions）：
    FAIL [ESH-001] default.conf 的 location /assets/ 沒有 include security-headers.conf
    FAIL [ESH-002/005] http://127.0.0.1:10467/assets/RedemptionScannerPage-DuDr3aDd.js 缺少 Content-Security-Policy
    FAIL [ESH-002/005] http://127.0.0.1:10467/assets/RedemptionScannerPage-DuDr3aDd.js 缺少 X-Content-Type-Options
    FAIL [ESH-002/005] http://127.0.0.1:10467/assets/RedemptionScannerPage-DuDr3aDd.js 缺少 X-Frame-Options
    FAIL [ESH-002/005] http://127.0.0.1:10467/assets/RedemptionScannerPage-DuDr3aDd.js 缺少 Referrer-Policy
    FAIL [ESH-002/005] http://127.0.0.1:10467/assets/RedemptionScannerPage-DuDr3aDd.js 缺少 Permissions-Policy
    FAIL [ESH-004] http://127.0.0.1:10467/assets/does-not-exist.js 缺少 Content-Security-Policy
    FAIL [ESH-004] http://127.0.0.1:10467/assets/does-not-exist.js 缺少 X-Content-Type-Options
    FAIL [ESH-004] http://127.0.0.1:10467/assets/does-not-exist.js 缺少 X-Frame-Options
    FAIL [ESH-004] http://127.0.0.1:10467/assets/does-not-exist.js 缺少 Referrer-Policy
    FAIL [ESH-004] http://127.0.0.1:10467/assets/does-not-exist.js 缺少 Permissions-Policy
    FAIL [ESH-007] /assets/RedemptionScannerPage-DuDr3aDd.js 的 HSTS 出現 0 次、值 []
    結果：PASS 20、FAIL 12
    
    備註：ESH-011 固定以 /api/ok（上游替身 return 200，帶 Content-Length）驗證，腳本另斷言該回應確有 Content-Length。JS 的實際 Content-Type 為 application/javascript，已在 gzip_types。ESH-006 見 6.1。
    ```

## 3. 同源／無 CORS／API 不送 HSTS

- [x] 3.1 API 整合測試固定「無 CORS、無 HSTS」（對應 ESH-009、ESH-013）
  - 測試先行（`tests/ProjectC.WebApi.Tests/Startup/EdgeBoundaryNoCorsNoHstsTests.cs`）：
    - `GetHealthLive_WhenCrossOriginRequest_HasNoAccessControlAllowHeaders`
    - `Options_WhenPreflightFromForeignOrigin_HasNoAccessControlAllowHeaders`
    - `GetApiEvents_WhenForwardedProtoHttpsFromTrustedProxy_HasNoStrictTransportSecurity`（須用非 loopback 的 Host，如 `app.example`，並讓 `X-Forwarded-Proto: https` 經 `UseForwardedHeaders` 還原為 https；`HstsMiddleware` 預設排除 loopback 且只在 `IsHttps` 時送標頭，否則 `UseHsts` mutate 後也不會失敗；TestServer 的 `RemoteIpAddress` 預設為 null，可能不在受信任網段而不還原 `X-Forwarded-Proto`，測試須明確設定來源位址或以 `ForwardedHeaders:TrustedNetworks` 涵蓋，並先確認未 mutate 時請求確實被還原為 https）
  - TDD：特性測試（程式碼不需變更，無法有 RED）；**還原驗證**：`mutate` 暫時加入 `AddCors`／`UseCors`（允許任意來源）與 `UseHsts`，確認對應測試失敗後還原，一次一個改動。
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~EdgeBoundaryNoCorsNoHstsTests"` 全數通過
  - 紀錄（原文）：
    ```text
    GREEN：
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~EdgeBoundaryNoCorsNoHstsTests"
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.WebApi.Tests -> /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    
    Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 637 ms - ProjectC.WebApi.Tests.dll (net10.0)
    
    還原驗證 1（UseHsts）：$ bash scripts/verify-test-evidence.sh mutate --file src/ProjectC.WebApi/Program.cs --from 'app.UseForwardedHeaders();' --to 'app.UseForwardedHeaders(); app.UseHsts();' -- docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~EdgeBoundaryNoCorsNoHstsTests"
    mutation: app.UseForwardedHeaders(); -> app.UseForwardedHeaders(); app.UseHsts();
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~EdgeBoundaryNoCorsNoHstsTests
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:03.67]     ProjectC.WebApi.Tests.Startup.EdgeBoundaryNoCorsNoHstsTests.GetApiEvents_WhenForwardedProtoHttpsFromTrustedProxy_HasNoStrictTransportSecurity [FAIL]
      Failed ProjectC.WebApi.Tests.Startup.EdgeBoundaryNoCorsNoHstsTests.GetApiEvents_WhenForwardedProtoHttpsFromTrustedProxy_HasNoStrictTransportSecurity [367 ms]
      Error Message:
       Expected response.Headers.Contains("Strict-Transport-Security") to be False, but found True.
      Stack Trace:
         at FluentAssertions.Primitives.BooleanAssertions`1.BeFalse(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Startup.EdgeBoundaryNoCorsNoHstsTests.GetApiEvents_WhenForwardedProtoHttpsFromTrustedProxy_HasNoStrictTransportSecurity() in /src/tests/ProjectC.WebApi.Tests/Startup/EdgeBoundaryNoCorsNoHstsTests.cs:line 72
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3, Duration: 708 ms - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    
    還原驗證 2（CORS；AddCors 與 UseCors 缺一不可（只加 UseCors 會因缺服務而丟例外、不是標頭差異），因此在同一個字串替換內視為單一改動）：$ bash scripts/verify-test-evidence.sh mutate --file src/ProjectC.WebApi/Program.cs --from 'var app = builder.Build();' --to 'builder.Services.AddCors(); var app = builder.Build(); app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());' -- （同上）
    mutation: var app = builder.Build(); -> builder.Services.AddCors(); var app = builder.Build(); app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
    $ docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter FullyQualifiedName~EdgeBoundaryNoCorsNoHstsTests
    Test run for /src/tests/ProjectC.WebApi.Tests/bin/Debug/net10.0/ProjectC.WebApi.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:03.16]     ProjectC.WebApi.Tests.Startup.EdgeBoundaryNoCorsNoHstsTests.Options_WhenPreflightFromForeignOrigin_HasNoAccessControlAllowHeaders [FAIL]
    [xUnit.net 00:00:03.19]     ProjectC.WebApi.Tests.Startup.EdgeBoundaryNoCorsNoHstsTests.GetHealthLive_WhenCrossOriginRequest_HasNoAccessControlAllowHeaders [FAIL]
      Failed ProjectC.WebApi.Tests.Startup.EdgeBoundaryNoCorsNoHstsTests.Options_WhenPreflightFromForeignOrigin_HasNoAccessControlAllowHeaders [317 ms]
      Error Message:
       Expected HasAccessControlAllowHeader(response) to be False, but found True.
      Stack Trace:
         at FluentAssertions.Primitives.BooleanAssertions`1.BeFalse(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Startup.EdgeBoundaryNoCorsNoHstsTests.Options_WhenPreflightFromForeignOrigin_HasNoAccessControlAllowHeaders() in /src/tests/ProjectC.WebApi.Tests/Startup/EdgeBoundaryNoCorsNoHstsTests.cs:line 52
    --- End of stack trace from previous location ---
      Failed ProjectC.WebApi.Tests.Startup.EdgeBoundaryNoCorsNoHstsTests.GetHealthLive_WhenCrossOriginRequest_HasNoAccessControlAllowHeaders [27 ms]
      Error Message:
       Expected HasAccessControlAllowHeader(response) to be False, but found True.
      Stack Trace:
         at FluentAssertions.Primitives.BooleanAssertions`1.BeFalse(String because, Object[] becauseArgs)
       at ProjectC.WebApi.Tests.Startup.EdgeBoundaryNoCorsNoHstsTests.GetHealthLive_WhenCrossOriginRequest_HasNoAccessControlAllowHeaders() in /src/tests/ProjectC.WebApi.Tests/Startup/EdgeBoundaryNoCorsNoHstsTests.cs:line 38
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3, Duration: 654 ms - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    
    兩次 mutate 後 git status 不含 src/ProjectC.WebApi/Program.cs（已還原）。HSTS 前提由測試內探針斷言 Scheme == https（GREEN 通過即證實被還原）。
    ```

## 4. 映像 digest 釘版

- [x] 4.1 `verify-image-pinning.sh` 靜態檢查（對應 CIP-001、003）
  - 測試先行：新增 `scripts/verify-image-pinning.sh`——檢查兩份 Dockerfile 的外部 `FROM`（排除 `FROM <階段名稱>`）與 `k8s/` 非 `projectc/` 映像皆符合 `@sha256:<64 hex>`；另斷言 `docs/2026-11-13-image-pinning.md` 存在且含 `imagetools inspect`、`@sha256`、`verify-image-pinning.sh` 三個字串（CIP-005）；任一不符列出檔案行號並非 0 結束。
  - 預期 RED：尚未釘版時，列出 `deploy/api.Dockerfile`、`deploy/web.Dockerfile` 與三個 k8s manifest 的違規行，以及文件不存在。
  - 最小實作見 4.2。
  - 紀錄（原文）：
    - RED（釘版前；命令 `bash scripts/verify-image-pinning.sh`）：
    - ```text
      FAIL deploy/api.Dockerfile:7 未釘 digest：mcr.microsoft.com/dotnet/sdk:10.0.403
      FAIL deploy/api.Dockerfile:33 未釘 digest：mcr.microsoft.com/dotnet/aspnet:10.0.14
      FAIL deploy/api.Dockerfile:45 未釘 digest：mcr.microsoft.com/dotnet/runtime-deps:10.0.14
      FAIL deploy/web.Dockerfile:5 未釘 digest：node:22.22-alpine
      FAIL deploy/web.Dockerfile:13 未釘 digest：nginxinc/nginx-unprivileged:1.30.6-alpine
      FAIL k8s/20-postgres.yaml:36 未釘 digest：postgres:16-alpine
      FAIL k8s/21-redis.yaml:21 未釘 digest：redis:7-alpine
      FAIL k8s/22-seq.yaml:22 未釘 digest：datalust/seq:2026.1.17114
      FAIL docs/2026-11-13-image-pinning.md:0 釘版文件不存在
      FAIL 共 9 項不符（已檢查 8 個映像參照）
      ```
    - GREEN（釘版與文件完成後）：
    - ```text
      PASS 檢查 8 個外部映像參照皆已釘 digest，釘版文件關鍵字齊全
      ```
    - 

- [ ] 4.2 取得 digest 並釘版（對應 CIP-001～004）
  - 以 `docker buildx imagetools inspect <name:tag>` 取多架構索引 digest，寫成 `name:tag@sha256:...`：`deploy/api.Dockerfile`（sdk、aspnet、runtime-deps）、`deploy/web.Dockerfile`（node、nginx-unprivileged）、`k8s/20-postgres.yaml`、`21-redis.yaml`、`22-seq.yaml`；更新兩份 Dockerfile 開頭註解中「未釘 digest」的敘述。
  - GREEN：`bash scripts/verify-image-pinning.sh` PASS；依 Dockerfile 開頭指令建置 `projectc/api`、`projectc/api-migrator`、`projectc/web` 皆成功（各建置命令與輸出摘要原文記入紀錄）；`bash scripts/verify-k8s-manifests.sh` 全 PASS（同 1.2 的環境條件）。
  - 紀錄（原文）：
    - imagetools inspect（取多架構索引 digest）：
    - ```text
      $ docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0.403
      Digest:    sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e
      $ docker buildx imagetools inspect mcr.microsoft.com/dotnet/aspnet:10.0.14
      Digest:    sha256:1a48394b6f29d3a32c79d9edecd4abda0ecbe8776d567dbfc4590ed6979c740c
      $ docker buildx imagetools inspect mcr.microsoft.com/dotnet/runtime-deps:10.0.14
      Digest:    sha256:f87d6824cd56fa4ad117d06461e4eb0058f46a159872dbcc4a836a72efdd1bec
      $ docker buildx imagetools inspect node:22.22-alpine
      Digest:    sha256:e58326d0d441090181ac150dc2078d3e2cf6a0d42e809aebba3ef5880935ffdd
      $ docker buildx imagetools inspect nginxinc/nginx-unprivileged:1.30.6-alpine
      Digest:    sha256:f5116ed4606f3eb015dd4a8e28cb9b351476dc0ad4bc6e9f93b19cec130cdf03
      $ docker buildx imagetools inspect postgres:16-alpine
      Digest:    sha256:f377d590e5474681d4d7838635031a2b50647923b7abfa803ce9b5ff35d5c663
      $ docker buildx imagetools inspect redis:7-alpine
      Digest:    sha256:7f7d30b7fdd09467bac58198a1f166dd69789d348d4fe859cccb20e0cf287fbe
      $ docker buildx imagetools inspect datalust/seq:2026.1.17114
      Digest:    sha256:fa405502a3c57884c88477b78b55ca76c5a3fb1a4d20c6a185c24e750db34056
      ```
    - 建置（釘版後；各命令輸出末 4 行）：
    - ```text
      $ docker build -f deploy/api.Dockerfile --target runtime -t projectc/api:local .
      #19 naming to docker.io/projectc/api:local done
      #19 DONE 0.0s
      
      View build details: docker-desktop://dashboard/build/desktop-linux/desktop-linux/7gf9xpejcz3s04q9mnkz7veen
      $ docker build -f deploy/api.Dockerfile --target migrator -t projectc/api-migrator:local .
      #21 naming to docker.io/projectc/api-migrator:local done
      #21 DONE 0.0s
      
      View build details: docker-desktop://dashboard/build/desktop-linux/desktop-linux/thu15lfneaugu66zimdn8ldbs
      $ docker build -f deploy/web.Dockerfile --build-context deploy=deploy -t projectc/web:local web
      #19 naming to docker.io/projectc/web:local done
      #19 DONE 0.0s
      
      View build details: docker-desktop://dashboard/build/desktop-linux/desktop-linux/4000eu8opf4nxb594y2s94mon
      ```
    - `bash scripts/verify-image-pinning.sh`：PASS 檢查 8 個外部映像參照皆已釘 digest，釘版文件關鍵字齊全
    - `verify-k8s-manifests.sh` 全 PASS 條件**未達成**（同 1.2 的 NodePort 環境限制，見 1.2 紀錄）；映像釘版後三個 :local 映像可建置、叢集內 db／redis／seq 以釘版 digest 映像啟動並就緒（斷言 1 PASS）。**UNVERIFIED 項目與原因（使用者 2026-11-14 決定接受、之後補驗）**：(1) 釘版後 `verify-k8s-manifests.sh` 叢集全 PASS（CIP-004）——因 kind 模式 NodePort 30081 主機連不到，3a／3b／4／5／8a／10d／11d 失敗；(2) 釘版後 web 映像（nginx 設定與釘版 base）經 NodePort 的行為。本 task 維持未勾選，見 `docs/todo/2026-11-14-edge-security-headers-k8s-verification-todo.md`。
    - 

## 5. 文件

- [x] 5.1 文件同步（TDD N/A：純文件，由審查核對；`bash scripts/check-doc-paths.sh` 須通過）
  - `docs/todo/2026-11-02-deployment-prerequisites.md`（部署前提現行位置）：nginx 前方須有 TLS 代理並送 `X-Forwarded-Proto: https`（須覆寫為單一值 `https`，若附加成 `https, http` 則 HSTS 不送，屬 fail-safe）；`AllowedHosts` 上線前必改為實際網域；與現有「80／443 由既有反向代理佔用」一節對照；本機或維運以 port-forward／NodePort 存取須用清單內主機名稱（如 `localhost`），以 `127.0.0.1` 或節點 IP 經 nginx 呼叫 `/api/` 會得 400。
  - `docs/api-conventions.md`：「框架層錯誤」補 HostFiltering 400（無 `ProblemDetails` 本文），並修正第 23 行「框架層錯誤 Content-Type 一律為 problem+json」使 HostFiltering 400 明列為例外；新增同源／無 CORS 決策一行。
  - 新增 `docs/2026-11-13-image-pinning.md`：取得 digest 指令、tag 與 digest 同步更新、更新後驗證指令（CIP-005）。
  - `docs/review/TODO.md`：H-3、L-5、L-8 與 ② 狀態更新；順手修正 ⑧ `db-timeout-pool-tuning` 等已歸檔項目的過期狀態。
  - 紀錄：
    - 已改：`docs/todo/2026-11-02-deployment-prerequisites.md` 第 6 項（X-Forwarded-Proto 單一值 https、AllowedHosts、port-forward 主機名稱）；`docs/api-conventions.md`（HostFiltering 400 例外、同源／無 CORS）；新增 `docs/2026-11-13-image-pinning.md`；`docs/review/TODO.md`（H-3、L-5、L-8、②，並修正 ④⑦⑧ 為已歸檔）。
    - `bash scripts/check-doc-paths.sh` → `OK: 所有 docs 路徑引用都存在`

## 6. 真實瀏覽器驗證

- [ ] 6.1 CSP 與頁面功能（對應 ESH-006；替代驗證，例外已核准）
  - 有效觀測要求見 design.md「核准例外」(a)–(d)；以 claude-in-chrome 經邊界 nginx（`KEEP_UP=1 bash scripts/verify-k8s-manifests.sh` 後的 `http://localhost:30081`，必須用 `localhost`，不可用 127.0.0.1，否則 `/api/` 得 400；或等效部署）載入買家端、後台、核銷頁，完成登入與各頁主要操作；以 console 訊息核對沒有 CSP 違規，核銷頁相機可啟用（相機以 stub 或實機）。發現違規時依 design.md 決策 2 調整 CSP 並同步 ESH-005、2.1 腳本。
  - 紀錄（頁面文字／console 原文，不只依截圖）：
    - **UNVERIFIED**：docker-desktop 為 kind 模式節點，主機連不到 `localhost:30081`（NodePort 未對主機開放；以 socat 容器轉發的嘗試被 auto mode 分類器拒絕，未繞過），無法經邊界 nginx 於真實瀏覽器驗證 CSP 與頁面功能。原因：kind 模式 NodePort 主機連不到。使用者 2026-11-14 決定標 UNVERIFIED、之後補驗、不轉發 30081；有效觀測要求 (a)–(d) 全部未做。本 task 維持未勾選，補驗見 `docs/todo/2026-11-14-edge-security-headers-k8s-verification-todo.md`。

## 7. hardener 與審查

- [x] 7.1 hardener 最終檢查：N/A——本 change 沒有 Handler／Repository／I/O 程式碼變更（只有 nginx、K8s、Dockerfile、文件、腳本與特性測試），不在 hardener 適用範圍；於此記錄理由。
  - 紀錄：N/A（理由同上；本 change 無 Handler／Repository／I/O 程式碼變更，hardener 清單不適用）。
- [ ] 7.2 審查迴圈紀錄（`strict-reviewer`、對抗式 subagent、`cross-review` 每輪工具／模型與 commit，以及 blocking 處理結果）
  - 紀錄：
    - 實作階段 strict-reviewer 第 1 輪（subagent，未 commit 的工作樹）：PASS，無 blocking。測試：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~HostFilteringTests|FullyQualifiedName~EdgeBoundaryNoCorsNoHstsTests"` 第一次 `Failed: 2, Passed: 7, Total: 9`（`GetHealthLive_WhenCrossOriginRequest_HasNoAccessControlAllowHeaders`、`Options_WhenPreflightFromForeignOrigin_HasNoAccessControlAllowHeaders`：`Expected HasAccessControlAllowHeader(response) to be False, but found True.`），原因為容器 bin/obj 殘留 mutate 的增量建置產物（src 無 AddCors／UseCors，git status src 乾淨）；`dotnet build src/ProjectC.WebApi --no-incremental` 後重跑 `Passed: 9, Failed: 0, Total: 9`。`verify-image-pinning.sh` → `PASS 檢查 8 個外部映像參照皆已釘 digest，釘版文件關鍵字齊全`。建議事項：BREACH（gzip JSON）風險評估、CSP 相容性（6.1 補驗）、最終全套測試前須 `--no-incremental` 重建。
    - commit 795e249。外部審查第 1 輪（Claude Code Opus，審查對象 strict-reviewer 實作階段）：PASS，無 blocking；測試 `Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 695 ms - ProjectC.WebApi.Tests.dll (net10.0)`（`--no-incremental` 建置後）。建議：CSP 相容性（6.1 補驗）、gzip JSON BREACH 評估、7.2 補記、最終測試前 `--no-incremental`、image-pinning 文件日期前綴為 spec 預先指定。
    - 外部 PASS 後最後一次 strict-reviewer：PASS，無 blocking；`Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 743 ms - ProjectC.WebApi.Tests.dll (net10.0)`。
    - 對抗式 general-purpose subagent（全新）：1 項 blocking——`docs/2026-11-12-k8s-guide.md:91` 的維運指令 `curl http://127.0.0.1:18080/health/live` 在 `AllowedHosts=localhost;api` 下會得 400。自行驗證成立（design 的 §2 與 11c：非清單 Host → 400）；修正為 `http://localhost:18080/health/live` 並註明須用 localhost。附帶修正 `scripts/verify-edge-headers.sh` 第 145–146 行的字面 CR 與引號內換行（改為 `tr -d ''`、`printf '%s
'`），重跑 `bash scripts/verify-edge-headers.sh` → `結果：PASS 24、FAIL 0`；`check-doc-paths.sh` → `OK: 所有 docs 路徑引用都存在`。未採納（建議）：gzip_types 加 application/problem+json、.gitattributes 補 `*.conf text eol=lf`（default.conf 為 CRLF、snippet 為 LF，不影響功能）。
    - 上述修正已於 486b2a8 commit。外部審查第 2 輪（Claude Code Opus，審查 486b2a8 完整 diff）：PASS，無 blocking，回歸核對兩處修正確認；`Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 769 ms - ProjectC.WebApi.Tests.dll (net10.0)`；`verify-edge-headers.sh` → `結果：PASS 24、FAIL 0`。建議：BREACH 與 CSP 相容性續於補驗 todo 追蹤、11d 補驗前維持 UNVERIFIED。
    - 第 2 輪外部 PASS 後最後一次 strict-reviewer（HEAD 486b2a8）：PASS，無 blocking；`Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 905 ms - ProjectC.WebApi.Tests.dll (net10.0)`。
    - 第 2 次全新對抗式 general-purpose subagent：無 blocking；`Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 783 ms - ProjectC.WebApi.Tests.dll (net10.0)`、`verify-image-pinning.sh` → `PASS 檢查 8 個外部映像參照皆已釘 digest，釘版文件關鍵字齊全`、`verify-edge-headers.sh` → `結果：PASS 24、FAIL 0`。建議（未採納／延後）：`docs/review/TODO.md` 狀態註記補驗 todo 連結、ESH-010 `content_type` 空字串守衛、gzip_types 加 problem+json、`.gitattributes` 補 `*.conf`。
    - 採納外部審查建議（未 commit）：(1) ESH-010 `content_type` 為空時直接 fail；(2) `.gitattributes` 補 `*.conf text eol=lf`，`deploy/nginx/default.conf` 工作樹原為 CRLF（`tr -cd '\r' | wc -c` = 46；`git ls-files --eol`：`i/lf w/crlf`）已轉 LF（轉後 CR=0、`i/lf w/lf`），`security-headers.conf` 本來即 LF（CR=0）。
      - 修正前（暫時在迴圈內加 `content_type=""`，副本 `scripts/.mut-before.sh`，已刪除）`bash scripts/.mut-before.sh` exit=0，假通過：
        ```
        PASS [ESH-010] nginx -T 的 gzip_types 含 RedemptionScannerPage-DuDr3aDd.js 的 Content-Type（application/javascript）
        PASS [ESH-010] nginx -T 的 gzip_types 含 index-B9ObGmIc.css 的 Content-Type（text/css）
        結果：PASS 24、FAIL 0
        ```
      - 修正後（同樣暫時強制 `content_type=""`，副本 `scripts/.mut-after.sh`，已刪除）exit=1，正確 FAIL：
        ```
        FAIL [ESH-010] 抓不到 RedemptionScannerPage-DuDr3aDd.js 的 Content-Type（回應沒有 Content-Type 標頭），無法比對 gzip_types
        FAIL [ESH-010] 抓不到 index-B9ObGmIc.css 的 Content-Type（回應沒有 Content-Type 標頭），無法比對 gzip_types
        結果：PASS 22、FAIL 2
        ```
        （verify-test-evidence.sh 僅以 dotnet test 的 `Failed!` 判定，不適用 bash 腳本，故改用暫時注入並還原。）
      - 還原後（正式腳本）`bash scripts/verify-edge-headers.sh` exit=0：`PASS [ESH-010] nginx -T 的 gzip_types 含 RedemptionScannerPage-DuDr3aDd.js 的 Content-Type（application/javascript）`、`PASS [ESH-010] nginx -T 的 gzip_types 含 index-B9ObGmIc.css 的 Content-Type（text/css）`、`結果：PASS 24、FAIL 0`；`bash scripts/check-doc-paths.sh` → `OK: 所有 docs 路徑引用都存在`。腳本 CR 數 0、`bash -n` 通過。
    - commit c5c1308（採納 ESH-010 守衛與 `*.conf` eol=lf）。外部審查第 3 輪（Claude Code Opus，沿用 reviewer pane，審查 c5c1308 相對 486b2a8 的 diff）：PASS，無 blocking，無新增建議；本輪未跑容器測試（diff 無 .NET／web 變更）。
    - 第 3 輪外部 PASS 後最後一次 strict-reviewer（HEAD c5c1308）：PASS，無 blocking；`Build succeeded. 0 Warning(s) 0 Error(s)`（`--no-incremental`）、`Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 27 s - ProjectC.WebApi.Tests.dll (net10.0)`。
    - 第 3 次全新對抗式 general-purpose subagent（靜態審查，未跑容器）：無 blocking；建議（皆不處理）：gzip_types 不含 application/problem+json（已列延後）、AllowedHosts 不含 127.0.0.1（文件已警告）、直連 nginx 者可自帶 X-Forwarded-Proto 觸發 HSTS（在 TLS 代理前提下可接受）。
