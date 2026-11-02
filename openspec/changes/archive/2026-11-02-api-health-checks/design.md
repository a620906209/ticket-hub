## Context

- `src` 內沒有 `AddHealthChecks` / `MapHealthChecks`；目前 `GET /health/live` 實測回 404（compose，HTTP，未被轉址）。
- `Program.cs` 管線順序：TraceId → `UseSerilogRequestLogging` → `UseExceptionHandler` → `UseHttpsRedirection` → 驗證／授權／限流 → `MapControllers`。
- `AddRateLimiter` 只有具名 policy，沒有 GlobalLimiter；也沒有授權 FallbackPolicy，因此新端點目前預設不受兩者影響；限流的明確排除見決策 8。
- compose 只開 HTTP（`ASPNETCORE_URLS=http://0.0.0.0:8080`），容器內沒有 `HTTPS_PORT` 環境變數。`UseHttpsRedirection` 在這個情況下未轉址（上述實測）；設定 HTTPS 埠後的行為由 HC-REDIRECT-OTHER-UNCHANGED 驗證。
- 預設日誌最低等級為 Information，`Microsoft.AspNetCore` 為 Warning。請求摘要日誌由 `UseSerilogRequestLogging` 產生，預設 5xx 或例外為 Error、其餘為 Information。

## Goals / Non-Goals

**Goals:**

- 提供 K8s 探針可用的 live / ready 端點，行為由整合測試驗證。
- Redis、Seq 故障不會讓 Pod 被重啟或移出 Service。
- 匿名端點不洩漏內部資訊，也不產生日誌雜訊。

**Non-Goals:**

- production Dockerfile、K8s manifest 與探針參數、compose api healthcheck、Secret / ConfigMap 分流。
- `ASPNETCORE_ENVIRONMENT` 與 nginx TLS 的決定。
- Redis 健康監控或告警。

## Decisions

### 決策 1：使用內建 Health Checks，放在 WebApi 層

使用 `AddHealthChecks`、`AddCheck<DatabaseReadinessHealthCheck>` 與 `MapHealthChecks`。探測是部署層關注點，沒有業務規則，所以不經過 Application Handler；符合 CLAUDE.md「Minimal API 僅用於極簡單、無需複雜驗證的內部端點」。不新增專案介面，`IHealthCheck` 是框架介面。

`DatabaseReadinessHealthCheck` 放在 WebApi，注入 `ApplicationDbContext`，以 `Database.CanConnectAsync(cancellationToken)` 探測，不執行業務查詢：回傳 `true` 為 Healthy，`false` 為 Unhealthy。依 EF Core 文件，`CanConnectAsync` 會吞掉連線例外並回 `false`（取消除外），所以拒絕連線與目標資料庫不存在都走 `false`；check 本身不捕捉例外。逾時取消若以 `OperationCanceledException` 拋出，由內建 Health Check 服務轉為 Unhealthy；若 Npgsql 包成其他例外而回 `false`，同樣是 Unhealthy，兩條路徑都回 503。內建服務每次檢查建立獨立 scope，符合 `DbContext` 的 Scoped 生命週期。

替代方案：

- Controller 自行查 DB：需要自行處理逾時、狀態碼與回應，內建機制已涵蓋，不採用。
- `AddDbContextCheck`（Grill 時的原方案）：官方簽章只有 `name`、`failureStatus`、`tags`、`customTestQuery`，沒有 `timeout`。若要用，須新增套件，再透過 `Configure<HealthCheckServiceOptions>` 另外改 registration 的 `Timeout`，比約 10 行的自訂 check 更迂迴，不採用。

### 決策 2：readiness 只檢查 DB，不檢查 Redis

Redis 是所有 Pod 共用的相依。若納入 readiness，Redis 故障會把所有 Pod 移出 Service，讓可降級的功能（查詢快取等）變成全站中斷。DB 不可用時核心流程確實無法運作，因此只檢查 DB。

已接受的取捨：Redis 故障時 captcha 會失敗，探針看不出來。

live 不檢查任何相依，因為重啟行程修不好外部故障。以 tag 區分：DB check 加 `ready` tag，live 的 predicate 為 `_ => false`，ready 只跑帶 `ready` tag 的 check。

### 決策 3：逾時由 Health Check 註冊設定為 2 秒

`AddCheck<DatabaseReadinessHealthCheck>("database", tags: ["ready"], timeout: TimeSpan.FromSeconds(2))`，內建機制逾時後取消傳入 check 的 `CancellationToken` 並回報 Unhealthy，對應 503。不改全域連線字串的 Timeout，避免影響其他 DB 操作。

是否真的能中斷 Npgsql 的連線流程，以 HC-READY-TIMEOUT 實測，並在 GREEN 後暫時移除 timeout 確認測試會失敗（tasks 2.2）。

實測結果（apply 階段）：端點接受 TCP 但不回應時，只傳 token 給 `CanConnectAsync` 不會中斷，`/health/ready` 花 15 秒，等於 Npgsql 預設的連線 Timeout；內建服務會等 check 回傳，所以 registration 的逾時沒有實際效果。曾嘗試 `CanConnectAsync(cancellationToken).WaitAsync(cancellationToken)` 放棄等待，但 scope 釋放 `DbContext` 時連線仍在進行，拋出 `InvalidOperationException: Can't close, connection is in state Connecting`，回應變成 500，所以不採用。

因此 check 在呼叫 `CanConnectAsync` 前，以 `Database.SetConnectionString` 為這次檢查的 `DbContext` 設定 `Timeout`，值與 registration 共用常數 `DatabaseReadinessHealthCheck.TimeoutSeconds`。內建服務每次檢查建立獨立 scope，所以全域連線字串與其他 DB 操作不受影響。實際結束等待的是 Npgsql 的連線 Timeout；registration 的 timeout 保留，負責取消傳入的 token，兩者同為 2 秒，會互相競爭，實測都回 503。

對抗式審查發現，連線 Timeout 只管建立新連線：預熱後 DB 才凍結時，探測重用池中的閒置連線，等待改由命令逾時決定，`/health/ready` 卡 34 秒。候選方案實測結果：`CommandTimeout = 2` 為 6.02 秒，再加 `CancellationTimeout = -1` 仍為 6.02 秒，都超過 4 秒上限；`Pooling = false` 為 2.01 秒。因此探測的連線字串另外設 `Pooling = false`，每次都建立新連線。以 `GetHealthReady_WhenDatabaseFreezesAfterSuccessfulProbe_ReturnsServiceUnavailableWithinTimeout` 保護。

已接受的取捨：每次探測建立並關閉一條實體連線，一般探針頻率下成本很小；探測不受業務連線池的上限約束，併發風險見 Risks。連線已建立、DB 才在命令途中凍結的極短窗口，該次探測仍可能等到命令逾時，下一次探測就會回到 2 秒，不另外處理。

第 2 輪對抗式審查發現，2 秒上限涵蓋不到 DB 主機名稱解析：實測 `docker compose stop db` 為 2 到約 4 秒（tasks 6.3 當時為 0.015 秒，耗時取決於 DNS 回應速度），容器內 DNS 無回應時為 10 到 12 秒（推測為 glibc 預設 5 秒兩次，未讀 Npgsql 原始碼確認），都回 503。決定收窄規格，上限不含名稱解析，不改程式。理由：要硬性限時須放棄 DbContext，自行管理脫離請求的連線與背景工作，複雜度不划算；kubelet 逾時本來就判定失敗，就緒判定結果不會錯。部署限制記在 `docs/deployment-prerequisites.md`。

### 決策 4：使用內建 response writer

內建預設 writer 只輸出整體狀態文字，預設狀態碼對應為 Healthy／Degraded → 200、Unhealthy → 503。本次沒有 Degraded 來源。不自訂 writer，不加入 JSON 明細，因為端點匿名，明細會暴露內部資訊。

`MapHealthChecks` 不限制 HTTP 方法，非 GET 請求會得到相同結果。端點沒有副作用，不另外限制；規格只約束探針使用的 GET。

### 決策 8：以 `.DisableRateLimiting()` 明確排除限流

目前沒有 GlobalLimiter，端點不掛 policy 就不受限，但這依賴現況：之後若新增 GlobalLimiter，探針會被一起限流，且不會有任何測試失敗。因此兩個端點明確加上 `.DisableRateLimiting()`。

只發固定次數的請求無法證明沒有限流，額度高於請求數的限流一樣會通過。所以用兩個互相獨立的測試，都先讓控制端點回 429，證明限流確實生效且額度已用盡，再呼叫探測端點並逐筆斷言：

- 全域限流：factory 加入額度為 1、視窗 1 小時的 GlobalLimiter，控制端點為 `GET /api/events`。沒有 `.DisableRateLimiting()` 的端點都會被擋下。
- 端點專屬限流：不加 GlobalLimiter，以設定把 `LoginRateLimiting:PermitLimit` 調為 1，控制端點為 `POST /api/auth/login`（限流早於模型繫結，本文內容不影響計數）。探測端點若誤掛 `login` policy，會因與控制端點共用同一分區而被擋下；全域額度不會干擾這個結果。

現有 `place-order`、`confirm-order`、`captcha`、`create-seat-map` 依會員或主辦方分區，要逐一耗盡須建立身分，且日後新增的 policy 不會被涵蓋。因此另外從 `EndpointDataSource` 取得兩個探測端點，斷言有 `DisableRateLimitingAttribute`、沒有 `EnableRateLimitingAttribute`：這個斷言不依賴 policy 名稱與額度，涵蓋現有與未來的所有具名 policy。行為測試證明停用確實生效，中繼資料測試證明沒有綁定任何 policy，兩者互補。契約不收窄為特定頻率，因為探針週期由後續 K8s change 決定，本次沒有可依據的數值。

框架在有 `DisableRateLimiting` 時會略過限流，這點依記憶中的 middleware 行為，未查原始碼，由 tasks 2.1、2.2 的測試與移除驗證實測。此 factory 的限流額度會污染同 host 的其他測試，所以這兩個測試各自使用獨立的 factory 實例。

### 決策 9：探測處理例外走全域例外處理

check 內的例外由內建服務轉為 Unhealthy，只有服務本身失敗才會離開端點。這條路徑交給既有的 `GlobalExceptionHandler`：回 500 與通用的 `ProblemDetails`，內容只有 `status`、固定標題與 `traceId`，不含例外訊息。規格與測試不約束 `Content-Type`，因為 handler 用 `WriteAsJsonAsync` 輸出，實際值由框架決定，且與探針判定無關。不另外攔截成 `Unhealthy`，因為這是罕見路徑，平台看到非 200 就會視為失敗，不值得多一層處理。因此回應內容只有 `Healthy`／`Unhealthy` 的保證只適用於探測結果。

`UseExceptionHandler` 在 Serilog middleware 內層，例外不會傳到 `GetLevel` 的 `exception` 參數，實際走的是非 200 分支。測試以 factory 將 `HealthCheckService` 換成會拋出帶標記訊息例外的實作，觸發真實管線。標記訊息內含假的連線字串與相依服務名稱。除了掃描本文，也斷言欄位集合完全等於 `status`、`title`、`traceId`，並限制 `traceId` 只含識別碼字元：`title` 固定、欄位固定，新增的 `detail` 或任何洩漏內容都會失敗，不必窮舉敏感字串。掃描時先移除 `traceId` 的值，因為隨機識別碼可能剛好含 `seq` 等字樣。

`GlobalExceptionHandler` 會把完整例外傳給 `LogError`，沒有遮蔽機制。因此本 change 的密碼保證明確限定為兩件事：探測處理不把系統設定的資料庫密碼帶入日誌或例外（HC-LOG-EXCEPTION-NO-PASSWORD）；真實 DB 連線失敗的日誌，含第三方例外內容，不含該密碼（HC-LOG-FAILURE-NO-PASSWORD）。不保證任意例外的訊息本身不含密碼，這是全站缺口，違反專案不得記錄敏感資訊的規則。使用者已核准限期例外（見「已核准的安全規則例外」），由獨立 change 處理，待辦記於 `docs/exception-log-masking-todo.md`：handler 為所有端點共用，遮蔽例外內容會改變全站日誌與排錯資訊。

測試例外因此不放任何形似密碼的字串，否則測試本身就會製造出專案禁止的日誌。回應的洩漏防護不依賴假密碼，靠的是欄位集合與 `title` 完全固定。

### 決策 5：日誌等級以 `GetLevel` 調整

在既有 `UseSerilogRequestLogging` 的 options 加 `GetLevel`：路徑是探測端點、狀態碼 200、沒有例外時回 Debug；探測端點的其他結果回 Error；非探測端點維持與 Serilog 預設相同的規則（例外或 5xx 為 Error，其餘 Information）。

替代方案：把探測端點移到 Serilog middleware 之前短路。失敗請求也會失去摘要日誌，不採用。

`observability` 要求每次請求產生摘要，這裡仍會產生，只是等級較低；該 Requirement 約束內容不含敏感資訊，所以不需修改。

DB 故障時的失敗原因不會由 check 回報（`CanConnectAsync` 只回 `false`）。依 aspnetcore 原始碼，內建 Health Check 服務會以 Error 記錄 check 結果，逾時情境會含完整堆疊，其他情況通常不含例外；EF Core 的連線錯誤在 `errorsExpected` 路徑可能降為 Debug，預設等級下看不到。因此預設等級下只知道探測失敗，不一定知道原因。這是為了簡單而接受的取捨，實際輸出在 3.1 確認。若日誌含主機名稱，屬內部資訊可接受；密碼不得出現，由 HC-LOG-FAILURE-NO-PASSWORD 以專用標記密碼驗證。

### 決策 6：以 `UseWhen` 將探測端點排除於 HTTPS 轉址之外

目前環境實測不會轉址，但這依賴「未設定 HTTPS 埠」這個隱性條件。K8s 環境或後續 TLS 決定可能設定它，因此以 `UseWhen` 明確排除：只有非探測路徑才進 `UseHttpsRedirection`。

替代方案：只靠不設定 HTTPS 埠。需求無法用會失敗的測試保護，不採用。

探測路徑在 WebApi 內集中定義為常數，由端點對應、`GetLevel`、`UseWhen` 共用，並提供單一判斷方法（路徑比對不分大小寫），避免三處字串不同步。比對只涵蓋兩個精確路徑。

### 決策 7：測試方式

以 `CustomWebApplicationFactory` 為基礎新增一個 factory，可覆寫 DB 連線字串、HTTPS 埠、Seq URL 與最低日誌等級，並掛 `InMemoryLogEventSink`（比照 `RedisUnavailableWebApplicationFactory` 的做法）。

- DB 拒絕連線：連線字串指向 `127.0.0.1:1`。
- 目標資料庫不存在：測試容器的連線字串改用不存在的資料庫名稱。
- DB 無回應：測試內開 `TcpListener` 接受連線但不回應，先呼叫 `/health/live` 完成 host 啟動，再量測 `/health/ready` 從送出請求到讀完回應的時間，並確認 listener 確實收到連線。量測值必須介於 1.5 到 4 秒：下限擋住過短的逾時，上限為 2 秒契約加上 2 秒負載餘裕，5 秒以上的實作都會失敗。
- 逾時設定值：時間量測在 2 到 4 秒之間無法分辨，例如 3 秒逾時也會通過，因此另外從 DI 取得 `HealthCheckServiceOptions`，斷言 `database` registration 的 `Timeout` 完全等於 2 秒。設定值測試保護數值，時間量測保護設定真的生效，兩者都需要。
- Redis 無法連線：沿用 `RedisUnavailableWebApplicationFactory`。
- Seq 無法連線：覆寫 Seq URL 為無人監聽的位址。
- 日誌：以 `SourceContext` 等於 `Serilog.AspNetCore.RequestLoggingMiddleware` 且路徑相符的事件斷言等級。摘要可能在回應送出後才寫入，所以用輪詢等待事件；斷言「沒有事件」時，先確認探測請求回 200，之後才呼叫一個非探測端點並等到它的摘要，避免探測沒成功或摘要還沒寫入就通過。
- 相依檢查未被呼叫：只看狀態碼分不出「呼叫了但忽略結果」。factory 以 `PostConfigure<HealthCheckServiceOptions>` 把每個 registration 的 `Factory` 包成計數裝飾器，並加一個未帶 tag 的探查 check，斷言實際執行次數；另在未加料的 factory 斷言 registration 只有 `database`，擋下新增的 Redis 或 Seq check。限制：計數只涵蓋 Health Check 機制內的呼叫，繞過機制直接呼叫相依的情況由下方「已核准的驗證例外」處理。
- 限流控制請求：`ExhaustRateLimitAsync` 最多發 5 次請求，整體期限 30 秒。額度為 1，最遲第 2 次就該回 429；達上限或逾時仍未出現時明確失敗，不進入探測請求，避免限流沒生效時無限等待或假通過。
- 密碼不外洩：比照 `SensitiveDataMaskingInStructuredPropertiesTests`，掃描所有擷取事件的 `RenderMessage()`、訊息樣板、`Exception.ToString()` 與每個 `Properties` 值的 `ToString()`，因為密碼可能只出現在結構化屬性。最低等級設為 Debug，讓 EF Core 降級的連線日誌也進入掃描範圍；這是預設等級事件的超集。掃描範圍是所有擷取事件，但掃描前的存在性斷言必須依 `TraceId` 關聯到該次探測請求，否則舊請求或背景服務的事件會湊數。`TraceIdLoggingMiddleware` 在最外層，摘要與請求內事件帶同一個 `TraceId`；測試對該路徑只發一次請求，斷言摘要恰好一筆後取其 `TraceId`。取值時屬性必須存在且為非空白字串，否則測試失敗：不在請求或背景循環範圍內的事件（例如 host 啟動日誌）沒有 `TraceId`，若以 null 比對，這些事件會被當成同一請求而讓存在性斷言假通過。關聯後須有三種事件：失敗請求的 Error 摘要、`SourceContext` 為 `Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService` 的 Error 事件、`SourceContext` 為 `Microsoft.EntityFrameworkCore.Database.Connection` 的事件。前兩者證明探測失敗有被擷取，第三者證明最可能接觸連線資訊的來源有被掃描。掃描方法另以一筆只在屬性內放標記密碼的事件驗證它會偵測到。

Testing 環境不執行 seeder，預期 DB 不可連線時 host 仍可啟動；背景服務的錯誤日誌不影響斷言，因為斷言以請求路徑篩選。這點尚未實測，由 tasks 2.1 的測試確認；若 host 起不來，停止實作並回到設計，因為這也代表正式環境的探針行為與假設不符。

### 已核准的驗證例外

使用者於 2026-11-02 核准。例外只豁免測試種類，以下替代驗證仍須可重現、可判定通過或失敗。

- **適用範圍**：只限 `GET /health/live` 不檢查 DB、Redis、Seq 或其他外部服務，以及 `GET /health/ready` 只檢查 DB 這兩項保證中，「探測流程繞過 Health Check 機制直接呼叫相依」的部分（HC-LIVE-NO-CHECKS、HC-READY-ONLY-DATABASE 所屬 Requirement）。Health Check 機制內的呼叫仍由計數與 registration 測試驗證。
- **偏離理由**：Redis 連線、背景服務與 Serilog 的 Seq sink 會自行產生流量，只以連線數或日誌無法歸因到探測請求；要歸因須為 EF、Redis、HTTP 各建一套依請求區分的追蹤，測試基礎設施的份量遠超過正式程式碼。實作只有 `MapHealthChecks` 與一個 check，沒有自訂 middleware 或 handler，繞過的路徑目前不存在。
- **替代驗證**：tasks 6.4 的腳本化靜態檢查（判定條件與失敗情境驗證見該 task），加上計數與 registration 測試，再由 `strict-reviewer` 核對探測路徑。
- **解除條件**：探測流程新增自訂 middleware、handler、`ResponseWriter`，或 `HealthChecks` 目錄與 `DatabaseReadinessHealthCheck` 須依賴 `DbContext` 以外的相依時，本例外失效，須改以能觀察相依呼叫的測試驗證。

### 已核准的安全規則例外

使用者於 2026-11-02 核准。

- **適用範圍**：只限探測端點未預期例外經既有 `GlobalExceptionHandler` 記錄完整例外的行為。本 change 不得新增其他會記錄例外內容的日誌，也不豁免 HC-LOG-FAILURE-NO-PASSWORD、HC-LOG-EXCEPTION-NO-PASSWORD 的保證。
- **偏離內容**：CLAUDE.md 規定錯誤日誌不得記錄敏感資訊；若未預期例外的訊息本身含敏感資訊，探測路徑仍會記錄。
- **偏離理由**：缺口為全站既有，非本 change 引入。handler 為所有端點共用，遮蔽層級與範圍須另行設計，不併入本 change。
- **補償措施**：探測流程不把設定的資料庫密碼帶入日誌或例外，真實 DB 失敗的日誌不含該密碼，兩者皆有測試；回應本文不含例外內容。
- **解除條件與期限**：遮蔽 change 完成後本例外失效；該 change MUST 在正式部署 K8s 前完成。tasks 5.1 將此前置條件寫入 `docs/k8s-todo.md` 第 1 項。

### 安全確認

- **輸入驗證**：端點不接受參數、body 或 query，沒有外部輸入進入邏輯，也沒有拼接 SQL 或 shell。
- **資料庫**：只透過 EF Core `CanConnectAsync` 測試連線，不讀寫資料，沒有 N+1。
- **權限**：刻意匿名，以 `.AllowAnonymous()` 明示。未授權使用者可以觸發，但探測結果只能得到 `Healthy` / `Unhealthy`；來源限制見 Risks 的上線 gate。
- **前端**：無前端變更。
- **機敏資訊**：回應不含內部資訊；探測流程與真實 DB 失敗的日誌不含設定的資料庫密碼，有測試驗證。例外訊息本身含敏感資訊的情況未遮蔽，見「已核准的安全規則例外」。

## Risks / Trade-offs

- **`/health/ready` 匿名且不限流，大量併發請求各自建立實體連線，可能耗盡 PostgreSQL 的 `max_connections`，讓業務請求連不上 DB** → 2 秒逾時只限制單次檢查，不限制頻率或併發；web 的 proxy 只轉送 `/api`，但這不涵蓋直接連到 api 的來源。本次不實作來源限制，因為入口與網路設定屬於後續部署 change，改由下列上線 gate 約束：
  - **可信來源**：部署平台的探針（K8s 為 kubelet，從節點發出）與維運人員。其他叢集內工作負載不因位於叢集內就視為可信。
  - **上線 gate**：K8s 或 compose／VPS 部署前，須以入口控制、NetworkPolicy 或等效機制，只讓可信來源存取這兩個路徑，並實測不可信來源確實無法連到；未完成前，MUST NOT 讓 api 暴露給不受信任的網路或工作負載。規則須放行探針來源。
  - **實測原因**：NetworkPolicy 是否生效取決於 CNI，Docker Desktop 尚未確認；K8s 的 `NodePort` 與 compose 未綁定 `127.0.0.1` 的 port 發布（`docker-compose.yml` 的 `API_HOST_PORT`）都會繞過「僅內部可達」的假設。
  - **必要驗收**：部署 change 的提案 MUST 將來源限制的設定列為 task，並將以下三項列為 AC 與驗證 task：探針來源可達且 Pod 能 Ready、維運來源可達、不可信 workload 與外部來源不可達。缺少任一項的部署提案不得進入實作。
  - **追蹤與限制**：repo 目前沒有 `k8s/`、production compose 或部署 change，本 change 沒有可設定或實測的部署拓樸，也無法以程式阻止未受保護的部署；gate 在部署 change 建立前只是流程約束。tasks 5.1 將 gate 與必要驗收寫入 `docs/k8s-todo.md` 第 1 項與「驗收方向」，後者是部署提案細化 AC 的來源；VPS 部署提案同樣適用。K8s 與 VPS 共用的前置條件彙整於 `docs/deployment-prerequisites.md`。
- **DB 連線數耗盡時 ready 失敗，高負載下 Pod 可能被移出 Service 並加重其他 Pod 負載** → K8s change 設定探針時應使用大於 1 的 `failureThreshold`，並在壓測時觀察。本次不處理。
- **DB 故障期間，每次探測都產生 Error 日誌** → 接受。這是真實故障訊號，頻率由探針週期決定。
- **Redis 故障時 captcha 失敗，探針看不出來** → 已接受（決策 2）。
- **`GlobalExceptionHandler` 不遮蔽例外內容，若未預期例外的訊息本身含密碼會被記錄** → 既有的全站缺口，非本次引入，也不限於探測端點。本次只驗證探測流程不帶入密碼，以及真實 DB 失敗不外洩；使用者已核准限期例外，全站遮蔽須在正式部署 K8s 前以獨立 change 完成，待辦記於 `docs/exception-log-masking-todo.md`。

## Migration Plan

新增端點，無資料遷移，不影響既有呼叫端。回復時還原變更即可。K8s 探針設定由後續 change 處理。

## Open Questions

無。
