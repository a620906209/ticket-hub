## 1. NuGet 套件與 Serilog 基礎設定

> 對應 Requirement:「系統以結構化欄位輸出日誌」「日誌等級與 Seq 連線位址可透過設定調整，不寫死於程式碼」（spec.md）——本節為落實這兩條 Requirement 的基礎設定，非直接對應單一 Scenario 的測試任務。

- [x] 1.1 `Directory.Packages.props` 集中新增套件版本：`Serilog.AspNetCore`、`Serilog.Sinks.Seq`、`Serilog.Settings.Configuration`（實際需要哪些套件以 `dotnet list package` 安裝結果為準，不假設套件圖，見 design.md Risk）；`ProjectC.WebApi.csproj` 引用
- [x] 1.2 `src/ProjectC.WebApi/Program.cs` 改用 `Host.UseSerilog(...)` 取代預設 logging 註冊，`ReadFrom.Configuration(...)` 讀取 `appsettings.json` 的 `Serilog` 節；初始化用 `try/catch` 包住（bootstrap logger + `Log.Fatal` + `Log.CloseAndFlush()`），失敗時記錄啟動錯誤後才拋出（design.md Risk）
- [x] 1.3 `appsettings.json` 新增 `Serilog` 設定節（結構見 design.md 附錄範例）：`MinimumLevel.Default: Information`、`Override` 包含既有 `Microsoft.AspNetCore: Warning` 與新增 `Microsoft.EntityFrameworkCore.Database.Command: Warning`、`WriteTo` 含 Console；**實作調整**：Seq sink 不透過 `Serilog:WriteTo` 陣列宣告（陣列索引型環境變數覆寫語法脆弱、依版本而定），改用獨立的純量設定值 `Seq:ServerUrl`，`Program.cs` 於 `UseSerilog` 回呼中判斷該值非空白時才呼叫 `loggerConfiguration.WriteTo.Seq(...)`，預設空值時不啟用該 sink，效果與原計畫相同、實作更直接可靠（**第六輪外部審查（blocking）**：spec.md 開頭「系統以結構化欄位輸出日誌」這條 Requirement 的文字容易被讀成連 Console sink 的純文字輸出本身都要是結構化格式（例如 JSON），但實作的 Console sink 就是預設的人類可讀文字樣板，兩者顯得矛盾——已釐清 spec.md 用詞：結構化保證的對象是 `LogEvent`／Seq 這類機器可查詢的目的地，Console sink 的文字渲染本來就不在此保證範圍內，已在 spec.md 該 Requirement 補上明確排除段落）
- [x] 1.4 `Program.cs` 掛上 `app.UseSerilogRequestLogging()`（`Serilog.AspNetCore` 內建的請求摘要日誌中介軟體，每次請求自動記錄方法／路徑／狀態碼／耗時），不客製化 `EnrichDiagnosticContext` 加入 headers 或 body（見 spec.md「日誌不得輸出敏感資訊」Requirement）

## 2. 請求／背景服務關聯值（TraceId）

> 對應 Requirement:「同一次 HTTP 請求的所有日誌自動共用同一個關聯值」「背景服務每輪執行週期的日誌共用專屬關聯值」（spec.md）。

- [x] 2.1 新增 middleware（`TraceIdLoggingMiddleware`，`src/ProjectC.WebApi/Logging/`），掛在管線前段：進入時 `LogContext.PushProperty("TraceId", httpContext.TraceIdentifier)`，包住後續 `await _next(httpContext)`
- [x] 2.2 `ExpiredOrderCleanupService`、`PurchaseQueueAdmissionService` 各自在每輪執行週期開始時 `LogContext.PushProperty("TraceId", Guid.NewGuid().ToString())`，包住該輪次的處理邏輯（**修正**：第二輪外部審查抓到——原本這個 scope 只包住 `ExecuteAsync` 呼叫的 core 方法本身，`ExecuteAsync` 外層 catch 到「整輪掃描階段就失敗」的 `_logger.LogError("...cycle failed...")` 這筆日誌，此時 `using` 早已 Dispose、TraceId 已跳出 scope，導致這筆日誌沒有 TraceId，不符合 spec「同一輪次所有日誌共用同一個關聯值」。已改為 `ExecuteAsync` 直接呼叫 core 方法，把 try/catch 整個包進同一個 `using` 內；新增 `BackgroundServiceCycleLevelFailureTraceIdTests.cs` 兩個測試鎖住這條路徑）

## 3. Docker Compose：新增 Seq 服務

> 對應 Requirement:「Seq 作為本機 Docker Compose 集中查詢平台」（spec.md，含其中的存取權限範圍段落）。

- [x] 3.1 `docker-compose.yml` 新增 `seq` 服務（`datalust/seq` image，`ACCEPT_EULA=Y`，Web UI port 映射 `${SEQ_HOST_PORT:-8081}:80`，比照既有服務不額外限制綁定介面；ingestion port 5341 不對外映射，僅供 compose 網路內部使用；不掛載 named volume，見 design.md 決策 6）（**實作調整**：`datalust/seq:latest` 改釘選為已驗證的 `datalust/seq:2026.1.17114`，避免未來 image 更新後行為/環境變數要求無預警改變——外部審查建議，`SeqTestcontainersFixture` 同步使用同一個版本）
- [x] 3.2 `api` 服務新增 `depends_on: seq`（`condition: service_started`，非 `service_healthy`——Seq 未提供健康檢查端點，且 Seq 不可用本就不該阻塞 api 啟動），新增環境變數 `Seq__ServerUrl=http://seq:5341`
- [x] 3.3 `.env.example` 新增 `SEQ_HOST_PORT` 說明，比照既有 `DB_HOST_PORT`／`API_HOST_PORT` 慣例

## 4. 測試基礎設施

> 對應 Requirement: 供第 5、6 節所有測試任務共用的前置設施，非直接對應單一 Scenario。

- [x] 4.1 `ProjectC.WebApi.Tests`（`TestSupport/InMemoryLogEventSink.cs`）新增一個記憶體內 Serilog sink，供第 5 節測試共用；`ObservabilityWebApplicationFactory` 透過 DI 註冊此 sink，`SerilogConfigurator.Configure` 從 `IServiceProvider` 解析並掛上（**實作調整**：原計畫在 `WebApplicationFactory.CreateHost` 二次呼叫 `UseSerilog` 掛 sink，實測會與 Program.cs 原本那次互相干擾、部分日誌管線各自為政、記憶體 sink 收不到所有事件，改用「Program.cs 唯一一次 `UseSerilog` 從 DI 解析 `ILogEventSink`」；過程中也發現並修好一個真實 production bug：`preserveStaticLogger: true` 會讓 `UseSerilogRequestLogging()` 的請求摘要日誌悄悄只走 bootstrap logger（無 Seq、無等級設定），已明確指定其 `options.Logger` 為 DI 解析出的 `Serilog.ILogger` 修正，見 Program.cs 註解）
- [x] 4.2 `ProjectC.WebApi.Tests/TestSupport/SeqTestcontainersFixture.cs` 新增一個 Seq Testcontainers fixture，啟動獨立的 `datalust/seq` 容器（`ACCEPT_EULA=Y`、`SEQ_FIRSTRUN_NOAUTHENTICATION=True`），用 compose 網路別名連線，供第 5.12／5.13、6 節測試共用；此容器與 `docker-compose.yml` 定義的 `seq` 服務完全獨立（design.md 決策 8）（**實作調整**：改放 `ProjectC.WebApi.Tests`，因為所有 observability 測試都在這個專案、需要 `WebApplicationFactory` 才能測到完整 app 行為，`ProjectC.Infrastructure.Tests` 沒有這個能力；就緒策略也踩到一個真實 bug——`UntilHttpRequestIsSucceeded` 會從測試行程用 host-mapped port 探測，但 Docker Desktop for Windows 下這種跨容器連線本來就不通（與既有 `PostgreSqlContainer` 註解說明的坑完全相同），導致整個測試行程卡死超過 20 分鐘、CPU 時間卻幾乎不動；改用 `UntilMessageIsLogged("Ingestion enabled")` 讀容器日誌判斷就緒，繞開這個網路限制，修好後 2 秒內完成）

## 5. 測試：結構化欄位、關聯值、敏感資訊、Seq sink 容錯（記憶體 sink，不依賴真實 Seq）

- [x] 5.1 單元測試：既有具名參數樣板（例如 `_logger.LogError(exception, "...{OrderId}...", orderId)`）輸出後，`LogEvent.Properties` 可取得名為 `OrderId` 的獨立欄位且值相符
  - 對應 AC: OBS-STRUCTURED-FIELD-PRESERVED
- [x] 5.2 整合測試（`WebApplicationFactory`，接上第 4.1 節的記憶體 sink）：單次請求處理過程中，Handler 與其呼叫路徑各自輸出的日誌條目，`LogEvent.Properties` 中鍵名確實為 `"TraceId"`（非框架自動轉換過的其他名稱）且值彼此相同，並與該次回應中 `HttpContext.TraceIdentifier`（或觸發例外時 `ProblemDetails.Extensions["traceId"]`）一致（**實作調整**：以觸發 429 限流回應的 `ProblemDetails.traceId` 比對，比自造例外路徑更貼近既有程式碼行為，見測試檔內註解；**第二輪外部審查再修正**：原本只斷言「至少一筆」日誌符合，蓋不到「這次請求是否有日誌漏掉 TraceId 或帶了不同值」——改成先在隔離窗口外把限流額度暖機打滿，`Clear()` 後只送隔離觀察的那一次請求，斷言該次產生的**所有**日誌都有 TraceId、且全部等於同一個值、且等於回應的 traceId）
  - 對應 AC: OBS-REQUEST-TRACE-CONSISTENT
- [x] 5.3 整合測試：依序送出兩次不同請求，各自日誌的 `TraceId` 欄位值不相同
  - 對應 AC: OBS-REQUEST-TRACE-UNIQUE
- [x] 5.4 整合測試：觸發背景服務單一輪次處理多筆項目（例如注入多筆逾時訂單資料），同一輪次輸出的日誌條目共用同一個 `TraceId`；手動觸發第二輪後，兩輪的 `TraceId` 值不同（**實作調整**：沿用 `ExpiredOrderCleanupServiceTests` 現有 seeding 手法建構「已由本訂單售出但仍是 Pending」的不一致狀態，確保處理時一定進 `LogWarning` 分支才有日誌可觀察；並改用 DI 解析出的真實 `ILogger`，不能沿用既有測試的 `NullLogger`）；另補上 `BackgroundServiceCycleLevelFailureTraceIdTests.cs`（用 `Mock<IServiceScopeFactory>` 讓掃描階段直接拋例外，覆蓋個別項目失敗以外的「整輪失敗」路徑，對應 2.2 的修正）
  - 對應 AC: OBS-BACKGROUND-CYCLE-TRACE（`BackgroundServiceCycleLevelFailureTraceIdTests` 最初用固定 `Task.Delay(300)` 等待背景服務跑完第一輪，第三輪外部審查（strict-reviewer）標記為潛在 flaky 風險——已改成輪詢等待條件成立，逾時 5 秒才放棄，不賭一個固定時間點；第六輪外部審查（非 blocking 建議）：原本逾時後靜默返回，失敗訊息只顯示「找不到事件」，看不出是背景服務真的沒觸發還是單純逾時太短——`WaitUntilAsync` 已改為接受描述性 `timeoutMessage` 參數，逾時時明確拋出 `TimeoutException` 而非靜默返回）
- [x] 5.5 整合測試：送出帶 `Authorization: Bearer <token>` 標頭的請求，觸發請求摘要日誌後，記憶體 sink 收到的所有 `LogEvent`（含其 `Properties`）內容 MUST NOT 包含該 token 字串
  - 對應 AC: OBS-REQUEST-LOG-NO-AUTH-HEADER
- [x] 5.6 整合測試：送出帶 `Cookie` 標頭的請求，觸發請求摘要日誌後，記憶體 sink 收到的所有 `LogEvent` 內容 MUST NOT 包含該 Cookie 值
  - 對應 AC: OBS-REQUEST-LOG-NO-COOKIE
- [x] 5.7 整合測試：送出帶 JSON body 的請求，觸發請求摘要日誌後，記憶體 sink 收到的所有 `LogEvent` 內容 MUST NOT 包含該 body 內容
  - 對應 AC: OBS-REQUEST-LOG-NO-BODY
- [x] 5.8 整合測試：走完整下單→確認訂單流程觸發 `MockEmailNotificationService` 的成功通知記錄路徑，記憶體 sink 收到的該筆 `LogEvent` 的**所有結構化屬性**（不只渲染後訊息文字）皆不包含未遮蔽的完整 Email，`ToEmail` 屬性本身即為 `EmailMasker.Mask` 的遮蔽格式（**實作調整**：改走成功通知路徑而非失敗路徑——`OrderService` 的失敗記錄只含 `OrderId` 不含 Email，成功路徑的 `MockEmailNotificationService.NotifyTicketsIssuedAsync` 才是唯一把 Email 放進日誌屬性的呼叫點；**第二輪外部審查抓到 spec drift**：當時只改了 tasks.md 的文字說明，spec.md 的 Scenario WHEN 子句仍寫著過時的「通知失敗記錄」，兩邊不一致——已回頭同步更新 spec.md 對齊實際實作）
  - 對應 AC: OBS-EMAIL-MASKED-IN-STRUCTURED-PROPERTIES
- [x] 5.9 整合測試：觸發一次核銷相關的日誌記錄路徑（例如帶簽章的核銷請求），記憶體 sink 收到的該筆 `LogEvent` 的**所有結構化屬性**皆不包含 QR 內容或簽章值本身
  - 對應 AC: OBS-SIGNATURE-NOT-IN-STRUCTURED-PROPERTIES
- [x] 5.10 整合測試：執行一次一般（非例外）的 EF Core 查詢後，記憶體 sink 收到的 `LogEvent` 中不存在來源為 `Microsoft.EntityFrameworkCore.Database.Command`、等級為 `Information` 或以下的條目（`Warning` 等級以上的條目可能存在，不在此斷言範圍內）
  - 對應 AC: OBS-EF-COMMAND-LOG-SUPPRESSED
- [x] 5.11 單元測試：以測試專屬設定（`IConfiguration` 注入不同 `MinimumLevel`）建立 Logger，驗證低於設定門檻的日誌呼叫不會產生對應 `LogEvent`，高於門檻的正常輸出
  - 對應 AC: OBS-LOG-LEVEL-VIA-CONFIG
- [x] 5.12 整合測試（`ObservabilityWebApplicationFactory`，在建構後、`CreateClient()` 前就把 `SeqServerUrl` 設定為一個會立即拒絕連線的位址，確保應用程式從啟動當下就帶著這個無法連線的設定）：
  - 斷言一：建立 `WebApplicationFactory` 並取得 `HttpClient`（觸發 host 建置與啟動）本身不拋出例外——獨立驗證 Requirement 的「應用程式仍可正常啟動」子句，不與後續請求斷言合併
  - 斷言二（第二輪外部審查補上，之後歷經兩次再修正，第六輪外部審查後升級為主要判斷依據）：驗證 Seq sink 連線失敗不影響「其他 sink」持續接收事件（spec.md 舉的具體例子是 Console sink，本質是同一個 Serilog sink-dispatch-互相獨立的底層保證）——第一次修正把重導向 `Console.Out` 的窗口從「整段 HTTP 請求」縮到「一次同步方法呼叫」；第四輪外部審查認為即使縮到同步呼叫，OS 執行緒排程理論上仍可能在任意指令邊界搶佔，process 全域靜態狀態的風險沒有真正歸零，「測試可靠性風險不該用機率很小打發」——最終改成完全不碰真正的 `Console.Out`，改用另一個記憶體內 sink 當作「Console 以外的任何 sink」的代表（走 Serilog 核心同一套 dispatch 機制，驗證力等價，且零跨測試共用狀態）；Console sink 本身持續正常運作由第 7 節人工驗證涵蓋
  - 斷言三（同一測試用例內對比，第六輪外部審查後降級為輔助訊號）：先用 `SeqTestcontainersFixture`（第 4.2 節，可正常連線的真實 Seq 容器）送出一次請求量測基準耗時，再用無法連線的 `factory` 送出一次請求量測耗時，斷言兩次皆回應成功且耗時同一數量級（`baseline * 5 + 2s` 寬鬆門檻，避免系統抖動 flaky）；第六輪外部審查指出 baseline factory 可能已被同測試類別內其他測試方法「用熱」（EF Core 連線池、JIT、路由快取都已初始化），而失敗情境的 factory 每次都是全新 `new` 出來、必定是「冷」的第一次請求，兩者不對稱可能讓比較失真——已在 `MeasureRequestElapsedAsync` 加入一次不計時的暖機請求排除這類一次性成本，並在測試檔內註解明確標註這條斷言只是門檻刻意寬鬆的 sanity check，不阻塞的核心保證已由斷言一（啟動不拋例外）與斷言二（其他 sink 持續正常運作）涵蓋
  - 斷言四（第四輪外部審查補上）：明確驗證同時存在的兩個 `ObservabilityWebApplicationFactory`（一個 Seq 可連線的 baseline、一個不可連線）解析到的 `Serilog.ILogger` 是不同物件——把「每個 host 各自獨立、不共用 process 全域 logger」這個底層假設從「設計上如此＋其他測試行為間接印證」變成明確驗證過的事實
  - 對應 AC: OBS-SEQ-SINK-FAILURE-RESILIENT（**實作調整**：手動 `new` 出來的 `ObservabilityWebApplicationFactory` 不受 xUnit `IClassFixture` 管理，`IAsyncLifetime.InitializeAsync()` 不會自動被呼叫，必須自己顯式呼叫一次，否則 Postgres Testcontainers 沒啟動、連線字串是空字串，實測發現）
- [x] 5.13 整合測試：同 5.12 的兩段斷言，但失敗情境的 `SeqServerUrl` 改指向 RFC 5737 保留位址（`192.0.2.1`，全球路由器不轉發、穩定重現黑洞情境）
  - 對應 AC: OBS-SEQ-SINK-BLACKHOLE-RESILIENT

## 6. 測試：Seq 服務本身（Testcontainers，真實 Seq 容器）

- [x] 6.1 整合測試（接上第 4.2 節的 Seq Testcontainers fixture）：容器啟動後，透過 compose 網路別名對 Web UI 發出 HTTP 請求，斷言回應成功
  - 對應 AC: OBS-SEQ-SERVICE-STARTS
- [x] 6.2 整合測試：把 Serilog 設定指向第 4.2 節的 Seq Testcontainers 容器，觸發應用程式輸出日誌，透過 Seq 的查詢 API（`GET /api/events`，依訊息內容篩選）確認該筆日誌可查得（Seq 為非同步批次寫入，測試輪詢等待，而非假設立即可查）（**修正**：第二輪外部審查抓到 false positive——原本用自訂 header 當唯一 marker，但 header 從不出現在日誌裡（本專案刻意不記錄 headers），查詢時只驗證「訊息含 "HTTP"」，殘留的舊日誌就足以讓斷言誤判通過。改成打一個帶不存在 Guid 的既有端點（`GET /api/events/{guid}/seats`，安全 404、不拋例外），用這個唯一路徑片段（會出現在 `RequestPath` 屬性、進而出現在渲染訊息）查詢比對，才能證明「這次」請求真的寫進 Seq）
  - 對應 AC: OBS-API-LOG-QUERYABLE-IN-SEQ

## 7. 人工補充驗證（`docker-compose.yml` 實際 Seq 服務設定，非 AC 的唯一覆蓋）

> 第 6 節的 Testcontainers 測試已經是 OBS-SEQ-SERVICE-STARTS／OBS-API-LOG-QUERYABLE-IN-SEQ 這兩條 AC 的正式自動化覆蓋。以下人工步驟驗證的是「`docker-compose.yml` 裡實際定義的 `seq` 服務設定本身寫得對不對」（host port 映射、`ACCEPT_EULA`、跟 `api` 服務的 `depends_on`／連線設定等 compose YAML 語法），屬於自動化測試之外的環境健全性補充確認，不是 AC 的唯一驗收依據（比照 `redemption-scanner-ui` 6.4 相機掃描實機驗證——那裡的 AC 同樣已有 xUnit 覆蓋，實機驗證是額外的真實環境信心，見 `openspec/changes/archive/2026-08-31-redemption-scanner-ui/tasks.md`）。

- [x] 7.1 `docker compose up` 啟動所有服務，確認 `seq` 容器成功啟動且可透過對外映射的 host port 開啟 Web 查詢介面（已確認：`http://localhost:8081/` 回應 200；第五輪外部審查提醒不應只憑先前快照宣稱「已確認」，已用重啟後的最新 `api` 容器重新驗證一次，見 7.2）
- [x] 7.2 觸發任一 API 請求（例如 `GET /api/events`）後，於 Seq Web UI 依 `TraceId` 或訊息內容查詢，確認該筆日誌可查得（**第五輪重新驗證**：`docker compose restart api` 套用最新程式碼後，觸發一次帶唯一 Guid 的請求 `GET /api/events/{uniqueGuid}/seats`，透過 compose 定義的 `seq` 服務（非 Testcontainers，`http://localhost:8081/api/events`）查詢，確認該筆日誌的 `RequestPath` 屬性含這個唯一 Guid、`TraceId` 屬性存在——一併驗證了 host port 映射、`api`→`seq` 的 compose 網路連線、`Seq__ServerUrl` 環境變數在最新程式碼下全部正常運作）

## 8. 收尾

- [x] 8.1 清查既有所有 `_logger.Log*(...)` 呼叫點（`OrderService`、`ExpiredOrderCleanupService`、`PurchaseQueueAdmissionService`、`MockEmailNotificationService`、`GlobalExceptionHandler` 等），確認沒有任何一處直接把 token、密碼、完整請求 body 等機敏資訊放進訊息樣板或參數——Serilog 只是替換輸出目的地，不會自動遮蔽既有呼叫點的內容；第 5.8／5.9 的測試涵蓋既有規則的兩個具體案例（Email、簽章），本項為更廣泛的人工複查，非重複勞動（design.md Risk）（複查結果：共 8 個呼叫點，其餘皆為例外物件、`OrderId`／`EventId`／`ErrorType`／`ErrorMessage` 等非機敏識別資訊，`ErrorMessage` 來自內部 `Error` 工廠方法產生的固定樣板文字，不含使用者輸入或機敏欄位，未發現問題）
- [x] 8.2 `docker compose exec api dotnet test` 全數通過（含既有測試套件，確認未因 logging provider 替換造成既有測試失敗，例如依賴 `ILogger` mock 行為的既有測試）——538 個測試全數通過（97 Domain + 194 Application + 80 Infrastructure + 167 WebApi，WebApi 較合併前的 149 增加 18 個 observability 測試）。經過四輪外部審查（第一輪 strict-reviewer PASS；第二輪使用者親自抓 2 blocking + 2 spec 對應問題 + 3 建議性問題；第三輪 strict-reviewer PASS 但 2 個測試穩健性 warning；第四輪使用者再抓「Console.Out 全域狀態風險沒有真正歸零」「Task.Delay 引用已過時」「logger 隔離性缺明確驗證」「Seq 釘選版本待查證」）逐一查證與修正後皆複測確認全數通過，過程無回歸；Seq 釘選版本（`datalust/seq:2026.1.17114`）已直接確認：image 可正常 pull、`SEQ_FIRSTRUN_NOAUTHENTICATION=True` 受此版本支援（容器正常啟動）、`Ingestion enabled` 確實出現在此版本 log、`/api/events` 查詢 API 行為與所有 Seq 相關測試一致
- [x] 8.3 更新 `docs/project-scope.md` 第 4 節「監控」欄位：移除 `［待確認］`，標記已完成並附 `openspec/changes/archive/` 連結（歸檔時執行）（同步更新第 8 節「待確認事項彙整」，移除已解決的「監控方案是否採用 Serilog + Seq」項目）
