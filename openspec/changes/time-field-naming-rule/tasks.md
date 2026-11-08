# Tasks

> 對應的 spec：`organizer-management` 的 MODIFIED delta（只把 Scenario `ORG-SWITCH-007` 的 `ExpiresAt` 改為 `ExpiresAtUtc`，語意不變，由任務 4.3 處理）；其餘為純內部命名一致化，驗證依據為 design.md 的決策與下列測試。

## 1. 反射式規則測試與屬性名稱測試（TDD）

- [x] 1.1 在 `tests/ProjectC.WebApi.Tests/Common/` 新增 `UtcSuffixNamingRuleTests`：掃 Domain、Application、WebApi 組件中 public 型別（含介面與巢狀 public 型別，`IsVisible` 為 true；nonpublic 型別不掃）以 `DeclaredOnly` 取得的 public 實例屬性，找出型別為 `DateTime` / `DateTime?` 者，斷言名稱以 `Utc` 結尾。例外清單初始只有 `IDateTimeProvider.UtcNow`，並在測試中以註解寫明理由（.NET 慣用名 `UtcNow`，名稱已標明 UTC，改名牽動全專案）；例外須以「型別全名＋屬性名」精確比對，不得用萬用字元。測試命名 `DateTimeProperties_AcrossAssemblies_EndWithUtc`。
  - 預期 RED：失敗訊息恰好列出 `RefreshToken.ExpiresAt`、`PasswordResetToken.ExpiresAt` 兩個違規者（`IDateTimeProvider.UtcNow` 已在例外清單，不得出現）；掃描結果須先斷言「至少掃到 N 個 `DateTime` 屬性」（N 取 RED 時實測的掃描數，並記入證據），避免掃描範圍錯誤而空過。
  - 並新增 `RefreshToken` / `PasswordResetToken` 的 Domain 測試：`typeof(...).GetProperty("ExpiresAtUtc")` 非 null 且 `GetProperty("ExpiresAt")` 為 null（反射，非編譯依賴）。
  - 並新增 Infrastructure 測試：`Model.FindEntityType(typeof(RefreshToken)).FindProperty("ExpiresAtUtc")` 非 null，PasswordResetToken 同。
  - RED 紀錄（命令、失敗測試名、實際 Expected／Actual 原文、需在修改正式程式碼前執行）：此時正式程式碼尚未修改（git status 僅有三個新增測試檔）。掃描數實測為 32 個 `DateTime` 屬性（先把下限設為 100000 取得 `Expected dateTimeProperties.Count to be greater than or equal to 100000, but found 32`，下限因此定為 32）。
    - `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~UtcSuffixNamingRuleTests"`
      ```
      Failed ProjectC.WebApi.Tests.Common.UtcSuffixNamingRuleTests.DateTimeProperties_AcrossAssemblies_EndWithUtc [43 ms]
      Expected violations to be empty because 以下 DateTime 屬性未以 Utc 結尾：ProjectC.Domain.Authentication.PasswordResetToken.ExpiresAt, ProjectC.Domain.Authentication.RefreshToken.ExpiresAt, but found at least one item {"ProjectC.Domain.Authentication.PasswordResetToken.ExpiresAt"}.
      Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 50 ms - ProjectC.WebApi.Tests.dll (net10.0)
      ```
    - `docker compose exec -T api dotnet test tests/ProjectC.Domain.Tests --filter "FullyQualifiedName~TokenExpiryPropertyNameTests"`
      ```
      Failed ...IsNamedExpiresAtUtc(tokenType: typeof(ProjectC.Domain.Authentication.RefreshToken)) [30 ms]
      Expected tokenType.GetProperty("ExpiresAtUtc") not to be <null>.
      Failed ...IsNamedExpiresAtUtc(tokenType: typeof(ProjectC.Domain.Authentication.PasswordResetToken)) [3 ms]
      Expected tokenType.GetProperty("ExpiresAtUtc") not to be <null>.
      Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 49 ms - ProjectC.Domain.Tests.dll (net10.0)
      ```
    - `docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~TokenExpiryColumnMappingTests"`
      ```
      Failed ...Model_TokenEntity_MapsExpiryToExpiresAtUtc(tokenType: typeof(ProjectC.Domain.Authentication.PasswordResetToken)) [633 ms]
      Expected entityType!.FindProperty("ExpiresAtUtc") not to be <null>.
      Failed ...Model_TokenEntity_MapsExpiryToExpiresAtUtc(tokenType: typeof(ProjectC.Domain.Authentication.RefreshToken)) [17 ms]
      Expected entityType!.FindProperty("ExpiresAtUtc") not to be <null>.
      Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 657 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
    - 備註：FluentAssertions 的 `BeEmpty` 只列第一個元素，因此在 `because` 帶入完整違規清單，兩個違規者（`PasswordResetToken.ExpiresAt`、`RefreshToken.ExpiresAt`）皆列出，`IDateTimeProvider.UtcNow` 未出現。
  - GREEN 紀錄：見 2.1（改名後同三組測試重跑通過）。
  - 重構後重跑：N/A（改名無重構步驟）。

## 2. 改名實作

- [x] 2.1 `RefreshToken`、`PasswordResetToken` 屬性改為 `ExpiresAtUtc`，`Issue` 參數改為 `expiresAtUtc`；兩個 EF Configuration、`LoginHandler`、`RefreshTokenHandler`、`RequestPasswordResetHandler` 同步。建置通過，1.1 的三組測試轉為 GREEN。3.2 完成前，所有以 Testcontainers 套用 migration 的整合測試（登入、換發、忘記密碼等）會因 DB 欄位仍是 `ExpiresAt` 而失敗，這是預期現象，不計入 2.1 的 GREEN 判定；全套測試於 5.1 驗證。
  - GREEN 紀錄（命令與摘要行原文）：改名範圍 `rg -w ExpiresAt src --glob '!**/Migrations/**'` 的 7 個檔案（兩個 Entity、兩個 Configuration、三個 handler）。
    - `... --filter "FullyQualifiedName~UtcSuffixNamingRuleTests"`：`Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 21 ms - ProjectC.WebApi.Tests.dll (net10.0)`
    - `... --filter "FullyQualifiedName~TokenExpiryPropertyNameTests"`：`Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 19 ms - ProjectC.Domain.Tests.dll (net10.0)`
    - `... --filter "FullyQualifiedName~TokenExpiryColumnMappingTests"`：`Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 495 ms - ProjectC.Infrastructure.Tests.dll (net10.0)`

## 3. Migration 與資料保留測試（TDD）

- [x] 3.1 在 `tests/ProjectC.Infrastructure.Tests/Authentication/` 新增 `ExpiresAtUtcRenameMigrationTests`（比照 `OrderCreatedAtBackfillMigrationTests`，Testcontainers）：先 `MigrateAsync` 到前一個 migration（`20261014081305_AddOrderCreatedAt`），先建立 `Member`（`MemberId` 為必填外鍵），再以 raw SQL 植入一筆 `RefreshTokens` 與一筆 `PasswordResetTokens`（含 `ExpiresAt` 值），再遷到新 migration，斷言欄位名為 `ExpiresAtUtc`、舊欄位不存在、值不變；再遷回前一個 migration，斷言 `Down` 還原為 `ExpiresAt` 且值不變。
  - 預期 RED：在 2.1 完成後，於容器內以 `dotnet ef migrations add RenameTokenExpiresAtToExpiresAtUtc ...` 產生 migration（此時 `Designer.cs` 與 snapshot 已反映 `ExpiresAtUtc`），然後把產生的 `Up`／`Down` 清空，讓測試能編譯並遷移成功；測試因「遷移後 `ExpiresAtUtc` 欄位不存在、`ExpiresAt` 仍存在」的欄位名斷言失敗（Expected／Actual 為欄位名清單），不是編譯或找不到 migration 的環境錯誤。3.2 再填入 `RenameColumn` 實作轉 GREEN。
  - RED 紀錄：容器內 `dotnet ef migrations add RenameTokenExpiresAtToExpiresAtUtc --project src/ProjectC.Infrastructure --startup-project src/ProjectC.WebApi`（`HostAbortedException` 為 EF 工具正常結束訊息）產生 `20261108081752_RenameTokenExpiresAtToExpiresAtUtc`，EF 已自行產生 `RenameColumn`；為取得 RED 先把 `Up`／`Down` 清空（暫存完整版後還原）：
    - `docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~ExpiresAtUtcRenameMigrationTests"`
      ```
      Failed ...Up_WhenTokensExist_RenamesColumnAndKeepsValue(tableName: "PasswordResetTokens") [511 ms]
      Expected (ReadExpiryColumnNamesAsync(dbContext, tableName)) to be equal to {"ExpiresAtUtc"}, but {"ExpiresAt"} differs at index 0.
      Failed ...Up_WhenTokensExist_RenamesColumnAndKeepsValue(tableName: "RefreshTokens") [257 ms]
      Expected (ReadExpiryColumnNamesAsync(dbContext, tableName)) to be equal to {"ExpiresAtUtc"}, but {"ExpiresAt"} differs at index 0.
      Failed!  - Failed:     2, Passed:     2, Skipped:     0, Total:     4, Duration: 1 s - ProjectC.Infrastructure.Tests.dll (net10.0)
      ```
    - 空 migration 時兩個 `Down_...` 案例會通過：清空後 Up／Down 皆為 no-op，欄位名維持 `ExpiresAt`，恰好等於 Down 的預期，這是已知現象；Down 的行為由 3.2 填入後 GREEN 驗證，其斷言本身會在欄位名或值錯誤時失敗。
- [x] 3.2 migration 檔已由 3.1 產生，不重複產生；在其 `Up`／`Down` 填入 `RenameColumn`（`RefreshTokens`、`PasswordResetTokens` 兩表）。EF 對同型別屬性改名可能產生 `RenameColumn`，也可能產生 drop + add，一律人工檢查並以手寫 `RenameColumn` 為準，`Down` 還原；確認 `Designer.cs` 與 `ApplicationDbContextModelSnapshot.cs` 反映新欄位名，並以 `dotnet ef migrations has-pending-model-changes` 確認沒有殘留差異。3.1 轉 GREEN。
  - GREEN 紀錄（含生成的 `Up`/`Down` 內容摘要）：還原 EF 產生的完整版，`Up` 對 `RefreshTokens`、`PasswordResetTokens` 各 `RenameColumn(ExpiresAt → ExpiresAtUtc)`，`Down` 反向各 `RenameColumn(ExpiresAtUtc → ExpiresAt)`（EF 直接產生 rename，非 drop + add，無需手改）。
    - `... --filter "FullyQualifiedName~ExpiresAtUtcRenameMigrationTests"`：`Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 1 s - ProjectC.Infrastructure.Tests.dll (net10.0)`
    - `docker compose exec -T api dotnet ef migrations has-pending-model-changes --project src/ProjectC.Infrastructure --startup-project src/ProjectC.WebApi`：`No changes have been made to the model since the last migration.`
    - snapshot：`rg ExpiresAt ApplicationDbContextModelSnapshot.cs` 為兩處 `ExpiresAtUtc`（PasswordResetToken、RefreshToken）加既有的 `AdmissionExpiresAtUtc`，無舊名。

## 4. 文件

- [x] 4.1 `docs/api-conventions.md` 第 3 節「時間欄位後綴」改為「`DateTime` 時間欄位一律以 `Utc` 結尾，由 `UtcSuffixNamingRuleTests` 強制」，刪除「不一致」描述；第 4 節如需列修改時機則補一條。TDD：N/A（純文件）。
- [x] 4.2 `docs/api-conventions-todo.md`：第 4 項勾選，第 5 項標示「取消，併入第 4 項」。在 `docs/deployment-prerequisites.md` 加上此 migration 的停機部署注意事項（對應 design.md Migration Plan）。TDD：N/A（純文件）。
- [ ] 4.3 主 spec 同步：本 change 的 `specs/organizer-management/spec.md` MODIFIED delta 只把「會員可以切換目前操作中的 Organizer」Requirement 中 Scenario `ORG-SWITCH-007` 的 `ExpiresAt` 改為 `ExpiresAtUtc`，其餘原文照抄；archive 時由 sync 寫入主 spec。驗證（雙向）：archive 後 `rg -w "ExpiresAt" openspec/specs` 無結果，且 `rg -w "ExpiresAtUtc" openspec/specs` 恰好只命中 `organizer-management/spec.md` 的 `ORG-SWITCH-007` 一處。TDD：N/A（純文件，語意不變；`ORG-SWITCH-007` 的行為由既有測試涵蓋，5.1 重跑）。

## 5. 全套驗證與審查

- [x] 5.1 全套測試（容器內）：Domain、Application、Infrastructure、WebApi、Seeder.Tests 的各專案摘要行逐字貼入下方紀錄；`web` 無變更，不需前端測試。
  - 全套紀錄：`docker compose exec -T api dotnet test tests/ProjectC.<專案>.Tests`（五個專案各跑一次，未過濾，無 Skipped）：
    ```
    Passed!  - Failed:     0, Passed:   153, Skipped:     0, Total:   153, Duration: 67 ms - ProjectC.Domain.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   566, Skipped:     0, Total:   566, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   266, Skipped:     0, Total:   266, Duration: 45 s - ProjectC.Infrastructure.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   726, Skipped:     0, Total:   726, Duration: 3 m 12 s - ProjectC.WebApi.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 7 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
    ```
- [x] 5.2 hardener 最終檢查：N/A——僅改名與 migration，沒有新增 I/O、授權、狀態機或並發邏輯。
- [x] 5.3 RED 還原驗證：N/A——不是 bug fix，也不是下單／排隊／庫存核心邏輯。
- [x] 5.4 審查迴圈：`strict-reviewer` 與全新的對抗式 general-purpose subagent（只給 change 名稱與審查目標）依序執行，修到都沒有 blocking；需要時再由使用者另開 Opus 交叉審查。
  - 審查紀錄（2026-11-08，未 commit 的工作樹，無程式碼於審查後再改動）：
    - 第 1 輪 `strict-reviewer`：PASS，無 blocking；容器內重跑五個專案全數通過（Domain 153、Application 566、Infrastructure 266、WebApi 726、Seeder 38，Failed 0／Skipped 0）。
    - 第 1 輪對抗式 general-purpose subagent（只給 change 名稱與審查目標；靜態核對，未跑容器測試）：無 blocking。建議項：(1) 規則只比對 `DateTime`／`DateTime?`，日後新增公開 `DateTimeOffset` 屬性不會被抓到——現況 Domain、Application、WebApi 無此類屬性，且 proposal 明列 `DateTimeOffset` 為非變更範圍，故不處理；(2) 規則不含 Infrastructure 組件，design 決策 2 已說明；(3) 4.3 待 archive 後做雙向 `rg`；(4) 開發環境 `dotnet watch` 不會熱重載此改名，已記於 5.5。
- [x] 5.5 前端真實瀏覽器驗證：N/A——沒有前端變更；但實作後以登入、換發、忘記密碼三個流程各打一次 API 作 smoke test，並記錄結果。
  - 紀錄（2026-11-08）：容器內 `dotnet ef database update` 套用 `20261108081752_RenameTokenExpiresAtToExpiresAtUtc`（`Done.`）。套用後 api 容器（`dotnet watch`）仍跑舊程式碼，登入回 500，日誌為 `42703: column r.ExpiresAt does not exist`；`docker compose restart api` 後重測（captcha 答案以 SHA256 雜湊直接寫入 Redis 取得）：
    - `POST /api/auth/register` → 201
    - `POST /api/auth/login` → 200
    - `POST /api/auth/refresh` → 200
    - `POST /api/auth/password-reset/request` → 200
    - DB 核對：該會員 `RefreshTokens` 2 筆（登入＋換發）、`PasswordResetTokens` 1 筆，`ExpiresAtUtc` 皆有值（refresh 約 +14 天，reset 約 +15 分鐘）。
    - 部署含意：遷移後必須重啟 api，與 design.md Migration Plan 的「停止舊版 → 遷移 → 啟動新版」一致。
- [x] 5.6 實作完成後執行 `graphify update .`。
