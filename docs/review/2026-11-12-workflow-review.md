# 開發工作流檢視與建議 — 2026-11-12

> 對象：本專案自 2026-08 至今的 Spec-First ＋ AI 協作流程（CLAUDE.md、`.claude/`、`openspec/`、`scripts/`、記憶中的歷次經驗）。
> 目的：指出流程中「成本高但回報遞減」與「成本低但沒做」的地方，供你調整。不是要推翻現行流程。
> 數據來源：`git log`、`openspec/changes/archive/`、CLAUDE.md 規則、本次 PRR 結果。

---

## 1. 現況快照

| 指標 | 數值 | 來源 |
|---|---|---|
| 期間 | 2026-08 ～ 2026-11（約 3.5 個月） | `git log` |
| Commit 數 | 511（8 月 98、9 月 60、10 月 182、11 月 171） | `git log --format=%ad` |
| 歸檔 change 數 | 71 | `ls openspec/changes/archive` |
| 平均每 change | 約 7 commit、約 1.5 天 | 推算 |
| 後端測試 | 1,983（檔案 330 個） | `dotnet test` |
| 前端測試 | 725 | Vitest |
| 審查 ≥ 5 輪的 change | 6 個 tasks.md 有「第 5～19 輪」字樣 | `grep` |
| master 與 origin 同步 | 0 ahead | `git status -sb` |
| CI | 測試＋lint＋openspec validate＋docs 路徑；無覆蓋率門檻、無映像掃描 | `.github/workflows/ci.yml` |

---

## 2. 做得好的（請保留）

1. **Spec-First 真的有執行**：71 個 change 都有 proposal／design／tasks，主 spec 26 個 capability 與程式碼一致（本次抽查 api-conventions、ErrorType 全對上）。
2. **TDD 證據制**：RED／GREEN 原文貼進 `tasks.md`，加上 `verify-test-evidence.sh restore/mutate` 的還原驗證，這在個人專案極少見，且確實擋過「測試不會失敗」的假測試。
3. **多層審查有找到真 bug**：記憶中至少 10 次是 spec-reviewer／strict-reviewer PASS 後，由對抗式 subagent、Devin 或你本人找出真實缺陷（ZPOPMIN、RedisServerException、eager-config-read、App.vue meta…）。
4. **真實瀏覽器驗證**：router／HMR／相機黑屏這類只有真機會出現的問題被抓到，並寫成規則。
5. **文件紀律**：決策理由寫在 design.md 而非 spec；路徑檢查腳本進 CI；過時文件主動歸檔。
6. **安全底線**：pre-commit TruffleHog、host dotnet 攔截 hook、Testcontainers 隔離、Secret 不進版控、ImageSharp 風險明文接受。

---

## 3. 觀察到的問題

### 3.1 審查成本高、邊際報酬遞減

- 審查迴圈不設上限，單一 change 最多跑到 7～19 輪；每輪 strict-reviewer 都重跑全套測試（約 2,000 個，含 Testcontainers）。
- 但 PRR 顯示：**所有 71 個 change 都 PASS 的情況下，系統仍有 2 個 Critical**。這兩個都是「跨 change 的系統層問題」（Mock 在 Production 註冊、代理後限流失效），單一 change 的 diff 審查天生看不到。
- 推論：審查預算花在「這個 change 內部對不對」已經飽和，缺的是「整個系統放到真實拓樸裡對不對」的檢查。

### 3.2 規則文件膨脹且有自相矛盾

- 專案 CLAUDE.md 約 380 行，含 5 條例外條款；全域 CLAUDE.md 另有 Laravel 殘留、agentmemory MUST、token 預算 4,000／30,000（任何一次 strict-reviewer 都會超過，等於從未遵守）。
- `.claude/agents/`、`.agents/agents/`、`.devin/rules/` 三處並存，靠人工同步；記憶已記錄過漂移。
- `settings.local.json` 曾允許 `dotnet *` 與 `git commit`，與 CLAUDE.md 禁令相反（本次已修）。
- 推論：規則是在每次踩坑後「追加」而非「重構」，閱讀者（含 AI）要同時記住規則與例外，實際遵守率下降。

### 3.3 流程對所有 change 一視同仁

- 一行設定、純文件、核心下單邏輯，目前都走同一套：propose → spec-reviewer → design-hardener → 使用者交叉審查 → apply（TDD＋還原驗證）→ hardener → strict-reviewer ＋ 對抗式 subagent（不限輪）→ 瀏覽器驗證 → archive。
- 「無 spec 時的處理」條款存在，但實務上幾乎所有改動都開了 change（71 個）。
- 推論：小改動的固定成本過高，會讓人傾向「累積成大 change」，與顆粒度規則互相拉扯。

### 3.4 自動化缺口讓人工審查在做機器的事

| 人工審查反覆檢查的事 | 可自動化的方式 | 現況 |
|---|---|---|
| 分層依賴方向 | ArchUnitNET／NetArchTest 架構測試 | 無，靠 strict-reviewer 讀 csproj |
| 測試是否真的會失敗 | Stryker.NET mutation testing | 無，靠 `verify-test-evidence.sh mutate` 一次一個手動 |
| 覆蓋率下降 | CI 門檻 | 無 |
| 映像／相依弱點 | trivy、`dotnet list package --vulnerable` 進 CI | 無（僅本機 TruffleHog） |
| 購票主流程端到端 | Playwright | 無，每次靠 claude-in-chrome 手動 |
| Production 組態合理性 | 啟動時 `ValidateOnStart` ＋ 環境守門 | 僅 Jwt／Captcha／QueryCache 有；Mock 沒有 |

### 3.5 測試穩定性靠記憶而非登記

- WSL2 時鐘漂移、Seq flush race、login-rate-limit under load、BW-PENDING-007 等 flaky 問題散在記憶與 commit message，沒有一份「已知 flaky 清單＋根因＋現況」。
- 推論：重跑一次就過的測試會被當成通過；PRR 的全綠也只是單次。

### 3.6 單人瓶頸與工具鏈個人化

- herdr 三 pane、Devin smart mode、graphify hook、statusline.js、絕對路徑 `C:/Users/2110062/...`：任何一環換機器或換人即失效。
- graphify：CLAUDE.md 要求「先 query 再 grep」，但記憶記錄 query 常混入無關節點、需 `--seed` 才準；本次 PRR 逐行取證時完全沒用到它。
- 推論：工具是為了加速單人開發而加，但維護成本（三處 agent 定義、hook 路徑、117 MB 圖譜）開始反噬。

### 3.7 「完成」的定義偏向工程內部

- README 寫「Phase 1–3 已全數完成」，project-scope 把退款／取消／出貨列 Won't，這在作品集情境合理；但付款與 Email 是 Mock、密碼重設無投遞，這些是「流程走得通但對使用者沒有結果」的功能，完成度表述容易讓接手者誤判。
- 推論：需要一個與「spec 已歸檔」不同的「可上線」判準，PRR 報告 §6 是第一版。

---

## 4. 建議（依投入／回報排序）

### 4.1 立刻可做（各 < 半天）

1. **把規則分層**：CLAUDE.md 拆成「不可違反（≤ 20 條）」與「流程手冊（連結到 skill／docs）」；例外條款集中成一張表。全域 CLAUDE.md 刪除 Laravel 與 token 預算，或改成可遵守的值。
2. **單一來源的 agent 定義**：保留 `.claude/agents/` 為來源，其餘以腳本產生或 symlink；在 `check-doc-paths.sh` 旁加一支 diff 檢查進 CI。
3. **flaky 登記簿**：`docs/` 新增一份表（測試名、症狀、根因、修正 commit、是否仍會發生），CI 失敗時先查表。
4. **把 PRR §6「上線最低必要條件」納入 README 的完成度定義**：「Phase 完成」與「可上線」分開寫。

### 4.2 一～兩週內（各一個小 change 或無 spec 改動）

5. **分級流程**（寫進 CLAUDE.md 取代一視同仁）：
   - **L0 文件／設定／相依升級**：直接改，commit 註明「無對應 spec」，跑既有 CI 即可。
   - **L1 非核心程式碼（新端點、UI 調整）**：propose → spec-reviewer → apply（TDD）→ strict-reviewer 一輪 → archive；對抗式 subagent 與還原驗證可省。
   - **L2 核心（下單、排隊、座位、金流、認證、授權）**：現行完整流程，但審查迴圈改為「最多 3 輪，第 3 輪仍有 blocking 就停下來重新設計」而非無上限。
6. **架構測試取代人工分層審查**：一個 `ProjectC.Architecture.Tests` 專案（NetArchTest），斷言 Domain 無外部參考、Application 不依賴 Infrastructure、Handler 不直接 new Repository、Controller 都有 Authorize 或 AllowAnonymous。strict-reviewer 的分層檢查項改為「架構測試是否通過」。
7. **Production 守門**：所有「Development 才合理」的綁定（Mock 付款、Mock Email、AcceptAny captcha、Seq 無認證）統一在一個 `ProductionGuard` 啟動檢查裡 fail-fast，並有整合測試以 `ASPNETCORE_ENVIRONMENT=Production` 啟動驗證。這一項直接對應 PRR C-1，也是未來新 Mock 的防線。
8. **CI 加三個 gate**：覆蓋率門檻（先設現值 −2%，不倒退即可）、`dotnet list package --vulnerable`、trivy image scan（allowlist 沿用 `.security/`）。

### 4.3 一個月內

9. **Playwright 黃金路徑**：一條 e2e 覆蓋註冊→登入→排隊→下單→確認→QR→核銷，進 CI（compose up 後跑）。claude-in-chrome 保留給「新 UI 的探索性驗證」，不再是每個 change 的必經步驟。
10. **Stryker.NET 跑核心專案**：先只跑 `ProjectC.Domain` 與 `Application/Orders`，mutation score 進 tasks.md 取代部分對抗式審查；分數明顯低的測試檔就是「不會失敗的測試」。
11. **系統層審查排程化**：每 10 個 change 或每月一次，跑一次類似本次 PRR 的「拓樸級」審查（只看 Program.cs、DI 註冊、compose／k8s、nginx），不看 diff。本次兩個 Critical 都屬此類。
12. **工具鏈去個人化**：hook 與 statusline 改用 `$HOME`／PATH；graphify 改為選用（CLAUDE.md 不再強制「先 query」），保留 `update` hook 但移除 `hook-guard`，或量化一次它實際省下的 token 再決定。

### 4.4 可以考慮放掉的

- **審查迴圈不設上限**：改為 3 輪上限＋升級設計討論。
- **每個 change 都跑對抗式 subagent**：L2 才跑。
- **每輪 strict-reviewer 重跑全套測試**：改為只跑受影響測試專案，全套交給 CI。
- **`verify-test-evidence.sh restore` 對每個 bug fix**：有 mutation testing 後，只對 L2 保留。
- **三 pane herdr 流程作為預設**：保留為「大 change」選項。

---

## 5. 一張表：現行 vs 建議

| 面向 | 現行 | 建議 |
|---|---|---|
| 流程分級 | 全部同一套 | L0／L1／L2 三級 |
| 審查輪數 | 不設上限 | L2 ≤ 3 輪，超過則回設計 |
| 分層檢查 | strict-reviewer 人工 | 架構測試 |
| 測試有效性 | 還原驗證腳本（手動、一次一個） | Stryker（L2）＋還原驗證（金流／庫存） |
| E2E | claude-in-chrome 手動 | Playwright 黃金路徑進 CI＋手動探索 |
| 系統層審查 | 無（本次是第一次） | 每月或每 10 change 一次 |
| 組態安全 | 個別 `ValidateOnStart` | 統一 `ProductionGuard` |
| 規則文件 | 380 行＋例外散落 | 核心 ≤ 20 條＋手冊＋例外表 |
| Agent 定義 | 三處手動同步 | 單一來源＋CI diff |
| CI gate | 測試／lint／spec／路徑 | ＋覆蓋率／弱點／映像掃描／e2e |
| Flaky 追蹤 | 記憶與 commit message | 登記簿 |

---

## 6. 不建議改的

- Spec-First 與 design.md 寫理由：這是本專案文件品質高的根源。
- RED／GREEN 原文證據：成本不高，防假測試有效。
- 真實瀏覽器驗證本身：只是從「每次必做」改為「新 UI 必做、回歸交給 e2e」。
- 不自動 commit／push：個人專案也該維持，本次 PRR 的 9 項修正就是靠這條讓你能先看再決定。
