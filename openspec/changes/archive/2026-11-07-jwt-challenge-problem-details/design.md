## Context

`AuthenticationServiceCollectionExtensions.AddJwtBearer` 沒有設定 `JwtBearerEvents`。預設 `JwtBearerHandler` 的行為（尚待 RED 階段實測確認細節）：

- 401：挑戰時設定狀態碼與 `WWW-Authenticate` header（無 token 為 `Bearer`；驗證失敗含 `error="invalid_token"`，並依失敗類型（過期、簽章錯誤等）可能帶 `error_description`，以 1.1 基準為準），**不寫 body**。
- 403：`ForbidAsync` 設定 403，不寫 body。`[Authorize(Policy=...)]` 失敗與控制器 `Forbid()` 都走同一條路徑（`AdminEventsController` 等 9 處 `Forbid()` 是防日後漏掛 Policy 的防禦分支，Policy 保證實務上走不到）。

`Program.cs` 的 `AddProblemDetails()` 不會套用到認證挑戰。其他錯誤（業務錯誤、`GlobalExceptionHandler`、限流）皆為 `ProblemDetails`，並帶 `traceId = HttpContext.TraceIdentifier`（見 `api-error-responses` 主 spec）。前端 `httpClient.ts` 對 401 只看狀態碼做 single-flight 換發，不讀 body；改動後 `problem` 會由 `null` 變為無 `detail` 的物件，訊息仍 fallback 為含狀態碼的通用文字，且前端沒有任何邏輯比對 `title` 為 `Unauthorized`／`Forbidden`，故不受影響。業務失敗轉換的 401／403（登入失敗、非訂單本人等）本來就是帶 `detail` 的 `ProblemDetails`，不在本 change 範圍。

## Goals / Non-Goals

**Goals**

- 401／403 與其他錯誤同為 `ProblemDetails`（`status`、`title`、`traceId`），可與 log 對照。
- 狀態碼與 `WWW-Authenticate` header 不變。
- 補整合測試守護 401／403 的 body 與 header。

**Non-Goals**

- 不加 `detail`；不擴及 404／405 等其他空 body 回應；不改前端；不改 Policy 或授權規則。

## Decisions

### 決策 1：用 `JwtBearerEvents.OnChallenge`／`OnForbidden`，不用 `UseStatusCodePages`

`UseStatusCodePages` 會讓所有空 body 的 4xx（含 404、405）都被改寫，超出範圍，且框架預設 factory 的 `traceId` 取 `Activity.Current.Id`，與 `HttpContext.TraceIdentifier` 不同（`error-response-title-traceid` design 決策 2 已實測）。事件掛在認證 scheme 上，範圍精確。

- `OnForbidden`：預設流程已設好 403 且不寫 header，事件內直接寫 body。
- `OnChallenge`：預設流程在事件之後才設狀態碼與 header，所以先寫 body 會讓 header 設不上。做法為事件內 `HandleResponse()`，自行設 401、依 `context.Error`／`context.ErrorDescription`（事件觸發前已由框架填好）重建與預設相同格式的 `WWW-Authenticate`，再寫 body。**重建結果是否與預設逐字相同，由 RED 階段以整合測試的同一個 factory 與固定 `exp` 的 token 錄下現況 header 作為基準（過期案例的 `error_description` 含 token 的 `exp`，不同 token 不可比；開發環境 curl 錄得的僅供人工參考），GREEN 後在同一 fixture 逐字比對**；若無法逐字相同，改以不攔截預設流程的方式（例如在 `OnStarting` 之前保留預設 header）重新評估，並回到 design 更新本決策。

### 決策 2：Title 與 traceId

`title` 沿用 `ErrorType` 的名稱 `Unauthorized`／`Forbidden`，與業務錯誤同一組代碼；`traceId` 取 `HttpContext.TraceIdentifier`。以 `ProblemDetails` 序列化（`application/problem+json`），不含 `detail`。

### 決策 3：不帶 detail

驗證失敗原因（過期、簽章錯誤）已在 `WWW-Authenticate` header，body 重複會多洩漏資訊給匿名呼叫者；也與 500 刻意無 `detail` 的處理一致。

### 決策 4：控制器 `Forbid()` 的驗證

`Forbid()` 與 Policy 失敗同走 `OnForbidden`，但 Policy 保證控制器內的 `Forbid()` 分支實務上無法經 HTTP 觸發。AER-016 以最小侵入方式驗證：在測試用 `WebApplicationFactory` 註冊一個只存在於測試的端點回傳 `Forbid()`。若 factory 無法承載，停止並回報，由使用者決定是否在 design 補正式例外（直接呼叫 `OnForbidden` 的單元測試證明不了控制器會走到該事件，依 CLAUDE.md 不得無例外紀錄地取代 AC）。

### 決策 5：Capability 與介面歸類

僅在既有 `api-error-responses` 新增一條 Requirement，不另開 capability。事件處理放 `WebApi`（組合根與 HTTP 細節），不新增跨層介面，不需介面歸類說明。

### 決策 6：安全確認

- 外部輸入：無新增（只讀取請求的 `TraceIdentifier` 與框架填的 `Error`）。
- DB：無。
- 權限：不改授權規則，只改拒絕時的回應格式；未授權者看到的資訊不增加（不含 `detail`）。
- 前端：無變更。

## Risks / Trade-offs

- **`WWW-Authenticate` 重建與預設不一致**：影響標準 Bearer 客戶端的行為。以決策 1 的基準錄製與逐字比對降低風險。
- **未來升級 ASP.NET Core 後預設 header 格式改變**：重建邏輯不會跟著變。以整合測試斷言 header 內容，升級時會失敗而被發現。
- **404／405 等其他空 body 回應仍不是 `ProblemDetails`**：刻意不在本 change 處理，若要統一另開 change。

## Migration Plan

無 DB、無設定變更。純行為新增，部署後 401／403 多了 body；前端不受影響。回滾即移除 `JwtBearerEvents` 設定。

## Open Questions

無。
