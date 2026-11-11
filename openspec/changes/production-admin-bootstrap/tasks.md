# Tasks

> 所有 dotnet／測試指令一律在容器內執行（`docker compose exec api ...`）。RED／GREEN 證據依 CLAUDE.md「證據貼原文」填入各 task 的「完成紀錄」；提案階段只定義計畫。
> 實作這些 task 的 AC 對應：ADMIN-BOOT-001~010（見 specs/admin-bootstrap/spec.md）。

## 1. Options 與啟動期設定驗證

- [ ] 1.1 新增 `AdminBootstrapOptions` 與 `AdminBootstrapOptionsValidator`（WebApi），並於 `ResolveEagerlyValidatedOptions` 觸發；對應 ADMIN-BOOT-005/006/007。
  - 先寫測試（WebApi.Tests/Startup，比照 `CaptchaOptionsFailFastTests`）：兩項皆空 → 啟動成功；只設 Email／只設 Password → 啟動失敗，且各寫一案、缺哪一項訊息就含哪一項的名稱；失敗例外文字一併收集並斷言不含密碼值（ADMIN-BOOT-010）；Email 格式錯、含前後空白、密碼 <8 碼／無數字／無字母 → 失敗；失敗訊息不含密碼值。
  - 預期 RED：Options／validator 尚不存在或未驗證，斷言「啟動應失敗」得到成功（Expected exception／Actual no exception），目標行為不符而非編譯錯誤（先建空殼型別使其可編譯）。
  - GREEN：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~AdminBootstrapOptions"` 全數通過。
  - 完成紀錄：RED／GREEN／（重構重跑或 N/A）：<待填>

## 2. ProductionAdminBootstrapper

- [ ] 2.1 實作 `ProductionAdminBootstrapper`（Infrastructure）；對應 ADMIN-BOOT-001/002/003/005/008/010。先對照 hardener 清單列出防禦分支（參數、`CancellationToken` 傳遞、並發、例外邊界、日誌遮蔽）一併納入測試。
  - 先寫測試（Infrastructure.Tests/Seeding，Testcontainers，比照 `DevelopmentDataSeederTests`）：無 Admin → 建立 Admin 且無 Organizer、密碼為雜湊且可驗證；已有 Admin（不同 Email／已停用）→ 無變更；相同 Email 非 Admin → 拋明確例外且該會員不變，並對該例外訊息斷言不含設定 Email 值與資料庫既有會員 Email 值（ADMIN-BOOT-003 的驗證證據）；未設定 → 略過；有未套用 migration → 略過，並斷言 Warning 等級且訊息含未套用 migration 數量；已有 Admin 與未設定的略過路徑斷言為 Info 等級；內層非 SqlState 23505 的 `DbUpdateException` 一律重拋、23505 但約束非 Email 唯一索引時不視為「已存在」而重拋（避免例外過濾過寬吞掉其他例外）；取消的 `CancellationToken` → 拋 `OperationCanceledException`；以 `ILogger` 擷取全部日誌與例外訊息，斷言不含密碼與雜湊。
  - 預期 RED：bootstrapper 為空殼時，「應建立 Admin」得到 0 筆（Expected 1／Actual 0）等斷言失敗。
  - GREEN：`docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~ProductionAdminBootstrapper"` 通過。
  - 完成紀錄：<待填>
- [ ] 2.2 並發：兩個獨立 `DbContext` 同時執行 bootstrapper；對應 ADMIN-BOOT-009。核心業務邏輯（並發）依 CLAUDE.md 加做還原驗證（`bash scripts/verify-test-evidence.sh restore` 或 `mutate`，例如暫時移除唯一性衝突處理）。
  - 先寫測試：(a) 確定性測試——以 EF `SaveChangesInterceptor`（或等價機制）在 bootstrapper 的 `SaveChanges` 前，另以獨立 DbContext 提交一筆 Email 與 `AdminBootstrap__Email` 完全相同、角色為 Admin 的會員，使 bootstrapper 的寫入實際收到 Email 唯一索引造成的 SqlState 23505；斷言以全新查詢確認該會員為 Admin 後視為成功、不拋例外，並斷言確實走過 23505 恢復分支（以攔截到的 `DbUpdateException` 或日誌／計數佐證，不得只斷言最終資料狀態）；(b) 兩個獨立 `DbContext` 同時執行的壓力測試。
  - 預期 RED：移除衝突處理的版本，其中一個執行緒拋出未處理的 `DbUpdateException`（Expected no exception／Actual DbUpdateException）；變異驗證針對移除 23505 恢復分支。
  - GREEN：同上 filter 通過；多次重跑（至少 20 次）皆通過。
  - 完成紀錄：RED／還原驗證輸出：<待填>

## 3. 註冊與啟動整合

- [ ] 3.1 於 `WebApi/Extensions` 新增 DI 註冊（Scoped）並在 `Program.cs` 於 seeder 區塊旁以 `!IsDevelopment()` 呼叫；更新 `ServiceRegistrationSnapshot.baseline.txt`；對應 ADMIN-BOOT-004。
  - 先寫測試（WebApi.Tests，以 `WebApplicationFactory` 走真實啟動流程，被測主體為 `Program.cs` 的環境判斷，不得只測抽出的輔助方法）：成對的兩個測試，皆設定合法的 `AdminBootstrap__*` 且 `DevelopmentSeed` 未設定、資料庫無 Admin——Development 環境啟動後資料庫無 Admin 會員；非 Development（如 Production 或 Testing）啟動後有該 Email 的 Admin。另驗證非 Development 啟動後以該 Email 與密碼經既有登入流程（測試環境驗證碼可處理）取得 token。
  - 預期 RED：未接上啟動流程時，非 Development 啟動後 Admin 不存在（Expected 1／Actual 0）；並以暫時移除或反轉 `!IsDevelopment()` 條件做變異驗證（`bash scripts/verify-test-evidence.sh mutate`），確認 Development 那個測試會失敗。
  - GREEN：`ServiceRegistrationSnapshotTests` 與新測試通過；整合測試使用 Testcontainers，不連線開發用 db。
  - 完成紀錄：<待填>

## 4. 部署設定與文件

- [ ] 4.1 更新 `k8s/10-configmap.yaml`（`ASPNETCORE_ENVIRONMENT: Production`、`AdminBootstrap__Email`、移除 `DevelopmentSeed__*` 與過時註解）、`k8s/examples/secret.example.yaml`、`scripts/verify-k8s-manifests.sh`（`SECRET_KEYS` 與產生的 secret 改為 `AdminBootstrap__Password`）。TDD：N/A（部署設定），驗證見 4.3。
- [ ] 4.2 更新 `docs/todo/2026-11-10-deployment-env-vars.md`（新鍵、移除 Development 專用說明）、`docs/todo/2026-11-02-deployment-prerequisites.md`／`docs/todo/2026-10-28-k8s-todo.md` 中「首位 Admin bootstrap 待辦」的敘述；註明 K8s 3.3 瀏覽器流程需改走會員申請 + Admin 核准；建立首位 Admin 後應移除 `AdminBootstrap__Password`；本機制不提供密碼輪替；若 Secret 殘留密碼，Admin 全被刪除後重啟會依該密碼重建（移除後此路徑才不會意外觸發）。驗證：`grep -rn "DevelopmentSeed" k8s docs/todo scripts` 無過時殘留。TDD：N/A（純文件）。
- [ ] 4.3 重跑 `bash scripts/verify-k8s-manifests.sh`（`ASPNETCORE_ENVIRONMENT=Production`）並貼原文輸出，確認 api 就緒（含 `/health/ready`），並以 DB 查詢確認 Admin 已建立（Production 不可停用驗證碼，故不以腳本登入；登入由 3.1 整合測試覆蓋，必要時另以真實瀏覽器手動登入）；確認 OpenAPI／SwaggerUI 在 Production 不可用。完成紀錄：<待填>

## 5. 最終檢查

- [ ] 5.1 hardener 最終檢查：對 `ProductionAdminBootstrapper` 套用 `.claude/skills/hardener/SKILL.md` 清單並記錄；有補強依 TDD 先補 RED 測試。
- [ ] 5.2 全套測試（api 容器內 `dotnet test` 各測試專案）貼各專案摘要行原文，0 failed／0 skipped；`graphify update .`。
- [ ] 5.3 spec 同步檢查：確認實作與 specs/admin-bootstrap 一致，偏離處記入 design.md。

## Workflow follow-up

- strict-reviewer 與全新對抗式 subagent 審查迴圈至無 blocking（CLAUDE.md 開發流程步驟 6）。
- 歸檔（`/opsx:archive`，含主 spec 同步）後，詢問是否以 `scripts/archive-todo.sh` 搬移已完成的 `docs/todo/` 項目；合併回 master。
