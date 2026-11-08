# API 跨模組共用慣例

> 本文件整理**目前程式碼實際採用**的跨模組決策（認證、錯誤格式、資料命名），來源以括號標註。
> 標「不一致」「待決定」者是現況與意圖不一致之處，尚未做決定，不代表已核准的規則。
> 2026-11-07 依 `src/` 與 `web/src/api/httpClient.ts` 反推建立。

## 1. 認證與授權

| 項目 | 現況 | 來源 |
|---|---|---|
| 機制 | JWT Bearer；Access Token + Refresh Token | `AuthenticationServiceCollectionExtensions.cs` |
| Access Token 效期 | 預設 30 分鐘（可設 1–1440） | `JwtOptions.AccessTokenExpirationMinutes` |
| Refresh Token 效期 | 預設 14 天 | `AuthOptions.RefreshTokenExpirationDays` |
| 驗證項目 | Issuer、Audience、Lifetime、簽章金鑰皆驗證；ClockSkew 30 秒；SigningKey ≥ 32 字元，啟動時驗證（Fail Fast） | 同上 |
| Claim | `MapInboundClaims = false`；角色用 `ClaimTypes.Role`；主辦方用自訂 `CustomClaimTypes.OrganizerId` | `JwtTokenService`、`CustomClaimTypes` |
| Policy | `AdminOnly`（角色 Admin）、`RequireOrganizerContext`（token 須帶合法 OrganizerId） | `AuthorizationPolicies` |
| Controller 預設 | 會員資源類 Controller 以 `[Authorize]` 保護；`/api/admin/*` 用 Policy；匿名端點僅 `auth`、`captcha`、公開活動查詢 | 各 Controller |
| 前端 | 統一由 `httpClient.ts` 加 `Authorization: Bearer`；收到 401 時 single-flight 換發一次並重放原請求，重試後仍 401 則丟出錯誤 | `web/src/api/httpClient.ts` |

## 2. 錯誤格式

所有錯誤回應為 RFC 7807 `ProblemDetails`（`application/problem+json`）。

### 業務錯誤（Handler 回傳 `Result`）

由 `ResultExtensions.CreateProblemResult` 統一轉換：`Title = ErrorType.ToString()`、`Detail = error.Message`、`Status` 依下表，並帶 `traceId` extension（值為 `HttpContext.TraceIdentifier`，與 log 的 `TraceId` 相同）。**`Title` 是前端判斷錯誤種類的穩定代碼**，不可隨意改名。

| ErrorType | HTTP | 備註 |
|---|---|---|
| Validation | 400 | |
| InvalidTicketSignature | 400 | 前端依 Title 區分泛用 400 |
| CaptchaInvalid | 400 | 同上 |
| Unauthorized | 401 | |
| Forbidden | 403 | |
| QueueAdmissionRequired | 403 | 前端據此導向排隊畫面 |
| RealNameRequired | 403 | 前端據此導向實名登記 |
| NotFound | 404 | |
| Conflict | 409 | |
| HolderVerificationRequired | 409 | 與「已核銷」的 Conflict 區分 |
| SalesNotOpen / SalesClosed | 409 | 活動狀態不允許，刻意不用 403 |

新增 `ErrorType` 時必須同步在 `ResultExtensions` 加對應 status；未列出的類型會落入 `_ => 400`。

### 框架層錯誤

- `GlobalExceptionHandler`：未預期例外 → 500，`Title = "InternalServerError"`；`BadHttpRequestException` → 其原本狀態碼，`Title` 為機器代碼：413 `PayloadTooLarge`、408 `RequestTimeout`、400 `BadRequest`，其他狀態碼為 reason phrase 僅保留英數字元（框架無 reason phrase，或移除非英數字元後為空字串時 `BadRequest`）。皆帶 `traceId` extension，不含 `Detail`。
- 認證挑戰／授權拒絕：JWT 認證 scheme 產生的 401（未帶、無效或過期 token）與 403（角色或 Policy 不足、控制器 `Forbid()`）為 `application/problem+json`，含 `status`、`Title`（`Unauthorized`／`Forbidden`）與 `traceId`（`HttpContext.TraceIdentifier`），不含 `Detail`；401 的狀態碼與 `WWW-Authenticate` header 與框架預設逐字相同。業務失敗轉換的 401／403 不屬此類，仍帶原 `Detail`。實作在 `JwtBearerProblemDetailsResponses`。
- 限流：429，`Title = "TooManyRequests"`，帶 `Retry-After` header（若可得）與 `traceId`。

### 驗證錯誤的逐欄位 `errors`

- 請求 Validator（FluentValidation）失敗的 400 `Validation` 為 `ValidationProblemDetails`：除 `status`、`title`、`detail`、`traceId` 外，另含 `errors`（`{ "欄位": ["訊息", ...] }`）。`detail` 仍是所有訊息依回報順序以單一空白串接的字串，只讀 `detail` 的 client 不受影響。
- Handler 以 `ValidationResult.ToValidationError()` 轉換（`Error.Validation(message, fieldErrors)`）；業務規則類的 `Validation` 錯誤（例如下單「所選項目不屬於同一個活動」）沒有對應欄位，維持 `Error.Validation(message)`，回應不含 `errors`。
- 鍵格式由 WebApi 的 `FieldErrorKeyFormatter` 決定：以 `.` 分隔的每一段首字母轉小寫、其餘不變，集合索引保留（`Selections[0].Quantity` → `selections[0].quantity`）；轉換後相同的鍵合併其訊息。
- 模型繫結失敗（JSON 格式錯誤、型別不符、非 nullable 參考型別缺漏或為 null 的隱含必填）由 `InvalidModelStateResponseFactory` 輸出同格式：`title = Validation`、無 `detail`、`traceId` 取 `HttpContext.TraceIdentifier`（不再是 Activity id）。框架內部的鍵會被處理：移除 `$.` 前綴（括號路徑如 `$['x y']` 移除 `$` 後括號段原樣保留）、濾除 action 的 body 參數名稱鍵；只剩這類鍵或本文為空時，訊息列在空字串鍵 `""` 下。
- 前端 `ApiError.problem.errors` 保留此欄位，但 `ApiError.message` 仍取 `detail`；目前沒有畫面依 `errors` 逐欄位顯示。

### 不一致／待決定

（目前無）

## 3. 資料命名

| 項目 | 現況 | 來源 |
|---|---|---|
| 路由 | `api/{小寫複數資源}`；管理端為 `api/admin/{資源}`；例外：`api/auth`、`api/captcha`、`api/events/{id:guid}/queue`。無版本號 | `[Route]` 彙整 |
| JSON 欄位 | `AddControllers()` 預設 → camelCase；未自訂 `JsonNamingPolicy`、未註冊 `JsonStringEnumConverter`（enum 序列化方式需依個別 DTO 確認，已知狀態欄位多為 `ToString()` 後的字串） | `Program.cs` |
| DB | EF Core 預設：表名 PascalCase 複數（`Events`、`EventSeats`、`Members`），未使用 snake_case 轉換 | `Configurations/*` |
| 時間 | 一律 UTC，由 `IDateTimeProvider.UtcNow`（`DateTime`）取得，禁止直接用 `DateTime.UtcNow` | `IDateTimeProvider` |
| 時間欄位後綴 | **不一致**：部分有 `Utc`（`HeldUntilUtc`、`AdmissionExpiresAtUtc`），部分沒有（`expiresAt`） | 各 Entity／DTO |
| 列表分頁 | 未發現統一的分頁參數／回應格式，待決定 | — |

## 4. 修改本文件的時機

- 新增 `ErrorType`、Policy、Claim 或路由前綴時。
- 決定上方「不一致」項目時：先在對應 change 的 `design.md` 記錄決策，再回頭更新本文件。
