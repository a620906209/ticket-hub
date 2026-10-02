## 0. 前置條件

- [ ] 0.1 確認在分支 `feature/event-sales-window`（自 master `f016de5` 建立），所有指令透過 `docker compose exec api`；既有測試全數通過作為基準（記錄 pass 數）

## 1. Domain

- [ ] 1.1（design.md 決策 1、2）`Event` 新增 `SalesStartAtUtc`、`SalesEndAtUtc`（`DateTime?`，`private set`），建構子新增兩個選填參數（置於參數列尾端，既有 64 處 `new Event(` 呼叫不需修改）；建構子驗證 `SalesEndAtUtc <= StartAtUtc`、`SalesStartAtUtc < (SalesEndAtUtc ?? StartAtUtc)`、有值時 `Kind == Utc`，違反時丟 `ArgumentException`（與既有建構子驗證一致）
- [ ] 1.2（決策 2）新增 `EventSalesStatus { NotOpen, Open, Closed }` 與 `Event.GetSalesStatus(DateTime nowUtc)`，左閉右開 `[SalesStart ?? -∞, SalesEnd ?? StartAt)`
- [ ] 1.3 `EventTests` 單元測試：
  - 建構子：合法組合（EVT-SALES-001／002／003／007 的 Domain 面）、`SalesEnd > StartAt`（004）、`SalesStart >= SalesEnd`（005，含相等）、`SalesEnd` 為 null 時 `SalesStart >= StartAt`（006，含相等）、非 Utc Kind 被拒
  - `GetSalesStatus`：開賣前一 tick → NotOpen；開賣當下 → Open（左閉）；停售當下 → Closed（右開）；`SalesEnd` null 時以 `StartAt` 為停售；兩者皆 null 且 now < StartAt → Open

## 2. Infrastructure

- [ ] 2.1（決策 7）`EventConfiguration` 明確 `Property()` 對映兩欄位（nullable timestamptz，不設預設值）；建構子參數名與屬性名一致以利 EF ctor binding
- [ ] 2.2（決策 7）新增 migration `AddEventSalesWindow`：`Up` 加兩個 nullable 欄位不回填；`Down` 比照 `AddRealNameVerification` 以 DO-block 在任一活動兩欄位之一非 null 時 `RAISE EXCEPTION`，再 drop 欄位
- [ ] 2.3 新增 `EventSalesWindowMigrationTests`（Testcontainers，比照 `RealNameVerificationMigrationTests`）：
  - EVT-SALES-010：遷移到前一版建立活動 → 套用本 migration → 兩欄位為 null
  - EVT-SALES-011：有活動 `SalesStartAtUtc` 非 null → Down 拋錯，欄位與資料不變
  - EVT-SALES-012：全部為 null → Down 成功，欄位已移除
- [ ] 2.4 `RepositoryCrudRoundTripTests`（或同等既有測試）補一筆兩欄位有值的 round-trip，確認 EF 讀回值與 Kind 為 Utc（必做，不可略過：Domain 建構子的 Kind 檢查在 EF 具現化時也會執行，依賴 Npgsql 讀回為 Utc）

## 3. Application — 建立活動與列表

- [ ] 3.1（決策 1）`CreateEventRequest` 尾端新增 `DateTime? SalesStartAtUtc = null, DateTime? SalesEndAtUtc = null`；Validator 加上與 Domain 一致的時間關係規則及 `Kind == Utc` 規則；Handler 傳入 `Event` 建構子
- [ ] 3.2（決策 6）`EventDto`、`AdminEventSummaryDto` 新增兩欄位（`DateTime?`，置於尾端），`GetEventsHandler`、`GetAdminEventsHandler` 帶出原始值，不加任何推導狀態欄位
- [ ] 3.3 `CreateEventValidator`／`CreateEventHandlerTests` 單元測試：EVT-SALES-001／002／003／007 成功並傳入正確值；004／005／006 回 Validation 且未呼叫 repository 新增；非 Utc Kind 回 Validation（EVT-SALES-008 的 Application 面）
- [ ] 3.4 `GetEventsHandlerTests`、`GetAdminEventsHandlerTests`：有設定與未設定的活動各一筆，DTO 值與 Entity 一致、未設定者為 null（TP-BROWSE-SALES-001、EVT-SALES-009 的 Application 面）

## 4. Application — 錯誤型別與閘門

- [ ] 4.1（決策 5）`ErrorType` 新增 `SalesNotOpen`、`SalesClosed`；`Error` 新增對應 factory，訊息只含活動 Id
- [ ] 4.2（決策 3）`OrderService.PlaceOrderAsync`：
  - 交易外：跨活動檢查之後、實名與限購之前，`orderEvent` 非 null 時以 `_dateTimeProvider.UtcNow` 檢查
  - 交易內：`lockedEvent` null 檢查之後、實名補位與排隊資格之前，以重新取得的 `UtcNow` 檢查（`now` 必須在 `GetForUpdateAsync` 返回之後取得，避免用等鎖之前的時間判斷）；失敗時回滾並回傳錯誤，不丟例外
  - 確認付款、取消訂單不加任何檢查
- [ ] 4.3（決策 4）`JoinPurchaseQueueHandler`：
  - 交易外：驗證碼、活動存在之後，熱門搶購模式與實名之前
  - 交易內：`lockedEvent` null 檢查之後、RN-mismatch 與熱門搶購模式重驗之前，在任何排隊紀錄查詢／寫入（含 Admitted→Expired 轉換）之前檢查；把既有 `var now = _dateTimeProvider.UtcNow` 上移到 `lockedEvent` null 檢查之後，販售期間檢查與 Admitted 逾時判斷共用同一個 `now`，不產生兩個 `now`
- [ ] 4.4 `OrderServiceTests` 單元測試（`FakeDateTimeProvider`）：
  - TP-SALES-ORDER-001／002（含停售當下相等）／003（含開賣當下相等）／004／005：回傳結果正確，失敗時未呼叫任何座位／票種鎖定與 `Add`
  - TP-SALES-ORDER-006：未實名 + 尚未開賣 + 超過限購 → `SalesNotOpen`
  - TP-SALES-ORDER-007：跨活動 + 尚未開賣 → 既有跨活動 Validation
  - TP-SALES-ORDER-008：`FakeDateTimeProvider` 在交易外讀取後推進到停售（以 `GetForUpdateAsync` 測試替身的回呼或依序回傳值的時間替身模擬）→ `SalesClosed`，交易回滾、未鎖定
  - TP-SALES-ORDER-009：`GetByIdAsync` 回 null、`GetForUpdateAsync` 回已停售活動 → `SalesClosed`，未查詢排隊資格、未鎖定
  - TP-SALES-ORDER-011／012：活動已停售時 `ConfirmOrderAsync`／`CancelOrderAsync` 照常成功
- [ ] 4.5 `JoinPurchaseQueueHandlerTests` 單元測試（`FakeDateTimeProvider`）：
  - PQ-SALES-JOIN-001／002／003
  - PQ-SALES-JOIN-004：驗證碼錯誤 + 尚未開賣 → `CaptchaInvalid`
  - PQ-SALES-JOIN-005：未開啟熱門搶購模式 + 尚未開賣 → `SalesNotOpen`
  - PQ-SALES-JOIN-006：未實名 + 需實名 + 尚未開賣 → `SalesNotOpen`
  - PQ-SALES-JOIN-007：已逾時 `Admitted` 紀錄 + 已停售 → `SalesClosed`，紀錄仍為 `Admitted`、未呼叫更新
  - PQ-SALES-JOIN-008：交易外可售、交易內時間推進到停售 → `SalesClosed`，未建立紀錄；另一案例為交易內同時已停售且 `IsQueueModeEnabled = false` → `SalesClosed`（驗證交易內販售期間檢查先於熱門搶購模式重驗）

## 5. WebApi

- [ ] 5.1（決策 5）`ResultExtensions` 將 `SalesNotOpen`、`SalesClosed` 對映為 409，附「為什麼是 409 而非 403」的註解（與其他對映的註解風格一致）
- [ ] 5.2 `EventsControllerTests`／`AdminEventsControllerTests`（WebApplicationFactory + Testcontainers）：
  - EVT-SALES-001／002：建立後經後台列表讀回，值一致或為 null；EVT-SALES-009：後台列表混合兩種活動
  - EVT-SALES-004／005／006：回 400 且資料庫無新 `Event`、無 `EventSeat`
  - EVT-SALES-008：`SalesStartAtUtc` 不帶 `Z` → 400，不是 500（請求的 `StartAtUtc` 必須帶 `Z`，避免被 design.md R4 的既有缺口干擾）；帶偏移（例如 `+08:00`，反序列化為 `Kind=Local`）同樣 → 400
  - TP-BROWSE-SALES-001：公開列表（匿名）附帶原始值、回應 JSON 不含任何販售狀態欄位
- [ ] 5.3 TP-BROWSE-SALES-002：於 `QueryCachingComponentTests`（或 `RealNameEventListCacheTests` 同一模式）以 `JsonSerializer.Serialize` 序列化一個舊 shape（不含兩欄位）的匿名物件寫入快取（不手寫 JSON 字串，以符合 `RedisQueryCache` 預設的 PascalCase），公開列表命中快取時兩欄位為 null、回 200
- [ ] 5.4 `OrdersControllerTests`：TP-SALES-ORDER-001（409 + `Title = "SalesNotOpen"`、座位仍 Available、票種庫存不變、無訂單）、TP-SALES-ORDER-002（409 + `SalesClosed`）；TP-SALES-ORDER-011／012：以資料庫建立「販售期間內建立的 Pending 訂單」後活動已停售的狀態（Event 不可變更，故直接 seed 已停售活動與 Pending 訂單），確認付款成功出票、取消成功並釋放
- [ ] 5.5 TP-SALES-ORDER-010：開啟熱門搶購模式、已停售活動，seed 一筆未逾時 `Admitted` 紀錄，建立訂單 → 409 `SalesClosed`，排隊紀錄狀態不變（`OrdersControllerTests` 或 `OrderServiceQueueModeLinearizationTests` 所在專案）
- [ ] 5.6 `EventQueueControllerTests`／`PurchaseQueueCaptchaTests`：PQ-SALES-JOIN-001／002 的 HTTP 409 + Title；PQ-SALES-JOIN-009：seed 已停售活動上的既有排隊紀錄，`GET .../queue/entries/me` 照常回傳
- [ ] 5.7 整合測試時間一律相對 `DateTime.UtcNow` 至少數小時，不貼邊界（design.md R3，WSL2 時鐘倒退）；核對 WebApi 測試中所有以 POST 建立活動的請求 `StartAtUtc` 皆在未來（design.md R5）

## 6. 驗證與文件

- [ ] 6.1（design.md R4）實測 `StartAtUtc` 不帶 `Z`、新欄位帶 `Z` 的建立活動請求；若回 500，記入 `docs/project-scope.md` 第 8 節待確認事項，不在本 change 修正
- [ ] 6.2 全部測試在容器內通過（`docker compose exec api dotnet test`），與 0.1 基準比對，回報新增數與任何 skip
- [ ] 6.3 核對情境對應表：EVT-SALES-001…012、TP-BROWSE-SALES-001／002、TP-SALES-ORDER-001…012、PQ-SALES-JOIN-001…009 每條至少一個測試，列出對應測試名稱
- [ ] 6.4 套用 `.claude/skills/hardener/SKILL.md` 檢查 `OrderService`、`JoinPurchaseQueueHandler`、`CreateEventHandler` 本次變更，之後呼叫 strict-reviewer
- [ ] 6.5 `docs/project-scope.md` 第 8 節補強項目 ① 標註後端完成、前端待 `event-sales-window-web-ui`；部署說明寫明 R1（已開始的既有活動遷移後為已停售），以及部署與回滾後執行 `DEL query-cache:events:list` 清除列表快取
- [ ] 6.6 歸檔時同步三個 delta spec 至主 spec，`openspec validate --specs` 通過
