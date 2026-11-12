# Design

## Context

見 proposal.md「Why」。現況（已查證）：
- 拓樸：瀏覽器 → NodePort 30080 → web（nginx）→ `api:8080`；`api-ingress` NetworkPolicy 只允許 web Pod 連 API。
- `deploy/nginx/default.conf` 的 `/api/` 只設 `Host`；`Program.cs` 無 `UseForwardedHeaders`。
- `LoginRateLimiterPartitioning.GetPartitionKey` 讀 `Connection.RemoteIpAddress`，`login` 與 `captcha` 兩個 policy 共用。
- `EventsController` 有四個匿名 GET（列表、詳情、`/seats`、`/ticket-types`），無限流。
- 開發環境（Vite proxy、直連）不送 `X-Forwarded-For`。
- WebApplicationFactory／TestServer 對所有請求回報固定的 `RemoteIpAddress`（見 `LoginRateLimiterPartitioning` 註解）。

## Goals / Non-Goals

**Goals**：K8s／nginx 部署下，限流依真實用戶端 IP 分區；不受信任來源無法偽造；公開讀取端點有限流；開發環境行為不變。

**Non-Goals**：TLS／HSTS（change ②）；login「IP＋帳號」複合分區；Ingress／LB 的來源 IP 保留；rate limit 改用 Redis 共享計數（目前 in-memory、每副本獨立，沿用既有設計）。

## Decisions

### 決策 1：信任網段以設定檔 CIDR 陣列提供，預設私有網段＋loopback

`ForwardedHeaders:TrustedNetworks`，預設 RFC1918＋`127.0.0.0/8`＋`::1/128`，`ForwardLimit = 1`，只啟用 `XForwardedFor | XForwardedProto`。
- 為何不用單一 `KnownProxies`：Pod IP 會變，無法列舉。為何不全信任：任何能直連 API 的來源可偽造 IP 繞過限流。NetworkPolicy 已限制只有 web 能連 API，信任私有網段在目前部署可接受；網段過寬的風險見下方。
- 預設值（而非必填）：沿用既有 options「有安全預設值」的模式，compose／K8s 不必新增設定即可運作。
- 設定錯誤（非法 CIDR、空陣列）→ 啟動失敗（fail-fast），不略過該項。實作為獨立 options 類別＋啟動時解析 CIDR。
- **實作時須對照已安裝套件驗證**：.NET 10 的 `ForwardedHeadersOptions` 以 `KnownIPNetworks` 取代過時的 `KnownNetworks`；先清空預設 `KnownProxies`（預設含 loopback）與網路清單，再加入自訂項，避免預設值與設定檔混合。以實際 build 與測試確認，不憑文件。

### 決策 2：middleware 位置——`UseExceptionHandler` 之後、`UseWhen(…UseHttpsRedirection)` 之前

還原須早於 HTTPS 轉址（讀 Scheme）與 `UseRateLimiter`（讀 IP）。TraceId、Serilog request logging 在更外層，記錄到的 IP 仍是代理位址——可接受；讓請求日誌帶用戶端 IP 列為後續（見 Open Questions）。

### 決策 3：nginx 以 `$remote_addr` 覆蓋 `X-Forwarded-For`

`proxy_set_header X-Forwarded-For $remote_addr;`（不用 `$proxy_add_x_forwarded_for`）；加 `X-Forwarded-Proto $scheme`。nginx 是最外層、前面沒有受信任代理，串接用戶端自帶值會讓攻擊者預先塞入任意 IP；API 端 `ForwardLimit = 1` 取最後一個值本來就能擋，這裡再從源頭覆蓋是雙層防線。日後前面加 Ingress 時改用 nginx `real_ip` 模組，屬 change ②。不送 `X-Real-IP`：API 不讀。

### 決策 4：`web` Service 設 `externalTrafficPolicy: Local`

`Cluster`（預設）會對 NodePort 流量做 SNAT，nginx 看到的 `$remote_addr` 是節點 IP，修完仍共用計數。`Local` 保留來源 IP；代價是只有 web Pod 所在節點會處理該 NodePort 流量（其他節點的 NodePort 連線會被丟棄）。記錄於 K8s 指南；正式環境的解法是 Ingress／LB 的來源 IP 保留（change ②）。

### 決策 5：分區邏輯不改，login／captcha 自動同步

`GetPartitionKey` 仍讀 `RemoteIpAddress`；`UseForwardedHeaders` 還原後該值即為用戶端 IP。login 維持純 IP 分區（複合分區是另一個設計議題：攻擊者換帳號即可繞過、分區數隨帳號數增長，不屬於 C-2 最小修正）。

### 決策 6：`public-read` policy 以 controller 層級 `[EnableRateLimiting]` 套用，四個 GET 共用額度

Grill 時列了三個端點（列表、詳情、seats）；實作前查證 `EventsController` 還有第四個匿名 GET `ticket-types`，同為公開讀取，漏掉會留下繞過缺口，故一併納入（與 Q5「整個公開讀取面」的意圖一致）。controller 層級套用而非 GlobalLimiter：不影響探針與已登入端點。

獨立 `PublicReadRateLimitingOptions`（預設 120／60 秒）比照 `CaptchaRateLimitingOptions`：有安全預設值、`Range(1, int.MaxValue)`、不用 `ValidateOnStart`，在 `ResolveEagerlyValidatedOptions` 強制解析（理由同既有項目）。分區鍵重用 `GetPartitionKey`；policy 與計數器仍獨立。額度依據：正常瀏覽（列表→詳情→seats→ticket-types）每分鐘遠低於 120；壓測風險見下。

### 決策 7：驗證策略（TestServer 的 RemoteIpAddress 固定）

- 單元測試：直接建構 `DefaultHttpContext`（沿用 `LoginRateLimiterPartitioningTests` 模式）驗證設定解析、預設值與驗證。
- 整合測試：以 `WithWebHostBuilder` 覆寫設定，並在 `UseForwardedHeaders` 之前插入測試用 middleware 設定 `Connection.RemoteIpAddress`，模擬「來自受信任代理」「來自非信任來源」兩種連線，再帶不同 `X-Forwarded-For` 打 login／events，驗證 CAR-001～008、LRL-011／012、PRL-001～004。不為了測試在正式碼暴露開關。
- 部署驗證：`scripts/verify-k8s-manifests.sh` 補斷言（nginx 設定含 `X-Forwarded-For`、`web` Service `externalTrafficPolicy: Local`），並在 kind 叢集實測一次標頭到達 API 後的還原結果。

## 安全確認（CLAUDE.md 安全強制規則）

- 輸入驗證：外部輸入有兩類——(1) `X-Forwarded-For`／`X-Forwarded-Proto` 標頭，由 ASP.NET Core ForwardedHeaders middleware 解析，且僅信任網段內來源才處理；無法解析時維持實際連線位址（CAR-009），不拋例外；(2) 設定值 `ForwardedHeaders:TrustedNetworks`／`PublicReadRateLimiting`，於啟動時解析與驗證，錯誤即啟動失敗。不拼接 SQL 或 shell 指令。
- 資料庫：無新增 DB 存取，無參數化查詢與 N+1 疑慮。
- 權限：`public-read` 套用的端點本來就是匿名端點，防護手段為 IP 限流；權限檢查由 `UseRateLimiter` middleware（endpoint metadata）執行。未授權者可觸發這些端點是既有設計；偽造來源 IP 繞過限流的路徑由「僅信任網段」＋nginx 覆蓋 `X-Forwarded-For`＋`ForwardLimit = 1` 擋下（LRL-012）。
- 前端：不變，不渲染使用者輸入，無新增 API 呼叫。
- 機敏資訊：無新增；日誌不新增記錄標頭內容。

## 跨模組慣例對照（docs/api-conventions.md）

- 錯誤格式：429 沿用第 2 節「限流：429，`Title = "TooManyRequests"`，帶 `Retry-After` 與 `traceId`」，共用既有 `OnRejected`，不新增 `ErrorType`。沿用。
- 認證：`public-read` 套用的端點皆為匿名端點（第 1 節「匿名端點僅 `auth`、`captcha`、公開活動查詢」），不改認證。沿用。
- 路由／欄位命名／列表分頁：不新增端點、不改回應結構。不適用。
- 需同步更新：api-conventions.md 第 2 節限流列補 `public-read` 與「分區鍵為轉送還原後的 IP」，並註記新設定區段（`ForwardedHeaders`、`PublicReadRateLimiting`）。偏離：無。

## Risks / Trade-offs

- [信任網段過寬：同網段內任何 Pod 都能偽造標頭] → 目前由 `api-ingress` NetworkPolicy 限定只有 web 可連 API；日後放寬 NetworkPolicy 或讓 API 直接暴露時，須同步收窄 `TrustedNetworks`（文件記錄）。compose 開發環境發布 API 連接埠，宿主機直連的來源為 Docker bridge 閘道（私有網段），落在預設信任網段內，`X-Forwarded-For` 可被偽造；compose 僅限開發使用，不視為防偽造邊界。
- [`externalTrafficPolicy: Local` 使非 web 節點的 NodePort 連不到] → 文件註明；正式環境走 Ingress（change ②）。
- [限流計數器為 in-memory、每副本獨立，2 副本下實際額度最多為設定值的 2 倍] → 既有設計，非本 change 範圍；記錄即可。
- [`public-read` 120/60 可能擋到壓測] → k6 腳本從單一 IP 打 `GET /api/events*` 時會被限流；apply 時檢查 `loadtest/` 腳本，必要時於壓測設定覆寫 `PublicReadRateLimiting`，不降低預設值。
- [代理未轉送標頭時所有請求落入同一分區] → 與修正前行為相同，不比現況差；LRL 需求的「部署前提」已寫明。
- [`KnownIPNetworks` API 名稱依版本而異] → 決策 1 已要求實作時對照實際套件。

## Migration Plan

1. 部署新版 API（預設設定即可運作，無必填設定）。
2. 更新 nginx 設定與 `web` Service（`kubectl apply`）；順序無關：API 在未收到標頭時行為與舊版相同。
3. 回滾：還原 manifest 與映像即可，無資料變更。

## Open Questions

- 請求日誌（Serilog）是否也記錄還原後的用戶端 IP：不影響本 change 的需求與任務，可於後續 observability 相關 change 決定。
