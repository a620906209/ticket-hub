# Proposal

## Why

`PersistenceServiceCollectionExtensions.AddPersistence` 以 `UseNpgsql(connectionString)` 註冊 DbContext，沒有任何命令逾時、連線逾時或連線池上限的明確設定，全部靠 Npgsql 預設值，且預設值無法依環境調整（`docs/review/TODO.md` ⑧／M-1）。後果有二：

1. DB 卡住或慢查詢時，請求等待多久、失敗時回什麼，完全不由專案控制；逾時發生時 `GlobalExceptionHandler` 只會回 500 `InternalServerError`（已讀 `GlobalExceptionHandler.cs`：除 `BadHttpRequestException` 外一律 500），無法與「程式 bug」區分。
2. k8s 以 2 個 api 副本運行（`k8s/40-api.yaml`，`RollingUpdate maxSurge: 1`，滾動更新期間最多 3 個 pod），Npgsql 預設連線池上限 100，PostgreSQL 預設 `max_connections` 100（`k8s/20-postgres.yaml`、`docker-compose.yml` 皆未覆寫）。各 pod 連線池上限加總遠超過 PostgreSQL 容量，連線耗盡時 DB 端以 `too many clients` 拒絕連線；compose 單副本 100 = 100 的情況已在 k6 報告觀察到（`docs/load-test/p95-optimization-report.md`）。

範圍刻意只含「逾時與池上限」：`EnableRetryOnFailure` 需把所有手動交易（多處，實作前須逐一盤點）改包 `CreateExecutionStrategy().ExecuteAsync`，且每處的重放安全須逐一審視（`IUnitOfWork` 註解已載明），是獨立的大型跨切面重構，另開 `db-retry-execution-strategy`，不在本 change。

## What Changes

- 新增 `DatabaseOptions`（設定節 `Database`）：`CommandTimeoutSeconds`（預設 30）、`ConnectionTimeoutSeconds`（預設 15）、`MaxPoolSize`（預設 100）。預設值等於 Npgsql 現行預設，**未設定時行為不變**。範圍外的值（0、負數、超過上限）啟動即失敗，錯誤訊息只含設定項名稱與規則。
- `AddPersistence` 改以 `(sp, options)` 多載註冊：`CommandTimeout` 以 `UseNpgsql(o => o.CommandTimeout(n))` 套用；`Timeout`、`Maximum Pool Size` 以 `NpgsqlConnectionStringBuilder` 套用到連線字串。
- `GlobalExceptionHandler` 新增資料庫逾時分支：命令逾時、取得連線逾時回 **503**，`Title = "ServiceUnavailable"`，含 `traceId`、不含 `Detail`；日誌 Warning、不含 SQL 或連線字串。其餘未預期例外維持 500。
- `k8s/10-configmap.yaml` 新增 `Database__MaxPoolSize`，使「(副本數 + maxSurge) × MaxPoolSize + 預留 ≤ `max_connections`」成立；`docs/todo/2026-11-02-deployment-prerequisites.md` 與 `docs/api-conventions.md` 記錄公式與新錯誤碼。
- `docs/review/TODO.md` ⑧／M-1 改為實際範圍，註明 retry 已拆出為 `db-retry-execution-strategy`。

不改：`EnableRetryOnFailure`、任何交易邊界、`OrderService`、`UnitOfWork`、health check 的獨立連線設定（`Timeout = 2`、`Pooling = false` 維持）、compose 的連線池設定（維持與 k6 基準相同）。k6 複測不納入驗收，僅在報告列為建議。

## Capabilities

### New Capabilities
- `database-connection-limits`: 資料庫命令逾時、連線逾時與連線池上限的可設定性、啟動時驗證，以及逾時確實生效。

### Modified Capabilities
- `api-error-responses`: 新增「資料庫逾時回 503 `ServiceUnavailable`」的框架層錯誤對照（以 ADDED Requirement 補充，不改寫既有 Requirement；理由見 design.md 決策 4）。

## Impact

- 程式碼：`src/ProjectC.Application/Common/DatabaseOptions.cs`（新）、`src/ProjectC.WebApi/Extensions/PersistenceServiceCollectionExtensions.cs`、`EagerOptionsValidationExtensions.cs`、`ExceptionHandling/GlobalExceptionHandler.cs`（含新的逾時判定輔助）、`Program.cs` 的註冊呼叫（若簽章改變）。
- 測試：`tests/ProjectC.WebApi.Tests/Startup/`（Options fail-fast）、`ExceptionHandling/`、`tests/ProjectC.Infrastructure.Tests/`（`pg_sleep` 逾時、連線池耗盡）；可能更新 `ServiceRegistrationSnapshot.baseline.txt`。
- 設定與文件：`k8s/10-configmap.yaml`、`docs/api-conventions.md`、`docs/todo/2026-11-02-deployment-prerequisites.md`、`docs/review/TODO.md`。
- 相依：無新增套件、無 migration、無 API 路由變更；對外行為變化僅「DB 逾時由 500 改為 503」。
- 規模：2 個 capability、單一關注點，預估 S 級，不需拆分。
