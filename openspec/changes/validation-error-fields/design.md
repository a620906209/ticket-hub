# Design

## Context

動機與範圍見 proposal.md。現況：

- 14 處（`LoginHandler`、`ResetPasswordHandler`、`CreateEventHandler`、`SetEventQueueModeHandler`、`RegisterMemberHandler`、`RegisterRealNameHandler`、`UpdateMyProfileHandler`、`OrderService`、`ApplyForOrganizerHandler`、`SwitchOrganizerContextHandler`、`JoinPurchaseQueueHandler`、`CreateTicketTypeHandler`、`CreateSeatMapHandler`、`CreateVenueHandler`）各自以 `Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage)))` 轉換 FluentValidation 結果，`PropertyName` 被丟棄。
- `Error` 只有 `Type`、`Message`；`ResultExtensions.CreateProblemResult` 以 `ProblemDetailsWithTraceIdResult` 在執行時補 `traceId`。
- 模型繫結失敗由 `[ApiController]` 預設行為產生 400，`Program.cs` 目前只呼叫 `AddControllers()`，未自訂 `ApiBehaviorOptions`。
- 前端 `ProblemDetails` 介面已含 `[key: string]: unknown`，`ApiError.message` 取 `detail`；`web/src` 與測試中沒有依賴框架預設 title 句子（`One or more validation errors occurred.`）的程式碼（已 grep 確認）。

## Goals / Non-Goals

**Goals:**

- 兩條 400 路徑（Handler 的 FluentValidation 失敗、框架模型繫結失敗）輸出同一個 `errors` 形狀與同一套鍵命名規則。
- 向下相容：`status`、`title`（Handler 路徑）、`detail`、`traceId` 行為不變。
- 14 處重複轉換收斂為單一擴充方法。

**Non-Goals:**

- 業務規則類 `Validation` 錯誤欄位化（它們沒有單一欄位語意，例如「所選項目須屬同一活動」）。
- 前端逐欄位顯示；`detail` 仍是前端唯一顯示來源。
- 為模型繫結 400 補 `detail`：框架預設本來就沒有，不在本次範圍（前端對這類 400 顯示通用狀態碼文字，與現況相同）。
- 改變任何其他 `ErrorType` 的格式。

## Decisions

### 決策 1：`Error` 攜帶可為 null 的欄位錯誤字典

`Error` 新增 `IReadOnlyDictionary<string, string[]>? FieldErrors`，新增 `Error.Validation(string message, IReadOnlyDictionary<string, string[]> fieldErrors)` 多載；舊的單參數 `Validation(string)` 保留，其 `FieldErrors` 為 null。

- 理由：保留單參數多載讓 `OrderService` 等業務規則錯誤、以及所有非驗證類 `ErrorType` 不需修改；null 代表「沒有欄位語意」，對應 spec AER-023。
- 備選：把欄位資訊塞進 `Message`（字串解析）——脆弱，否決；新增 `ErrorType.FieldValidation`——會改變 `Title`，破壞前端以 `Validation` 判斷的相容性，否決。

### 決策 2：Application 層只傳原始屬性路徑，camelCase 轉換集中在 WebApi

FluentValidation 的 `PropertyName`（例如 `Selections[0].Quantity`）原樣放進 `FieldErrors`；轉成 JSON 鍵（每段首字母小寫、保留索引）由 WebApi 層單一函式 `FieldErrorKeyFormatter` 負責，Handler 路徑與模型繫結路徑共用。

- 理由：鍵命名是 JSON 輸出慣例，屬 WebApi 關注點；放在 Application 會讓 Application 知道 JSON 命名；兩條路徑共用一個函式才能保證鍵規則一致（spec 的「errors 欄位名稱採 camelCase」只會有一份實作）。
- 備選：在 Application 擴充方法內直接轉 camelCase——簡單，但模型繫結路徑仍需另一份轉換，兩份實作會漂移，否決。
- 轉換規則：以 `.` 分段，每段第一個字元小寫、其餘不動、`[n]` 原樣保留；模型繫結鍵先移除開頭的 `$.`（System.Text.Json 路徑前綴）；`$` 或空字串視為請求本文層級，鍵為空字串。兩個原始鍵轉換後相同時，訊息合併進同一陣列而不是覆寫。

### 決策 3：Application 擴充方法取代 14 處重複

在 `ProjectC.Application/Common/Validation/` 新增 `ValidationResultExtensions.ToValidationError(this ValidationResult)`：`Message` 與舊行為逐字相同（`ErrorMessage` 依 `Errors` 順序以單一空白串接），`FieldErrors` 依 `PropertyName` 分組。14 處呼叫端改為 `Error.Validation` 的呼叫替換成此方法。

- 理由：`detail` 向下相容靠同一份串接邏輯保證（spec AER-022）；日後調整驗證錯誤格式只改一處。
- `ResetPasswordHandler` 同方法內另有兩處 `Error.Validation(InvalidTokenMessage)` 是業務判斷，不替換。

### 決策 4：`CreateProblemResult` 依 `FieldErrors` 選擇 ProblemDetails 型別

有 `FieldErrors` 時建 `ValidationProblemDetails`（`Errors` 為轉換後鍵），否則維持 `ProblemDetails`；`Title`／`Detail`／`Status` 邏輯不變，`traceId` 仍由 `ProblemDetailsWithTraceIdResult` 在執行時補。

- 理由：`ValidationProblemDetails` 是 ASP.NET 標準型別，序列化出標準的 `errors` 物件，不必自訂 JSON 結構。

### 決策 5：模型繫結 400 以 `InvalidModelStateResponseFactory` 統一

在 `AddControllers()` 後以 `ConfigureApiBehaviorOptions` 設定 `InvalidModelStateResponseFactory`，從 `ModelState` 取出錯誤，鍵經決策 2 的函式轉換，建 `ValidationProblemDetails`（`Status = 400`、`Title = "Validation"`、不設 `Detail`），回傳 `ProblemDetailsWithTraceIdResult`，`traceId` 明確取 `HttpContext.TraceIdentifier`（不經 `ProblemDetailsFactory`，其 traceId 為 `Activity.Current?.Id`，兩者不同，見 archive `error-response-title-traceid` design）。

細節：(1) 只濾除「請求本文參數」的名稱（如 `request`，以原始鍵、區分大小寫比對）；query／route 參數繫結失敗時鍵本身就是參數名稱，不濾除。本文參數以 `ActionDescriptor` 參數的 `BindingInfo.BindingSource == Body` 判定，不寫死字串。若濾除後 `errors` 無任何鍵，其訊息改列於空字串鍵，確保鍵數必大於 0 且不含參數名鍵（防禦性規則，由工廠單元測試直接以只含參數名鍵的 ModelState 覆蓋；實測字面值 `null` 本文的鍵為 `""` 與參數名鍵，濾除後本來就恰為 [`""`]，AER-034 不依賴此後備）。`ConfigureApiBehaviorOptions` 須接在 `AddControllers()` 之後，否則框架的預設設定會覆蓋——本文反序列化失敗或為空時框架會另加 `"request": ["The request field is required."]`，該鍵名會隨參數改名而變，不屬於 JSON 路徑；(2) 沒有錯誤訊息的項目不輸出（避免空陣列鍵）；(3) `ErrorMessage` 為空但有例外的項目，訊息補 `The input was not valid.`（框架原本行為）；(4) 非 nullable 參考型別的隱含必填只在欄位缺漏或為 null 時觸發（例如 `title` 缺漏或 null），發生在模型繫結階段，走本路徑、無 `detail`；空字串模型繫結通過，由 Handler 路徑的 Validator 處理（`NotEmpty` 失敗，帶 `detail` 與 `errors.title`，AER-035），Validator 規則（如長度上限）也在此路徑。設定放在 `Extensions/` 下的獨立擴充方法，`Program.cs` 只多一行呼叫，維持 `webapi-composition-root-cleanup` 後 Program.cs 的結構。

- 理由：與 Handler 路徑共用同一個 result 型別與 `traceId` 機制。
- 備選：改用 `SuppressModelStateInvalidFilter` 並在 filter 內處理——改動面更大，否決。
- 已知差異（接受）：框架產生的模型繫結訊息（例如 `The JSON value could not be converted to System.Int32. Path: $.maxTicketsPerOrder ...`）原樣放入陣列，可能含型別名稱；這些是框架既有行為且本來就回傳給匿名 client，本次不過濾。

### 決策 6：前端只加型別

`ProblemDetails` 介面加 `errors?: Record<string, string[]>`，`ApiError.message` 規則不動。以 `httpClient.test.ts` 新增測試守護 spec AER-030；既有表單頁測試（Vitest）全綠守護 AER-031。

### 決策 7：審查取捨記錄（spec-reviewer 第 1 輪，採推薦方案）

- `detail` 以驗證器回報順序串接（與舊行為相同），不以 `errors` 攤平順序定義：欄位分組會讓交錯的訊息重新排序，兩者無法同時「逐字相同」。
- 鍵碰撞合併是實作細節，不列為 spec 行為保證，由 `FieldErrorKeyFormatter` 單元測試守護；目前 Validator 的屬性名不會因 camelCase 轉換而碰撞。
- 請求本文層級（空字串鍵）只在模型繫結路徑出現，Handler 路徑的 FluentValidation 針對具體屬性，故不為 Handler 路徑設空字串鍵 Scenario。
- `errors` 鍵「與 JSON 欄位路徑一致」的前提：`src/` 內沒有 `JsonPropertyName`，CLR 屬性名首字母小寫即 JSON 名稱；日後若引入自訂 JSON 名稱，須改為從序列化設定取得名稱。
- AER-023 的判準為「由請求 Validator 產生」對比「Handler／Service 內的業務判斷」，不以「是否跨項目」區分：`PlaceOrderRequestValidator` 的總張數上限與同座位重複雖是跨項目規則，但由 Validator 產生，會帶 `errors.selections`。
- 鍵格式化處理 `$['a.b']` 括號路徑：括號段由 client 送出含 `.` 或空白的屬性名時產生（與 DTO 無關），先移除 `$`／`$.` 前綴，括號段內的 `.` 不拆段、鍵名原樣保留，括號段之外的每段仍首字母小寫。Opus 交叉審查指出原先「原樣保留（含 `$`）」違反「不帶 `$.` 前綴」。client 自訂、以 `$` 開頭的屬性名（如 `$x`）STJ 以點號路徑回報，鍵原樣保留為 `$x`，不視為框架前綴；括號段內若含 `']`，段落會提早結束（STJ 不轉義），僅影響該回音鍵的大小寫外觀，接受。
- `InvalidModelStateResponseFactory` 實際套用到所有 ModelState 錯誤（含 query、route），格式一致；spec 只對本文繫結設 Scenario。

## 安全確認（CLAUDE.md 安全強制規則）

本次不新增外部輸入、不讀寫 DB、不改認證授權：輸入驗證層級維持原樣（FluentValidation 於 Handler、模型繫結於框架）；無 SQL／shell 拼接；無 DB 參數化或 N+1 問題；權限邏輯不變；前端不將使用者輸入渲染進 DOM（只保留 `errors` 資料、不顯示）。回應內容限於框架與 Validator 既有的訊息，不新增資訊洩漏面。

### 決策 8：design-hardener 判斷

spec-reviewer 第 2 輪 PASS 後評估是否跑 design-hardener：本次不新增相依套件、不涉及安全邊界變更（認證授權、加密、機敏資訊）、不改容器 runtime、不引入新的失效模式（純輸出格式轉換），不符合觸發條件，**不執行**。

## Risks / Trade-offs

- [模型繫結的 `ModelState` 鍵形式依錯誤種類而異（`$.field`、`field`、`""`、`$`），spec 的 AER-026～029 若假設錯誤會在 GREEN 才發現] → 實作的第一個 RED 任務先以整合測試對真實端點實測各種錯誤的實際鍵，再據以定稿測試斷言；若實測與 spec 描述不符，回頭修 spec 而不是改測試去遷就。
- [模型繫結 400 的 `title` 由句子改為 `Validation`，外部 client 若依賴舊句子會壞] → 專案內前端與測試已確認無依賴；本專案無已知外部 client。
- [鍵轉換後碰撞（例如 `Title` 與 `title` 同時存在）] → 決策 2 以合併陣列處理，不覆寫、不丟例外。
- [14 處呼叫端一次改動，漏改一處會讓該端點仍無 `errors`] → 每個改動端點由整合測試或 Handler 單元測試斷言 `FieldErrors` 非 null；`grep` 確認 `string.Join(" ", validation.Errors` 在 `src/` 內歸零作為完成條件。

## Migration Plan

無 DB、無設定變更。單一版本部署即可；`errors` 為新增欄位，舊 client 忽略。回退為還原 commit，無資料副作用。

## hardener 適用性

本次新增的是純轉換邏輯（擴充方法、鍵格式化函式）與組態，不做 I/O、不管理資源；14 處 Handler 的改動只是替換轉換呼叫，不改變其 I/O 與資源管理行為。hardener 檢查清單適用範圍判斷為不適用，於 tasks.md 以 N/A 標示並說明。

### 決策 9：交叉審查取捨記錄（Claude Code Opus 審查＋ spec-reviewer 第 3、4 輪，採推薦方案）

- 非 nullable 參考型別的隱含必填（`title` 缺漏／null）發生在模型繫結階段，歸入模型繫結 400（AER-033），不帶 `detail`；Handler 路徑的端對端測試改用 Validator 才會擋的輸入（title 201 字元）。
- 模型繫結 400 的 `errors` 濾除參數名稱鍵與空陣列鍵；`traceId` 明確取 `HttpContext.TraceIdentifier`，測試以 Activity 區分。
- AER-023 判準為「Validator 產生」對比「Handler／Service 業務判斷」。
- 模型繫結 400 的「只剩參數名鍵改列空字串鍵」規則（AER-034）。
- 改前即綠的測試（AER-023 相關、`FieldErrors` 預設 null）標為特性固定測試並以突變證明。

### 決策 10：task 1.1 實測後的 spec 修正（採推薦方案）

- 實測（見 tasks.md task 1.1 完成紀錄）：`title` 為空字串模型繫結通過、走 Validator（有 `detail`），只有缺漏／null 走隱含必填。AER-033 移除空字串，新增 AER-035；不改程式去讓空字串走隱含必填。
- 實測：字面值 `null` 本文的鍵為 `""` 與 `request`，與空本文相同；移除 spec 中「只剩參數名鍵」的例子，後備規則改為防禦性規則由工廠單元測試覆蓋。
