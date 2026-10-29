# K8s 部署待辦（2026-10-28）

狀態：**尚未開始**。目前沒有雲端部署環境，先以 Docker Desktop 內建 K8s（kubeadm 模式）在本機驗證 manifest。部署到共享 VPS 的另一條線見記憶 `project_cloud_deploy_deferred`；VPS 剩餘記憶體約 858MB，跑不動 K8s 控制平面，因此 VPS 部署仍以 compose 為準。

## 目標與定位

- 本機開發與測試**繼續使用 docker compose**（bind mount 熱更新、`docker compose exec api dotnet test`、Testcontainers DooD、`block-host-dotnet.js` hook 都依賴它），不取代。
- K8s 只是**額外的部署目標**：production Dockerfile + `k8s/` manifest，驗收標準為在 Docker Desktop K8s 上跑完整購票流程。
- 目的：驗證容器化與 manifest 正確、練習後端工程師日常會碰的 K8s 操作（`kubectl`、探針、資源限制、Secret 注入、migration Job、滾動更新）。

## 已決定

| 項目 | 決定 | 理由 |
| --- | --- | --- |
| web 形式 | 多階段 build，nginx 服務靜態檔（約 16MB） | 去除 dev server，映像精簡 |
| `/api` 路由 | nginx 反向代理 `/api` → api Service | 對應 `web/vite.config.ts` 現有的 proxy（`/api` → `http://api:8080`）；瀏覽器維持同源，api 無 CORS 設定，不必改 CORS 或前端 API 位址，也不必安裝 Ingress controller |
| Seq | 放進叢集，不掛 PVC | api 已用 `Seq__ServerUrl`，同名 Service 即可；練習有狀態服務與 Service DNS；compose 也未掛 volume |
| manifest 格式 | 純 YAML，放 `k8s/` | 最簡單，不引入 Kustomize |
| 叢集模式 | Docker Desktop kubeadm 模式 | 本機 `docker build` 的映像與叢集共用 image store；kind 模式需 `kind load` |

## 待辦

### 1. api 新增健康檢查 endpoint

- 內容：`/health/live`（行程存活）與 `/health/ready`（DB、Redis 等相依就緒）。
- 原因：`src` 內沒有任何 `MapHealthChecks` / `AddHealthChecks`，K8s 的 readiness / liveness 探針沒有可探測的路徑；compose 的 k6 也註記「api 沒有 healthcheck」。
- 需補 spec，並依 TDD 流程撰寫測試。

### 2. Secret 與 ConfigMap 分流

| 類型 | 項目 |
| --- | --- |
| Secret | `POSTGRES_PASSWORD`（含組出的 `ConnectionStrings__DefaultConnection`）、`Jwt__SigningKey`、`TicketSigning__SigningKey`、`DevelopmentSeed__AdminPassword` |
| ConfigMap | `Jwt__Issuer`、`Jwt__Audience`、`Jwt__AccessTokenExpirationMinutes`、`Auth__RefreshTokenExpirationDays`、`Auth__PasswordResetTokenExpirationMinutes`、`Seq__ServerUrl`、`ConnectionStrings__Redis`、`DevelopmentSeed__AdminEmail`、`DevelopmentSeed__OrganizerName` |

- 原因：機敏資訊不得明文進版控；Secret 的實際值不得 commit，只提交範本（例如 `secret.example.yaml`）。
- 注意：`appsettings.Development.json` 內有已 commit 的 dev secret 與 `localhost`（見記憶 `project_webapi_todo`），轉換時一併檢視。

### 3. 決定 `ASPNETCORE_ENVIRONMENT`

- 選項：`Development`（seeder 會建立 Admin 與 Organizer，適合展示）或 `Production`（無 seeder，沒有 Admin）。
- 原因：首位 Admin 的 bootstrap 仍是待辦（見 `docs/project-review-2026-10-16.md`），Production 模式下無法登入管理端。
- **開提案時需使用者決定。**

### 4. 決定 nginx 是否需要 TLS

- 背景：`web/vite.config.ts` 的 dev server 有 `httpsConfig`。
- 選項：本機展示只用 HTTP，或 nginx 掛自簽憑證。
- 影響：相機（兌換頁）等瀏覽器 API 可能要求安全來源（localhost 例外）。開提案時確認。

### 5. 確認映像對叢集可見

- 實作第一步：`docker build` 後建立 Pod，確認不需推 registry 即可拉到映像；若不行，設 `imagePullPolicy: Never` 或改用本機 registry。
- 原因：叢集由 kind 模式切到 kubeadm 模式（2026-10-28），共用 image store 的假設需實測，不憑文件判斷。

## 實作範圍草案（單一 change）

- production 多階段 Dockerfile：api（SDK build → aspnet runtime）、web（node build → nginx）。
- `k8s/`：Namespace、Deployment / Service（api、web、db、redis、seq）、ConfigMap、Secret 範本、Postgres PVC、migration Job、探針、資源 requests / limits。
- db 與 redis 在 manifest 內使用 service name（`db`、`redis`、`seq`），與 compose 一致，連線字串不用 `localhost`。
- migration 以 Job 執行，維持既有「先 migrate 再上新版」原則。
- 預估：最小可跑版本 3–5 天；含 production 映像、探針、Secret 的完整版 1–2 週。依 CLAUDE.md 的 change 顆粒度規則，若超過 3 個 capability 或約 30 項 task，須評估拆分（例如：① 健康檢查 + production 映像；② k8s manifest 與本機驗證）。

## 本機環境確認（2026-10-28）

- `kubectl` v1.36.1，context `docker-desktop`，節點 Ready，kubeadm 模式。
- Docker 配置 15.49GiB 記憶體、20 CPU，資源充足。
- compose 與 K8s 可同時運行，前提是對外 port 不重複：compose 的 port 可用 `.env` 的 `API_HOST_PORT` / `WEB_HOST_PORT` / `DB_HOST_PORT` / `SEQ_HOST_PORT` 調整，K8s 對外用 `NodePort`（30000 以上）或 `port-forward`；兩邊資料庫彼此獨立。
- 避開 Windows 排除範圍（8080 / 8081 / 5173 有時會落入），並用明顯不同的 port 範圍區分兩套環境，避免搞混正在操作哪一套。

## 驗收方向（提案時細化為 AC）

- 全部 Pod Ready，探針通過；migration Job 完成。
- 瀏覽器開啟 web，完成登入 → 瀏覽活動 → 下單 → 付款的主流程。
- 刪除 api Pod 後自動重建，服務恢復；滾動更新期間請求不中斷。
- Secret 值不出現在版控與 log 中。
