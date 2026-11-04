# Tasks

## AC 與測試對應

| Scenario | 測試任務 |
| --- | --- |
| OBS-EXMASK-001 | 4.1、4.4 |
| OBS-EXMASK-002 | 4.2 |
| OBS-EXMASK-003 | 3.1 |
| OBS-EXMASK-004 | 3.1 |
| OBS-EXMASK-005 ~ 008、017 ~ 024 | 2.1 |
| OBS-EXMASK-009 | 2.1、3.1 |
| OBS-EXMASK-010 | 3.1 |
| OBS-EXMASK-011 | 3.1 |
| OBS-EXMASK-012 | 3.2 |
| OBS-EXMASK-013 | 4.1 |
| OBS-EXMASK-014 | 3.1 |
| OBS-EXMASK-015 | 4.3 |
| OBS-EXMASK-016 | 4.4 |
| OBS-EXMASK-025、026 | 6.5 |
| OBS-EXMASK-027、028 | 6.6、6.7 |

測試指令（容器內）：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~<類別或方法>"`。證據一律貼原文（CLAUDE.md「證據貼原文」）；RED 不得是編譯失敗，所以每組先建立會編譯通過、行為為空的骨架，讓測試因斷言失敗而紅燈。

## 1. 鎖定現行日誌管線行為（結構改動前的基準）

- [x] 1.1 TDD：先新增特性測試並在現行程式碼上執行，確認通過，作為重構前基準（不是 RED，因為尚未改變行為）。涵蓋：`appsettings` 的 `MinimumLevel.Override` 仍抑制低於門檻的類別（例如 `Microsoft.AspNetCore` 的 Information 不出現）；Information 以上的應用日誌同時抵達 DI 注入的記憶體 sink；Seq sink 在 `Seq:ServerUrl` 有值時仍被加入（沿用 `SeqSinkResilienceTests` 既有手法）；**低於 Default 的 Override**：`Default=Information` 搭配 `Override[ProjectC.X]=Debug` 時，X 類別的 Debug 事件抵達記憶體 sink，且 Console 因 `restrictedToMinimumLevel` 不輸出（對應 `docker-compose.loadtest-measure.yml` 的 OrderService Debug 用法）；`Default=Warning` 搭配 `Override[ProjectC]=Information` 同理。驗證：上述測試在現行程式碼全數通過。

  **證據（1.1）**：新增 `tests/ProjectC.WebApi.Tests/Observability/LogPipelineCharacterizationTests.cs`（5 個特性測試，現行程式碼上執行，非 RED）與 `TestSupport/FakeSeqIngestionServer.cs`、`TestSupport/GlobalLoggingStateGroup.cs`。命令：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~LogPipelineCharacterizationTests"`
  ```
  Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 149 ms - ProjectC.WebApi.Tests.dll (net10.0)
  ```
  - 完成紀錄（基準測試命令與每個測試專案摘要行）：
  - 若發現現行行為與 design.md 決策 1 的假設不符，停止並回到 design 重新決策。

- [x] 1.2 盤點受影響的既有測試：grep `tests/` 中斷言 `LogEvent.Exception`（型別、參考、訊息）的測試，列出清單與各自的例外訊息是否含指定敏感格式。驗證：清單寫在本 task 下；清單內任何會因 `MaskedException` 取代而失敗的測試，在 4.1 前先決定是調整測試還是調整設計。

  **清單（1.2）**：`grep -rn ".Exception" tests` 結果分三類：(1) 使用 MEL 假 logger（`_logger.Entries`／`ExceptionHandlerLogger.Entries`）者不經 Serilog，不受影響：`GlobalExceptionHandlerTests`、`OrganizerScopingDataCorruptionTests`、`TicketHolderDataCorruptionTests`、`UnitOfWork*Tests`、`OrderServiceNotificationTests`、`OrderServiceTests`；(2) 經 Serilog 記憶體 sink、斷言例外型別：`QueryCachingFailOpenTests`（`RedisConnectionException`、`JsonException`）— 例外訊息不含指定敏感格式，沒命中時原物件通過（決策 2），不受影響，由 6.2 全套測試驗證；(3) 斷言 `Exception.ToString()` 含／不含 marker：`HealthCheckLoggingTests`、`RealNameLoggingTests`、`RequestBodyLimitsKestrelTests` — marker 與 sentinel 不含指定敏感格式，同樣由 6.2 驗證。結論：無需先調整測試或設計。
  - 盤點結果：

## 2. 敏感文字遮蔽器

- [x] 2.1 TDD（Scenario 005～009；009 僅限不超過 64 KB 的文字；KB 以 `string.Length` 計，測試輸入以字元數建構，另含一個以全形中文字組成、字元數未超過上限的案例斷言不截斷）：先建立 `SensitiveTextMasker.Mask(string)` 骨架（原樣回傳），再新增測試：`Pwd=`／`Password=` 保留 key 並遮值（含單雙引號包住的值）、Bearer 與 JWT、`redis://user:pw@host` 只換密碼段、`apikey=`／`SECRET:`／`Token=` 不分大小寫、不含敏感格式的文字完全不變、`Server=db;Port=5432` 等相鄰欄位原樣保留。預期 RED：斷言輸出不含 `s3cret-pw` 失敗，實際輸出仍含明文。另含：`Password: s3cret-pw`（Scenario 017）；JSON 與前綴 key（Scenario 018）；含空白的連線字串密碼（Scenario 019）；超過 64 KB 的文字，64 KB 之後的明文不輸出且輸出含截斷標記（Scenario 020）；敏感值跨越頭段上限（第 32 KB）：測試輸入以總長超過 64 KB 的文字，將敏感值放在頭段上限前後；另測敏感值位於尾段起點之前被省略區、與尾段起點被切開的情況；JWT 起於邊界前、延伸到邊界後，以及含空白的 `Password=my s3cret pw` 被邊界切在中間，輸出都不含該 JWT 的任何連續 10 個以上字元片段、不含緊接在 `Password=` 之後的 `my`／`s3cret`／`pw`，且輸出含截斷標記（Scenario 021、022）；含逗號的 URL 密碼 `redis://user:abc,def@cache:6379` 被頭段邊界切在 `def` 中，輸出不含 `abc`（Scenario 024）；訊息超過 64 KB 的例外，堆疊在訊息之後，輸出仍含型別名稱與至少一個堆疊框架（Scenario 023）；超長文字中段的內部例外型別可被省略，測試只斷言頭段型別與尾段堆疊存在，不斷言中段型別；頭尾各自獨立遮蔽，並以斷言驗證頭段在換行／空白退回、尾段從第一個換行之後開始、無換行時該段為空；公開的格式清單中每條 regex 的 `MatchTimeout` 皆不是 `Regex.InfiniteMatchTimeout` 且不大於 100 ms；以確定性的病態輸入（針對本清單實際 regex 構造，實作時於 design 補上案例）呼叫 `Mask`，以 `Stopwatch` 斷言在上限內回傳或擲 `RegexMatchTimeoutException`；另以 64 KB 的病態輸入呼叫 `Mask`（頭尾兩段、全部 regex 逐一處理），斷言整體處理時間不超過 3 秒（約為 regex 數 × 100 ms × 兩段的上界）。最小實作使其通過；格式清單集中為單一有序清單（以唯讀集合公開供測試檢查），每條 regex 設 match timeout。驗證：該測試類別全數通過。

  **證據（2.1）**
  - RED（骨架 `Mask` 原樣回傳、`Patterns` 為空；編譯通過，斷言失敗；正式程式碼尚未實作）。命令：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~SensitiveTextMaskerTests"`
  ```
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_UrlPasswordWithCommaCutByHeadLimit_OutputsNoPasswordFragment [29 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_KeyValueSecrets_RemovesValueCaseInsensitively(input: "\"client_secret\": \"s3cret-pw\"", plaintext: "s3cret-pw") [2 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_KeyValueSecrets_RemovesValueCaseInsensitively(input: "X-Api-Key: s3cret-pw", plaintext: "s3cret-pw") [1 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_KeyValueSecrets_RemovesValueCaseInsensitively(input: "access_token=s3cret-pw", plaintext: "s3cret-pw") [3 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_KeyValueSecrets_RemovesValueCaseInsensitively(input: "SECRET: k-456", plaintext: "k-456") [3 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_KeyValueSecrets_RemovesValueCaseInsensitively(input: "{\"password\":\"s3cret-pw\"}", plaintext: "s3cret-pw") [4 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_KeyValueSecrets_RemovesValueCaseInsensitively(input: "apikey=k-123", plaintext: "k-123") [2 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_KeyValueSecrets_RemovesValueCaseInsensitively(input: "Token=k-789", plaintext: "k-789") [2 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_OversizedTextWithoutNewline_HeadBacksOffToLastSpaceAndTailIsEmpty [7 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_OversizedMessageKeepsTypeNameAndStackFrames [4 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_JsonPassword_KeepsJsonShape [2 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_UrlEmbeddedPassword_ReplacesOnlyPasswordSegment [2 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_ConnectionStringPassword_MasksValueAndKeepsKeyAndNeighbours(input: "Pool=true;Pwd=s3cret-pw;Port=5432", expected: "Pool=true;Pwd=***;Port=5432") [2 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_ConnectionStringPassword_MasksValueAndKeepsKeyAndNeighbours(input: "Password: s3cret-pw", expected: "Password: ***") [2 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_ConnectionStringPassword_MasksValueAndKeepsKeyAndNeighbours(input: "Server=db;Password=s3cret-pw;Port=5432", expected: "Server=db;Password=***;Port=5432") [2 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_ConnectionStringPassword_MasksValueAndKeepsKeyAndNeighbours(input: "Host=db;Password=\"s3cret-pw\";Port=5432", expected: "Host=db;Password=\"***\";Port=5432") [2
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_ConnectionStringPassword_MasksValueAndKeepsKeyAndNeighbours(input: "Pool=true;Password=my s3cret pw;Port=5432", expected: "Pool=true;Password=***;Port=5432") [1 
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_ConnectionStringPassword_MasksValueAndKeepsKeyAndNeighbours(input: "Host=db;PASSWORD='s3cret-pw';Port=5432", expected: "Host=db;PASSWORD='***';Port=5432") [2 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_OversizedText_HeadBacksOffToLastNewlineBeforeLimit [4 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_SecretStraddlingTailStart_OutputsNoSecretFragment [3 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_TextOneCharOverLimit_IsTruncatedWithMarker [3 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_NullText_Throws [6 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_SecretInOmittedMiddleOfOversizedText_IsNotOutputAndMarkerPresent [3 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_BearerAndJwt_RemovesTokenText(input: "token was eyJhbGciOi.eyJzdWIiOi.c2lnbmF0dXJl here", token: "eyJhbGciOi.eyJzdWIiOi.c2lnbmF0dXJl") [2 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_BearerAndJwt_RemovesTokenText(input: "Authorization: Bearer abc.def.ghi", token: "abc.def.ghi") [1 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_JwtStraddlingHeadLimit_OutputsNoFragmentOfJwt [4 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_OversizedText_TailStartsAfterFirstNewlineWithinTailWindow [4 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Patterns_EachHasBoundedMatchTimeout [4 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_SpacedPasswordCutByHeadLimit_OutputsNoPasswordWordsAfterKey [5 ms]
    Failed ProjectC.WebApi.Tests.Logging.SensitiveTextMaskerTests.Mask_OversizedTextWithoutWhitespace_HeadAndTailAreEmpty [2 ms]
  Failed!  - Failed:    30, Passed:    13, Skipped:     0, Total:    43, Duration: 149 ms - ProjectC.WebApi.Tests.dll (net10.0)
  --- 代表性失敗訊息 ---
     Expected SensitiveTextMasker.Mask(input) to be a match with the expectation, but it differs at index 10:
     Expected SensitiveTextMasker.Mask(input) to be a match with the expectation, but it differs at index 14:
     Expected SensitiveTextMasker.Mask(input) to be a match with the expectation, but it differs at index 18:
     Expected SensitiveTextMasker.Mask(input) to be a match with the expectation, but it differs at index 19:
     Expected SensitiveTextMasker.Patterns not to be empty.
     Expected a <System.ArgumentNullException> to be thrown, but no exception was thrown.
  ```
  - GREEN（實作 `SensitiveTextMasker` 後同一命令）：
  ```
  Passed!  - Failed:     0, Passed:    43, Skipped:     0, Total:    43, Duration: 115 ms - ProjectC.WebApi.Tests.dll (net10.0)
  ```
  - 重構：N/A（無需重構）。RED 還原驗證：本 task 為新增型別，無既有行為可還原，N/A。


## 3. 例外承載與遮蔽 sink

- [x] 3.1 TDD（Scenario 003、004、009、010、011、014）：先建立 `MaskedException` 與 `ExceptionMaskingSink` 骨架（原樣轉發），再新增測試：以測試用 sink 接收轉發結果，斷言 (a0) `MaskedException.Message` 與 `ToString()` 都不含明文、`InnerException` 為 null、不持有原例外參考；(a1) 轉發的是新建的 `LogEvent`，原事件的 `Properties` 未被就地修改；(a) 內部例外鏈任一層與 `AggregateException` 內層的明文被遮蔽；(b) 輸出文字仍含例外型別名稱、拋出方法的堆疊位置，且外層型別在內層之前；(c) 例外文字沒有命中時，轉發的 `LogEvent` 與例外物件是原物件（參考相等）；(d) 命中時 `{OrderId}` 等屬性與渲染訊息不變。預期 RED：斷言輸出不含明文失敗（骨架原樣轉發）。最小實作：對 `exception.ToString()` 遮蔽，命中才以 `MaskedException` 取代（`ToString()` 回傳遮蔽後文字，保留 `OriginalTypeName`）。驗證：該測試類別全數通過。

  **證據（3.1）**
  - RED（骨架原樣轉發；編譯通過，斷言失敗）。命令：`docker compose exec api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ExceptionMaskingSinkTests"`
  ```
    Failed ProjectC.WebApi.Tests.Logging.ExceptionMaskingSinkTests.Emit_WhenMasked_ReplacementHasNoPlaintextNoInnerExceptionAndNoOriginalReference [30 ms]
    Failed ProjectC.WebApi.Tests.Logging.ExceptionMaskingSinkTests.Emit_WhenMasked_KeepsTypeNameAndThrowingMethodInStack [8 ms]
    Failed ProjectC.WebApi.Tests.Logging.ExceptionMaskingSinkTests.Emit_WhenMasked_DoesNotModifyOriginalEventProperties [10 ms]
    Failed ProjectC.WebApi.Tests.Logging.ExceptionMaskingSinkTests.Emit_SecretInInnerExceptionChain_IsMaskedAtEveryLevel [2 ms]
    Failed ProjectC.WebApi.Tests.Logging.ExceptionMaskingSinkTests.Emit_SecretInAggregateExceptionInner_IsMasked [2 ms]
  Failed!  - Failed:     5, Passed:     6, Skipped:     0, Total:    11, Duration: 82 ms - ProjectC.WebApi.Tests.dll (net10.0)
     Expected type to be ProjectC.WebApi.Logging.MaskedException, but found System.InvalidOperationException.
     Expected ForwardedText(capture) "System.InvalidOperationException: Password=s3cret-pw
  ```
  - GREEN：
  ```
  Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11, Duration: 55 ms - ProjectC.WebApi.Tests.dll (net10.0)
  ```
  - 重構：N/A。注意：Emit_WhenMasked_OuterTypeAppearsBeforeInnerType、Emit_WhenMasked_PreservesTraceAndSpanIds 在骨架下即通過（原事件原樣轉發），作為實作後的回歸保護，非 RED 證據。

- [x] 3.2 TDD（Scenario 012）：新增測試，`ExceptionMaskingSink` 建構時傳入會擲例外的遮蔽函式（含一個擲 `RegexMatchTimeoutException` 的案例），斷言該筆日誌仍被轉發、例外文字只含原型別名稱、不含原始訊息與堆疊，且呼叫端 `Emit` 不擲例外；另含 `ToString()` 本身擲例外的例外類別案例；另有一筆固定範本 `Exception masking failed for {ExceptionType}` 的 Warning 被轉發，其渲染文字與屬性都不含原例外訊息。預期 RED：骨架下遮蔽函式的例外直接外洩，或輸出仍含原始訊息、沒有 Warning。最小實作：捕捉遮蔽例外、降級為占位 `MaskedException`、向內層送 Warning。另測 `ExceptionMaskingSink.Dispose` 會 dispose 內層 logger（以可偵測 dispose 的內層 sink 斷言）。驗證：上述測試通過。

  **證據（3.2）**
  - RED（3.1 實作後、尚無 fallback 與 Dispose）。同命令：
  ```
    Failed ProjectC.WebApi.Tests.Logging.ExceptionMaskingSinkTests.Dispose_DisposesInnerLogger [30 ms]
     Expected disposable.IsDisposed to be True, but found False.
    Failed ProjectC.WebApi.Tests.Logging.ExceptionMaskingSinkTests.Emit_WhenMaskingFails_ForwardsTypeOnlyPlaceholderAndFixedTemplateWarning(failingMasker: Func`2 { Method = System.String <FailingMaskers
     Did not expect any exception, but found System.InvalidOperationException: masker bug: System.InvalidOperationException: Password=s3cret-pw
    Failed ProjectC.WebApi.Tests.Logging.ExceptionMaskingSinkTests.Emit_WhenMaskingFails_ForwardsTypeOnlyPlaceholderAndFixedTemplateWarning(failingMasker: Func`2 { Method = System.String <FailingMaskers
     Did not expect any exception, but found System.Text.RegularExpressions.RegexMatchTimeoutException: The Regex engine has timed out while trying to match a pattern to an input string. This can occur 
    Failed ProjectC.WebApi.Tests.Logging.ExceptionMaskingSinkTests.Emit_WhenExceptionToStringThrows_ForwardsPlaceholderWithoutThrowing [3 ms]
     Did not expect any exception, but found System.InvalidOperationException: ToString failed
  Failed!  - Failed:     4, Passed:    11, Skipped:     0, Total:    15, Duration: 85 ms - ProjectC.WebApi.Tests.dll (net10.0)
  ```
  - GREEN（含 SensitiveTextMaskerTests 一併重跑）：
  ```
  Passed!  - Failed:     0, Passed:    58, Skipped:     0, Total:    58, Duration: 117 ms - ProjectC.WebApi.Tests.dll (net10.0)
  ```
  - 重構：N/A。


## 4. 接上日誌管線與 bootstrap logger

- [x] 4.0 TDD：先新增單元測試，驗證外層設定的建立方式：給定含 `MinimumLevel`、`Override` 與 `WriteTo: Console` 的設定，外層只有 `ExceptionMaskingSink` 一個 sink（以測試用方式列舉或以輸出行為斷言 Console 不會收到未遮蔽事件）、等級與 override 仍生效，且內層 `MinimumLevel` 為 `Verbose`。並含以環境變數式設定覆寫 `Override` 的案例（例如 `Serilog:MinimumLevel:Override:X`）。實作過濾後 `IConfiguration`（從完整設定複製 `Serilog` 區段並排除 `WriteTo`）的建立。預期 RED：骨架下外層仍讀到 `WriteTo` 或內層等級過濾掉低於 Default 的 override 事件。驗證：測試通過，且 1.1 基準測試（含低於 Default 的 override 案例）仍全數通過。另含：Override 設 Debug、Console 限制 Information、Seq 未啟用時，帶例外的 Debug 事件不產生任何輸出、遮蔽層不擲例外（驗證此情境的行為正確，成本為接受的限制，見 design 風險）。
  - RED 紀錄：命令 `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~SerilogConfiguratorMaskingTests"`（正式程式碼尚未修改；失敗在目標行為斷言，非編譯／環境錯誤）：
    ```
    Failed ...SerilogConfiguratorMaskingTests.Configure_ErrorWithSecretInException_ConsoleOutputHasNoPlaintext [26 ms]
    Expected _consoleOut.ToString() "[19:10:21 ERR] boom
    System.InvalidOperationException: Password=s3cret-pw
    " to contain "Password=***".
    Failed ...SerilogConfiguratorMaskingTests.Configure_DebugOverrideWithExceptionAndConsoleRestricted_ProducesNoConsoleOutputAndDoesNotThrow [5 ms]
    Did not expect memorySink.Events.Single().Exception!.ToString() "System.InvalidOperationException: Password=s3cret-pw" to contain "s3cret-pw".
    Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3, Duration: 168 ms - ProjectC.WebApi.Tests.dll (net10.0)
    ```
  - GREEN 紀錄：實作 `SerilogConfigurator.Configure` 外層／內層結構與過濾後設定（`BuildSection`）後，同一命令加上 `|FullyQualifiedName~LogPipelineCharacterizationTests`（含 1.1 基準五項）：
    ```
    Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8, Duration: 167 ms - ProjectC.WebApi.Tests.dll (net10.0)
    ```
  - 重構：N/A（結構單純）。

- [x] 4.1 TDD（Scenario 001、013）：新增整合測試，沿用 `LogCapturingWebApplicationFactory` 與 `InMemoryLogEventSink`，替換一個會擲出訊息含 `Password=s3cret-pw` 的例外的端點依賴，呼叫後斷言：該請求期間記憶體 sink 收到的「所有帶例外的 LogEvent」（先斷言集合非空且至少含 `GlobalExceptionHandler` 來源的一筆，且 `TraceId` 屬性仍存在）其例外文字都不含 `s3cret-pw`，且 `GlobalExceptionHandler` 那筆含 `Password=***` 與例外型別名稱；500 回應本文欄位集合恰為 `status`、`title`、`traceId`，且整段回應不含 `s3cret-pw`。預期 RED：`ExceptionMaskingSink` 尚未接上，日誌例外文字仍含明文。實作：調整 `SerilogConfigurator`，外層 logger 保留等級設定並只掛 `ExceptionMaskingSink`，內層 logger 掛 Console、Seq 與注入的 sink（design 決策 1）。驗證：該測試通過，且任務 1.1 的基準測試仍全數通過（等級行為未變）。
  - RED 紀錄（還原驗證，`bash scripts/verify-test-evidence.sh restore --path src/ProjectC.WebApi/Logging/SerilogConfigurator.cs -- docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ExceptionMaskingPipelineTests|FullyQualifiedName~SerilogConfiguratorMaskingTests|FullyQualifiedName~LogPipelineCharacterizationTests"`；4.0 已先完成接線，故 4.1 的 RED 以還原驗證取得）：
    ```
    Failed ...ExceptionMaskingPipelineTests.GetEvents_WhenDependencyThrowsWithPassword_LogsMaskedExceptionAndKeepsResponseShape
      ...Exception!.ToString() "...Password=s3cret-pw..." to contain "s3cret-pw" (AllSatisfy, ExceptionMaskingPipelineTests.cs:line 45)
    Failed ...SerilogConfiguratorMaskingTests.Configure_ErrorWithSecretInException_ConsoleOutputHasNoPlaintext
      Expected _consoleOut.ToString() "[19:12:23 ERR] boom
      System.InvalidOperationException: Password=s3cret-pw
      " to contain "Password=***".
    Failed ...SerilogConfiguratorMaskingTests.Configure_DebugOverrideWithExceptionAndConsoleRestricted_ProducesNoConsoleOutputAndDoesNotThrow
      Did not expect memorySink.Events.Single().Exception!.ToString() "System.InvalidOperationException: Password=s3cret-pw" to contain "s3cret-pw".
    Failed!  - Failed:     3, Passed:     6, Skipped:     0, Total:     9, Duration: 1 s - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    $ git stash pop  → Dropped refs/stash@{0} (ec1d980924fe564561ad543fb8d70123520e987c)；SerilogConfigurator.cs 恢復為 modified
    ```
  - GREEN 紀錄（還原後同一過濾條件，含 1.1 基準五項）：
    ```
    Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 939 ms - ProjectC.WebApi.Tests.dll (net10.0)
    ```
    （此次過濾為 ExceptionMaskingPipelineTests + LogPipelineCharacterizationTests；SerilogConfiguratorMaskingTests 3 項見 4.0 的 8/8。）
  - 重構：N/A。
- [x] 4.2 TDD（Scenario 002）：新增整合測試，以背景服務路徑（例如讓 `ExpiredOrderCleanupService` 的依賴擲出含明文密碼的例外）觸發錯誤日誌，斷言記憶體 sink 收到的例外文字不含明文。預期 RED：若 4.1 的接線被還原則失敗；此測試證明遮蔽不只作用於 HTTP 路徑。驗證：通過。
  - RED／還原驗證紀錄（`bash scripts/verify-test-evidence.sh restore --path src/ProjectC.WebApi/Logging/SerilogConfigurator.cs -- docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ExceptionMaskingPipelineTests.ExpiredOrderCleanupService"`）：
    ```
    $ git stash push -u -- src/ProjectC.WebApi/Logging/SerilogConfigurator.cs
    $ git stash list
    stash@{0}: WIP on feature/exception-log-masking: 8b49ec7 補充交叉審查流程待觀察項目
      Failed ProjectC.WebApi.Tests.Logging.ExceptionMaskingPipelineTests.ExpiredOrderCleanupService_WhenCycleFailsWithPassword_LogsMaskedException [315 ms]
      Error Message:
       Expected cycleFailure.Exception!.ToString() "System.InvalidOperationException: Host=db;Password=s3cret-pw;Database=x
       at Moq.Behaviors.ThrowException.Execute(...)" (含明文，斷言 Password=*** 失敗)
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 315 ms - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    $ git stash pop
    Dropped refs/stash@{0} (b9a84a3e8ea700f120ef978916adbc71eb51f3e1)
    ```
  - GREEN 紀錄（接線狀態）：
    ```
    Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 418 ms - ProjectC.WebApi.Tests.dll (net10.0)
    ```
- [x] 4.3 TDD（Scenario 015）：`SerilogConfigurator` 新增建立 bootstrap logger 的方法（以 `ExceptionMaskingSink` 包住 Console），`Program.cs` 改呼叫它。測試走真實啟動路徑，證明 `Program.cs` 確實接線：在以 `[CollectionDefinition(Name, DisableParallelization = true)]` 宣告的 collection 內，先 `Console.SetOut` 到 `StringWriter`，再用 `WebApplicationFactory` 啟動，並在其 `ConfigureServices` 擲出訊息含 `Password=s3cret-pw` 的例外，使啟動在 `Program.cs` 的 `try` 區塊內失敗、走到 `Log.Fatal(exception, "Application terminated unexpectedly")`；斷言擷取到的 Console 輸出含 "Application terminated unexpectedly"（證明走到該路徑）、含 `Password=***`、不含 `s3cret-pw`。預期 RED：`Program.cs` 仍使用原本只有 Console 的 bootstrap logger，輸出含明文。驗證：該測試通過，且 `docker compose up` 啟動 api 後 `docker compose logs api` 仍顯示 "Starting up"。
  - 若 `Log.Logger` 的 process 全域特性使此測試無法穩定執行，停止並回報，改在 design.md 提請使用者核准替代驗證例外，不得改成只測工廠方法。
  - RED 紀錄（還原驗證，`bash scripts/verify-test-evidence.sh restore --path src/ProjectC.WebApi/Program.cs -- docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~BootstrapLoggerMaskingTests"`；測試於實作前先以原 Program.cs 執行，同樣失敗於 `Expected ... to contain "Password=***"`）：
    ```
    Saved working directory and index state WIP on feature/exception-log-masking: 8b49ec7 補充交叉審查流程待觀察項目
    stash@{0}: WIP on feature/exception-log-masking: 8b49ec7 補充交叉審查流程待觀察項目
      Failed ProjectC.WebApi.Tests.Logging.BootstrapLoggerMaskingTests.Startup_WhenHostBuildFailsWithPassword_FatalLogOnConsoleIsMasked [210 ms]
    [19:15:25 FTL] Application terminated unexpectedly
    System.InvalidOperationException: Host=db;Password=s3cret-pw;Database=x
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 216 ms - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    Dropped refs/stash@{0} (acc216bae6651a41364396a50e80c247beb20a82)
    ```
  - GREEN 紀錄：`SerilogConfigurator.CreateBootstrapLogger()` 以 `ExceptionMaskingSink` 包住 Console，`Program.cs` 改呼叫它（保留 `CreateBootstrapLogger` 的 ReloadableLogger 語意）：
    ```
    Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 191 ms - ProjectC.WebApi.Tests.dll (net10.0)
    ```
  - 啟動日誌檢查輸出（`docker compose restart api` 後 `docker compose logs api --since 2m`）：
    ```
    api-1  | [19:16:22 INF] Starting up
    api-1  | [19:16:24 INF] Now listening on: http://0.0.0.0:8080
    ```
- [x] 4.4 TDD（Scenario 001、016）：新增測試證明遮蔽涵蓋 Console、Seq 與框架來源，而不只記憶體 sink。(a) Console：在不平行執行的 collection 內 `Console.SetOut` 到 `StringWriter`，以 `SerilogConfigurator.Configure` 搭配含 `Console` 的 `Serilog:WriteTo` 設定建立 logger，記錄含明文的例外後 dispose 並斷言輸出不含明文；(b) Seq：`Seq:ServerUrl` 指向測試內的本機 `HttpListener` 假 ingestion 端點，記錄後 dispose logger 強制 flush，斷言收到的 request body 不含明文；(c) 框架來源：以 `ILoggerFactory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics")` 記錄含明文的例外，同時掛上記憶體 sink、Console 與指向本機 `HttpListener` 假 Seq 端點的 Seq sink，例外訊息含 `Password=s3cret-pw`；斷言記憶體 sink 與 Console 輸出不含明文，且 dispose logger 後假 Seq 端點收到的 request body 非空、含 `Password=***`、不含 `s3cret-pw`（Scenario 016 的 Seq 斷言）。預期 RED：外層若仍帶 config 宣告的 Console，或 Seq 掛在遮蔽層之外，對應斷言失敗。驗證：三組測試通過。
  - RED／還原驗證紀錄（`bash scripts/verify-test-evidence.sh restore --path src/ProjectC.WebApi/Logging/SerilogConfigurator.cs --path src/ProjectC.WebApi/Program.cs -- docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~AllSinksMaskingTests"`；第一次只還原 SerilogConfigurator 因 Program.cs 呼叫 CreateBootstrapLogger 而編譯失敗，腳本判定「環境或編譯錯誤」exit=2，已 pop 還原，改同時還原兩檔）：
    ```
    Saved working directory and index state WIP on feature/exception-log-masking: 8b49ec7 補充交叉審查流程待觀察項目
    stash@{0}: WIP on feature/exception-log-masking: 8b49ec7 補充交叉審查流程待觀察項目
      Failed ProjectC.WebApi.Tests.Logging.AllSinksMaskingTests.FrameworkSource_WhenExceptionHasPassword_AllSinksAreMasked [127 ms]
       Expected memorySink.Events.Single().Exception!.ToString() "System.InvalidOperationException: Password=s3cret-pw" to contain "Password=***".
      Failed ProjectC.WebApi.Tests.Logging.AllSinksMaskingTests.Seq_WhenExceptionHasPassword_RequestBodyIsMasked [8 ms]
       Expected body "{"@t":"2026-11-03T19:18:04.3210508Z","@mt":"seq-probe","@l":"Error","@x":"System.InvalidOperationException: Password=s3cret-pw"}
      Failed ProjectC.WebApi.Tests.Logging.AllSinksMaskingTests.Console_WhenExceptionHasPassword_OutputIsMasked [3 ms]
       Expected _consoleOut.ToString() "[19:18:04 ERR] console-probe
    Failed!  - Failed:     3, Passed:     0, Skipped:     0, Total:     3, Duration: 146 ms - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    Dropped refs/stash@{0} (77274af362538d0d489fb3ce9b297bf8049d5926)
    ```
    （Console 的斷言另見 4.0 RED；三項各自對應 Console／Seq／框架來源。）
    ```
    Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 126 ms - ProjectC.WebApi.Tests.dll (net10.0)
    ```

- [x] 4.5 檢查不變條件：`Program.cs` 與 `src/` 沒有啟用 `writeToProviders`、`AddConsole` 或 `ClearProviders` 之外的另一條 MEL Console 輸出路徑（design 決策 8）。驗證：grep 結果貼在本 task 下；host 關機時 Seq 最後一批日誌的 flush 路徑（外層 logger 被 dispose）於此一併確認並記錄。
  - 檢查結果：`grep -rnE "writeToProviders|AddConsole|ClearProviders|AddSerilog|UseSerilog" src --include=*.cs`（排除 obj／bin）——除註解外只有 `Program.cs:105 builder.Host.UseSerilog(SerilogConfigurator.Configure, preserveStaticLogger: true)`，無 `writeToProviders`／`AddConsole`／`ClearProviders`／`AddSerilog`，沒有另一條 MEL Console 輸出路徑。
  - Seq 關機 flush：新增 `HostShutdownSeqFlushTests.HostDispose_FlushesLastMaskedBatchToSeq`（`ObservabilityWebApplicationFactory` + 假 Seq，記錄後 dispose host，斷言假 Seq body 含 `shutdown-probe`、`Password=***`、不含 `s3cret-pw`）：
    ```
    Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 3 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```

## 5. 防禦性最終檢查

- [x] 5.1 hardener 最終檢查：程式碼全部完成後，套用 `.claude/skills/hardener/SKILL.md` 檢查清單於 `ExceptionMaskingSink`、`SensitiveTextMasker`（參數 null 處理、遮蔽失敗邊界、`LogEvent` 欄位複製是否完整保留 `Timestamp`／`Level`／`MessageTemplate`／`Properties`／`TraceId`／`SpanId`、執行緒安全、日誌遮蔽）。發現缺口依 TDD 先補 RED 測試再修改；無缺口則記錄結論。驗證：結果寫在本 task 下。
  - 檢查結果（套用 hardener 清單於 `ExceptionMaskingSink`、`SensitiveTextMasker`；兩者屬 Infrastructure 性質的純轉換元件，無 DB／Result／權限／N+1，安全強制問題中的輸入驗證與權限不適用）：
    - 參數驗證：`ExceptionMaskingSink` 建構子與 `Emit`、`SensitiveTextMasker.Mask` 皆 `ArgumentNullException.ThrowIfNull`；`SensitiveTextMasker.Mask` 有測試 `Mask_NullText_Throws`；`ExceptionMaskingSink` 的建構子與 `Emit` 守衛是單行 `ThrowIfNull`，無對應測試（內部契約守衛，呼叫端只有 Serilog 與 `SerilogConfigurator`），已知未測。
    - 遮蔽失敗邊界：`MaskException` 包在 try/catch，任何例外（含 `RegexMatchTimeoutException`、`ToString` 擲例外）降級為只含型別名稱的 `MaskedException` 占位並另送固定範本 Warning，不重擲、不記錄 `maskingFailure` 原文（可能含明文）；測試 `Emit_WhenMaskingFails_*`、`Emit_WhenExceptionToStringThrows_*`。
    - `LogEvent` 欄位複製：`CopyWithException` 帶入 `Timestamp`／`Level`／`MessageTemplate`／全部 `Properties`／`TraceId`／`SpanId`；測試 `Emit_PropertiesAndRenderedMessage_*`、`Emit_WhenMasked_PreservesTraceAndSpanIds`。
    - 執行緒安全：sink 無可變狀態；`Regex` 實例為執行緒安全且為 static readonly；內層 logger 本身執行緒安全。
    - 資源釋放：`Dispose` 釋放內層 logger（測試 `Dispose_DisposesInnerLogger`；host 關機 flush 見 4.5）。
    - 日誌遮蔽：本身不輸出含原文的日誌；失敗 Warning 只含型別名稱。
    - `CancellationToken`／狀態機／並發鎖：同步純轉換，不適用。
    - 結論：未發現缺口，未修改程式碼，不需另呼叫 hardener 補強 task。

## 6. 文件收尾與全套測試

- [x] 6.1 更新 `docs/deployment-prerequisites.md` 第 2 項與 `docs/exception-log-masking-todo.md` 為已完成（寫明完成的 change 與遮蔽範圍、已知限制：只認指定格式、不處理日誌屬性值與歷史日誌）。驗證：兩份文件內容與 design.md 決策 3 的格式清單一致，TDD 為 N/A（純文件）。
  - 結果：兩份文件已更新（deployment-prerequisites 第 2 項標為已完成並列已知限制；todo 加完成狀態）。格式清單與 design.md 決策 3 一致；實作多一個 `passwd` key，已於 6.5 補進 design 與 spec。TDD：N/A（純文件）。
- [x] 6.2 容器內重跑全套測試（所有測試專案），確認沒有既有測試因 `MaskedException` 取代而失敗；若有失敗，逐一判斷是測試過度綁定或真實行為改變並記錄。驗證：每個測試專案的摘要行貼原文。
  - 結果（`docker compose exec -T api dotnet test tests/<專案>` 逐一執行；根目錄無 .sln，故逐專案；無失敗、無略過，沒有既有測試因 `MaskedException` 取代而失敗）：
    ```
    ## ProjectC.Domain.Tests
    Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 62 ms - ProjectC.Domain.Tests.dll (net10.0)
    ## ProjectC.Application.Tests
    Passed!  - Failed:     0, Passed:   527, Skipped:     0, Total:   527, Duration: 975 ms - ProjectC.Application.Tests.dll (net10.0)
    ## ProjectC.Infrastructure.Tests
    Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: 46 s - ProjectC.Infrastructure.Tests.dll (net10.0)
    ## ProjectC.LoadTest.Seeder.Tests
    Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 6 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
    ## ProjectC.WebApi.Tests
    Passed!  - Failed:     0, Passed:   662, Skipped:     0, Total:   662, Duration: 2 m 42 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
- [x] 6.3 `graphify update .` 更新圖譜。驗證：指令成功結束。
  - 結果：`graphify update .` 成功結束（graph.json、graph.html、GRAPH_REPORT.md 已更新）。

- [x] 6.4 Devin 交叉審查 B-001 補強（TDD）：引號內含反斜線跳脫引號時（例如 `{"password":"pre\"s3cret-pw"}`），舊的 `QuotedValue` 把 `\"` 當成值的結尾，密碼尾段 `s3cret-pw` 留在輸出。新增 `Mask_QuotedValueContainingEscapedQuote_MasksWholeValue`（3 組：雙引號 JSON、單引號連線字串、`\\\"` 組合），`QuotedValue` 改為反斜線跳脫字元視為值的一部分（兩分支首字元互斥）。
  - RED（修改正式程式碼前，`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~Mask_QuotedValueContainingEscapedQuote"`）：
    ```
    Failed ...Mask_QuotedValueContainingEscapedQuote_MasksWholeValue(input: "{\"password\":\"pre\\\"s3cret-pw\"}", expected: "{\"password\":\"***\"}")
      Did not expect masked "{"password":"***"s3cret-pw"}" to contain "s3cret-pw".
    Failed ...(input: "Host=db;Password='pre\\'s3cret-pw';Port=5432", ...)
      Did not expect masked "Host=db;Password='***'s3cret-pw';Port=5432" to contain "s3cret-pw".
    Failed ...(input: "{\"client_secret\":\"a\\\\\\\"s3cret-pw\",\"user\""···, ...)
      Did not expect masked "{"client_secret":"***"s3cret-pw","user":"bob"}" to contain "s3cret-pw".
    Failed!  - Failed:     3, Passed:     0, Skipped:     0, Total:     3
    ```
  - GREEN（`--filter "FullyQualifiedName~SensitiveTextMaskerTests"`，含病態輸入時間上限測試）：`Passed!  - Failed:     0, Passed:    46, Skipped:     0, Total:    46, Duration: 124 ms - ProjectC.WebApi.Tests.dll (net10.0)`
  - 還原驗證（`scripts/verify-test-evidence.sh mutate` 的參數經 Git Bash→node 傳遞時反斜線被改寫，無法匹配，改用暫時 node 腳本逐一套用一個改動、跑測試、還原，腳本已刪除）：
    - 只把雙引號分支改回不支援跳脫：`Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3`（`Did not expect masked "{"password":"***"s3cret-pw"}"`…）。
    - 只把單引號分支改回舊版 `'[^'\r\n]*'`：`Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3`（`Did not expect masked "Host=db;Password='***'s3cret-pw';Port=5432" to contain "s3cret-pw".`）。
    - 還原後 `QuotedValue` 為修正後版本（已以 `sed -n 23p` 確認）。
  - 已知限制（不處理，記於 design.md 風險區）：JSON 字串內再跳脫的 `\"password\":\"x\"`、無引號值遇空白／逗號即終止（`Secret=a b`）、URL 密碼含未編碼的 `@`。

- [x] 6.5 審查建議採納（TDD）：(a) `BuildSection` 把 `Serilog:AuditTo` 也歸內層，外層不再讀它（設定檔日後加 `AuditTo` 會繞過遮蔽）；key 比對由 `StartsWith` 改為精確比對 key 或其子鍵（`IsKeyOrChild`）。(b) `HostShutdownSeqFlushTests` 加入不平行的 `GlobalLoggingState` collection（4 次完整套件曾有 1 次失敗，失敗訊息未抓到，成因為推測）。(c) delta spec 補 `passwd`（格式清單與 OBS-EXMASK-025）與跳脫引號（OBS-EXMASK-026），`SensitiveTextMaskerTests` 補 `Passwd` 案例。
  - RED（修改正式程式碼前，`--filter "FullyQualifiedName~SerilogConfiguratorMaskingTests"`）：`Failed ...Configure_AuditToConsoleDeclaredInConfiguration_ConsoleOutputHasNoPlaintext`，`Expected _consoleOut.ToString() "[04:42:35 ERR] boom` / `System.InvalidOperationException: Password=s3cret-pw`…；`Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4`。
  - GREEN：`--filter "FullyQualifiedName~Logging|FullyQualifiedName~Observability"` → `Passed!  - Failed:     0, Passed:   123, Skipped:     0, Total:   123`；WebApi 全套 → `Passed!  - Failed:     0, Passed:   667, Skipped:     0, Total:   667, Duration: 3 m 51 s - ProjectC.WebApi.Tests.dll (net10.0)`。
  - 還原驗證（`scripts/verify-test-evidence.sh mutate`，把外層排除條件改回只排除 `WriteTo`）：
    ```
    Failed ...Configure_AuditToConsoleDeclaredInConfiguration_ConsoleOutputHasNoPlaintext [30 ms]
      Did not expect _consoleOut.ToString() "[04:48:32 ERR] boom
    System.InvalidOperationException: Password=***
    [04:48:32 ERR] boom
    System.InvalidOperationException: Password=s3cret-pw
    " to contain "s3cret-pw".
    Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4, Duration: 158 ms - ProjectC.WebApi.Tests.dll (net10.0)
    結果：測試如預期失敗
    ```
  - `IsKeyOrChild` 的精確比對（相對 `StartsWith` 的差異）沒有獨立測試：目前設定中不存在 `Serilog:WriteToXxx` 之類的 key，屬防禦性改動。
  - `Passed` 的 `Passwd` 案例與 `HostShutdownSeqFlushTests` 的 collection 屬性在加入時即通過（既有行為／並行隔離），無 RED。

- [x] 6.6 Devin 第 3 輪 B-001（key 內含關鍵字即遮蔽，如 `NotASecret=visible`）：決定維持現行「寧可多遮蔽」行為，改在 spec 明文化（OBS-EXMASK-027 與格式要求的 key 說明），並以 `Mask_KeyContainingSensitiveKeyword_MasksValue`（2 案例）鎖定；同時修正 6.x 內過時的 `passwd` 待確認文字、design 決策 1 補 `AuditTo`。
  - TDD：RED N/A——此為對既有行為的特徵化測試與規格明文化，正式程式碼未改動，測試加入時即通過；RED 還原驗證不適用（無正式程式碼修改）。
  - 評估後不採納的非 blocking 建議：021／022／024 截斷測試已斷言不含片段（021 用 `AssertNoFragment`，022／024 用 `NotContain`），不另加；001 同請求驗證所有輸出與既有 Console／記憶體 sink 測試重複，不加；tasks 內兩處行尾空白位於逐字測試輸出內，修改會破壞證據，不動。
  - GREEN（`docker compose exec -T api dotnet build ... --no-incremental` 後，filter `Logging|Observability`）：
    ```
    Passed!  - Failed:     0, Passed:   126, Skipped:     0, Total:   126, Duration: 55 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```

- [x] 6.7 Devin 第 4 輪 B-002（引號未閉合的 secret 值外洩）與 B-003（OBS-EXMASK-027 文字比實作寬）：
  - B-002 修正：`secret`／`token`／`api-key` 規則的 value 新增 `["'][^\r\n]*` 分支（開引號到行尾 fail-closed 遮蔽）；新增 OBS-EXMASK-028 與 `Mask_UnterminatedQuotedSecret_MasksToEndOfLine`（3 案例）；已知限制文件移除該項。
  - B-003 處理：收窄 spec 文字為「以關鍵字結尾的欄位名」，`DatabaseTokenHint` 這類關鍵字後仍接字元者明列為不在保證範圍。
  - RED（修改正式程式碼前）：
    ```
    Failed ...Mask_UnterminatedQuotedSecret_MasksToEndOfLine(input: "client_secret=\"pre\\\"s3cret-pw", expected: "client_secret=***")
    Failed ...Mask_UnterminatedQuotedSecret_MasksToEndOfLine(input: "ApiKey: \"s3cret-pw\nnext line", expected: "ApiKey: ***\nnext line")
    Failed ...Mask_UnterminatedQuotedSecret_MasksToEndOfLine(input: "token='s3cret-pw", expected: "token=***")
    Failed!  - Failed:     3, Passed:     0, Skipped:     0, Total:     3
    ```
  - GREEN（`--no-incremental` 重建後，filter `Logging|Observability`）：
    ```
    Passed!  - Failed:     0, Passed:   129, Skipped:     0, Total:   129, Duration: 1 m 6 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```
  - 還原驗證（暫時移除新分支，`--no-incremental` 重建，備份於 /tmp 後以 cp 還原；因 regex 含反斜線，`verify-test-evidence.sh mutate` 無法比對，改用 node 腳本）：
    ```
    Failed ...Mask_UnterminatedQuotedSecret_MasksToEndOfLine(input: "client_secret=\"pre\\\"s3cret-pw", expected: "client_secret=***")
    Failed ...Mask_UnterminatedQuotedSecret_MasksToEndOfLine(input: "ApiKey: \"s3cret-pw\nnext line", expected: "ApiKey: ***\nnext line")
    Failed ...Mask_UnterminatedQuotedSecret_MasksToEndOfLine(input: "token='s3cret-pw", expected: "token=***")
    Failed!  - Failed:     3, Passed:     0, Skipped:     0, Total:     3
    ```
    還原並重建後：
    ```
    Passed!  - Failed:     0, Passed:   129, Skipped:     0, Total:   129, Duration: 1 m 5 s - ProjectC.WebApi.Tests.dll (net10.0)
    ```

## Workflow follow-up

- spec-reviewer 通過後暫停，等待使用者交叉審查並明確指示才開始實作。
- 實作完成後依 CLAUDE.md 依序執行 strict-reviewer 與全新的對抗式 subagent，直到兩者都沒有 blocking。
- 無前端變更，略過真實瀏覽器驗證。
- 使用者同意後 `/opsx:archive` 並合併回 master；任何步驟都不自動 commit。

## 規格審查紀錄

- Devin 交叉審查（GPT-6 Luna Medium Thinking）：spec-reviewer 與 design-hardener 階段經多輪修正後皆 PASS；Devin 端的 spec-reviewer／design-hardener subagent 因未取得工具而 BLOCKED，結果來自 Devin 主審查者。
- DH-B-001（完整 `Exception.ToString()` 無時間／大小上限）經使用者於 2026-11-04 核准為已知限制，見 design.md Risk 區。
- 主 session 最終 spec-reviewer：PASS（2026-11-04）。
