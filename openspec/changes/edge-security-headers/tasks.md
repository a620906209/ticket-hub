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

- [ ] 1.1 API 整合測試固定 Host 驗證行為（對應 HHV-001、002、003）
  - 測試先行（`tests/ProjectC.WebApi.Tests/Startup/HostFilteringTests.cs`，`WithWebHostBuilder` 覆寫 `AllowedHosts`）：
    - `GetHealthLive_WhenHostNotAllowed_Returns400`（Host `evil.example`）
    - `GetApiPath_WhenHostNotAllowed_Returns400`（Host `evil.example`，任一 `/api/...` 路徑）
    - `GetHealthLive_WhenHostAllowed_Returns200`（含帶連接埠）
    - `GetHealthLive_WhenAllowedHostsIsDefault_Returns200ForAnyHost`
  - TDD：框架既有行為的特性測試，程式碼不需變更，因此沒有「因行為不符而失敗」的 RED；以**還原驗證**取代：`bash scripts/verify-test-evidence.sh mutate` 暫時把工廠覆寫的 `AllowedHosts` 改成 `*`，確認前兩個斷言失敗，再還原。
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~HostFilteringTests"` 全數通過
  - 紀錄（原文）：
    - 

- [ ] 1.2 K8s：ConfigMap 加 `AllowedHosts`、三個探針加 `Host` 標頭（對應 HHV-004～008）
  - 測試先行：先在 `scripts/verify-k8s-manifests.sh` 新增斷言——ConfigMap 有 `AllowedHosts` 且不含 `*` 且含 `api`；`Host: evil` 請求 `/health/live` 回 400；`Host: localhost` 經 web 請求 `/api/...` 不為 400；`Host: evil` 經 web 請求 `/api/...` 回 400；API Pod Ready。既有的直連 API 斷言（8b，`http://127.0.0.1:<port>/health/live`）須改帶 `Host: localhost`，否則會被新設定擋成 400；同時確認 `deploy/docker-compose.verify.yml` 與 k6 腳本直連 API 的呼叫端（走預設 `*` 者不受影響）。維運以 port-forward 存取 API 也須用清單內主機名稱，寫入 5.1 文件。
  - 預期 RED：尚未改 manifest 時，ConfigMap 斷言 FAIL 且 `Host: evil` 得 200（`Expected 400, got 200` 一類原文）。
  - 最小實作：`k8s/10-configmap.yaml` 加 `AllowedHosts: "localhost;api"`（註解說明上線前改實際網域）；`k8s/40-api.yaml` 三個探針加 `httpHeaders: [{name: Host, value: api}]`。
  - GREEN：`bash scripts/verify-k8s-manifests.sh`（本機 docker-desktop 叢集）全 PASS；需使用者環境可用 kubectl，不可用時於回報註明 UNVERIFIED。
  - 紀錄（原文）：
    - 

## 2. nginx 安全標頭、HSTS、gzip

- [ ] 2.1 `verify-edge-headers.sh` 與 nginx 設定（對應 ESH-001～005、007、008、010～012）
  - 測試先行：新增 `scripts/verify-edge-headers.sh`（建暫時 network，以 `projectc/web:local` 加簡易設定當 `api` 上游替身；對 `/`、`/assets/<檔>`、`/assets/不存在`、`/api/...`（上游 200／4xx／5xx）逐路徑斷言五個安全標頭存在且各只出現一次；CSP 值逐字比對；`X-Forwarded-Proto: https` 才有 `Strict-Transport-Security: max-age=15552000`、`http`／缺省沒有；gzip（JS 與 CSS 各驗一個、小型、無 Accept-Encoding）；`nginx -t` 與 `nginx -T` 核對 JS 的 Content-Type 在 `gzip_types` 內）。腳本結束時清除暫時容器與 network，任一斷言失敗即非 0 結束。
  - 預期 RED：以尚未修改的 `default.conf` 建置映像執行，標頭斷言以 `FAIL: / 缺少 Content-Security-Policy` 之類訊息失敗、gzip 斷言失敗（非腳本或環境錯誤）。
  - 最小實作：新增 `deploy/nginx/security-headers.conf`（五個標頭＋HSTS map 用法），`default.conf` 加 `map`、gzip 設定，並在 `/api/`、`/assets/`、`/` 三個 location 各 `include`；`deploy/web.Dockerfile` COPY snippet。
  - GREEN：`bash scripts/verify-edge-headers.sh` 全 PASS。
  - 紀錄（原文）：
    - 

## 3. 同源／無 CORS／API 不送 HSTS

- [ ] 3.1 API 整合測試固定「無 CORS、無 HSTS」（對應 ESH-009、ESH-013）
  - 測試先行（`tests/ProjectC.WebApi.Tests/Startup/EdgeBoundaryNoCorsNoHstsTests.cs`）：
    - `GetHealthLive_WhenCrossOriginRequest_HasNoAccessControlAllowHeaders`
    - `Options_WhenPreflightFromForeignOrigin_HasNoAccessControlAllowHeaders`
    - `GetApiEvents_WhenForwardedProtoHttpsFromTrustedProxy_HasNoStrictTransportSecurity`（須用非 loopback 的 Host，如 `app.example`，並讓 `X-Forwarded-Proto: https` 經 `UseForwardedHeaders` 還原為 https；`HstsMiddleware` 預設排除 loopback 且只在 `IsHttps` 時送標頭，否則 `UseHsts` mutate 後也不會失敗；TestServer 的 `RemoteIpAddress` 預設為 null，可能不在受信任網段而不還原 `X-Forwarded-Proto`，測試須明確設定來源位址或以 `ForwardedHeaders:TrustedNetworks` 涵蓋，並先確認未 mutate 時請求確實被還原為 https）
  - TDD：特性測試（程式碼不需變更，無法有 RED）；**還原驗證**：`mutate` 暫時加入 `AddCors`／`UseCors`（允許任意來源）與 `UseHsts`，確認對應測試失敗後還原，一次一個改動。
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~EdgeBoundaryNoCorsNoHstsTests"` 全數通過
  - 紀錄（原文）：
    - 

## 4. 映像 digest 釘版

- [ ] 4.1 `verify-image-pinning.sh` 靜態檢查（對應 CIP-001、003）
  - 測試先行：新增 `scripts/verify-image-pinning.sh`——檢查兩份 Dockerfile 的外部 `FROM`（排除 `FROM <階段名稱>`）與 `k8s/` 非 `projectc/` 映像皆符合 `@sha256:<64 hex>`；另斷言 `docs/2026-11-13-image-pinning.md` 存在且含 `imagetools inspect`、`@sha256`、`verify-image-pinning.sh` 三個字串（CIP-005）；任一不符列出檔案行號並非 0 結束。
  - 預期 RED：尚未釘版時，列出 `deploy/api.Dockerfile`、`deploy/web.Dockerfile` 與三個 k8s manifest 的違規行，以及文件不存在。
  - 最小實作見 4.2。
  - 紀錄（原文）：
    - 

- [ ] 4.2 取得 digest 並釘版（對應 CIP-001～004）
  - 以 `docker buildx imagetools inspect <name:tag>` 取多架構索引 digest，寫成 `name:tag@sha256:...`：`deploy/api.Dockerfile`（sdk、aspnet、runtime-deps）、`deploy/web.Dockerfile`（node、nginx-unprivileged）、`k8s/20-postgres.yaml`、`21-redis.yaml`、`22-seq.yaml`；更新兩份 Dockerfile 開頭註解中「未釘 digest」的敘述。
  - GREEN：`bash scripts/verify-image-pinning.sh` PASS；依 Dockerfile 開頭指令建置 `projectc/api`、`projectc/api-migrator`、`projectc/web` 皆成功（各建置命令與輸出摘要原文記入紀錄）；`bash scripts/verify-k8s-manifests.sh` 全 PASS（同 1.2 的環境條件）。
  - 紀錄（原文）：
    - 

## 5. 文件

- [ ] 5.1 文件同步（TDD N/A：純文件，由審查核對；`bash scripts/check-doc-paths.sh` 須通過）
  - `docs/todo/2026-11-02-deployment-prerequisites.md`（部署前提現行位置）：nginx 前方須有 TLS 代理並送 `X-Forwarded-Proto: https`（須覆寫為單一值 `https`，若附加成 `https, http` 則 HSTS 不送，屬 fail-safe）；`AllowedHosts` 上線前必改為實際網域；與現有「80／443 由既有反向代理佔用」一節對照；本機或維運以 port-forward／NodePort 存取須用清單內主機名稱（如 `localhost`），以 `127.0.0.1` 或節點 IP 經 nginx 呼叫 `/api/` 會得 400。
  - `docs/api-conventions.md`：「框架層錯誤」補 HostFiltering 400（無 `ProblemDetails` 本文），並修正第 23 行「框架層錯誤 Content-Type 一律為 problem+json」使 HostFiltering 400 明列為例外；新增同源／無 CORS 決策一行。
  - 新增 `docs/2026-11-13-image-pinning.md`：取得 digest 指令、tag 與 digest 同步更新、更新後驗證指令（CIP-005）。
  - `docs/review/TODO.md`：H-3、L-5、L-8 與 ② 狀態更新；順手修正 ⑧ `db-timeout-pool-tuning` 等已歸檔項目的過期狀態。
  - 紀錄：

## 6. 真實瀏覽器驗證

- [ ] 6.1 CSP 與頁面功能（對應 ESH-006；替代驗證，例外已核准）
  - 有效觀測要求見 design.md「核准例外」(a)–(d)；以 claude-in-chrome 經邊界 nginx（`KEEP_UP=1 bash scripts/verify-k8s-manifests.sh` 後的 `http://localhost:30081`，必須用 `localhost`，不可用 127.0.0.1，否則 `/api/` 得 400；或等效部署）載入買家端、後台、核銷頁，完成登入與各頁主要操作；以 console 訊息核對沒有 CSP 違規，核銷頁相機可啟用（相機以 stub 或實機）。發現違規時依 design.md 決策 2 調整 CSP 並同步 ESH-005、2.1 腳本。
  - 紀錄（頁面文字／console 原文，不只依截圖）：
    - 

## 7. hardener 與審查

- [ ] 7.1 hardener 最終檢查：N/A——本 change 沒有 Handler／Repository／I/O 程式碼變更（只有 nginx、K8s、Dockerfile、文件、腳本與特性測試），不在 hardener 適用範圍；於此記錄理由。
- [ ] 7.2 審查迴圈紀錄（`strict-reviewer`、對抗式 subagent、`cross-review` 每輪工具／模型與 commit，以及 blocking 處理結果）
  - 紀錄：
