## Why

JWT middleware 產生的 401（`[Authorize]` 失敗）與 403（Policy 失敗、控制器 `Forbid()`）回應沒有 body（實測 `GET /api/orders` 無 token／無效 token 皆為 `Content-Length: 0`），與其他錯誤一律為 `ProblemDetails` 的契約不一致，使用者回報問題時也沒有 `traceId` 可與 log 對照。這是 `docs/api-conventions.md` 第 2 節「未驗證」項目、`docs/api-conventions-todo.md` 第 2 項（0-A 查證已確認缺口）。既有授權測試只斷言狀態碼，這個缺口沒有被守護。

## What Changes

- 以 `JwtBearerEvents.OnChallenge`／`OnForbidden` 讓 401／403 回應帶 `ProblemDetails` body：`status`、`title`（`Unauthorized`／`Forbidden`，與 `ErrorType` 名稱一致）、`traceId`（`HttpContext.TraceIdentifier`）；不帶 `detail`。
- 狀態碼與 `WWW-Authenticate` header 內容與現況完全相同（含 `error="invalid_token"` 等）。
- 新增整合測試守護：無 token、格式錯誤 token、簽章錯誤 token、過期 token 的 401，業務失敗轉換的 401／403 仍保留 `detail`（AER-018 回歸），`AdminOnly` 角色不足與 `RequireOrganizerContext` 失敗的 403，並斷言 body 與 header；補上 0-A 中 403 只靠推論、未實測的缺口。
- 更新 `docs/api-conventions.md` 第 2 節（移除「未驗證」項目並記入 401／403 規則）、`docs/api-conventions-todo.md` 第 2 項標記完成。

不在範圍內：模型繫結 400 的 `Title`、驗證錯誤逐欄位結構、`detail` 內容、前端（401 仍只依狀態碼換發，不讀 body）、`UseStatusCodePages`（不擴及 404／405 等其他空 body 回應）。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `api-error-responses`: 新增 Requirement——認證挑戰（401）與授權拒絕（403）的回應為含 `status`、`title`、`traceId` 的 `ProblemDetails`，且 `WWW-Authenticate` header 與狀態碼不變；另 MODIFIED「框架層錯誤的 Title 為機器代碼」，把範圍句改為 JWT 401／403 由新 Requirement 規範。

## Impact

- 後端：`src/ProjectC.WebApi/Extensions/AuthenticationServiceCollectionExtensions.cs`（設定 `JwtBearerEvents`）；視實作可能新增同資料夾外的小型事件處理類別。
- 測試：`tests/ProjectC.WebApi.Tests` 新增認證回應整合測試；既有授權矩陣測試不改。
- 文件：`docs/api-conventions.md`、`docs/api-conventions-todo.md`。
- 無 DB／migration、無新相依、無路由變更、無前端變更。
- Change 顆粒度：1 個 capability、tasks 遠少於 30 項，不需拆分。
