## Context

`Event` 目前只有 `StartAtUtc`（活動開始時間），沒有任何販售期間的概念：
- `OrderService.PlaceOrderAsync` 與 `JoinPurchaseQueueHandler.HandleAsync` 都不檢查時間
- 熱門搶購模式（`IsQueueModeEnabled`）由 Organizer 手動開關，與時間無關

既有可參照的模式（皆為現有行為；販售期間檢查本身是本 change 新增的）：
- **欄位新增**：`real-name-verification` 為 `Event` 新增建立後不可變的 `IsRealNameRequired`。建構子用選填參數，所以既有 `new Event(...)` 呼叫不用改；API 欄位選填，未提供時沿用舊行為；`Down` migration 用 DO-block 防護
- **交易外讀取 + 交易內鎖定重讀的結構**（現有程式皆**沒有**販售時間判斷）：
  - 加入排隊：`JoinPurchaseQueueHandler`（`rate-limiting-queue` design.md 決策 4），可作為決策 4 的參考。
    - **既有流程（現況）**：交易外依序為 請求驗證 → 驗證碼 → 活動存在 → Queue Mode → 實名；交易內 `GetForUpdateAsync` 列鎖重讀後依序為 `lockedEvent` 存在 → 實名旗標一致性 → Queue Mode 重驗 → 排隊紀錄查詢／寫入。
    - **本 change 完成後**：交易外為 請求驗證 → 驗證碼 → 活動存在 → **販售期間** → Queue Mode → 實名；交易內為 `lockedEvent` 存在 → **販售期間** → 實名旗標一致性 → Queue Mode 重驗 → 排隊紀錄查詢／寫入。
  - 下單：`OrderService.PlaceOrderAsync` 的 Queue Mode **只**在交易內以鎖定重讀的 `lockedEvent` 判斷，交易外沒有 Queue Mode 檢查。交易外讀取的 `orderEvent` 參與既有實名與限購等前置判斷（既有跨活動驗證在讀取 `orderEvent` 之前完成，不使用它）；若交易外未取得活動，交易內則以 `lockedEvent` 補做必要的實名與其他權威判斷；兩次讀取的不可變設定（`IsRealNameRequired`）則依既有規則進行一致性處理（不一致即拋出例外）。本 change 不改變 `orderEvent` 的既有用途。本 change 的交易外販售期間檢查，插入既有跨活動驗證完成後、其他會影響下單結果的交易外檢查之前；交易內則以 `lockedEvent` 與重新取得的 `UtcNow` 作為販售期間最終判斷（`real-name-verification`）。決策 3 參考的是後者的「交易外 `orderEvent` + 交易內 `lockedEvent`」結構
  - 本 change 對兩個流程新增販售期間判斷：交易外快速拒絕、交易內以重新取得的時間作為最終判斷
- **時間來源**：Application 層一律注入 `IDateTimeProvider`；單元測試用 `FakeDateTimeProvider`，固定時間為 2026-01-01
- **錯誤分流**：可區分的錯誤靠 `ErrorType` → `ResultExtensions` 對映 HTTP status，`ProblemDetails.Title = error.Type.ToString()`，前端依 `Title` 判斷

## Goals / Non-Goals

**Goals:**
- 主辦方建立活動時可指定開賣時間與停售時間，建立後不可變更
- 建立訂單與加入排隊在販售期間外被拒，並回傳前端可區分的 `SalesNotOpen`／`SalesClosed`
- 既有客戶端（未帶新欄位）的 API 相容；業務行為的唯一變更是「未設定 `SalesEndAtUtc` 的活動（不論新舊）自 `StartAtUtc` 起停售」
- 公開與後台活動列表提供販售期間的原始值，供後續前端 change 使用

**Non-Goals:**
- 前端（建立表單、活動頁狀態與倒數）：後續 change `event-sales-window-web-ui`
- 開賣前的預先等候室：開賣前一律不能加入排隊
- 停售後主動清理 `Waiting`／`Admitted` 排隊紀錄：入場推進服務照常運作，被放行的人下單時會收到 `SalesClosed`
- 活動建立後修改販售期間：活動編輯已列入 Won't
- 自動依開賣時間開關熱門搶購模式
- 修正既有 `StartAtUtc` 對非 UTC 時間輸入的處理（見風險 R4）

## Decisions

### 決策 1：兩個欄位皆可為 null，null 有明確語意，不回填

- `SalesStartAtUtc = null` 表示「不設開賣下界」（停售仍依 `SalesEndAtUtc ?? StartAtUtc`）
- `SalesEndAtUtc = null` 表示「停售時間 = `StartAtUtc`」
- 實際販售期間為 `[SalesStartAtUtc ?? -∞, SalesEndAtUtc ?? StartAtUtc)`，左閉右開：
  - `now < start` → `NotOpen`
  - `now >= end` → `Closed`
  - 其餘 → `Open`

**理由**：
- 既有活動遷移後不用回填任何時間（使用者原本選「以建立時間或極早值回填」，null 在行為上等價）
- `Event` 建構子可以用選填參數，既有呼叫與絕大多數既有測試完全不用改

**替代方案**：
- **不可為 null，並以 `CreatedAtUtc` 或極早值回填**：需要挑一個 sentinel 值；`DateTime.MinValue` 寫入 timestamptz 時，Npgsql 會對應成 `-infinity`，語意隱晦。不採用。
- **API 必填**：後端先 merge、前端還沒跟上之前，現有表單建立活動會全部回 400，既有測試中建立活動的請求也都要改。使用者選擇「API 選填、前端必填」。

### 決策 2：販售狀態由 Domain 判斷，建構時驗證時間關係

- `Event` 新增 `GetSalesStatus(DateTime nowUtc)`，回傳 `EventSalesStatus { NotOpen, Open, Closed }`。這是單一 Entity 就能判斷的規則，依 CLAUDE.md 放在 Domain。
- 建構子驗證，違反時拋 `ArgumentException`，比照既有欄位：
  - `SalesEndAtUtc` 有值時 MUST `<= StartAtUtc`
  - `SalesStartAtUtc` 有值時 MUST `< (SalesEndAtUtc ?? StartAtUtc)`
- `CreateEventRequestValidator` 做相同驗證，讓 API 回 400，不會走到 Domain 例外變成 500。
- **已定案**：兩個新欄位各自獨立驗證，任一欄位有值時 MUST 為 `DateTimeKind.Utc`，也就是請求的時間字串須帶 `Z`；不帶時區（Unspecified）或帶偏移（如 `+08:00`，反序列化為 Local）一律回 400，避免寫入 timestamptz 時才失敗（見 R4）。此規則不依賴 Npgsql 實測結果；apply 時的實測（tasks 2.4、6.1）只用來驗證「讀回為 Utc」與 R4 的既定假設。
- **精度**（apply 後對抗審查發現）：比較在 .NET 100ns tick 精度進行，PostgreSQL timestamptz 只存到微秒，Npgsql 寫入時以 2000-01-01 為基準向零取整（2000 年後往下、2000 年前往上，第 3 輪對抗審查實測）。同一微秒內的「開賣 < 停售」寫入後會相等，EF 以建構子具現化時丟 `ArgumentException`，連帶讓公開活動列表 500。Validator 因此拒絕含次微秒的 `SalesStartAtUtc`／`SalesEndAtUtc`，以及有 `SalesStartAtUtc` 時的 `StartAtUtc`（回 400）。選擇拒絕而非截斷：不靜默改動使用者輸入；Domain 不加此檢查，避免 Domain 依賴資料庫精度。`SalesEnd <= StartAt` 在取整後仍成立（取整單調不減，且 SalesEnd 本身為整微秒不變），不受影響。
- **最小日期**（第 2 輪對抗審查發現）：`0001-01-01T00:00:00Z` 通過 Kind／精度／時間關係檢查，但 Npgsql 預設的 infinity 轉換把 `DateTime.MinValue` 存成 `-infinity`，讀回為 Kind=Unspecified，建構子 Kind 檢查丟例外，同樣讓活動列表 500。Validator 拒絕兩個販售欄位為 `DateTime.MinValue`；不改全域 Npgsql 設定（影響範圍大）。`DateTime.MaxValue` 已被整微秒規則擋下；`StartAtUtc` 為最小值時已被既有 `NotEqual(default)` 擋下。
- **不**驗證開賣時間須晚於現在：主辦方常需要「建立後立即開賣」，而時鐘差會讓「必須在未來」的規則誤擋。

### 決策 3：建立訂單的檢查位置——交易外快速失敗 + 交易內權威重驗

- **交易外**：位置在「所有項目屬同一活動」之後、實名閘門與限購檢查之前，以交易外讀到的 `orderEvent` 檢查。`orderEvent` 為 null 時略過。
- **交易內**：以 `lockedEvent` 和**交易內重新取得的** `now` 再檢查一次，位置在 `lockedEvent` null 檢查之後、實名補位檢查與排隊資格檢查之前，也在任何座位或庫存鎖定之前。這一次才是權威，失敗時回滾交易。
- 兩次檢查不比較彼此結果，也不因不一致而拋例外：販售期間不可變，但「現在時間」本來就會前進，交易外判斷 `Open`、交易內判斷 `Closed` 是合法情形。

**理由**：
- 錯誤優先順序：「尚未開賣／已停售」比「需實名」「超過限購」更根本，所以放在它們前面。未登記實名的買家在開賣前送單，會先看到 `SalesNotOpen`。
- 交易外快速失敗，可以讓開賣前的搶先請求不取得 Event 列鎖。
- 交易內權威重驗：不依賴「販售期間不可變」這個不變量。就算未來開放修改販售期間，也只需要維持交易內的檢查。同時涵蓋交易外讀不到活動的情形，不需要另外寫補位分支。

**判斷時間點**：以交易內那一次檢查取得的時間為準。請求在停售前送出、但交易內檢查時已過停售時間，會被拒絕；這是預期中的行為。

**替代方案**：
- **只在交易內檢查**：優先順序會落在實名與限購之後，而且開賣前每個請求都會鎖 Event 列。不採用。
- **比照實名閘門「交易外為主、交易內補位 + 不一致時回 500」**：時間會前進，兩次結果不同不代表不變量被破壞。不採用。

### 決策 4：加入排隊的檢查位置——同一套「交易外快速失敗 + 交易內權威重驗」

- **交易外**：既有順序為 驗證碼 → 活動存在 → 熱門搶購模式 → 實名；販售期間檢查插入在活動存在之後、既有熱門搶購模式檢查之前，即 驗證碼 → 活動存在 → 販售期間 → 熱門搶購模式 → 實名。「驗證碼先於任何查詢」的既有規則不變。
- **交易內**：以 `lockedEvent` 和交易內重新取得的 `now` 再檢查，位置在 `lockedEvent` null 檢查之後、實名不一致檢查與熱門搶購模式檢查之前，也在任何排隊紀錄查詢或寫入之前。
- 已有進行中紀錄的會員也同樣受此檢查：停售後的重複呼叫回 `SalesClosed`，不回傳既有紀錄，也不把逾時的 `Admitted` 轉為 `Expired`。
- **排隊狀態查詢**（`GET .../queue/entries/me`）不檢查販售期間，維持輪詢可用。

### 決策 5：錯誤型別與 HTTP status

- 新增 `ErrorType.SalesNotOpen`、`ErrorType.SalesClosed`，兩者都對映 **409 Conflict**。
- **理由**：這是「資源目前狀態不允許此操作」，跟既有「未開啟熱門搶購模式」的 409 同類。用 403 會跟權限類拒絕（`RealNameRequired`、`QueueAdmissionRequired`）混在一起。
- 分成兩個型別，前端才能分別顯示「尚未開賣」與「已停售」。
- 錯誤訊息只含活動 Id，不含時間：訊息會原樣放進 `ProblemDetails.Detail`，前端需要的時間從活動列表取得。

### 決策 6：列表回傳原始時間，不回傳推導狀態

- `EventDto`（公開，會被快取）與 `AdminEventSummaryDto`（後台）新增 `SalesStartAtUtc`、`SalesEndAtUtc`，回傳原始值，可為 null。
- **不**回傳依當下時間推導的 `SalesStatus`：公開列表會被 `query-caching` 快取，推導出的狀態會隨時間過期，但原始時間不可變，快取不會跟資料庫不一致。
- **不**把 `SalesEndAtUtc` 的 null 在 API 層展開成 `StartAtUtc`，維持「原始值」語意；由前端（後續 change）套用同一條規則。
- **快取 key 版本化（`query-caching` 能力修改）**：`EventDto` 多了兩個欄位，因此活動列表快取 key 需要版本化。
  - **目前現況**：程式使用 `query-cache:events:list`（`GetEventsHandler.CacheKey` 常數）。活動列表的明確失效呼叫端皆引用此常數，目前為 `CreateEventHandler` 與 `SetEventQueueModeHandler`；票種列表的失效呼叫端（建立票種、訂單扣減庫存、取消／逾時釋放庫存）使用各自的 `query-cache:ticket-types:event:{eventId}` key（`GetTicketTypesHandler.BuildCacheKey`），不在本 change 的 key 變更範圍內。
  - **本 change 完成後**：`GetEventsHandler.CacheKey` 改為 `query-cache:events:list:v2`，新版本只讀寫、只清除 v2 key；舊版本（本 change 之前的執行檔）只使用無後綴 key；不同版本不讀寫對方的 key。
  - **理由**：新舊版本實例重疊（滾動部署）時，舊版本只讀寫無後綴 key，無法把缺欄位的 DTO 寫進新版本讀取的 key；新版本也永遠不會讀到舊形狀的內容，不需要「缺欄位解讀為 null」的相容推論，也不需要限制部署方式。
  - **重疊期間的陳舊性**：舊版本實例建立活動時只清除無後綴 key，新版本 key 最長落後一個 TTL（30 秒），屬 `query-caching` 既有 TTL 安全網的陳舊上限。後端閘門一律讀資料庫，不受影響。
  - **回滾**：部署回舊執行檔，資料庫保留新欄位（舊 EF model 不查詢未對映欄位），不執行 Down。
    - 舊版本只讀寫、只清除無後綴 key，失效行為與本變更前相同；新版本 MUST NOT 寫入無後綴 key（QC-EVT-VER-003），所以回滾時該 key 只會有舊版本重疊期間寫入的舊形狀內容，最長一個 TTL 內過期。
    - v2 key 讀取時不檢查形狀：唯一寫入者是新版本未命中路徑，寫入完整 `EventDto`，舊形狀不可達。兩欄位可合法為 null，反序列化後無法分辨「缺欄位」與「值為 null」，若要在讀取時檢查就得改通用的 `IQueryCache` 讀取原始 JSON，成本高且保護的是不可達狀態，不採用。改以 QC-EVT-VER-004 鎖定前提：`RedisQueryCache` 以預設 `JsonSerializer.Serialize` 寫出，null 屬性不省略；序列化設定若改變，該測試失敗
    - 新版本 key 不主動清除，舊版本不讀，TTL 過期。
    - 舊版本行為由其既有測試涵蓋；新舊兩份執行檔無法在同一個測試程序中執行，因此「回滾後由舊版本操作」不寫成自動化整合測試，改以 tasks 6.9 部署演練驗證。
  - **QC-TTL-004 的驗證方法**（v2 需重新驗證 QC-TTL-004，且活動列表首次有競態測試）：spec 只規定語意（重新寫入自寫入時刻套用完整 TTL），量測方法屬測試設計，放在這裡而非 spec。
    - 時間一律用 `Stopwatch`（單調時鐘），不用 `DateTime.UtcNow`（WSL2 時鐘會倒退）；剩餘存活時間以 Redis PTTL（`KeyTimeToLiveAsync`，毫秒精度）讀取。
    - 寫入時刻無法從客戶端直接觀測（SET 在 Redis 伺服器端執行），只能夾住：測試用 `RecordingQueryCache` 包裝真正的 `RedisQueryCache`，記錄內層 `SetAsync` 的開始 `ws` 與完成 `we`，Redis 實際寫入時刻 `s ∈ [ws, we]`。PTTL 取樣 MUST 在 `SetCompleted` 之後才開始（`we ≤ rs`），讀取前後記錄 `rs`、`re`，Redis 計算 PTTL 的時刻 `p ∈ [rs, re]`。正確實作的 PTTL ≈ `T − (p − s)`，因此必落在 `[T − (re − ws) − 1ms, T − (rs − we) + 1ms]`；1ms 只對應 Redis 整數毫秒截斷，不是可調容差。不採用「替身回報實際寫入時刻 `w`」：客戶端能觀測的最精確點就是 `[ws, we]`，任何單點 `w` 都只是在這個區間內取值，不會更可靠。
    - 競態的先後關係以同一份包裝器事件紀錄證明（交易已提交 → 失效完成 → 舊資料寫入開始 → 寫入完成），R 的釋放等待「失效完成」訊號，不以固定等待推測。
    - 寫入成功不能以 `SetAsync` 返回判斷：`RedisQueryCache` 對 Redis 例外 fail-open（記 Warning 後正常返回）。包裝器在返回後確認 key 存在、讀回內容等於本次寫入值、PTTL > 0，才視為本次寫入已落在 Redis；否則測試明確失敗，PTTL 區間公式只在這個前提成立時才套用。
    - 存在／過期檢查以 `ws`／`we` 為基準安排窗口：包裝器公開事件紀錄（含 `Stopwatch.GetTimestamp()` 時刻），測試以同一個時鐘來源記錄自己的檢查時刻，因此可直接比較。檢查若因負載落在窗口外，丟出測試專用的「結果不可判定」例外，與一般斷言失敗區分，不視為通過。具體等待值是測試控制值，見 tasks 5.5，不是業務契約。
  - **替代方案**：(a) 禁止重疊部署並以「缺欄位解讀為 null」維持一致——無法以自動化測試驗證部署約束，不採用；(b) 在 `query-caching` 主 spec 定義過渡期例外——等於放寬一致性保證，不採用。
  - **能力數**：因此本 change 修改 4 個能力，超過 CLAUDE.md 的 3 個門檻；不拆分的理由見 proposal。

### 決策 7：Migration 與回滾

- **Up**：`Events` 新增兩個可為 null 的 `timestamp with time zone` 欄位，不回填。
- **部署順序**：MUST 先套用 migration 再啟動新版實例。新版在未遷移的資料庫上查詢活動會失敗（tasks 6.9 演練實測 `GET /api/events` 回 500）；舊版在已遷移的資料庫上不受影響，所以「先遷移、後滾動切換」是安全的。
- **Down**：比照 `AddRealNameVerification`，用 DO-block 檢查。只要有任何活動的 `SalesStartAtUtc` 或 `SalesEndAtUtc` 非 null，就 `RAISE EXCEPTION` 中止，避免靜默丟掉主辦方設定的販售期間（丟掉後這些活動會變成立即可售）。
- **建議的回滾方式**：只回滾程式碼。舊程式碼會忽略新欄位，所有活動退回「不檢查時間」；資料保留，重新部署即恢復。
- `EventConfiguration` 必須明確 `Property()` 兩個屬性：getter-only 的建構子綁定屬性，EF 不會自動對映。

## Risks / Trade-offs

- **R1 未設定停售時間的活動自 `StartAtUtc` 起停售（含遷移當下已開始的既有活動）** → 這是本 change 想修正的行為，適用所有新舊活動，不只遷移當下；demo 資料若需要可售活動，須建立 `StartAtUtc` 在未來的活動。部署說明須寫明。
- **R2 停售後排隊者被放行，卻無法下單** → 屬於非目標。被放行者下單會得到 `SalesClosed`，排隊紀錄依既有入場逾時自然轉為 `Expired`。後續前端 change 會依停售時間顯示「已停售」，降低這種困惑。
- **R3 伺服器時鐘決定開賣瞬間** → 判斷一律用伺服器的 `IDateTimeProvider`，不採信客戶端時間；多實例之間的時鐘差會讓各實例的開賣瞬間有毫秒級差異，可接受。WSL2 時鐘每 30 秒倒退的已知問題只影響開發環境的時間相關測試，所以單元測試一律用 `FakeDateTimeProvider`，整合測試用相對現在至少數小時的時間，不貼著邊界。
- **R4 既有 `StartAtUtc` 對不帶 `Z` 的時間輸入可能回 500** → 這是既有缺口，不在本次修正範圍。新欄位由 Validator 擋下非 UTC，所以新欄位帶 `Z`、`StartAtUtc` 不帶 `Z` 的請求，仍可能因為 `StartAtUtc` 而失敗。apply 時先實測確認，如果確實如此，就記入 `docs/project-scope.md` 第 8 節待確認事項，不在本 change 修。
- **R5 錯誤優先順序改變** → 販售期間外的請求，原本會回報實名、限購或排隊資格錯誤，現在改回報 `SalesNotOpen`／`SalesClosed`。既有測試的活動都沒有設定販售期間，有效停售時間即 `StartAtUtc`；開始時間在未來的不受影響。已知例外：`AdminTicketsControllerTests.GetHolder_WhenEventAlreadyStarted_Returns200WithHolderData`（RDM-HOLDER-009）以 `startsAtUtc: DateTime.UtcNow.AddHours(-1)` 建立活動後經 `RealNameTestData.SeedIssuedTicketAsync` 下單，本變更後會得到 `SalesClosed` 而失敗，須改寫（tasks 5.9）。此盤點以 grep 為主，可能不完整，以 6.2 全部測試結果為準。

## 安全確認（CLAUDE.md 安全強制規則）

- **輸入驗證**：
  - 兩個新欄位由 FluentValidation（`CreateEventRequestValidator`）在 Application 層驗證時間關係與 Kind，Domain 建構子再驗一次
  - 不拼接 SQL 或 shell 指令
- **資料庫**：
  - 一律走 EF Core。新增的讀取都沿用既有的 `GetByIdAsync`／`GetForUpdateAsync`／`GetAllAsync`，沒有新查詢，也沒有 N+1
  - Migration 的 DO-block 是固定 SQL，不含外部輸入
- **權限**：
  - 建立活動維持既有 `RequireOrganizerContext` 與租戶歸屬
  - 下單與排隊維持既有 `[Authorize]`
  - 公開列表維持匿名存取
  - 販售期間檢查只會讓請求變嚴格，不會放寬任何權限
- **可被未授權者觸發？** 不會，沒有新增端點。
- **個資**：販售期間不屬於敏感資訊；錯誤訊息只含活動 Id。
- **前端**：本 change 不改前端。
