# 文件審查（Documentation Audit）— 2026-11-12

> 對象：`master` @ `9c5f5e2`。只讀不改。
> 判定標準：文件敘述是否與現行程式碼／設定一致；是否存在上線所需文件。

---

## 1. 文件盤點表

| 路徑 | 性質 | 最後對應事實 | 狀態 |
|---|---|---|---|
| `README.md` | 產品總覽、Phase 進度、已知限制、GIF | §4 Phase 1–3 全完成；已知限制誠實（P95 冷啟動、Mock、Admin 僅後端） | ✅ 準確 |
| `web/README.md` | 前端說明 | L49-52「已知限制」 | ❌ 過時（見 §2-1） |
| `CLAUDE.md`（專案） | AI 協作規則、架構規範、流程 | 多條豁免條款；graphify 敘述 | ⚠️ 部分與現況不符（見 §4） |
| `~/.claude/CLAUDE.md`（全域） | 跨專案規則 | 提及 Laravel、token 預算 | ⚠️ 與專案衝突（見 §4） |
| `docs/project-scope.md` | 範圍、NFR、§8 待確認、§9 進度 | §5 P95 目標、§8 決策紀錄 | ✅ 準確且更新勤 |
| `docs/api-conventions.md` | ErrorType／Policy／Claim 慣例 | 與 `ErrorType.cs`、`ResultExtensions.cs:22-48` 對照一致 | ✅ 準確 |
| `docs/architecture-guide/index.html` | 學習型架構指南 | L84 釘 `ba835ff`；L419 引 `Program.cs:386` | ❌ 行號過時（見 §2-3） |
| `docs/load-test/*.md`、`*.json` | k6 報告 | P95 數據 | ✅ 準確（本機 WSL2 量測） |
| `docs/todo/2026-11-02-deployment-prerequisites.md` | 部署前置條件 | L3 開頭句 | ❌ 開頭過時（見 §2-2），後段已更新至 2026-11-12 |
| `docs/todo/`（其餘 3 檔） | 待辦 | — | ✅ 待辦性質 |
| `docs/archive/`（8 檔） | 封存審視 | `2026-11-10-deployment-env-vars.md` 分類 Secret/ConfigMap | ✅ |
| `openspec/specs/**`（26 capability） | 主 spec | `openspec validate --all` 32 passed；`--strict` 全數 warning（>500 字 requirement） | ⚠️ 格式警告；`ticket-redemption` Purpose 仍為佔位 |
| `openspec/changes/archive/` | 歷史 change | — | ✅ |
| `openspec/config.yaml` | TDD 規則 | 規則 3 N/A 條款 | ⚠️ 豁免條款 |
| `deploy/*.Dockerfile`、`deploy/nginx/default.conf` | 映像／代理 | 內嵌註解 | ✅ 註解準確但設定缺 TLS／轉送標頭 |
| `k8s/*.yaml`、`k8s/examples/` | 清單 | — | ✅ 與 manifest 一致 |
| `.env.example` | 開發環境範本 | 缺 `WEB_HOST_PORT` 等 | ❌ 不完整（見 §2-4） |
| `.security/vulnerability-allowlist.txt` | 接受風險 | ImageSharp 5 GHSA | ✅ |
| `scripts/*.sh` | 驗證腳本 | `check-doc-paths.sh` OK | ✅ |
| `.claude/agents/`、`.agents/agents/`、`.devin/rules/` | Agent 定義 | 三處並存 | ⚠️ 漂移風險 |
| `graphify-out/` | 知識圖譜（117 MB，ignored） | CLAUDE.md 稱 committed | ❌ 敘述不符（見 §2-5） |

---

## 2. 文件 vs 程式碼不一致清單

| # | 文件位置 | 文件說法 | 程式碼現況 | 嚴重度 |
|---|---|---|---|---|
| 1 | `web/README.md:51` | 「買家『我的訂單』列表/明細只有空狀態畫面，尚未串接查詢 API（後端還沒有買家專屬的訂單查詢端點）」 | `src/ProjectC.WebApi/Controllers/OrdersController.cs` 有 `GET /api/orders`；`src/ProjectC.Application/Orders/GetMyOrders*`、`GetMyOrderDetailHandler.cs` 存在；前端 `web/src/api/orders.ts`、`web/src/composables/usePendingOrderActions.ts` 已串接 | 🟡 [文件] |
| 2 | `docs/todo/2026-11-02-deployment-prerequisites.md:3` | 「目前 repo 沒有 `k8s/`、production compose 或部署 change，以下條件都尚未完成」 | `k8s/10-configmap.yaml` ～ `60-networkpolicy.yaml`、`deploy/docker-compose.verify.yml` 皆存在；同文件後段已記錄 2026-11-12 K8s 驗證通過 | 🟡 [文件] |
| 3 | `docs/architecture-guide/index.html:419` | 引用 `Program.cs:386` | `src/ProjectC.WebApi/Program.cs` 共 138 行（`wc -l`）；Program.cs 已於 2026-11-05 拆分（memory：webapi-composition-root-cleanup）。指南 L84 自承釘在 `ba835ff`，但未標示「行號可能過時」 | 🔵 [文件] |
| 4 | `.env.example` | 列出 `DB_HOST_PORT`、`API_HOST_PORT`、`SEQ_HOST_PORT` | `docker-compose.yml:136` 引用 `WEB_HOST_PORT`；`ProductionAdminBootstrapper` 需 `AdminBootstrap__*`；`Captcha__VerificationMode`、`MockPaymentGateway__AlwaysSucceed`、`MockEmailNotificationService__AlwaysSucceed`、各 `*RateLimiting__*` 皆未列 | 🟡 [文件] |
| 5 | `CLAUDE.md` graphify 段落 | 「A committed graphify-out/graph.json is rewritten whole on every build, so a large diff … is expected」 | `.gitignore:500` 忽略 `graphify-out/`；`git ls-files graphify-out` 為 0 | 🔵 [文件] |
| 6 | `CLAUDE.md:69` | `IApplicationDbContext` 僅限會員／主辦方範圍 | 與程式碼一致，但此條本身是對「架構強制規則」的永久豁免 | 🟡（規則性發現） |
| 7 | `deploy/api.Dockerfile:44` | 「K8s Job 日後應改由 Secret 以環境變數注入」 | `k8s/30-migrator-job.yaml` 仍以 `args: ["--connection", "$(ConnectionStrings__DefaultConnection)"]` 走命令列（值來自 Secret，但仍出現在 Pod spec／`ps`） | 🟡 |
| 8 | `README.md` §4 Phase 3「完成」 | 完成 | 付款／Email 為 Mock、密碼重設無投遞；README 已知限制有提 Mock，但「完成」字樣易被誤讀為可上線 | 🟡 [文件] |
| 9 | `openspec/specs/ticket-redemption/spec.md` Purpose | 佔位文字（validate warning） | — | 🔵 [文件] |

---

## 3. 缺失文件清單

| 文件 | 為何需要 | 嚴重度 |
|---|---|---|
| `LICENSE` | 公開 repo 無授權 = 預設保留所有權利；面試用途仍建議明確 | 🔵 |
| `SECURITY.md` | 漏洞通報管道 | 🔵 |
| `CHANGELOG.md`／版本號 | 無任何 release tag（`git tag` 空）；無法對應「哪個版本上線」 | 🟡 |
| Runbook（啟動、回滾、migration 失敗處置、Redis 失效行為、Seq 查詢） | 上線維運最低需求 | 🟠 |
| 備份／還原手冊＋RPO/RTO | 資料庫無備份策略 | 🟠 |
| 隱私權政策／個資蒐集告知（個資法第 8 條） | 系統蒐集 email、姓名、身分證末四碼 | 🟠 |
| 威脅模型（Threat Model）／資料流圖 | 目前只有 `design-hardener` 條件式審查，無全系統 TM | 🟡 |
| ADR 索引 | 決策散落於 `openspec/changes/archive/*/design.md`，無單一入口 | 🔵 |
| API 清單／正式環境 OpenAPI | OpenAPI 只在 Development 開放 | 🟡 |
| 環境變數完整對照表（dev／verify／k8s） | `docs/archive/2026-11-10-deployment-env-vars.md` 已封存但仍是唯一來源；`.env.example` 不完整 | 🟡 |
| 事故應變／on-call 流程 | 單人專案可簡化，但至少要有「誰、怎麼看、怎麼回滾」 | 🟡 |

---

## 4. CLAUDE.md／.claude 審查

### 4.1 專案 `CLAUDE.md`

| 行 | 內容 | 判定 |
|---|---|---|
| 69 | `IApplicationDbContext` 範圍不重構 | 永久豁免，削弱「新增 Entity 走 Repository」規則的可信度；已有理由與來源文件，接受但列為發現 |
| 74 | 「何時可簡化」：Entity < 5 可直接注入 DbContext | 已自承本專案不適用，條款仍在，可能誤導 |
| 269 | TDD 例外：N/A 條款 | 有邊界（只豁免 RED/GREEN、不得以編譯失敗當 RED），合理 |
| 288 | 審查迴圈例外：只改 `tasks.md` 可單 reviewer；外部審查前只跑 strict-reviewer | 兩個加速例外；記憶顯示多次 PASS 後仍由人工找出 bug，例外擴大了漏網面 |
| 302 | AC 替代驗證例外 | 需使用者核准、有解除條件，合理 |
| graphify 段 | 「committed graphify-out」 | 與 `.gitignore` 不符 |
| 「禁止在本機執行 dotnet」 | 由 hook 強制 | ✅ 有效（本次被攔） |

### 4.2 全域 `~/.claude/CLAUDE.md`

| 問題 | 說明 |
|---|---|
| 提及 Laravel `.env`／`config/*` | 與本專案 .NET 無關，為其他專案殘留 |
| agentmemory 「MUST」規則 | 與專案記憶系統（`~/.claude/projects/.../memory/`）並存，兩套記憶 |
| Token 預算 4,000/task、30,000/session | 與本專案多輪審查流程（單次 strict-reviewer 即超過）直接衝突，實際上從未被遵守 → 規則形同虛設 |
| 「English Pivot Reasoning」 | 與專案「所有回應繁體中文」不衝突，但重複 |

### 4.3 `.claude/settings.json`／`settings.local.json`

| 檔案:行 | 內容 | 判定 |
|---|---|---|
| `settings.json` hooks | `block-host-dotnet.js`（PreToolUse）、graphify hook-guard | ✅；graphify 路徑為絕對路徑 `C:/Users/2110062/.local/bin/graphify.EXE`，不可攜 |
| `settings.local.json:14-17` | allow `Bash(dotnet build *)`、`dotnet test *`、`dotnet ef *`、`dotnet run *` | ❌ 與 CLAUDE.md 禁令矛盾；僅靠 hook 擋 |
| `settings.local.json:20` | allow `Bash(git commit -m ' *)` | ⚠️ 與「不自動 commit」矛盾，靠模型自律 |
| `settings.local.json` | allow `docker compose *`、`curl *` | ⚠️ 範圍寬（`docker compose down -v` 可刪 volume） |

### 4.4 Agent／Skill 定義

| 項目 | 判定 |
|---|---|
| `.claude/agents/{design-hardener,spec-reviewer,strict-reviewer}.md` 與 `.agents/agents/` 鏡像 | 無自動同步機制；記憶記錄 `.devin/agents` 曾漂移 |
| `strict-reviewer` 工具白名單含 `docker compose exec api dotnet test:*` | 合理；但 reviewer 可跑全套測試 → 與主 session 並行會互相干擾（CLAUDE.md:288 已記錄） |
| 17 個 skill | 多數為流程 skill；`hardener` 為檢查清單，無自動化 |
| `openspec/config.yaml` | TDD 規則寫入提案生成；規則 3 N/A 條款與 CLAUDE.md:269 一致 |

---

## 5. 審查後修正狀態（2026-11-12）

| §2 編號 | 狀態 |
|---|---|
| 1 `web/README.md:51` | ✅ 已改寫 |
| 2 `deployment-prerequisites.md:3` | ✅ 已改寫（仍留 `docs/todo/`，VPS 線未完成） |
| 3 架構指南 `Program.cs:386` | ✅ 已改指 `RateLimitingServiceCollectionExtensions.cs:51`，導覽列加註行號漂移；其餘連結未逐一重對 |
| 4 `.env.example` | ✅ 補 `WEB_HOST_PORT` 與部署用變數指引；原建議補 Captcha／Mock 選項有誤（compose 不轉入） |
| 5 CLAUDE.md graphify | ✅ 已改為 git-ignored 敘述 |
| 6 CLAUDE.md:69 豁免 | 未動（規則性發現，屬使用者決策） |
| 7 migrator 命令列 | 未動（需改 K8s Job 與 bundle 行為，走 change） |
| 8 README §4 | ✅ 已知限制新增「尚未達可上線狀態」並連結本審查 |
| 9 `ticket-redemption` Purpose | ✅ 已填寫 |
| §3 缺失文件 | 皆未新增（LICENSE 屬授權決策；runbook／備份／隱私告知建議隨對應 change 產出） |
| §4.2 全域 CLAUDE.md | 未動（影響其他專案） |
| §4.3 `settings.local.json` | ✅ 已移除 `dotnet *`、`git commit`、`git merge` 允許項 |

## 6. 小結

- 文件整體品質高、更新勤（project-scope、api-conventions、archive 皆準確）。
- **3 處過時已修**：`web/README.md:51`、`deployment-prerequisites.md:3`、`architecture-guide/index.html:419`。
- **上線前必補 3 份**：runbook、備份手冊、隱私告知。
- **AI 設定**：`settings.local.json` 矛盾已修；全域 CLAUDE.md 殘留規則待使用者處理。
