# 專案整體審視（2026-10-16）

> 審視範圍：`docs/project-scope.md`、`openspec/specs/` 主 spec、程式碼結構（`.csproj` 相依、介面位置、授權 policy）。
> 未逐一重讀 50 份已歸檔 change 的 design.md，未重跑後端完整測試。本文件只記錄發現，各項的處理方式待決定。

## 1. 完整度

| 項目 | 狀態 |
|---|---|
| Phase 1 Must（7 項） | ✅ 全數完成，「建立活動 → 下單 → 付款 → 出票 → 核銷」可 end-to-end |
| Phase 2 Should（5 項） | ✅ 全數完成（金流、Email 為 Mock） |
| Phase 3 Could（6 項） | ✅ 全數完成 |
| 第 8 節補強項目 | ✅ 開賣時間、下單上限、請求大小限制、時間驗證已處理 |
| 500 併發 0% 超賣 | ✅ 達成 |
| P95 < 500ms | ⚠️ 暖機後達標，冷啟動未達標（2026-10-13 決定暫緩，README「已知限制」已揭露） |

## 2. Clean Architecture 實現程度

**已實現的部分**

- 專案以 4 個 `.csproj` 拆分，相依方向為 WebApi → Infrastructure → Application → Domain，單向無反向參照（逐一檢查 `ProjectReference` 確認）
- `ProjectC.Domain.csproj` 沒有任何 `ProjectReference` 或 `PackageReference`，Domain 完全不依賴框架
- Domain Entity 沒有 public setter（全面搜尋 `{ get; set; }`／`public set` 無結果），符合 Rich Domain Model
- 外部服務（金流、Email、QR Code、簽章、Redis、Captcha）皆以介面隔離，實作放在 Infrastructure

**偏離的部分**

- **Application 依賴 EF Core**：`ProjectC.Application.csproj` 參照 `Microsoft.EntityFrameworkCore`。`IApplicationDbContext` 直接暴露 `DbSet<T>`，會員／主辦方相關的 19 個 Handler 在 Application 層呼叫 `FirstOrDefaultAsync`、`ToListAsync` 等 EF 方法。這是 Clean Architecture 中最主要的洩漏：Application 層綁定了特定 ORM，無法在不碰 EF 的情況下替換資料存取技術
  - 此做法源自 `membership-system` design（當時 Entity 少於 5 個，符合 CLAUDE.md 簡化條款），之後 Entity 增加到十幾個，但沒有回頭重新評估
  - 業界常見的 Clean Architecture 範本（如 Jason Taylor 的 CleanArchitecture template）也採用同樣做法，屬於有爭議但常見的務實取捨，不算錯誤，但與本專案售票側的 Repository 寫法不一致

## 3. 偏差與矛盾

### 3.1 資料存取兩套模式並存

- 會員、主辦方：Handler 直接使用 `IApplicationDbContext`（EF 查詢寫在 Application）
- 售票（活動、票種、座位、訂單、票券、排隊）：使用 Domain 定義的 Repository 介面，實作在 Infrastructure
- CLAUDE.md 簡化條款的前提（Entity < 5）已不成立，但兩套並存的狀態沒有被重新決策

**形成經過**（依 git 歷史與各 change 的 design.md 追溯）：

| 日期 | 事件 | 判斷 |
|---|---|---|
| 2026-08-13 | 建立專案骨架與 CLAUDE.md（353b120），已含「Entity < 5 時 Application 可直接注入 `DbContext`」簡化條款 | — |
| 2026-08-14 | `membership-system` design 決策 5 採用 `IApplicationDbContext`（Member、RefreshToken、PasswordResetToken 共 3 個 Entity），且以介面包裝而非直接注入具體 `DbContext` | 符合當時規則 |
| 2026-08-17 | `ticketing-infrastructure` design 明確不把售票側 8 個 `DbSet` 加進 `IApplicationDbContext`，改走 Repository | 符合規則，兩套模式自此並存 |
| 2026-09-28 | `organizer-management` 實作（7bbfe55）把 `Organizer`、`OrganizerMember` 加進 `IApplicationDbContext`，design 沒有對應決策，當時 Entity 已超過 5 個；審查紀錄中沒有相關指摘 | **真正的偏離點** |

結論：架構規則一開始就有訂，會員側最初使用 `DbContext` 是合規的簡化；問題出在主辦方管理沿用既有寫法時沒有重新檢查簡化條款的前提，審查流程也沒有攔下。

### 3.2 CLAUDE.md「介面一律放 Domain」與實作脫節

- Application 層現有 9 個介面：`IApplicationDbContext`、`ICaptchaService`、`IDateTimeProvider`、`IDistributedLock`、`IPasswordHasher`、`IPurchaseQueueAdmissionMirror`、`IQueryCache`、`ITokenService`、`IUnitOfWork`
- 各 change 都在自己的 design.md 核准例外，並以前一個例外為先例（如 `captcha-verification` 決策 12 引用 `IDistributedLock`／`IDateTimeProvider`），但 CLAUDE.md 的規則文字從未修改
- 實際形成的規則是：有業務語意的介面放 Domain（`IPaymentGateway`、`IEmailNotificationService`、各 Repository），純技術性的放 Application。這本身是標準 Clean Architecture 的做法（Port 放在 Application），問題在於規則文件沒有反映現況

### 3.3 「服務費」未實作也未列 Won't

`project-scope.md` 第 1 節商業模式寫「買家每張票服務費示意」，程式碼與 spec 皆無對應，也不在第 2 節 Won't 清單，屬於未追蹤的缺口。

### 3.4 多租戶邊界不一致

- `Venue`／`SeatMap` 為平台共用資源，但任何已切換至 Approved Organizer 的成員皆可建立，且所有租戶都看得到（`event-management-organizer-scoping` design 已明確接受此取捨，視為 YAGNI）。實際影響：任一主辦方可新增場館，出現在其他主辦方的下拉選單
- `/api/admin/*` 前綴同時涵蓋平台 Admin 專用（`AdminMembersController`、`AdminOrganizersController`，policy `AdminOnly`）與主辦方後台（活動、訂單、核銷、場館，policy `RequireOrganizerContext`），路由名稱無法反映實際權限

### 3.5 僅有後端、沒有前端入口的管理功能

- 會員停用／啟用（`POST /api/admin/members/{id}/deactivate|activate`）
- 主辦方停權（Suspend，organizer-management 已記錄為「僅後端端點」）
- 平台 Admin 帳號只能由 `DevelopmentDataSeeder` 建立，正式環境沒有產生第一個 Admin 的方式（會員系統時期即延後處理）

### 3.6 主 spec 可維護性下降

- 主 spec 應只描述「系統現在的行為」，但部分 Requirement 內夾帶審查歷程敘述，例如「本輪審查要求」「第七輪審查發現」「strict-reviewer 三輪審查皆未發現，由人工審查找出」。統計：`purchase-queue-leader-election` 8 處、`purchase-queue` 5 處（另有 3 處「本次改動」字樣）、`buyer-web-ui` 1 處（初版誤記為 39／16／11 處，當時的搜尋把「輪詢」的「輪」也算進去，已更正）。這類內容屬於 design.md 或 archive，留在主 spec 會讓讀者難以分辨哪些是規則、哪些是歷史
- 成因：審查迴圈中修正 spec 時，把「回應哪個審查問題」寫進 delta 條文；`openspec archive` 原文複製進主 spec；之後的 MODIFIED 又整條沿用
- `buyer-web-ui` 942 行、`admin-web-ui` 613 行；`openspec validate --specs --strict` 時 26 份主 spec 因 Requirement 過長（> 500 字元）失敗（非 strict 模式通過）

### 3.7 已知未處理事項

- P95 冷啟動未達標（見第 1 節）
- WSL2 時鐘造成 TTL 相關測試偶發失敗（`project-scope.md` 第 8 節）
- ~~後台活動列表開始時間顯示不一致~~——2026-10-16 查證為截圖誤判，非 bug（`project-scope.md` 第 8 節）
- dev DB 殘留 `frontend-order-limit-hints` 瀏覽器驗證建立的兩筆 `[測試]` 活動（時間顯示查證建立的一筆已刪除）

## 4. 流程觀察

多數 change 在 strict-reviewer／spec-reviewer 判定 PASS 後，仍由人工審查找出 blocking 問題（各 change 的紀錄見 `project-scope.md` 第 2、8 節）。目前品質實際依賴人工複審，審查 agent 不足以單獨作為把關。

## 5. 處理紀錄

| 項目 | 處理方式 | 狀態 |
|---|---|---|
| 3.1 資料存取兩套模式 | **決定不重構程式碼**：改動需新增 4～5 個 Repository、改寫 19 個 Handler 與對應測試，集中在登入／Token／密碼重設等安全敏感流程，收益只有寫法一致。改為在 CLAUDE.md 明定範圍：會員／主辦方沿用 `IApplicationDbContext`，禁止再擴充其 `DbSet`，新 Entity 一律走 Repository | ✅ 2026-10-16 |
| 3.2 介面放置規則 | CLAUDE.md 改寫為「有業務語意放 Domain、純技術性放 Application」並附判準，現有 9 個介面不再是例外 | ✅ 2026-10-16 |
| 3.6 主 spec 審查歷程 | 預防：CLAUDE.md 新增「Spec 條文撰寫」規則，`spec-reviewer` 一致性清單新增對應 blocking 檢查項。清理：`purchase-queue-leader-election`、`purchase-queue`、`buyer-web-ui` 三份移除審查歷程字樣，只刪歷程、保留規則句，Scenario ID 前後逐一比對一致，`openspec validate --specs` 27/27 通過（無對應 spec 的文件整理） | ✅ 2026-10-16 |
| 3.6 其餘以 change 為主詞的措辭 | 7 份主 spec 共約 11 處「本次不提供／本 change 範圍」這類措辭（`admin-web-ui`、`email-notification`、`event-management`、`observability`、`organizer-management`、`real-name-verification`、`web-bundle-splitting`），語意是範圍界定而非審查歷程，影響較小，暫不處理 | ⏸ 暫緩 |
| 3.6 Requirement 過長 | 拆分 Requirement 會改動 Requirement 名稱，影響後續 MODIFIED 對應，暫不處理 | ⏸ 暫緩 |
| 3.3 服務費 | 列入 `project-scope.md` 第 2 節 Won't：僅為商業模式示意，與高併發／防黃牛主軸無關 | ✅ 2026-10-16 |
| 3.5 正式環境第一個 Admin | 不補 CLI／seed 機制，demo 用途由 `DevelopmentDataSeeder` 建立，於 README demo 說明交代（`project-scope.md` 第 8 節面試展示前待辦 ③） | ✅ 2026-10-17 |
