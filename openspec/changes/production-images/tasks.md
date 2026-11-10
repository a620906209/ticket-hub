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

- [x] 1.1 補強根目錄 `.dockerignore`（`**/appsettings.Development.json`、`**/.env*`、`**/node_modules`、`**/certs`、`web/`）並新增 `web/.dockerignore`（`node_modules`、`dist`、`certs`、`.env*`、`*.md`）。驗證：2.1 斷言 8。
  - 完成紀錄：2026-11-10。根目錄 `.dockerignore` 追加 `**/appsettings.Development.json`、`**/.env*`、`**/node_modules`、`**/certs`、`web/`（工作區為 CRLF（core.autocrlf 檢出），HEAD 為 LF，`git diff --stat` 僅顯示 +7 行）；新增 `web/.dockerignore`（`node_modules`、`dist`、`certs`、`.env*`、`*.md`）。驗證：2.1 斷言 8 與突變 (d)。
- [x] 1.2 新增 `docs/todo/2026-11-10-deployment-env-vars.md`：逐項對照 `docker-compose.yml` 與 `.env.example`，分「機敏（Secret）」「一般（ConfigMap）」「執行環境（ASPNETCORE_*）」三類並寫明歸類理由；標註 `docs/todo/2026-10-28-k8s-todo.md` 對應進度。驗證：3.1。
  - 完成紀錄：2026-11-10。已新增 `docs/todo/2026-11-10-deployment-env-vars.md`（機敏／一般／執行環境／本機專用／映像版本五節），並於 `docs/todo/2026-10-28-k8s-todo.md` 加註進度。依 Devin 審查 warning 補：容器鍵名與 `.env.example` 插值名對照欄、`SEQ_FIRSTRUN_NOAUTHENTICATION` 僅限本機、migrator `--connection` 密碼日後應改 env、基底映像 tag 與查證日期。驗證：3.1。

## 2. 映像與驗證腳本

- [x] 2.1 先寫 `scripts/verify-production-images.sh` 與 `deploy/docker-compose.verify.yml`（獨立 compose 專案 `projectc-verify`，機敏值由腳本隨機產生，不進版控），實作 design 決策 5 的斷言 1～9。**RED**：在 `deploy/*.Dockerfile` 尚不存在時執行，預期因建置失敗而非 0 結束（記錄命令與原文輸出；此為流程性 RED，不當作行為 RED）。
  - 完成紀錄（2026-11-10）：
    - 流程性 RED 第 1 次（腳本自身 bug，不計為 RED）：映像 tag 含 `:` 與 `:` 分隔符衝突，錯誤為 `path "deploy/api.Dockerfile:." not found`，不是 Dockerfile 缺少；修正分隔符為 `|` 後重跑。
    - 流程性 RED（命令 `bash scripts/verify-production-images.sh`，`deploy/*.Dockerfile` 不存在，結束碼 1；原文）：
```
FAIL [1] 建置 projectc-verify/api:test 失敗
---- 實際輸出 ----
#0 building with "desktop-linux" instance using docker driver

#1 [internal] load build definition from api.Dockerfile
#1 transferring dockerfile: 2B 0.0s done
#1 DONE 0.1s
ERROR: failed to build: failed to solve: failed to read dockerfile: open api.Dockerfile: no such file or directory

View build details: docker-desktop://dashboard/build/desktop-linux/desktop-linux/dd1fpye2n53y2qcms8degm1u0
------------------
FAIL [1] 建置 projectc-verify/migrator:test 失敗
---- 實際輸出 ----
#0 building with "desktop-linux" instance using docker driver

#1 [internal] load build definition from api.Dockerfile
#1 transferring dockerfile: 2B done
#1 DONE 0.0s
ERROR: failed to build: failed to solve: failed to read dockerfile: open api.Dockerfile: no such file or directory

View build details: docker-desktop://dashboard/build/desktop-linux/desktop-linux/s89avi6jhfgykyuqm20pts50l
------------------
FAIL [1] 建置 projectc-verify/web:test 失敗
---- 實際輸出 ----
#0 building with "desktop-linux" instance using docker driver

#1 [internal] load build definition from web.Dockerfile
#1 transferring dockerfile: 2B done
#1 DONE 0.0s
ERROR: failed to build: failed to solve: failed to read dockerfile: open web.Dockerfile: no such file or directory

View build details: docker-desktop://dashboard/build/desktop-linux/desktop-linux/i6iv9ypwvj7stb3oesbf5z0fm
------------------
映像建置失敗，後續斷言無法執行
SUMMARY: 0 passed, 3 failed
```
    - 另：斷言 9 依 Devin 審查改用 `POST /api/auth/login`（匿名、讀 body、以 Content-Length 傳送）；斷言 5 改斷言 2xx。
    - **第 1 輪外部審查（Devin，commit 9064470）B-001 處理：不採納。** 審查認為 2.1 的流程性 RED 不是行為 RED。design.md 決策 5 已明定本 change 採替代驗證例外（腳本先行的流程性 RED + 突變驗證），spec-reviewer 與 design-hardener 皆已接受；「目標行為失敗」的證據由 2.3 突變 a–e 提供（每個突變讓對應斷言失敗，還原後重跑通過）。
    - **行為 RED：以暫時錯誤 Dockerfile 取得**（2026-11-10；補充上方「流程性 RED」，原紀錄保留）。同時套用三個暫時的錯誤實作（api runtime `USER root`、nginx 無 `/api/` 代理且無 SPA `try_files`、`.dockerignore` 未排除 `appsettings.Development.json`），執行腳本取得斷言層級的失敗；跑完以備份還原並確認 `git status`、`git diff --stat` 皆無輸出。完整原文：
    ```
    $ git status --short   # 改動前
    (無輸出 = 乾淨)
    $ sed -i "0,/^USER \$APP_UID/s//USER root/" deploy/api.Dockerfile
    $ sed -i "/location \/api\/ {/,/^    }/d; /try_files \$uri \/index.html;/d" deploy/nginx/default.conf
    $ sed -i "/appsettings.Development.json/d" .dockerignore
    $ git diff -- deploy .dockerignore
    warning: in the working copy of '.dockerignore', LF will be replaced by CRLF the next time Git touches it
    warning: in the working copy of 'deploy/api.Dockerfile', LF will be replaced by CRLF the next time Git touches it
    warning: in the working copy of 'deploy/nginx/default.conf', LF will be replaced by CRLF the next time Git touches it
    diff --git a/.dockerignore b/.dockerignore
    index 318bd42..f31e29d 100644
    --- a/.dockerignore
    +++ b/.dockerignore
    @@ -8,7 +8,6 @@ openspec/
     *.md

     # production 映像（deploy/）：機敏與本機專用檔案不得進入 build context
    -**/appsettings.Development.json
     **/.env*
     **/node_modules
     **/certs
    diff --git a/deploy/api.Dockerfile b/deploy/api.Dockerfile
    index af61ed1..0895347 100644
    --- a/deploy/api.Dockerfile
    +++ b/deploy/api.Dockerfile
    @@ -38,7 +38,7 @@ ENV ASPNETCORE_URLS=http://+:8080 \
     EXPOSE 8080
     # APP_UID 為基底映像內建的非 root 使用者（數字 UID），日後 K8s runAsNonRoot 可直接驗證。
     # 不設 HEALTHCHECK：aspnet 映像沒有 curl，探針交給編排層（/health/live、/health/ready）。
    -USER $APP_UID
    +USER root
     ENTRYPOINT ["dotnet", "ProjectC.WebApi.dll"]

     # 用法：migrator --connection "<連線字串>"。密碼會出現在命令列；K8s Job 日後應改由 Secret 以環境變數注入。
    diff --git a/deploy/nginx/default.conf b/deploy/nginx/default.conf
    index a6715a0..ccd545e 100644
    --- a/deploy/nginx/default.conf
    +++ b/deploy/nginx/default.conf
    @@ -6,11 +6,6 @@ server {
         # 上游主機名 `api` 沿用 compose／日後 K8s Service 名稱；nginx 啟動時解析不到會直接退出。
         # client_max_body_size 取 10m：等於 API 單端點最大上限 RequestBodyLimits.CreateSeatMapBytes
         # （10,485,760 bytes）；nginx 預設 1m 會以 413 擋掉座位圖建立。實際的 1 MiB 全域與各端點上限仍由 API 負責。
    -    location /api/ {
    -        client_max_body_size 10m;
    -        proxy_pass http://api:8080;
    -        proxy_set_header Host $host;
    -    }

         # Vite 產出的檔名帶 hash，可長期快取。
         location /assets/ {
    @@ -19,7 +14,6 @@ server {
         }

         location / {
    -        try_files $uri /index.html;
             add_header Cache-Control "no-cache";
         }
     }
    $ bash scripts/verify-production-images.sh
    PASS [1] 建置 projectc-verify/api:test
    PASS [1] 建置 projectc-verify/migrator:test
    PASS [1] 建置 projectc-verify/web:test
    FAIL [2a] runtime 使用者不是非 root 數字 UID
    ---- 實際輸出 ----
    0
    ------------------
    PASS [2b] runtime 映像不含 SDK
    FAIL [8] runtime 映像內存在 appsettings.Development.json
    ---- 實際輸出 ----
    /app/appsettings.Development.json
    ------------------
    PASS [3] migrator 套用 16 筆 migration（預期 16）
    PASS [7] 缺 Jwt__SigningKey 時 api 以結束碼 139 退出，日誌含 OptionsValidationException 與 JwtOptions
    PASS [4] /health/live=200，/health/ready=200
    FAIL [5] 經 web 的 GET /api/events 回 404（預期 2xx）
    ---- 實際輸出 ----
    HTTP/1.1 404 Not Found
    Server: nginx/1.30.6
    Date: Tue, 10 Nov 2026 06:47:36 GMT
    Content-Type: text/html
    Content-Length: 153
    Connection: keep-alive

    <html>
    <head><title>404 Not Found</title></head>
    <body>
    <center><h1>404 Not Found</h1></center>
    <hr><center>nginx/1.30.6</center>
    </body>
    </html>
    ------------------
    FAIL [6a] SPA 子路由 /events/1 不符（狀態=404）
    ---- 實際輸出 ----
    HTTP/1.1 404 Not Found
    Server: nginx/1.30.6
    Date: Tue, 10 Nov 2026 06:47:37 GMT
    Content-Type: text/html
    Content-Length: 153
    Connection: keep-alive
    ------------------
    FAIL [6b] 靜態資產 '' 不符
    FAIL [9] 經 nginx 送 10485760 bytes 至 /api/auth/login 的回應不是 API 的 PayloadTooLarge ProblemDetails
    ---- 實際輸出 ----
    HTTP/1.1 413 Request Entity Too Large
    Server: nginx/1.30.6
    Date: Tue, 10 Nov 2026 06:47:37 GMT
    Content-Type: text/html
    Content-Length: 183
    Connection: close

    <html>
    <head><title>413 Request Entity Too Large</title></head>
    <body>
    <center><h1>413 Request Entity Too Large</h1></center>
    <hr><center>nginx/1.30.6</center>
    </body>
    </html>
    ------------------
    SUMMARY: 7 passed, 6 failed
    exit=1
    $ git status --short   # 還原後
    (無輸出 = 乾淨)
    $ git diff --stat
    (無輸出 = 與 commit 相同)
    ```
- [x] 2.2 新增 `deploy/api.Dockerfile`（`build`／`runtime`／`migrator`）、`deploy/web.Dockerfile` 與 `deploy/nginx/default.conf`，內容依 design 決策 2、3。**GREEN**：`bash scripts/verify-production-images.sh` 全部斷言通過。另以真實瀏覽器（claude-in-chrome）對 web 容器對外 port 跑一次：註冊、登入、重新整理子路由；數字與狀態以頁面文字或 DB 核對。
  - 完成紀錄（2026-11-10）：
    - 首次 GREEN 嘗試失敗兩次並修正：(1) web `npm ci` ERESOLVE（openapi-typescript peer 為 TypeScript 5）→ Dockerfile 需一併 COPY `web/.npmrc`（`legacy-peer-deps=true`）；(2) Windows 原生 curl 讀不到 MSYS `/tmp` 路徑 → 腳本加 `cygpath -m`。
    - GREEN 原文（命令 `bash scripts/verify-production-images.sh`，結束碼 0）：
```
PASS [1] 建置 projectc-verify/api:test
PASS [1] 建置 projectc-verify/migrator:test
PASS [1] 建置 projectc-verify/web:test
PASS [2a] runtime 使用者 UID=1654（非 root）
PASS [2b] runtime 映像不含 SDK
PASS [8] runtime 映像內不存在 appsettings.Development.json
PASS [3] migrator 套用 16 筆 migration（預期 16）
PASS [7] 缺 Jwt__SigningKey 時 api 以結束碼 139 退出，日誌含 OptionsValidationException 與 JwtOptions
PASS [4] /health/live=200，/health/ready=200
PASS [5] 經 web 的 GET /api/events 回 200
PASS [6a] SPA 子路由 /events/1 回 200、為 index.html 且 Cache-Control 含 no-cache
PASS [6b] /assets/index-Znza2iwO.js 回 200 且 Cache-Control 含 immutable
PASS [9] 經 nginx 送 10485760 bytes 至 /api/auth/login，回應來自 API（ProblemDetails PayloadTooLarge）
SUMMARY: 13 passed, 0 failed
```
    - 全部突變還原後重跑（`KEEP_UP=1 bash scripts/verify-production-images.sh`，結束碼 0，原文）：
```
PASS [1] 建置 projectc-verify/api:test
PASS [1] 建置 projectc-verify/migrator:test
PASS [1] 建置 projectc-verify/web:test
PASS [2a] runtime 使用者 UID=1654（非 root）
PASS [2b] runtime 映像不含 SDK
PASS [8] runtime 映像內不存在 appsettings.Development.json
PASS [3] migrator 套用 16 筆 migration（預期 16）
PASS [7] 缺 Jwt__SigningKey 時 api 以結束碼 139 退出，日誌含 OptionsValidationException 與 JwtOptions
PASS [4] /health/live=200，/health/ready=200
PASS [5] 經 web 的 GET /api/events 回 200
PASS [6a] SPA 子路由 /events/1 回 200、為 index.html 且 Cache-Control 含 no-cache
PASS [6b] /assets/index-Znza2iwO.js 回 200 且 Cache-Control 含 immutable
PASS [9] 經 nginx 送 10485760 bytes 至 /api/auth/login，回應來自 API（ProblemDetails PayloadTooLarge）
KEEP_UP：web=http://127.0.0.1:6334 api=http://127.0.0.1:6333
SUMMARY: 13 passed, 0 failed
```
    - **真實瀏覽器驗證（2026-11-10，由主 session 完成）**：對 `KEEP_UP=1` 起的 verify 堆疊 web（`http://127.0.0.1:14526`，nginx-unprivileged production 映像）操作 claude-in-chrome：
      1. `/register` 頁正常載入（SPA 由 nginx 提供）。
      2. 使用者親自輸入驗證碼完成註冊（驗證碼由使用者輸入，非 agent）；DB 查詢 `Members` 有 `verify-prod-20261110@example.test`｜`VerifyProd`。
      3. 使用者登入後導向 `/`，頁面文字「活動列表／目前沒有活動」。
      4. 直接 navigate 子路由 `/events/1`（等同重新整理）：導覽列仍顯示「我的訂單」「VerifyProd」（登入狀態保留），main 文字為「找不到這個活動」（SPA fallback 正確、無 nginx 404）。
      驗證完已關閉分頁，並移除 `projectc-verify` 容器／volume／網路。
- [x] 2.3 突變驗證（腳本須失敗；一次一個改動，還原後重跑通過）：(a) `runtime` stage 改 `USER root`；(b) 移除 nginx `try_files`；(c) 移除 `migrator` 的 `ef migrations bundle` 步驟；(d) 在 `.dockerignore` 移除 `**/appsettings.Development.json`（腳本已於 build context 放 dummy 檔，見 design 斷言 8，否則此突變不產生差異）；(e) 移除 nginx `/api/` 的 `client_max_body_size 10m`。每個突變貼失敗斷言名稱與輸出原文。
  - 完成紀錄（2026-11-10；每次只改一個、以 `cp` 備份還原並以 `cmp` 證明位元組相同；還原後重跑通過見 2.2 的 GREEN）。以下為單次執行的完整原文輸出，含每個突變的 sed 命令、`diff -u`、腳本全部 PASS／FAIL 行、結束碼與還原證明：
    ```
    ### 突變 (a)
    $ sed -i '0,/^USER \$APP_UID/s//USER root/' deploy/api.Dockerfile
    $ diff -u <原檔> deploy/api.Dockerfile
    @@ -38,7 +38,7 @@
     EXPOSE 8080
     # APP_UID 為基底映像內建的非 root 使用者（數字 UID），日後 K8s runAsNonRoot 可直接驗證。
     # 不設 HEALTHCHECK：aspnet 映像沒有 curl，探針交給編排層（/health/live、/health/ready）。
    -USER $APP_UID
    +USER root
     ENTRYPOINT ["dotnet", "ProjectC.WebApi.dll"]

     # 用法：migrator --connection "<連線字串>"。密碼會出現在命令列；K8s Job 日後應改由 Secret 以環境變數注入。
    $ bash scripts/verify-production-images.sh
    PASS [1] 建置 projectc-verify/api:test
    PASS [1] 建置 projectc-verify/migrator:test
    PASS [1] 建置 projectc-verify/web:test
    FAIL [2a] runtime 使用者不是非 root 數字 UID
    ---- 實際輸出 ----
    0
    ------------------
    PASS [2b] runtime 映像不含 SDK
    PASS [8] runtime 映像內不存在 appsettings.Development.json
    PASS [3] migrator 套用 16 筆 migration（預期 16）
    PASS [7] 缺 Jwt__SigningKey 時 api 以結束碼 139 退出，日誌含 OptionsValidationException 與 JwtOptions
    PASS [4] /health/live=200，/health/ready=200
    PASS [5] 經 web 的 GET /api/events 回 200
    PASS [6a] SPA 子路由 /events/1 回 200、為 index.html 且 Cache-Control 含 no-cache
    PASS [6b] /assets/index-Znza2iwO.js 回 200 且 Cache-Control 含 immutable
    PASS [9] 經 nginx 送 10485760 bytes 至 /api/auth/login，回應來自 API（ProblemDetails PayloadTooLarge）
    SUMMARY: 12 passed, 1 failed
    exit=1
    $ cp <原檔備份> deploy/api.Dockerfile && cmp /tmp/orig.bak deploy/api.Dockerfile
    還原後與原檔位元組相同

    ### 突變 (b)
    $ sed -i '/try_files \$uri \/index.html;/d' deploy/nginx/default.conf
    $ diff -u <原檔> deploy/nginx/default.conf
    @@ -19,7 +19,6 @@
         }

         location / {
    -        try_files $uri /index.html;
             add_header Cache-Control "no-cache";
         }
     }
    $ bash scripts/verify-production-images.sh
    PASS [1] 建置 projectc-verify/api:test
    PASS [1] 建置 projectc-verify/migrator:test
    PASS [1] 建置 projectc-verify/web:test
    PASS [2a] runtime 使用者 UID=1654（非 root）
    PASS [2b] runtime 映像不含 SDK
    PASS [8] runtime 映像內不存在 appsettings.Development.json
    PASS [3] migrator 套用 16 筆 migration（預期 16）
    PASS [7] 缺 Jwt__SigningKey 時 api 以結束碼 139 退出，日誌含 OptionsValidationException 與 JwtOptions
    PASS [4] /health/live=200，/health/ready=200
    PASS [5] 經 web 的 GET /api/events 回 200
    FAIL [6a] SPA 子路由 /events/1 不符（狀態=404）
    ---- 實際輸出 ----
    HTTP/1.1 404 Not Found
    Server: nginx/1.30.6
    Date: Tue, 10 Nov 2026 06:12:51 GMT
    Content-Type: text/html
    Content-Length: 153
    Connection: keep-alive
    ------------------
    FAIL [6b] 靜態資產 '' 不符
    PASS [9] 經 nginx 送 10485760 bytes 至 /api/auth/login，回應來自 API（ProblemDetails PayloadTooLarge）
    SUMMARY: 11 passed, 2 failed
    exit=1
    $ cp <原檔備份> deploy/nginx/default.conf && cmp /tmp/orig.bak deploy/nginx/default.conf
    還原後與原檔位元組相同

    ### 突變 (c)
    $ sed -i '/^RUN dotnet ef migrations bundle/,/-o \/out\/efbundle/d' deploy/api.Dockerfile
    $ diff -u <原檔> deploy/api.Dockerfile
    @@ -23,12 +23,6 @@
     FROM build AS bundle
     RUN dotnet tool install --global dotnet-ef --version "10.*"
     ENV PATH="${PATH}:/root/.dotnet/tools"
    -RUN dotnet ef migrations bundle \
    -    --project src/ProjectC.Infrastructure \
    -    --startup-project src/ProjectC.WebApi \
    -    --configuration Release \
    -    --self-contained -r linux-x64 \
    -    -o /out/efbundle

     FROM mcr.microsoft.com/dotnet/aspnet:10.0.14 AS runtime
     WORKDIR /app
    $ bash scripts/verify-production-images.sh
    PASS [1] 建置 projectc-verify/api:test
    FAIL [1] 建置 projectc-verify/migrator:test 失敗
    ---- 實際輸出 ----
    #11 [build  8/10] RUN dotnet restore src/ProjectC.WebApi/ProjectC.WebApi.csproj
    #11 CACHED

    #12 [build  9/10] COPY src/ src/
    #12 CACHED

    #13 [build  7/10] COPY src/ProjectC.WebApi/ProjectC.WebApi.csproj src/ProjectC.WebApi/
    #13 CACHED

    #14 [build  3/10] COPY Directory.Packages.props ./
    #14 CACHED

    #15 [build  2/10] WORKDIR /src
    #15 CACHED

    #16 [build  4/10] COPY src/ProjectC.Domain/ProjectC.Domain.csproj src/ProjectC.Domain/
    #16 CACHED

    #17 [bundle 1/1] RUN dotnet tool install --global dotnet-ef --version "10.*"
    #17 CACHED

    #18 [migrator 2/3] WORKDIR /app
    #18 CACHED

    #19 [migrator 3/3] COPY --from=bundle /out/efbundle .
    #19 ERROR: failed to calculate checksum of ref 1a7a855b-33bb-47db-900d-b9f50e07d360::cyikjfcuptsxdsrccgqokq8sy: "/out/efbundle": not found
    ------
     > [migrator 3/3] COPY --from=bundle /out/efbundle .:
    ------
    api.Dockerfile:41
    --------------------
      39 |     FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.14 AS migrator
      40 |     WORKDIR /app
      41 | >>> COPY --from=bundle /out/efbundle .
      42 |     # 自包含單一檔案若需解壓原生函式庫，指向非 root 可寫路徑，不改回 root。
      43 |     ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp
    --------------------
    ERROR: failed to build: failed to solve: failed to compute cache key: failed to calculate checksum of ref 1a7a855b-33bb-47db-900d-b9f50e07d360::cyikjfcuptsxdsrccgqokq8sy: "/out/efbundle": not found

    View build details: docker-desktop://dashboard/build/desktop-linux/desktop-linux/yd1pfv0qzme2pc4tsamlu0o15
    ------------------
    PASS [1] 建置 projectc-verify/web:test
    映像建置失敗，後續斷言無法執行
    SUMMARY: 2 passed, 1 failed
    exit=1
    $ cp <原檔備份> deploy/api.Dockerfile && cmp /tmp/orig.bak deploy/api.Dockerfile
    還原後與原檔位元組相同

    ### 突變 (d)
    $ sed -i '/appsettings.Development.json/d' .dockerignore
    $ diff -u <原檔> .dockerignore
    @@ -1,15 +1,14 @@
    -**/bin/
    -**/obj/
    -**/.vs/
    -**/.vscode/
    -.git/
    -.gitignore
    -openspec/
    -*.md
    -
    -# production 映像（deploy/）：機敏與本機專用檔案不得進入 build context
    -**/appsettings.Development.json
    -**/.env*
    -**/node_modules
    -**/certs
    -web/
    +**/bin/
    +**/obj/
    +**/.vs/
    +**/.vscode/
    +.git/
    +.gitignore
    +openspec/
    +*.md
    +
    +# production 映像（deploy/）：機敏與本機專用檔案不得進入 build context
    +**/.env*
    +**/node_modules
    +**/certs
    +web/
    $ bash scripts/verify-production-images.sh
    PASS [1] 建置 projectc-verify/api:test
    PASS [1] 建置 projectc-verify/migrator:test
    PASS [1] 建置 projectc-verify/web:test
    PASS [2a] runtime 使用者 UID=1654（非 root）
    PASS [2b] runtime 映像不含 SDK
    FAIL [8] runtime 映像內存在 appsettings.Development.json
    ---- 實際輸出 ----
    /app/appsettings.Development.json
    ------------------
    PASS [3] migrator 套用 16 筆 migration（預期 16）
    PASS [7] 缺 Jwt__SigningKey 時 api 以結束碼 139 退出，日誌含 OptionsValidationException 與 JwtOptions
    PASS [4] /health/live=200，/health/ready=200
    PASS [5] 經 web 的 GET /api/events 回 200
    PASS [6a] SPA 子路由 /events/1 回 200、為 index.html 且 Cache-Control 含 no-cache
    PASS [6b] /assets/index-Znza2iwO.js 回 200 且 Cache-Control 含 immutable
    PASS [9] 經 nginx 送 10485760 bytes 至 /api/auth/login，回應來自 API（ProblemDetails PayloadTooLarge）
    SUMMARY: 12 passed, 1 failed
    exit=1
    $ cp <原檔備份> .dockerignore && cmp /tmp/orig.bak .dockerignore
    還原後與原檔位元組相同

    ### 突變 (e)
    $ sed -i '/client_max_body_size 10m;/d' deploy/nginx/default.conf
    $ diff -u <原檔> deploy/nginx/default.conf
    @@ -7,7 +7,6 @@
         # client_max_body_size 取 10m：等於 API 單端點最大上限 RequestBodyLimits.CreateSeatMapBytes
         # （10,485,760 bytes）；nginx 預設 1m 會以 413 擋掉座位圖建立。實際的 1 MiB 全域與各端點上限仍由 API 負責。
         location /api/ {
    -        client_max_body_size 10m;
             proxy_pass http://api:8080;
             proxy_set_header Host $host;
         }
    $ bash scripts/verify-production-images.sh
    PASS [1] 建置 projectc-verify/api:test
    PASS [1] 建置 projectc-verify/migrator:test
    PASS [1] 建置 projectc-verify/web:test
    PASS [2a] runtime 使用者 UID=1654（非 root）
    PASS [2b] runtime 映像不含 SDK
    PASS [8] runtime 映像內不存在 appsettings.Development.json
    PASS [3] migrator 套用 16 筆 migration（預期 16）
    PASS [7] 缺 Jwt__SigningKey 時 api 以結束碼 139 退出，日誌含 OptionsValidationException 與 JwtOptions
    PASS [4] /health/live=200，/health/ready=200
    PASS [5] 經 web 的 GET /api/events 回 200
    PASS [6a] SPA 子路由 /events/1 回 200、為 index.html 且 Cache-Control 含 no-cache
    PASS [6b] /assets/index-Znza2iwO.js 回 200 且 Cache-Control 含 immutable
    FAIL [9] 經 nginx 送 10485760 bytes 至 /api/auth/login 的回應不是 API 的 PayloadTooLarge ProblemDetails
    ---- 實際輸出 ----
    HTTP/1.1 413 Request Entity Too Large
    Server: nginx/1.30.6
    Date: Tue, 10 Nov 2026 06:14:13 GMT
    Content-Type: text/html
    Content-Length: 183
    Connection: close

    <html>
    <head><title>413 Request Entity Too Large</title></head>
    <body>
    <center><h1>413 Request Entity Too Large</h1></center>
    <hr><center>nginx/1.30.6</center>
    </body>
    </html>
    ------------------
    SUMMARY: 12 passed, 1 failed
    exit=1
    $ cp <原檔備份> deploy/nginx/default.conf && cmp /tmp/orig.bak deploy/nginx/default.conf
    還原後與原檔位元組相同
    ```

## 3. 文件與收尾

- [x] 3.1 核對 1.2 文件：以腳本或 `grep` 列出 `docker-compose.yml` 全部服務（`db`、`seq`、`api`、`web` 等）的環境變數鍵、`${VAR}` 插值鍵與 `.env.example` 鍵，逐項確認都在分類表中、沒有遺漏或重複；貼核對命令與結果。
  - 完成紀錄（2026-11-10）：鍵來源 = compose 各服務 `environment:` 鍵 ∪ compose `${VAR}` 插值鍵 ∪ `.env.example` 鍵；逐一確認出現於文件反引號中。完整命令與原文輸出：
    ```
    $ { awk '/environment:/{f=1;next} f&&/^    [a-z]/{f=0} f&&/^      [A-Za-z_]/{print $1}' docker-compose.yml | tr -d ':'; grep -oE '\$\{[A-Z_]+' docker-compose.yml | tr -d '${'; grep -oE '^#? ?[A-Z_]+=' .env.example | tr -d '# ='; } | sort -u > /tmp/keys.txt
    $ wc -l < /tmp/keys.txt
    39
    $ cat /tmp/keys.txt
    ACCEPT_EULA
    API_HOST_PORT
    ASPNETCORE_ENVIRONMENT
    ASPNETCORE_URLS
    AUTH_PASSWORD_RESET_TOKEN_EXPIRATION_MINUTES
    AUTH_REFRESH_TOKEN_EXPIRATION_DAYS
    Auth__PasswordResetTokenExpirationMinutes
    Auth__RefreshTokenExpirationDays
    ConnectionStrings__DefaultConnection
    ConnectionStrings__Redis
    DB_HOST_PORT
    DEV_SEED_ADMIN_EMAIL
    DEV_SEED_ADMIN_PASSWORD
    DEV_SEED_ORGANIZER_NAME
    DevelopmentSeed__AdminEmail
    DevelopmentSeed__AdminPassword
    DevelopmentSeed__OrganizerName
    JWT_ACCESS_TOKEN_EXPIRATION_MINUTES
    JWT_AUDIENCE
    JWT_ISSUER
    JWT_SIGNING_KEY
    Jwt__AccessTokenExpirationMinutes
    Jwt__Audience
    Jwt__Issuer
    Jwt__SigningKey
    PGADMIN_EMAIL
    PGADMIN_HOST_PORT
    PGADMIN_PASSWORD
    POSTGRES_DB
    POSTGRES_PASSWORD
    POSTGRES_USER
    SEQ_FIRSTRUN_NOAUTHENTICATION
    SEQ_HOST_PORT
    Seq__ServerUrl
    TESTCONTAINERS_RYUK_DISABLED
    TICKET_SIGNING_KEY
    Testcontainers__ComposeNetworkName
    TicketSigning__SigningKey
    WEB_HOST_PORT
    $ missing=0; while read k; do grep -qF "\`$k\`" docs/todo/2026-11-10-deployment-env-vars.md || { echo "MISSING: $k"; missing=$((missing+1)); }; done < /tmp/keys.txt; echo "missing=$missing"
    missing=0
    $ grep -oE '^\| `[A-Za-z_]+`' docs/todo/2026-11-10-deployment-env-vars.md | sort | uniq -d
    (uniq -d 結束，上方無輸出即無重複)
    ```
    服務涵蓋：db、seq、redis、api、web（無 environment）、k6（無 environment）；`ACCEPT_EULA`／`SEQ_FIRSTRUN_NOAUTHENTICATION`／`TESTCONTAINERS_*`／`Testcontainers__*` 皆已分類。
- [x] 3.2 更新 `docs/todo/2026-10-28-k8s-todo.md`：標註「production-images」完成的項目與日期，保留 `k8s-local-manifests` 待辦；執行 `bash scripts/check-doc-paths.sh` 通過。
  - 完成紀錄（2026-11-10）：`docs/todo/2026-10-28-k8s-todo.md` 頂部加註進度（production-images 完成項目，`k8s-local-manifests` 待辦保留）。`bash scripts/check-doc-paths.sh` 輸出：`OK: 所有 docs 路徑引用都存在`。
- [x] 3.3 回報：實作結果是否與 design 一致、偏差點、spec 同步狀態（無 spec 影響）、AC 對應（D1～D6）、維護性提醒（重複邏輯：Dockerfile 與開發用 Dockerfile 的 SDK／Node 版本是否一致；命名一致性）。
  - 完成紀錄（2026-11-10）：回報已送主 session，重點：與 design 一致；偏差見下。spec 同步：無 spec 影響（`skip_specs: true`）。D1–D4、D6 由腳本斷言與突變驗證；D5 由 3.1；D4 的真實瀏覽器部分未完成（見 2.2）。維護性：SDK 主版本 `10.0` 與開發 `Dockerfile` 一致（production 釘 `10.0.403`）、Node 主版本 22 與 `web/Dockerfile` 一致（production 釘 `22.22`）；`appsettings.Development.json` dummy、`ConnectionStrings` 組字串在 verify compose 與開發 compose 各一份（屬刻意獨立）。偏差：(1) web 映像用 `--build-context deploy=deploy` 取得 `deploy/nginx/default.conf`（web build context 為 `web/`，無法直接 COPY）；(2) web Dockerfile 需 COPY `.npmrc`；(3) `/assets/` 用單一 `Cache-Control: public, max-age=31536000, immutable` 而非 `expires 1y` + add_header；(4) 負向斷言 7 的 api 結束碼為 139（非 134），僅斷言非 0 + 日誌；(5) 斷言 9 的 nginx 判定改為端點 `/api/auth/login`。
