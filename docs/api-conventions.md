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
| 時間欄位後綴 | `DateTime` 時間欄位一律以 `Utc` 結尾（`HeldUntilUtc`、`ExpiresAtUtc`）。範圍：Domain、Application、WebApi 組件中 public 型別的 public 實例屬性（不含方法參數）；唯一例外是 `IDateTimeProvider.UtcNow`（.NET 慣用名）。由 `UtcSuffixNamingRuleTests` 強制，新增例外須在該測試寫明理由 | `UtcSuffixNamingRuleTests` |
| 列表分頁 | offset 分頁（`list-pagination` 能力）：查詢參數 `page`（從 1 起，預設 1、上限 10000）、`pageSize`（預設 20、上限 100）；回應 `{ items, page, pageSize, totalCount }`；`page`／`pageSize` 越界或非整數回 400 `Validation`（逐欄位 `errors`，鍵為小寫 `page`／`pageSize`），不靜默截斷；`page` 超過最後一頁回 200 與空 `items`（`totalCount` 為實際總數）；排序必須確定（`CreatedAtUtc` 新到舊、`Id` 升冪）。共用型別為 `PageRequest`／`PageRequestValidator`／`PagedResultDto<T>`（`Application/Common/Paging`）。**已套用**：`GET /api/orders`、`GET /api/admin/orders`、`GET /api/admin/events`（`admin-event-list-pagination`，排序為 `CreatedAtUtc` 新到舊、null 最後、`Id` 升冪）。`GET /api/events`（`public-event-list-pagination`，公開；排序同 `GET /api/admin/events`；後端單一整份快取 `query-cache:events:list:v3` 在記憶體切頁，不同 `page`／`pageSize` 共用同一筆快取，舊鍵 `list`、`list:v2` 不讀不寫；單一活動端點 `GET /api/events/{id}` 與分頁無關，仍可取得任何一頁的活動）。**判定不做分頁**：`GET /api/admin/venues`（少量固定資料）、`GET /api/organizers/mine`（受限於單一會員的 Organizer 數）、`GET /api/admin/organizers`（全平台主辦方清單，會隨申請增加，目前規模可接受並列為觀察項目，規模成長時另案分頁） | `order-list-pagination` |

## 4. 修改本文件的時機

- 新增 `ErrorType`、Policy、Claim 或路由前綴時。
- 決定上方「待決定」項目時：先在對應 change 的 `design.md` 記錄決策，再回頭更新本文件。
- 新增或改造列表端點的分頁（含 `GET /api/events`）時：更新第 3 節「列表分頁」列的「已套用」「後續 change」清單。
- 提案偏離本文件任何一條，或導入新的跨模組慣例時：在該 change 的 `design.md`「跨模組慣例對照」小節記錄理由，並於同一 change 更新本文件。
- 變更時間欄位後綴規則或其例外清單時：同步更新 `UtcSuffixNamingRuleTests` 與本文件第 3 節。
