# observability Specification

## Purpose
TBD - created by archiving change observability. Update Purpose after archive.
## Requirements

### Requirement: 系統以結構化欄位輸出日誌
系統 SHALL 讓所有透過 `ILogger` 記錄的日誌，在 Serilog 內部的 `LogEvent` 表示（進而流向 Seq、可被結構化查詢）中保留結構化格式；既有程式碼呼叫 `ILogger` 時使用具名參數的訊息樣板（例如 `"...{OrderId}..."`）時，該具名參數 SHALL 被保留為可獨立查詢的結構化欄位（`LogEvent.Properties`），不得僅被展開成字串插值後即遺失欄位邊界。此能力 MUST NOT 要求既有呼叫端程式碼變更。

**本 Requirement 不要求 Console sink 的文字輸出本身是結構化格式（例如 JSON）**：Console sink 刻意保留人類可讀的純文字 render（`docker compose logs api` 直接看得懂），這是既有除錯習慣、也是 Serilog + Seq 這套組合的標準做法——結構化查詢的責任交給 Seq，Console 只負責即時、可讀的本機除錯輸出，兩者職責分工明確（見 design.md 決策 3）。結構化保證的對象是 `LogEvent` 本身與其在 Seq 中的表示，不是「每一個 sink 各自的渲染格式」。

#### Scenario: OBS-STRUCTURED-FIELD-PRESERVED 具名參數保留為結構化欄位
- **WHEN** 既有程式碼呼叫 `_logger.LogError(exception, "...{OrderId}...", orderId)` 這類具名參數樣板
- **THEN** 輸出的日誌條目中，`OrderId` 為一個獨立可查詢的欄位，其值等於傳入的 `orderId`

### Requirement: 同一次 HTTP 請求的所有日誌自動共用同一個關聯值
系統 SHALL 讓單次 HTTP 請求處理期間輸出的所有日誌條目，自動帶上同一個關聯值（`TraceId`），且此值 SHALL 與既有 `GlobalExceptionHandler` 回傳給前端的 `ProblemDetails.Extensions["traceId"]`（即 `HttpContext.TraceIdentifier`）為同一個值，不得另外產生獨立於此的第二個關聯識別碼。不同次請求之間的 `TraceId` 值 SHALL 互不相同。

#### Scenario: OBS-REQUEST-TRACE-CONSISTENT 單次請求內的日誌共用同一關聯值
- **WHEN** 單次 HTTP 請求處理過程中，程式碼在多個不同位置（例如 Handler 與其呼叫的 Repository）各自輸出日誌
- **THEN** 這些日誌條目的 `TraceId` 欄位值彼此相同，且與該次回應中 `HttpContext.TraceIdentifier` 的值一致

#### Scenario: OBS-REQUEST-TRACE-UNIQUE 不同請求的關聯值互不相同
- **WHEN** 系統依序處理兩次不同的 HTTP 請求，兩次皆有輸出日誌
- **THEN** 兩次請求各自日誌條目的 `TraceId` 欄位值不相同

### Requirement: 背景服務每輪執行週期的日誌共用專屬關聯值
不具備 `HttpContext` 的背景服務（例如逾時訂單清理、購票排隊放行）SHALL 在每一輪執行週期開始時產生一個新的關聯值，該輪次內所有日誌條目 SHALL 共用此值，且此值與其他輪次或任何 HTTP 請求的關聯值不重複；欄位名稱 SHALL 與 HTTP 請求路徑使用的欄位名稱（`TraceId`）相同。此關聯值 MUST NOT 要求與 HTTP 請求端的格式相同（`HttpContext.TraceIdentifier` 為 ASP.NET Core 內建格式，非 GUID）——兩者是各自獨立的識別碼空間，僅共用欄位名稱以便在 Seq 中用同一個查詢條件（`TraceId`）篩選，不要求值本身可互相比對格式。

#### Scenario: OBS-BACKGROUND-CYCLE-TRACE 同一輪次的日誌共用專屬關聯值
- **WHEN** 背景服務單一輪次執行中對多筆項目（例如多筆逾時訂單）分別輸出日誌
- **THEN** 這些日誌條目的 `TraceId` 欄位值彼此相同；下一輪執行週期產生的日誌 `TraceId` 值與本輪不同

### Requirement: 日誌不得輸出敏感資訊
本次新增的請求摘要日誌（由 `Serilog.AspNetCore` 內建的請求記錄中介軟體針對每一次 HTTP 請求自動產生一筆摘要日誌，記錄方法、路徑、狀態碼、耗時等欄位）MUST NOT 包含 `Authorization` 標頭內容、Cookie 內容，或完整的請求／回應 body——這些欄位不屬於該中介軟體的預設輸出欄位，本 Requirement 明確禁止實作階段額外客製化把它們加進去。此規則為既有 CLAUDE.md「不得記錄敏感資訊」規範在日誌基礎設施層級的具體化；既有各呼叫點（非此中介軟體產生的日誌）是否符合此規則不在本次變更範圍內（既有程式碼已個別處理，例如 Email 遮蔽）。

#### Scenario: OBS-REQUEST-LOG-NO-AUTH-HEADER 請求摘要日誌不含 Authorization 標頭
- **WHEN** 系統記錄一次帶有 `Authorization: Bearer <token>` 標頭的 HTTP 請求摘要日誌
- **THEN** 該筆日誌的所有欄位內容 MUST NOT 包含該 token 字串

#### Scenario: OBS-REQUEST-LOG-NO-COOKIE 請求摘要日誌不含 Cookie 內容
- **WHEN** 系統記錄一次帶有 `Cookie` 標頭的 HTTP 請求摘要日誌
- **THEN** 該筆日誌的所有欄位內容 MUST NOT 包含該 Cookie 的值

#### Scenario: OBS-REQUEST-LOG-NO-BODY 請求摘要日誌不含請求/回應 body 內容
- **WHEN** 系統記錄一次帶有 JSON body 的 HTTP 請求摘要日誌
- **THEN** 該筆日誌的所有欄位內容 MUST NOT 包含該 body 的內容

### Requirement: 既有能力定義的敏感資訊遮蔽規則在結構化日誌下持續適用
`email-notification` 能力（買家 Email 遮蔽）與 `ticket-issuance`／`ticket-redemption` 能力（QR 內容／簽章不得輸出至日誌）已各自定義敏感資訊遮蔽規則；本次把 `ILogger` 底層換成 Serilog 之後，日誌不再只是純文字字串，而是保留具名參數為獨立可查詢的結構化欄位（見「系統以結構化欄位輸出日誌」Requirement）。此變化本身帶來一個既有純文字 log 不存在的風險：即使日誌訊息的渲染後文字內容遮蔽正確，若程式碼呼叫端不慎把未遮蔽的原始值當作額外的具名參數傳入樣板，Serilog 仍會把該原始值保留為一個結構化屬性（即使該屬性未出現在渲染後的訊息文字裡），連帶被寫入 Seq、可被查詢。系統 SHALL 確保：既有遮蔽規則涵蓋的欄位（買家 Email、QR 內容／簽章），在其對應日誌的 `LogEvent` 結構化屬性（不僅是渲染後的訊息文字）中，皆不包含未遮蔽的原始值。

#### Scenario: OBS-EMAIL-MASKED-IN-STRUCTURED-PROPERTIES Email 遮蔽規則涵蓋結構化屬性
- **WHEN** `MockEmailNotificationService` 記錄「出票通知已送出」的日誌（`email-notification` 能力目前唯一把買家收件信箱放進日誌樣板具名參數的呼叫點；通知失敗路徑的記錄僅含 `OrderId`，不含 Email，故不適用本情境）
- **THEN** 該筆 `LogEvent` 的所有結構化屬性（不只渲染後的訊息文字）皆不包含未遮蔽的完整 Email，只包含遮蔽後的值

#### Scenario: OBS-SIGNATURE-NOT-IN-STRUCTURED-PROPERTIES QR 簽章不得出現在結構化屬性
- **WHEN** 核銷流程記錄的日誌內容涉及票券核銷（依 `ticket-issuance`／`ticket-redemption` 能力規則，簽章與完整 QR 內容不得輸出至日誌）
- **THEN** 該筆 `LogEvent` 的所有結構化屬性（不只渲染後的訊息文字）皆不包含 QR 內容或簽章值本身

### Requirement: Seq sink 連線失敗不得影響應用程式啟動或請求處理
系統 SHALL 確保 Seq sink 無法連線時，應用程式仍可正常啟動並處理請求；MUST NOT 因為 Seq sink 寫入失敗而阻塞請求處理執行緒，也 MUST NOT 造成應用程式啟動失敗。Console sink 的輸出不受 Seq sink 連線狀態影響，SHALL 持續正常運作。「不阻塞」的可驗收界線定義為：單次請求因觸發日誌寫入而增加的處理時間，SHALL 與 Seq 可連線時的基準相比無明顯差異（因為 Serilog 對 sink 的寫入是非同步排入佇列，不等待網路 I/O 完成，寫入呼叫本身 SHALL 在毫秒等級內返回，不因網路逾時而等秒級時間），此界線同時適用於連線立即被拒絕（connection refused）與連線後無回應（black-hole，例如連到一個不存在的位址且封包被靜默丟棄）兩種失敗型態。

系統 MUST NOT 保證 Seq sink 無法連線期間所產生的日誌不遺失：本次不採用 durable buffering（落地緩衝到本機檔案再重送），僅採用 Serilog 預設的記憶體內批次緩衝——緩衝區滿版時舊事件可能被捨棄，這是刻意接受的行為（本機展示用途，非任何正式環境的稽核日誌保存需求），Console sink 作為不受 Seq 影響的備援輸出。

#### Scenario: OBS-SEQ-SINK-FAILURE-RESILIENT Seq 連線被拒絕時應用程式仍正常運作
- **WHEN** Seq 連線位址設定為一個會立即拒絕連線的位址（例如 Seq 服務未啟動）
- **THEN** 應用程式仍能正常啟動並成功處理請求，處理時間與 Seq 可連線時的基準相比無明顯差異，Console sink 的日誌輸出不受影響

#### Scenario: OBS-SEQ-SINK-BLACKHOLE-RESILIENT Seq 連線黑洞（無回應）時應用程式仍正常運作
- **WHEN** Seq 連線位址設定為一個連線後不會回應的位址（black-hole，非立即拒絕）
- **THEN** 應用程式仍能正常啟動並成功處理請求，處理時間與 Seq 可連線時的基準相比無明顯差異，不因等待 Seq 回應而卡住請求處理執行緒

### Requirement: EF Core 產生的 SQL 指令日誌降噪
系統 SHALL 將 `Microsoft.EntityFrameworkCore.Database.Command` 命名空間的日誌最低輸出等級設定為 `Warning`，避免預設 `Information` 等級下每次查詢都產生一筆日誌，稀釋其餘業務日誌的可讀性。

#### Scenario: OBS-EF-COMMAND-LOG-SUPPRESSED EF Core 一般查詢不產生 Information 等級日誌
- **WHEN** 系統執行一次一般（非例外）的 EF Core 資料庫查詢
- **THEN** 不產生任何 `Microsoft.EntityFrameworkCore.Database.Command` 來源、等級為 `Information` 或以下的日誌條目

### Requirement: Seq 作為本機 Docker Compose 集中查詢平台
系統 SHALL 在 `docker-compose.yml` 提供 `seq` 服務，隨 `docker compose up` 一併啟動；`api` 服務 SHALL 透過 compose 服務名稱（非 `localhost`）將日誌寫入 Seq。Seq 的 Web 查詢介面 SHALL 透過可調整的 host port 對外映射，比照既有服務（`db`／`api`／`web`）「對外 port 衝突時可透過 `.env` 調整」的既定慣例。

**存取權限範圍（明確界定，非遺漏）**：Seq Web 介面 MUST NOT 額外要求帳號密碼或其他驗證機制——與既有 `pgadmin` 服務的既定慣例完全相同：凡能連上該 host port 的任何使用者（本機、或同一區網內能連到該 port 的其他裝置，視 host 防火牆設定而定，本次不額外收斂）皆可直接存取，系統對此不做任何攔截或審計。此設計 MUST 限定於單機本地開發／展示情境；本能力 SHALL NOT 被部署於正式環境或任何多人共用的網路環境——若未來需要，須先補上獨立的認證機制提案，屬本次範疇外。此為刻意的範疇邊界（比照既有 `pgadmin` 現況與 design.md Non-Goals），非待補的存取控制缺口。

#### Scenario: OBS-SEQ-SERVICE-STARTS Seq 服務隨 compose 啟動成功
- **WHEN** 執行 `docker compose up` 啟動所有服務
- **THEN** `seq` 服務啟動成功且可透過對外映射的 host port 存取其 Web 查詢介面

#### Scenario: OBS-API-LOG-QUERYABLE-IN-SEQ API 產生的日誌可在 Seq 查詢到
- **WHEN** `api` 服務因處理一次請求而輸出至少一筆日誌
- **THEN** 該筆日誌可透過 Seq 查詢介面依 `TraceId` 或訊息內容查得

### Requirement: 日誌等級與 Seq 連線位址可透過設定調整，不寫死於程式碼
系統 SHALL 將日誌最低輸出等級、per-namespace 等級覆寫、Seq 連線位址等設定值放在 `appsettings.json` 的設定節（機敏連線資訊透過既有 `.env`／compose `env_file` 機制注入），修改這些值 MUST NOT 需要重新編譯程式碼。

#### Scenario: OBS-LOG-LEVEL-VIA-CONFIG 調整設定即改變日誌輸出行為
- **WHEN** 修改 `appsettings.json` 中 Serilog 設定節的最低輸出等級後重新啟動應用程式
- **THEN** 應用程式依新的等級設定過濾日誌輸出，不需修改任何程式碼

### Requirement: 日誌中的例外內容須遮蔽敏感資訊
系統 SHALL 在日誌輸出前，遮蔽例外文字（`Message`、`InnerException` 鏈、`AggregateException` 的內層例外）中符合敏感格式的值，使 Console、Seq 等所有輸出目的地都不含明文敏感值。此規則 SHALL 對所有日誌來源一致適用：HTTP 請求處理、背景服務、基礎設施元件、框架自行記錄的例外，以及應用程式啟動階段的失敗日誌。

#### Scenario: OBS-EXMASK-001 HTTP 未預期例外的日誌不含明文密碼
- **WHEN** 某端點處理請求時拋出例外，其訊息含 `Password=s3cret-pw`
- **THEN** 該請求期間產生的每一筆帶例外的日誌（含框架自行記錄的那些），在 Console 與 Seq 的輸出中都 MUST NOT 包含 `s3cret-pw`，且 `Password=` 之後的值為 `***`

#### Scenario: OBS-EXMASK-002 背景服務的例外日誌同樣遮蔽
- **WHEN** 背景服務單一輪次中捕捉到訊息含 `Password=s3cret-pw` 的例外並記錄錯誤日誌
- **THEN** 該筆日誌的例外文字 MUST NOT 包含 `s3cret-pw`

#### Scenario: OBS-EXMASK-003 內部例外鏈中的敏感值被遮蔽
- **WHEN** 例外的 `InnerException`（任一層）訊息含 `Password=s3cret-pw`
- **THEN** 日誌輸出的例外文字 MUST NOT 包含 `s3cret-pw`

#### Scenario: OBS-EXMASK-004 AggregateException 內層例外被遮蔽
- **WHEN** `AggregateException` 的某個內層例外訊息含 `Password=s3cret-pw`
- **THEN** 日誌輸出的例外文字 MUST NOT 包含 `s3cret-pw`

#### Scenario: OBS-EXMASK-015 啟動階段失敗日誌同樣遮蔽
- **WHEN** 應用程式啟動階段拋出訊息含 `Password=s3cret-pw` 的例外，並以「Application terminated unexpectedly」記錄致命錯誤
- **THEN** 該筆日誌的例外文字 MUST NOT 包含 `s3cret-pw`

### Requirement: 例外遮蔽涵蓋指定的敏感格式
系統 SHALL 遮蔽下列格式中的值，將值換成 `***` 並保留 key 與其餘文字；key 為「以」下列關鍵字結尾的欄位名（關鍵字後緊接 `=` 或 `:`，其間可有引號與空白；例如 `client_secret`、`access_token`、`NotASecret`）即視為命中，不要求關鍵字位於欄位名開頭；關鍵字後仍接其他字元的欄位名（例如 `DatabaseTokenHint`）不在保證範圍內：連線字串的 `Password`／`Passwd`／`Pwd` 後接 `=` 或 `:`；`Bearer` token 與 JWT（`eyJ` 開頭的三段式字串）；`scheme://user:password@host` 中的密碼；`ApiKey`、`Secret`、`Token` 的 `key=value` 或 `key: value`。key 比對 SHALL 不分大小寫。

#### Scenario: OBS-EXMASK-005 連線字串密碼被遮蔽且 key 保留
- **WHEN** 例外訊息含 `Pool=true;Pwd=s3cret-pw;Port=5432`
- **THEN** 輸出含 `Pwd=***`，且不含 `s3cret-pw`，`Pool=true` 與 `Port=5432` 原樣保留

#### Scenario: OBS-EXMASK-006 Bearer token 與 JWT 被遮蔽
- **WHEN** 例外訊息含 `Authorization: Bearer abc.def.ghi` 或獨立出現的 `eyJhbGciOi.eyJzdWIiOi.c2lnbmF0dXJl`
- **THEN** 輸出不含該 token 字串

#### Scenario: OBS-EXMASK-007 URL 內嵌的密碼被遮蔽
- **WHEN** 例外訊息含 `redis://user:s3cret-pw@cache:6379`
- **THEN** 輸出含 `redis://user:***@cache:6379`，且不含 `s3cret-pw`

#### Scenario: OBS-EXMASK-008 ApiKey、Secret、Token 的值被遮蔽且大小寫不敏感
- **WHEN** 例外訊息含 `apikey=k-123`、`SECRET: k-456` 或 `Token=k-789`
- **THEN** 輸出不含 `k-123`、`k-456`、`k-789`

#### Scenario: OBS-EXMASK-009 不含敏感格式的例外文字不被改動
- **WHEN** 例外訊息不含任何上述格式（例如 `Sequence contains no elements`），且例外文字長度不超過 64 KB
- **THEN** 日誌輸出的例外文字與遮蔽前完全相同

### Requirement: 遮蔽後保留排錯所需的例外資訊
系統 SHALL 在遮蔽後的日誌中保留原始例外的型別名稱、堆疊追蹤與內部例外鏈的層次結構，只替換命中敏感格式的值。例外文字超過 64 KB 時，系統 SHALL 僅保證保留文字開頭 32 KB 內的型別與訊息，以及結尾 32 KB 內的堆疊；位於中間而被省略的內部例外型別、訊息，以及被省略的內部例外鏈層數與順序，皆不在保證範圍內。本規格中的「KB」一律以 UTF-16 字元數計（1 KB ＝ 1,024 個 `string.Length` 單位），不是位元組數。

#### Scenario: OBS-EXMASK-010 遮蔽後仍可辨識例外型別與堆疊
- **WHEN** 一個 `InvalidOperationException` 的訊息含 `Password=s3cret-pw`，且該例外是從某方法拋出
- **THEN** 日誌輸出的例外文字含 `InvalidOperationException`、該方法在堆疊中的位置，且不含 `s3cret-pw`

#### Scenario: OBS-EXMASK-011 內部例外鏈的型別順序保留
- **WHEN** 例外文字長度不超過 64 KB，例外鏈為外層 `A` 包住內層 `B`，且 `B` 的訊息含 `Password=s3cret-pw`
- **THEN** 日誌輸出的例外文字中 `A` 的型別名稱出現在 `B` 之前，且 `B` 的型別名稱存在

### Requirement: 遮蔽失敗時不得外洩原文也不得丟失日誌
系統 SHALL 在遮蔽處理本身失敗時，仍輸出該筆日誌，並將例外內容換成只含型別名稱的占位文字；MUST NOT 把未遮蔽的例外文字傳給任何輸出目的地，也 MUST NOT 讓遮蔽失敗中斷呼叫端的日誌呼叫。

#### Scenario: OBS-EXMASK-012 遮蔽逾時時改輸出只含型別的占位文字
- **WHEN** 遮蔽處理對某例外文字無法在允許時間內完成
- **THEN** 該筆日誌仍被輸出，其例外文字只含原例外的型別名稱，不含原始訊息與堆疊；並另有一筆固定範本、不含例外文字的 Warning 日誌記錄遮蔽失敗

### Requirement: 回應本文與非例外欄位不受遮蔽影響
遮蔽 SHALL 只作用於日誌的例外內容；未預期例外的 500 回應本文 SHALL 維持只含 `status`、`title`、`traceId`，日誌訊息範本的具名屬性值與渲染訊息 SHALL 維持原樣。

#### Scenario: OBS-EXMASK-013 500 回應本文不變
- **WHEN** 某端點拋出訊息含 `Password=s3cret-pw` 的例外
- **THEN** 回應狀態為 500，本文欄位集合恰為 `status`、`title`、`traceId`

#### Scenario: OBS-EXMASK-014 非例外的結構化屬性不被遮蔽器改動
- **WHEN** 一筆日誌帶有具名屬性 `{OrderId}`，其例外分別為「不含敏感格式」與「訊息含 `Password=s3cret-pw`」兩種情況
- **THEN** 兩種情況下該筆日誌的 `OrderId` 屬性值與渲染訊息都和遮蔽前相同

#### Scenario: OBS-EXMASK-016 框架類別記錄的例外同樣遮蔽
- **WHEN** 以框架類別名稱（例如 `Microsoft.AspNetCore.Hosting.Diagnostics`）為來源記錄一筆錯誤日誌，其例外訊息含 `Password=s3cret-pw`
- **THEN** 該筆日誌在 Console 與 Seq 的輸出中 MUST NOT 包含 `s3cret-pw`

#### Scenario: OBS-EXMASK-017 Password 以冒號分隔時同樣遮蔽
- **WHEN** 例外訊息含 `Password: s3cret-pw`
- **THEN** 輸出含 `Password: ***`，且不含 `s3cret-pw`

#### Scenario: OBS-EXMASK-018 JSON 與前綴 key 的值被遮蔽
- **WHEN** 例外訊息含 `{"password":"s3cret-pw"}`、`access_token=s3cret-pw` 或 `"client_secret": "s3cret-pw"`
- **THEN** 輸出不含 `s3cret-pw`

#### Scenario: OBS-EXMASK-019 含空白的連線字串密碼完整遮蔽
- **WHEN** 例外訊息含 `Pool=true;Password=my s3cret pw;Port=5432`
- **THEN** 輸出不含 `my`、`s3cret`、`pw` 這三段密碼內容，且 `Pool=true`、`Port=5432` 原樣保留

#### Scenario: OBS-EXMASK-020 超長例外文字的省略部分不輸出
- **WHEN** 例外文字長度超過 64 KB，且位於頭段與尾段之間（第 32 KB 之後、距結尾 32 KB 之前）的位置含 `Password=s3cret-pw`
- **THEN** 輸出不含 `s3cret-pw`，且輸出含固定的截斷標記

#### Scenario: OBS-EXMASK-021 JWT 跨越頭段上限時不輸出任何片段
- **WHEN** 例外文字長度超過 64 KB，其中一個 JWT 起於頭段上限（第 32 KB）之前、延伸到其之後
- **THEN** 輸出不含該 JWT 的任何連續 10 個以上字元的片段，且輸出含固定的截斷標記

#### Scenario: OBS-EXMASK-022 含空白密碼被頭段上限切斷時不輸出任何片段
- **WHEN** 例外文字長度超過 64 KB，且 `Password=my s3cret pw` 被頭段上限（第 32 KB）切在中間
- **THEN** 輸出不含 `my`、`s3cret`、`pw` 三段中任何一段緊接在 `Password=` 之後的內容，且輸出含固定的截斷標記

#### Scenario: OBS-EXMASK-023 超長訊息的例外仍保留型別與堆疊
- **WHEN** 例外的訊息超過 64 KB，堆疊追蹤位於訊息之後，且訊息中段含 `Password=s3cret-pw`
- **THEN** 輸出含該例外的型別名稱與堆疊中至少一個 `at ` 開頭的堆疊框架，含固定的截斷標記，且不含 `s3cret-pw`

#### Scenario: OBS-EXMASK-024 含逗號的 URL 密碼被邊界切開時不輸出任何片段
- **WHEN** 例外文字長度超過 64 KB，且 `redis://user:abc,def@cache:6379` 被頭段上限（第 32 KB）切在 `def` 中
- **THEN** 輸出不含 `abc` 緊接在 `redis://user:` 之後的內容，且含固定的截斷標記

#### Scenario: OBS-EXMASK-025 Passwd 同樣遮蔽
- **WHEN** 例外訊息含 `Server=db;Passwd=s3cret-pw;Port=5432`
- **THEN** 輸出含 `Passwd=***`，且不含 `s3cret-pw`，`Server=db` 與 `Port=5432` 原樣保留

#### Scenario: OBS-EXMASK-026 引號內含跳脫引號的值完整遮蔽
- **WHEN** 例外訊息含 `{"password":"pre\"s3cret-pw"}` 或 `Password='pre\'s3cret-pw'`（引號內有以反斜線跳脫的同種引號）
- **THEN** 輸出不含 `s3cret-pw`，值整段被替換為 `***` 並保留外層引號

#### Scenario: OBS-EXMASK-028 引號未閉合的 secret 值遮蔽到行尾
- **WHEN** 例外訊息含 `client_secret="pre\"s3cret-pw` 或 `token='s3cret-pw`（`secret`／`token`／`api-key` 的值以引號開頭，但同一行內沒有閉合引號）
- **THEN** 輸出不含 `s3cret-pw`，從開引號到該行行尾的整段被替換為 `***`，後續行原樣保留

#### Scenario: OBS-EXMASK-027 欄位名內含敏感關鍵字時視為命中
- **WHEN** 例外訊息含 `NotASecret=visible`（欄位名以關鍵字結尾但不等於關鍵字）
- **THEN** 輸出為 `NotASecret=***`；系統寧可多遮蔽也不外洩，此為刻意取捨
