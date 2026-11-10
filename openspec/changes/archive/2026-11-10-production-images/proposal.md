# Proposal

## Why

現有 `Dockerfile` 與 `web/Dockerfile` 都是開發用：SDK 映像、原始碼 bind mount、`dotnet watch`／Vite dev server，無法直接放進 Kubernetes。`docs/todo/2026-10-28-k8s-todo.md` 規劃的本機 K8s 學習線（`k8s-local-manifests`）需要先有可獨立執行的 production 映像；先把映像與環境變數分類獨立成一個 change，K8s manifest 才不必同時處理建置問題。

## What Changes

- 新增 api 的 production 多階段 Dockerfile（`deploy/api.Dockerfile`）：`runtime` target 使用 `mcr.microsoft.com/dotnet/aspnet:10.0`、非 root、監聽 8080、`ASPNETCORE_ENVIRONMENT` 預設 `Production`；另有 `migrator` target，在建置階段以 `dotnet ef migrations bundle` 產生自包含執行檔，供日後 migration Job 使用，不把 SDK 帶進 runtime 映像。
- 新增 web 的 production 多階段 Dockerfile（`deploy/web.Dockerfile`）：node build → `nginxinc/nginx-unprivileged`，nginx 設定（`deploy/nginx/default.conf`）含 `/api` 反向代理到 `http://api:8080`（本文上限不低於 API 最大單端點上限 10 MB）、SPA `try_files` 回退、hash 靜態檔長快取、`index.html` 不快取。
- 補強根目錄 `.dockerignore`（api 建置不需要 `web/`，一併排除以縮小 context），新增 `web/.dockerignore`（排除 `node_modules`、`bin`、`obj`、`.env`、`web/certs` 等），確保憑證與機敏檔不進映像。
- 新增 `deploy/docker-compose.verify.yml` 與 `scripts/verify-production-images.sh`：以獨立 compose 專案起 db／redis／api／web 並做可重現的通過／失敗斷言（見 design.md 決策 5）。
- 新增 `docs/todo/2026-11-10-deployment-env-vars.md`：環境變數分類表（機敏 → 日後 K8s Secret；一般設定 → ConfigMap），並在 `docs/todo/2026-10-28-k8s-todo.md` 標註本 change 對應的項目。
- **非變更**：不動既有開發用 `Dockerfile`、`web/Dockerfile`、`docker-compose.yml`；不動任何 `.cs`／`.vue` 程式碼；不新增 K8s manifest、Secret／ConfigMap 物件、NetworkPolicy；不做 TLS；不做 CI 建置映像；`ASPNETCORE_ENVIRONMENT=Development`（demo seeding）的覆寫留給 `k8s-local-manifests`。

## Capabilities

### New Capabilities
無。

### Modified Capabilities
無。純部署產物，不改變任何對外行為（HTTP 契約、設定鍵、啟動驗證），因此在 `.openspec.yaml` 設定 `skip_specs: true`。驗收改由 `scripts/verify-production-images.sh` 的斷言承擔（design.md 決策 5 的替代驗證例外），不為通過驗證而發明 Requirement。

## Impact

- 新增檔案：`docs/todo/2026-11-10-deployment-env-vars.md`、`deploy/api.Dockerfile`、`deploy/web.Dockerfile`、`deploy/nginx/default.conf`、`deploy/docker-compose.verify.yml`、`web/.dockerignore`、`scripts/verify-production-images.sh`。
- 修改檔案：`.dockerignore`、`docs/todo/2026-10-28-k8s-todo.md`（標註進度）。
- 套件／API／設定鍵：無變動。`docker-compose.yml` 的 `bin`／`obj` named volume 規則不受影響（未新增 .csproj）。
- Change 顆粒度：0 個 capability、tasks 8 項（未達拆分門檻），不需拆分。
