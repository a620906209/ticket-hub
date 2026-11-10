# Design

## Context

動機見 proposal.md「Why」。已讀檔案與實測確認的現況：

- 映像（`production-images`）：`projectc/api`（runtime，監聽 8080、非 root、預設 `Production`）、`projectc/api-migrator`（EF bundle，`--connection "<連線字串>"`）、`projectc/web`（nginx-unprivileged 監聽 8080，`/api/` 以 `proxy_pass http://api:8080` 轉發，上游主機名 `api` 必須能解析，否則 nginx 啟動即退出）。
- 環境變數分類：`docs/todo/2026-11-10-deployment-env-vars.md`（Secret：`POSTGRES_PASSWORD`、`ConnectionStrings__DefaultConnection`、`Jwt__SigningKey`、`TicketSigning__SigningKey`、`DevelopmentSeed__AdminPassword`；其餘為 ConfigMap 或各服務固定值）。
- api 不在啟動時 migrate；`/health/live`、`/health/ready` 已存在（`Program.cs` 的 `MapHealthChecks`）。`/health/ready` 只檢查 DB，且 2 秒上限不含 DB 主機名稱解析，DNS 無回應時實測可達 10～12 秒（`deployment-prerequisites` 第 3 項），探針逾時須據此設定。
- 叢集（2026-11-10 實測）：context `docker-desktop`，`kube-system` 只有 coredns、etcd、apiserver、controller-manager、kube-proxy、scheduler、storage-provisioner、vpnkit-controller，**沒有 Calico／Cilium 等會執行 NetworkPolicy 的 CNI 元件**。NetworkPolicy 是否被執行屬未驗證假設，見決策 7。
- nginx 的 `location /` 使用 `try_files $uri /index.html`：對 web 請求 `/health/live` 會回 200 與 SPA 的 `index.html`，不是 404。「web 不轉發 `/health/*`」的斷言因此不能只看狀態碼，須看回應本文不是 api 的健康檢查結果。
- `deployment-prerequisites` 第 1 項指出 NodePort 會繞過「僅內部可達」的假設；本 change 只對外開 web 的 NodePort，api 不開。

本 change 設 `skip_specs: true`，理由見 proposal.md「Capabilities」。

### 跨模組慣例對照

不適用：不新增或修改 API 端點、錯誤回應、認證／授權、DTO 欄位或列表端點（純部署產物）。

### 安全確認（CLAUDE.md 觸發條件對照）

- 外部輸入：無新增輸入點；對外只有 web NodePort，沿用既有 API 驗證。N/A。
- 資料庫：不新增查詢；migrator 沿用 EF bundle。N/A。
- 權限：Secret 以 K8s Secret 注入；api 無對外 Service；`/health/*` 不經 web 轉發；NetworkPolicy 限制叢集內來源（本機是否執行見決策 7）。
- 前端：無前端變更。N/A。
- 暴露面：Docker Desktop 的 NodePort 可能綁定所有網路介面，區網內可嘗試登入 seeder Admin。manifest 與文件註明「僅限信任的本機網路」，`secret.example.yaml` 的佔位密碼不得使用可猜的值（例如 `password123`），改為明顯需替換的字串。

## Goals / Non-Goals

**Goals:**
- 在 Docker Desktop K8s 上以 manifest 跑起完整系統（db、redis、seq、migrator、api×2、web），完成登入 → 瀏覽活動 → 下單 → 付款。
- 以可重現的腳本證明：Pod Ready、migration Job 完成、刪 api Pod 後恢復、滾動更新不中斷、Secret 值不出現在 log 與 describe、`/health/*` 不能經 web 取得。
- 把 `deployment-prerequisites` 第 1 項能在本機驗證的部分做完，驗證不了的明確記成待辦。

**Non-Goals:**
- TLS、Ingress controller、Kustomize、Helm、CI、映像推送、VPS（compose）部署、`X-Forwarded-*` 來源 IP 轉送、Seq 驗證與持久化、Redis 持久化、HPA、PodDisruptionBudget。
- 改動 migrator 或任何應用程式碼。
- 首位 Admin 的正式 bootstrap（本 change 以 `Development` seeder 繞過，見決策 3）。

## Decisions

### 決策 1：檔案配置與套用順序

純 YAML 放 `k8s/`，檔名以數字前綴表示套用順序，單一 Namespace `projectc`：

| 檔案 | 內容 |
| --- | --- |
| `00-namespace.yaml` | Namespace |
| `10-configmap.yaml` | api 一般設定、db 的 `POSTGRES_USER`／`POSTGRES_DB` |
| `secret.example.yaml` | Secret 範本（佔位值）；實際 `secret.yaml` 由使用者複製，列入 `.gitignore` |
| `20-postgres.yaml` | PVC、Deployment（1 副本，`Recreate`）、Service `db` |
| `21-redis.yaml` | Deployment、Service `redis` |
| `22-seq.yaml` | Deployment、Service `seq`（只開 5341 ingestion 給 api；UI 用 `kubectl port-forward`） |
| `30-migrator-job.yaml` | Job |
| `40-api.yaml` | Deployment（2 副本）、Service `api` |
| `50-web.yaml` | Deployment（1 副本）、Service `web`（NodePort 30080） |
| `60-networkpolicy.yaml` | NetworkPolicy |

Service 名稱沿用 compose 的 `db`、`redis`、`seq`、`api`，連線字串因此與 compose 一致，不使用 `localhost`。套用順序（腳本與文件都依此）：Namespace → ConfigMap／Secret → db／redis／seq（等 Ready）→ migrator Job（等 Complete）→ api（等 Ready）→ web → NetworkPolicy。理由：維持「migrate before new version」；api 的 `/health/ready` 只檢查 DB 連線，不檢查 schema，所以順序由套用流程保證，不靠探針。Job 的 `spec.template` 不可變，重跑須先刪除舊 Job，腳本與文件都要寫明。

替代方案：Kustomize `base`／`overlay`。否決：k8s-todo 已決定純 YAML，單一環境不需要 overlay。

### 決策 2：映像取得方式

使用本機建置的映像（tag `projectc/api:local`、`projectc/api-migrator:local`、`projectc/web:local`），`imagePullPolicy: IfNotPresent`；Docker Desktop kubeadm 模式與 `docker build` 共用 image store 的假設需實測。實作第一步（task 1.1）起一個使用本機映像的 Pod：若 `ErrImagePull`，改設 `imagePullPolicy: Never`；仍不行才考慮本機 registry，並更新本決策。不使用 `latest` tag（`latest` 會使預設 pull policy 變 `Always`）。

### 決策 3：`ASPNETCORE_ENVIRONMENT=Development`

使用者 2026-11-10 決定。理由：首位 Admin 的 bootstrap 仍是待辦，`Production` 沒有 Admin，無法登入管理端與完成購票流程驗證。`DevelopmentSeed__AdminEmail`、`DevelopmentSeed__OrganizerName` 放 ConfigMap，`DevelopmentSeed__AdminPassword` 放 Secret。副作用：Development 會啟用 OpenAPI／SwaggerUI；因 api 只有 ClusterIP 且 web 只轉發 `/api/`（`deploy/nginx/default.conf`），對外不可達；叢集內其他 Pod 是否可達取決於 NetworkPolicy，本機未必執行（決策 7），所以實際的緩解是前者，不依賴 NetworkPolicy。此設定是展示用，不得原樣帶到共享或對外環境（寫入 manifest 註解）。

### 決策 4：Secret 與 ConfigMap 注入

- Secret `projectc-secrets` 的鍵：`POSTGRES_PASSWORD`、`ConnectionStrings__DefaultConnection`、`Jwt__SigningKey`、`TicketSigning__SigningKey`、`DevelopmentSeed__AdminPassword`。api 以 `envFrom.secretRef` 注入，db 以 `secretKeyRef` 取 `POSTGRES_PASSWORD`。連線字串為 `Host=db;Port=5432;Database=<db>;Username=<user>;Password=<password>`，由使用者在 `secret.yaml` 自行組出（與 `POSTGRES_PASSWORD` 須一致，範本註解寫明）。
- ConfigMap `projectc-config`：`ASPNETCORE_ENVIRONMENT=Development`、`ASPNETCORE_URLS=http://+:8080`、`Jwt__Issuer`、`Jwt__Audience`、`Jwt__AccessTokenExpirationMinutes`、`Auth__RefreshTokenExpirationDays`、`Auth__PasswordResetTokenExpirationMinutes`、`Seq__ServerUrl=http://seq:5341`、`ConnectionStrings__Redis=redis:6379`、`DevelopmentSeed__AdminEmail`、`DevelopmentSeed__OrganizerName`、`POSTGRES_USER`、`POSTGRES_DB`。
- Seq 的 `ACCEPT_EULA=Y`、`SEQ_FIRSTRUN_NOAUTHENTICATION=True` 直接寫在 Seq Deployment 的 `env`（非機敏，且 `deployment-env-vars` 已註明僅限本機展示）。
- 實際 `secret.yaml` 不進版控；腳本在執行時以隨機值產生 Secret，寫入 `mktemp -d` 的暫存檔、套用後由 `trap` 刪除，不寫入 repo、不使用 `set -x`。

### 決策 5：migrator Job 的連線字串

Job 以 `env` 的 `secretKeyRef` 取得 `ConnectionStrings__DefaultConnection`，容器 `args` 為 `["--connection", "$(ConnectionStrings__DefaultConnection)"]`（K8s 在容器啟動時展開 `$(VAR)`）。結果：manifest 與 `kubectl describe` 只出現 `$(...)` 與 `secretKeyRef`，不出現密碼；已知限制：容器內的行程參數仍是展開後的值（`ps` 或 `/proc/<pid>/cmdline` 可見），能進入該容器的人可讀到，接受並記在 Risks。`backoffLimit` 設小（2）、`ttlSecondsAfterFinished` 不設（保留供 `kubectl logs` 檢查）。替代方案：改 migrator 讀環境變數。否決：需改 migrator 程式與 spec，使用者 2026-11-10 決定不改。

### 決策 6：api 副本、探針、滾動更新與資源

- api 2 副本，`strategy: RollingUpdate`、`maxUnavailable: 0`、`maxSurge: 1`；web、db、redis、seq 各 1 副本（db 用 `Recreate`，避免兩個 Postgres 同時掛同一 PVC）。api 多副本與 Redis leader election 的設計相容（purchase-queue-leader-election）。
- 探針（路徑須與 `/health/live`、`/health/ready` 完全相同，結尾不加斜線，`deployment-prerequisites` 第 3 項）：`livenessProbe` 打 `/health/live`；`readinessProbe` 打 `/health/ready`，`timeoutSeconds` 設 15（高於實測 DNS 無回應的 10～12 秒），`failureThreshold` 3；`startupProbe` 打 `/health/live`，`periodSeconds: 5`、`failureThreshold` 不低於 24（至少 120 秒；seeder 在監聽前同步執行，雙副本首次啟動還可能重啟一次）。web 的 readiness 打 `/`。
- 優雅關閉：api 加 `preStop` `sleep 5` 並設 `terminationGracePeriodSeconds: 30`，讓 Service endpoint 移除先於行程收到 SIGTERM，滾動更新期間不丟請求；實測不達標才調整數值。
- 資源（初值，task 2.x 以實測 `kubectl top` 或 OOMKilled 結果調整並記錄）：api requests 100m／192Mi、limits 500m／512Mi；web 10m／16Mi／limits 100m／64Mi；db 100m／128Mi／limits 500m／512Mi；redis 20m／32Mi／limits 200m／128Mi；seq 100m／256Mi／limits 500m／1Gi。數值是起點，不是承諾。
- securityContext：api、web、migrator 設 `runAsNonRoot: true`、`allowPrivilegeEscalation: false`、`capabilities.drop: [ALL]`（映像已是非 root，`runAsNonRoot` 讓非 root 被改壞時由 K8s 直接拒絕啟動，腳本加斷言）；db、redis、seq 沿用映像預設。api 以 `envFrom.secretRef` 取得整個 Secret（含不需要的 `POSTGRES_PASSWORD`），本機展示可接受。
- db：`PGDATA` 設為掛載點子目錄（`/var/lib/postgresql/data/pgdata`），避免 volume 根目錄非空時初始化失敗。
- db PVC：`storageClassName` 不指定，使用 Docker Desktop 預設的 `hostpath` provisioner；容量 1Gi。

### 決策 7：NetworkPolicy 與 `/health/*` 來源限制

`deployment-prerequisites` 第 1 項要求：探針來源可達且 Pod 能 Ready、維運來源可達、不可信 workload 與外部來源不可達。本 change 的做法與驗證邊界：

- manifest：api 的 ingress 只允許 `app=web` 的 Pod 連 8080（nginx 只轉發 `/api/`）；db 只允許 `app=api` 與 migrator Job 連 5432；redis 只允許 `app=api` 連 6379；seq 只允許 `app=api` 連 5341。kubelet 探針從節點發出，一般 CNI 不對節點到 Pod 的探針套用 NetworkPolicy；維運用 `kubectl port-forward` 走 API server → kubelet → 容器，同樣不經 NetworkPolicy。
- **可在本機驗證**（腳本斷言，不依賴 CNI）：api 沒有 NodePort／LoadBalancer；經 web 的 `/health/live`、`/health/ready`、`/api/health/*` 取不到 api 的健康檢查結果（回應本文不是 `Healthy`，且 `/health/live` 回的是 SPA `index.html`）；探針通過使 Pod Ready；`kubectl port-forward` 到 api 可取得 `/health/live`。
- **可能無法在本機驗證**：不可信 workload（同叢集的任意 Pod）連 api 被擋。腳本先做對照實驗：在暫存 Namespace 套用 deny-all ingress 的 NetworkPolicy，從另一 Pod 連它；若連得通，判定本機 CNI 沒有執行 NetworkPolicy，「不可信 workload 不可達」斷言輸出 `UNVERIFIED`（不算 PASS、不算 FAIL、摘要行單獨列出，腳本以特定結束碼區分），並由 `docs/todo/2026-11-10-networkpolicy-enforcement-todo.md` 承接。若對照實驗顯示 CNI 有執行，該斷言轉為正式 PASS／FAIL 判定。
- **task 1.1(c) 實測結論（2026-11-10）**：本機 CNI 不執行 NetworkPolicy。deny-all ingress 保護的 Pod 仍被另一 Pod 以 `redis-cli ping` 連通（回 `PONG`），因此「不可信 workload 不可達」斷言在本機固定為 `UNVERIFIED`（原文見 tasks.md 1.1）。
- 因此「完成前不得讓 api 暴露給不受信任的網路」在本 change 的範圍是：本機叢集不開放對外與不可信 workload 的信任假設只限單人本機；`deployment-prerequisites` 的上線 gate 在有可執行 NetworkPolicy 的環境驗證前**仍未解除**，本 change 不修改該文件的「必要條件」文字，只更新實測狀態。

替代方案：安裝 Calico 讓本機也能驗證。否決：改動 Docker Desktop 叢集的 CNI 風險高、超出本 change 範圍；列為待辦。

### 決策 8：替代驗證 `scripts/verify-k8s-manifests.sh`（使用者已於 2026-11-10 核准的例外）

manifest 沒有適合 xUnit 的行為，依 CLAUDE.md「Task 執行前」第 2 點記錄替代驗證例外：

- **適用範圍**：本 change 全部驗收項目（無 spec AC）。
- **偏離理由**：驗證對象是叢集中的資源狀態與跨 Pod 行為，沒有單元測試可觸及的邊界；Testcontainers 無法替代 kubeadm 叢集。
- **替代內容**：腳本只在 `kubectl config current-context` 為 `docker-desktop` 時執行（否則立即非 0 結束，避免對錯誤叢集動手）；使用獨立的 Namespace（`projectc-verify`，以 `sed`／`kubectl -n` 指定，不改 manifest 中的 `projectc`），機敏值於執行時隨機產生；不使用 `set -x`、不把機敏值寫入 repo；`trap` 清理 Namespace。斷言（任一 FAIL 即非 0 結束，印出失敗項目與實際輸出）：
  1. 全部 Deployment／Pod Ready，migrator Job `Complete`，`__EFMigrationsHistory` 筆數等於 `src/ProjectC.Infrastructure/Persistence/Migrations/` 內 `*.Designer.cs` 的檔案數；容許 api 重啟至多一次（restartCount ≤ 1），摘要行輸出實際值。
  2. 探針設定存在且路徑與 `/health/live`、`/health/ready` 完全相同；api 在 DB 不可用時 readiness 轉為 NotReady：以 `kubectl scale deployment/db --replicas=0` 停 db，輪詢 api Service 的 endpoints 變空，逾時 90 秒（readiness `periodSeconds: 10`、`timeoutSeconds: 15`、`failureThreshold: 3`，最長約 75 秒），再 scale 回 1，須在逾時內回到 Ready。
  3. 經 NodePort（腳本以變數 `VERIFY_NODEPORT`＝30081 覆寫 manifest 的 30080，並先斷言覆寫結果確為該值與 Namespace 為 `projectc-verify`；以下斷言 3、9、突變 e 一律使用此變數）的 `/` 回 200、`/api/events` 回 2xx 且非 3xx。
  4. 刪除一個 api Pod 後，期間對 `/api/events` 持續請求無非 2xx，且 Pod 在逾時內重建為 Ready。
  5. `kubectl rollout restart deployment/api` 期間，以背景迴圈持續請求 `/api/events`，非 2xx 次數必須為 0。
  6. 隨機產生的 Secret 值（含 JWT 簽章金鑰、DB 密碼）不出現在：api／migrator／db 的 `kubectl logs`、`kubectl describe` 輸出、以及 `k8s/*.yaml` 與 `git ls-files`；另斷言 Secret 的鍵名（`Jwt__SigningKey`、`TicketSigning__SigningKey`、`ConnectionStrings__DefaultConnection`、`POSTGRES_PASSWORD`、`DevelopmentSeed__AdminPassword`）不出現在 `k8s/10-configmap.yaml`（突變 c 由此斷言偵測）。
  7. 缺設定演練：以暫存 Deployment 複製 api 規格、僅從 Secret 注入來源移除 `Jwt__SigningKey`（其餘必填設定齊全），另做一份少了該鍵的 Secret 副本，暫存 Deployment 使用不同標籤（`app=api-missing-config`，不得帶 `app=api`，以免被 Service 選到而干擾斷言 4、5；NetworkPolicy 對它的涵蓋與否須在腳本註解寫明）。須進入 `CrashLoopBackOff`，記錄 `kubectl describe` 的結束碼與日誌（承接 `api-missing-config-exit-code-139` 待辦，不做根因分析，結論寫回該待辦）。
  8. `/health/*` 來源限制的可本機驗證部分（決策 7）；「不可信 workload 不可達」依對照實驗輸出 PASS／FAIL／`UNVERIFIED`。
  9. api 沒有 NodePort／LoadBalancer；只有 web Service 為 NodePort；api、web、migrator 的 Pod spec 含 `runAsNonRoot: true`；經 NodePort 取 `/swagger`、`/openapi/v1.json`、`/api/openapi/v1.json` 得不到 OpenAPI 文件（Development 暴露面）；判定看回應本文：nginx 的 `try_files` 會讓 `/swagger` 回 200 與 SPA `index.html`，故條件是本文既不是 OpenAPI JSON 也不是 SwaggerUI，不能只看狀態碼。

  腳本產生的隨機密碼與金鑰只用英數字（hex），避免 `;`、`=`、`$(` 破壞連線字串解析或 `$(VAR)` 展開；金鑰長度須通過 api 的啟動驗證（至少 32 字元）。
- **失敗情境可證明**：以手動暫時改動各做一次（`scripts/verify-test-evidence.sh mutate` 以 `dotnet test` 摘要行判定，不適用本腳本，不呼叫它），腳本須失敗並還原後重跑通過，原文記錄於 tasks.md。預期失敗斷言：(a) 拿掉 api 的 `readinessProbe` → 斷言 2；(b) api 副本改為 1 → 斷言 4；(c) 把 `Jwt__SigningKey` 放進 ConfigMap → 斷言 6 的 ConfigMap 鍵名檢查；(d) 讓 nginx 多一個 `location /health/` 轉發到 api → 斷言 8（須改 `deploy/nginx/default.conf` 並重建 `projectc/web:local`，還原後也要重建；`git status` 乾淨不能證明映像已還原，還原證據是還原並重建後斷言 8 全過）；(e) 把 api Service 改為 NodePort → 斷言 9。
- **RED 證據與時序豁免**：本例外豁免 RED 先於實作的時序（manifest 與腳本在同一 task 內先後撰寫，manifest 不存在時腳本本身只能因「資源不存在」而失敗）；RED 證據 = 流程性 RED（manifest 不存在時腳本的原文輸出）+ 突變 a–e 的行為 RED。比照 `production-images` 的既有做法；使用者於 2026-11-10 在規格審查 PASS 後核准此例外並指示進入實作。
- **解除條件**：日後 CI 能起叢集（kind／k3d）並執行同一支腳本，此例外改由 CI 取代。
- 另以 claude-in-chrome 對 NodePort 30080 做一次真實瀏覽器流程（Admin 建活動 → 一般會員登入 → 瀏覽 → 下單 → 付款），數字與狀態以頁面文字或 DB 核對，不只依截圖。

## Risks / Trade-offs

- [本機 CNI 大概率不執行 NetworkPolicy，「不可信 workload 不可達」無法驗證] → 決策 7 的對照實驗輸出 `UNVERIFIED` 並由待辦檔承接；`deployment-prerequisites` 的上線 gate 不因本 change 解除。
- [`Development` 環境啟用 SwaggerUI 與 seeder] → 僅限本機展示；api 無對外路徑；manifest 註解寫明不得原樣用於共享環境。
- [migrator 密碼出現在容器內行程參數] → 只有能進入該 Job 容器者可見；`kubectl describe` 與 manifest 不含密碼；日後若要消除，需改 migrator 讀環境變數（另開 change）。
- [Seq `SEQ_FIRSTRUN_NOAUTHENTICATION=True` 無驗證] → 只開 ClusterIP 且 NetworkPolicy 限 api 來源；Seq UI 只經 `port-forward`；僅限本機展示（`deployment-env-vars` 已註明）。
- [未設定 `X-Forwarded-*`／ForwardedHeaders，經 nginx 後 api 看到的來源 IP 都是 web Pod，依 IP 分區的限流會讓所有使用者共用額度，斷言 4、5 的持續請求與 3.3 流程可能誤觸 429（與 `production-images` 的既有取捨相同）] → 實作時先確認被驗證端點（`/api/events`）有無限流；若觸發，降低請求頻率並記錄，不在本 change 補 ForwardedHeaders。
- [Development 環境下每個 api Pod 啟動都會跑 `DevelopmentDataSeeder`（先查 Email 再新增，Email 有唯一索引）；2 副本同時首次啟動可能讓後寫入者拋唯一鍵例外而重啟一次（`Program.cs`、`DevelopmentDataSeeder.cs` 核對）] → 斷言 1 的 Ready 等待須容許 api 重啟一次，並記錄實際 restartCount；若實測穩定發生，改為先 1 副本 Ready 再擴為 2。
- [Docker Desktop 與 `docker build` 共用 image store 為假設] → 決策 2 / task 1.1 實測；失敗則 `imagePullPolicy: Never` 或本機 registry。
- [`/health/ready` 逾時 10～12 秒（DNS 無回應）] → readiness `timeoutSeconds: 15`；斷言 2 暫停 db 驗證 NotReady 路徑。
- [hostpath PVC 在 Docker Desktop 重設叢集後消失] → 學習用途可接受，文件註明。
- [NodePort 30080 可能落入 Windows 排除範圍] → task 1.1 先以 `netsh int ipv4 show excludedportrange protocol=tcp` 確認，衝突則改埠並同步更新本文。
- [滾動更新零中斷的數值（preStop、grace period）未實測] → 斷言 5 以實測承擔，不達標才調整，不憑感覺加大。
- [腳本使用獨立 Namespace `projectc-verify`，與使用者手動部署的 `projectc` 並存時 NodePort 30080 會衝突] → 腳本用另一個 NodePort（30081）並在 manifest 以 `sed` 覆寫；使用者手動部署與腳本不同時執行並於文件註明。

## Migration Plan

無資料遷移。新增檔案不影響既有 compose 開發流程；回滾 = `kubectl delete namespace projectc` 並刪除 `k8s/` 與新增的腳本、文件。

## Open Questions

- 無（映像可見性與 NodePort 排除範圍為實作第一步的實測項目，見決策 2 與 Risks，不影響範圍）。
