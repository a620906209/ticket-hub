## ADDED Requirements

### Requirement: 資料庫逾時與連線池上限可由設定控制
系統 SHALL 提供設定節 `Database`，含三項整數設定：`CommandTimeoutSeconds`（單一資料庫命令的執行逾時，預設 30）、`ConnectionTimeoutSeconds`（建立連線，以及連線池滿時等待可用連線的逾時，預設 15）、`MaxPoolSize`（單一 api 行程對資料庫的連線池上限，預設 100）。三項皆未設定時，系統對資料庫的逾時與連線池行為 MUST 與引入本設定前相同（預設值等於 Npgsql 既有預設）。設定的值 MUST 套用到所有經由應用程式 DbContext 的一般資料庫存取（`/health/ready` 的資料庫檢查除外：它以自己的獨立連線設定——2 秒連線逾時、不使用連線池——為準，不受本設定影響，見 `api-health-checks` 能力）：`CommandTimeoutSeconds` 套用為命令逾時，`ConnectionTimeoutSeconds` 與 `MaxPoolSize` 套用到連線字串的 `Timeout` 與 `Maximum Pool Size`。連線字串本身已含同名參數時，MUST 以 `Database` 設定的值為準（設定節為唯一的調整入口，避免同一參數兩處來源不一致）。

#### Scenario: DBL-001 未設定時採用等同現行行為的預設值
- **WHEN** 設定中沒有 `Database` 節
- **THEN** 系統啟動成功，命令逾時為 30 秒、連線逾時為 15 秒、連線池上限為 100

#### Scenario: DBL-002 設定值確實套用到 DbContext
- **WHEN** 設定 `Database:CommandTimeoutSeconds = 7`、`Database:ConnectionTimeoutSeconds = 5`、`Database:MaxPoolSize = 20`
- **THEN** 以應用程式 DbContext 取得的連線，其連線字串的 `Timeout` 為 5、`Maximum Pool Size` 為 20，且 DbContext 的命令逾時為 7 秒

#### Scenario: DBL-003 設定值優先於連線字串中的同名參數
- **WHEN** `ConnectionStrings:DefaultConnection` 已含 `Maximum Pool Size=500;Timeout=60`，且設定 `Database:MaxPoolSize = 20`、`Database:ConnectionTimeoutSeconds = 5`
- **THEN** 實際使用的連線字串其 `Maximum Pool Size` 為 20、`Timeout` 為 5

#### Scenario: DBL-004 不影響 readiness 檢查自己的連線設定
- **WHEN** 設定 `Database:ConnectionTimeoutSeconds = 30` 與 `Database:MaxPoolSize = 20`
- **THEN** `/health/ready` 的資料庫檢查仍使用 2 秒連線逾時且不使用連線池（`api-health-checks` 既有行為不變）

### Requirement: 資料庫設定於啟動時驗證
`Database` 設定節的三項值 MUST 在應用程式啟動階段驗證，不得延遲到第一個資料庫請求：`CommandTimeoutSeconds` MUST 介於 1 至 3600、`ConnectionTimeoutSeconds` MUST 介於 1 至 300、`MaxPoolSize` MUST 介於 1 至 1000（皆含邊界）；不在範圍內的值（含 0、負數；其中 0 在 Npgsql 代表無限等待，正是本機制要避免的狀況）MUST 使應用程式啟動失敗。失敗訊息 MUST 指出違反的設定項名稱與規則，且 MUST NOT 包含連線字串或其中的密碼。

#### Scenario: DBL-005 範圍外的值使啟動失敗
- **WHEN** 分別以 `Database:CommandTimeoutSeconds` 為 0、3601，`Database:ConnectionTimeoutSeconds` 為 0、301，`Database:MaxPoolSize` 為 0、1001 啟動應用程式
- **THEN** 每一種設定都使主機建立失敗並拋出選項驗證例外，訊息含對應的設定項名稱

#### Scenario: DBL-006 邊界值可正常啟動
- **WHEN** 以 `CommandTimeoutSeconds` 為 1 與 3600、`ConnectionTimeoutSeconds` 為 1 與 300、`MaxPoolSize` 為 1 與 1000 啟動應用程式
- **THEN** 主機建立成功

#### Scenario: DBL-007 驗證失敗訊息不含連線字串
- **WHEN** `Database:MaxPoolSize = 0` 且 `ConnectionStrings:DefaultConnection` 含密碼 `Password=SECRET-SENTINEL`
- **THEN** 啟動失敗的例外文字（含內層例外）不含 `SECRET-SENTINEL`

### Requirement: 資料庫命令與連線逾時確實生效
系統 MUST 在 `CommandTimeoutSeconds` 到期時中止仍在執行的資料庫命令並使該次存取失敗，不得無限等待；連線池已達 `MaxPoolSize` 且無可用連線時，等待可用連線的時間 MUST 不超過 `ConnectionTimeoutSeconds`，逾時後該次存取失敗。逾時失敗經由 HTTP 請求觸發時的回應規格見 `api-error-responses` 能力「資料庫逾時回應 503 ServiceUnavailable」。本能力不包含逾時後自動重試（重試另案處理）。

#### Scenario: DBL-008 命令超過逾時即失敗
- **WHEN** `Database:CommandTimeoutSeconds = 2`，對資料庫執行一個需要 6 秒才會完成的命令（`pg_sleep(6)`）
- **THEN** 該命令在約 2 秒（容許排程誤差，不超過 5 秒）內失敗，且失敗的例外鏈含逾時例外；連線池未因此損壞，之後的簡單查詢可成功

#### Scenario: DBL-009 連線池耗盡時等待受連線逾時限制
- **WHEN** `Database:MaxPoolSize = 1`、`Database:ConnectionTimeoutSeconds = 2`，一條連線被長時間占用，另一個存取要求取得連線
- **THEN** 後者在約 2 秒（不超過 5 秒）內失敗，不無限等待；占用者釋放連線後，新的存取可成功取得連線
