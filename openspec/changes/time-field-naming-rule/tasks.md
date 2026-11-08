# Tasks

> 對應的 spec：`organizer-management` 的 MODIFIED delta（只把 Scenario `ORG-SWITCH-007` 的 `ExpiresAt` 改為 `ExpiresAtUtc`，語意不變，由任務 4.3 處理）；其餘為純內部命名一致化，驗證依據為 design.md 的決策與下列測試。

## 1. 反射式規則測試與屬性名稱測試（TDD）

- [ ] 1.1 在 `tests/ProjectC.WebApi.Tests/Common/` 新增 `UtcSuffixNamingRuleTests`：掃 Domain、Application、WebApi 組件中 public 型別（含介面與巢狀 public 型別，`IsVisible` 為 true；nonpublic 型別不掃）以 `DeclaredOnly` 取得的 public 實例屬性，找出型別為 `DateTime` / `DateTime?` 者，斷言名稱以 `Utc` 結尾。例外清單初始只有 `IDateTimeProvider.UtcNow`，並在測試中以註解寫明理由（.NET 慣用名 `UtcNow`，名稱已標明 UTC，改名牽動全專案）；例外須以「型別全名＋屬性名」精確比對，不得用萬用字元。測試命名 `DateTimeProperties_AcrossAssemblies_EndWithUtc`。
  - 預期 RED：失敗訊息恰好列出 `RefreshToken.ExpiresAt`、`PasswordResetToken.ExpiresAt` 兩個違規者（`IDateTimeProvider.UtcNow` 已在例外清單，不得出現）；掃描結果須先斷言「至少掃到 N 個 `DateTime` 屬性」（N 取 RED 時實測的掃描數，並記入證據），避免掃描範圍錯誤而空過。
  - 並新增 `RefreshToken` / `PasswordResetToken` 的 Domain 測試：`typeof(...).GetProperty("ExpiresAtUtc")` 非 null 且 `GetProperty("ExpiresAt")` 為 null（反射，非編譯依賴）。
  - 並新增 Infrastructure 測試：`Model.FindEntityType(typeof(RefreshToken)).FindProperty("ExpiresAtUtc")` 非 null，PasswordResetToken 同。
  - RED 紀錄（命令、失敗測試名、實際 Expected／Actual 原文、需在修改正式程式碼前執行）：
  - GREEN 紀錄：
  - 重構後重跑：N/A（改名無重構步驟）。

## 2. 改名實作

- [ ] 2.1 `RefreshToken`、`PasswordResetToken` 屬性改為 `ExpiresAtUtc`，`Issue` 參數改為 `expiresAtUtc`；兩個 EF Configuration、`LoginHandler`、`RefreshTokenHandler`、`RequestPasswordResetHandler` 同步。建置通過，1.1 的三組測試轉為 GREEN。3.2 完成前，所有以 Testcontainers 套用 migration 的整合測試（登入、換發、忘記密碼等）會因 DB 欄位仍是 `ExpiresAt` 而失敗，這是預期現象，不計入 2.1 的 GREEN 判定；全套測試於 5.1 驗證。
  - GREEN 紀錄（命令與摘要行原文）：

## 3. Migration 與資料保留測試（TDD）

- [ ] 3.1 在 `tests/ProjectC.Infrastructure.Tests/Authentication/` 新增 `ExpiresAtUtcRenameMigrationTests`（比照 `OrderCreatedAtBackfillMigrationTests`，Testcontainers）：先 `MigrateAsync` 到前一個 migration（`20261014081305_AddOrderCreatedAt`），先建立 `Member`（`MemberId` 為必填外鍵），再以 raw SQL 植入一筆 `RefreshTokens` 與一筆 `PasswordResetTokens`（含 `ExpiresAt` 值），再遷到新 migration，斷言欄位名為 `ExpiresAtUtc`、舊欄位不存在、值不變；再遷回前一個 migration，斷言 `Down` 還原為 `ExpiresAt` 且值不變。
  - 預期 RED：在 2.1 完成後，於容器內以 `dotnet ef migrations add RenameTokenExpiresAtToExpiresAtUtc ...` 產生 migration（此時 `Designer.cs` 與 snapshot 已反映 `ExpiresAtUtc`），然後把產生的 `Up`／`Down` 清空，讓測試能編譯並遷移成功；測試因「遷移後 `ExpiresAtUtc` 欄位不存在、`ExpiresAt` 仍存在」的欄位名斷言失敗（Expected／Actual 為欄位名清單），不是編譯或找不到 migration 的環境錯誤。3.2 再填入 `RenameColumn` 實作轉 GREEN。
  - RED 紀錄：
- [ ] 3.2 migration 檔已由 3.1 產生，不重複產生；在其 `Up`／`Down` 填入 `RenameColumn`（`RefreshTokens`、`PasswordResetTokens` 兩表）。EF 對同型別屬性改名可能產生 `RenameColumn`，也可能產生 drop + add，一律人工檢查並以手寫 `RenameColumn` 為準，`Down` 還原；確認 `Designer.cs` 與 `ApplicationDbContextModelSnapshot.cs` 反映新欄位名，並以 `dotnet ef migrations has-pending-model-changes` 確認沒有殘留差異。3.1 轉 GREEN。
  - GREEN 紀錄（含生成的 `Up`/`Down` 內容摘要）：

## 4. 文件

- [ ] 4.1 `docs/api-conventions.md` 第 3 節「時間欄位後綴」改為「`DateTime` 時間欄位一律以 `Utc` 結尾，由 `UtcSuffixNamingRuleTests` 強制」，刪除「不一致」描述；第 4 節如需列修改時機則補一條。TDD：N/A（純文件）。
- [ ] 4.2 `docs/api-conventions-todo.md`：第 4 項勾選，第 5 項標示「取消，併入第 4 項」。在 `docs/deployment-prerequisites.md` 加上此 migration 的停機部署注意事項（對應 design.md Migration Plan）。TDD：N/A（純文件）。
- [ ] 4.3 主 spec 同步：本 change 的 `specs/organizer-management/spec.md` MODIFIED delta 只把「會員可以切換目前操作中的 Organizer」Requirement 中 Scenario `ORG-SWITCH-007` 的 `ExpiresAt` 改為 `ExpiresAtUtc`，其餘原文照抄；archive 時由 sync 寫入主 spec。驗證（雙向）：archive 後 `rg -w "ExpiresAt" openspec/specs` 無結果，且 `rg -w "ExpiresAtUtc" openspec/specs` 恰好只命中 `organizer-management/spec.md` 的 `ORG-SWITCH-007` 一處。TDD：N/A（純文件，語意不變；`ORG-SWITCH-007` 的行為由既有測試涵蓋，5.1 重跑）。

## 5. 全套驗證與審查

- [ ] 5.1 全套測試（容器內）：Domain、Application、Infrastructure、WebApi、Seeder.Tests 的各專案摘要行逐字貼入下方紀錄；`web` 無變更，不需前端測試。
  - 全套紀錄：
- [ ] 5.2 hardener 最終檢查：N/A——僅改名與 migration，沒有新增 I/O、授權、狀態機或並發邏輯。
- [ ] 5.3 RED 還原驗證：N/A——不是 bug fix，也不是下單／排隊／庫存核心邏輯。
- [ ] 5.4 審查迴圈：`strict-reviewer` 與全新的對抗式 general-purpose subagent（只給 change 名稱與審查目標）依序執行，修到都沒有 blocking；需要時再由使用者另開 Opus 交叉審查。
  - 審查紀錄：
- [ ] 5.5 前端真實瀏覽器驗證：N/A——沒有前端變更；但實作後以登入、換發、忘記密碼三個流程各打一次 API 作 smoke test，並記錄結果。
- [ ] 5.6 實作完成後執行 `graphify update .`。
