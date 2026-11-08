# Tasks

> 所有指令在容器內執行：後端 `docker compose exec api dotnet test ...`，前端 `docker compose exec web npm run test`。
> 每個 task 的「完成紀錄」於實作時填寫；RED 紀錄須貼失敗測試名稱與訊息原文、標明在修改正式程式碼前執行；bug fix 與核心業務邏輯另需還原驗證（本 change 不涉及下單、排隊、座位／庫存邏輯，僅替換驗證錯誤轉換，故還原驗證不適用，理由見各 task）。

## 1. 實測模型繫結錯誤鍵（先於一切實作，決定 spec AER-026～029 的斷言）

- [x] 1.1 在 WebApi 整合測試專案新增探測測試，對真實端點（建立活動 `POST /api/admin/events`，需帶有效組織者 token）分別送出：欄位型別不符、非法 JSON、本文為字面值 `null`、`Content-Type: application/json` 空本文（不帶 Content-Type 會得 415），記錄框架目前回應的 `title`、`traceId` 值（與 `HttpContext.TraceIdentifier` 比對）與 `errors` **全部鍵**（含是否出現 `request` 等參數名鍵、`$`、`""`）；另送 `title` 缺漏與空字串，確認走隱含必填而非 Validator；並對 RegisterRealName、Login 送型別不符，記錄框架訊息是否回顯輸入值。
  - 目的：確認 `$.` 前綴、`""`、`$` 等鍵的實際形式，作為 1.2 起的斷言依據。若與本 change 全部模型繫結 Scenario（AER-026～029、033～035）及 Requirement 內的例子不符，**先回頭修 spec 並重新審查**，不得改測試遷就。
  - TDD：N/A（探測，不改正式程式碼；探測測試於確認後改寫為 task 4 的正式測試或刪除）。
  - 完成紀錄：（實測輸出原文）**2026-11-08 探測結果與 spec 不符；已依 design.md 決策 10 修正 spec（空字串走 Validator＝AER-035、`null` 本文鍵為 [""]＝AER-034），spec-reviewer 重審 PASS 後才繼續 task 2 以後；探測檔已刪除，其斷言改寫為 task 4.1 的正式測試。**
    - 命令：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ModelBindingErrorProbeTests" --logger "console;verbosity=detailed"`（探測檔 `tests/ProjectC.WebApi.Tests/Common/ModelBindingErrorProbeTests.cs`，未提交）；`Passed: 2`。
    - 全部回應 `Content-Type: application/problem+json; charset=utf-8`，模型繫結 400 的 `traceId` 皆為 Activity id（`00-<32hex>-<16hex>-00`），與 `TraceIdentifier`（`0HNPTB5VUSNJ3` 型）不同，確認決策 5 的前提。
    - `[wrongType]`（`maxTicketsPerOrder:"abc"`）：`"errors":{"request":["The request field is required."],"$.maxTicketsPerOrder":["The JSON value could not be converted to ProjectC.Application.Events.CreateEvent.CreateEventRequest. Path: $.maxTicketsPerOrder | LineNumber: 0 | BytePositionInLine: 175."]}`
    - `[malformedJson]`（`{not json`）：`"errors":{"$":["'n' is an invalid start of a property name. Expected a '\"'. Path: $ | LineNumber: 0 | BytePositionInLine: 1."],"request":["The request field is required."]}`
    - `[nullLiteral]`（`null`）：`"errors":{"":["A non-empty request body is required."],"request":["The request field is required."]}`（**與 emptyBody 完全相同**）
    - `[emptyBody]`：`"errors":{"":["A non-empty request body is required."],"request":["The request field is required."]}`
    - `[titleMissing]`／`[titleNull]`：`"errors":{"Title":["The Title field is required."]}`（無 `request` 鍵、無 `detail`）
    - `[titleEmpty]`（`"title":""`）：**`{"title":"Validation","status":400,"detail":"'Title' must not be empty.","traceId":"0HNPTB5VUSNJ3"}`——走 Handler 的 Validator 路徑（有 `detail`、`traceId` 為 TraceIdentifier），不是隱含必填。**
    - `login-wrongType`（`email:123`）、`register-wrongType`（`displayName:{...}`）：`"errors":{"request":[...],"$.email":["The JSON value could not be converted to ProjectC.Application.Authentication.Login.LoginRequest. Path: $.email | ..."]}`；訊息含 CLR 型別全名與 JSON 路徑，**未回顯輸入值**（密碼 `SECRET-PW` 未出現）。
    - **與 spec 不符之處**：(1) AER-033 與 Requirement 文字、決策 5 細節 (4)、決策 9、task 4.1 的 `[Theory]` 把「`title` 為空字串」歸入隱含必填／模型繫結 400（無 `detail`），實測空字串走 Validator（有 `detail`、無 `errors`——實作後將帶 `errors.title`）；只有缺漏與 null 走隱含必填。(2) 決策 5 細節 (1)、AER-034 與 Requirement 文字的例子「本文為字面值 `null` 時只剩參數名鍵」不成立：字面值 `null` 的實測鍵為 `""` 與 `request`，濾除 `request` 後即恰為 [`""`]；「只剩參數名鍵改列空字串鍵」的後備規則在 null 例子下不會被觸發（`InvalidModelStateFactory_OnlyParameterNameKey_FallsBackToEmptyKey` 工廠單元測試仍可直接覆蓋該分支，但需改述其成因）。AER-034 的鍵集合結論（恰為 [`""`]）仍成立。

## 2. Error 與 Application 擴充方法（AER-019、020、021、022、023）

- [x] 2.1 `Error.Validation(message, fieldErrors)` 多載與 `FieldErrors` 屬性；舊多載 `FieldErrors` 為 null。TDD：與 2.2 同批，測試 `Validation_WithoutFieldErrors_HasNullFieldErrors` 為特性固定測試（見 task 5.1 例外說明）。
  - GREEN／完成紀錄：併入 2.2 的完成紀錄。還原驗證：不適用（同 2.2）。
- [x] 2.2 `ValidationResultExtensions.ToValidationError`（`ProjectC.Application/Common/Validation/`）。
  - 測試先行（`ProjectC.Application.Tests/Common/`）：
    - `ToValidationError_WithSingleFailingField_ReturnsFieldErrorsWithThatKey`（AER-019）
    - `ToValidationError_WithMultipleFailingFields_ReturnsOneKeyPerField`（AER-020）
    - `ToValidationError_WithMultipleRulesOnSameField_AggregatesAllMessages`（AER-021）
    - `ToValidationError_Message_EqualsLegacySpaceJoinedErrorMessages`（AER-022，與舊串接式逐字比對，含順序）
    - `Validation_WithoutFieldErrors_HasNullFieldErrors`（AER-023 的 Error 層）
  - 預期 RED：測試因 `ToValidationError`／`FieldErrors` 不存在而編譯失敗**不算 RED**；先建立僅有簽章、回傳 `Error.Validation(string.Empty)` 的最小樁，再執行，預期斷言失敗（Expected 含欄位鍵／訊息，Actual 為 null／空字串）。
  - GREEN：實作 `PropertyName` 分組與串接，測試全綠。重構：無則記 N/A。
  - 還原驗證：不適用——不屬 bug fix 或下單／排隊／座位／庫存核心邏輯。
  - 完成紀錄（2026-11-08）：
    - RED（樁：`ToValidationError` 回傳 `Error.Validation(string.Empty)`，正式程式碼修改前）`docker compose exec api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~ValidationResultExtensionsTests`：
      ```
      Failed ProjectC.Application.Tests.Common.ValidationResultExtensionsTests.ToValidationError_Message_EqualsLegacySpaceJoinedErrorMessages [34 ms]
       Expected error.Message to be "m1 m2 m3" with a length of 8, but "" has a length of 0, differs near "" (index 0).
      Failed ProjectC.Application.Tests.Common.ValidationResultExtensionsTests.ToValidationError_WithSingleFailingField_ReturnsFieldErrorsWithThatKey [6 ms]
       Expected error.FieldErrors not to be <null>.
      Failed ...ToValidationError_WithMultipleRulesOnSameField_AggregatesAllMessages [< 1 ms]
      Failed ...ToValidationError_WithMultipleFailingFields_ReturnsOneKeyPerField [< 1 ms]
      Failed!  - Failed:     4, Passed:     1, Skipped:     0, Total:     5, Duration: 54 ms - ProjectC.Application.Tests.dll (net10.0)
      ```
    - GREEN：`Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 45 ms - ProjectC.Application.Tests.dll (net10.0)`。重構：N/A。
    - 突變（特性固定測試 `Validation_WithoutFieldErrors_HasNullFieldErrors`）：`mutation: new(ErrorType.Validation, message); -> new(ErrorType.Validation, message, new Dictionary<string, string[]>());` →
      `Expected Error.Validation("business rule").FieldErrors to be <null>, but found {empty}.`

## 3. WebApi 輸出：鍵格式化與 ProblemDetails（AER-024、025、019～023 的 HTTP 層）

- [x] 3.1 `FieldErrorKeyFormatter`（`ProjectC.WebApi/Common/`）：以 `.` 分段首字母小寫、保留索引、移除開頭 `$.`、`$`／空字串→空字串、碰撞合併。
  - 測試先行（`ProjectC.WebApi.Tests/Common/`）：`Format_NestedCollectionPath_LowercasesEachSegmentAndKeepsIndex`（`Selections[0].Quantity`→`selections[0].quantity`，AER-024）、`Format_PascalCaseName_LowercasesOnlyFirstCharacter`（`StartAtUtc`→`startAtUtc`，AER-025）、`Format_JsonPathPrefix_StripsDollarDot`、`Format_RootKey_ReturnsEmptyString`、`Format_KeysCollidingAfterConversion_MergesMessages`；另於 task 4.1 的工廠單元測試 `InvalidModelStateFactory_EntryWithoutMessages_OmitsKey` 守護「不輸出空陣列鍵」與參數名鍵濾除（`InvalidModelStateFactory_ParameterNameKey_IsOmitted`（同時斷言空字串鍵的訊息內容不含參數名鍵的訊息））。
  - 預期 RED：以回傳原字串的樁執行，斷言 Expected `selections[0].quantity`／Actual `Selections[0].Quantity`。
  - GREEN：實作 `FieldErrorKeyFormatter`，測試全綠；完成紀錄併入 task 3 的完成紀錄。
  - 完成紀錄（2026-11-08）：
    - RED（回傳原字串的樁，修改前）：
      ```
      Failed ...FieldErrorKeyFormatterTests.Format_PascalCaseName_LowercasesOnlyFirstCharacter [27 ms]
       Expected FieldErrorKeyFormatter.Format("StartAtUtc") to be a match with the expectation, but it differs at index 0:
      Failed ...Format_NestedCollectionPath_LowercasesEachSegmentAndKeepsIndex [3 ms]
       Expected FieldErrorKeyFormatter.Format("Selections[0].Quantity") to be a match with the expectation, but it differs at index 0:
      Failed ...Format_RootKey_ReturnsEmptyString(key: "$") [6 ms]
       Expected FieldErrorKeyFormatter.Format(key) to be empty, but found "$".
      Failed ...FormatAll_KeysCollidingAfterConversion_MergesMessages [28 ms]
       Expected formatted.Keys to contain exactly one item, but found one extraneous item at index 0: "Title"
      Failed ...Format_JsonPathPrefix_StripsDollarDot [3 ms]
       Expected FieldErrorKeyFormatter.Format("$.maxTicketsPerOrder") to be a match with the expectation, but it differs at index 0:
      Failed!  - Failed:     5, Passed:     2, Skipped:     0, Total:     7, Duration: 77 ms - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - GREEN：`Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: 43 ms - ProjectC.WebApi.Tests.dll (net10.0)`。重構：N/A。
- [x] 3.2 `ResultExtensions.CreateProblemResult` 在 `FieldErrors` 非 null 時輸出 `ValidationProblemDetails`。
  - 測試先行：`ToActionResult_ErrorWithFieldErrors_ReturnsValidationProblemDetailsWithFormattedKeys`（同時斷言 `status == 400`、`title == "Validation"`）、`ToActionResult_ValidationErrorWithoutFieldErrors_HasNoErrorsMember`（AER-023）。
  - 預期 RED：Actual 為一般 `ProblemDetails`、無 `errors`。
  - 還原驗證：不適用（同 task 2）。
  - 完成紀錄（2026-11-08）：
    - RED（修改前）`ToActionResult_ErrorWithFieldErrors_ReturnsValidationProblemDetailsWithFormattedKeys`：`Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4, Duration: 133 ms - ProjectC.WebApi.Tests.dll (net10.0)`（Actual 為一般 `ProblemDetails`、無 `errors`）。
    - `ToActionResult_ValidationErrorWithoutFieldErrors_HasNoErrorsMember`（特性固定）突變（讓舊多載帶非 null 欄位字典）：`Failed ...ToActionResult_ValidationErrorWithoutFieldErrors_HasNoErrorsMember [98 ms]`、`Failed!  - Failed:     1, Passed:    10, Skipped:     0, Total:    11, Duration: 135 ms`；還原後 `Total:    11 ... Duration: 141 ms` 全數通過。
    - GREEN／重構：N/A。

## 4. 模型繫結 400（AER-026、027、028、029、033、034）

- [x] 4.1 `InvalidModelStateResponseFactory` 設定（`Extensions/` 內新擴充方法）；`Program.cs` 加一行呼叫。
  - 測試先行（`ProjectC.WebApi.Tests`，沿用既有 `WebApplicationFactory`）：`CreateEvent_WithWrongTypedField_Returns400ValidationWithFieldKeyAndTraceId`（AER-026）、`CreateEvent_WithMalformedJson_Returns400ValidationWithErrors`（AER-027）、`CreateEvent_WithEmptyBody_Returns400ValidationWithEmptyKey`（AER-028，鍵集合恰為 [""]）、`CreateEvent_WithNullLiteralBody_Returns400ValidationWithEmptyKey`（AER-034，鍵集合恰為 [""]）、`InvalidModelStateFactory_EntryWithoutMessages_OmitsKey`、`InvalidModelStateFactory_ParameterNameKey_IsOmitted`（同時斷言空字串鍵的訊息內容不含參數名鍵的訊息）、`InvalidModelStateFactory_OnlyParameterNameKey_FallsBackToEmptyKey`（直接以只含參數名鍵的 ModelState 建構，因實測 `null` 本文不會觸發此分支）、`InvalidModelStateFactory_ErrorWithoutMessage_UsesDefaultMessage`（工廠單元測試，守護決策 5 細節 (1)(2)(3)；`ConfigureApiBehaviorOptions` 須接在 `AddControllers()` 之後）、`CreateEvent_WithMissingOrNullTitle_Returns400ValidationWithTitleKey`（AER-033，`[Theory]` 涵蓋省略 `title`、`"title": null` 兩種輸入，每種皆斷言 `errors` 鍵集合恰為 [`title`]、無 `detail`、`traceId` 等於 `TraceIdentifier`）、`CreateEvent_WithWrongTypedField_ErrorKeysHaveNoDollarPrefix`（AER-029）。每個測試皆斷言 Content-Type 為 `application/problem+json`、無 `detail`、`errors` 鍵集合恰等於預期（防空集合與參數名鍵）、`traceId` 等於 `HttpContext.TraceIdentifier`：期望值由測試內註冊的 middleware 擷取該請求的 `HttpContext.TraceIdentifier`，並在同一個 middleware 擷取 `Activity.Current?.Id`，先斷言它非 null 且不等於 `TraceIdentifier`（伺服器端 Activity 由 Hosting 自動建立；測試端自行啟動的 Activity 不會流進 TestServer），以免 traceId 斷言空轉；若 Activity 為 null，測試中註冊 `ActivityListener`（監聽 `Microsoft.AspNetCore`），做法參考 `tests/ProjectC.WebApi.Tests/Common/ResultExtensionsTests.cs:39-45`。AER-026、AER-033 的請求本文其餘欄位皆為合法值，否則鍵集合會多出其他鍵。整合測試需帶有效組織者 token（`RequireOrganizerContext`），否則得 401。斷言依 task 1.1 實測結果定稿。
  - 預期 RED：Actual `title` 為 `One or more validation errors occurred.`、`errors` 含 `request` 鍵或 `traceId` 為 Activity id。
  - 還原驗證：不適用（非 bug fix／核心業務邏輯）。
  - 完成紀錄（2026-11-08）：
    - 工廠單元測試 RED（工廠為不濾除參數名鍵、不補預設訊息的初版，修改前）：
      ```
      Failed ...InvalidModelStateFactory_ParameterNameKey_IsOmitted [146 ms]
       Expected errors.Keys to contain exactly one item, but found one extraneous item at index 1: "request"
      Failed ...InvalidModelStateFactory_OnlyParameterNameKey_FallsBackToEmptyKey [10 ms]
       Expected errors.Keys[0] to be "" with a length of 0, but "request" has a length of 7, differs near "req" (index 0).
      Failed ...InvalidModelStateFactory_ErrorWithoutMessage_UsesDefaultMessage [12 ms]
       Expected errors["maxTicketsPerOrder"] to be equal to {"The input was not valid."}, but {"The supplied value is invalid."} differs at index 0.
      Failed!  - Failed:     3, Passed:     2, Skipped:     0, Total:     5, Duration: 180 ms - ProjectC.WebApi.Tests.dll (net10.0)
      ```
      GREEN：`Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 150 ms - ProjectC.WebApi.Tests.dll (net10.0)`。
    - 整合測試（`ModelBindingValidationResponseTests`）**沒有真正的 RED**：工廠實作與 DI 註冊在測試撰寫前已存在，故不能宣稱先紅後綠。改以突變證明測試會失敗：移除 `Program.cs` 的 `builder.Services.AddValidationProblemResponses();`，7 個測試全部失敗（例如 `Expected root.GetProperty("title").GetString() to be a match with the expectation, but it differs at index 0:`，框架預設 `title` 句子）；還原後 `Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: 2 s - ProjectC.WebApi.Tests.dll (net10.0)`。
    - 重構：N/A。

## 5. 替換 14 處呼叫端並整合驗證（AER-019～023 端對端）

- [x] 5.1 逐處將 `Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage)))` 換為 `ToValidationError()`：Login、ResetPassword（僅驗證那一處）、CreateEvent、SetEventQueueMode、RegisterMember、RegisterRealName、UpdateMyProfile、OrderService、ApplyForOrganizer、SwitchOrganizerContext、JoinPurchaseQueue、CreateTicketType、CreateSeatMap、CreateVenue。
  - 測試先行：
    - WebApi 整合測試 `CreateEvent_WithOverlongTitle_Returns400WithTitleFieldError`（AER-019 端對端，title 201 字元，其餘欄位合法；`errors` 只含 `title`、`errors.title` 非空）、`CreateEvent_WithOverlongTitle_DetailEqualsJoinedMessages`（AER-022 端對端）。
    - `CreateEvent_WithEmptyTitle_Returns400WithTitleFieldErrorAndDetail`（AER-035，`"title": ""`，其餘欄位合法；`errors` 鍵集合恰為 [`title`]、有 `detail`、`traceId` 等於 `TraceIdentifier`）。
    - `PlaceOrder_WithInvalidSelectionQuantity_ReturnsNestedFieldKey`（AER-024 端對端，鍵 `selections[0].quantity`）。
    - `PlaceOrder_WithItemsFromDifferentEvents_Returns400WithoutErrors`（AER-023 端對端）。
    - `CreateEvent_WithInterleavedFieldFailures_DetailFollowsReportOrderAndErrorsGroupByField`（AER-032，用 SalesStart／SalesEnd／SalesStart 交錯的請求；請求寫死具體值：SalesStartAtUtc 為 Unspecified kind、SalesEndAtUtc 晚於 StartAtUtc、SalesStartAtUtc 不早於 SalesEndAtUtc，並於測試內以字串常數斷言 m1／m2／m3 與 `detail`）。
    - 其餘 12 處各由其 Handler 既有單元測試補一條斷言 `Error.FieldErrors` 非 null（新增 `<Handler>_WhenValidationFails_ReturnsFieldErrors`）；沒有 Application 單元測試檔的 `SetEventQueueModeHandler` 於 `AdminEventsControllerTests` 補一條 HTTP 層測試斷言回應含 `errors`。
  - 預期 RED：Actual 回應無 `errors` 成員／`FieldErrors` 為 null。例外：`PlaceOrder_WithItemsFromDifferentEvents_Returns400WithoutErrors`、`ToActionResult_ValidationErrorWithoutFieldErrors_HasNoErrorsMember`、`Validation_WithoutFieldErrors_HasNullFieldErrors` 改前即綠，為特性固定測試：TDD 標示「預期一開始即通過」，並以突變（暫時讓該路徑輸出 `errors`／讓舊多載帶非 null）證明測試會失敗，記錄命令與輸出。
  - 完成條件：`grep -rn "string.Join(\" \", validation.Errors" src/` 無結果；Application、WebApi 全套測試通過（貼各測試專案摘要行）。
  - 還原驗證：不適用——驗證錯誤的表示法調整，不涉及下單、排隊、座位／庫存邏輯。
  - 完成紀錄（2026-11-08）：
    - RED（12 個 Handler 單元測試，替換呼叫端前）`docker compose exec api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~WhenValidationFails_ReturnsFieldErrors"`：12 個測試全部失敗，每個皆為 `Expected result.Error.FieldErrors not to be <null>.`（CreateVenue、CreateTicketType、CreateEvent、CreateSeatMap、JoinPurchaseQueue、UpdateMyProfile、ApplyForOrganizer、SwitchOrganizerContext、ResetPassword、RegisterMember、Login、RegisterRealName）；`Failed!  - Failed:    12, Passed:     0, Skipped:     0, Total:    12, Duration: 177 ms - ProjectC.Application.Tests.dll (net10.0)`。
    - WebApi 端對端測試（`HandlerValidationFieldErrorsTests` 4 個、`PlaceOrder_WithInvalidSelectionQuantity_ReturnsNestedFieldKey`、`PlaceOrder_WithItemsFromDifferentEvents_Returns400WithoutErrors`、`SetQueueMode_WithMissingEnabled_Returns400WithEnabledFieldError`）撰寫於替換之後，**沒有真正的 RED**，以突變證明會失敗：
      - CreateEventHandler 還原為舊串接式（`mutation: validation.ToValidationError() -> Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage)))`）：`System.Collections.Generic.KeyNotFoundException : The given key was not present in the dictionary.`，`Failed!  - Failed:     4, Passed:     0, Skipped:     0, Total:     4, Duration: 2 s - ProjectC.WebApi.Tests.dll (net10.0)`。
      - OrderService 還原為舊串接式：`Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1`（`PlaceOrder_WithInvalidSelectionQuantity_ReturnsNestedFieldKey`）。
      - SetEventQueueModeHandler 還原為舊串接式：`Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1`（`SetQueueMode_WithMissingEnabled_Returns400WithEnabledFieldError`）。
      - AER-023 特性固定：`mutation: Error.Validation("All selected items must belong to the same event.") -> Error.Validation("All selected items must belong to the same event.", new Dictionary<string, string[]> { ["Selections"] = ["x"] })` → `Expected json.RootElement.TryGetProperty("errors", out _) to be False, but found True.`，`Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 2 s - ProjectC.WebApi.Tests.dll (net10.0)`。
      - 注意：突變還原後須 `touch` 被改檔案，否則下一次全套測試會用到過期 DLL（曾因此得到一次假失敗 `Failed: 1, Passed: 720, Total: 721`，touch 後重跑通過）。
    - `grep -rn 'string.Join(" ", validation.Errors' src/` 無輸出。
    - GREEN 全套：`Passed!  - Failed:     0, Passed:   566, Skipped:     0, Total:   566, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)`；`Passed!  - Failed:     0, Passed:   721, Skipped:     0, Total:   721, Duration: 2 m 57 s - ProjectC.WebApi.Tests.dll (net10.0)`。重構：N/A。

## 6. 前端（AER-030、031）

- [x] 6.1 `ProblemDetails` 介面加 `errors?: Record<string, string[]>`。
  - 測試先行（`web/src/api/httpClient.test.ts`）：（測試描述句用中文，比照 `httpClient.test.ts` 既有 `it(...)` 寫法；英文名僅示意）`request_WhenResponseHasErrors_KeepsErrorsOnApiErrorProblem`（AER-030，`problem.errors` 等於回應、`message` 等於 `detail`）。
  - 預期 RED：TypeScript 型別檢查失敗**不算 RED**；先加上型別再執行，預期不會 RED（型別本身不改行為）——若 `problem.errors` 本來就保留，則此測試為特性固定測試，TDD 標示「測試先行但預期一開始即通過」，並在紀錄說明原因（`parseProblemDetails` 原樣保留整個 JSON）。AER-031 另新增表單頁測試：選一個既有表單頁測試檔（如 EventCreatePage 或 LoginPage），新增 `WhenApiReturns400WithDetailAndErrors_ShowsDetailText`——mock 400 回應帶 `detail` 與 `errors`（`errors` 的訊息字串須與 `detail` 不同），斷言畫面文字等於 `detail`、不含 `errors` 內容。此為特性固定測試（原本即通過），須以突變（暫時讓頁面顯示 `errors` 內容）證明測試會失敗，並記錄命令與輸出。
  - 完成紀錄（2026-11-08）：
    - 加型別前 `docker compose exec web npx vue-tsc -b`：`src/api/httpClient.test.ts(120,12): error TS7053: Element implicitly has an 'any' type because expression of type '"selections[0].quantity"' can't be used to index type '{}'.`（型別失敗不算 RED）；加上 `errors?: Record<string, string[]>` 後 `vue-tsc -b` 無輸出、退出碼 0。
    - 兩個新測試皆為特性固定（`parseProblemDetails` 原樣保留整個 JSON、`ApiError.message` 取 `detail`），一開始即通過：`✓ src/api/httpClient.test.ts (15 tests)`、`✓ src/pages/admin/EventCreatePage.test.ts (26 tests)`。
    - 突變（`EventCreatePage.vue` 的 `eventError.value = toErrorMessage(...)` 暫改為顯示 `errors` 內容）：`× createEvent_WhenApiReturns400WithDetailAndErrors_ShowsDetailText`、`Tests  1 failed | 25 passed (26)`；已還原（`git status` 無 `EventCreatePage.vue` 變更）。
    - `npm run lint`：無輸出（通過）。`npm run test`：`Test Files  47 passed (47)`、`Tests  673 passed (673)`。

## 7. 文件

- [x] 7.1 更新 `docs/api-conventions.md` 第 2 節：驗證錯誤格式（`errors`、鍵規則、模型繫結 400）；移除「不一致／待決定」項目 1、2。勾選 `docs/api-conventions-todo.md` 第 3 節各項。TDD：N/A（純文件）。
- [x] 7.2 `graphify update .`。（輸出：`[graphify watch] graph.json, graph.html and GRAPH_REPORT.md updated in graphify-out`）

## 8. hardener 最終檢查

- [x] 8.1 N/A（2026-11-08 依實際 diff 複核：只新增純轉換與 DI 組態，14 處呼叫端僅替換轉換呼叫，無新增 I/O 或資源管理）：本次新增為純轉換邏輯與組態，不做 I/O、不管理資源；14 處呼叫端只替換轉換呼叫（理由見 design.md「hardener 適用性」）。實作完成時再依實際 diff 複核此判斷，若新增了 I/O 或資源管理程式碼則改為執行。

## 9. 審查與驗證

- [x] 9.1 strict-reviewer 與全新對抗式 subagent 依序審查迴圈（CLAUDE.md 開發流程步驟 6），直到雙方無 blocking；結果與每輪 commit 記錄於此。
  - 第 1 輪（2026-11-08，工作樹未 commit）：strict-reviewer PASS（無 blocking）；全新對抗式 general-purpose subagent 無 blocking；Claude Opus 5.5 交叉審查（herdr pane）FAIL，1 項 blocking B1：`FieldErrorKeyFormatter` 對括號路徑（client 送出含空白或 `.` 的屬性名，如 `{"title":"t","x y": tru}`）原樣輸出 `$['x y']`，違反「不帶 `$.` 前綴」。前兩者都漏掉，因 design 決策 7 的前提「DTO 不會產生」不成立。
  - B1 修正（TDD）：RED `Failed!  - Failed:     4, Passed:     6, Skipped:     0, Total:    10, Duration: 97 ms`（例：`Expected FieldErrorKeyFormatter.Format(key) to be "['x y']" with a length of 7, but "$['x y']" has a length of 8`）；GREEN FieldErrorKeyFormatterTests+ModelBindingValidationResponseTests `Passed!  - Failed:     0, Passed:    18, Skipped:     0, Total:    18, Duration: 7 s`；突變（拿掉 `$[` 前綴移除）`Failed!  - Failed:     3, Passed:    15, Skipped:     0, Total:    18`（含整合測試 `CreateEvent_WithMalformedUnknownPropertyName_ErrorKeysHaveNoDollarPrefix`）；還原後全套（原文）：
    ```
    Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 105 ms - ProjectC.Domain.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   566, Skipped:     0, Total:   566, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 9 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: 51 s - ProjectC.Infrastructure.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   725, Skipped:     0, Total:   725, Duration: 3 m 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
    突變未用 `verify-test-evidence.sh restore`：`FieldErrorKeyFormatter.cs` 是尚未 commit 的新檔，restore 會整檔移走而無法編譯，故手動備份後改一行再還原。design.md 決策 7 已改寫。
  - 第 2 輪（2026-11-08，工作樹未 commit）：strict-reviewer PASS；全新對抗式 subagent 無 blocking（非 blocking：字典鍵含 `']` 時括號段提早結束、普通字典鍵被小寫、空字串鍵尾端多 `.`，皆需 client 刻意構造，回應僅影響回音鍵外觀）；Claude Opus 5.5 PASS（容器內實測 STJ 路徑含 `$['a'b']`、`$['$.a']`、`$.`、`$` 皆不帶 `$` 前綴、不拋例外）。Opus 實測：Application 566、WebApi 725、vitest 673 通過。
  - 已知取捨：client 自訂、以 `$` 開頭的屬性名（如 `$x`）STJ 用點號路徑，鍵原樣保留為 `$x`，不視為框架前綴。
- [x] 9.2 真實瀏覽器驗證：後台建立活動表單的規則（`maxlength="200"`、`maxLengthRule`、必填、限購上下限、販售期間、`toISOString()` 固定帶 Z）已鏡像後端，正常 UI 送不出會觸發 Validator 的值，故 AER-031 的畫面行為由 task 6.1 的 Vitest 表單頁測試（含突變）證明；瀏覽器僅確認 API 回應：以 claude-in-chrome 登入後台後，在頁面內以 JS `fetch` 帶 token 直送（access token 只存在 Pinia 記憶體，localStorage 只有 refresh token；dev build 從 `__vue_app__` 讀 Pinia auth store 取 access token，不得呼叫 refresh 端點，否則 refresh token 輪替會使頁面登出；token 須帶主辦方情境，否則得 403） `POST /api/admin/events`（title 201 字元、其餘欄位為合法值；預期 400），以 `read_network_requests` 或回應文字核對含 `errors.title` 與 `traceId`，並確認 DB 沒有新增活動。同時確認既有表單頁載入與送出一筆合法活動仍正常。
  - 完成紀錄（2026-11-08，claude-in-chrome，`https://localhost:5173`，切換「Ticket-Hub 示範主辦方」情境後以頁面內 JS fetch 帶記憶體 token，未呼叫 refresh）：
    - `POST /api/admin/events`（title 201 字元）→ 400：`{"title":"Validation","status":400,"detail":"The length of 'Title' must be 200 characters or fewer. You entered 201 characters.","errors":{"title":["The length of 'Title' must be 200 characters or fewer. You entered 201 characters."]},"traceId":"0HNPTCLN7M8MU:00000001"}`。切換情境前同請求回 403 `{"title":"Forbidden","status":403,"traceId":"0HNPTCLN7M8MQ:00000001"}`（符合 jwt-challenge-problem-details）。
    - 模型繫結：`{"title":"t","maxTicketsPerOrder":"abc"}` → 400，鍵 `maxTicketsPerOrder`、無 `detail`；`{"title":"t","x y": tru}` → 400，鍵 `['x y']`（無 `$`，B1 修正生效）。
    - DB：上述三次 400 後 `select count(*) from "Events"` 仍為 7。
    - 表單頁 `/admin/events/new` 載入正常；以 UI 填入合法值送出，顯示「活動建立成功」並導向活動列表，DB 變為 8 筆（新增一筆 `[測試] validation-error-fields 驗證`，Id `4e40a11b-9975-47dc-9f67-9d7059fb2a97`，已於使用者同意後連同其 350 筆 EventSeats 刪除，Events 回到 7 筆）。
    - 補註：框架產生的錯誤訊息文字本身含 `Path: $.maxTicketsPerOrder`（STJ 原文），那是訊息內容而非鍵，spec 只規範鍵。
