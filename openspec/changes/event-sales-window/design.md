## Context

`Event` 目前只有 `StartAtUtc`（活動開始時間），沒有任何販售期間的概念：
- `OrderService.PlaceOrderAsync` 與 `JoinPurchaseQueueHandler.HandleAsync` 都不檢查時間
- 熱門搶購模式（`IsQueueModeEnabled`）由 Organizer 手動開關，與時間無關

既有可參照的模式：
- **欄位新增**：`real-name-verification` 為 `Event` 新增建立後不可變的 `IsRealNameRequired`。建構子用選填參數，所以 64 處 `new Event(...)` 不用改；API 欄位選填，未提供時沿用舊行為；`Down` migration 用 DO-block 防護
- **交易外快速失敗、交易內權威重驗**：`JoinPurchaseQueueHandler` 對 `IsQueueModeEnabled` 的處理（`rate-limiting-queue` design.md 決策 4）
- **時間來源**：Application 層一律注入 `IDateTimeProvider`（24 個檔案）；單元測試用 `FakeDateTimeProvider`，固定時間為 2026-01-01
- **錯誤分流**：可區分的錯誤靠 `ErrorType` → `ResultExtensions` 對映 HTTP status，`ProblemDetails.Title = error.Type.ToString()`，前端依 `Title` 判斷

## Goals / Non-Goals

**Goals:**
- 主辦方建立活動時可指定開賣時間與停售時間，建立後不可變更
- 建立訂單與加入排隊在販售期間外被拒，並回傳前端可區分的 `SalesNotOpen`／`SalesClosed`
- 既有活動與既有客戶端（未帶新欄位）的行為不變，唯一例外是「已開始的活動停售」
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

- `SalesStartAtUtc = null` 表示「無開賣限制」
- `SalesEndAtUtc = null` 表示「停售時間 = `StartAtUtc`」
- 實際販售期間為 `[SalesStartAtUtc ?? -∞, SalesEndAtUtc ?? StartAtUtc)`，左閉右開：
  - `now < start` → `NotOpen`
  - `now >= end` → `Closed`
  - 其餘 → `Open`

**理由**：
- 既有活動遷移後不用回填任何時間（使用者原本選「以建立時間或極早值回填」，null 在行為上等價）
- `Event` 建構子可以用選填參數，64 處既有呼叫與絕大多數既有測試完全不用改
- 部署前的舊快取項目缺欄位時，反序列化為 null 剛好也是正確語意（見決策 6）

**替代方案**：
- **不可為 null，並以 `CreatedAtUtc` 或極早值回填**：需要挑一個 sentinel 值；`DateTime.MinValue` 寫入 timestamptz 時，Npgsql 會對應成 `-infinity`，語意隱晦。不採用。
- **API 必填**：後端先 merge、前端還沒跟上之前，現有表單建立活動會全部回 400，另外有 43 處測試請求要改。使用者選擇「API 選填、前端必填」。

### 決策 2：販售狀態由 Domain 判斷，建構時驗證時間關係

- `Event` 新增 `GetSalesStatus(DateTime nowUtc)`，回傳 `EventSalesStatus { NotOpen, Open, Closed }`。這是單一 Entity 就能判斷的規則，依 CLAUDE.md 放在 Domain。
- 建構子驗證，違反時拋 `ArgumentException`，比照既有欄位：
  - `SalesEndAtUtc` 有值時 MUST `<= StartAtUtc`
  - `SalesStartAtUtc` 有值時 MUST `< (SalesEndAtUtc ?? StartAtUtc)`
- `CreateEventRequestValidator` 做相同驗證，讓 API 回 400，不會走到 Domain 例外變成 500。
- 新欄位有值時，Validator 要求 `DateTimeKind.Utc`，也就是請求的時間字串須帶 `Z`；否則回 400，避免寫入 timestamptz 時才失敗（見 R4）。apply 時須實測 Npgsql 10.0.3 對 Unspecified／Local 的實際行為，再定案這條驗證。
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

- **交易外**：位置在驗證碼與活動存在檢查之後、熱門搶購模式檢查之前。「驗證碼先於任何查詢」的既有規則不變。
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
- **舊快取項目**：部署前寫入的快取缺這兩個欄位，反序列化後為 null，代表「無開賣限制、停售時間沿用活動開始時間」，對部署前建立的活動來說正是正確值。部署後新建的活動會讓列表快取失效（既有 `RemoveAsync`），但這**不是嚴格不變量**：滾動部署期間舊實例可能在快取失效後以舊 DTO 重新填入，僅回滾程式碼時舊程式碼也會寫入不含欄位的列表。影響上限為一個快取 TTL（30 秒）內該活動列表欄位顯示為 null；後端閘門一律讀資料庫，不會因此放行錯誤的下單。部署與回滾後以 `DEL` 清除列表快取鍵即可消除此窗口（見 tasks 6.5）。此例外僅限部署／回滾的過渡期，不改變 `query-caching` 主 spec「快取內容與資料庫查詢回應一致」的穩態保證，因此不修改 `query-caching` 能力（同時避免本 change 影響的能力超過 3 個）。

### 決策 7：Migration 與回滾

- **Up**：`Events` 新增兩個可為 null 的 `timestamp with time zone` 欄位，不回填。
- **Down**：比照 `AddRealNameVerification`，用 DO-block 檢查。只要有任何活動的 `SalesStartAtUtc` 或 `SalesEndAtUtc` 非 null，就 `RAISE EXCEPTION` 中止，避免靜默丟掉主辦方設定的販售期間（丟掉後這些活動會變成立即可售）。
- **建議的回滾方式**：只回滾程式碼。舊程式碼會忽略新欄位，所有活動退回「不檢查時間」；資料保留，重新部署即恢復。
- `EventConfiguration` 必須明確 `Property()` 兩個屬性：getter-only 的建構子綁定屬性，EF 不會自動對映。

## Risks / Trade-offs

- **R1 已開始的既有活動遷移後變成已停售** → 這是本 change 想修正的行為；demo 資料若需要可售活動，須建立 `StartAtUtc` 在未來的活動。部署說明須寫明。
- **R2 停售後排隊者被放行，卻無法下單** → 屬於非目標。被放行者下單會得到 `SalesClosed`，排隊紀錄依既有入場逾時自然轉為 `Expired`。後續前端 change 會依停售時間顯示「已停售」，降低這種困惑。
- **R3 伺服器時鐘決定開賣瞬間** → 判斷一律用伺服器的 `IDateTimeProvider`，不採信客戶端時間；多實例之間的時鐘差會讓各實例的開賣瞬間有毫秒級差異，可接受。WSL2 時鐘每 30 秒倒退的已知問題只影響開發環境的時間相關測試，所以單元測試一律用 `FakeDateTimeProvider`，整合測試用相對現在至少數小時的時間，不貼著邊界。
- **R4 既有 `StartAtUtc` 對不帶 `Z` 的時間輸入可能回 500** → 這是既有缺口，不在本次修正範圍。新欄位由 Validator 擋下非 UTC，所以新欄位帶 `Z`、`StartAtUtc` 不帶 `Z` 的請求，仍可能因為 `StartAtUtc` 而失敗。apply 時先實測確認，如果確實如此，就記入 `docs/project-scope.md` 第 8 節待確認事項，不在本 change 修。
- **R5 錯誤優先順序改變** → 販售期間外的請求，原本會回報實名、限購或排隊資格錯誤，現在改回報 `SalesNotOpen`／`SalesClosed`。既有測試的活動都沒有設定販售期間，且開始時間在未來，所以不受影響。

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
