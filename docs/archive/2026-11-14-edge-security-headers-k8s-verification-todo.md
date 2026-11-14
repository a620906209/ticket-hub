# edge-security-headers 叢集補驗待辦

建立於 2026-11-14。`edge-security-headers` 實作時，docker-desktop 的 K8s 為 kind 模式，NodePort 30081 主機連不到（HTTP 000），使用者決定接受下列項目標 UNVERIFIED、之後補驗、不轉發 30081。change 歸檔前須補完或由使用者再次明確接受。

## 待補驗項目

### 1. `scripts/verify-k8s-manifests.sh` 以 NodePort 為前提的斷言（對應 tasks 1.2）

- 3a、3b、4、5、8a、10d：既有斷言，皆經 `localhost:30081`，實作時 RED 與 GREEN 同樣為 HTTP 000。
- 11d（HHV-007／008）：經 web，`Host: localhost` → 2xx、`Host: evil.example` → 400。為本 change 的行為斷言，優先補驗。
- 已驗證（不需補驗）：11a、11b、11c（經 port-forward，不依賴 NodePort）。

### 2. 釘版後叢集全 PASS（對應 tasks 4.2、CIP-004）

- 釘版後 `bash scripts/verify-k8s-manifests.sh` 須全 PASS（含上列 1 的全部斷言）。
- 順帶確認釘版後 web 映像（含 nginx 安全標頭設定）經 NodePort 的行為。

### 3. 真實瀏覽器 CSP 驗證（對應 tasks 6.1、ESH-006）

以 claude-in-chrome 經邊界 nginx（`KEEP_UP=1` 保留環境後的 `http://localhost:30081`，必須用 `localhost`，不可用 `127.0.0.1`，否則 `/api/` 得 400）操作買家端、後台、核銷頁，須滿足 `openspec/changes/edge-security-headers/design.md`「核准例外」的有效觀測要求：

- (a) 先在頁面主動 `console.log` 已知訊息並讀回，證明 console 擷取有效。
- (b) 每頁先以頁面文字／元素斷言已載入非空白（買家端活動列表、後台列表、核銷頁），並以 `Content-Security-Policy` 回應標頭確認該頁實際帶 CSP。
- (c) 核銷頁以 stub 的 `getUserMedia` 被呼叫為證據，證明相機流程確實觸發。
- (d) 以 `read_console_messages` 的 pattern 過濾 `Content Security Policy|Refused to`，須為 0 筆。

發現違規時依 design.md 決策 2 調整 CSP，並同步 ESH-005 與 `scripts/verify-edge-headers.sh`。

## 前置條件

1. 主機可連 NodePort 的環境，擇一：
   - kind 叢集建立時設 `extraPortMappings`（containerPort 30081 → hostPort 30081），見 `docs/2026-11-12-k8s-guide.md` 的 kind 建立步驟；或
   - 改以 `kubectl port-forward` 暴露 web，並調整驗證腳本的目標位址（腳本預設為 `localhost:30081`）。
2. 映像匯入叢集（kind 與 Docker Desktop 不共用 image store）。先依 `deploy/api.Dockerfile`、`deploy/web.Dockerfile` 開頭註解建置三個 `:local` 映像，再匯入節點：

   ```bash
   for i in projectc/api:local projectc/api-migrator:local projectc/web:local; do
     docker save "$i" | docker exec -i desktop-control-plane ctr -n k8s.io images import --all-platforms -
   done
   docker exec desktop-control-plane crictl images | grep projectc
   ```

   節點容器名稱以 `docker ps` 為準；使用具名 kind 叢集時可改用 `kind load docker-image <映像> --name <叢集名>`。
3. 腳本只允許 `docker-desktop` 或 `kind-npdemo` context。

## 完成後回填位置

`openspec/changes/edge-security-headers/tasks.md`：

- 1.2「紀錄（原文）」：貼 `verify-k8s-manifests.sh` 全斷言 PASS 原文，移除 UNVERIFIED 敘述後勾選。
- 4.2「紀錄（原文）」：貼叢集全 PASS（CIP-004）原文後勾選。
- 6.1「紀錄」：貼 (a)–(d) 的頁面文字與 console 原文後勾選。

三項補完後，確認 `docs/review/TODO.md` 的 H-3、L-5 狀態，並依 CLAUDE.md 將本檔移至 `docs/archive/`（`bash scripts/archive-todo.sh <檔名>`，是否完成由使用者決定）。
