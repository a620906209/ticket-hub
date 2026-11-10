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
> | K10：完整購票流程可在叢集上完成（驗證時 captcha 仍開啟、由使用者手動填驗證碼，captcha 流程本身未由本 change 驗證；無法以 ConfigMap 關閉，程式無此開關） | 3.3 真實瀏覽器 |
>
> TDD：manifest 沒有 xUnit 可觸及的行為，依 design 決策 8 的替代驗證例外（使用者已於 2026-11-10 核准），先寫腳本（`k8s/` 不存在時預期失敗），再補 manifest 使腳本通過。證據一律貼原文（CLAUDE.md「證據貼原文」）：每個失敗斷言的名稱與輸出、腳本摘要行、突變命令與輸出、`git stash`／`restore` 指令輸出。hardener：N/A（無 I/O 程式碼、Handler 或 Repository 變更）。

## 1. 前置實測與驗證腳本

- [x] 1.1 實測前置假設並記錄：(a) 起一個使用 `projectc/api:local` 的 Pod，確認 `imagePullPolicy: IfNotPresent` 能直接取得本機映像（失敗則改 `Never` 並更新 design 決策 2）；(b) `netsh int ipv4 show excludedportrange protocol=tcp` 確認 30080／30081 不在排除範圍（衝突則改埠並同步 proposal／design）；(c) `kubectl get pods -A` 與對照實驗（deny-all NetworkPolicy）確認本機 CNI 是否執行 NetworkPolicy。驗證：三項結果原文貼在完成紀錄，並把 (c) 結論寫回 design 決策 7。
  - 完成紀錄（2026-11-10，context `docker-desktop`；三個映像先以 `docker build` 建為 `projectc/api:local`、`projectc/api-migrator:local`、`projectc/web:local`）：
    - (a) 結論：`IfNotPresent` 可直接使用本機映像，決策 2 不需改。`kubectl -n img-probe run apiprobe --image=projectc/api:local --image-pull-policy=IfNotPresent --restart=Never --command -- sleep 30` 輸出：
      ```
      pod/apiprobe condition met
      apiprobe   1/1     Running   0          1s    10.1.0.77   docker-desktop
      Normal  Pulled     0s    kubelet   spec.containers{apiprobe}: Container image "projectc/api:local" already present on machine and can be accessed by the pod
      ```
    - (b) 結論：30080／30081 不在排除範圍。`netsh int ipv4 show excludedportrange protocol=tcp` 輸出：
      ```
      開始連接埠    結束連接埠
            5000        5000
            5357        5357
            5426        5426
           29999       29999
           50000       50059     *
      ```
    - (c) 結論：本機 CNI **不執行** NetworkPolicy。`kubectl get pods -A`（kube-system 僅 coredns×2、etcd、apiserver、controller-manager、kube-proxy、scheduler、storage-provisioner、vpnkit-controller，全部 Running，無 Calico／Cilium）；對照實驗：Namespace `np-probe` 內 Pod `srv`（redis:7-alpine）套用 deny-all ingress NetworkPolicy（`podSelector app=srv`、`policyTypes: [Ingress]`、無 ingress 規則），另一 Pod 執行 `redis-cli -h 10.1.0.75 ping` 輸出 `PONG`（exit=0）。被 deny-all 保護的 Pod 仍可連通 → 斷言 8 的「不可信 workload 不可達」將輸出 `UNVERIFIED`，4.2 須新增待辦檔。
- [x] 1.2 先寫 `scripts/verify-k8s-manifests.sh`，實作 design 決策 8 的斷言 1～9（含 context 守衛、`projectc-verify` Namespace、隨機 Secret、`trap` 清理、`UNVERIFIED` 專用結束碼與摘要行）。**RED**：在 `k8s/` 尚不存在時執行，預期因資源不存在而非 0 結束（記錄命令與原文輸出；此為流程性 RED，不當作行為 RED）。驗證：腳本對不存在的 manifest 非 0 結束，且不留下殘餘資源。
  - 完成紀錄（流程性 RED，`k8s/` 不存在，`ls k8s` → `ls: cannot access 'k8s': No such file or directory`；`bash -n` → `syntax-ok`）：命令 `bash scripts/verify-k8s-manifests.sh; echo "exit=$?"; kubectl get ns --no-headers | grep -c projectc-verify`，原文輸出：
    ```
    缺少 k8s/00-namespace.yaml
    缺少 k8s/10-configmap.yaml
    缺少 k8s/20-postgres.yaml
    缺少 k8s/21-redis.yaml
    缺少 k8s/22-seq.yaml
    缺少 k8s/30-migrator-job.yaml
    缺少 k8s/40-api.yaml
    缺少 k8s/50-web.yaml
    缺少 k8s/60-networkpolicy.yaml
    缺少 k8s/secret.example.yaml
    FAIL [0] manifest 不完整，無法驗證（未對叢集做任何操作）
    SUMMARY: 0 passed, 1 failed, 0 unverified
    exit=1
    0
    ```
    結束碼 1，且 `projectc-verify` Namespace 殘留數為 0（腳本在動叢集前即失敗）。腳本結束碼約定：0 全 PASS、1 有 FAIL、2 前置條件不符、3 無 FAIL 但有 UNVERIFIED。此為流程性 RED；行為 RED 為 3.2 突變 a–e。斷言 1～9 的實際行為在 2.x 與 3.1 驗證。

## 2. Manifest

- [x] 2.1 新增 `k8s/00-namespace.yaml`、`k8s/10-configmap.yaml`、`k8s/secret.example.yaml`，並在 `.gitignore` 加入 `k8s/secret.yaml`。驗證：`kubectl apply --dry-run=server -f k8s/` 無錯；`git check-ignore k8s/secret.yaml` 命中；範本只含佔位值。
  - 完成紀錄：`git check-ignore -v k8s/secret.yaml` → `.gitignore:505:k8s/secret.yaml	k8s/secret.yaml`。`secret.example.yaml` 只含 `REPLACE_ME_*` 佔位值。`--dry-run=server -f k8s/` 無法整個目錄執行：Namespace 在 server dry-run 不會真的建立，其餘資源會報 `namespaces "projectc" not found`（`Error from server (NotFound): error when creating "k8s/10-configmap.yaml": namespaces "projectc" not found`），這是 dry-run 對「同批建立 Namespace」的已知限制，不是 manifest 錯誤；改為先真實建立渲染後的 Namespace（`projectc-verify`），再對全部渲染後的檔案逐一 `--dry-run=server`，輸出（無錯）：
    ```
    namespace/projectc-verify created
    configmap/projectc-config created (server dry run)
    secret/projectc-secrets created (server dry run)
    persistentvolumeclaim/db-data created (server dry run)
    deployment.apps/db created (server dry run)
    service/db created (server dry run)
    deployment.apps/redis created (server dry run)
    service/redis created (server dry run)
    deployment.apps/seq created (server dry run)
    service/seq created (server dry run)
    job.batch/migrator created (server dry run)
    deployment.apps/api created (server dry run)
    service/api created (server dry run)
    deployment.apps/web created (server dry run)
    service/web created (server dry run)
    networkpolicy.networking.k8s.io/api-ingress created (server dry run)
    networkpolicy.networking.k8s.io/db-ingress created (server dry run)
    networkpolicy.networking.k8s.io/redis-ingress created (server dry run)
    networkpolicy.networking.k8s.io/seq-ingress created (server dry run)
    namespace "projectc-verify" deleted
    ```
    另 `kubectl apply --dry-run=client -f k8s/` 對整個目錄無錯（含 `secret/projectc-secrets created (dry run)`）。
- [x] 2.2 新增 `k8s/20-postgres.yaml`（PVC＋Deployment `Recreate`＋Service `db`）、`k8s/21-redis.yaml`、`k8s/22-seq.yaml`（含資源 requests／limits）。驗證：腳本斷言 1 的 db／redis／seq 部分通過。
  - 完成紀錄：`bash scripts/verify-k8s-manifests.sh` 第一次執行因腳本缺陷（Windows 原生 kubectl 讀不到 MSYS `/tmp` 路徑，`--from-env-file` 失敗）輸出 `FAIL [1] 建立 Secret 失敗`、結束碼 1；修正腳本（以 `cygpath -m` 轉路徑，與 `verify-production-images.sh` 同做法）後重跑，完整輸出見 3.1；斷言 1：`PASS [1] db／redis／seq／api／web 就緒（套用 NetworkPolicy 後仍就緒），migrator Complete，migration 筆數 16（預期 16），api 最大 restartCount=1`（腳本內 db／redis／seq 的 `rollout status` 先於 migrator 套用，任一未就緒會使斷言 1 失敗）。
- [x] 2.3 新增 `k8s/30-migrator-job.yaml`（`args` 用 `$(ConnectionStrings__DefaultConnection)`、`backoffLimit: 2`）。驗證：腳本斷言 1（Job Complete、migration 筆數）與斷言 6（describe 不含密碼）通過。
  - 完成紀錄：同一次執行（見 3.1）：斷言 1 含 `migrator Complete，migration 筆數 16（預期 16）`；`PASS [6] 5 個隨機機敏值未出現在 logs、describe、k8s/、版控；Secret 鍵名不在 ConfigMap`（describe 輸出涵蓋 pods、deployments、jobs、services、configmaps，含 migrator Job 與其 Pod）。
- [x] 2.4 新增 `k8s/40-api.yaml`（2 副本、`RollingUpdate maxUnavailable 0 / maxSurge 1`、三種探針、`preStop`、資源）。驗證：腳本斷言 1、2、4、5、7 通過。
  - 完成紀錄：資源初值（api requests 100m／192Mi、limits 500m／512Mi；web limit 64Mi；db limit 512Mi；redis limit 128Mi；seq limit 1Gi）經 3.1／3.3 實跑後不需調整。叢集無 metrics-server（`kubectl top pods` 輸出 `error: Metrics API not available`），改用 `docker stats --no-stream` 於 3.3 購票流程後取樣：`k8s_api_...nnd55 127.4MiB / 512MiB 0.67%`、`k8s_api_...27l4r 127.9MiB / 512MiB 0.33%`、`k8s_db 30.35MiB / 512MiB`、`k8s_web 16.09MiB / 64MiB`、`k8s_seq 97.12MiB / 1GiB`、`k8s_redis 3.941MiB / 128MiB`。所有 Pod `restartCount` 為 0，無任何容器 `lastState.terminated.reason=OOMKilled`（jq 逐一輸出 `oom=false`）。限制：流量僅為單人手動購票流程加 3.1 的小量請求，非壓測，不代表尖峰記憶體；api 常駐約 128Mi 低於 192Mi request 之上、遠低於 512Mi limit。2.4 的驗證斷言 1、2、4、5、7 見 3.1 完整輸出。
- [x] 2.5 新增 `k8s/50-web.yaml`（NodePort 30080、readiness 打 `/`、資源）。驗證：腳本斷言 3、8（web 不轉發 `/health/*`）、9 通過。
  - 完成紀錄：見 3.1 完整輸出：`PASS [3a] GET http://localhost:30081/ → 200`、`PASS [3b] GET /api/events → 200（2xx，非 3xx）`、`PASS [8a] 經 web 取不到 /health/live、/health/ready、/api/health/*（回應不是 Healthy）`、`PASS [9] api 為 ClusterIP、僅 web 為 NodePort（30081）、api／web／migrator 皆 runAsNonRoot、OpenAPI／SwaggerUI 不可經 web 取得`（腳本以 30081 覆寫 manifest 的 30080；manifest 本身維持 30080，1.1(b) 已確認兩者皆不在排除範圍）。
- [x] 2.6 新增 `k8s/60-networkpolicy.yaml`（api 只收 web、db 只收 api 與 migrator、redis 與 seq 只收 api）。驗證：腳本斷言 1 在套用後仍通過（探針與 Pod Ready 不受影響）；斷言 8 的對照實驗結果依 1.1(c) 輸出。
  - 完成紀錄：腳本先套用 NetworkPolicy 之後才做 Ready 與 migration 筆數檢查，`PASS [1]` 即「套用後仍通過」；斷言 2、4、5 也在 NetworkPolicy 已套用下通過。斷言 8 對照實驗依 1.1(c) 輸出 `UNVERIFIED [8c] 本機 CNI 不執行 NetworkPolicy（deny-all 保護的 Pod 仍可連通），無法驗證不可信 workload 不可達；見 docs/todo/2026-11-10-networkpolicy-enforcement-todo.md`（完整輸出見 3.1）。「CNI 有執行」分支的實測（不可信 Pod 連不到 api／redis／db、app=web 連得到 api）本機無法執行，該分支程式碼僅經語法檢查，未實測。

## 3. 驗證

- [x] 3.1 執行 `bash scripts/verify-k8s-manifests.sh` 全套。驗證：摘要行為 `0 failed`（`UNVERIFIED` 若有須單獨列出且與待辦檔一致）。
  - 完成紀錄：命令 `bash scripts/verify-k8s-manifests.sh`（全部 manifest 完成後的第一次完整成功執行；`echo "exit=$?"` 為結束碼）。完整輸出原文：
    ```
    PASS [0] manifest 覆寫為 Namespace projectc-verify、NodePort 30081
    PASS [1] db／redis／seq／api／web 就緒（套用 NetworkPolicy 後仍就緒），migrator Complete，migration 筆數 16（預期 16），api 最大 restartCount=1
    PASS [2a] 探針路徑：liveness=/health/live readiness=/health/ready startup=/health/live
    PASS [2b] db 停止後 api Service endpoints 在 90 秒內清空（readiness 轉為 NotReady）
    PASS [2c] db 恢復後 api endpoints 在 150 秒內回到 Ready
    PASS [3a] GET http://localhost:30081/ → 200
    PASS [3b] GET /api/events → 200（2xx，非 3xx）
    PASS [4] 刪除 Pod api-57769dcd57-jj6xm 期間 23 次請求全為 2xx，Deployment 回到 2 個 Ready
    PASS [5] rollout restart 期間 44 次請求全為 2xx
    PASS [6] 5 個隨機機敏值未出現在 logs、describe、k8s/、版控；Secret 鍵名不在 ConfigMap
    PASS [7] 缺 Jwt__SigningKey 時 api 進入 CrashLoopBackOff，上次結束碼=139
    ---- 斷言 7 日誌（最後 20 行）----
       at Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(IHost host, CancellationToken token)
       at Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.Run(IHost host)
       at Program.<Main>$(String[] args) in /src/src/ProjectC.WebApi/Program.cs:line 117
    Unhandled exception. Microsoft.Extensions.Options.OptionsValidationException: DataAnnotation validation failed for 'JwtOptions' members: 'SigningKey' with the error: 'The SigningKey field is required.'.
       at Microsoft.Extensions.Options.OptionsFactory`1.Create(String name)
       at System.Lazy`1.ViaFactory(LazyThreadSafetyMode mode)
       at System.Lazy`1.ExecutionAndPublication(LazyHelper executionAndPublication, Boolean useDefaultConstructor)
       at System.Lazy`1.CreateValue()
       at Microsoft.Extensions.Options.OptionsCache`1.GetOrAdd[TArg](String name, Func`3 createOptions, TArg factoryArgument)
       at Microsoft.Extensions.DependencyInjection.OptionsBuilderExtensions.<>c__DisplayClass0_1`1.<ValidateOnStart>b__1()
       at Microsoft.Extensions.Options.StartupValidator.Validate()
    --- End of stack trace from previous location ---
       at Microsoft.Extensions.Options.StartupValidator.Validate()
       at Microsoft.Extensions.Hosting.Internal.Host.StartAsync(CancellationToken cancellationToken)
       at Microsoft.Extensions.Hosting.Internal.Host.StartAsync(CancellationToken cancellationToken)
       at Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(IHost host, CancellationToken token)
       at Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(IHost host, CancellationToken token)
       at Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.Run(IHost host)
       at Program.<Main>$(String[] args) in /src/src/ProjectC.WebApi/Program.cs:line 117
       at Program.<Main>(String[] args)
    ----------------------------------
    PASS [8a] 經 web 取不到 /health/live、/health/ready、/api/health/*（回應不是 Healthy）
    PASS [8b] kubectl port-forward 到 api 可取得 /health/live（維運來源可達）
    UNVERIFIED [8c] 本機 CNI 不執行 NetworkPolicy（deny-all 保護的 Pod 仍可連通），無法驗證不可信 workload 不可達；見 docs/todo/2026-11-10-networkpolicy-enforcement-todo.md
    PASS [9] api 為 ClusterIP、僅 web 為 NodePort（30081）、api／web／migrator 皆 runAsNonRoot、OpenAPI／SwaggerUI 不可經 web 取得
    SUMMARY: 14 passed, 0 failed, 1 unverified
    UNVERIFIED: 8c
    exit=3
    ```
    摘要行 `SUMMARY: 14 passed, 0 failed, 1 unverified`、`UNVERIFIED: 8c`（結束碼 3 = 無 FAIL 但有 UNVERIFIED，符合預期），與 4.2 待辦檔一致。執行後 `kubectl get ns | grep -c projectc` → `0`（trap 清理，無殘餘 Namespace）。觀察：斷言 1 的 api `restartCount=1`（容許上限內，與 design Risks 的 seeder 唯一鍵競態一致，未進一步分析根因）；斷言 4／5 的請求數 23／44（≥20 下限），未觸發 429。
- [x] 3.2 突變驗證（手動改動、還原、貼原文，不呼叫 `verify-test-evidence.sh mutate`）：a 拿掉 api `readinessProbe`（預期斷言 2 失敗）；b api 副本改 1（斷言 4）；c 把 `Jwt__SigningKey` 放進 ConfigMap（斷言 6）；d nginx 多 `location /health/` 轉發（斷言 8；須重建 `projectc/web:local`，還原後再重建）；e api Service 改 NodePort（斷言 9）。腳本須因對應斷言失敗，還原後重跑通過。驗證：每個突變的失敗斷言名稱與實際輸出、還原後摘要行；`git status` 於還原後為乾淨。
  - 完成紀錄：驅動腳本 /tmp/mutate.sh 對每個突變：改動 → `bash scripts/verify-k8s-manifests.sh` → 還原 → 與改動前快照 diff（`RESTORE-DIFF: identical`）；未使用 `verify-test-evidence.sh mutate`。以下為各突變的 diff 與腳本 FAIL／SUMMARY 原文（腳本結束碼皆為 `exit=1`）。
    - 突變 a：拿掉 api readinessProbe（預期 斷言 2 失敗）。diff 與輸出原文：
      ```
      == mutation a diff ==
      54,60d53
      <           readinessProbe:
      <             httpGet:
      <               path: /health/ready
      <               port: http
      <             periodSeconds: 10
      <             timeoutSeconds: 15
      <             failureThreshold: 3
      FAIL [2a] 探針路徑不符：liveness=/health/live readiness=none startup=/health/live（預期 /health/live、/health/ready、/health/live）
      FAIL [2b] db 停止後 90 秒內 api endpoints 仍非空
      FAIL [3b] GET /api/events → 500（預期 2xx）
      FAIL [4] recovered=1，請求總數=24（至少 20），非 2xx=2
      SUMMARY: 10 passed, 4 failed, 1 unverified
      exit=1
      RESTORE-DIFF: identical
      ```
    - 突變 b：api replicas 2→1（預期 斷言 4 失敗）。diff 與輸出原文：
      ```
      == mutation b diff ==
      11c11
      <   replicas: 2
      ---
      >   replicas: 1
      FAIL [4] recovered=0，請求總數=664（至少 20），非 2xx=1
      SUMMARY: 13 passed, 1 failed, 1 unverified
      exit=1
      RESTORE-DIFF: identical
      ```
    - 突變 c：Jwt__SigningKey 放進 ConfigMap（預期 斷言 6 失敗）。diff 與輸出原文：
      ```
      == mutation c diff ==
      22a23
      >   Jwt__SigningKey: "mutation-value-in-configmap"
      FAIL [6] Secret 鍵名 Jwt__SigningKey 出現在 k8s/10-configmap.yaml; Secret 鍵名 Jwt__SigningKey 出現在叢集的 ConfigMap;
      SUMMARY: 13 passed, 1 failed, 1 unverified
      exit=1
      RESTORE-DIFF: identical
      ```
    - 突變 e：api Service 改 NodePort（預期 斷言 9 失敗）。diff 與輸出原文：
      ```
      == mutation e diff ==
      85c85
      <   type: ClusterIP
      ---
      >   type: NodePort
      FAIL [9] api Service 不是 ClusterIP; NodePort／LoadBalancer Service 數量不是 1;
      SUMMARY: 13 passed, 1 failed, 1 unverified
      exit=1
      RESTORE-DIFF: identical
      ```
    - 突變 d：nginx 多 location /health/ 轉發（重建 projectc/web:local）（預期 斷言 8 失敗）。diff 與輸出原文：
      ```
      == mutation d diff ==
      1,25c1,29
      < server {
      <     listen 8080;
      <     server_name _;
      <     root /usr/share/nginx/html;
      <
      <     # 上游主機名 `api` 沿用 compose／日後 K8s Service 名稱；nginx 啟動時解析不到會直接退出。
      <     # client_max_body_size 取 10m：等於 API 單端點最大上限 RequestBodyLimits.CreateSeatMapBytes
      <     # （10,485,760 bytes）；nginx 預設 1m 會以 413 擋掉座位圖建立。實際的 1 MiB 全域與各端點上限仍由 API 負責。
      <     location /api/ {
      <         client_max_body_size 10m;
      <         proxy_pass http://api:8080;
      <         proxy_set_header Host $host;
      <     }
      <
      <     # Vite 產出的檔名帶 hash，可長期快取。
      <     location /assets/ {
      <         add_header Cache-Control "public, max-age=31536000, immutable";
      <         try_files $uri =404;
      <     }
      <
      <     location / {
      <         try_files $uri /index.html;
      <         add_header Cache-Control "no-cache";
      <     }
      < }
      ---
      > server {
      >     listen 8080;
      >     server_name _;
      >     root /usr/share/nginx/html;
      >
      >     # 上游主機名 `api` 沿用 compose／日後 K8s Service 名稱；nginx 啟動時解析不到會直接退出。
      >     # client_max_body_size 取 10m：等於 API 單端點最大上限 RequestBodyLimits.CreateSeatMapBytes
      >     # （10,485,760 bytes）；nginx 預設 1m 會以 413 擋掉座位圖建立。實際的 1 MiB 全域與各端點上限仍由 API 負責。
      >     location /api/ {
      >         client_max_body_size 10m;
      >         proxy_pass http://api:8080;
      >         proxy_set_header Host $host;
      >     }
      >
      >     # Vite 產出的檔名帶 hash，可長期快取。
      >     location /assets/ {
      >         add_header Cache-Control "public, max-age=31536000, immutable";
      >         try_files $uri =404;
      >     }
      >
      >     location /health/ {
      >         proxy_pass http://api:8080;
      >     }
      >
      >     location / {
      >         try_files $uri /index.html;
      >         add_header Cache-Control "no-cache";
      >     }
      > }
      build-exit=0
      FAIL [8a] /health/live 回應為 api 健康檢查結果; /health/ready 回應為 api 健康檢查結果; /health/live 回應不是 SPA index.html（預期 nginx try_files 回 index.html）;
      SUMMARY: 13 passed, 1 failed, 1 unverified
      exit=1
      RESTORE-DIFF: identical
      ```
    - 全部還原後完整重跑（/tmp/mut-final.log 原文，含 web 映像已重建；`rebuild-exit=0`）：
      ```
      PASS [0] manifest 覆寫為 Namespace projectc-verify、NodePort 30081
      PASS [1] db／redis／seq／api／web 就緒（套用 NetworkPolicy 後仍就緒），migrator Complete，migration 筆數 16（預期 16），api 最大 restartCount=1
      PASS [2a] 探針路徑：liveness=/health/live readiness=/health/ready startup=/health/live
      PASS [2b] db 停止後 api Service endpoints 在 90 秒內清空（readiness 轉為 NotReady）
      PASS [2c] db 恢復後 api endpoints 在 150 秒內回到 Ready
      PASS [3a] GET http://localhost:30081/ → 200
      PASS [3b] GET /api/events → 200（2xx，非 3xx）
      PASS [4] 刪除 Pod api-57769dcd57-ghtxh 期間 23 次請求全為 2xx，Deployment 回到 2 個 Ready
      PASS [5] rollout restart 期間 41 次請求全為 2xx
      PASS [6] 5 個隨機機敏值未出現在 logs、describe、k8s/、版控；Secret 鍵名不在 ConfigMap
      PASS [7] 缺 Jwt__SigningKey 時 api 進入 CrashLoopBackOff，上次結束碼=139
      PASS [8a] 經 web 取不到 /health/live、/health/ready、/api/health/*（回應不是 Healthy）
      PASS [8b] kubectl port-forward 到 api 可取得 /health/live（維運來源可達）
      UNVERIFIED [8c] 本機 CNI 不執行 NetworkPolicy（deny-all 保護的 Pod 仍可連通），無法驗證不可信 workload 不可達；見 docs/todo/2026-11-10-networkpolicy-enforcement-todo.md
      PASS [9] api 為 ClusterIP、僅 web 為 NodePort（30081）、api／web／migrator 皆 runAsNonRoot、OpenAPI／SwaggerUI 不可經 web 取得
      SUMMARY: 14 passed, 0 failed, 1 unverified
      UNVERIFIED: 8c
      exit=3
      ```
    - 限制（須揭露）：(1) `k8s/` 為未追蹤目錄，`git status` 無法顯示還原乾淨，改以還原前快照 diff `identical` 為證（nginx 設定為已追蹤檔，還原後 `git status` 不含 `deploy/nginx/default.conf`，見上方狀態無該檔）；(2) 還原後只做一次最終完整重跑，非每個突變各一次；(3) 突變 a 同時觸發 2a、2b、3b、4 失敗（含預期的斷言 2）。
- [x] 3.3 以 claude-in-chrome 對 `http://localhost:30080`（或 1.1 確認後的埠）做真實瀏覽器流程：以 seeder Admin 登入、建立活動與票種、改以一般會員登入、瀏覽、下單、付款。驗證：頁面文字與 DB（`kubectl exec` 進 db 查詢）核對訂單狀態，不只依截圖；結果與觀察到的異常貼在完成紀錄。
  - 完成紀錄（2026-11-10，`KEEP_UP=1 bash scripts/verify-k8s-manifests.sh` 後 `SUMMARY: 14 passed, 0 failed, 1 unverified`；瀏覽器 `http://localhost:30081`）：
    - captcha 說明（須揭露）：登入與註冊有驗證碼。原計畫以 ConfigMap 暫時關閉，但 appsettings.json 的 `Captcha` 只有 `TtlSeconds`，Login／Register／JoinPurchaseQueue handler 皆無條件驗證，沒有可關閉的開關；使用者選擇手動填驗證碼（我未讀 Redis 取答案、未改程式）。因此**驗證時 captcha 未關閉，由使用者手動通過；captcha 流程本身未在叢集驗證**（僅證明叢集上可通過）。下單與付款未出現驗證碼。
    - 流程與頁面文字：seeder Admin（admin@local.dev，密碼取自叢集 Secret）登入（使用者填驗證碼）→ 切換 Demo Organizer（已核准）→ 建立場館「K8s驗證場館」（DB：1 筆 Venues、20 筆 Seats，座位圖 A 區 1–20）→ 建立活動「K8s驗證演唱會」（2026/11/30）→ 建立票種 A（NT$100、有座位）→ 註冊並登入一般會員 buyer@local.dev（顯示名稱 K8s Buyer，使用者填驗證碼；DB `Members` 兩列：admin@local.dev Role=1、buyer@local.dev Role=0）→ 活動列表頁文字「K8s驗證演唱會 2026/11/30 上午12:00:00 販售中」→ 活動頁選 A-1、A-2，頁面文字「已選 2 個座位、0 張計數票券，總金額 NT$200」→ 確認訂單（勾選注意事項）→ 訂單明細頁文字「狀態： 待付款 保留至 2026/11/10 下午8:59:23」，DB `Orders.Status=0`、`OrderItems` 2 筆 → 按「確認付款」→ 頁面文字「狀態： 已付款」、兩個項目皆「票券狀態： 已出票」，Ticket ID `e236dfc8-9f95-4a12-be7c-7d9249e9f46c`、`0ca426ce-dc4c-4404-b9e8-b59c096d1996`。
    - DB 核對（`kubectl exec deploy/db -- psql`）：付款後 `Orders.Status` = `1`、`tickets` = `2`、`order_items` = `2`，與頁面一致。
    - DB 原始輸出（`kubectl exec deploy/db -- psql -U projectc -d projectc`，依序為建立場館／座位圖後、下單後、付款後）：
      ```
       count
      -------
           1
      (1 row)
       count
      -------
          20
      (1 row)
       Id                                   | EventId                              | HeldUntilUtc                  | Status | BuyerId                              | CreatedAtUtc
      --------------------------------------+--------------------------------------+-------------------------------+--------+--------------------------------------+-------------------------------
       c96b8365-44a7-4c6e-9587-6e7b9fcfeee9 | aa42041f-1f40-425b-9906-aca22f99a615 | 2026-11-10 12:59:23.102961+00 |      0 | 9ee43f1c-f52a-4889-adc9-56ce1c8af958 | 2026-11-10 12:49:23.102961+00
       order_items: 2
       Status: 1   tickets: 2   order_items: 2
      ```
    - 異常：無。api 在整個流程中 restartCount 0。

## 4. 文件與待辦

- [x] 4.1 更新 `docs/todo/2026-10-28-k8s-todo.md`（進度）、`docs/todo/2026-11-02-deployment-prerequisites.md` 第 1 項（寫明本機實測狀態，不改必要條件文字）、`docs/todo/2026-11-10-deployment-env-vars.md`（migrator 密碼改以 `$(VAR)` 引用）、`docs/todo/2026-11-10-api-missing-config-exit-code-139.md`（寫入斷言 7 的 K8s 實測結果）。驗證：`bash scripts/check-doc-paths.sh` 通過；各文件的路徑引用有效。
  - 完成紀錄：已更新四份文件（`git diff --stat -- docs`：`2026-10-28-k8s-todo.md`、`2026-11-02-deployment-prerequisites.md`、`2026-11-10-api-missing-config-exit-code-139.md`、`2026-11-10-deployment-env-vars.md`）。`bash scripts/check-doc-paths.sh` 輸出 `OK: 所有 docs 路徑引用都存在`，結束碼 0。
- [x] 4.2 新增 `docs/todo/2026-11-10-networkpolicy-enforcement-todo.md`，記錄「不可信 workload 不可達」在本機未驗證的原因、需要的環境（可執行 NetworkPolicy 的 CNI）、驗證步驟與解除條件；若 1.1(c) 顯示 CNI 有執行，改記為已驗證並刪除此待辦。驗證：文件存在，且與腳本輸出的 `UNVERIFIED` 狀態一致。
  - 完成紀錄：已新增 `docs/todo/2026-11-10-networkpolicy-enforcement-todo.md`；1.1(c) 實測本機 CNI 不執行 NetworkPolicy，與腳本輸出 `UNVERIFIED [8c]` 一致（見 3.1／3.2 最終重跑）；`check-doc-paths.sh` 通過（見 4.1）。
- [x] 4.3 hardener 最終檢查：N/A（無 I/O 程式碼變更）。驗證：此項記錄 N/A 理由即可。
  - 完成紀錄：N/A。本 change 只新增 YAML manifest、bash 驗證腳本與文件，沒有 Handler、Repository、UnitOfWork 或其他 I/O／資源管理的應用程式碼變更，不適用 hardener 清單。

## Workflow follow-up

- 實作 pane 先跑 `strict-reviewer` 至 PASS → 外部 reviewer → 外部 PASS 後再跑 `strict-reviewer` 與全新的對抗式 subagent（見 CLAUDE.md 流程第 6 步）。
- 無前端程式碼變更，但 3.3 為真實瀏覽器驗證。
- 審查通過後 `/opsx:archive`（`skip_specs`，無主 spec 同步）→ merge 回 master；歸檔後詢問是否以 `bash scripts/archive-todo.sh` 搬移已完成的待辦。

## 交叉審查紀錄

- 第 1 輪：Devin CLI（smart 模式），審 commit `158a509`，結果 FAIL，B-001：`scripts/verify-k8s-manifests.sh` 開頭與 EXIT trap 無條件刪除固定名稱的 `projectc-verify`／`projectc-verify-np`，同名既有 Namespace 會被誤刪。驗證成立。
  - 修正：兩個 Namespace 以 label `verify-k8s-manifests/owned=true` 標記所有權；啟動時（trap 設定前）若 Namespace 已存在且缺此 label 則 `exit 2`；`cleanup()` 只刪帶 label 者。同輪處理 warning：移除本檔 12 行行尾空白（`git diff --check master...HEAD` 乾淨）。
  - 驗證（docker-desktop）：重跑 `bash scripts/verify-k8s-manifests.sh` → `SUMMARY: 14 passed, 0 failed, 1 unverified`、`UNVERIFIED: 8c`、`exit=3`。守衛：預先建立無 label 的 `projectc-verify`（含 ConfigMap `foreign`）後執行 → `PRECONDITION: Namespace projectc-verify 已存在且不是本腳本建立（缺 label verify-k8s-manifests/owned=true），拒絕執行以免刪除既有資源；請改名或自行處理。`、`exit=2`；`foreign` ConfigMap 仍在（`foreign   1      2s`）；手動清掉後叢集無殘留。
- 第 2 輪：Devin CLI（smart 模式），審 commit `af52f50`，結果 FAIL。B-002：`k label` 結束碼未檢查，標記失敗時 Namespace 殘留（成立）。B-003：守衛後 `kubectl apply` 建立固定名稱 Namespace，競態下可能接管他人 Namespace 並於清理時刪除（成立；窗口很窄，但修法簡單）。
  - 修正：Namespace 改用 `kubectl create`（已存在即失敗，不接管）；清理只依「本次 create 成功」旗標（`ns_created`／`np_ns_created`），不依事後 label；`create`／`label` 結束碼皆檢查，失敗即 FAIL 並結束（EXIT trap 仍清掉已建立者）；啟動時清殘留也只刪帶 label 者。
  - 驗證（docker-desktop）：完整重跑 → `SUMMARY: 14 passed, 0 failed, 1 unverified`、`UNVERIFIED: 8c`、`exit=3`，結束後無殘留。以 PATH shim 讓 `kubectl label` 失敗 → `FAIL [1] 標記 Namespace projectc-verify 失敗`、輸出 `forbidden: cannot patch namespace`、`SUMMARY: 1 passed, 1 failed, 0 unverified`、`exit=1`，結束後無殘留。無 label 的既有 `projectc-verify` → `PRECONDITION: …` `exit=2`，`foreign` ConfigMap 仍在。帶 label 的殘留 Namespace → 下一輪重跑 `14 passed, 0 failed, 1 unverified`、`exit=3` 並清除。
  - 限制：守衛通過後、`create` 之前的競態窗口無法在本機穩定重現；其保護依賴 `kubectl create` 對既有 Namespace 回 AlreadyExists 的語意，未做競態實測。
- 第 3 輪：Devin CLI（smart 模式），審 commit `195ffab`，結果 FAIL；前輪 B-002、B-003 確認已解決。新 B-001：固定 Namespace 與 owner label 讓並行的第二個執行個體通過守衛並把第一個的 Namespace 當殘留刪除；且 Namespace 若在執行期間被換成同名資源，旗標式清理會依名稱誤刪。判斷：並行本就不被支援（固定 Namespace／NodePort 30081），但防護成本低，採納。
  - 修正：以 `mkdir` 互斥鎖（`$TMPDIR/verify-k8s-manifests.lock`，內含 pid；pid 已死則接管）禁止並行，鎖在守衛前取得、EXIT 時釋放；建立 Namespace 後記錄 UID，清理前核對 UID 相符才刪。
  - 驗證（docker-desktop）：背景啟動第一個執行個體，25 秒後啟動第二個 → `PRECONDITION: 另一個 verify-k8s-manifests 執行個體（pid 39337）仍在執行，拒絕並行。`、`exit=2`；第一個完成 `SUMMARY: 14 passed, 0 failed, 1 unverified`、`exit=3`，無殘留、鎖已釋放。預先放入死 pid（999999）的鎖 → 接管並完成 `14 passed, 0 failed, 1 unverified`、`exit=3`，無殘留。label 失敗 shim 仍 `exit=1`、無殘留。
  - 限制：UID 不符分支（Namespace 於執行期間被替換）未做實測，僅程式審閱。
- 第 4 輪：Devin CLI（smart 模式），審 commit `8c4390e`，結果 FAIL。B-001：死 PID 鎖的「rm -rf 後 mkdir」接管非原子，兩個執行個體可同時取得鎖（成立）。B-002：Namespace UID 查詢失敗（空值）時 cleanup 因 UID guard 跳過，留下整組資源（成立）。
  - 修正：移除自動接管，只用原子 `mkdir` 取鎖，鎖已存在一律 `exit 2` 並提示人工刪除（殘鎖只會在程序被 SIGKILL 時出現）；cleanup 只在 pid 檔內容等於自己的 `$$` 時才移除鎖；UID 為空時改依名稱刪除（旗標已證明是本次 create 的），UID 已知則須相符。
  - 驗證（docker-desktop）：PATH shim 讓 `kubectl get ... metadata.uid` 失敗 → 完整 `SUMMARY: 14 passed, 0 failed, 1 unverified`、`exit=3`，Namespace 與鎖皆無殘留。預置死 pid（999999）的鎖 → `PRECONDITION: 互斥鎖 /tmp/verify-k8s-manifests.lock 已存在（持有者 pid 999999）…`、`exit=2`，鎖未被動到。背景執行第一個、20 秒後第二個 → 第二個 `exit=2`，第一個 `14 passed, 0 failed, 1 unverified`、`exit=3`，結束後無 Namespace 與鎖殘留。label 失敗 shim → `exit=1`，無殘留。
  - 過程中發現並修正：以 JS `String.replace` 寫入 `$$` 被吃成 `$`，導致 cleanup 不釋放鎖；已改正並以上述「無鎖殘留」驗證。
  - 限制：UID 不符分支仍未實測（僅程式審閱）；`TMPDIR` 不同時鎖不互斥（不支援的呼叫方式）。
- 第 5 輪：Devin CLI（smart 模式），審 commit `d08cbf5`，結果 FAIL（strict-reviewer subagent 判 PASS，主審判 FAIL）。前輪 B-001、B-002 確認解決。新 B-001：create 後另以 `get` 取 UID，兩次呼叫間若 Namespace 被換成同名資源，UID 會綁到別人的物件；UID 為空時依名稱刪除亦可能誤刪。判斷：需要有權限的操作者在毫秒級窗口內刪除並重建同名 Namespace，實際機率極低，但修法簡單，採納。另採納可用性建議：守衛 `exit 2` 前釋放鎖。
  - 修正：UID 直接取自 `kubectl create -o jsonpath='{.metadata.uid}'` 的回應（同一次 API 呼叫）；拿不到 UID 視為建立失敗、不記為本次建立；移除「UID 為空依名稱刪除」的 fallback，cleanup 一律須 UID 非空且相符才刪；守衛拒絕時先釋放鎖。
  - 驗證（docker-desktop）：完整重跑 `SUMMARY: 14 passed, 0 failed, 1 unverified`、`exit=3`，Namespace 與鎖無殘留。PATH shim 讓含 `metadata.uid` 的 kubectl 呼叫失敗 → `FAIL [1] 建立 Namespace projectc-verify 失敗（已存在、權限不足或回應無 UID；不接管既有 Namespace）`、輸出 `connection refused`、`exit=1`，無殘留。label 失敗 shim `exit=1`、無殘留。無 label 的既有 Namespace → `exit=2`、`foreign` ConfigMap 仍在、鎖已釋放。並行第二個 `exit=2`，第一個 `14 passed, 0 failed, 1 unverified`、`exit=3`，無殘留。
  - 限制：若 create 實際已在伺服器端成功、但回應遺失（網路中斷），該 Namespace 不會被清理（不依名稱刪除未知身分的物件，寧可殘留不誤刪）；UID 不符分支仍僅程式審閱。
- 第 6 輪：Devin CLI（smart 模式），審 commit `74a8efc`，結果 FAIL；前輪 B-001 與鎖釋放建議確認解決。唯一 blocking：cleanup 的 UID 核對（get）與刪除（delete）非原子，有權限者於其間刪除並重建同名 Namespace 會被誤刪。
  - 使用者裁定（2026-11-10）：「有權限者蓄意在毫秒窗口替換同名 Namespace」不在支援範圍（單人本機 docker-desktop 驗證腳本，威脅模型不成立）。降為已知限制，不修；理由寫於腳本註解（`kubectl delete` 無 UID precondition，`kubectl delete --raw -f` 實測回 NotFound，原子化須改走 kubectl proxy + curl，複雜度不值得）。
  - 後續外部審查與最後一次 strict-reviewer／對抗式審查均被告知此範圍裁定，不再審查有權限者蓄意競態替換。
- 第 7 輪：Devin CLI（smart 模式），審 commit `4bddacd`，結果 FAIL；UID 核對／delete 非原子依使用者裁定未列入。blocking：鎖路徑取決於呼叫端 `TMPDIR`，不同 TMPDIR 的一般呼叫各拿各的鎖，會把對方的 Namespace 當殘留刪除（成立，且是一般呼叫而非蓄意情境）。
  - 修正：鎖固定放 `/tmp/verify-k8s-manifests.lock`，不受 `TMPDIR` 影響（註解寫明原因）。
  - 驗證（docker-desktop）：背景啟動第一個（預設 TMPDIR），20 秒後以 `TMPDIR=/tmp/othertmp` 啟動第二個 → `PRECONDITION: 互斥鎖 /tmp/verify-k8s-manifests.lock 已存在（持有者 pid …` `exit=2`，未觸碰叢集；第一個 `SUMMARY: 14 passed, 0 failed, 1 unverified`、`exit=3`，無 Namespace 與鎖殘留。
