# Tasks

> 本 change 為純部署產物（`skip_specs: true`），無新增 Acceptance Criteria；以下「驗收準則」取代 AC，每條對應驗證 task。所有叢集操作只對 context `docker-desktop` 進行；映像相關操作只透過 `docker build`／`docker compose`，不在本機執行 dotnet／psql。
>
> | 驗收準則 | 驗證 |
> | --- | --- |
> | K1：全部 Pod Ready、migrator Job 完成且 migration 筆數正確 | 1.2 斷言 1；3.1 |
> | K2：探針路徑正確；DB 不可用時 api 轉為 NotReady | 1.2 斷言 2；3.1 |
> | K3：經 NodePort 可達 web 與 `/api`，且非 3xx | 1.2 斷言 3；3.1 |
> | K4：刪除 api Pod 後服務不中斷並自動重建 | 1.2 斷言 4；3.1 |
> | K5：滾動更新期間請求零失敗 | 1.2 斷言 5；3.1 |
> | K6：Secret 值不出現在 log、describe、版控 | 1.2 斷言 6；3.1 |
> | K7：缺 `Jwt__SigningKey` 時 api 啟動失敗並留下可查證的結束碼與日誌 | 1.2 斷言 7；3.1 |
> | K8：`/health/*` 不可經 web 取得；api 無對外 Service；不可信 workload 不可達（依 CNI 結果為 PASS／FAIL／UNVERIFIED） | 1.2 斷言 8、9；3.1；4.2 |
> | K9：腳本在 manifest 被破壞時會失敗 | 3.2 突變驗證 |
> | K10：完整購票流程可在叢集上完成 | 3.3 真實瀏覽器 |
>
> TDD：manifest 沒有 xUnit 可觸及的行為，依 design 決策 8 的替代驗證例外（**待使用者核准**），先寫腳本（`k8s/` 不存在時預期失敗），再補 manifest 使腳本通過。證據一律貼原文（CLAUDE.md「證據貼原文」）：每個失敗斷言的名稱與輸出、腳本摘要行、突變命令與輸出、`git stash`／`restore` 指令輸出。hardener：N/A（無 I/O 程式碼、Handler 或 Repository 變更）。

## 1. 前置實測與驗證腳本

- [ ] 1.1 實測前置假設並記錄：(a) 起一個使用 `projectc/api:local` 的 Pod，確認 `imagePullPolicy: IfNotPresent` 能直接取得本機映像（失敗則改 `Never` 並更新 design 決策 2）；(b) `netsh int ipv4 show excludedportrange protocol=tcp` 確認 30080／30081 不在排除範圍（衝突則改埠並同步 proposal／design）；(c) `kubectl get pods -A` 與對照實驗（deny-all NetworkPolicy）確認本機 CNI 是否執行 NetworkPolicy。驗證：三項結果原文貼在完成紀錄，並把 (c) 結論寫回 design 決策 7。
  - 完成紀錄：（實作後填）
- [ ] 1.2 先寫 `scripts/verify-k8s-manifests.sh`，實作 design 決策 8 的斷言 1～9（含 context 守衛、`projectc-verify` Namespace、隨機 Secret、`trap` 清理、`UNVERIFIED` 專用結束碼與摘要行）。**RED**：在 `k8s/` 尚不存在時執行，預期因資源不存在而非 0 結束（記錄命令與原文輸出；此為流程性 RED，不當作行為 RED）。驗證：腳本對不存在的 manifest 非 0 結束，且不留下殘餘資源。
  - 完成紀錄：（實作後填；RED 命令、結束碼、原文輸出）

## 2. Manifest

- [ ] 2.1 新增 `k8s/00-namespace.yaml`、`k8s/10-configmap.yaml`、`k8s/secret.example.yaml`，並在 `.gitignore` 加入 `k8s/secret.yaml`。驗證：`kubectl apply --dry-run=server -f k8s/` 無錯；`git check-ignore k8s/secret.yaml` 命中；範本只含佔位值。
  - 完成紀錄：（實作後填）
- [ ] 2.2 新增 `k8s/20-postgres.yaml`（PVC＋Deployment `Recreate`＋Service `db`）、`k8s/21-redis.yaml`、`k8s/22-seq.yaml`（含資源 requests／limits）。驗證：腳本斷言 1 的 db／redis／seq 部分通過。
  - 完成紀錄：（實作後填）
- [ ] 2.3 新增 `k8s/30-migrator-job.yaml`（`args` 用 `$(ConnectionStrings__DefaultConnection)`、`backoffLimit: 2`）。驗證：腳本斷言 1（Job Complete、migration 筆數）與斷言 6（describe 不含密碼）通過。
  - 完成紀錄：（實作後填）
- [ ] 2.4 新增 `k8s/40-api.yaml`（2 副本、`RollingUpdate maxUnavailable 0 / maxSurge 1`、三種探針、`preStop`、資源）。驗證：腳本斷言 1、2、4、5、7 通過。
  - 完成紀錄：（實作後填；含資源初值依實測調整的紀錄）
- [ ] 2.5 新增 `k8s/50-web.yaml`（NodePort 30080、readiness 打 `/`、資源）。驗證：腳本斷言 3、8（web 不轉發 `/health/*`）、9 通過。
  - 完成紀錄：（實作後填）
- [ ] 2.6 新增 `k8s/60-networkpolicy.yaml`（api 只收 web、db 只收 api 與 migrator、redis 與 seq 只收 api）。驗證：腳本斷言 1 在套用後仍通過（探針與 Pod Ready 不受影響）；斷言 8 的對照實驗結果依 1.1(c) 輸出。
  - 完成紀錄：（實作後填）

## 3. 驗證

- [ ] 3.1 執行 `bash scripts/verify-k8s-manifests.sh` 全套。驗證：摘要行為 `0 failed`（`UNVERIFIED` 若有須單獨列出且與待辦檔一致）。
  - 完成紀錄：（實作後填；貼完整輸出原文與摘要行）
- [ ] 3.2 突變驗證（手動改動、還原、貼原文，不呼叫 `verify-test-evidence.sh mutate`）：a 拿掉 api `readinessProbe`（預期斷言 2 失敗）；b api 副本改 1（斷言 4）；c 把 `Jwt__SigningKey` 放進 ConfigMap（斷言 6）；d nginx 多 `location /health/` 轉發（斷言 8；須重建 `projectc/web:local`，還原後再重建）；e api Service 改 NodePort（斷言 9）。腳本須因對應斷言失敗，還原後重跑通過。驗證：每個突變的失敗斷言名稱與實際輸出、還原後摘要行；`git status` 於還原後為乾淨。
  - 完成紀錄：（實作後填；每個突變的命令與原文輸出）
- [ ] 3.3 以 claude-in-chrome 對 `http://localhost:30080`（或 1.1 確認後的埠）做真實瀏覽器流程：以 seeder Admin 登入、建立活動與票種、改以一般會員登入、瀏覽、下單、付款。驗證：頁面文字與 DB（`kubectl exec` 進 db 查詢）核對訂單狀態，不只依截圖；結果與觀察到的異常貼在完成紀錄。
  - 完成紀錄：（實作後填）

## 4. 文件與待辦

- [ ] 4.1 更新 `docs/todo/2026-10-28-k8s-todo.md`（進度）、`docs/todo/2026-11-02-deployment-prerequisites.md` 第 1 項（寫明本機實測狀態，不改必要條件文字）、`docs/todo/2026-11-10-deployment-env-vars.md`（migrator 密碼改以 `$(VAR)` 引用）、`docs/todo/2026-11-10-api-missing-config-exit-code-139.md`（寫入斷言 7 的 K8s 實測結果）。驗證：`bash scripts/check-doc-paths.sh` 通過；各文件的路徑引用有效。
  - 完成紀錄：（實作後填）
- [ ] 4.2 新增 `docs/todo/2026-11-10-networkpolicy-enforcement-todo.md`，記錄「不可信 workload 不可達」在本機未驗證的原因、需要的環境（可執行 NetworkPolicy 的 CNI）、驗證步驟與解除條件；若 1.1(c) 顯示 CNI 有執行，改記為已驗證並刪除此待辦。驗證：文件存在，且與腳本輸出的 `UNVERIFIED` 狀態一致。
  - 完成紀錄：（實作後填）
- [ ] 4.3 hardener 最終檢查：N/A（無 I/O 程式碼變更）。驗證：此項記錄 N/A 理由即可。
  - 完成紀錄：（實作後填）

## Workflow follow-up

- 實作 pane 先跑 `strict-reviewer` 至 PASS → 外部 reviewer → 外部 PASS 後再跑 `strict-reviewer` 與全新的對抗式 subagent（見 CLAUDE.md 流程第 6 步）。
- 無前端程式碼變更，但 3.3 為真實瀏覽器驗證。
- 審查通過後 `/opsx:archive`（`skip_specs`，無主 spec 同步）→ merge 回 master；歸檔後詢問是否以 `bash scripts/archive-todo.sh` 搬移已完成的待辦。
