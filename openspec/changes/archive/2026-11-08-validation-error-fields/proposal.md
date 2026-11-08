# Proposal

## Why

驗證失敗（400 `Validation`）目前只有把所有欄位訊息以空白串成的單一 `detail` 字串，FluentValidation 的 `PropertyName` 在 Handler 中被丟棄，client 無法得知是哪個欄位出錯。另外，框架對模型繫結失敗自動產生的 400，`title` 為框架預設句子，與其他錯誤的機器代碼風格不一致；其 `traceId` 值為 `Activity` id 而非 `HttpContext.TraceIdentifier`，與其他錯誤不同（`docs/api-conventions.md` 第 2 節「不一致／待決定」項目 1、2）。`error-response-title-traceid` 已完成共用錯誤格式決策，現在可處理。

## What Changes

- `Error` 新增可為 null 的欄位錯誤字典與 `Error.Validation(message, fieldErrors)` 多載；舊的單參數多載保留。
- Application 層新增 FluentValidation `ValidationResult` 轉 `Error` 的擴充方法，取代 14 處 Handler／Service 中重複的 `Error.Validation(string.Join(" ", ...))`。
- `Result` 轉 `ProblemDetails` 時，若錯誤帶欄位資訊，輸出 `errors`（`{ "欄位": ["訊息", ...] }`），欄位名稱轉為 camelCase（巢狀路徑如 `items[0].quantity`）；`detail` 維持現行合併字串。
- 模型繫結失敗的 400 改以 `InvalidModelStateResponseFactory` 輸出：`title` 為 `Validation`、`errors`（key 同樣轉 camelCase，濾除參數名稱鍵；只剩參數名鍵時改列於空字串鍵）、`traceId` 明確取 `HttpContext.TraceIdentifier`；非 nullable 參考型別的隱含必填失敗亦走此路徑。
- 前端 `ProblemDetails` 型別新增可選的 `errors`；不做逐欄位錯誤顯示。
- 更新 `docs/api-conventions.md` 第 2 節並移除不一致項目 1、2；勾選 `docs/api-conventions-todo.md` 第 3 項。
- 非目標：業務規則類 `Validation` 錯誤（如 `OrderService` 同一活動檢查）欄位化、前端欄位級錯誤 UI、其他 `ErrorType`。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `api-error-responses`: 新增「驗證錯誤回應含逐欄位 errors」Requirement；「框架層錯誤的 Title 為機器代碼」中對模型繫結 400 的排除說明，改為由新 Requirement 規範；「前端錯誤訊息不得以 Title 作為備援」不變。

## Impact

- 程式碼：`ProjectC.Application/Common/Error.cs`、新增 Application 層擴充方法、14 處呼叫 `Error.Validation(string.Join(...))` 的 Handler／`OrderService`、`ProjectC.WebApi/Common/ResultExtensions.cs`、`Program.cs`（或其拆出的組態擴充）的 `ConfigureApiBehaviorOptions`、`web/src/api/httpClient.ts`。
- API：400 回應新增 `errors` 欄位（向下相容，`title`／`detail`／`status` 不變）；模型繫結 400 的 `title` 由框架句子改為 `Validation`（**行為變更**：依賴舊 `title` 句子的 client 需調整，前端經查未依賴）。
- 無 DB 變更、無 migration、無新相依套件。
