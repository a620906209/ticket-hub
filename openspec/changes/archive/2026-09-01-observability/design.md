## Context

專案目前唯一的日誌輸出是 ASP.NET Core 預設 `ILogger` + Console provider（容器 stdout），沒有集中查詢介面；既有程式碼（`OrderService`、`ExpiredOrderCleanupService`、`PurchaseQueueAdmissionService`、`MockEmailNotificationService`、`GlobalExceptionHandler` 等）已經在用 `ILogger<T>` 搭配結構化訊息樣板（例如 `_logger.LogError(exception, "...{OrderId}...", orderId)`），只是輸出目的地單一、無法跨請求關聯查詢。`GlobalExceptionHandler` 已把 `HttpContext.TraceIdentifier` 當作對外回傳的關聯識別碼（`ProblemDetails.Extensions["traceId"]`），但目前只有這一處使用，其餘日誌沒有帶上同一個值。

本機開發環境固定透過 Docker Compose 啟動（`api`／`db`／`web`／`pgadmin` 四個既有服務），新服務需遵循同一套慣例（compose service name 連線、可調 host port、`.env` 注入敏感設定）。

## Goals / Non-Goals

**Goals:**
- 把 `ILogger` 的底層 provider 換成 Serilog，讓既有所有呼叫點不需修改就能輸出結構化欄位
- 新增 Seq 作為集中查詢/視覺化平台，本機 Docker Compose 一鍵啟動
- 讓同一次 HTTP 請求內的所有日誌自動帶上同一個關聯值（沿用既有 `HttpContext.TraceIdentifier`），背景服務的每次執行週期也帶上各自獨立的關聯值
- 日誌等級與 Seq 連線位址透過設定檔／環境變數注入，不寫死

**Non-Goals:**
- 不導入分散式追蹤（OpenTelemetry trace/span、跨服務 trace）——本專案僅單一 API 服務，沒有需要串接的下游微服務
- 不導入 Metrics/APM（CPU、記憶體、QPS 儀表板）——`docs/project-scope.md` 監控項目僅指日誌集中查詢，Metrics 不在本次範疇
- 不處理 Seq 資料的長期保留/清理策略——本機展示用途，沿用 Seq 預設值即可，非正式上線環境
- 不新增 Seq 帳號密碼驗證——比照既有 `pgadmin` 服務的本機展示慣例（僅供本機開發存取），非公開部署

## Decisions

### 1. Serilog 設定方式：`appsettings.json` 驅動，而非純程式碼組態
用 `Serilog.Settings.Configuration` 套件的 `ReadFrom.Configuration(context.Configuration)`，把 sink、最低等級、per-namespace override 寫在 `appsettings.json` 的 `Serilog` 節點，而非在 `Program.cs` 用程式碼硬寫 `LoggerConfiguration`。
**理由**：與既有 `Jwt`／`RateLimiting`／`TicketSigning` 等設定節一致，都是「設定注入、不寫死」；本機/未來若要切換環境（例如關閉 Seq sink）不需重新編譯。
**替代方案（未採用）**：純程式碼 `LoggerConfiguration()...CreateLogger()`——更精簡但等級調整需改程式碼重新部署，與專案既有慣例不一致。

### 2. 關聯值（correlation id）沿用 `HttpContext.TraceIdentifier`，不引入額外套件
新增一個輕量 middleware，在請求進入時用 Serilog 的 `LogContext.PushProperty("TraceId", httpContext.TraceIdentifier)` 把值推進當前請求的 log scope，該請求內所有後續日誌（含 Handler、Repository 拋出的例外）自動帶上這個屬性，直到請求結束彈出。
**理由**：`TraceIdentifier` 是 ASP.NET Core 內建、每次請求唯一，`GlobalExceptionHandler` 已經用它作為回傳給前端的關聯識別碼——沿用同一個值，前端錯誤畫面顯示的 traceId 與 Seq 查詢到的日誌可以直接對上，不需維護第二套識別碼。
**替代方案（未採用）**：`Serilog.Enrichers.CorrelationId`——會另外產生一個新 GUID 當作關聯值，與 `TraceIdentifier` 是兩個不同的值，前端顯示的錯誤 traceId 反而無法用來查 Seq，徒增混淆；且該套件非 Serilog 官方維護。

背景服務（`ExpiredOrderCleanupService`、`PurchaseQueueAdmissionService`）沒有 `HttpContext`，改為在每次執行週期開始時用同一個 `LogContext.PushProperty("TraceId", Guid.NewGuid())` 推入一個新產生的關聯值，讓同一輪執行週期內的多筆日誌（例如處理多筆逾時訂單）可用同一個 `TraceId` 篩選，屬性名稱與 HTTP 路徑保持一致，Seq 查詢時不需區分兩種來源。

### 3. Sinks：Console + Seq 並存
Console sink 保留（`docker compose logs api` 仍可直接看），新增 Seq sink（`Serilog.Sinks.Seq`）透過 compose service name（`http://seq:5341`）寫入。
**理由**：Console 輸出是既有除錯習慣（不中斷既有工作流程），Seq 是新增的查詢能力，兩者互不取代。

### 4. `Microsoft.EntityFrameworkCore.Database.Command` 覆寫為 Warning
Serilog 設定的 per-namespace override 明確把 EF Core 產生的 SQL 指令日誌等級調到 `Warning`（預設 `Information` 會讓每一次查詢都產生一筆日誌，本機測試/一般使用情境下會迅速灌爆 Seq，稀釋真正需要關注的業務日誌）。
**理由**：與既有 `appsettings.json` 的 `Microsoft.AspNetCore: Warning` 是同一種「框架雜訊降噪」考量的延伸，不是新原則。

### 5. Seq 服務對外僅暴露 Web UI port，ingestion port 留在 compose 網路內
`api` 容器透過 compose service name（`http://seq:5341`）寫入日誌，不需要 host 對外映射；只有 Web UI（container port 80）映射到 host（`${SEQ_HOST_PORT:-8081}:80`），比照既有服務「可調 host port 避免衝突」慣例。Web UI port 映射方式（`"${SEQ_HOST_PORT:-8081}:80"`）沿用既有 `db`／`api`／`web` 服務的既定作法，不額外限制僅綁定 `127.0.0.1`——與其餘服務的暴露範圍一致，非本次新增的例外。
**理由**：ingestion port 沒有從瀏覽器直接存取的需求，縮小對外暴露面；Web UI port 綁定方式維持與既有服務一致，避免同一個 compose 檔案內出現不一致的慣例。

### 6. Seq 容器資料不持久化（不使用 named volume）
`seq` 服務不掛載 named volume，容器重建（`docker compose down` 後 `up`，或 `docker compose up --build`）後歷史日誌清空，須重新產生。
**理由**：本機展示/開發用途，不需要跨容器重建保留歷史日誌；比照本次不新增帳號密碼驗證的同一個「本機展示情境」判斷基準，避免為非必要情境引入 volume 生命週期管理的複雜度。若未來需要保留歷史日誌（例如真的要拿來對照排查一個已重建過的問題），屬於部署情境變化，屆時再另外評估。

### 7. Seq sink 連線失敗不阻塞應用程式
`Serilog.Sinks.Seq` 本身以非同步批次方式送出日誌（背景執行緒定期批次上傳，不是每筆日誌同步等待網路 I/O），sink 本身連線失敗時的例外由 Serilog 內部處理（透過 `Serilog.Debugging.SelfLog` 記錄，不會往外拋出到呼叫端），因此屬於 Serilog 既有的設計保證，不需要額外程式碼包一層 try/catch。
**理由**：這是選用 `Serilog.Sinks.Seq` 而非自行寫 HTTP 呼叫的主要原因之一——不必自己處理連線失敗、重試、逾時的邊界情況。實作階段仍需寫一個整合測試驗證這個保證在本專案的實際設定下成立（見 tasks.md），不能只憑套件文件宣稱就視為已驗證。「不阻塞」的可驗收界線（見 spec.md）採「與 Seq 可連線時的基準相比無明顯差異」而非絕對毫秒數，測試以 `Stopwatch` 量測同一請求在 Seq 可連線／不可連線（含拒絕連線與 black-hole 兩種）三種情境下的處理時間，三者應落在同一數量級（例如彼此差距在數十毫秒內，而非因為等待網路逾時多出秒級時間），避免用一個武斷的絕對數字當驗收標準。

### 8. OBS-SEQ-SERVICE-STARTS／OBS-API-LOG-QUERYABLE-IN-SEQ 用 Testcontainers 啟動真實 Seq 容器做自動化整合測試
比照既有 `ProjectC.Infrastructure.Tests` 用 Testcontainers 啟動獨立 Postgres 容器做整合測試的既定慣例（`PostgresFixture`），新增一個測試專用的 Seq Testcontainers fixture：測試啟動一個獨立的 `datalust/seq` 容器（與 `docker-compose.yml` 定義的 `seq` 服務完全獨立，不共用），取得動態映射的 port，把 Serilog 設定指向這個測試容器，觸發一次日誌後透過 Seq 的查詢 API（`GET /api/events`）確認該筆日誌可查得。
**理由**：CLAUDE.md 規則要求每條 Acceptance Criteria 至少對應一個 xUnit／NUnit 測試，人工驗證只能補充、不能取代；先前認為「這是 Should 等級的本機工具，用 Testcontainers 自動化這兩個環境行為的複雜度不成比例」的判斷不成立——本專案的整合測試已經是 Testcontainers-based（Postgres），對 Seq 採用同一套既有模式並非新增一套陌生機制，複雜度是可控的既有慣例延伸，沒有理由對 Seq 例外。
**與 tasks.md 第 7 節人工驗證的關係**：第 6 節的 Testcontainers 整合測試涵蓋的是「本專案程式碼是否正確地把日誌寫進任何一個 Seq 服務」；tasks.md 7.1／7.2 保留作為「`docker-compose.yml` 裡實際定義的 `seq` 服務本身設定（host port 映射、`ACCEPT_EULA` 等 compose YAML 語法）有沒有寫對」的額外人工確認——兩者驗證的對象不同，人工驗證是自動化測試之外的補充，不是替代。

## Risks / Trade-offs

- **[Risk]** Serilog 相關 NuGet 套件的實際 transitive 依賴（例如 `Serilog.AspNetCore` 是否已內含 `Serilog.Settings.Configuration`、`Serilog.Sinks.Console`）依版本而異，文件記憶可能過時 → **Mitigation**：實作階段以 `dotnet list package` 實際安裝結果為準，不假設套件圖，缺哪個套件再明確加（比照既有「驗證安裝結果，不盡信文件」的專案慣例）
- **[Risk]** 切換 Provider 若設定錯誤（例如 `Serilog` 節點語法錯誤）可能導致應用程式啟動失敗 → **Mitigation**：`Program.cs` 用 `try/catch` 包住 `Log.Logger` 初始化並在失敗時 fallback 輸出到 Console、記錄啟動錯誤後才拋出，避免靜默啟動失敗難以排查
- **[Risk]** 既有程式碼若有任何呼叫點不慎把敏感資訊（token、密碼、完整請求 body）直接放進日誌訊息樣板，Serilog 只是換了輸出目的地，不會自動遮蔽；更隱蔽的風險是結構化日誌特有的：即使渲染後的訊息文字有正確遮蔽，若原始未遮蔽值被當成額外具名參數傳入樣板，仍會被 Serilog 保留成一個獨立的結構化屬性，連帶寫入 Seq → **Mitigation**：已升級為明確 spec Requirement（見 spec.md「既有能力定義的敏感資訊遮蔽規則在結構化日誌下持續適用」），透過整合測試直接檢查 `LogEvent` 的所有結構化屬性（不只渲染後文字），涵蓋既有 `email-notification`（Email 遮蔽）與 `ticket-issuance`／`ticket-redemption`（簽章／QR 內容不得輸出）兩組既有規則（tasks.md 對應測試任務）
- **[Trade-off]** Seq 本機容器不設帳號密碼 → 僅接受本機展示情境（比照 `pgadmin` 現況），不適用於任何正式/多人共用環境；若未來需要多人共用的部署環境，需另外評估認證機制（不在本次範疇）

## Migration Plan

1. 新增 NuGet 套件（`Directory.Packages.props` 集中管理版本），`Program.cs` 改用 `Host.UseSerilog(...)` 取代預設 logging 註冊
2. `appsettings.json` 新增 `Serilog` 設定節（sinks、最低等級、per-namespace override）
3. 新增 TraceId enrichment middleware，掛在管線前段（越早掛載，涵蓋範圍越完整）
4. `docker-compose.yml` 新增 `seq` 服務，`api` 服務新增 `depends_on: seq` 與 Seq 連線設定
5. 背景服務加上每輪執行週期的 `LogContext.PushProperty`
6. 本機 `docker compose up` 驗證：Console 仍可見日誌、Seq UI 可查詢、同一次請求的多筆日誌共用同一個 `TraceId`
7. 無資料遷移、無需回滾流程（純新增基礎設施，關閉/移除 `seq` 服務不影響既有功能，`ILogger` 呼叫端完全不受影響）

## 附錄：`appsettings.json` Serilog 設定節範例

**實作已完成，以下為實際採用的最終結構**（非原始規劃草稿——原草稿把 Seq sink 也放進 `WriteTo` 陣列，實作階段改用獨立的純量設定值 `Seq:ServerUrl`，見決策 1 附註；`Program.cs` 的 `SerilogConfigurator.Configure` 判斷該值非空白時才呼叫 `WriteTo.Seq(...)`，`WriteTo` 陣列本身只保留 Console）：

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
      }
    },
    "WriteTo": [
      { "Name": "Console" }
    ],
    "Enrich": ["FromLogContext"]
  },
  "Seq": {
    "ServerUrl": ""
  }
}
```

## Open Questions

測試環境（含 CI）的 Seq 依賴分兩種情況：多數整合測試（tasks.md 第 5 節，結構化欄位、TraceId 關聯、敏感資訊遮蔽、Seq sink 失敗容錯）一律接上第 4.1 節的記憶體內 Serilog sink 斷言，或指向不存在位址的假 `serverUrl`，不依賴真的 Seq 容器；唯獨 OBS-SEQ-SERVICE-STARTS／OBS-API-LOG-QUERYABLE-IN-SEQ 兩項（決策 8）透過 Testcontainers 啟動真實 Seq 容器，比照既有 Postgres 整合測試模式，CI 環境只要能跑 Docker（既有 Postgres 整合測試已經要求這個前提）即可執行，不需額外環境準備。因此本次規劃無殘留的開放問題。
