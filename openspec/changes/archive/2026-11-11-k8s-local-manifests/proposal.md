# Proposal

## Why

`production-images` 已產出 api／migrator／web production 映像與環境變數分類（`docs/todo/2026-11-10-deployment-env-vars.md`），但還沒有任何 K8s manifest，也沒驗證過容器化後的完整購票流程能在叢集上跑。`docs/todo/2026-10-28-k8s-todo.md` 的目標是在 Docker Desktop K8s（kubeadm 模式）驗證 manifest 並練習 `kubectl`、探針、資源限制、Secret 注入、migration Job、滾動更新；`docs/todo/2026-11-02-deployment-prerequisites.md` 另要求任何開發機以外的部署都要先限制 `/health/*` 的來源。本 change 補上這一步。

## What Changes

- 新增 `k8s/`（純 YAML，不用 Kustomize／Helm）：Namespace `projectc`；db（PVC）、redis、seq（不掛 PVC）、api（2 副本）、web（1 副本）的 Deployment 與 Service；ConfigMap；Secret 範本 `examples/secret.example.yaml`（實際 `secret.yaml` 加入 `.gitignore`）；migrator Job；探針與資源 requests／limits；NetworkPolicy。
- 對外只有 web 的 NodePort（30080）；api、db、redis、seq 一律 ClusterIP。瀏覽器同源走 nginx 的 `/api`，不改 CORS 與前端 API 位址。
- migrator Job 以 `args` 的 `$(ConnectionStrings__DefaultConnection)` 引用 Secret 注入的環境變數，密碼不出現在 manifest 與 `kubectl describe`；不改 migrator 程式。
- `ASPNETCORE_ENVIRONMENT=Development`（ConfigMap 覆寫映像預設的 `Production`），讓 seeder 建立展示用 Admin 與 Organizer；只用 HTTP，不做 TLS。
- 新增 `scripts/verify-k8s-manifests.sh`：以可判定通過／失敗的斷言驗證叢集行為（見 design.md 決策 8 的替代驗證例外）。
- 新增 `docs/todo/2026-11-10-networkpolicy-enforcement-todo.md`：記錄本機 CNI 無法驗證的「不可信 workload 不可達」缺口。
- 更新 `docs/todo/2026-10-28-k8s-todo.md`（進度）、`docs/todo/2026-11-02-deployment-prerequisites.md`（第 1 項的實測狀態）、`docs/todo/2026-11-10-deployment-env-vars.md`（migrator 密碼注入方式）、`docs/todo/2026-11-10-api-missing-config-exit-code-139.md`（K8s 缺設定演練結果）。
- **非變更**：不動 `docker-compose.yml`、既有開發用 Dockerfile、任何 `.cs`／`.vue` 程式碼與 migrator；不做 TLS、Ingress controller、Kustomize、Helm、CI、映像推送、VPS 部署；不處理 `X-Forwarded-*` 來源 IP 轉送；不為 Seq 設驗證或 PVC。

## Capabilities

### New Capabilities
無。

### Modified Capabilities
無。純部署產物，不改變任何對外行為（HTTP 契約、設定鍵、啟動驗證），因此在 `.openspec.yaml` 設定 `skip_specs: true`，與 `production-images` 相同。驗收改由 `scripts/verify-k8s-manifests.sh` 的斷言與一次真實瀏覽器流程承擔（design.md 決策 8），不為通過驗證而發明 Requirement。

## Impact

- 新增檔案：`k8s/*.yaml`（約 10 個）、`scripts/verify-k8s-manifests.sh`、`docs/todo/2026-11-10-networkpolicy-enforcement-todo.md`。
- 修改檔案：`.gitignore`（`k8s/secret.yaml`）、上列四份 `docs/todo/` 文件。
- 套件／API／設定鍵：無變動。`docker-compose.yml` 的 `bin`／`obj` named volume 規則不受影響（未新增 .csproj）。
- 本機資源：叢集使用 NodePort 30080，與 compose 的 5173／8080／8081 不重複；須確認 30080 不在 Windows 排除範圍。
- Change 顆粒度：0 個 capability、tasks 14 項（未達拆分門檻），不需拆分。
