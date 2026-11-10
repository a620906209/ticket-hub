# Design

## Context

動機見 proposal.md「Why」。已讀檔案確認的現況：

- 根目錄 `Dockerfile` 為開發用（SDK 映像、安裝 dotnet-ef、`dotnet watch`），`web/Dockerfile` 為開發用（`npm run dev`）；兩者都靠 bind mount，沒有任何 production 建置。
- api 不在啟動時跑 migration；migration 放在 `src/ProjectC.Infrastructure/Persistence/Migrations/`，`Microsoft.EntityFrameworkCore.Design` 由 `ProjectC.WebApi.csproj` 參照（可作為 `--startup-project`）。
- `Program.cs` 只在 Development 執行 `DevelopmentDataSeeder` 與 OpenAPI／SwaggerUI；`appsettings.json` 的機敏值皆為空，需由環境變數注入。`/health/live`、`/health/ready` 已存在（`api-health-checks`），並以 `UseWhen` 排除 HTTPS 轉址。
- `UseHttpsRedirection` 在 HTTP-only 容器內沒有可用的 HTTPS port，行為（略過轉址並記警告）屬未驗證假設，由驗證腳本斷言「經 nginx 的 `/api` 回應不是 3xx」。
- `web/vite.config.ts` 的 `/api` 代理只存在於 dev server（target `http://api:8080`），前端程式碼不寫死 API 位址；production 需由 nginx 取代。
- `src/ProjectC.WebApi/appsettings.Development.json` 本機存在但被 `.gitignore` 排除；docker build context 為本機目錄，若不排除會被複製進映像。
- 本機未裝 .NET SDK 的使用規則（CLAUDE.md）：只透過 `docker build`／`docker compose` 操作，不在本機執行 dotnet。

本 change 設 `skip_specs: true`，理由見 proposal.md「Capabilities」。

### 跨模組慣例對照

不適用：不新增或修改 API 端點、錯誤回應、認證／授權、DTO 欄位或列表端點（純部署產物）。

## Goals / Non-Goals

**Goals:**
- 產出可獨立執行（不靠 bind mount、不含 SDK）的 api 與 web 映像，以及 migration 用的 `migrator` 映像。
- 以可重現的腳本證明映像可用，並寫下機敏／一般設定的分類，供 `k8s-local-manifests` 直接使用。

**Non-Goals:**
- K8s manifest、Secret／ConfigMap 物件、NetworkPolicy、探針設定、TLS、Ingress、來源 IP 轉送（`X-Forwarded-*`）、CI 建置與映像推送、多架構映像、映像掃描。
- 改動既有開發環境（`Dockerfile`、`web/Dockerfile`、`docker-compose.yml`）。

## Decisions

### 決策 1：Dockerfile 放 `deploy/`，與開發用檔案並存

新檔 `deploy/api.Dockerfile`、`deploy/web.Dockerfile`，建置 context 為專案根目錄（api）與 `web/`（web）。理由：根目錄 `Dockerfile` 已被 `docker-compose.yml` 的 build 定義引用（`docker-compose.loadtest-release.yml` 間接依賴），改名或覆寫會動到開發流程。替代方案：用 `Dockerfile.prod` 並排在根目錄，否決原因是根目錄已有多個 compose 檔，再增加檔案更難分辨用途。

### 決策 2：api 映像 — 三個 stage、非 root、預設 Production

- `build`（`sdk:10.0`）：`COPY` 後 `dotnet publish src/ProjectC.WebApi -c Release -o /app/publish --no-self-contained`，並先只複製 `*.csproj` 與 `Directory.Packages.props`（Central Package Management，另含 `Directory.Build.props`、`nuget.config`，若存在）做 `restore`，讓相依層可快取。
- `runtime`（`aspnet:10.0`）：複製 publish 輸出，`USER $APP_UID`（映像內建的非 root 使用者，數字 UID，日後 K8s `runAsNonRoot` 可驗證），`ENV ASPNETCORE_URLS=http://+:8080`、`ASPNETCORE_ENVIRONMENT=Production`，`EXPOSE 8080`。不設 `HEALTHCHECK`（aspnet 映像沒有 curl，探針交給編排層）。
- `migrator`（`runtime-deps:10.0`，同樣 `USER $APP_UID` 非 root）：在 `build` stage 安裝 `dotnet-ef` 並執行 `dotnet ef migrations bundle --project src/ProjectC.Infrastructure --startup-project src/ProjectC.WebApi --self-contained -r linux-x64 -o /out/efbundle`，只把 `efbundle` 複製進此 stage。連線以 `--connection` 參數傳入（密碼會出現在命令列，日後 K8s 應改由 Secret 注入，記入環境變數分類文件）。若 build stage 需 dummy 環境變數，用單行 `RUN VAR=x ...` 或 `ARG`，不用 `ENV`；非 root 下若 bundle 無法解壓原生函式庫，設 `DOTNET_BUNDLE_EXTRACT_BASE_DIR` 指向可寫路徑，不改回 root。
- 基底映像 tag 釘選主版本（`10.0`），不用 `latest`；digest 釘選不做（見 Risks）。

替代方案：runtime 映像內含 `dotnet-ef`。否決原因：需要 SDK（約數百 MB）且擴大攻擊面。替代方案：api 啟動時自動 `Migrate()`。否決原因：與既有「migrate-before-new-version」規則衝突，且多副本會競爭。

### 決策 3：web 映像 — node build → nginx-unprivileged

- `build`（`node:22-alpine`）：`npm ci` → `npm run build`（`vue-tsc -b && vite build`）。
- 最終 stage 用 `nginxinc/nginx-unprivileged:<具體版本，實作時填入，不用 latest>-alpine`（UID 101、監聽 8080），複製 `dist/` 與 `deploy/nginx/default.conf`。
- nginx 設定：`location /api/ { client_max_body_size 10m; proxy_pass http://api:8080; }`（上游主機名沿用 compose／日後 K8s Service 的 `api`）。`client_max_body_size` 取 10m，等於 API 單端點最大上限 `RequestBodyLimits.CreateSeatMapBytes`（10,485,760 bytes）；nginx 預設 1m 會在 nginx 層以 413 擋掉座位圖建立，與直連 api／dev proxy 行為不一致。實際的全域 1MB 與各端點上限仍由 API（Kestrel 與 `[RequestSizeLimit]`）負責，nginx 只需不低於其最大值；`location /assets/ { expires 1y; add_header Cache-Control "public, immutable"; }`（Vite 產出的 hash 檔名）；`location / { try_files $uri /index.html; add_header Cache-Control "no-cache"; }`。
- `.dockerignore`：`web/.dockerignore` 排除 `node_modules`、`dist`、`certs`、`.env*`、`*.md`；根目錄 `.dockerignore` 補 `**/appsettings.Development.json`、`**/.env*`、`**/node_modules`、`**/certs`、`web/`（api 建置不需要，且 `web/certs` 含憑證）。web 映像的 build context 為 `web/`，只套用 `web/.dockerignore`。

### 決策 4：環境變數分類表放 `docs/todo/2026-11-10-deployment-env-vars.md`

依據 `docker-compose.yml` 與 `.env.example` 逐項分類：
- **機敏（日後 Secret）**：`POSTGRES_PASSWORD`（及含密碼的 `ConnectionStrings__DefaultConnection`）、`Jwt__SigningKey`、`TicketSigning__SigningKey`、`DevelopmentSeed__AdminPassword`。
- **一般（日後 ConfigMap）**：`Jwt__Issuer`、`Jwt__Audience`、`Jwt__AccessTokenExpirationMinutes`、`Auth__*` 到期設定、`Seq__ServerUrl`、`ConnectionStrings__Redis`、`DevelopmentSeed__AdminEmail`、`DevelopmentSeed__OrganizerName`、`ASPNETCORE_URLS`／`ASPNETCORE_ENVIRONMENT`、`POSTGRES_USER`、`POSTGRES_DB`（db 服務必需，日後 Postgres Deployment 也需要）、`seq` 服務的 `ACCEPT_EULA` 與 `SEQ_FIRSTRUN_NOAUTHENTICATION`（缺少時新版 Seq 啟動失敗，見 `docker-compose.yml` seq 註解）。
- **本機開發專用，不進部署分類表**（文件須各列一行標註排除理由）：`Testcontainers__ComposeNetworkName`、`TESTCONTAINERS_RYUK_DISABLED`（容器內整合測試用）、`docker-compose.yml` 中所有 `*_HOST_PORT` 覆寫，含 `.env.example` 未列的 `WEB_HOST_PORT`（僅決定 host 對外 port）、`PGADMIN_*`（本機 pgAdmin）。

文件只做分類與說明「為何歸入該類」，不產生 manifest；`k8s-local-manifests` 以此表為輸入。文件為新建，依 CLAUDE.md 檔名須帶建立日期前綴，放 `docs/todo/2026-11-10-deployment-env-vars.md`（K8s 線尚未完成，屬進行中前置文件）。

### 決策 5：替代驗證 `scripts/verify-production-images.sh`

映像沒有適合單元測試的行為，依 CLAUDE.md「Task 執行前」第 2 點記錄替代驗證例外：

- **適用範圍**：本 change 全部驗收項目（無 spec AC）。
- **偏離理由**：驗證對象是 Docker 建置與容器 runtime 屬性，沒有 xUnit 可觸及的邊界；Testcontainers 無法在此情境替代「完整映像＋nginx＋api 互連」。
- **替代內容**：腳本使用獨立的 compose 專案 `projectc-verify`（`deploy/docker-compose.verify.yml`，不共用開發環境的網路、volume、port），所有機敏值由腳本在執行時隨機產生並以環境變數傳入，不進版控；腳本不使用 `set -x` 或 `docker compose config`（會展開機敏值）、不把機敏值寫入持久檔案，並以 `trap` 清理 `projectc-verify` 的容器、volume 與網路。斷言（任一失敗即非 0 結束，並印出失敗項目與實際輸出）：
  1. 兩個映像與 `migrator` 映像建置成功。
  2. runtime 映像 `id -u` 不為 0，且 `dotnet --list-sdks` 輸出為空（不含 SDK）。
  3. `migrator` 對空資料庫執行成功，且 `__EFMigrationsHistory` 的筆數等於 `src/ProjectC.Infrastructure/Persistence/Migrations/` 內 `*.Designer.cs` 的檔案數（`[Migration("...")]` 屬性只存在於 `.Designer.cs`，主檔與 `ModelSnapshot` 不帶；目前為 16）。
  4. api 起動後 `/health/live`、`/health/ready` 回 200。
  5. 經 web（nginx）的 `GET /api/events`（既有匿名端點）回應可達且非 3xx（證實 HTTP-only 無轉址）。
  6. SPA 子路由（例如 `/events/1`）直接 GET 回 200 與 `index.html`，`/assets/*` 帶 `immutable` 快取標頭，`index.html` 帶 `no-cache`。
  7. 負向：僅缺少 `Jwt__SigningKey`（其餘必填設定齊全）時 api 容器以非 0 結束，且容器日誌含 `OptionsValidationException` 與 `Jwt` 設定驗證訊息（避免因其他設定缺漏而假通過；其餘必填設定指連線字串、`TicketSigning__SigningKey`，皆由腳本提供）。
  8. runtime 映像內不存在 `appsettings.Development.json`。`appsettings.Development.json` 被 `.gitignore` 排除，乾淨 checkout 上本來就不存在，直接檢查會 vacuously 通過；因此腳本建置前先在 `src/ProjectC.WebApi/` 放一個 dummy 同名檔（內容無意義），以 `trap` 還原（原檔已存在者不覆寫、不刪除），並在斷言前確認該檔確實存在於 build context，確保 `.dockerignore` 排除規則真的被測到。
  9. 經 nginx 對公開端點送恰好 10,485,760 bytes（= `RequestBodyLimits.CreateSeatMapBytes`，大於 API 全域 1 MiB）的 POST body，回應必須來自 API 而非 nginx：判定為回應 Body 為 ProblemDetails 且 `title` 為 `PayloadTooLarge`（API 全域 1MB 上限的 413，見 `GlobalExceptionHandler`），nginx 自己的 413 是 HTML 頁面、不符。nginx 上限若低於 10,485,760 bytes（含 `9.6m` 這類略低的值）會回 HTML 413 而失敗，故此斷言實證 nginx 上限不低於 API 最大值；突變 (e)（移除設定回到預設 1m）承擔「設定確實生效」。10 MiB 座位圖端點本身需 Admin 認證，不在腳本涵蓋範圍。
- **失敗情境可證明**：以 `scripts/verify-test-evidence.sh mutate` 風格的暫時改動（改成 `USER root`、移除 `try_files`、移除 migrator 步驟）各做一次，腳本須失敗，記錄於 tasks.md。
- **解除條件**：日後若 CI 能建置映像並執行同一支腳本，此例外改由 CI 取代；或本專案新增 `k8s-local-manifests` 後以 cluster 內驗證補強。
- 另以 claude-in-chrome 對 web 容器的對外 port 做一次真實瀏覽器流程（註冊、登入、重新整理子路由），數字與狀態以頁面文字或 DB 核對。

## Risks / Trade-offs

- [`migrator` 以 `-r linux-x64` 自包含，在 ARM 主機（Apple Silicon）上無法執行] → 本專案開發機為 Windows／amd64；多架構列為 Non-Goal，tasks 驗證在 amd64 進行並於文件註明。
- [基底映像只釘主版本，未釘 digest，未來小版本更新可能改變行為] → 本機學習用途可接受；日後 CI 建置時再改 digest 釘選。
- [nginx 啟動時解析 `api` 失敗會直接退出（上游主機名不存在）] → 驗證 compose 與日後 K8s Service 皆命名 `api`；文件註明此依賴。
- [`UseHttpsRedirection` 在 HTTP-only 容器的行為為假設] → 斷言 5 以實測取代假設，若出現 3xx 則停止並回報，不自行在 nginx 上補丁。
- [`dotnet ef migrations bundle` 在 build stage 沒有環境變數，且專案沒有 `IDesignTimeDbContextFactory`，須經 `Program.cs` 的 host 建置取得 DbContext 設定；連線字串在建置時為 null 是否可行屬未驗證假設] → 斷言 3 於實作階段實測；失敗時改在 build stage 傳 dummy 環境變數，不改程式碼。
- [經 nginx 後所有請求來自 nginx 的 IP，限流若依來源 IP 分區會共用同一個桶；未設定 `X-Forwarded-*`／`ForwardedHeaders`] → 與 dev 的 Vite proxy 行為相同（行為對等），來源 IP 轉送列為 Non-Goal，留給 `k8s-local-manifests` 決定。
- [api build context 為專案根目錄，會帶入 `web/` 內容使 context 變大] → 根目錄 `.dockerignore` 同時排除 `web/`（api 建置不需要）。
- [`appsettings.Development.json` 被誤打進映像] → `.dockerignore` 排除，並在驗證腳本加斷言：runtime 映像內不存在該檔。
- [web `npm ci` 的建置時間與 node 版本差異] → 沿用開發映像相同的 `node:22-alpine`，以 `package-lock.json` 鎖定相依。

## Migration Plan

無資料遷移。新增檔案不影響既有開發流程；回滾 = 刪除 `deploy/` 與新增的文件、腳本。

## Open Questions

- demo 環境最終用 `Development`（有 seeder 與 Swagger）或 `Production`（無初始 Admin）：已決定留給 `k8s-local-manifests`，映像本身預設 `Production`，不影響本 change。
