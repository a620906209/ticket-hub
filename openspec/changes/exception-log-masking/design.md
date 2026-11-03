# Design

## Context

動機見 proposal.md。現況：

- `Program.cs` 以 `UseSerilog(SerilogConfigurator.Configure, preserveStaticLogger: true)` 建立主 logger；Console sink 由 `appsettings.json` 的 `Serilog:WriteTo` 宣告，Seq sink 與測試用記憶體 sink（DI 註冊的 `ILogEventSink`）由 `SerilogConfigurator` 以程式碼加入。
- 啟動階段另有 bootstrap logger（`Program.cs` 開頭，只有 Console），`catch` 區塊的 `Log.Fatal(exception, ...)` 走它，不經過主 logger。
- `GlobalExceptionHandler.CreateUnexpectedErrorProblemDetails` 把完整例外傳給 `LogError`；全專案另有二十多處帶例外的 `LogError`／`LogWarning`，框架（ASP.NET Core、Npgsql、EF Core）也會記錄例外。
- Serilog 的 `LogEvent.Exception` 為唯讀，enricher 與 filter 都無法替換例外，只能在 sink 層替換。
- `Microsoft.EntityFrameworkCore.Database.Command` 等類別靠 `appsettings.json` 的 `MinimumLevel.Override` 抑制，框架在建構日誌前會先查 `IsEnabled`，所以最小等級必須仍在最外層生效。

## Goals / Non-Goals

**Goals:**

- 所有日誌來源的例外文字，在抵達任何 sink 前已遮蔽（requirement：日誌中的例外內容須遮蔽敏感資訊）。
- 遮蔽不改變既有日誌等級行為，也不改動現有 log 呼叫端。
- 遮蔽失敗時 fail-safe：不外洩、不丟日誌。

**Non-Goals:**

- 日誌訊息範本屬性值（例如 `{Email}`）的遮蔽；既有 Email 遮蔽規則由 `email-notification` 能力負責。
- 偵測「沒有 key 的裸密碼」。只認指定的格式（見決策 3）。
- 清理已進入 Seq 的歷史日誌。

## Decisions

### 決策 1：以裝飾 sink 在管線最外層遮蔽

新增 `ExceptionMaskingSink : ILogEventSink`，把 `LogEvent` 複製一份（例外換成遮蔽後版本）再轉給內層 logger。內層 logger 包含現有所有 sink：config 宣告的 Console、Seq、測試用記憶體 sink。外層 logger 只保留最小等級與 `MinimumLevel.Override`，唯一的 sink 是 `ExceptionMaskingSink`。

外層是等級的唯一判斷點（過濾後的設定從完整 `IConfiguration` 複製 `Serilog` 區段並排除 `WriteTo`，使環境變數覆寫也生效；外層保留 `Enrich`，內層不重複）：等級設定（`MinimumLevel.Default` 與 `Override`）來自 `appsettings.json` 的 `Serilog` 區段，但以過濾後的 `IConfiguration` 排除 `WriteTo`（否則 Console 會繞過遮蔽；`ReadFrom.Configuration` 沒有排除選項，須自行建立過濾後的設定）。內層 logger 讀取 `WriteTo` 建立 sink 後，將 `MinimumLevel` 設為 `Verbose`，只保留各 sink 自身的 `restrictedToMinimumLevel`（例如 Console 的 Information）。理由：`MinimumLevel.Override` 靠 `ForContext(SourceContext)` 才生效，外層 sink 以 `Write(LogEvent)` 轉發時內層不會套用 override，若內層保留 `Default`，會丟掉外層依 override 放行的低等級事件；`docker-compose.loadtest-measure.yml` 將 `OrderService` 設為 Debug 以讓分段耗時日誌進 Seq，就是會受影響的既有用法。

替代方案與不採用原因：

- **只在 `GlobalExceptionHandler` 遮蔽**：漏掉背景服務、Redis、`UnitOfWork` 等其他呼叫處與框架日誌，缺口只是換位置。
- **包裝 `ILoggerFactory`／MEL provider**：等級判斷可沿用，但 `UseSerilogRequestLogging`、`Log.Fatal` 這類直接使用 Serilog `ILogger` 的路徑不經過，仍會漏。
- **外層 `MinimumLevel.Verbose`、等級交給內層**：EF Core 等框架會對本來被抑制的等級照常建構日誌，是對熱路徑的效能回歸。
- **逐一包裝每個 sink**：config 宣告的 Console 由 Settings 套件建立，無法包裝；改成程式碼宣告會改變 config 驅動的行為。

此做法的細節（外層等級設定如何排除 `WriteTo`、內層與外層重複等級是否有副作用）在 tasks 1.x 以測試先驗證；若驗證不成立，退回到本決策列出的替代方案前須先回到 design 重新決策，不在實作時悄悄改道。

### 決策 2：遮蔽 `exception.ToString()` 的完整文字，並以包裝例外承載

遮蔽對象是 `exception.ToString()`：它已含型別名稱、`Message`、整條 `InnerException` 鏈（`AggregateException` 的內層也在其中）與堆疊。對這段文字做一次遮蔽，一次涵蓋所有層，不需遞迴走訪例外物件。

遮蔽後以 `MaskedException`（自訂例外型別）承載：`ToString()` 回傳遮蔽後文字，另保留 `OriginalTypeName`。Console、Seq 等 sink 都以 `ToString()` 輸出例外，所以輸出內容就是遮蔽後文字。

不重建原型別的例外鏈：任意型別無法安全實例化，且 `ToString()` 一次處理更簡單。

遮蔽結果與原文相同（沒有任何格式命中）時，`LogEvent` 原樣通過，不替換例外物件。理由：維持沒有敏感值時的現有行為，降低既有測試與下游工具（例如依例外型別判斷的程式）的影響（OBS-EXMASK-009）。

### 決策 3：敏感格式集中在單一清單

`SensitiveTextMasker` 持有一份有序的 regex 清單，命中的「值」換成 `***`，key 與其餘文字保留：

| 格式 | 說明 |
| --- | --- |
| `password`／`pwd` 後接 `=` 或 `:` | 值可為單引號、雙引號包住的字串，或（無引號時）到 `;` 或行尾為止，允許含空白 |
| `apikey`／`api-key`／`secret`／`token` 後接 `=` 或 `:` | 規則同上；key 不分大小寫 |
| `Bearer` 後接 token | 到空白為止 |
| JWT | `eyJ` 開頭的三段式字串 |
| `scheme://user:password@host` | 只換 password 段 |

Regex 一律 `IgnoreCase | CultureInvariant`，並設定 match timeout（100 ms）。輸入長度上限 64 KB：超過時保留頭尾各 32 KB，中間以固定截斷標記取代，長度一律以 `string.Length`（UTF-16 字元數）計，1 KB ＝ 1,024 字元，非位元組；頭段上限與尾段長度皆為 32 KB（具名常數，測試依此常數放置敏感值），頭尾兩段**各自獨立**遮蔽後再串接；不超過 64 KB 的文字不經截斷、完全不改動（Scenario 009）（避免跨段的格式被誤判為連續）。頭尾保留的理由：型別名稱與訊息在文字開頭，堆疊在結尾，只保留前綴會在訊息很長時丟掉堆疊。邊界修剪規則：頭段退回最後一個換行之前，沒有換行則退回最後一個空白之前，兩者都沒有則頭段為空；尾段從第一個換行之後開始，沒有換行則尾段為空。只以換行／空白修剪、不以 `;`、`,` 修剪，因為 URL 密碼（`redis://user:abc,def@host`）可含這些字元，在其中切開會留下密碼片段；整段 URL 沒有空白時會被整個丟棄。JWT、Bearer token 等需完整比對的格式，被邊界切開的殘段因此不會輸出。頭段尾端若是未閉合的 `Password=my`，由「無引號 Password 值到分號或行尾」的規則遮蔽。規則不使用巢狀量詞。

key 比對允許可選的前綴詞與引號（涵蓋 `access_token`、`client_secret`、`api_key`、JSON 的 `"password":"x"`）。無引號的 `Password`／`Pwd` 值取到 `;` 或行尾（連線字串允許未加引號的空白）；其他 key 的無引號值以空白、`;`、`,`、`&`、`)`、引號為終止。理由：例外文字可能來自外部輸入（例如請求參數反映在訊息中），要避免 regex 被惡意輸入拖成 ReDoS；timeout 命中走決策 4 的 fail-safe。

已知取捨：key 比對偏寬鬆，`Token: expired` 這類一般英文訊息也會被遮蔽（寧可多遮）；沒有 key 的裸密碼不會被偵測（Non-Goal）。

### 決策 4：遮蔽失敗改輸出只含型別的占位文字

遮蔽過程擲出任何例外（含 `RegexMatchTimeoutException`）時，捕捉後以 `MaskedException` 承載「只含原例外型別名稱」的文字，不含原始訊息、堆疊與其他說明文字，照常轉發；同時向內層另送一筆固定範本的 Warning（`Exception masking failed for {ExceptionType}`，不帶例外文字），可被 Seq 查詢也可測試。不用 `SelfLog`：它是 process 全域且預設未啟用，專案內也沒有啟用，production 不會有獨立訊號。不重擲：Serilog 本身會吞 sink 例外，重擲只會讓該筆日誌消失，且呼叫端無從得知。

這是專案「禁止吞例外」規則下的明確例外邏輯：捕捉後有處理（降級輸出）與記錄（固定範本 Warning），不是靜默失敗。取得文字（`exception.ToString()`，自訂例外的 `Message` 可能擲例外）也在 try 範圍內。

### 決策 8：sink 擁有內層 logger 並負責 Dispose

`ExceptionMaskingSink` 實作 `IDisposable`，Dispose 時 dispose 內層 logger，使 Seq 批次 sink 在關機時 flush 最後一批日誌。不使用 `WriteTo.Logger(...)` 代替（它預設不 dispose 次級 logger）。bootstrap logger 的包裝同理。轉發時以建構子建立新的 `LogEvent`（帶入 `Timestamp`、`Level`、`MessageTemplate`、`Properties`、`TraceId`、`SpanId`），不就地修改原事件，因為其 `Properties` 可能與其他 sink 共用。

不變條件：不得啟用 `UseSerilog` 的 `writeToProviders`，也不得另掛 MEL Console provider，否則例外會經由未遮蔽的 provider 輸出；實作與審查時檢查。

### 決策 5：bootstrap logger 一併納入

`SerilogConfigurator` 新增建立 bootstrap logger 的方法，以同一個 `ExceptionMaskingSink` 包住 Console，`Program.cs` 改呼叫它。理由：啟動階段失敗（連線字串解析、設定錯誤）最可能把連線字串放進例外訊息，且 `Log.Fatal` 只走 bootstrap logger；若不納入，requirement 的「所有日誌來源」不成立。

### 決策 7：以真實輸出目的地驗證，不只看記憶體 sink

記憶體 sink 掛在內層，無法證明 Console 與 Seq 沒有繞過遮蔽。因此驗證分三層：Console 以 `Console.SetOut` 擷取實際輸出；Seq 以測試內的本機 `HttpListener` 假 ingestion 端點收取實際送出的 body；框架來源以框架類別名稱的 `ILogger` 記錄例外。啟動階段以真實 `WebApplicationFactory` 啟動失敗路徑驗證 `Program.cs` 確實接線，不只測工廠方法。這些測試使用 process 全域狀態（`Console`、`Log.Logger`），須放在以 `DisableParallelization = true` 宣告的 xUnit collection（決策 9）。

### 決策 9：全域狀態測試的隔離

使用 `Console`、`Log.Logger` 的測試放進以 `[CollectionDefinition(Name, DisableParallelization = true)]` 宣告的 collection。具名 collection 只保證 collection 內序列，不保證與其他測試類別互斥，必須關閉平行化。

### 決策 6：測試縫隙

`ExceptionMaskingSink` 的建構子接受遮蔽函式（預設為 `SensitiveTextMasker.Mask`），測試傳入會擲例外的函式驗證決策 4（OBS-EXMASK-012）。不另建介面：只有測試需要替換，一個委派參數即可。

## 安全確認

- **輸入驗證**：被處理的文字是例外訊息，可能含外部輸入；以 match timeout 與 fail-safe 處理 ReDoS（決策 3、4）。沒有拼接 SQL 或 shell。
- **資料庫**：不涉及。
- **權限**：不涉及；不新增端點。
- **機敏資訊**：本 change 的目的。限制：只遮蔽指定格式（決策 3 取捨），已進入 Seq 的歷史日誌不處理。

## 介面歸類（DI 與分層）

`ExceptionMaskingSink`、`SensitiveTextMasker`、`MaskedException` 都是 WebApi 層的日誌基礎設施，與 `SerilogConfigurator` 同在 `src/ProjectC.WebApi/Logging/`，不新增跨層介面，也不註冊 DI（由 `SerilogConfigurator` 直接建立）。

## Risks / Trade-offs

- **外層／內層雙 logger 改變等級行為**（例如 `Override` 套用不一致，被抑制的日誌突然出現或預期日誌消失）→ tasks 1.x 先以測試鎖定現行等級行為，再改結構；全套測試重跑。
- **既有測試斷言例外物件**（例如斷言 `LogEvent.Exception` 的型別或參考）在遮蔽命中時會看到 `MaskedException` → 沒命中時原物件通過（決策 2）；現有測試的例外訊息預期不含指定格式，實作時全套測試驗證，若有失敗須逐一判斷是測試過度綁定還是真實行為改變。
- **完整 `ToString()` 的前置成本（接受的限制）**：64 KB 上限只限制 regex 的輸入與輸出，不限制 `exception.ToString()` 本身。理由：(1) 凡是至少有一個內層 sink 會輸出的事件，該 sink 原本就會對同一例外呼叫完整 `ToString()`，遮蔽層最多多一次格式化；唯一新增成本的情境是「事件通過外層等級、但所有內層 sink 都因 `restrictedToMinimumLevel` 或未啟用而丟棄」（例如 `Override` 設 Debug、Console 限制 Information 且 Seq 未啟用），這類事件原本不會被格式化，現在會被格式化一次並遮蔽。此情境只出現在刻意把某來源 `Override` 降到所有 sink 的限制等級以下，且該等級的事件帶有例外時（目前僅 `docker-compose.loadtest-measure.yml` 的 `OrderService: Debug`，量測用途），**不界定該次 `ToString()` 的耗時與字串大小**（regex timeout 與整體處理時間斷言只涵蓋已取得字串後的 `Mask`），此為使用者核准（2026-11-04）的已知限制：僅在刻意把 `Override` 降到所有 sink 限制等級以下時出現，不阻擋實作；若日後該情境進入正式環境，再改為逐 sink 包裝遮蔽；(2) .NET 無法安全中斷同步執行中的 `ToString()`，在 sink 內為它設時限只能靠額外執行緒，會引入比它解決的問題更大的風險；(3) 自訂例外的 `ToString()` 若永不返回，沒有遮蔽層時同樣會卡住原有 sink。處理時間的界線由 regex 的 100 ms timeout 與下方的整體處理時間斷言承擔。
- **超長文字（>64 KB）的取捨**：頭尾各 32 KB 以外的內容被省略，包含位於中間的內部例外型別與訊息；排錯以頭段的外層型別與尾段堆疊為主，已寫入 spec 的保證範圍。
- **過度遮蔽**（決策 3）→ 接受，排錯需要原文時可由堆疊與型別定位。
- **遮蔽不到的格式**（裸密碼、未列入的 key 名稱）→ 清單集中，日後補格式只需新增一筆規則與測試。
- **屬性解構不被遮蔽**：`Log.Error("{@Ex}", ex)` 這類把例外當屬性解構的寫法不經過例外遮蔽（Non-Goal），收尾文件的已知限制須寫明。
- **每筆例外日誌多一次 regex 掃描** → 例外日誌是低頻路徑；沒有例外的日誌不進入遮蔽邏輯。
- **`MaskedException.StackTrace` 與原例外不同** → 堆疊文字已包含在 `ToString()` 內，工具若依 `LogEvent.Exception.StackTrace` 屬性判斷會看到空值；目前沒有此用法（實作時 grep 確認）。

## Migration Plan

無資料或設定遷移。部署即生效；回退為還原 `SerilogConfigurator` 與 `Program.cs` 的 bootstrap 呼叫。

## 收尾

完成後更新 `docs/deployment-prerequisites.md` 第 2 項與 `docs/exception-log-masking-todo.md` 為已完成；`api-health-checks` 已歸檔的 design 不修改，其限期例外因本 change 完成而失效。
