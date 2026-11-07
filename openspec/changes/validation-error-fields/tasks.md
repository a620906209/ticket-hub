# Tasks

> 所有指令在容器內執行：後端 `docker compose exec api dotnet test ...`，前端 `docker compose exec web npm run test`。
> 每個 task 的「完成紀錄」於實作時填寫；RED 紀錄須貼失敗測試名稱與訊息原文、標明在修改正式程式碼前執行；bug fix 與核心業務邏輯另需還原驗證（本 change 不涉及下單、排隊、座位／庫存邏輯，僅替換驗證錯誤轉換，故還原驗證不適用，理由見各 task）。

## 1. 實測模型繫結錯誤鍵（先於一切實作，決定 spec AER-026～029 的斷言）

- [ ] 1.1 在 WebApi 整合測試專案新增探測測試，對真實端點（建立活動 `POST /api/admin/events`，需帶有效組織者 token）分別送出：欄位型別不符、非法 JSON、本文為字面值 `null`、`Content-Type: application/json` 空本文（不帶 Content-Type 會得 415），記錄框架目前回應的 `title`、`traceId` 值（與 `HttpContext.TraceIdentifier` 比對）與 `errors` **全部鍵**（含是否出現 `request` 等參數名鍵、`$`、`""`）；另送 `title` 缺漏與空字串，確認走隱含必填而非 Validator；並對 RegisterRealName、Login 送型別不符，記錄框架訊息是否回顯輸入值。
  - 目的：確認 `$.` 前綴、`""`、`$` 等鍵的實際形式，作為 1.2 起的斷言依據。若與本 change 全部模型繫結 Scenario（AER-026～029、033、034）及 Requirement 內的例子（含「本文為字面值 `null` 只剩參數名鍵」的假設）不符，**先回頭修 spec 並重新審查**，不得改測試遷就。
  - TDD：N/A（探測，不改正式程式碼；探測測試於確認後改寫為 task 4 的正式測試或刪除）。
  - 完成紀錄：（實測輸出原文）

## 2. Error 與 Application 擴充方法（AER-019、020、021、022、023）

- [ ] 2.1 `Error.Validation(message, fieldErrors)` 多載與 `FieldErrors` 屬性；舊多載 `FieldErrors` 為 null。TDD：與 2.2 同批，測試 `Validation_WithoutFieldErrors_HasNullFieldErrors` 為特性固定測試（見 task 5.1 例外說明）。
  - GREEN／完成紀錄：併入 2.2 的完成紀錄。還原驗證：不適用（同 2.2）。
- [ ] 2.2 `ValidationResultExtensions.ToValidationError`（`ProjectC.Application/Common/Validation/`）。
  - 測試先行（`ProjectC.Application.Tests/Common/`）：
    - `ToValidationError_WithSingleFailingField_ReturnsFieldErrorsWithThatKey`（AER-019）
    - `ToValidationError_WithMultipleFailingFields_ReturnsOneKeyPerField`（AER-020）
    - `ToValidationError_WithMultipleRulesOnSameField_AggregatesAllMessages`（AER-021）
    - `ToValidationError_Message_EqualsLegacySpaceJoinedErrorMessages`（AER-022，與舊串接式逐字比對，含順序）
    - `Validation_WithoutFieldErrors_HasNullFieldErrors`（AER-023 的 Error 層）
  - 預期 RED：測試因 `ToValidationError`／`FieldErrors` 不存在而編譯失敗**不算 RED**；先建立僅有簽章、回傳 `Error.Validation(string.Empty)` 的最小樁，再執行，預期斷言失敗（Expected 含欄位鍵／訊息，Actual 為 null／空字串）。
  - GREEN：實作 `PropertyName` 分組與串接，測試全綠。重構：無則記 N/A。
  - 還原驗證：不適用——不屬 bug fix 或下單／排隊／座位／庫存核心邏輯。
  - 完成紀錄：RED／GREEN／重構命令與輸出：

## 3. WebApi 輸出：鍵格式化與 ProblemDetails（AER-024、025、019～023 的 HTTP 層）

- [ ] 3.1 `FieldErrorKeyFormatter`（`ProjectC.WebApi/Common/`）：以 `.` 分段首字母小寫、保留索引、移除開頭 `$.`、`$`／空字串→空字串、碰撞合併。
  - 測試先行（`ProjectC.WebApi.Tests/Common/`）：`Format_NestedCollectionPath_LowercasesEachSegmentAndKeepsIndex`（`Selections[0].Quantity`→`selections[0].quantity`，AER-024）、`Format_PascalCaseName_LowercasesOnlyFirstCharacter`（`StartAtUtc`→`startAtUtc`，AER-025）、`Format_JsonPathPrefix_StripsDollarDot`、`Format_RootKey_ReturnsEmptyString`、`Format_KeysCollidingAfterConversion_MergesMessages`；另於 task 4.1 的工廠單元測試 `InvalidModelStateFactory_EntryWithoutMessages_OmitsKey` 守護「不輸出空陣列鍵」與參數名鍵濾除（`InvalidModelStateFactory_ParameterNameKey_IsOmitted`（同時斷言空字串鍵的訊息內容不含參數名鍵的訊息））。
  - 預期 RED：以回傳原字串的樁執行，斷言 Expected `selections[0].quantity`／Actual `Selections[0].Quantity`。
  - GREEN：實作 `FieldErrorKeyFormatter`，測試全綠；完成紀錄併入 task 3 的完成紀錄。
- [ ] 3.2 `ResultExtensions.CreateProblemResult` 在 `FieldErrors` 非 null 時輸出 `ValidationProblemDetails`。
  - 測試先行：`ToActionResult_ErrorWithFieldErrors_ReturnsValidationProblemDetailsWithFormattedKeys`（同時斷言 `status == 400`、`title == "Validation"`）、`ToActionResult_ValidationErrorWithoutFieldErrors_HasNoErrorsMember`（AER-023）。
  - 預期 RED：Actual 為一般 `ProblemDetails`、無 `errors`。
  - 還原驗證：不適用（同 task 2）。
  - 完成紀錄：RED／GREEN／重構：

## 4. 模型繫結 400（AER-026、027、028、029、033、034）

- [ ] 4.1 `InvalidModelStateResponseFactory` 設定（`Extensions/` 內新擴充方法）；`Program.cs` 加一行呼叫。
  - 測試先行（`ProjectC.WebApi.Tests`，沿用既有 `WebApplicationFactory`）：`CreateEvent_WithWrongTypedField_Returns400ValidationWithFieldKeyAndTraceId`（AER-026）、`CreateEvent_WithMalformedJson_Returns400ValidationWithErrors`（AER-027）、`CreateEvent_WithEmptyBody_Returns400ValidationWithEmptyKey`（AER-028，鍵集合恰為 [""]）、`CreateEvent_WithNullLiteralBody_Returns400ValidationWithEmptyKey`（AER-034，鍵集合恰為 [""]）、`InvalidModelStateFactory_EntryWithoutMessages_OmitsKey`、`InvalidModelStateFactory_ParameterNameKey_IsOmitted`（同時斷言空字串鍵的訊息內容不含參數名鍵的訊息）、`InvalidModelStateFactory_OnlyParameterNameKey_FallsBackToEmptyKey`、`InvalidModelStateFactory_ErrorWithoutMessage_UsesDefaultMessage`（工廠單元測試，守護決策 5 細節 (1)(2)(3)；`ConfigureApiBehaviorOptions` 須接在 `AddControllers()` 之後）、`CreateEvent_WithMissingNullOrEmptyTitle_Returns400ValidationWithTitleKey`（AER-033，`[Theory]` 涵蓋省略 `title`、`"title": null`、`"title": ""` 三種輸入，每種皆斷言 `errors` 鍵集合恰為 [`title`]、無 `detail`、`traceId` 等於 `TraceIdentifier`）、`CreateEvent_WithWrongTypedField_ErrorKeysHaveNoDollarPrefix`（AER-029）。每個測試皆斷言 Content-Type 為 `application/problem+json`、無 `detail`、`errors` 鍵集合恰等於預期（防空集合與參數名鍵）、`traceId` 等於 `HttpContext.TraceIdentifier`：期望值由測試內註冊的 middleware 擷取該請求的 `HttpContext.TraceIdentifier`，並在同一個 middleware 擷取 `Activity.Current?.Id`，先斷言它非 null 且不等於 `TraceIdentifier`（伺服器端 Activity 由 Hosting 自動建立；測試端自行啟動的 Activity 不會流進 TestServer），以免 traceId 斷言空轉；若 Activity 為 null，測試中註冊 `ActivityListener`（監聽 `Microsoft.AspNetCore`），做法參考 `tests/ProjectC.WebApi.Tests/Common/ResultExtensionsTests.cs:39-45`。AER-026、AER-033 的請求本文其餘欄位皆為合法值，否則鍵集合會多出其他鍵。整合測試需帶有效組織者 token（`RequireOrganizerContext`），否則得 401。斷言依 task 1.1 實測結果定稿。
  - 預期 RED：Actual `title` 為 `One or more validation errors occurred.`、`errors` 含 `request` 鍵或 `traceId` 為 Activity id。
  - 還原驗證：不適用（非 bug fix／核心業務邏輯）。
  - 完成紀錄：RED／GREEN／重構：

## 5. 替換 14 處呼叫端並整合驗證（AER-019～023 端對端）

- [ ] 5.1 逐處將 `Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage)))` 換為 `ToValidationError()`：Login、ResetPassword（僅驗證那一處）、CreateEvent、SetEventQueueMode、RegisterMember、RegisterRealName、UpdateMyProfile、OrderService、ApplyForOrganizer、SwitchOrganizerContext、JoinPurchaseQueue、CreateTicketType、CreateSeatMap、CreateVenue。
  - 測試先行：
    - WebApi 整合測試 `CreateEvent_WithOverlongTitle_Returns400WithTitleFieldError`（AER-019 端對端，title 201 字元，其餘欄位合法；`errors` 只含 `title`、`errors.title` 非空）、`CreateEvent_WithOverlongTitle_DetailEqualsJoinedMessages`（AER-022 端對端）。
    - `PlaceOrder_WithInvalidSelectionQuantity_ReturnsNestedFieldKey`（AER-024 端對端，鍵 `selections[0].quantity`）。
    - `PlaceOrder_WithItemsFromDifferentEvents_Returns400WithoutErrors`（AER-023 端對端）。
    - `CreateEvent_WithInterleavedFieldFailures_DetailFollowsReportOrderAndErrorsGroupByField`（AER-032，用 SalesStart／SalesEnd／SalesStart 交錯的請求；請求寫死具體值：SalesStartAtUtc 為 Unspecified kind、SalesEndAtUtc 晚於 StartAtUtc、SalesStartAtUtc 不早於 SalesEndAtUtc，並於測試內以字串常數斷言 m1／m2／m3 與 `detail`）。
    - 其餘 12 處各由其 Handler 既有單元測試補一條斷言 `Error.FieldErrors` 非 null（新增 `<Handler>_WhenValidationFails_ReturnsFieldErrors`）；沒有 Application 單元測試檔的 `SetEventQueueModeHandler` 於 `AdminEventsControllerTests` 補一條 HTTP 層測試斷言回應含 `errors`。
  - 預期 RED：Actual 回應無 `errors` 成員／`FieldErrors` 為 null。例外：`PlaceOrder_WithItemsFromDifferentEvents_Returns400WithoutErrors`、`ToActionResult_ValidationErrorWithoutFieldErrors_HasNoErrorsMember`、`Validation_WithoutFieldErrors_HasNullFieldErrors` 改前即綠，為特性固定測試：TDD 標示「預期一開始即通過」，並以突變（暫時讓該路徑輸出 `errors`／讓舊多載帶非 null）證明測試會失敗，記錄命令與輸出。
  - 完成條件：`grep -rn "string.Join(\" \", validation.Errors" src/` 無結果；Application、WebApi 全套測試通過（貼各測試專案摘要行）。
  - 還原驗證：不適用——驗證錯誤的表示法調整，不涉及下單、排隊、座位／庫存邏輯。
  - 完成紀錄：RED／GREEN／重構、grep 輸出、全套摘要：

## 6. 前端（AER-030、031）

- [ ] 6.1 `ProblemDetails` 介面加 `errors?: Record<string, string[]>`。
  - 測試先行（`web/src/api/httpClient.test.ts`）：（測試描述句用中文，比照 `httpClient.test.ts` 既有 `it(...)` 寫法；英文名僅示意）`request_WhenResponseHasErrors_KeepsErrorsOnApiErrorProblem`（AER-030，`problem.errors` 等於回應、`message` 等於 `detail`）。
  - 預期 RED：TypeScript 型別檢查失敗**不算 RED**；先加上型別再執行，預期不會 RED（型別本身不改行為）——若 `problem.errors` 本來就保留，則此測試為特性固定測試，TDD 標示「測試先行但預期一開始即通過」，並在紀錄說明原因（`parseProblemDetails` 原樣保留整個 JSON）。AER-031 另新增表單頁測試：選一個既有表單頁測試檔（如 EventCreatePage 或 LoginPage），新增 `WhenApiReturns400WithDetailAndErrors_ShowsDetailText`——mock 400 回應帶 `detail` 與 `errors`（`errors` 的訊息字串須與 `detail` 不同），斷言畫面文字等於 `detail`、不含 `errors` 內容。此為特性固定測試（原本即通過），須以突變（暫時讓頁面顯示 `errors` 內容）證明測試會失敗，並記錄命令與輸出。
  - 完成紀錄：測試輸出、`npm run lint`、`npm run test` 全套摘要：

## 7. 文件

- [ ] 7.1 更新 `docs/api-conventions.md` 第 2 節：驗證錯誤格式（`errors`、鍵規則、模型繫結 400）；移除「不一致／待決定」項目 1、2。勾選 `docs/api-conventions-todo.md` 第 3 節各項。TDD：N/A（純文件）。
- [ ] 7.2 `graphify update .`。

## 8. hardener 最終檢查

- [ ] 8.1 N/A：本次新增為純轉換邏輯與組態，不做 I/O、不管理資源；14 處呼叫端只替換轉換呼叫（理由見 design.md「hardener 適用性」）。實作完成時再依實際 diff 複核此判斷，若新增了 I/O 或資源管理程式碼則改為執行。

## 9. 審查與驗證

- [ ] 9.1 strict-reviewer 與全新對抗式 subagent 依序審查迴圈（CLAUDE.md 開發流程步驟 6），直到雙方無 blocking；結果與每輪 commit 記錄於此。
- [ ] 9.2 真實瀏覽器驗證：後台建立活動表單的規則（`maxlength="200"`、`maxLengthRule`、必填、限購上下限、販售期間、`toISOString()` 固定帶 Z）已鏡像後端，正常 UI 送不出會觸發 Validator 的值，故 AER-031 的畫面行為由 task 6.1 的 Vitest 表單頁測試（含突變）證明；瀏覽器僅確認 API 回應：以 claude-in-chrome 登入後台後，在頁面內以 JS `fetch` 帶 token 直送（access token 只存在 Pinia 記憶體，localStorage 只有 refresh token；dev build 從 `__vue_app__` 讀 Pinia auth store 取 access token，不得呼叫 refresh 端點，否則 refresh token 輪替會使頁面登出；token 須帶主辦方情境，否則得 403） `POST /api/admin/events`（title 201 字元、其餘欄位為合法值；預期 400），以 `read_network_requests` 或回應文字核對含 `errors.title` 與 `traceId`，並確認 DB 沒有新增活動。同時確認既有表單頁載入與送出一筆合法活動仍正常。
