# Proposal

## Why

`docs/api-conventions.md` 第 3 節記載「時間欄位後綴不一致」。盤點（`docs/api-conventions-todo.md` 0-C）確認 Domain、Application、WebApi 的 `DateTime` 屬性中，只有 `RefreshToken.ExpiresAt` 與 `PasswordResetToken.ExpiresAt` 缺 `Utc` 後綴，其餘全部已有（唯一例外是 `IDateTimeProvider.UtcNow`，名稱已以 `Utc` 開頭，沿用 .NET 慣用名 `UtcNow`，列為規則的初始例外）。這兩個欄位沒有對外暴露於 JSON，現在改名成本最低；放著不處理，規則就只能寫成「一律 Utc，但有兩個例外」，日後新增欄位時缺乏可機械檢查的依據。

## What Changes

- `RefreshToken.ExpiresAt`、`PasswordResetToken.ExpiresAt` 改名為 `ExpiresAtUtc`（Entity 屬性、`Issue` 的參數、兩個 EF Configuration，以及 `LoginHandler`、`RefreshTokenHandler`、`RequestPasswordResetHandler` 的區域變數）。
- 新增 EF migration，以 `RenameColumn` 把 `RefreshTokens.ExpiresAt`、`PasswordResetTokens.ExpiresAt` 改名為 `ExpiresAtUtc`，保留既有資料，`Down` 可還原。
- 新增反射式規則測試：Domain、Application、WebApi 三個組件中，所有型別為 `DateTime` / `DateTime?` 的公開屬性（含 record 的位置參數）名稱必須以 `Utc` 結尾；初始例外清單只有 `IDateTimeProvider.UtcNow`（附理由）。掃描範圍僅含 public 型別（含介面、巢狀 public 型別）宣告的 public 實例屬性，nonpublic 型別（例如 `OrderService` 內 private record）不掃。
- `docs/api-conventions.md` 第 3 節「時間欄位後綴」改為「`DateTime` 時間欄位一律以 `Utc` 結尾」，刪除「不一致」描述；`docs/api-conventions-todo.md` 第 4 項勾選、第 5 項標示取消（併入本項）。
- **非變更**：不動任何 JSON 回應、不動 Redis 或其他欄位、不處理 `DateTimeOffset` / `DateOnly`（現有程式碼沒有此型別的欄位，只有方法簽章與內部轉換用到）、不做分頁（另案）。

## Capabilities

### New Capabilities
無。

### Modified Capabilities
- `organizer-management`: 「會員可以切換目前操作中的 Organizer」Requirement 的 Scenario `ORG-SWITCH-007` 以欄位名 `ExpiresAt` 描述 Refresh Token 資料列不變，改為 `ExpiresAtUtc`。語意不變，只同步欄位名；其餘 capability 沒有引用此欄位（已 `rg` 核對 `openspec/specs`）。

## Impact

- 程式碼：`src/ProjectC.Domain/Authentication/{RefreshToken,PasswordResetToken}.cs`、`src/ProjectC.Infrastructure/Persistence/Configurations/{RefreshToken,PasswordResetToken}Configuration.cs`、三個 Application handler、新 migration 與 `ApplicationDbContextModelSnapshot.cs`。
- 測試：新增反射式規則測試、migration 資料保留測試、Entity 屬性名稱測試。
- 文件：`docs/api-conventions.md`、`docs/api-conventions-todo.md`。
- 部署：此 migration 是**破壞性重新命名**，不像先前加欄位的 migration 可新舊版共存；見 design.md 的 Migration Plan。
- Change 顆粒度：0 個 capability、tasks 約 12 項，不需拆分。
