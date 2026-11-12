# Proposal

## Why

部署在 nginx／K8s 之後時，API 看到的 `Connection.RemoteIpAddress` 永遠是 web Pod 的位址：nginx 沒轉送 `X-Forwarded-For`、API 也沒呼叫 `UseForwardedHeaders`。登入與驗證碼的 IP 分區限流因此全站共用同一個計數器（`login` 預設 5 次／60 秒）——任一人輸錯 5 次即鎖住所有人登入，攻擊者也能藉此對全站發動鎖定攻擊。公開的 `GET /api/events*` 另外完全沒有限流。來源：`docs/review/TODO.md` C-2、M-3（2026-11-12 Production Readiness Review）。這是任何對外曝露前必須完成的缺口。

## What Changes

- nginx 的 `/api/` 轉送 `X-Forwarded-For`（以 `$remote_addr` 覆蓋，不串接用戶端自帶值）與 `X-Forwarded-Proto`。
- API 啟用 `UseForwardedHeaders`：只信任設定檔 `ForwardedHeaders:TrustedNetworks`（CIDR 陣列，預設為 RFC1918 私有網段與 loopback）送來的標頭，`ForwardLimit = 1`，只還原 `X-Forwarded-For` 與 `X-Forwarded-Proto`；非信任來源的標頭一律忽略。
- 還原後的 `RemoteIpAddress` 自動套用到 `login`、`captcha` 兩個既有 IP 分區 policy（共用 `LoginRateLimiterPartitioning.GetPartitionKey`，不需改分區邏輯）。
- 新增 `public-read` policy（IP 分區、Fixed Window），套用到 `EventsController` 的所有匿名 GET；新增獨立的 `PublicReadRateLimitingOptions`（預設 120 次／60 秒）。
- `k8s/50-web.yaml` 的 `web` Service 設 `externalTrafficPolicy: Local`，避免 NodePort SNAT 讓 nginx 看到節點 IP。
- `scripts/verify-k8s-manifests.sh` 補斷言；`api-rate-limiting` 登入需求的「部署前提」改寫為現況。

不做（另案）：TLS／HSTS／安全標頭（TODO 階段 1 ②）、login 改「IP＋帳號」複合分區、Ingress 的來源 IP 保留。

## Capabilities

### New Capabilities
- `client-address-resolution`: API 在反向代理之後還原真實用戶端位址的規則——哪些來源可被信任、如何還原、預設與設定驗證、不被信任時的行為。

### Modified Capabilities
- `api-rate-limiting`: 登入端點需求的「部署前提」改為由 `client-address-resolution` 保證；新增公開讀取端點（`GET /api/events*`）的 IP 分區限流與其設定值需求。

## Impact

- 程式：`Program.cs`（middleware 順序）、`RateLimitingServiceCollectionExtensions.cs`、`EventsController.cs`、`EagerOptionsValidationExtensions.cs`、新增 `PublicReadRateLimitingOptions`、新增轉送標頭的 DI 擴充方法、`appsettings.json`。
- 部署：`deploy/nginx/default.conf`、`k8s/50-web.yaml`、`scripts/verify-k8s-manifests.sh`。
- 文件：`docs/api-conventions.md`（限流 policy 與設定）、`docs/review/TODO.md`。
- 行為變化：K8s／nginx 部署下，限流由「全站共用一個計數器」變為「每個用戶端 IP 各自計數」；`GET /api/events*` 超過 120 次／60 秒的 IP 會收到 429（正常瀏覽遠低於此值；k6 壓測腳本若從單一 IP 打公開端點，須確認額度——見 design.md 風險）。
- 無 DB migration、無前端變更。
