# Tasks

## AC 與測試對應

| Scenario | 測試任務 |
| --- | --- |
| OBS-EXMASK-001 | 4.1、4.4 |
| OBS-EXMASK-002 | 4.2 |
| OBS-EXMASK-003 | 3.1 |
| OBS-EXMASK-004 | 3.1 |
| OBS-EXMASK-005 ~ 008、017 ~ 022 | 2.1 |
| OBS-EXMASK-009 | 2.1、3.1 |
| OBS-EXMASK-010 | 3.1 |
| OBS-EXMASK-011 | 3.1 |
| OBS-EXMASK-012 | 3.2 |
| OBS-EXMASK-013 | 4.1 |
| OBS-EXMASK-014 | 3.1 |
| OBS-EXMASK-015 | 4.3 |
| OBS-EXMASK-016 | 4.4 |

測試指令（容器內）：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~<類別或方法>"`。證據一律貼原文（CLAUDE.md「證據貼原文」）；RED 不得是編譯失敗，所以每組先建立會編譯通過、行為為空的骨架，讓測試因斷言失敗而紅燈。

## 1. 鎖定現行日誌管線行為（結構改動前的基準）

- [ ] 1.1 TDD：先新增特性測試並在現行程式碼上執行，確認通過，作為重構前基準（不是 RED，因為尚未改變行為）。涵蓋：`appsettings` 的 `MinimumLevel.Override` 仍抑制低於門檻的類別（例如 `Microsoft.AspNetCore` 的 Information 不出現）；Information 以上的應用日誌同時抵達 DI 注入的記憶體 sink；Seq sink 在 `Seq:ServerUrl` 有值時仍被加入（沿用 `SeqSinkResilienceTests` 既有手法）；**低於 Default 的 Override**：`Default=Information` 搭配 `Override[ProjectC.X]=Debug` 時，X 類別的 Debug 事件抵達記憶體 sink，且 Console 因 `restrictedToMinimumLevel` 不輸出（對應 `docker-compose.loadtest-measure.yml` 的 OrderService Debug 用法）；`Default=Warning` 搭配 `Override[ProjectC]=Information` 同理。驗證：上述測試在現行程式碼全數通過。
  - 完成紀錄（基準測試命令與每個測試專案摘要行）：
  - 若發現現行行為與 design.md 決策 1 的假設不符，停止並回到 design 重新決策。

- [ ] 1.2 盤點受影響的既有測試：grep `tests/` 中斷言 `LogEvent.Exception`（型別、參考、訊息）的測試，列出清單與各自的例外訊息是否含指定敏感格式。驗證：清單寫在本 task 下；清單內任何會因 `MaskedException` 取代而失敗的測試，在 4.1 前先決定是調整測試還是調整設計。
  - 盤點結果：

## 2. 敏感文字遮蔽器

- [ ] 2.1 TDD（Scenario 005～009）：先建立 `SensitiveTextMasker.Mask(string)` 骨架（原樣回傳），再新增測試：`Pwd=`／`Password=` 保留 key 並遮值（含單雙引號包住的值）、Bearer 與 JWT、`redis://user:pw@host` 只換密碼段、`apikey=`／`SECRET:`／`Token=` 不分大小寫、不含敏感格式的文字完全不變、`Server=db;Port=5432` 等相鄰欄位原樣保留。預期 RED：斷言輸出不含 `s3cret-pw` 失敗，實際輸出仍含明文。另含：`Password: s3cret-pw`（Scenario 017）；JSON 與前綴 key（Scenario 018）；含空白的連線字串密碼（Scenario 019）；超過 64 KB 的文字，64 KB 之後的明文不輸出且結尾含截斷標記（Scenario 020）；敏感值跨越 64 KB 邊界：JWT 起於邊界前、延伸到邊界後，以及含空白的 `Password=my s3cret pw` 被邊界切在中間，輸出都不含該 JWT 的任何片段、不含 `my`／`s3cret`／`pw` 的任何一段，且結尾含截斷標記（Scenario 021、022）；公開的格式清單中每條 regex 的 `MatchTimeout` 皆不是 `Regex.InfiniteMatchTimeout` 且不大於 100 ms；以確定性的病態輸入（針對本清單實際 regex 構造，實作時於 design 補上案例）呼叫 `Mask`，以 `Stopwatch` 斷言在上限內回傳或擲 `RegexMatchTimeoutException`。最小實作使其通過；格式清單集中為單一有序清單（以唯讀集合公開供測試檢查），每條 regex 設 match timeout。驗證：該測試類別全數通過。
  - RED 紀錄（命令、失敗測試名稱與 Expected／Actual 原文，需標明在修改正式程式碼前）：
  - GREEN 紀錄：
  - 重構後重跑紀錄（或 N/A）：

## 3. 例外承載與遮蔽 sink

- [ ] 3.1 TDD（Scenario 003、004、009、010、011、014）：先建立 `MaskedException` 與 `ExceptionMaskingSink` 骨架（原樣轉發），再新增測試：以測試用 sink 接收轉發結果，斷言 (a0) `MaskedException.Message` 與 `ToString()` 都不含明文、`InnerException` 為 null、不持有原例外參考；(a1) 轉發的是新建的 `LogEvent`，原事件的 `Properties` 未被就地修改；(a) 內部例外鏈任一層與 `AggregateException` 內層的明文被遮蔽；(b) 輸出文字仍含例外型別名稱、拋出方法的堆疊位置，且外層型別在內層之前；(c) 例外文字沒有命中時，轉發的 `LogEvent` 與例外物件是原物件（參考相等）；(d) 命中時 `{OrderId}` 等屬性與渲染訊息不變。預期 RED：斷言輸出不含明文失敗（骨架原樣轉發）。最小實作：對 `exception.ToString()` 遮蔽，命中才以 `MaskedException` 取代（`ToString()` 回傳遮蔽後文字，保留 `OriginalTypeName`）。驗證：該測試類別全數通過。
  - RED 紀錄：
  - GREEN 紀錄：
  - 重構後重跑紀錄（或 N/A）：
- [ ] 3.2 TDD（Scenario 012）：新增測試，`ExceptionMaskingSink` 建構時傳入會擲例外的遮蔽函式（含一個擲 `RegexMatchTimeoutException` 的案例），斷言該筆日誌仍被轉發、例外文字只含原型別名稱、不含原始訊息與堆疊，且呼叫端 `Emit` 不擲例外；另含 `ToString()` 本身擲例外的例外類別案例；另有一筆固定範本 `Exception masking failed for {ExceptionType}` 的 Warning 被轉發，其渲染文字與屬性都不含原例外訊息。預期 RED：骨架下遮蔽函式的例外直接外洩，或輸出仍含原始訊息、沒有 Warning。最小實作：捕捉遮蔽例外、降級為占位 `MaskedException`、向內層送 Warning。另測 `ExceptionMaskingSink.Dispose` 會 dispose 內層 logger（以可偵測 dispose 的內層 sink 斷言）。驗證：上述測試通過。
  - RED 紀錄：
  - GREEN 紀錄：
  - 重構後重跑紀錄（或 N/A）：

## 4. 接上日誌管線與 bootstrap logger

- [ ] 4.0 TDD：先新增單元測試，驗證外層設定的建立方式：給定含 `MinimumLevel`、`Override` 與 `WriteTo: Console` 的設定，外層只有 `ExceptionMaskingSink` 一個 sink（以測試用方式列舉或以輸出行為斷言 Console 不會收到未遮蔽事件）、等級與 override 仍生效，且內層 `MinimumLevel` 為 `Verbose`。並含以環境變數式設定覆寫 `Override` 的案例（例如 `Serilog:MinimumLevel:Override:X`）。實作過濾後 `IConfiguration`（從完整設定複製 `Serilog` 區段並排除 `WriteTo`）的建立。預期 RED：骨架下外層仍讀到 `WriteTo` 或內層等級過濾掉低於 Default 的 override 事件。驗證：測試通過，且 1.1 基準測試（含低於 Default 的 override 案例）仍全數通過。
  - RED 紀錄：
  - GREEN 紀錄：

- [ ] 4.1 TDD（Scenario 001、013）：新增整合測試，沿用 `LogCapturingWebApplicationFactory` 與 `InMemoryLogEventSink`，替換一個會擲出訊息含 `Password=s3cret-pw` 的例外的端點依賴，呼叫後斷言：該請求期間記憶體 sink 收到的「所有帶例外的 LogEvent」（先斷言集合非空且至少含 `GlobalExceptionHandler` 來源的一筆，且 `TraceId` 屬性仍存在）其例外文字都不含 `s3cret-pw`，且 `GlobalExceptionHandler` 那筆含 `Password=***` 與例外型別名稱；500 回應本文欄位集合恰為 `status`、`title`、`traceId`，且整段回應不含 `s3cret-pw`。預期 RED：`ExceptionMaskingSink` 尚未接上，日誌例外文字仍含明文。實作：調整 `SerilogConfigurator`，外層 logger 保留等級設定並只掛 `ExceptionMaskingSink`，內層 logger 掛 Console、Seq 與注入的 sink（design 決策 1）。驗證：該測試通過，且任務 1.1 的基準測試仍全數通過（等級行為未變）。
  - RED 紀錄：
  - GREEN 紀錄（含 1.1 基準測試重跑）：
  - 還原驗證紀錄（`bash scripts/verify-test-evidence.sh restore` 暫時還原 `SerilogConfigurator` 變更後測試失敗的命令與輸出）：
- [ ] 4.2 TDD（Scenario 002）：新增整合測試，以背景服務路徑（例如讓 `ExpiredOrderCleanupService` 的依賴擲出含明文密碼的例外）觸發錯誤日誌，斷言記憶體 sink 收到的例外文字不含明文。預期 RED：若 4.1 的接線被還原則失敗；此測試證明遮蔽不只作用於 HTTP 路徑。驗證：通過。
  - RED／還原驗證紀錄：
  - GREEN 紀錄：
- [ ] 4.3 TDD（Scenario 015）：`SerilogConfigurator` 新增建立 bootstrap logger 的方法（以 `ExceptionMaskingSink` 包住 Console），`Program.cs` 改呼叫它。測試走真實啟動路徑，證明 `Program.cs` 確實接線：在以 `[CollectionDefinition(Name, DisableParallelization = true)]` 宣告的 collection 內，先 `Console.SetOut` 到 `StringWriter`，再用 `WebApplicationFactory` 啟動，並在其 `ConfigureServices` 擲出訊息含 `Password=s3cret-pw` 的例外，使啟動在 `Program.cs` 的 `try` 區塊內失敗、走到 `Log.Fatal(exception, "Application terminated unexpectedly")`；斷言擷取到的 Console 輸出含 "Application terminated unexpectedly"（證明走到該路徑）、含 `Password=***`、不含 `s3cret-pw`。預期 RED：`Program.cs` 仍使用原本只有 Console 的 bootstrap logger，輸出含明文。驗證：該測試通過，且 `docker compose up` 啟動 api 後 `docker compose logs api` 仍顯示 "Starting up"。
  - 若 `Log.Logger` 的 process 全域特性使此測試無法穩定執行，停止並回報，改在 design.md 提請使用者核准替代驗證例外，不得改成只測工廠方法。
  - RED 紀錄：
  - GREEN 紀錄：
  - 啟動日誌檢查輸出：
  - 還原驗證紀錄（暫時還原 `Program.cs` 的 bootstrap 呼叫後測試失敗的命令與輸出）：
- [ ] 4.4 TDD（Scenario 001、016）：新增測試證明遮蔽涵蓋 Console、Seq 與框架來源，而不只記憶體 sink。(a) Console：在不平行執行的 collection 內 `Console.SetOut` 到 `StringWriter`，以 `SerilogConfigurator.Configure` 搭配含 `Console` 的 `Serilog:WriteTo` 設定建立 logger，記錄含明文的例外後 dispose 並斷言輸出不含明文；(b) Seq：`Seq:ServerUrl` 指向測試內的本機 `HttpListener` 假 ingestion 端點，記錄後 dispose logger 強制 flush，斷言收到的 request body 不含明文；(c) 框架來源：以 `ILoggerFactory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics")` 記錄含明文的例外，斷言記憶體 sink 與 Console 輸出都不含明文。預期 RED：外層若仍帶 config 宣告的 Console，或 Seq 掛在遮蔽層之外，對應斷言失敗。驗證：三組測試通過。
  - RED 紀錄：
  - GREEN 紀錄：
  - 還原驗證紀錄：

- [ ] 4.5 檢查不變條件：`Program.cs` 與 `src/` 沒有啟用 `writeToProviders`、`AddConsole` 或 `ClearProviders` 之外的另一條 MEL Console 輸出路徑（design 決策 8）。驗證：grep 結果貼在本 task 下；host 關機時 Seq 最後一批日誌的 flush 路徑（外層 logger 被 dispose）於此一併確認並記錄。
  - 檢查結果：

## 5. 防禦性最終檢查

- [ ] 5.1 hardener 最終檢查：程式碼全部完成後，套用 `.claude/skills/hardener/SKILL.md` 檢查清單於 `ExceptionMaskingSink`、`SensitiveTextMasker`（參數 null 處理、遮蔽失敗邊界、`LogEvent` 欄位複製是否完整保留 `Timestamp`／`Level`／`MessageTemplate`／`Properties`／`TraceId`／`SpanId`、執行緒安全、日誌遮蔽）。發現缺口依 TDD 先補 RED 測試再修改；無缺口則記錄結論。驗證：結果寫在本 task 下。
  - 檢查結果：

## 6. 文件收尾與全套測試

- [ ] 6.1 更新 `docs/deployment-prerequisites.md` 第 2 項與 `docs/exception-log-masking-todo.md` 為已完成（寫明完成的 change 與遮蔽範圍、已知限制：只認指定格式、不處理日誌屬性值與歷史日誌）。驗證：兩份文件內容與 design.md 決策 3 的格式清單一致，TDD 為 N/A（純文件）。
- [ ] 6.2 容器內重跑全套測試（所有測試專案），確認沒有既有測試因 `MaskedException` 取代而失敗；若有失敗，逐一判斷是測試過度綁定或真實行為改變並記錄。驗證：每個測試專案的摘要行貼原文。
  - 全套測試輸出：
- [ ] 6.3 `graphify update .` 更新圖譜。驗證：指令成功結束。

## Workflow follow-up

- spec-reviewer 通過後暫停，等待使用者交叉審查並明確指示才開始實作。
- 實作完成後依 CLAUDE.md 依序執行 strict-reviewer 與全新的對抗式 subagent，直到兩者都沒有 blocking。
- 無前端變更，略過真實瀏覽器驗證。
- 使用者同意後 `/opsx:archive` 並合併回 master；任何步驟都不自動 commit。
