## 1. 框架層錯誤 Title 改為機器代碼

對應 AER-001～AER-004（1.2、1.3）、AER-005（1.5）、RBL-011、HC-BODY-EXCEPTION。1.2 保留既有「500 不含 `detail`」的斷言。RBL-012、RBL-013、RBL-015、HC-BODY-HEALTHY、HC-BODY-UNHEALTHY 行為不變，由既有測試守護，6.1 全套重跑；其中 RBL-015 的 400 `title` 斷言（`RequestBodyLimitsKestrelTests` 現為 `Bad Request`）隨 1.2 改為 `BadRequest`。

- [ ] 1.1 全專案（`src/`、`tests/`、`web/src`、`k6` 或壓測腳本、`docs/`）搜尋舊字串 `An unexpected error occurred.`、`Request body too large.`，列出清單於本 task 下，確認沒有遺漏的消費者
- [ ] 1.2 TDD（RED）：修改 `GlobalExceptionHandlerTests` 的既有斷言為新代碼（500 → `InternalServerError`、413 → `PayloadTooLarge`、408 → `RequestTimeout`、400 → `BadRequest`），新增非 408／413／400 的狀態碼測試（431 → `RequestHeaderFieldsTooLarge`；418 → `Imateapot`，驗證標點被移除）與「無 reason phrase 的狀態碼 → `BadRequest`」測試（須選用框架確實回傳空字串的狀態碼）；同步修改 `RequestBodyLimitsKestrelTests`（413、400）、`HealthEndpointTestSupport`、`OrganizerScopingDataCorruptionTests` 的舊 `Title` 斷言。在容器內執行相關測試，預期 RED：Expected 為新代碼、Actual 為舊句子
- [ ] 1.3 GREEN：修改 `GlobalExceptionHandler`，以 `switch` 對照 413／408／400，其餘狀態碼用 reason phrase 僅保留英數字元、空字串退回 `BadRequest`；500 改為 `InternalServerError`。重跑 1.2 的測試，預期全數通過
- [ ] 1.4 重構（若有）後重跑 1.2 的測試；無則標 N/A
- [ ] 1.5 AER-005（429 的 `title` 維持 `TooManyRequests`）：本 change 不修改限流程式碼，由既有 `LoginRateLimitingTests`（斷言 `title == "TooManyRequests"`）守護；GREEN 後重跑該測試類別並把輸出摘要行記入下方完成紀錄。若實作過程動到 `RateLimitingServiceCollectionExtensions.cs`，改為先新增斷言 429 `title` 的測試再修改

完成紀錄（RED／GREEN／重構，依 CLAUDE.md「證據貼原文」）：

- 1.1 搜尋結果：
- RED（命令、失敗測試名稱與 Expected／Actual 原文、發生在正式程式碼修改前）：
- GREEN（命令與各測試專案摘要行）：
- 重構後重跑（或 N/A）：

## 2. 業務錯誤回應帶 traceId

對應 AER-006～AER-008。

- [ ] 2.1 TDD（RED）：新增測試——(a) 單元測試：對 `HttpContext.TraceIdentifier` 設為已知值的 `ActionContext` 執行業務失敗結果，斷言回應 body 的 `traceId` 等於該值，且 `status`、`title`、`detail` 不變、content-type 為 `application/problem+json`（AER-007）；(b) 整合測試：對任一會回 `NotFound` 的端點請求，斷言含非空 `traceId`（AER-006），連續兩次請求的 `traceId` 不同（AER-008）。預期 RED：`traceId` 屬性不存在
- [ ] 2.2 GREEN：新增小型 `IActionResult` 實作並由 `ResultExtensions.CreateProblemResult` 回傳，執行時寫入 `traceId`（design 決策 2）；以實測確認 `ProblemDetailsFactory` 的 `traceId` 行為並把結果記入 design 決策 2 的註記。重跑 2.1 的測試
- [ ] 2.3 重構（若有）後重跑 2.1 的測試與 `ToActionResult` 相關的既有 Controller 整合測試；無則標 N/A

完成紀錄：

- RED：
- GREEN：
- 重構後重跑（或 N/A）：

## 3. 前端 ApiError 訊息不再退回 title

對應 AER-009～AER-011。

- [ ] 3.1 TDD（RED）：在 `httpClient` 單元測試新增——有 `detail` → message 等於 detail（AER-010）；有 `title` 無 `detail` → message 為含狀態碼的通用文字且不等於 `title`（AER-009）；非 JSON 失敗回應 → 通用文字且 `problem` 為 `null`（AER-011）。在 `web` 容器內執行，預期 RED：AER-009 的 Actual 為 `title` 字串
- [ ] 3.2 GREEN：修改 `ApiError` 建構子為 `problem?.detail ?? \`API 請求失敗（狀態碼 ${status}）\``。重跑 3.1 的測試與前端全套測試
- [ ] 3.3 重構（若有）後重跑；無則標 N/A

完成紀錄：

- RED：
- GREEN：
- 重構後重跑（或 N/A）：

## 4. 文件同步

純文件，TDD 標示 N/A（無可自動化行為）。

- [ ] 4.1 更新 `docs/api-conventions.md` 第 2 節：框架層錯誤改為新代碼、業務錯誤補 `traceId`、移除不一致項目 1、2；保留「模型繫結自動產生的 400 `Title` 為框架預設句子」為待決定
- [ ] 4.2 更新 `docs/api-conventions-todo.md` 第 1 項標記完成
- [ ] 4.3 確認 `openspec/specs` 中沒有其他寫死舊 `title` 字串的 Requirement（`openspec validate error-response-title-traceid` 並搜尋舊字串）

## 5. hardener 最終檢查

- [ ] 5.1 程式碼全部完成後，對 `GlobalExceptionHandler` 與新增的 `IActionResult` 套用 `.claude/skills/hardener/SKILL.md` 檢查清單（`CancellationToken` 傳遞、例外邊界、日誌遮蔽等）；結果記於下方。若判定不適用須說明理由；若補強則依 TDD 先補 RED 測試

完成紀錄：

## 6. 全套驗證與審查

- [ ] 6.1 在容器內執行後端全套測試與前端全套測試，貼各測試專案摘要行原文
- [ ] 6.2 前端真實瀏覽器驗證（claude-in-chrome）：(a) 觸發一個業務錯誤（例如登入驗證碼錯誤），以頁面文字與回應 body 核對 `traceId` 存在、頁面行為不變；(b) 在已登入頁面以 fetch 對建立訂單端點送出超限本文，核對回應 `title` 為 `PayloadTooLarge`；(c) 核對 Seq 中同一請求的 `TraceId` 與回應 `traceId` 相同。數字與狀態以頁面文字或回應 body 核對，不只依截圖
- [ ] 6.3 `strict-reviewer` 與全新的對抗式 general-purpose subagent 依序審查，修正後重跑，直到兩者皆無 blocking
- [ ] 6.4 歸檔前確認使用者已明確同意；歸檔後不自動 commit
