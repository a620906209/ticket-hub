# Design

## Context

現況（已查證）：
- `deploy/nginx/default.conf`：`listen 8080` 純 HTTP；`/api/` 代理到 `http://api:8080`，`Host $host`、`X-Forwarded-For $remote_addr`（覆寫）、`X-Forwarded-Proto $scheme`；`/assets/` 與 `/` 各自用 `add_header Cache-Control`。沒有任何安全標頭、壓縮。
- API 管線（`Program.cs`）有 `UseForwardedHeaders` 與條件式 `UseHttpsRedirection`，沒有 `UseHsts`、沒有 CORS；`appsettings.json` 的 `AllowedHosts` 為 `*`。
- K8s：web 以 NodePort 30080 對外（`verify-k8s-manifests.sh` 驗證時覆寫為 `VERIFY_NODEPORT`＝30081，故 tasks 的本機驗證與瀏覽器檢查使用 30081）；API 探針為 `httpGet`（無 `Host`，kubelet 預設以 Pod IP 當 Host）；`projectc-config` ConfigMap 目前沒有 `AllowedHosts`。
- VPS 的 80／443 由既有反向代理佔用，HTTPS 沿用它（`docs/todo/2026-11-02-deployment-prerequisites.md`）；本機 kind／Docker Desktop 只有 HTTP。
- 前端無外部 CDN／字型／腳本；15 個 `.vue` 使用行內 `style`（含 Element Plus 動態樣式）。
- repo 內沒有 production compose（只有 dev compose 與 `deploy/docker-compose.verify.yml`），「正式設定」在本 change 指 K8s ConfigMap 與部署前提文件。
- 現行 `docs/review/TODO.md` H-3、L-5、L-8 動機見 proposal.md。

## Goals / Non-Goals

**Goals**
- 邊界統一處理安全標頭、HSTS、gzip；API 只負責 Host 驗證。
- 每項行為有可重現的驗證（容器內 curl 腳本、API 整合測試、真實瀏覽器）。

**Non-Goals**
- TLS 終結、Ingress／cert-manager、WAF、限流調整、dev compose 映像釘版。
- 修改 `X-Forwarded-Proto` 轉發邏輯（見決策 3 的觀察）。

## Decisions

### 決策 1：標頭集中在 nginx，以 snippet include 到每個 location
`add_header` 只要某個 location 自己有 `add_header`，就不會繼承上層（server／http）的 `add_header`。現有 `/assets/`、`/` 都有 `add_header Cache-Control`，若把安全標頭寫在 server 層，這兩處會靜默遺漏。作法：新增 `deploy/nginx/security-headers.conf`（內含全部 `add_header ... always`），在每個 location 以 `include` 引入（含 `/api/`、`/assets/`、`/`），Dockerfile 一併 COPY 到 `/etc/nginx/snippets/security-headers.conf`（不可放 `/etc/nginx/conf.d/`，否則會被 `include conf.d/*.conf` 當 http 層設定載入而報錯）。`always` 使 4xx／5xx 也帶標頭。保證範圍為落在 location 內的回應；nginx 在 server 層直接產生的錯誤（畸形請求 400、414 等）不經 location，不帶標頭，這類回應沒有可被瀏覽器渲染的內容，接受。
替代方案：server 層一處定義＋各 location 重複 Cache-Control——不採用，因為任一 location 日後新增 `add_header` 就會再次遺漏；include 方式遺漏時是「少 include 一行」，且由驗證腳本逐路徑斷言。
API 不自行送這些標頭，避免與 nginx 重複（ESH-003「只出現一次」）。

### 決策 2：CSP 內容與 `style-src 'unsafe-inline'`
採 Grill 確認的字串（見 ESH-005）直接 enforce。`script-src 'self'` 不含 inline／eval：`index.html` 只有 `type="module"` 外部腳本。`style-src` 必須含 `'unsafe-inline'`，因為 15 個 `.vue` 與 Element Plus 以 `style` 屬性設定樣式；風險是 CSS 注入，但 `script-src` 已限縮，XSS 的主要利用路徑（腳本執行）仍被擋。`img-src` 含 `data:`／`blob:`（內嵌小圖、QR 圖）；`connect-src 'self'` 因 API 同源。
相機：`getUserMedia` 不受 CSP 管，由 `Permissions-Policy: camera=(self)` 控制。若核銷頁的掃描函式庫使用 `blob:` Worker，`worker-src` 會回退到 `script-src 'self'` 而被擋——已查 `web/src` 沒有 `new Worker`，但函式庫內部行為須由 ESH-006 的真實瀏覽器驗證確認；若發現違規，修正本決策與 ESH-005（同步調整 CSP 字串與測試），不以放寬 `script-src` 處理。
直接 enforce 而不先 Report-Only：沒有蒐集端點可接報告，Report-Only 在本專案沒有觀察價值；以真實瀏覽器驗證取代。

### 決策 3：HSTS 以入站 `X-Forwarded-Proto` 判斷，只由 nginx 送
nginx 本身只聽 HTTP，`$scheme` 恆為 `http`，不能用來判斷。用 `map $http_x_forwarded_proto $hsts_value { default ""; https "max-age=15552000"; }`，`add_header Strict-Transport-Security $hsts_value always;`——nginx 對空值的 `add_header` 不輸出該標頭。`max-age` 180 天，不加 `includeSubDomains`／`preload`（網域下可能有其他專案，preload 難以回收）。
偽造風險：直連 nginx 的用戶端可自帶 `X-Forwarded-Proto: https`，結果只是在 HTTP 回應上多一個瀏覽器會忽略的 HSTS 標頭，無害。
**觀察（不在本 change 修改）**：`/api/` 區塊以 `proxy_set_header X-Forwarded-Proto $scheme` 轉發，會把前方 TLS 代理送來的 `https` 覆寫成 `http`，API 因此看不到 HTTPS。目前 API 未設定 HTTPS 轉址埠，無實際影響；若日後需要 API 感知 HTTPS，須另開 change 處理（並注意信任邊界）。
API 不加 `UseHsts`：與 nginx 重複，且 `UseHsts` 在 API 看到的協定恆為 `http` 時永遠不會輸出，沒有意義。此為與 TODO 建議（API `UseHsts`）的偏離，理由同上。

### 決策 4：`AllowedHosts` 由部署設定注入；nginx 保留原始 Host
框架的 HostFiltering 讀 `AllowedHosts`，不需要改程式碼（`appsettings.json` 維持 `*` 作為開發預設，HHV-003）。nginx 以 `proxy_set_header Host $host` 轉發，所以 API 看到的是使用者的對外網域，清單必須包含它（HHV-007／008）；本機驗證用 `localhost`；以 `127.0.0.1` 或節點 IP 經 nginx 存取時 `/api/` 會得 400（靜態頁正常）。取捨：不把 `127.0.0.1` 加入清單，維持清單最小，文件與驗證一律用 `localhost`。ConfigMap 加 `AllowedHosts: "localhost;api"`（以分號分隔），並在註解與部署前提文件說明上線前改為實際網域。
探針：kubelet 的 `httpGet` 預設以 Pod IP 當 Host，會被 HostFiltering 擋成 400，導致 Pod 永遠 NotReady。三個探針都加 `httpHeaders: [{name: Host, value: api}]`。`api` 在清單內，作為叢集內服務名稱。
HostFiltering 在 ASP.NET Core 的啟動篩選器階段執行，早於本專案管線，因此探針路徑也受限制（HHV-001）。
替代方案：把 `/health/*` 排除在 Host 驗證外——不採用，需要自訂 middleware，且為了探針放寬驗證等於留下旁路。

### 決策 5：CORS 不加 middleware
前端與 API 都在同一個 nginx 來源，瀏覽器不會發跨來源請求；未註冊 CORS 時框架不輸出任何 `Access-Control-*`，等於預設拒絕。加一個「空白」CORS policy 只會製造日後被誤放寬的入口。決策記錄在 `docs/api-conventions.md`，並以整合測試固定（ESH-013）。

### 決策 6：gzip 在 nginx，不在 API
`gzip on; gzip_types text/css text/javascript application/javascript application/json image/svg+xml text/plain; gzip_min_length 1024; gzip_vary on; gzip_proxied any;`（`text/html` 為預設類型；`.js` 的 Content-Type 依映像內 `mime.types` 可能是 `text/javascript` 或 `application/javascript`，兩者都列，並以 `nginx -T` 核對）。`gzip_min_length` 只依回應的 `Content-Length` 判斷；上游無 `Content-Length` 的 chunked 回應不受此下限限制而會被壓縮，因此 ESH-011 的小型回應下限只對具 `Content-Length` 的回應保證，`/api/` 小型 JSON 若走 chunked 被壓縮屬可接受（內容類型白名單仍生效）。API 不開 `UseResponseCompression`，避免雙重壓縮。BREACH：此攻擊需要「回應反射攻擊者可控內容，且同一回應含祕密」；本專案 JSON 回應不反射請求內容，登入回應的 token 位於獨立回應，風險低，接受；日後新增反射輸入的端點時須重新評估。

### 決策 7：digest 釘版範圍與寫法
範圍：兩份 Dockerfile 的所有外部 `FROM`、`k8s/` 的 postgres／redis／seq。寫成 `name:tag@sha256:...`，tag 保留供閱讀與 Renovate／Dependabot 日後解析；Docker 依 digest 取內容、忽略 tag。digest 取多架構索引的 digest（`docker buildx imagetools inspect <name:tag>` 輸出的 `Digest:`），否則在不同架構（Apple Silicon、CI）拉不到。dev compose 映像不動。
更新步驟寫在 `docs/` 的獨立文件（`docs/2026-11-13-image-pinning.md`，依文件規則帶日期前綴），並由 `scripts/verify-image-pinning.sh` 靜態檢查格式。
代價：基底映像的安全更新不再自動進來，須人工更新 digest；這與「變更必須可審查」的取捨一致，風險與緩解見下。

### 決策 8：驗證方式
- 邊界行為：`scripts/verify-edge-headers.sh` 以容器內 curl 斷言（逐路徑檢查標頭存在且只出現一次、HSTS 條件、gzip（JS 與 CSS 各驗一個、小型、無 Accept-Encoding）、`nginx -t`／`nginx -T`）。nginx 啟動需要可解析的 `api`，腳本建立暫時 Docker network，並以同一個 `projectc/web:local` 映像加掛簡易設定、設網路別名 `api` 當上游替身，不引入新映像；`/api/` 的上游 4xx／5xx 回應也由替身提供。
- Host 驗證與無 CORS／無 HSTS：`tests/ProjectC.WebApi.Tests` 的整合測試，以 `WithWebHostBuilder` 覆寫 `AllowedHosts`。這是對框架既有行為的特性測試，RED 以「還原驗證（mutate）」取得，見 tasks.md。
- K8s：擴充 `scripts/verify-k8s-manifests.sh`，斷言 `Host: evil` 回 400 與 Pod Ready（HHV-004～006）。
- CSP：真實瀏覽器驗證（ESH-006），數字與狀態以頁面文字與 console 核對。

### 核准例外（使用者已於 2026-11-13 核准）
- **適用 AC**：ESH-006。
- **偏離理由**：CSP 違規只在真實瀏覽器的渲染與腳本執行時發生，容器內 curl 與單元測試無法重現；專案尚無無頭瀏覽器測試基礎設施，為單一斷言新增會超出本 change 範圍。
- **替代驗證**：tasks.md 6.1（claude-in-chrome 腳本化操作），須滿足有效觀測：(a) 先在頁面主動 `console.log` 一則已知訊息並讀回，證明 console 擷取有效；(b) 每頁先以頁面文字／元素斷言已載入非空白（買家端活動列表、後台列表、核銷頁），並以 `Content-Security-Policy` 回應標頭確認該頁實際帶 CSP；(c) 核銷頁以 stub 的 `getUserMedia` 被呼叫為證據證明相機流程確實觸發；(d) 以 `read_console_messages` 的 pattern 過濾 `Content Security Policy|Refused to` 為 0 筆。
- **解除條件**：專案引入無頭瀏覽器端對端測試後，改為自動化並移除此例外。
- 範圍僅限 ESH-006；其餘 AC 皆須有自動化測試或腳本。

### 安全確認（對照 CLAUDE.md「安全確認問題」）
- 輸入驗證：Host 標頭為外部輸入，由框架 HostFiltering 在請求管線最前驗證；無字串拼接進 SQL／shell。
- 資料庫：不涉及。
- 權限：Host 驗證與標頭不涉及授權；不可能因此提升權限，未授權者能觸發的只是被 400 拒絕。
- 前端：CSP 限縮腳本來源以降低 XSS；`/api/` 呼叫沿用既有 Auth Header 攔截器，本 change 不改。

### 跨模組慣例對照（對照 `docs/api-conventions.md`）
- §1 認證與授權：不適用，本 change 不新增或修改認證、授權。
- §2 錯誤格式：**偏離（有意）**。HostFiltering 對不合法 Host 回 400，回應為框架預設（無 `ProblemDetails` 本文）。理由：該請求在進入 MVC 之前被框架拒絕，沒有合法用戶端會觸發；自訂為 `ProblemDetails` 需要攔截框架內部篩選器，成本高於效益。影響範圍：只影響帶偽造 Host 的請求；須在 `docs/api-conventions.md` 「框架層錯誤」小節補記一行。
- §3 資料命名、列表分頁：不適用。
- 同步更新：`docs/api-conventions.md`（HostFiltering 400 的格式、同源／無 CORS 決策）。

## Risks / Trade-offs

- [CSP 擋到未預期的頁面功能] → ESH-006 以真實瀏覽器逐頁驗證；發現違規時調整 CSP（決策 2），不放寬 `script-src`。
- [`style-src 'unsafe-inline'` 保留 CSS 注入面] → 接受；`script-src` 嚴格，且前端禁用 `v-html` 渲染未消毒輸入（既有規範）。
- [上線忘了把 `AllowedHosts` 改為實際網域，導致全部請求 400] → 部署前提文件列為必填；這是 fail-closed 而非靜默放行，驗收方式為 TODO H-3 的 curl。
- [探針 Host 與清單不一致造成 Pod 不就緒] → 探針 Host 固定為 `api` 且 `api` 為清單必備項；K8s 驗證腳本涵蓋 Ready。
- [HSTS 一旦被瀏覽器記住就難撤回] → `max-age` 取 180 天、不含子網域與 preload；真正啟用 HTTPS 前不會輸出（需 `X-Forwarded-Proto: https`）。
- [digest 釘版使安全修補需人工更新] → 文件記錄更新步驟與驗證指令；`verify-image-pinning.sh` 防止遺漏釘版，不防止過舊。
- [nginx location 日後新增卻漏 include] → 驗證腳本逐路徑斷言（含 `/assets/` 與 404）。

## Migration Plan

1. 先更新 K8s ConfigMap（`AllowedHosts`）與探針 `Host`，再部署新的 api／web 映像；順序顛倒時舊版探針會因 Host 限制失敗。
2. 上線前將 `AllowedHosts` 改為實際對外網域。
3. 回滾：還原 ConfigMap 的 `AllowedHosts`（或設回 `*`）與 nginx 設定即可，無資料遷移。

## Open Questions

- 無會影響 spec、做法或 task 的待決事項。核銷頁掃描函式庫在 CSP 下的實際行為列為 ESH-006 的驗證項目，不是未決決策。
