## Why

專案目前僅依賴 ASP.NET Core 內建的 `ILogger`／Console provider：日誌只印在容器的標準輸出，沒有集中查詢、沒有結構化欄位搜尋能力，且跨服務（API 主進程與背景服務）的請求無法用同一個關聯值串起來追蹤。`docs/project-scope.md` 第 4 節已把「監控（Serilog + Seq，本地容器化）」列為 Phase 2 Should，並標示 `［待確認］`；Phase 2 其餘四項（銷售報表、Rate limiting／排隊、Email 通知、登入 Rate limiting）皆已完成合併，此為 Phase 2 最後一個未決項目。導入結構化日誌與集中查詢平台，補齊 CLAUDE.md 既有「錯誤一律結構化 log（含 correlation id）」規則目前缺乏查詢出口的落差。

## What Changes

- 新增 Serilog 作為 `ILogger` 的實作 provider，取代預設 Console provider；既有程式碼所有 `_logger.LogXxx(...)` 呼叫點（含既有結構化訊息樣板，例如 `{OrderId}`／`{EventId}`）不需修改即可被 Serilog 擷取為結構化欄位
- 新增請求層級的關聯值（correlation id）：擴充現有 `HttpContext.TraceIdentifier`（`GlobalExceptionHandler` 已使用此值回傳給前端），透過 Serilog enrichment 讓每一筆日誌自動帶上同一次請求的 TraceId，串起單次請求內跨 Handler／背景服務片段的日誌
- 新增 Serilog sinks：Console（維持容器 stdout 可見，供 `docker compose logs` 除錯）與 Seq（集中查詢與視覺化）
- docker-compose.yml 新增 `seq` 服務（`datalust/seq` image），對外提供查詢介面 port（比照既有服務用 `${SEQ_HOST_PORT:-<port>}` 可調整慣例避免衝突），`api` 服務新增對 `seq` 的連線設定與 `depends_on`
- 日誌等級與 Seq 連線位址透過 `appsettings.json`／compose `env_file` 注入，不寫死於程式碼

## Capabilities

### New Capabilities
- `observability`：結構化日誌輸出格式、請求關聯值（correlation id）串接規則、敏感資訊遮蔽要求、Seq 查詢平台的本機部署與連線方式

### Modified Capabilities
（無——本次不變更任何既有 capability 的行為需求，僅新增日誌基礎設施；既有各 Handler／背景服務的業務邏輯與對外行為不受影響）

## Impact

- **受影響程式碼**：`src/ProjectC.WebApi/Program.cs`（日誌 provider 註冊）、`src/ProjectC.WebApi/appsettings.json`（Serilog 設定節）、`docker-compose.yml`（新增 `seq` 服務）、`.env.example`（新增 Seq 連線相關變數說明）
- **不受影響**：所有既有 Handler／Controller／背景服務的業務邏輯程式碼不需修改（`ILogger<T>` 介面不變，僅底層 provider 替換）
- **新依賴套件**：`Serilog.AspNetCore`、`Serilog.Sinks.Seq`（NuGet，透過 `Directory.Packages.props` 集中管理版本）
- **部署面**：本機 Docker Compose 新增一個容器（`seq`），需確認對外 port 不與既有服務（`api` 8080、`db` 5432、`pgadmin` 5050、`web` 5173）衝突
