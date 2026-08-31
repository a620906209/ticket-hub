## ADDED Requirements

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
