# 部署前置條件（2026-11-02）

適用於任何把 api 部署到本機開發以外環境的提案，包含 K8s 與 VPS（compose）。本文件 2026-11-02 建立時 repo 尚無 `k8s/` 與 production 映像；至 2026-11-12，`k8s/`（`k8s-local-manifests`）、`deploy/`（production 映像與 `docker-compose.verify.yml`）已存在，本機 K8s 部分的條件已逐項實測（狀態見各小節）。VPS（compose）線尚未實測。另見 `docs/review/production-readiness-report-2026-11-12.md` 的上線最低必要條件。

## 必要條件

提案須將下列項目列為 task 與 AC；缺少任一項不得進入實作。

### 1. 限制健康檢查端點的來源

`/health/ready` 匿名且不限流，每次探測建立一條不經連線池的實體連線，大量併發請求可能耗盡 PostgreSQL 的 `max_connections`，讓業務請求連不上 DB。2 秒逾時只限制單次檢查，不限制頻率或併發。理由與取捨見 `api-health-checks` 的 `design.md` Risks。

- 可信來源：部署平台的探針（K8s 為 kubelet，從節點發出）與維運人員。其他叢集內工作負載不因位於叢集內就視為可信。
- 以入口控制、NetworkPolicy 或等效機制，只讓可信來源存取 `/health/live` 與 `/health/ready`。
- AC 須實測：探針來源可達且 Pod 能 Ready；維運來源可達；不可信 workload 與外部來源不可達。
- 完成前，不得讓 api 暴露給不受信任的網路或工作負載。

本機實測狀態（`k8s-local-manifests`，2026-11-10，Docker Desktop kubeadm）：

- 已驗證：api 只有 ClusterIP，經 web NodePort 取不到 `/health/*`（nginx 只轉發 `/api/`），`kubectl port-forward` 可取得 `/health/live`，探針通過使 Pod Ready（`scripts/verify-k8s-manifests.sh` 斷言 8、9）。
- **已驗證（2026-11-12）**：不可信 workload 不可達。Docker Desktop 的 CNI 不執行 NetworkPolicy，改在 kind＋Calico 叢集（`VERIFY_CONTEXT=kind-npdemo bash scripts/verify-k8s-manifests.sh`）驗證：斷言 8c 通過，不可信 Pod 連不到 `api:8080`、`redis:6379`、`db:5432`，`app=web` 的 Pod 連得到 `api:8080`；全部 15 項斷言通過，探針不受 NetworkPolicy 影響。本機 K8s 部分的上線 gate 因此解除；VPS（compose）線仍須依本項必要條件自行實測。

實測時注意：

- NetworkPolicy 是否生效取決於 CNI，Docker Desktop 已實測**不執行**；規則也必須放行探針來源。
- K8s 的 `NodePort` 與 compose 未綁定 `127.0.0.1` 的 port 發布，都會繞過「僅內部可達」的假設。目前 `docker-compose.yml` 的 `API_HOST_PORT` 就沒有綁定，不符合此條件。
- VPS 綁定 `127.0.0.1` 不能取代實測，反向代理若轉送這兩個路徑，外部來源仍可達。

### 2. 完成例外日誌遮蔽（已完成）

`exception-log-masking` 已完成：Serilog 管線最外層的 `ExceptionMaskingSink` 遮蔽所有日誌來源（含 `GlobalExceptionHandler`、背景服務、框架來源與啟動階段的 bootstrap logger）帶出的例外文字，再轉給 Console、Seq 與測試用 sink。`api-health-checks` 的限期例外因此解除。

已知限制（部署前須知）：

- 只認指定格式：`password`／`passwd`／`pwd`、`api-key`／`secret`／`token` 後接 `=` 或 `:` 的值、`Bearer` token、JWT、`scheme://user:password@host`。沒有 key 的裸密碼不會被遮蔽；啟動失敗時 Program.cs 重擲的例外會由 .NET 執行階段以 `Unhandled exception.` 寫到 stderr，該文字不經 Serilog，不受遮蔽保護；大括號包住的連線字串值、雙寫引號跳脫、值中間夾引號的無引號值、簽章為空的 JWT 與緊接在 `_`／`-` 之後的 JWT、JSON 字串內再跳脫的 `\"password\":\"x\"`、無引號且含空白或逗號的 `secret`／`token`／`api-key` 值、含未編碼 `@` 的 URL 密碼也不會被完整遮蔽。
- 只處理例外文字；日誌訊息範本與屬性值不在範圍內。
- 已進入 Seq 的歷史日誌不會回溯清理。

細節見 `openspec/changes/exception-log-masking/design.md` 決策 3。

### 3. 設定探針參數

- 探針路徑須與 `/health/live`、`/health/ready` 完全相同，結尾不加斜線。加了斜線雖然仍命中端點，但不算探測路徑：設定 HTTPS 埠時會回 307，日誌也不會降級。
- `/health/ready` 的 2 秒上限不含 DB 主機名稱解析，DNS 無回應時實測可達 10 到 12 秒，所以探針的逾時不要假設回應一定在 2 秒內。探針逾時視同失敗，就緒判定結果不受影響。

## VPS 部署的已知限制（2026-10-17 記錄，開提案時須重新確認）

- 主機與其他專案共用，可用記憶體約 858MB，跑不動 K8s 控制平面，因此以 compose 部署。
- 80／443 已由既有的反向代理佔用，HTTPS 沿用它，不另外安裝。
- 規劃方向：各容器設記憶體上限；api 只綁定 `127.0.0.1`；映像在 CI 建置，不在 VPS 上建置；不部署 Seq，須先確認 api 在沒有 Seq 時可正常運作。
- 規劃拆成兩個 change：① production 映像、production compose、首位 Admin 建立方式（已由 `production-admin-bootstrap` 處理）；② CD 與維運手冊（反向代理、備份）。

主機位址與登入資訊不寫入版控。

## 含破壞性欄位改名的 migration（2026-11-08，time-field-naming-rule）

`RenameTokenExpiresAtToExpiresAtUtc` 把 `RefreshTokens.ExpiresAt`、`PasswordResetTokens.ExpiresAt` 改名為 `ExpiresAtUtc`。與先前加欄位的 migration 不同，新舊版程式碼不能共存：舊版讀 `ExpiresAt`，遷移後查詢失敗；新版讀 `ExpiresAtUtc`，遷移前查詢失敗。

- 順序：停止舊版 api（或不接受流量）→ 容器內 `dotnet ef database update` → 啟動新版 api。
- 回滾：停止新版 → `dotnet ef database update 20261014081305_AddOrderCreatedAt` → 啟動舊版。
- 影響：停機期間登入、換發 Token、忘記密碼不可用；下單、查詢等不碰這兩張表的流程不受影響。`RenameColumn` 只動 PostgreSQL catalog，不重寫資料表，既有 Token 保留、使用者不必重新登入。
