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
## 架構骨架

> 專案採 Clean Architecture 分層，依賴方向由外向內單向：
> WebApi → Infrastructure → Application → Domain
> Domain 為最內層，不得依賴任何其他層。

### 專案結構（.csproj 拆分，非資料夾模擬）

- `Domain`：Entity、Value Object、Domain Event、Repository/外部服務的 **interface 定義**。不 reference 任何其他專案。
- `Application`：Use case 邏輯（Handler）、DTO、跨 Entity 的協調邏輯。只 reference `Domain`。
- `Infrastructure`：EF Core 實作、外部 API 串接、檔案/快取/信件等技術細節，實作 `Domain` 定義的介面。reference `Domain` + `Application`。
- `WebApi`：Controller、DI 註冊、middleware。reference 全部。

### 強制規則

- `Domain.csproj` 禁止 `<ProjectReference>` 指向任何其他專案；PR review 時檢查此點。
- Repository / 外部服務介面一律定義在 `Domain`，實作放 `Infrastructure`——不得將介面與實作放在同一專案。
- Domain Entity 內部狀態一律 `private set`，外部只能透過方法修改（Rich Domain Model，禁止 Anemic Domain Model）。
- 跨 Entity 的判斷邏輯（需要查詢多個 Entity 或外部條件才能決定的規則）放 `Application`，單一 Entity 自己能判斷的規則放 `Domain`，不得混放。

### 何時可簡化（避免過度設計）

- Entity 數量少（< 5 個）時，Application 可直接注入 `DbContext`，不必每個 Entity 都建 Repository 介面；出現重複查詢邏輯時才抽出。
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

- 密碼、API Key 等一律放 `appsettings.Development.json`（不進版本控制）或環境變數，不寫死在程式碼中
- 正式環境建議使用 Secret Manager / Azure Key Vault 等機制注入

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

---

## 測試規範

- **測試命名慣例**：`MethodName_Scenario_ExpectedResult`（例如 `GetUserById_WhenNotFound_ReturnsNull`）
- **層級界定**：單元測試不碰 DB / 網路（以 mock 隔離）；整合測試才驗證真實 DB / API 邊界
- **涵蓋範圍**：每一條 Acceptance Criteria 至少對應一個測試；業務核心邏輯優先確保涵蓋
- **測試框架**：xUnit（預設）；斷言可搭配 FluentAssertions；mock 用 Moq / NSubstitute

---

## OpenSpec 工作流規則

### Task 執行前

每次執行 task 前，先確認：

1. 這個 task 對應哪一條 spec？（必須能指向具體文件與段落）
2. Acceptance Criteria 是否已定義且可測量？每一條是否已對應至少一項測試任務（xUnit / NUnit 單元測試或整合測試）？未對應者禁止進入實作階段。
3. 這個 task 完成後，哪些 spec 文件需要同步更新？

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
