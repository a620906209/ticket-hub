# Tasks

> 本 change 為純部署產物（`skip_specs: true`），無新增 Acceptance Criteria；以下「驗收準則」取代 AC，每條對應驗證 task。映像相關操作只透過 `docker build`／`docker compose`，不在本機執行 dotnet／psql。
>
> | 驗收準則 | 驗證 |
> | --- | --- |
> | D1：api runtime 映像非 root、不含 SDK、不含 `appsettings.Development.json` | 2.1 腳本斷言 2、8 |
> | D2：`migrator` 能對空資料庫套用全部 migration | 2.1 腳本斷言 3 |
> | D3：api 在 production 映像啟動後 `/health/live`、`/health/ready` 回 200；缺 `Jwt__SigningKey` 時啟動失敗 | 2.1 腳本斷言 4、7 |
> | D4：經 nginx 的 `/api` 可達且非 3xx、可傳 API 允許的最大請求本文（不被 nginx 413）；SPA 子路由重新整理回 200；快取標頭正確 | 2.1 腳本斷言 5、6、9 |
> | D5：機敏／一般設定分類完整且與 `docker-compose.yml`、`.env.example` 一致 | 3.1 文件核對 |
> | D6：腳本在映像被破壞時會失敗 | 2.3 突變驗證 |
>
> TDD：映像沒有 xUnit 可觸及的行為，依 design 決策 5 的替代驗證例外，先寫腳本（1.x 之前不存在任何 Dockerfile 時預期失敗），再補 Dockerfile 使腳本通過。證據一律貼原文（CLAUDE.md「證據貼原文」）：每個失敗斷言的名稱與輸出、腳本摘要行、突變命令與輸出。hardener：N/A（無 I/O 程式碼、Handler 或 Repository 變更）。

## 1. 前置檔案

- [ ] 1.1 補強根目錄 `.dockerignore`（`**/appsettings.Development.json`、`**/.env*`、`**/node_modules`、`**/certs`、`web/`）並新增 `web/.dockerignore`（`node_modules`、`dist`、`certs`、`.env*`、`*.md`）。驗證：2.1 斷言 8。
  - 完成紀錄：（待填）
- [ ] 1.2 新增 `docs/todo/2026-11-10-deployment-env-vars.md`：逐項對照 `docker-compose.yml` 與 `.env.example`，分「機敏（Secret）」「一般（ConfigMap）」「執行環境（ASPNETCORE_*）」三類並寫明歸類理由；標註 `docs/todo/2026-10-28-k8s-todo.md` 對應進度。驗證：3.1。
  - 完成紀錄：（待填）

## 2. 映像與驗證腳本

- [ ] 2.1 先寫 `scripts/verify-production-images.sh` 與 `deploy/docker-compose.verify.yml`（獨立 compose 專案 `projectc-verify`，機敏值由腳本隨機產生，不進版控），實作 design 決策 5 的斷言 1～9。**RED**：在 `deploy/*.Dockerfile` 尚不存在時執行，預期因建置失敗而非 0 結束（記錄命令與原文輸出；此為流程性 RED，不當作行為 RED）。
  - 完成紀錄：（待填：RED 輸出）
- [ ] 2.2 新增 `deploy/api.Dockerfile`（`build`／`runtime`／`migrator`）、`deploy/web.Dockerfile` 與 `deploy/nginx/default.conf`，內容依 design 決策 2、3。**GREEN**：`bash scripts/verify-production-images.sh` 全部斷言通過。另以真實瀏覽器（claude-in-chrome）對 web 容器對外 port 跑一次：註冊、登入、重新整理子路由；數字與狀態以頁面文字或 DB 核對。
  - 完成紀錄：（待填：腳本摘要行原文、瀏覽器核對結果）
- [ ] 2.3 突變驗證（腳本須失敗；一次一個改動，還原後重跑通過）：(a) `runtime` stage 改 `USER root`；(b) 移除 nginx `try_files`；(c) 移除 `migrator` 的 `ef migrations bundle` 步驟；(d) 在 `.dockerignore` 移除 `**/appsettings.Development.json`；(e) 移除 nginx `/api/` 的 `client_max_body_size 10m`。每個突變貼失敗斷言名稱與輸出原文。
  - 完成紀錄：（待填）

## 3. 文件與收尾

- [ ] 3.1 核對 1.2 文件：以腳本或 `grep` 列出 `docker-compose.yml` 的 `api` 環境變數鍵與 `.env.example` 鍵，逐項確認都在分類表中、沒有遺漏或重複；貼核對命令與結果。
  - 完成紀錄：（待填）
- [ ] 3.2 更新 `docs/todo/2026-10-28-k8s-todo.md`：標註「production-images」完成的項目與日期，保留 `k8s-local-manifests` 待辦；執行 `bash scripts/check-doc-paths.sh` 通過。
  - 完成紀錄：（待填）
- [ ] 3.3 回報：實作結果是否與 design 一致、偏差點、spec 同步狀態（無 spec 影響）、AC 對應（D1～D6）、維護性提醒（重複邏輯：Dockerfile 與開發用 Dockerfile 的 SDK／Node 版本是否一致；命名一致性）。
  - 完成紀錄：（待填）
