# Design

## Context

- 盤點結果見 `docs/api-conventions-todo.md`「查證結果」0-C；已再次以 `rg` 確認 `src/`（排除 Migrations）沒有 `DateOnly` / `TimeOnly` 型別的欄位，`DateTimeOffset` 只出現在方法簽章與 Redis 毫秒轉換內，沒有屬性。`DateTime` 參數 `now` 是方法參數，不是欄位。
- `ExpiresAt` 兩個欄位的引用範圍：Domain Entity、`Issue` 方法、兩個 EF Configuration（`IsRequired()`，未指定欄位名）、三個 handler 的區域變數 `expiresAt`；`tests/` 與 `web/src` 沒有引用，也沒有 raw SQL 引用。
- migration 由開發者手動執行（`docker compose exec api dotnet ef database update ...`，見 README），不是啟動時自動套用。
- 既有 migration 資料保留測試的做法：`OrderCreatedAtBackfillMigrationTests` 用 `IMigrator.MigrateAsync(targetMigration)` 先遷到舊版、植入資料、再遷到新版。

## Goals / Non-Goals

- Goals：消除最後兩個缺後綴的 `DateTime` 屬性；讓規則可機械檢查，新增欄位違規時測試立刻失敗。
- Non-Goals：不改欄位語意、不改 JSON、不改 `DateTimeOffset` 的使用、不處理 `Order` 以外的命名議題。

## Decisions

1. **規則範圍：公開屬性（含 record 位置參數），不含方法參數。** 方法參數如 `now`、`nowUtc` 是呼叫端傳入的時間點，不是持久化或對外欄位；規則測試只掃屬性。`Issue` 的參數 `expiresAt` 為了與屬性一致仍一併改名，但不由規則測試強制。
2. **反射式規則測試放在 `ProjectC.WebApi.Tests`。** 它 reference 全部專案，可一次掃 Domain、Application、WebApi 三個組件；Infrastructure 的型別只做持久化實作，不含對外欄位，不掃。例外清單初始只有 `IDateTimeProvider.UtcNow`（.NET 慣用名，名稱已標明 UTC，改名牽動全專案），以「型別全名＋屬性名」精確比對；新增例外須在測試裡寫明理由。掃描範圍為 public 型別（含介面與巢狀 public 型別）以 `DeclaredOnly` 取得的 public 實例屬性，nonpublic 型別（例如 `OrderService` 內 private record 的 `Now`）不掃。
3. **DB 用 `RenameColumn`，不用 drop 再 add。** 保留既有 Token 資料（使用者不必重新登入）；PostgreSQL 的改名只動 catalog，不重寫資料表。`Down` 改回舊名。EF 預設可能把 Rename 偵測成 drop + add，產生 migration 後必須人工檢查，必要時手動改寫成 `RenameColumn`。
4. **不採 expand/contract（先加新欄位、雙寫、再刪舊欄位）。** 專案為單實例、migration 手動執行，雙寫的複雜度與風險大於短暫停機；見 Migration Plan。
5. **RED 證據用反射，不用編譯失敗。** CLAUDE.md 禁止把編譯失敗當 RED，因此屬性名稱測試以 `GetProperty("ExpiresAtUtc")` 取得後斷言非 null，EF 對應以 `Model.FindEntityType(...).FindProperty("ExpiresAtUtc")` 斷言，兩者在修改前會以 Expected／Actual 明確失敗。
6. **hardener 與 RED 還原驗證不適用。** 變更內容是改名與 migration，沒有新增 I/O 或授權邏輯；也不是 bug fix 或下單／排隊／庫存邏輯。見 tasks.md 對應項目的 N/A 說明。

## Risks / Trade-offs

- [改名 migration 與應用程式版本互相不相容] → 見 Migration Plan；單實例短暫停機可接受。
- [EF 產生 drop + add 造成資料遺失] → 產生後人工檢查 `Up`/`Down`，並由 migration 資料保留測試驗證植入的列在遷移後仍存在且值不變。
- [反射規則測試誤抓合理的非 Utc 欄位] → 規則只掃 `DateTime` 屬性；現況盤點（`rg` 掃 `src/` 排除 Migrations）已確認改完後除初始例外 `UtcNow` 外零違規，之後出現違規時由開發者決定改名或加註明理由的例外。

## Migration Plan

1. 先停止（或不接受流量的）舊版 api，因為舊版程式碼讀 `ExpiresAt`，遷移後會查詢失敗；新版程式碼讀 `ExpiresAtUtc`，遷移前會查詢失敗。
2. 執行 `dotnet ef database update`（容器內）。
3. 啟動新版 api。
4. 回滾：停止新版、`dotnet ef database update <前一個 migration>`、啟動舊版。
5. 影響面：登入、換發 Token、忘記密碼三個流程在停機期間不可用，不影響下單、查詢等不碰這兩張表的流程。部署前須在 `docs/deployment-prerequisites.md` 註記（tasks 4.2）。

## 主 spec 同步

`organizer-management` 主 spec 的 `ORG-SWITCH-007` 引用 `ExpiresAt`，因此本 change 以 MODIFIED delta 同步欄位名（語意不變），不走 `skip_specs`。審查歷程：spec-reviewer 兩輪未發現，Opus 交叉審查以全文 `rg openspec/specs` 找出。
