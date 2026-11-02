# 部署前置條件（2026-11-02）

適用於任何把 api 部署到本機開發以外環境的提案，包含 K8s 與 VPS（compose）。目前 repo 沒有 `k8s/`、production compose 或部署 change，以下條件都尚未完成。

## 必要條件

提案須將下列項目列為 task 與 AC；缺少任一項不得進入實作。

### 1. 限制健康檢查端點的來源

`/health/ready` 匿名且不限流，大量併發請求可能佔滿 DB 連線池，讓業務請求排隊或逾時。2 秒逾時只限制單次檢查，不限制頻率或併發。理由與取捨見 `api-health-checks` 的 `design.md` Risks。

- 可信來源：部署平台的探針（K8s 為 kubelet，從節點發出）與維運人員。其他叢集內工作負載不因位於叢集內就視為可信。
- 以入口控制、NetworkPolicy 或等效機制，只讓可信來源存取 `/health/live` 與 `/health/ready`。
- AC 須實測：探針來源可達且 Pod 能 Ready；維運來源可達；不可信 workload 與外部來源不可達。
- 完成前，不得讓 api 暴露給不受信任的網路或工作負載。

實測時注意：

- NetworkPolicy 是否生效取決於 CNI，Docker Desktop 尚未確認；規則也必須放行探針來源。
- K8s 的 `NodePort` 與 compose 未綁定 `127.0.0.1` 的 port 發布，都會繞過「僅內部可達」的假設。目前 `docker-compose.yml` 的 `API_HOST_PORT` 就沒有綁定，不符合此條件。
- VPS 綁定 `127.0.0.1` 不能取代實測，反向代理若轉送這兩個路徑，外部來源仍可達。

### 2. 完成例外日誌遮蔽

`GlobalExceptionHandler` 記錄完整例外且沒有遮蔽。`api-health-checks` 取得的是限期例外，期限為正式部署前。待辦見 `docs/exception-log-masking-todo.md`。

此項不需要部署環境，可以先於部署 change 完成。

## VPS 部署的已知限制（2026-10-17 記錄，開提案時須重新確認）

- 主機與其他專案共用，可用記憶體約 858MB，跑不動 K8s 控制平面，因此以 compose 部署。
- 80／443 已由既有的反向代理佔用，HTTPS 沿用它，不另外安裝。
- 規劃方向：各容器設記憶體上限；api 只綁定 `127.0.0.1`；映像在 CI 建置，不在 VPS 上建置；不部署 Seq，須先確認 api 在沒有 Seq 時可正常運作。
- 規劃拆成兩個 change：① production 映像、production compose、首位 Admin 建立方式；② CD 與維運手冊（反向代理、備份）。

主機位址與登入資訊不寫入版控。
