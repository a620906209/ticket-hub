## 1. 錄製現況基準

對應 AER-012、AER-013（header 不變）。

- [x] 1.1 以整合測試用的同一個 `WebApplicationFactory`（相同 Issuer／SigningKey）搭配固定 `exp` 的 token，對 `GET /api/orders` 錄下現況：無 token、格式錯誤 token、簽章錯誤 token、過期 token 四種的狀態碼、`WWW-Authenticate` header 原文與 body 長度（過期案例的 `error_description` 含該 token 的 `exp`，故基準須由固定 `exp` 產生）；記入下方完成紀錄作為 header 不變的基準，另以 curl 對開發中 api 錄一份僅供人工參考（TDD 例外：純調查，N/A）

完成紀錄（2026-11-07，探針測試以 `CustomWebApplicationFactory`、固定 `exp`=2020-01-01 錄得後已刪除；命令 `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~JwtChallengeBaselineProbe" --logger "console;verbosity=detailed"`）：

```
BASELINE none: 401 WWW-Authenticate=[Bearer] raw=[Bearer] bodyLen=0 contentType=
BASELINE malformed: 401 WWW-Authenticate=[Bearer error="invalid_token"] raw=[Bearer error="invalid_token"] bodyLen=0 contentType=
BASELINE wrongSignature: 401 WWW-Authenticate=[Bearer error="invalid_token", error_description="The signature key was not found"] raw=[Bearer error="invalid_token", error_description="The signature key was not found"] bodyLen=0 contentType=
BASELINE expired: 401 WWW-Authenticate=[Bearer error="invalid_token", error_description="The token expired at '01/01/2020 00:00:00'"] raw=[Bearer error="invalid_token", error_description="The token expired at '01/01/2020 00:00:00'"] bodyLen=0 contentType=
```

curl 對開發中 api 的人工參考：未另錄（proposal 階段已實測無 token／無效 token 為 `Content-Length: 0`，與上列一致）。

另探針確認框架在 `OnChallenge` 觸發前已填好 `context.Error`／`ErrorDescription`（design 決策 1 的前提成立）：

```
PROBE error=[invalid_token] desc=[The token expired at '01/01/2020 00:00:00'] failure=[SecurityTokenExpiredException]
PROBE error=[] desc=[] failure=[]
PROBE error=[invalid_token] desc=[The signature key was not found] failure=[SecurityTokenSignatureKeyNotFoundException]
PROBE error=[invalid_token] desc=[] failure=[SecurityTokenMalformedException]
```

## 2. 401 挑戰回應為 ProblemDetails

對應 AER-012、AER-013、AER-017、AER-018。

- [x] 2.1 TDD（RED）：新增整合測試（沿用 `WebApplicationFactory`／Testcontainers 既有 fixture）——無 token、格式錯誤 token、簽章錯誤 token、過期 token 各一（token 以固定 `exp` 簽發），斷言狀態碼 401、content-type `application/problem+json`、body 含 `status`=401、`title`=`Unauthorized`、非空 `traceId`、無 `detail`，且 `WWW-Authenticate` 逐字等於 1.1 以同一 factory 錄得的基準；另新增業務 401／403 不受影響的測試（AER-018：`POST /api/auth/login` 密碼錯誤的 401、查看他人訂單的 403 仍含原 `detail`；此二測試守護既有行為，RED 階段預期即為通過，不計入 RED 證據，也不得為了製造失敗而改動）；另以已知 `TraceIdentifier` 的單元測試斷言 `traceId` 等於該值（AER-017）。預期 RED：body 為空（`GetProperty` 失敗或 content-type 不符），header 斷言通過
- [x] 2.2 GREEN：在 `AddJwtBearer` 設定 `JwtBearerEvents.OnChallenge`（design 決策 1），重跑 2.1。header 與基準不符時停止並回報，不自行改變 header 內容
- [x] 2.3 重構（若有）後重跑 2.1 與既有授權矩陣測試；無則標 N/A

完成紀錄（2.1 與 3.1 的測試寫在同一檔 `Auth/JwtChallengeProblemDetailsTests.cs`＋`TestSupport/ForbidEndpointWebApplicationFactory.cs`，RED／GREEN 合併為一次執行，涵蓋 AER-012～018；`Forbid()` 路徑以測試專用端點 `ForbidTestController` 驗證，factory 可承載，無需例外）：

**RED**（正式程式碼修改前；命令 `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --no-restore --filter "FullyQualifiedName~JwtChallengeProblemDetailsTests|FullyQualifiedName~JwtForbidProblemDetailsTests"`）。第一次執行時 AER-014 誤打不存在的 `GET /api/admin/members`（404），已改為 `POST /api/admin/members/{id}/deactivate` 後重跑，以下為重跑輸出（原文摘錄，7 個 NullReference 為 `response.Content.Headers.ContentType` 為 null，即空 body／無 content-type，符合預期 RED 分支；`WWW-Authenticate` 斷言在 content-type 之後，故此階段未被執行，header 逐字比對於 GREEN 驗證）：

```
  Failed ...JwtChallengeProblemDetailsTests.OnForbidden_WithKnownTraceIdentifier_WritesThatTraceId [312 ms]
   Expected httpContext.Response.StatusCode to be 403, but found 200 (difference of -203).
  Failed ...JwtForbidProblemDetailsTests.ForbidTestEndpoint_WhenControllerCallsForbid_Returns403ProblemDetails [1 s]
   System.NullReferenceException : Object reference not set to an instance of an object.
  Failed ...JwtChallengeProblemDetailsTests.PostAdminEvents_ByAdminWithoutOrganizerContext_Returns403ProblemDetails [368 ms]
   System.NullReferenceException : Object reference not set to an instance of an object.
  Failed ...GetOrders_WhenAuthenticationFails_Returns401ProblemDetailsWithUnchangedChallengeHeader(tokenCase: "expired", ...) [6 ms]
   System.NullReferenceException : Object reference not set to an instance of an object.
  Failed ...GetOrders_WhenAuthenticationFails_Returns401ProblemDetailsWithUnchangedChallengeHeader(tokenCase: "none", ...) [2 ms]
   System.NullReferenceException : Object reference not set to an instance of an object.
  Failed ...GetOrders_WhenAuthenticationFails_Returns401ProblemDetailsWithUnchangedChallengeHeader(tokenCase: "wrongSignature", ...) [1 ms]
   System.NullReferenceException : Object reference not set to an instance of an object.
  Failed ...GetOrders_WhenAuthenticationFails_Returns401ProblemDetailsWithUnchangedChallengeHeader(tokenCase: "malformed", ...) [1 ms]
   System.NullReferenceException : Object reference not set to an instance of an object.
  Failed ...JwtChallengeProblemDetailsTests.GetAdminMembers_ByNonAdmin_Returns403ProblemDetails [347 ms]
   System.NullReferenceException : Object reference not set to an instance of an object.
  Failed ...JwtChallengeProblemDetailsTests.OnChallenge_WithKnownTraceIdentifier_WritesThatTraceId [3 ms]
   Expected httpContext.Response.StatusCode to be 401, but found 200 (difference of -201).
Failed!  - Failed:     9, Passed:     2, Skipped:     0, Total:    11, Duration: 3 s - ProjectC.WebApi.Tests.dll (net10.0)
```

通過的 2 個為 AER-018 守護測試（`Login_WithWrongPassword_StillReturns401WithDetail`、`ConfirmOrder_ByNonBuyer_StillReturns403WithDetail`），預期即通過，不計入 RED 證據。RED 輸出中的 `GetAdminMembers_ByNonAdmin_Returns403ProblemDetails` 於 GREEN 前改名為 `DeactivateMember_ByNonAdmin_Returns403ProblemDetails`（實際請求為 POST deactivate，原名誤導），邏輯未變。

**GREEN**：新增 `src/ProjectC.WebApi/Common/JwtBearerProblemDetailsResponses.cs`，於 `AddJwtBearer` 設定 `JwtBearerEvents.OnChallenge`／`OnForbidden`。第一次 GREEN 嘗試僅 `WriteAsJsonAsync(problemDetails)` 時 7 個測試因 content-type 為 `application/json` 失敗（`Expected response.Content.Headers.ContentType!.MediaType to be a match with the expectation, but it differs at index 12`），改為明確 `contentType: "application/problem+json"` 後重跑：

```
Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11, Duration: 3 s - ProjectC.WebApi.Tests.dll (net10.0)
```

四個 401 案例的 `WWW-Authenticate` 皆逐字等於 1.1 基準（測試內以基準字串斷言）。

**重構**：N/A（無重構）；2.3、3.3 的「既有授權矩陣測試」包含於 6.1 後端全套執行。**RED 還原驗證**：本 change 非 bug fix、非下單／排隊／座位庫存核心邏輯，依 CLAUDE.md 不適用，未執行 stash 還原。

## 3. 403 拒絕回應為 ProblemDetails

對應 AER-014～AER-017。

- [x] 3.1 TDD（RED）：新增整合測試——`AdminOnly` 角色不足（AER-014）、`RequireOrganizerContext` 缺合法 `OrganizerId`（AER-015），斷言 403、`application/problem+json`、`title`=`Forbidden`、非空 `traceId`、無 `detail`；另以已知 `TraceIdentifier` 的單元測試對 `OnForbidden` 斷言 `traceId` 等於該值（AER-017 的 403 分支，需能擋下改用 `Activity.Current.Id` 的實作）；`Forbid()` 路徑（AER-016）依 design 決策 4 以測試專用端點驗證；若 factory 無法承載，停止並回報，不得自行改用直接呼叫 `OnForbidden`（無 design 例外紀錄，不能取代 AC）。預期 RED：body 為空
- [x] 3.2 GREEN：設定 `JwtBearerEvents.OnForbidden`，重跑 3.1
- [x] 3.3 重構（若有）後重跑 3.1 與既有授權矩陣測試（`EventManagementAuthorizationMatrixTests` 等）；無則標 N/A

完成紀錄：見第 2 節（2.1 與 3.1 測試同檔、RED/GREEN 合併執行，涵蓋 AER-012～018）。

## 4. 文件同步

純文件，TDD 標示 N/A（無可自動化行為）。

- [x] 4.1 更新 `docs/api-conventions.md` 第 2 節：新增「認證挑戰／授權拒絕」規則，移除「未驗證」項目 2
- [x] 4.2 更新 `docs/api-conventions-todo.md`：第 2 項標記完成、0-A 勾選框打勾、0-A 標題「403 為推論」改為實測結果
- [x] 4.3 `openspec validate jwt-challenge-problem-details` 通過，並確認 `openspec/specs` 沒有寫死「401／403 無 body」或「由後續 change 定義」JWT 401／403 的敘述（主 spec 該句已由本 change 的 MODIFIED 改寫）

## 5. hardener 最終檢查

- [x] 5.1 程式碼全部完成後，對 `JwtBearerEvents` 處理程式套用 `.claude/skills/hardener/SKILL.md` 檢查清單（例外邊界：寫 body 失敗、response 已開始時不得再改狀態碼；`CancellationToken`；日誌遮蔽：不記錄 token 內容）；結果記於下方，無缺漏則註明未修改程式碼

完成紀錄（2026-11-07，對 `JwtBearerProblemDetailsResponses` 套用 hardener 清單）：
- 例外邊界：兩個處理程式開頭皆檢查 `Response.HasStarted`，已開始則不再改狀態碼或寫 body；`WriteAsJsonAsync` 的例外不吞掉，交由全域 `IExceptionHandler`（用戶端中止時會拋 `OperationCanceledException`，其在全域處理器下的記錄行為未實測，與 `GlobalExceptionHandler` 同樣傳入 `RequestAborted` 的既有做法一致）。
- `CancellationToken`：傳入 `HttpContext.RequestAborted`。
- 日誌遮蔽：處理程式不寫任何 log，不接觸 token 內容；`WWW-Authenticate` 的 `error_description` 沿用框架原本即回傳的內容。
- 參數驗證／Entity 存在性／狀態機／並發：不適用（無外部輸入、無 DB、無共享狀態）。
- 結論：無缺漏，檢查後未再修改程式碼。

## 6. 全套驗證與審查

- [x] 6.1 在容器內執行後端全套與前端全套測試，貼各測試專案摘要行原文（前端無變更，確認不受影響）
6.1 完成紀錄（2026-11-07，容器內 `docker compose exec -T api dotnet test --no-restore`；第 1 輪審查後，Contains(' ') 改 IndexOf(' ') > 0 與 AER-015 測試改名，全套重跑如下）：
```
Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 85 ms - ProjectC.Domain.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   549, Skipped:     0, Total:   549, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 9 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: 50 s - ProjectC.Infrastructure.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   693, Skipped:     0, Total:   693, Duration: 3 m 27 s - ProjectC.WebApi.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    45, Skipped:     0, Total:    45, Duration: 15 s - ProjectC.WebApi.Tests.dll (net10.0)  [Jwt* + AuthorizationMatrix 子集]
Test Files  47 passed (47)
     Tests  671 passed (671)   [web, npm run test；前端無變更，沿用首次全套結果]
```
- [x] 6.2 前端真實瀏覽器驗證：N/A（無前端變更）；改以 curl 對執行中的 api（先 `docker compose restart api` 確認無 `dotnet watch` 停在重啟提示）驗證 401 與 403 的 body／header，並核對 Seq 同一請求的 `TraceId` 等於回應 `traceId`；另在需登入頁面（例如我的訂單）以過期 token 操作一次，確認前端仍能換發並重放
6.2 完成紀錄（2026-11-07，`docker compose restart api` 後對 http://localhost:8080 curl）：```--none: HTTP/1.1 401 Unauthorized | Content-Type: application/problem+json | WWW-Authenticate: Bearer{"title":"Unauthorized","status":401,"traceId":"0HNPS6AJDGL6N:00000001"}--bad: HTTP/1.1 401 Unauthorized | Content-Type: application/problem+json | WWW-Authenticate: Bearer error="invalid_token"{"title":"Unauthorized","status":401,"traceId":"0HNPS6AJDGL6O:00000001"}```Seq（GET /api/events?filter=RequestPath = '/api/orders'）：兩筆請求日誌 `StatusCode`=401，`TraceId` 屬性分別為 `0HNPS6AJDGL6N:00000001`、`0HNPS6AJDGL6O:00000001`，與回應 `traceId` 相同。
6.2 範圍調整：使用者於 2026-11-07 明確選擇跳過「403 實機 curl」與「過期 token 前端換發重放」兩項（需開發環境登入帳號）；403 由整合測試實測，前端未改且 `httpClient.ts` 對 401 只看狀態碼。上方 401 與 Seq TraceId 驗證已完成。
- [x] 6.3 `strict-reviewer` 與全新的對抗式 general-purpose subagent 依序審查，修正後重跑，直到兩者皆無 blocking
6.3 完成紀錄：strict-reviewer 第 1 輪 PASS（無 blocking；警告已處理：第 3 節佔位文字、6.2 跳過決定）；全新對抗式 subagent 第 1 輪無 blocking（次要建議：RequestAborted 的 OCE 記錄行為未實測、未覆蓋的 error_uri／scheme 含空白分支，皆為接受的非 blocking）。自上次雙方 PASS 後僅改 tasks.md 紀錄。
6.3 第 2 輪（使用者要求的 Claude Opus 交叉審查：無 blocking，4 項次要建議；採納 `Contains(' ')` → `IndexOf(' ') > 0` 與 AER-015 測試改名，程式碼因此有改動，故雙方重審）：strict-reviewer PASS（無 blocking；Jwt* + AuthorizationMatrix 子集 50/50 通過）；全新對抗式 subagent 無 blocking（Jwt* 11/11 通過；建議：error_uri／scheme 含空白分支未覆蓋、OCE 記錄行為未實測，皆接受；歸檔後須確認主 spec 不再殘留「由後續 change 定義」）。
- [x] 6.4 歸檔前確認使用者已明確同意；歸檔後不自動 commit
