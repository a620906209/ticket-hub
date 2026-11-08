# API 慣例不一致項目待辦

> 來源：`docs/api-conventions.md` 第 2、3 節標示的「不一致／待決定」項目。
> 建立日期：2026-11-07。每項決策先寫入對應 change 的 `design.md`，完成後回頭更新 `api-conventions.md` 並移除該條目。
> 顆粒度依 CLAUDE.md「Change 顆粒度」：單一 change 影響不超過 3 個 spec capability、tasks 不超過約 30 項。

## 執行順序

| 順序 | 項目 | 類型 | 依賴 |
|---|---|---|---|
| 0 | 查證三項現況 | 調查，無 spec | 無 |
| 1 | `error-response-title-traceid` | change | 0 |
| 2 | `jwt-challenge-problem-details` | change（條件式） | 0-A 查證結果 |
| 3 | `validation-error-fields` | change | 1（共用錯誤格式決策） |
| 4 | `time-field-naming-rule` | 文件規範（可不開 change） | 0-C |
| 5 | `time-field-rename` | change（條件式，破壞性） | 4 |
| 6 | `list-pagination-convention` | change（條件式） | 0-B |

## 0. 查證（先做，結果決定 2、4、5、6 要不要開）

- [x] **0-A** 測 JWT middleware 產生的 401／403 回應 body 是否為 ProblemDetails（未登入、token 過期、角色不足各測一次）
- [x] **0-B** 盤點所有列表端點：有無分頁參數、回應包裝形式、預期資料量
- [x] **0-C** 盤點 DTO／Entity 的 DateTime 欄位，列出有／無 `Utc` 後綴者及對外暴露的 JSON 欄位名
- [x] 查證結果記錄於本文件下方「查證結果」一節

## 1. `error-response-title-traceid`

對應不一致項目：`Title` 語意混用、業務錯誤沒有 `traceId`。

- [x] 決策：500、413 的 `Title` 改為機器代碼（例如 `InternalServerError`、`PayloadTooLarge`）；`Detail` 是否保留人類可讀句子
- [x] 決策：`traceId` 取得方式（`Activity.Current?.Id` 或 `HttpContext.TraceIdentifier`），與框架層錯誤一致
- [x] `ResultExtensions.CreateProblemResult` 補 `traceId` extension
- [x] `GlobalExceptionHandler` 的 `Title` 改為代碼
- [x] 檢查 `web/src/api/httpClient.ts` 與各處依賴 `Title` 字串的判斷，確認不會因改名而失效
- [x] 既有測試中斷言舊 `Title` 者同步更新
- [x] 更新 `api-conventions.md` 第 2 節

## 2. `jwt-challenge-problem-details`（條件式：0-A 發現不是 ProblemDetails 才開）

- [x] 以 `JwtBearerEvents.OnChallenge` / `OnForbidden` 輸出 ProblemDetails，`Title` 沿用 `Unauthorized` / `Forbidden`
- [x] 帶 `traceId`
- [ ] 若 0-A 確認已是 ProblemDetails，則本項改為「僅更新 `api-conventions.md` 移除未驗證註記」

## 3. `validation-error-fields`

對應不一致項目：驗證錯誤沒有逐欄位結構。

- [x] 決策：回應格式（沿用 `ValidationProblemDetails` 的 `errors`，或自訂）
- [x] 決策：`Result` / `Error` 型別如何攜帶欄位資訊（影響 Application 層）
- [x] 保持向下相容：既有 `Detail` 字串仍保留
- [x] 前端是否消費欄位錯誤：決定範圍，不消費則只確認不破壞既有表單
- [x] 更新 `api-conventions.md` 第 2 節

## 4. `time-field-naming-rule`

對應不一致項目：時間欄位後綴不一致。

- [x] 決策：規則（例如「新欄位一律 `Utc` 後綴」或「全部統一」）
- [x] 決策：舊欄位處置（列入例外清單，或排入第 5 項改名）
- [x] 規則寫入 `api-conventions.md` 第 3 節；若無程式碼變更可不開 change，走 CLAUDE.md「無 spec 小型改動」並於 commit 標注

## 5. `time-field-rename`（取消，併入第 4 項）

第 4 項的盤點顯示只有兩個欄位缺後綴且未對外暴露於 JSON，已在 `time-field-naming-rule` 一併改名，不另開 change。

## 6. `list-pagination-convention`（條件式：0-B 發現有需要才開）

- [x] 決策：統一請求參數與回應包裝（`page`、`pageSize`、`items`、`totalCount`；offset 分頁），見 `order-list-pagination` design.md 並已寫入 `api-conventions.md` §3
- [x] 先套用在 `GET /api/orders`、`GET /api/admin/orders` 驗證（`order-list-pagination`）
- [ ] 遷移其餘端點：`GET /api/admin/events`（`admin-event-list-pagination`）、`GET /api/events`（公開、整份快取，需獨立設計快取鍵）各一個後續 change
- [x] 上限保護：`pageSize` 最大 100、`page` 最大 10000，避免被當作資料匯出
- [x] `venues`、`organizers/mine`、`admin/organizers` 判定不做分頁（理由見 `api-conventions.md` §3）

## 查證結果

查證日期：2026-11-07。

### 0-A JWT 401／403 回應 body：**不是 ProblemDetails**（401、403 皆已由整合測試實測）

- `src/` 內沒有 `JwtBearerEvents`／`OnChallenge`／`OnForbidden`／`UseStatusCodePages`；`Program.cs:49` 的 `AddProblemDetails()` 不會套用到認證挑戰。
- 實測（容器 `api`，`GET /api/orders`）：
  - 無 token → `401`，`Content-Length: 0`，`WWW-Authenticate: Bearer`，無 body。
  - 無效 token → `401`，`Content-Length: 0`，`WWW-Authenticate: Bearer error="invalid_token"`，無 body。
- 403（角色／Policy 不足）已由整合測試（`JwtChallengeProblemDetailsTests`）實測：改動前為空 body，改動後為 ProblemDetails。
- 既有測試（如 `EventManagementAuthorizationMatrixTests`）只斷言狀態碼，不斷言 body，所以此缺口沒有被守護。
- 結論：第 2 項 `jwt-challenge-problem-details` **需要開**。

### 0-B 列表端點：**全部無分頁**

- 無任何 `page`／`pageSize`／`skip`／`take` 參數。
- 列表型 GET 共 7 個，皆無參數（僅 `AdminOrganizers` 有 `status` 篩選）：`GET /api/events`、`/api/orders`、`/api/admin/orders`、`/api/admin/events`、`/api/admin/venues`、`/api/organizers/mine`、`/api/admin/organizers`。
- 風險最高者為 `/api/orders`（會員訂單隨時間成長）與 `/api/admin/orders`、`/api/admin/events`；其餘資料量天然有界。
- 結論：第 6 項**有需要，但範圍可縮小**到訂單與活動列表，其餘列入「資料量有界，不做」。

### 0-C 時間欄位後綴：**實際不一致點只有認證模組**

- 活動、訂單、票券、主辦方、排隊、DTO 的 DateTime 欄位**全部**已有 `Utc` 後綴。
- 缺 `Utc` 的只有 `RefreshToken.ExpiresAt`、`PasswordResetToken.ExpiresAt`（Domain Entity，含建構子參數 `expiresAt`）。
- `api-conventions.md` 所說「`expiresAt` 沒有後綴」即指此；是否對外暴露於 JSON（例如 token 回應 DTO）尚未確認，需再查 `AuthController` 回應型別。
- 結論：第 5 項（全面改名）**不需要**；第 4 項規則可直接定為「一律 `Utc` 後綴」，例外只剩兩個 Entity，是否改名視其 JSON 暴露與 DB migration 成本決定。

### 對待辦的影響

| 項目 | 結論 |
|---|---|
| 2 `jwt-challenge-problem-details` | 開（0-A 確認缺口） |
| 4 `time-field-naming-rule` | 規範 + 兩個 Entity 欄位小改，併成一個小 change |
| 5 `time-field-rename` | 取消（併入 4） |
| 6 `list-pagination-convention` | 開，範圍縮到訂單／活動列表 |
