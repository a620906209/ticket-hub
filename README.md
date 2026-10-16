# Ticket-Hub — 高效能高併發售票系統

![Ticket-Hub](docs/images/banner.png)

> 個人作品專案，主軸情境為「熱門活動搶票」：抗高併發、防超賣、防黃牛，並涵蓋建立活動 → 選位/購票 → Mock 付款 → 出票（QR Code）→ 現場核銷的完整流程。
>
> 本 README 為求職 / 面試展示用途，說明專案範疇、技術棧、目前完成度，以及開發過程中如何與 AI 協作（含實際使用的工具與工作流程，而非僅是「有用 AI 寫程式」的空泛說明）。
>
> GitHub Repository：[github.com/a620906209/ticket-hub](https://github.com/a620906209/ticket-hub)
>
> 專案內的 C# 命名空間沿用開發初期代號 `ProjectC.*`（`ProjectC.Domain`／`ProjectC.Application` 等），對外產品名稱統一為 **Ticket-Hub**，兩者指同一個專案。

---

## 1. 專案定位

以「情境 B：高併發防黃牛」為主軸的售票系統，核心目標依優先順序：

1. **抗高併發與庫存一致性**（不超賣、開賣不當機）
2. **防黃牛 / 防機器人搶票**（Rate limiting、排隊機制）
3. **售票效率**（主辦方自助上架、買家流暢結帳）

技術驗證指標：模擬 **500 併發**搶購同一場次 **50 張票**，目標 **0% 超賣**、**P95 回應時間 < 500ms**。

完整範疇規劃見 [`docs/project-scope.md`](docs/project-scope.md)。

## 2. 技術棧

| 分類 | 選型 |
|---|---|
| 後端 | C# / .NET 10（ASP.NET Core，Controller-based MVC） |
| 前端 | Vue 3（Composition API + `<script setup>`）+ TypeScript + Vite + Element Plus + Pinia |
| 資料庫 | PostgreSQL 16 |
| ORM | Entity Framework Core |
| 快取 / 分散式鎖 / 排隊 | Redis（查詢快取、Leader Election、排隊入場 Sorted Set） |
| 可觀測性 | Serilog + Seq（結構化 log、TraceId） |
| 壓力測試 | k6（`loadtest` profile，只在壓測時單次執行） |
| 認證 | JWT（Access Token + Refresh Token） |
| 限流 | `Microsoft.AspNetCore.RateLimiting`（分區限流，搶購端點 + 登入端點各自獨立 policy） |
| 電子票券 | QRCoder（本地產生 QR Code，內容為 HMAC 簽章過的 Ticket ID，防偽造） |
| 驗證 | FluentValidation |
| 測試 | xUnit + FluentAssertions + Moq + Testcontainers（PostgreSQL 整合測試） |
| 靜態分析 | SonarAnalyzer.CSharp + Microsoft.CodeAnalysis.NetAnalyzers |
| 容器化 | Docker Compose（本機開發全流程容器化，不需在主機安裝 .NET SDK / Node.js / PostgreSQL） |

## 3. 架構

![架構總覽](docs/images/architecture-overview.png)

本機以 Docker Compose 部署五個常駐 service（`web` / `api` / `db` / `redis` / `seq`），另有只在壓測時執行的 `k6`。`api` 內部採 Clean Architecture 分層，依賴方向由外向內單向：

```
WebApi → Infrastructure → Application → Domain
```

- **Domain**：Entity、狀態機、有業務語意的介面（Repository、`IPaymentGateway`、`IEmailNotificationService` 等）；不參照任何專案或套件。
- **Application**：Use case 協調邏輯、DTO、純技術性介面（`IDateTimeProvider`、`IDistributedLock`、`IQueryCache` 等）；專案參照只有 Domain。
- **Infrastructure**：EF Core 實作、Redis、Mock 金流、QR Code 產生等技術細節。
- **WebApi**：Controller、DI 註冊、middleware（含全域例外處理、JWT 驗證、限流）。

資料存取有兩套寫法並存：售票側（活動、票種、座位、訂單、票券、排隊）走 Domain 定義的 Repository；會員與主辦方側的 Handler 直接使用 `IApplicationDbContext`（暴露 EF Core `DbSet`），因此 Application 參照了 EF Core 套件。這是早期會員系統沿用下來的寫法，評估後決定不重構，改為規定不再擴充、新 Entity 一律走 Repository，詳見 [`docs/project-review-2026-10-16.md`](docs/project-review-2026-10-16.md)。

核心實體關係：

```
Organizer → Event → TicketType（RequiresSeat 開關：座位制／純計數制）
                          ↓（座位制才存在）
                        Seat
Order → OrderItem → Ticket（電子票券，核銷用）
```

座位鎖定採**悲觀鎖**（資料庫交易鎖 + 固定順序取鎖避免死鎖），純計數票種庫存扣減沿用同一套模式。

## 4. 功能完成度

依 MoSCoW 分級（完整規劃見 [`docs/project-scope.md`](docs/project-scope.md) 第 2 節）：

**Phase 1（Must）— 已全數完成**，核心流程可 end-to-end 跑通：

- 活動 / 票種建立與上架（座位制、純計數制兩種模式）
- 座位選擇與鎖定（悲觀鎖，防死鎖）
- 訂單建立與結帳流程（`IPaymentGateway` 抽象化 + Mock 實作，展示依賴反轉）
- 電子票券產出（QR Code + HMAC 簽章防偽造）
- 核銷 API（`PATCH /api/admin/tickets/{id}/redeem`，併發防重複核銷、狀態機驗證）
- 會員系統整合登入（JWT）
- 前端 RWD（買家購票/訂單查詢、主辦方管理後台）

**Phase 2（Should）— 已全數完成**：

- 主辦方銷售報表（依票種彙總營收/售出張數）
- Rate limiting / 排隊機制（搶購端點分區限流）
- 登入 Rate limiting（防暴力破解）
- Email 通知（`IEmailNotificationService` 介面 + Mock 實作，結構化 log 記錄）
- 可觀測性（Serilog + Seq、HTTP／背景服務 TraceId 與結構化 log）

**Phase 3（Could）— 已全數完成**：

- Redis Queue Leader Election：透過 Redis 分散式鎖確保多實例下每輪僅由一個實例推進排隊
- 排隊入場推進改用 Redis Sorted Set + Lua 原子操作（Postgres 仍為持久化真相來源）
- Redis 一般查詢快取（cache-aside，寫入時明確失效 + TTL 安全網）
- 現場核銷掃碼前端：支援相機掃描與手動輸入備援
- 多租戶主辦方管理介面：申請、平台審核、切換主辦方，活動／訂單／核銷／報表依主辦方隔離
- 實名制驗證：一次性登記、需實名活動的下單與核銷比對
- CAPTCHA：註冊、登入、加入排隊三個端點

**Phase 3 完成後的補強**（見 [`docs/project-scope.md`](docs/project-scope.md) 第 8 節）：

- 開賣時間：活動可設定開賣／停售時間，下單與加入排隊在販售期間外拒絕
- k6 壓力測試與兩輪下單 P95 優化（結果見下方「已知限制」）
- 下單請求上限：每筆訂單最多 50 張、請求 body 大小限制，前端同步提示上限
- 活動開始時間驗證：未帶 UTC 時區或為極端值時回 400（原本回 500）
- 登出撤銷 Refresh Token、訂單確認頁、待付款訂單可在明細頁付款／取消
- 前端：手機版版面、色彩對比 WCAG AA、bundle 拆分、後台手機版導覽

### 已知限制

- **P95 < 500ms 只有部分達成**：500 併發搶 50 張、0% 超賣在 k6 實測中全數達成。下單 P95 經兩輪優化，暖機後兩種票種都在 500ms 內（數量票約 370–415ms、座位票約 241ms）；但驗收規則要求「api 重建後第一次執行（冷啟動）也要通過」，數量票冷啟動約 918ms，**判定未達成**；座位票正式流程沒有在冷啟動下驗收，以量測組態補測冷啟動，5 次中有 4 次 ≥ 500ms。冷啟動多出的時間大半花在開資料庫連線（推測與 JIT、從零建立連線有關，未驗證）。候選解法是 Redis 預扣閘門（售完後的請求不進資料庫），但需要重新驗證超賣保證，收尾時決定暫緩。完整數據見 [`docs/load-test/p95-phase2-report.md`](docs/load-test/p95-phase2-report.md)
- **量測環境是本機 Docker Compose**（WSL2），不是正式部署環境；數字只代表相對改善幅度
- **金流、Email 都是 Mock 實作**，沒有串接真實第三方服務；只提供電子票券，不處理活動取消／退款（範疇決策見 [`docs/project-scope.md`](docs/project-scope.md) 第 2 節 Won't）
- **前端 UI 審視的 P2 項目與買家探索頁改版暫緩**，見 [`docs/ui-ux-review-2026-10-08.md`](docs/ui-ux-review-2026-10-08.md) 第 6 節
- **部分平台管理功能只有後端**：會員停用／啟用、主辦方停權只有 API，沒有前端入口；平台 Admin 帳號只能由開發用的 `DevelopmentDataSeeder` 建立，正式環境產生第一個 Admin 的方式尚未決定
- **商業模式中的買家服務費未實作**，是否實作待決定
- 以上缺口與架構偏差的完整盤點見 [`docs/project-review-2026-10-16.md`](docs/project-review-2026-10-16.md)

## 5. 本機執行

所有服務跑在 Docker Compose 容器內，**不需要在本機安裝 .NET SDK / Node.js / PostgreSQL**。

```bash
# 1. 複製環境變數範本並依需要調整（JWT/HMAC 簽章金鑰務必自行更換）
cp .env.example .env

# 2. 啟動全部服務
docker compose up -d

# API：http://localhost:8080（Swagger UI 見 /swagger）
# 前端：http://localhost:5173
```

修改 `src`／`web/src` 下的原始碼會透過 bind mount 觸發 hot reload，不需重啟容器；新增 NuGet 套件或 npm 套件才需要 `docker compose restart api` / `web`。

### 測試

```bash
# 後端（xUnit，含 Testcontainers 整合測試）
docker compose exec api dotnet test

# 前端（Vitest）
docker compose exec web npm run test
```

## 6. 開發流程：Spec-First + AI 協作

這個專案刻意採用**先寫規格、審查通過再實作**的流程，而非直接讓 AI 產生程式碼——目的是控制 AI 輔助開發常見的風險（規格漂移、審查盲點、樣板程式碼掩蓋邏輯錯誤）。以下是實際使用的工具與分工，不是通用的「有用 Copilot」描述。

### 6.1 使用的 AI 工具

- **Claude Code**：主要的 pair-programming 工具，負責規格撰寫、程式碼實作、測試撰寫、程式碼審查。
- **Devin**：`.agents/agents/` 放置對應 `.claude/agents/` 的審查規則，作為第二個 AI 工具的交叉查核（兩者規則需人工同步，非自動鏡射）。

### 6.2 規格驅動流程（OpenSpec）

功能開發不是「描述需求後直接生成程式碼」，而是走固定流程：

```
branch-and-propose → openspec-propose（產出 proposal/design/tasks/specs）
    → spec-reviewer 審查規格
    → design-hardener 設計加固（視需要）
    → openspec-apply-change（依 tasks 逐項實作）
    → strict-reviewer 審查程式碼變更
    → openspec-archive-change（歸檔並同步正式 specs）
```

每個階段對應一個自訂 Claude Code Skill 或 Subagent：

| 元件 | 類型 | 職責 |
|---|---|---|
| `spec-scope` | Skill | 專案初期一次性宏觀需求盤點，產出 `docs/project-scope.md` |
| `branch-and-propose` | Skill | 從 master 同步、開新分支、進入提案流程 |
| `openspec-propose` | Skill | 產出單一功能的 proposal / design / tasks / specs |
| `spec-reviewer` | Subagent | **實作前**審查規格文件：需求完整性、邊界情況、安全與權限需求、AC 與測試任務雙向對應、與既有規格一致性 |
| `design-hardener` | Subagent | spec-reviewer 通過後、實作前，依條件審查安全、容器 runtime、第三方相依性、加密與失效模式 |
| `openspec-apply-change` | Skill | 依 tasks 清單逐項實作 |
| `hardener` | Skill | Application / Repository 層程式碼變更完成後，套用防禦性檢查清單（參數驗證、Entity 存在性、狀態機驗證、並發處理、`CancellationToken` 傳遞、例外邊界、日誌遮蔽） |
| `strict-reviewer` | Subagent | **實作後**審查 git 變更：測試覆蓋、Clean Architecture 分層、EF Core 使用、安全性、命名慣例，並在容器內實際執行測試 |
| `openspec-archive-change` | Skill | 完成後歸檔提案、同步正式 specs |

規格文件保留在 [`openspec/changes/archive/`](openspec/changes/archive/)（每個功能一份 proposal/design/tasks/specs），可對照 git 歷史逐一檢視每個功能從規劃到實作的完整過程。

### 6.3 AI 審查的實際限制（誠實揭露）

多輪自動化審查（`spec-reviewer` + `strict-reviewer` 兩層，甚至同一功能審過多次都回報 PASS）**不代表沒有問題**。開發過程中多次是由人工複審才抓到自動化審查漏掉的問題，例如：

- 併發情境下 `ChangeTracker` 快取與資料庫實際狀態不一致
- Mock 實作掩蓋了真實資料庫層級的邊界情況
- 同一個修正 pattern 沒有同步套用到相似的另一處
- 測試 fixture 的影響範圍未完整列舉，改動波及未預期的測試

因此實際流程是「AI 兩層自動審查 + 人工複審」為最終把關，AI 審查降低了人工複審的工作量，但不能取代它。這點刻意寫在這裡，是因為它是這個專案在「如何負責任地使用 AI 輔助開發」上最真實的心得，而不是行銷話術。

## 7. 專案結構

```
src/
  ProjectC.Domain/          # Entity、狀態機、業務語意介面（Repository 等）
  ProjectC.Application/     # Use case 協調邏輯、DTO、技術性介面
  ProjectC.Infrastructure/  # EF Core、Redis、Mock 金流、QR Code、Email 等實作
  ProjectC.WebApi/          # Controller、DI 註冊、middleware
tests/                      # 對應四層各自的測試專案（xUnit）
web/                        # Vue 3 前端（買家 + 主辦方後台）
openspec/
  changes/archive/          # 已完成功能的規格文件（proposal/design/tasks/specs）
  specs/                    # 目前正式規格（單一事實來源）
loadtest/                   # k6 壓測腳本
docs/
  project-scope.md          # 專案宏觀範疇盤點
  project-review-2026-10-16.md  # 專案整體審視（完整度、架構偏差、待決事項）
  load-test/                # 壓測與 P95 優化報告
.claude/agents/             # spec-reviewer / design-hardener / strict-reviewer 審查規則定義
.claude/skills/             # 自訂 OpenSpec 工作流 Skill
.agents/agents/             # Devin 對應的審查規則（人工同步 .claude/agents）
```
