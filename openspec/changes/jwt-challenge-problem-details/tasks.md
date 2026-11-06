## 1. 錄製現況基準

對應 AER-012、AER-013（header 不變）。

- [ ] 1.1 以整合測試用的同一個 `WebApplicationFactory`（相同 Issuer／SigningKey）搭配固定 `exp` 的 token，對 `GET /api/orders` 錄下現況：無 token、格式錯誤 token、簽章錯誤 token、過期 token 四種的狀態碼、`WWW-Authenticate` header 原文與 body 長度（過期案例的 `error_description` 含該 token 的 `exp`，故基準須由固定 `exp` 產生）；記入下方完成紀錄作為 header 不變的基準，另以 curl 對開發中 api 錄一份僅供人工參考（TDD 例外：純調查，N/A）

完成紀錄：（實作時填寫）

## 2. 401 挑戰回應為 ProblemDetails

對應 AER-012、AER-013、AER-017、AER-018。

- [ ] 2.1 TDD（RED）：新增整合測試（沿用 `WebApplicationFactory`／Testcontainers 既有 fixture）——無 token、格式錯誤 token、簽章錯誤 token、過期 token 各一（token 以固定 `exp` 簽發），斷言狀態碼 401、content-type `application/problem+json`、body 含 `status`=401、`title`=`Unauthorized`、非空 `traceId`、無 `detail`，且 `WWW-Authenticate` 逐字等於 1.1 以同一 factory 錄得的基準；另新增業務 401／403 不受影響的測試（AER-018：`POST /api/auth/login` 密碼錯誤的 401、查看他人訂單的 403 仍含原 `detail`；此二測試守護既有行為，RED 階段預期即為通過，不計入 RED 證據，也不得為了製造失敗而改動）；另以已知 `TraceIdentifier` 的單元測試斷言 `traceId` 等於該值（AER-017）。預期 RED：body 為空（`GetProperty` 失敗或 content-type 不符），header 斷言通過
- [ ] 2.2 GREEN：在 `AddJwtBearer` 設定 `JwtBearerEvents.OnChallenge`（design 決策 1），重跑 2.1。header 與基準不符時停止並回報，不自行改變 header 內容
- [ ] 2.3 重構（若有）後重跑 2.1 與既有授權矩陣測試；無則標 N/A

完成紀錄：（實作時填寫 RED／GREEN／重構，依 CLAUDE.md「證據貼原文」）

## 3. 403 拒絕回應為 ProblemDetails

對應 AER-014～AER-017。

- [ ] 3.1 TDD（RED）：新增整合測試——`AdminOnly` 角色不足（AER-014）、`RequireOrganizerContext` 缺合法 `OrganizerId`（AER-015），斷言 403、`application/problem+json`、`title`=`Forbidden`、非空 `traceId`、無 `detail`；另以已知 `TraceIdentifier` 的單元測試對 `OnForbidden` 斷言 `traceId` 等於該值（AER-017 的 403 分支，需能擋下改用 `Activity.Current.Id` 的實作）；`Forbid()` 路徑（AER-016）依 design 決策 4 以測試專用端點驗證；若 factory 無法承載，停止並回報，不得自行改用直接呼叫 `OnForbidden`（無 design 例外紀錄，不能取代 AC）。預期 RED：body 為空
- [ ] 3.2 GREEN：設定 `JwtBearerEvents.OnForbidden`，重跑 3.1
- [ ] 3.3 重構（若有）後重跑 3.1 與既有授權矩陣測試（`EventManagementAuthorizationMatrixTests` 等）；無則標 N/A

完成紀錄：（實作時填寫）

## 4. 文件同步

純文件，TDD 標示 N/A（無可自動化行為）。

- [ ] 4.1 更新 `docs/api-conventions.md` 第 2 節：新增「認證挑戰／授權拒絕」規則，移除「未驗證」項目 2
- [ ] 4.2 更新 `docs/api-conventions-todo.md`：第 2 項標記完成、0-A 勾選框打勾、0-A 標題「403 為推論」改為實測結果
- [ ] 4.3 `openspec validate jwt-challenge-problem-details` 通過，並確認 `openspec/specs` 沒有寫死「401／403 無 body」或「由後續 change 定義」JWT 401／403 的敘述（主 spec 該句已由本 change 的 MODIFIED 改寫）

## 5. hardener 最終檢查

- [ ] 5.1 程式碼全部完成後，對 `JwtBearerEvents` 處理程式套用 `.claude/skills/hardener/SKILL.md` 檢查清單（例外邊界：寫 body 失敗、response 已開始時不得再改狀態碼；`CancellationToken`；日誌遮蔽：不記錄 token 內容）；結果記於下方，無缺漏則註明未修改程式碼

完成紀錄：（實作時填寫）

## 6. 全套驗證與審查

- [ ] 6.1 在容器內執行後端全套與前端全套測試，貼各測試專案摘要行原文（前端無變更，確認不受影響）
- [ ] 6.2 前端真實瀏覽器驗證：N/A（無前端變更）；改以 curl 對執行中的 api（先 `docker compose restart api` 確認無 `dotnet watch` 停在重啟提示）驗證 401 與 403 的 body／header，並核對 Seq 同一請求的 `TraceId` 等於回應 `traceId`；另在需登入頁面（例如我的訂單）以過期 token 操作一次，確認前端仍能換發並重放
- [ ] 6.3 `strict-reviewer` 與全新的對抗式 general-purpose subagent 依序審查，修正後重跑，直到兩者皆無 blocking
- [ ] 6.4 歸檔前確認使用者已明確同意；歸檔後不自動 commit
