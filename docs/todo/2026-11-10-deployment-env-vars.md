# 部署環境變數分類（2026-11-10）

來源：`docker-compose.yml`（全部服務：`db`、`seq`、`redis`、`api`、`web`、`k6`）與 `.env.example`。供 `k8s-local-manifests` 作為 Secret／ConfigMap 的輸入；本文件只分類，不產生 manifest。對應 `docs/todo/2026-10-28-k8s-todo.md` 的「2. Secret 與 ConfigMap 分流」與「實作範圍草案」中的 production 映像（由 `production-images` change 完成）。

「容器鍵名」是容器內實際讀取的環境變數名；「`.env.example` 名稱」是 `docker-compose.yml` 以 `${...}` 插值的來源名（compose 把後者對映成前者）。K8s 的 Secret／ConfigMap 直接使用「容器鍵名」，不需要 `.env.example` 名稱。

## 一、機敏（日後 K8s Secret）

| 容器鍵名 | `.env.example` 名稱 | 服務 | 歸類理由 |
| --- | --- | --- | --- |
| `POSTGRES_PASSWORD` | `POSTGRES_PASSWORD` | db | 資料庫密碼 |
| `ConnectionStrings__DefaultConnection` | （由 `POSTGRES_USER`／`POSTGRES_PASSWORD`／`POSTGRES_DB` 組出） | api | 字串內含密碼，整條視為機敏 |
| `Jwt__SigningKey` | `JWT_SIGNING_KEY` | api | 可偽造任何使用者的 access token |
| `TicketSigning__SigningKey` | `TICKET_SIGNING_KEY` | api | 可偽造電子票券 QR Code 簽章 |
| `AdminBootstrap__Password` | `ADMIN_BOOTSTRAP_PASSWORD` | api | 首位 Admin 密碼（至少 8 碼、含英文字母與數字）；僅在資料庫無 Admin 時使用。範例佔位值 `CHANGE-ME-REQUIRED` 必須替換；首位 Admin 建立後應從 Secret 移除並連同 `AdminBootstrap__Email` 一併移除（只移除其一會因「只設定其一」啟動失敗），本機制不提供密碼輪替（Secret 殘留時，Admin 全被刪除後重啟會依該密碼重建） |

注意事項：

- migrator 映像目前以 `--connection "<連線字串>"` 命令列參數接收連線字串，密碼會出現在行程參數與 `kubectl describe`／容器 inspect 輸出。`k8s-local-manifests`（2026-11-10）的處理：不改 migrator，Job 以 `secretKeyRef` 把 `ConnectionStrings__DefaultConnection` 注入環境變數，`args` 寫 `["--connection", "$(ConnectionStrings__DefaultConnection)"]`（K8s 於容器啟動時展開 `$(VAR)`），密碼因此不出現在 manifest 與 `kubectl describe`。已知限制：容器內的行程參數是展開後的值，能進入該容器者可見；要消除須讓 migrator 直接讀環境變數（另開 change）。

## 二、一般設定（日後 K8s ConfigMap）

| 容器鍵名 | `.env.example` 名稱 | 服務 | 歸類理由 |
| --- | --- | --- | --- |
| `Jwt__Issuer` | `JWT_ISSUER` | api | 公開識別字串，非機敏 |
| `Jwt__Audience` | `JWT_AUDIENCE` | api | 同上 |
| `Jwt__AccessTokenExpirationMinutes` | `JWT_ACCESS_TOKEN_EXPIRATION_MINUTES` | api | 數值設定 |
| `Auth__RefreshTokenExpirationDays` | `AUTH_REFRESH_TOKEN_EXPIRATION_DAYS` | api | 數值設定 |
| `Auth__PasswordResetTokenExpirationMinutes` | `AUTH_PASSWORD_RESET_TOKEN_EXPIRATION_MINUTES` | api | 數值設定 |
| `Seq__ServerUrl` | （compose 寫死 `http://seq:5341`） | api | 叢集內 Service 位址，無憑證 |
| `ConnectionStrings__Redis` | （compose 寫死 `redis:6379`） | api | 叢集內 Service 位址，Redis 未設密碼 |
| `AdminBootstrap__Email` | `ADMIN_BOOTSTRAP_EMAIL` | api | 首位 Admin 帳號（非密碼）；與 `AdminBootstrap__Password` 須同時設定，皆未設定則略過 bootstrap；非 `Development` 環境執行 |
| `Captcha__VerificationMode` | （不設定，預設 `Enforced`） | api | 驗證碼核對模式；`AcceptAny` 僅供非 Production 自動化驗證，Production 設定會使啟動失敗。部署預設不設，Production ConfigMap 不得出現此鍵 |
| `POSTGRES_USER` | `POSTGRES_USER` | db | 使用者名稱，db 必需，日後 Postgres Deployment 也需要 |
| `POSTGRES_DB` | `POSTGRES_DB` | db | 資料庫名稱，同上 |
| `ACCEPT_EULA` | （compose 寫死 `Y`） | seq | Seq 授權同意，缺少則無法啟動 |
| `SEQ_FIRSTRUN_NOAUTHENTICATION` | （compose 寫死 `True`） | seq | 新版 Seq 第一次啟動必須二選一（見 `docker-compose.yml` seq 註解）。**僅限本機展示使用**：它讓 Seq 完全不需驗證，任何能連到 Seq 的人都能讀寫日誌；不得用於對外或共享網路的部署，日後若 Seq 暴露到本機 K8s 以外須改用 `SEQ_FIRSTRUN_ADMINPASSWORD`（屆時該值歸 Secret）。 |

## 三、執行環境（`ASPNETCORE_*`）

| 容器鍵名 | 服務 | production 映像預設 | 說明 |
| --- | --- | --- | --- |
| `ASPNETCORE_ENVIRONMENT` | api | `Production`（映像 `ENV`） | 開發 compose 設 `Development`（會執行展示用 seeder 與 OpenAPI／SwaggerUI）；K8s 已改 `Production`，首位 Admin 由 `AdminBootstrap__*` 建立，不建立 Organizer（Admin 核准會員申請後主辦方才出現）。 |
| `ASPNETCORE_URLS` | api | `http://+:8080`（映像 `ENV`） | 容器內監聽 8080，HTTP-only，TLS 不在本階段。 |

## 四、本機開發專用，不進部署分類

| 項目 | 排除理由 |
| --- | --- |
| `Testcontainers__ComposeNetworkName` | 容器內整合測試用，指向開發 compose 網路 |
| `TESTCONTAINERS_RYUK_DISABLED` | 同上（DooD 情境停用 Ryuk） |
| `API_HOST_PORT`、`DB_HOST_PORT`、`SEQ_HOST_PORT`、`WEB_HOST_PORT` | 只決定 host 對外 port；K8s 由 Service／port-forward 處理（`WEB_HOST_PORT` 未列於 `.env.example`，僅 compose 使用） |
| `PGADMIN_EMAIL`、`PGADMIN_PASSWORD`、`PGADMIN_HOST_PORT` | 本機 pgAdmin（`docker-compose.override.yml.example`），不屬於部署 |
| `k6` 服務 | 壓測工具，無環境變數，不部署 |

## 五、映像與基底版本（查證日期 2026-11-10）

- `mcr.microsoft.com/dotnet/sdk:10.0.403`、`aspnet:10.0.14`、`runtime-deps:10.0.14`：mcr.microsoft.com 的 tag 清單查證。
- `node:22.22-alpine`、`nginxinc/nginx-unprivileged:1.30.6-alpine`（stable 1.30 線）：Docker Hub tag 清單查證。
- 皆未釘 digest；開發用 `Dockerfile`／`web/Dockerfile` 仍使用浮動 tag（`sdk:10.0`、`node:22-alpine`），兩者主版本一致。
- nginx 啟動時必須解析得到上游主機名 `api`；日後 K8s Service 必須命名 `api`。
- `migrator` 為 `linux-x64` 自包含執行檔，ARM 主機無法執行（本專案開發機為 amd64）。
