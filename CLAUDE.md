# CLAUDE.MD

## 語言設定

所有回應、說明、註解一律使用**繁體中文**。
程式碼內的變數名稱、函式名稱維持英文。

---

## 技術棧預設

> 以下為**預設選擇**，僅在有明確理由時才切換，並須說明原因。

- **後端**：C# / .NET 10（ASP.NET Core）
- **API 路由風格**：Controller-based（MVC）；Minimal API 僅用於極簡單、無需複雜驗證的內部端點
- **前端**：預設 **Vue 3**（Composition API + `<script setup>`）；React 僅在既有專案已採用時使用
- **DB**：預設 **PostgreSQL**；既有專案若已使用 SQL Server 則沿用
- **ORM**：Entity Framework Core（主力）；Dapper 僅用於效能敏感場景（須說明理由）

---

## 執行環境（強制前提）

> 本專案所有服務跑在 Docker Compose 容器內。本機雖裝有 .NET SDK / dotnet-ef（供其他工具使用），但本專案**一律不使用本機 SDK**；本機未安裝 PostgreSQL。

### 服務組成

- `api`：ASP.NET Core 應用
- `db`：PostgreSQL
- 開發時以 bind mount 掛載原始碼；NuGet 快取、build 產出物用 named volume 隔離（Windows 檔案 I/O 效能考量）
- 新增 .csproj 時，須同步在 `docker-compose.yml` 為其 `bin` / `obj` 加上 named volume，否則容器 build 產出物會寫回本機原始碼目錄

### 強制規則

- **禁止在本機執行** dotnet / dotnet-ef / psql：本機執行不會報錯，而是會靜默連到錯誤的 DB（`appsettings.Development.json` 為 `localhost`）或把 `bin/obj` 寫進 bind mount 的原始碼目錄。Claude Code 由 `.claude/hooks/block-host-dotnet.js`（PreToolUse）強制攔截；其他工具（如 Devin）不受此 hook 約束，須自行遵守
- 所有指令以容器為執行點：
  `docker compose exec api dotnet ef database update`
  而非 `dotnet ef database update`
- 連線字串一律使用 compose **service name**（`Host=db;`），
  禁止 `localhost` / `127.0.0.1`（容器內指向容器自身，會靜默失敗）
- 修改對外 port 映射前，須確認未與現有服務衝突
- 新增服務時須說明其 `depends_on` 依賴關係
- 建立新專案（`dotnet new`）一律透過 `docker compose run --rm api`，
  確保 SDK 版本由容器決定

---

## 架構骨架

> 專案採 Clean Architecture 分層，依賴方向由外向內單向：
> WebApi → Infrastructure → Application → Domain
> Domain 為最內層，不得依賴任何其他層。

### 專案結構（.csproj 拆分，非資料夾模擬）

- `Domain`：Entity、Value Object、Domain Event、Repository/外部服務的 **interface 定義**。不 reference 任何其他專案。
- `Application`：Use case 邏輯（Handler）、DTO、跨 Entity 的協調邏輯。只 reference `Domain`。
- `Infrastructure`：EF Core 實作、外部 API 串接、檔案/快取/信件等技術細節，實作 `Domain` 與 `Application` 定義的介面。reference `Domain` + `Application`。
- `WebApi`：Controller、DI 註冊、middleware。reference 全部。

### 架構強制規則

- `Domain.csproj` 禁止 `<ProjectReference>` 指向任何其他專案；PR review 時檢查此點。
- 介面與實作不得放在同一專案，實作一律放 `Infrastructure`。介面位置依性質決定：
  - **有業務語意的介面放 `Domain`**：Repository，以及代表業務能力的外部服務（例如 `IPaymentGateway`、`IEmailNotificationService`、`ITicketSigningService`）。
  - **純技術性、不承載業務語意的介面放 `Application/Common/Interfaces`**：例如 `IDateTimeProvider`、`IDistributedLock`、`IQueryCache`、`ICaptchaService`、`ITokenService`、`IPasswordHasher`、`IUnitOfWork`。
  - 判準：換掉這個介面的實作時，業務規則的描述需不需要跟著改？需要就放 `Domain`，不需要就放 `Application`。新增介面時在 design.md 說明歸類理由，不需另立例外。
- 資料存取：
  - 會員、主辦方範圍（`Member`、`RefreshToken`、`PasswordResetToken`、`Organizer`、`OrganizerMember`）沿用 `IApplicationDbContext`，Handler 可直接以 EF 查詢。這是 `membership-system` 時期依簡化條款採用的既有做法，2026-10-16 審視後決定不重構（見 `docs/project-review-2026-10-16.md` 3.1）。
  - 其餘 Entity 一律透過 `Domain` 定義的 Repository 介面存取；**禁止再擴充 `IApplicationDbContext` 的 `DbSet`**，新增 Entity 一律走 Repository。
- Domain Entity 內部狀態一律 `private set`，外部只能透過方法修改（Rich Domain Model，禁止 Anemic Domain Model）。
- 跨 Entity 的判斷邏輯（需要查詢多個 Entity 或外部條件才能決定的規則）放 `Application`，單一 Entity 自己能判斷的規則放 `Domain`，不得混放。

### 何時可簡化（避免過度設計）

- 新專案 Entity 數量少（< 5 個）時，Application 可直接注入 `DbContext`，不必每個 Entity 都建 Repository 介面；出現重複查詢邏輯時才抽出。本專案已超過此條件，資料存取依上方「架構強制規則」。
- Value Object、Domain Event 非必要不用，出現「同一驗證邏輯在多處重複」時才導入 Value Object。

---

## 禁止行為

- 禁止在未確認 spec 對應關係的情況下開始實作
- 禁止繞過 EF Core 直接寫原生 SQL / raw ADO.NET query（除非有明確說明理由，例如效能考量下改用 Dapper）
- 禁止在前端直接使用 `innerHTML` 渲染未經處理的使用者輸入
- 禁止在 Acceptance Criteria 未對應測試任務的情況下開始實作
- 禁止使用 `async void`（事件處理常式除外）
- 禁止吞掉例外（空的 `catch` 或僅 log 後不處理而導致靜默失敗）

---

## 安全強制規則（涉及以下情境時必須執行）

> **觸發條件**：task 涉及以下任一項時，實作前必須先回答所有問題，未回答完畢不得開始實作。

### 觸發條件清單

- 接受外部輸入（表單、API 參數、URL query）
- 資料庫讀寫操作
- 身份驗證 / 授權邏輯
- 檔案上傳或處理
- 呼叫外部 API / Webhook

### 安全確認問題（觸發後必答）

#### 輸入驗證

- 外部輸入有沒有經過 Validation？在哪一層？（建議使用 FluentValidation 或 DataAnnotations）
- 有沒有直接拼接進 SQL 或 shell 指令？

#### 資料庫

- 是否使用 EF Core 或 Dapper 參數化查詢？（禁止使用 raw ADO.NET query，除非有明確理由）
- 有沒有 N+1 查詢風險？（EF Core 需檢查是否有適當使用 `Include` / `AsSplitQuery`）

#### 權限

- 這個操作需要什麼權限？權限檢查在哪一層執行？（Middleware / Authorization Policy / Handler）
- 有沒有可能被未授權使用者觸發？

#### 前端（Vue/React）

- 有沒有直接將使用者輸入渲染進 DOM？（XSS 風險）
- API 呼叫有沒有帶正確的 Auth Header？

### 機敏資訊管理

- 密碼、API Key 等禁止寫死在程式碼或進版控的設定檔中
- 正式環境建議使用 Secret Manager / Azure Key Vault 等機制注入
- 本地開發的環境變數透過 compose 的 env_file 注入，.env 不進版本控制

---

## 命名原則

- 遵循 .NET 官方命名慣例：**public 方法 / 類別 / 屬性用 `PascalCase`**（例如 `GetUserById`），**區域變數 / private 欄位用 `camelCase`**（private 欄位可加 `_` 前綴，例如 `_userRepository`）
- Function 名必須是動詞片語，且能完整描述其行為（`GetUserById` 而非 `GetUser`）
- Boolean 變數 / 屬性以 `Is` / `Has` / `Can` 開頭（`IsActive`、`HasPermission`）
- 禁止使用縮寫（`usr`、`btn`、`tmp`），除非是業界通用（`id`、`url`、`api`）
- Class 名是名詞，不帶 `Manager` / `Helper` / `Utils` 等空洞字尾——若需要，代表職責未釐清
- Interface 名一律加 `I` 前綴（例如 `IUserRepository`），這是 .NET 慣例，非空洞命名

---

## Function 設計原則

- 單一職責：一個 function 只做一件事，能用一句話描述
- 長度警戒線：**以邏輯複雜度為主要判準**。單純的 LINQ 查詢、guard clause、mapping 即使超過 20 行仍可接受；但若含多層巢狀分支或多個職責，即使未滿 20 行也應拆分
- 參數上限：超過 3 個參數時，考慮封裝為 DTO（Data Transfer Object）或 Record 型別

---

## 錯誤處理與例外

- **例外拋出時機**：僅在「無法在當層合理處理」時才拋出；可預期的業務失敗優先用 Result 型別 / 明確回傳值，而非以例外控制流程
- **全域處理**：ASP.NET Core 一律透過全域 `IExceptionHandler` / exception middleware 統一轉換為標準錯誤回應（建議 `ProblemDetails`），Controller 內不散落 try-catch
- **例外包裝**：捕捉低層例外後若要重拋，須保留原始堆疊（`throw;` 而非 `throw ex;`），必要時包成領域例外並帶上內部例外
- **Log 規範**：錯誤一律結構化 log（含 correlation id），且**不得記錄敏感資訊**（密碼、token、個資需遮蔽）
- **禁止靜默失敗**：不允許空 `catch`；捕捉後必須處理、重拋或明確記錄理由

---

## 非同步程式設計

- I/O 操作（DB、HTTP、檔案）一律使用 `async` / `await`
- 禁止 `async void`（UI 事件處理常式除外）
- 公開的非同步方法一律接受並向下傳遞 `CancellationToken`
- 避免在同步情境用 `.Result` / `.Wait()` 阻塞造成死結
- 方法名以 `Async` 結尾（例如 `GetUserByIdAsync`）

---

## 註解原則

- 好的命名取代大部分的註解——若需要大量解釋，先重構命名
- 禁止「描述行為」的註解（`// 遍歷陣列`），只允許「解釋為什麼」的註解（`// 此處使用 X 而非 Y，因為...`）
- Public API 建議使用 XML Doc Comment（`/// <summary>`）而非一般註解
- TODO 必須帶 issue 連結或日期，裸露的 TODO 視為技術債

---

## SOLID 原則

| 原則 | 實踐方式 |
| --- | --- |
| **S** 單一職責 | 每個 Controller 只處理一個資源；業務邏輯下放至 Application 層的 Handler |
| **O** 開放封閉 | 新增功能透過擴充（新 Class / 新方法），不修改既有邏輯 |
| **L** 里氏替換 | 子類別可完全替換父類別，不破壞預期行為 |
| **I** 介面隔離 | 不強迫實作用不到的方法；介面依使用情境拆細 |
| **D** 依賴反轉 | 依賴抽象（介面 / Contract）而非具體實作；透過 .NET 內建 DI Container（`IServiceCollection`）注入 |

### OOP 規範

- **封裝**：內部狀態不直接暴露，透過方法或屬性存取
- **繼承**：優先組合（Composition）而非繼承；繼承層數不超過 2 層
- **多型**：相同介面的不同實作透過 DI binding 切換，不用 `if/switch` 判斷型別

---

## DI 生命週期原則

> 註冊服務至 `IServiceCollection` 時，依下表判準選擇 lifetime，不得憑直覺選擇。

| Lifetime | 判準 | 典型例子 |
| --- | --- | --- |
| **Transient** | 無狀態、輕量，每次注入都該是新的 instance | Validator、Mapper |
| **Scoped** | 狀態綁定單一 HTTP request 生命週期 | `DbContext`、跟 request 綁定的 Unit of Work |
| **Singleton** | 全域共用、無狀態、必須 thread-safe | 設定物件（`IOptions<T>`）、`IMemoryCache`、`IDateTimeProvider` |

- 禁止將 `DbContext` 或任何持有 `DbContext` 的物件註冊為 Singleton（會導致多執行緒共用同一個 context，資料錯亂）
- 若不確定該選哪個 lifetime，預設用 **Scoped**，並在 PR 說明中標注理由待確認

---

## 前端規範（Vue 3 為主）

### 命名

- 元件檔名與元件名用 `PascalCase`（例如 `UserProfileCard.vue`）
- composable 以 `use` 開頭（例如 `useUserProfile`）
- props / emit 事件用 `camelCase`（template 中事件監聽用 `kebab-case`）

### 結構

- 一律使用 Composition API + `<script setup>`
- API 呼叫集中於獨立的 service / api 層，元件不直接寫 fetch/axios 細節
- 狀態管理預設 **Pinia**；跨頁共享才進 store，元件區域狀態留在元件內

### 安全

- 禁止 `v-html` 渲染未消毒的使用者輸入
- API 呼叫透過統一攔截器帶入 Auth Header

### UI Skill 路由

涉及前端 UI、UX、版面、樣式、元件或動畫時，先依任務類型選擇對應 UI skill：

- 新增 Landing Page、Portfolio 或行銷頁面：使用 `taste-skill`（frontmatter name 為 `design-taste-frontend`）
- 改造既有網站或 App：使用 `redesign-skill`
- 明確要求極簡、編輯風格、暖色單色系或 Bento Grid：使用 `minimalist-skill`
- UI 細節、元件質感、互動回饋或 UI 程式碼審查：使用 `emil-design-eng`
- 新增動畫或轉場：使用 `animate`

除非使用者明確要求，否則不要同時套用互相衝突的視覺風格 skill；先選一個主要 skill，必要時再以 `emil-design-eng` 做互動細節審查。

---

## 測試規範

- **測試命名慣例**：`MethodName_Scenario_ExpectedResult`（例如 `GetUserById_WhenNotFound_ReturnsNull`）
- **層級界定**：單元測試不碰 DB / 網路（以 mock 隔離）；整合測試才驗證真實 DB / API 邊界
- **涵蓋範圍**：每一條 Acceptance Criteria 至少對應一個測試；業務核心邏輯優先確保涵蓋
- **TDD 預設**：凡 task 涉及可觀察行為且適合自動化測試，先新增或修改最小相關測試並執行，確認測試因目標行為不符而失敗（不是編譯或環境錯誤）；再做最小實作使測試通過。若進行重構，重構後重跑相關測試；若無需重構，只將重構步驟記錄為 N/A。將 RED（命令、失敗測試／斷言、實際失敗訊息原文摘錄（例如 Expected／Actual，只寫「失敗」不算證據）、目標行為失敗原因及其發生在正式程式碼修改前）、GREEN 與重構後重跑（或 N/A）的命令和結果摘要，記錄在 `tasks.md` 對應 task 下；沒有 `tasks.md` 時記錄於實作交接摘要，且呼叫 `strict-reviewer` 時須將該摘要完整附在 prompt 中（subagent 讀不到對話內容，未附上會因無法核對證據而判定 blocking）。相關測試通過且證據已記錄後才標記 task 完成。測試與實作可在同一 task 內完成，不要求 `tasks.md` 一定拆成獨立 task。
- **TDD 例外**：純文件或沒有合理自動化行為測試的 task，可將 TDD 標示為 N/A 並說明原因；這只豁免 RED／GREEN 流程。若 task 對應 Acceptance Criteria，仍須依一般測試規範驗證，或使用 `design.md` 已正式核准的替代驗證。不得把編譯、環境或測試設定失敗當成預期的 RED。
- **RED 還原驗證**：「RED 發生在修改前」只能自行申報、無法查證，因此 bug fix，以及下單、排隊、座位／庫存等核心業務邏輯的 task，GREEN 後另須證明「測試在沒有這次修改時會失敗」：由主 agent（非 `strict-reviewer`，其維持唯讀）執行 `git stash push -u -- <正式程式碼路徑>` → 在容器內跑相關測試，確認因目標行為不符而失敗 → `git stash pop`，並將命令與失敗輸出記錄於 RED 證據旁。pop 前先以 `git stash list` 確認該 stash 存在；pop 失敗或有衝突時立即停止並回報，不得自行修復工作樹。
- **證據貼原文**：RED、還原驗證、全套測試的紀錄一律貼實際輸出——每個失敗測試的名稱與訊息、每個測試專案的摘要行、`git stash` 指令輸出；不得以數字、「全數通過」或「與 X 相同」代替。輸出沒擷取完整就不算驗證過。
- **測試框架**：xUnit（預設）；斷言可搭配 FluentAssertions；mock 用 Moq / NSubstitute
- 整合測試使用 Testcontainers 啟動獨立 DB 容器，禁止連線至開發用的 db 服務

---

## OpenSpec 工作流規則

### 開發流程總覽

新功能依序走完以下步驟；各步驟細節見對應章節或 skill／agent 定義：

1. **需求釐清與開分支**：`branch-and-propose`（先 Grill 取得共識，再從 master 開 `feature/<名稱>`）
2. **提案**：`/opsx:propose` 產出 proposal / design / specs / tasks（tasks 依 `openspec/config.yaml` 的 TDD 規則撰寫）
3. **規格審查**：`spec-reviewer` 修到 PASS；符合觸發條件時再跑 `design-hardener`。PASS 後暫停，等使用者交叉審查並明確指示才實作；每個階段轉換分開確認
4. **實作**：`/opsx:apply` 逐 task 依「測試規範」的 TDD 執行（RED → GREEN → 重構），證據寫入 `tasks.md` 後才勾選
5. **防禦性最終檢查**：程式碼全部完成後套用 `hardener`（見「防禦性檢查規則」），結果記在 `tasks.md` 的「hardener 最終檢查」task
6. **審查迴圈**：每輪同時跑 `strict-reviewer` 與一個全新的對抗式 general-purpose subagent（只給 change 名稱與審查目標，不給前一輪結論），修正後重跑，直到兩者都沒有 blocking。理由：多次 change 中 strict-reviewer PASS 後仍由對抗式審查或人工找出真實 bug
7. **前端真實瀏覽器驗證**（有前端變更時）：以 claude-in-chrome 實際操作受影響流程，數字與狀態以頁面文字或 DB 核對，不只依截圖判讀。理由：router、HMR、原生表單驗證、相機等問題只在真實瀏覽器出現
8. **歸檔與合併**：`/opsx:archive`（含主 spec 同步）→ merge 回 master

- 任何步驟都不自動 commit / push，等使用者明確指示
- 緊急 hotfix 或無對應 spec 的小型改動（見「無 spec 時的處理」）略過 1–3 與 8，仍須走 4（TDD）、5–7

### Task 執行前

每次執行 task 前，先確認：

1. 這個 task 對應哪一條 spec？（必須能指向具體文件與段落）
2. Acceptance Criteria 是否已定義且可測量？每一條是否已對應至少一項測試任務（xUnit / NUnit 單元測試或整合測試）？未對應者禁止進入實作階段。
   - **例外**：若 `design.md` 已正式記錄例外，且例外紀錄經使用者核准，列明適用的 AC 範圍、偏離理由、替代驗證任務與解除條件，該範圍內的 AC 可用替代驗證（例如腳本化的瀏覽器量測）取代上述測試種類。例外只豁免測試種類：替代驗證仍須可重現、有可判定的通過／失敗斷言，並且能在失敗情境下失敗。未核准或範圍不明的例外不得取代此要求。
3. 涉及可觀察行為且適合自動化測試的 task，是否列出測試先行步驟、預期 RED 失敗條件、GREEN／重構（若有）後的驗證方式，以及完成時記錄證據的位置？提案階段只檢查計畫與紀錄欄位，不要求測試檔或 RED／GREEN 結果已存在。
4. 這個 task 完成後，哪些 spec 文件需要同步更新？

### Spec Review 與 Implementation Review 邊界

- **Spec review 階段**只審查 `proposal.md`、`design.md`、`specs/**`、`tasks.md` 的完整性、可測量性、AC 與測試任務的雙向映射，以及與既有規格的一致性。
- Spec review 階段**不得將尚未依 tasks 實作的程式碼、測試檔案或腳本不存在視為 blocker**；這些檔案尚未建立是正常的提案狀態。
- **Implementation / strict review 階段**才檢查實際檔案是否已建立、程式碼是否符合 spec、測試是否存在且通過，以及實作與規格是否一致。
- 若 spec 已列出自動化測試 task，但測試檔案尚未建立，Spec review 應檢查 task 的測試目標、TDD 計畫、預期 RED 失敗分支與證據紀錄欄位是否清楚；不得要求實作階段才會產生的測試檔或 RED／GREEN 執行結果。

### Spec 條文撰寫

- delta spec（`specs/**`）的 Requirement 與 Scenario **只寫系統行為規則**，不寫審查歷程或修改來由（例如「第 N 輪審查發現」「因 X change 改動而新增」「strict-reviewer 未發現、由人工審查找出」）。
- 理由：archive 會把 delta 原文複製進主 spec，之後的 MODIFIED 又整條沿用，歷程敘述會在主 spec 中累積，讓讀者分不清哪些是規則、哪些是歷史。
- 審查歷程、修改來由、取捨理由寫在 `design.md`（歸檔後仍保留在 archive）。

### Change 顆粒度（建立 proposal 時檢查）

- 一個 change 預計影響**超過 3 個 spec capability**，或 tasks **超過約 30 項**時，必須評估拆分，並在 proposal 說明拆或不拆的理由
- 拆分方式：依可獨立交付的功能切成數個 change，依賴方向單向（A → B → C），依序 apply、merge；後續 change 的 proposal 引用前一個的 spec，而非合併實作
- 理由：real-name-verification 一次跨 8 個 capability、72 項 task、111 個檔案，審查跑 7 輪且每輪都要重看全部 diff，瀏覽器驗證也須反覆切換帳號

### 無 spec 時的處理

- 若為**緊急 hotfix** 或**無對應 spec 的小型改動**，可先實作，但須：
  1. 在 commit / PR 說明中標注「無對應 spec」及原因
  2. 事後補上或更新對應 spec 文件
- **新功能**一律不適用此例外，仍須先有 spec 才能實作

### Task 執行後

完成任何 task 後，主動告知：

- 實作結果是否與 spec 描述一致
- 如有偏差，列出偏差點並詢問是否更新 spec
- **必須告知 spec 同步狀態**：這個改動有沒有影響現有 spec 描述的行為？需要更新哪個文件？
- 測試是否涵蓋本次變更的 Acceptance Criteria？未涵蓋者需說明原因
- 對適用 TDD 的 task，告知 RED、GREEN 與重構後測試的證據紀錄位置；N/A 時說明理由

---

## 防禦性檢查規則

適用：會做 I/O（DB、HTTP、檔案、快取）、管理資源（交易、連線、鎖）或處理外部輸入的程式碼，例如 Handler、`OrderService`、Repository、`UnitOfWork`；不適用：純 Domain 方法、getter/setter、純計算邏輯。

上述程式碼變更**全部完成、確定不會再改**之後、呼叫 `strict-reviewer` 之前，先套用 `.claude/skills/hardener/SKILL.md` 的檢查清單（參數驗證、Entity 存在性、狀態機驗證、`CancellationToken` 傳遞、並發處理、例外邊界、日誌遮蔽），確認這次變更的方法有沒有漏掉的防禦分支。

- **TDD 中的位置**：撰寫 Application / Repository 方法的 RED 測試時，先對照 hardener 檢查清單列出該方法應有的防禦分支（參數驗證、Entity 不存在、狀態不合法、取消、並發），把這些分支一併納入測試先行，讓防禦分支在實作時就有 RED → GREEN，而不是事後補
- 只在任務的程式碼變更**全部完成**時套用一次，不要每改一個檔案就套用一次；此時的角色是查漏，發現缺少的防禦分支一樣依 TDD 先補 RED 測試再修改程式碼，證據記錄在 `tasks.md`（可新增一項「hardener 補強」task）
- 套用後如果因此又修改了程式碼，改完再呼叫 `strict-reviewer`；如果套用後確認既有程式碼已經符合檢查清單、沒有修改，不需要重複呼叫
- 純 Domain Entity 內部方法、getter/setter、純計算邏輯不適用，見 Skill 文件的「適用範圍」

---

## 維護性規則（所有 task 適用，非強制阻擋，但必須提醒）

每次 task 完成後，主動告知以下項目的狀態：

- **重複邏輯**：這段邏輯是否已存在於 codebase 中，應該抽共用？
- **命名一致性**：變數、函式、API endpoint 命名是否與現有規範一致？

---

## 溝通風格

- 發現問題直接說，不要繞彎
- 有不確定的地方，明確標注「我不確定，需要你確認」
- 不要重複確認已經決定的技術選擇
