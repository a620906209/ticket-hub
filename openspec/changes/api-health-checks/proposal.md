## Why

api 目前沒有健康檢查 endpoint，後續部署到 K8s 時，readiness 與 liveness 探針沒有可探測的路徑（見 `docs/k8s-todo.md` 第 1 項）。這是 K8s 部署的前置工作，需先有獨立、可驗證的探測行為，後續 manifest 才能引用。

## What Changes

- 新增 `GET /health/live`：不檢查任何相依，行程能處理請求就回 200。
- 新增 `GET /health/ready`：只檢查 PostgreSQL，2 秒內成功回 200，失敗或逾時回 503。Redis、Seq 故障不影響兩個端點。
- 兩個端點匿名、不套用 rate limit；探測結果的回應內容只有 `Healthy` 或 `Unhealthy`；未預期例外回通用的 500。兩者都不暴露相依名稱或錯誤細節。
- 成功探測的請求摘要日誌降為 Debug，失敗照常記錄，避免探針定期請求淹沒日誌。
- 兩個端點不受 HTTPS 轉址影響；其他端點的轉址行為不變。
- 修改 `docs/k8s-todo.md` 第 1 項與「驗收方向」：readiness 改為只檢查 DB 並記錄理由，加入部署前置條件與必要 AC。

不在範圍內：production Dockerfile、K8s manifest 與探針設定、compose 的 api healthcheck、Secret / ConfigMap 分流、`ASPNETCORE_ENVIRONMENT` 與 nginx TLS 的決定、Redis 健康監控、探測端點的部署來源限制與連線驗證（後續部署 change 的必要 AC，見 `design.md` Risks）。

## Capabilities

### New Capabilities

- `api-health-checks`: api 對部署平台提供的存活與就緒探測端點，涵蓋判定條件、存取限制、回應內容、日誌等級與轉址行為。

### Modified Capabilities

無。`observability` 的請求摘要日誌規範約束日誌內容不含敏感資訊，未規定等級，本次等級調整不改變該 Requirement。

## Impact

- **程式碼**：`src/ProjectC.WebApi/Program.cs`（服務註冊、中介軟體管線、端點對應）；WebApi 內新增探測路徑的集中定義與資料庫就緒檢查。
- **相依**：無新增套件，Health Checks 已包含在 ASP.NET Core shared framework。
- **API**：新增兩個匿名 GET 端點，路徑不在 `/api` 底下，不影響既有端點；web 的 `/api` proxy 不會轉送這兩個路徑。
- **測試**：`tests/ProjectC.WebApi.Tests` 新增健康檢查整合測試與測試用 factory。
- **文件**：`docs/k8s-todo.md`、`docs/deployment-prerequisites.md`。
- **DB／migration**：無。
