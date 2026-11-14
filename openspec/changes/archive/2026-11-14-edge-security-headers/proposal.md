# Proposal

## Why

生產就緒審查（`docs/review/TODO.md` H-3、L-5、L-8）指出邊界層缺少基本防護：nginx 沒有任何安全標頭與壓縮，API 的 `AllowedHosts` 為 `*`（任意 Host 標頭皆放行），同源／CORS 立場沒有記錄，容器映像只用 tag 而未釘 digest，同一 tag 被覆寫即可靜默換掉建置與執行環境。前置的 `reverse-proxy-forwarded-headers` 已完成，現在可以在同一個邊界上補齊。

## What Changes

- nginx 對所有回應（靜態資源、`index.html`、`/api/` 代理回應、錯誤頁）加上 CSP、`X-Content-Type-Options: nosniff`、`X-Frame-Options: DENY`、`Referrer-Policy`、`Permissions-Policy`（核銷頁需相機，僅 `camera=(self)`）；直接 enforce。
- nginx 僅在請求帶 `X-Forwarded-Proto: https`（TLS 由前方代理終結）時送 `Strict-Transport-Security`；API 不加 `UseHsts`。
- nginx 對文字類回應（js／css／svg／json）啟用 gzip。
- API `AllowedHosts` 在開發預設維持 `*`；K8s ConfigMap 改注入明確主機清單，K8s 探針補 `Host` 標頭，使不合法 Host 回 400 而探針不受影響。
- 記錄「前端與 API 同源（皆經 nginx），不加 CORS middleware」的決策。
- `deploy/api.Dockerfile`、`deploy/web.Dockerfile` 所有 `FROM` 與 `k8s/` 第三方映像改為 `tag@sha256:digest`，文件補更新步驟。
- `docs/todo/2026-11-02-deployment-prerequisites.md` 補前提：nginx 前方須有 TLS 代理並送 `X-Forwarded-Proto: https`。TLS 終結本身不在本 change。
- 新增 `scripts/verify-edge-headers.sh` 與 API 整合測試作為驗證。

## Capabilities

### New Capabilities
- `edge-security-headers`: nginx 邊界對回應加安全標頭、條件式 HSTS、gzip，並記錄同源／無 CORS 的決策。
- `host-header-validation`: API 依設定的 `AllowedHosts` 拒絕不在清單內的 Host，以及探針與代理轉發下維持可用的要求。
- `container-image-pinning`: 建置與執行用映像以 digest 釘版，並有可驗證的更新步驟。

### Modified Capabilities
<!-- 無。既有 client-address-resolution、api-health-checks 的 Requirement 不變。 -->

## Impact

- 程式：`deploy/nginx/default.conf`（新增標頭／gzip，含 location 層 `add_header` 繼承處理）、`deploy/api.Dockerfile`、`deploy/web.Dockerfile`、`k8s/10-configmap.yaml`、`k8s/40-api.yaml`（探針 Host）、`k8s/20-postgres.yaml`／`21-redis.yaml`／`22-seq.yaml`（digest）、`scripts/verify-k8s-manifests.sh`（直連 API 的斷言須帶清單內 Host）。API 程式碼不需修改（`AllowedHosts` 為框架既有設定）。
- 文件：`docs/todo/2026-11-02-deployment-prerequisites.md`、`docs/api-conventions.md`（同源／無 CORS）、`docs/review/TODO.md` 狀態。
- 新增：`scripts/verify-edge-headers.sh`、`tests/ProjectC.WebApi.Tests/` 下 Host 驗證整合測試。
- 對外行為：不合法 Host 直連 API 回 400；瀏覽器收到新標頭，CSP 違規會被擋（需真實瀏覽器驗證）。
- 排除：Ingress／cert-manager、WAF、限流調整、dev compose 映像釘版。
- Change 顆粒度：3 個新 capability、tasks 約 10 項，未超過 CLAUDE.md 拆分門檻。
