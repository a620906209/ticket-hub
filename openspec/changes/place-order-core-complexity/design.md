# Design

## Context

動機見 proposal.md「Why」。現況（已讀 `OrderService.cs:138-464` 確認）：

- `PlaceOrderCoreAsync` 依序做：FluentValidation → 向連線池借一次連線 → 交易外驗證（票種存在性、RequiresSeat 與請求形狀交叉驗證、座位存在性、跨活動、販售期間快速失敗、實名主要檢查、限購、座位配置與座位圖成員／分區、提早 409）→ `BeginTransactionAsync` → `GetForShareAsync` 鎖 Event 並重驗販售期間 → 實名／座位配置補位（交易外讀不到活動時）與實名旗標不變量檢查（`real-name-verification` 的 I1，與下方本 change 的 I1 無關）→ 排隊資格（`GetForUpdateAsync`）→ 鎖座位 → 鎖計數票種 → 組 `SeatSelection`／`QuantitySelection` → `CreateOrderHandler.Handle` → `queueEntry?.Complete()` → `Add` → commit → 先釋放連線 → Redis 入場鏡像同步 → 票種列表快取失效。
- 多個階段帶提前 `return`，加上 `foreach`、`?.`、`is { } x` 模式、`&&` 條件，累計循環複雜度 48（`.editorconfig` 的 `CA1502` 門檻為預設 25）。
- `timings` 在 6 個時間點被寫入（`PlaceOrderAsync` 的 finally 依這些點輸出分段耗時），位置有測試守護（`PlaceOrderAsync_WhenDebugEnabled…` 系列）。
- `await using var connection` 與 `await using var transaction` 的宣告位置決定釋放順序（離開方法時先回滾交易再關連線），commit 後會手動 `connection.DisposeAsync()`。
- 既有測試：`OrderServiceTests`（Application.Tests，PlaceOrder 案例以 1.1 對照表實數為準，含鎖定前拒絕、日誌分段、鎖內重驗、快取失效）與 Infrastructure.Tests 的 `OrderServiceConcurrencyTests`、`OrderServiceEarlyConflictTests`、`OrderServiceEventShareLockTests`、`OrderServiceSingleConnectionTests`、`OrderServiceQueueModeLinearizationTests`，以及 `Orders/OrderServiceNotificationTests`（不在突變驗證範圍，但 `~OrderService` filter 會一併執行）。

本 change 設 `skip_specs: true`：沒有任何 Requirement 新增或修改（proposal.md「Capabilities」）。

## Goals / Non-Goals

**Goals:**
- `PlaceOrderCoreAsync` 與每個新抽出的方法循環複雜度都不超過 25（CA1502 僅在大於 25 時警告），CI 不再出現 `CA1502`。（`SonarAnalyzer` 只有 `ProjectC.WebApi.csproj` 引用，Application 專案的 `OrderService` 不受 `S3776` 分析，因此 `S3776` 在本 change 範圍內不會新增，也不作為驗收條件；基準警告清單中的 `S3776` 屬 WebApi 的 `ReconcileEventQueueAsync`。）
- 重構前後全套測試結果一致；下方「不變量」每一條都有現有（或補上的）測試對應，並以突變驗證證明該測試會失敗。

**Non-Goals:**
- 不改任何對外行為、不新增類別、不改建構子與 DI、不調整 `.editorconfig` 門檻。
- 不順手改善被搬動的邏輯（命名、順序、重複的 `request.Selections` 走訪等都原樣保留）；註解中的 `design.md 決策 N` 引用隨程式碼搬移，內容不改。
- 不處理 `PurchaseQueueAdmissionService.ReconcileEventQueueAsync` 的 `S3776`。

## Decisions

### 決策 1：拆成 `OrderService` 內的 private 方法，不新增類別

抽成獨立驗證器類別需新增型別、DI 註冊並增加建構子依賴（目前已有 18 個）；本 change 的目標是零結構變動。階段間資料以 private sealed record 傳遞；需要回傳資料的階段回傳既有 `Result<T>`，純檢查階段回傳 `Error?`（沿用 `GetSeatPlacementErrorOrNull`）。協調者以 `if (error is not null) return Result<Guid>.Failure(error)` 轉成回傳值。

替代方案：把階段抽成 `Application/Orders/PlaceOrder/` 下的 collaborator 類別。否決原因：新增型別與 DI 註冊、需要把 `_ticketTypeRepository` 等依賴再傳一次，且「換實作」並無需求（Rule 2）。

### 決策 2：階段切分（建議，實作可微調，但須遵守決策 3 的不變量）

**階段間的呼叫順序（含各階段內的 I/O 與提前 return）一律與原碼一致，不得調整**；實作者只能改切分邊界與方法命名，不能重排相鄰檢查（例如把驗證用座位載入提到 RequiresSeat 形狀檢查之前，會讓「計數票種帶不存在 EventSeatId」從 Validation 變成 NotFound）。每個 2.x 子 task 的驗證須含「diff 核對呼叫順序與原碼一致」。優先順序的測試守護見 tasks 1.1「優先順序組合」欄。

| 階段 | 建議方法 | 內容 | 回傳 |
| --- | --- | --- | --- |
| 1 | `LoadTicketTypesAsync` | 批次讀票種、依請求順序回報第一個缺漏 | `Result<Dictionary<Guid, TicketType>>` |
| 2 | `GetSelectionShapeErrorOrNull`（static） | RequiresSeat 與請求形狀交叉驗證 | `Error?` |
| 3 | `LoadValidationSeatsAsync` | 座位存在性批次讀取 | `Result<Dictionary<Guid, EventSeat>>` |
| 4 | `GetCrossEventErrorOrNull`（static）＋ `LoadOrderEventAsync` | 票種／座位活動聯集檢查；唯一 `eventId` 由協調者從聯集取得，交易外 `orderEvent` 由 `LoadOrderEventAsync` 回傳 | `Error?`；`Task<Event?>` |
| 5 | `GetPreTransactionGateErrorAsync` | 販售期間快速失敗 → 實名主要檢查 → 限購（順序固定） | `Task<Error?>` |
| 6 | `LoadSeatPlacementsAsync` ＋ 既有 `GetSeatPlacementErrorOrNull` | 座位樣板讀取、配置、座位圖成員／分區比對 | `Result<IReadOnlyList<SeatPlacement>>` |
| 7 | 保留既有 `GetEarlyConflictOrNull` 呼叫 | 提早 409 | `Error?` |
| 8 | `GetLockedEventErrorOrNullAsync`（含 I/O：`_memberRealNameRepository.GetAsync`） | 鎖內販售期間重驗（`now` 由協調者在 `GetForShareAsync` 與 `EventLocked` 之後取得並傳入，I5 載體留在協調者）、實名／座位配置補位、實名旗標不變量（丟 `InvalidOperationException`）；`lockedEvent is null` → NotFound（:314–317）留在協調者，緊接 `GetForShareAsync` 與 `EventLocked` 時間點，之後才呼叫本方法 | `Task<Error?>`（例外照丟）；共 6 個輸入（buyerId、orderEvent、lockedEvent、seatPlacements、now、ct；`now` 在 `lockedEvent is null` 檢查之後取得，與原碼 :314–321 一致）：前 5 個資料欄位依 CLAUDE.md 封裝為一個 private record，`CancellationToken` 保留為獨立參數（便於 5.2 hardener 核對 CT 傳遞），故方法實際為 2 個參數 |
| 9 | `LockQueueEntryAsync` | 排隊模式資格檢查；**必須在 `_purchaseQueueRepository.GetForUpdateAsync` 返回之後自行讀 `_dateTimeProvider.UtcNow`（原碼 :352–353），不得重用協調者傳給階段 8 的 `now`**（等候列鎖期間跨過 `AdmissionExpiresAtUtc` 的請求否則會被誤放行；`FakeDateTimeProvider.UtcNow` 可設定，但 `FakePurchaseQueueRepository.GetForUpdateAsync` 目前無 callback，現有測試無法在列鎖期間推進時鐘；由 1.2 新增的 callback 測試與 3.7 階段 9b 突變守護。`UtcNow` 的讀取次數與位置（含交易外 :235 與 :296 各一次）也屬呼叫順序，不得合併或移動） | `Result<PurchaseQueueEntry?>` |
| 10 | `LockSeatsAsync` / `LockCountingTicketTypesAsync` | 鎖座位、鎖計數票種 | `Result<Dictionary<…>>` |
| 11 | `BuildSelections`（參數 request、ticketTypesById、eventSeatsById、lockedTicketTypesById 超過 3 個，以 private record 封裝資料） | 組 `SeatSelection`／`QuantitySelection` | `Result<…>` |
| 12 | `SyncAfterCommitAsync` | commit 後：鏡像同步、票種快取失效 | `Task` |

協調者 `PlaceOrderCoreAsync` 保留：FluentValidation、`await using var connection`、`await using var transaction` 的宣告、所有 `timings.X = …` 寫入、`GetForShareAsync` 呼叫與其緊接的 `EventLocked` 時間點、`CreateOrderHandler.Handle`、`queueEntry?.Complete()`、`Add`、`CommitAsync`、`connection.DisposeAsync()`。理由：資源宣告位置與時間點寫入位置是不變量 2–4 的載體，抽走會讓不變量跨方法而難以審查。

### 決策 3：不變量（行為不變準則，取代 Acceptance Criteria）

| # | 不變量 | 守護測試（apply 階段逐條核對並補缺口） |
| --- | --- | --- |
| I1 | 鎖定順序固定 Event → PurchaseQueueEntry → EventSeat → TicketType | Event／PurchaseQueueEntry 的順序：`OrderServiceQueueModeLinearizationTests`、`OrderServiceEventShareLockTests`、`OrderServiceTests` 中「ReturnsX…BeforeQueueCheckAndLocks」系列。**EventSeat → TicketType 的順序目前沒有呼叫順序斷言**（跨交易並發測試不驗呼叫順序），1.2 須新增特徵化測試（在 Fake 加共用順序紀錄掛點，記錄各鎖定呼叫順序）；決策 2 拆出的鎖座位／鎖票種兩個方法正是最容易對調處 |
| I2 | 全部交易外驗證在 `BeginTransactionAsync` 之前（失敗時不開交易） | `…RejectsBeforeOpeningTransaction`、`…BeforeOpeningTransaction`、`…BeforeTakingAnyLock` 系列 |
| I3 | `timings` 6 個時間點的取得位置與順序不變（含「未到達為 null」） | `PlaceOrderAsync_WhenDebugEnabled…` 系列、`…WhenWallClockJumpsDuringOrder…`、`OrderService_DoesNotReadWallClockDirectly` |
| I4 | commit 後先 `connection.DisposeAsync()`，再呼叫 Redis | `OrderServiceSingleConnectionTests` |
| I5 | `now` 在 `GetForShareAsync` 返回之後才取得（等鎖期間跨過停售時點須回 SalesClosed） | `…WhenSalesClosesWhileWaitingForEventLock…` |
| I6 | 所有失敗路徑的錯誤類型與訊息逐字相同；同一請求觸發多個失敗時的優先順序不變 | `OrderServiceTests` 各 `Returns…` 案例（含 `…AndLimitAlsoFail_ReturnsSalesNotOpen`、`…RealNameMissingAndMaxTicketsExceeded…`） |

額外須守住（不屬於上列但同樣可被誤改）：`ConnectionOpened` 之前的 FluentValidation 失敗不借連線；Redis 呼叫與快取失效只在 commit 成功後；純座位訂單不失效票種快取；`RealNameFlag` 前後讀不一致丟 `InvalidOperationException`。

### 決策 4：驗證方式——特徵化測試＋突變驗證，TDD 的 RED 標 N/A

沒有新行為，RED 不存在。保護網為：(1) 重構前先建立「分支 → 測試」對照表，缺口補特徵化測試（在現有碼上就通過，不是 RED）；(2) 記錄重構前全套基準；(3) 重構後重跑比對；(4) 每個被搬動的階段用 `scripts/verify-test-evidence.sh mutate` 暫時改壞一個分支（一次一個），確認至少一個測試失敗，證明保護網有效。理由：特徵化測試通過只證明「現在綠」，只有突變驗證能證明「改壞會紅」。

CLAUDE.md 的「RED 還原驗證」（`restore`）在本 change N/A：行為不變重構的還原後測試照樣通過是預期結果，證明「測試會失敗」的責任改由 `mutate` 承擔。任一 mutation 沒有測試失敗時，停止並回 1.2 補測試（適用第 3 節全部 task）；由於第 3 節在第 2 節重構之後，回頭補的測試必須先還原 OrderService.cs 到重構前驗證其在原碼通過、再於重構碼驗證（做法見 tasks 第 3 節前言），避免把重構造成的回歸固定成預期行為。已確認的缺口（`lockedEvent == null` 與 timings 先後順序、6 則失敗訊息逐字斷言、EventSeat → TicketType 鎖定順序）一律在 1.2 無條件先補，不留待第 3 節才發現。`timings` 的 Stopwatch 為真實時鐘，I3 的「位置不變」只能部分以測試守護（EventLocked 對 null 檢查、時間點先後），其餘由 diff 審查守護。

主 spec 追溯（供 1.1 核對，編號以實際主 spec 為準）：I3 ↔ `load-testing` LT-MEASURE-001～004、I4 ↔ `ticket-purchase` 的單一連線相關 Requirement（TP-ORDER-037／038）、I5 ↔ `ticket-purchase` 的 TP-SALES-ORDER-008（守護測試 `…WhenSalesClosesWhileWaitingForEventLock…` 註解亦標 008）、I6 ↔ `ticket-purchase` 的失敗訊息 Requirement，另 TP-SALES-ORDER-013（交易內已停售先於實名補位回報）對應階段 8 內部順序，併入 I6 核對。

### 決策 5：複雜度量測方式

CA1502 只在超過門檻時警告，且訊息帶實際值（現況：`'PlaceOrderCoreAsync' has a cyclomatic complexity of '48'`）。`.editorconfig:164` 只設 severity，門檻無法在 `.editorconfig` 調整；分析器只讀標為 `AdditionalFiles` 的 `CodeMetricsConfig.txt`，而目前沒有該檔，專案內也沒有任何 `AdditionalFiles`（已確認）。要量每個方法的實際值，暫時：(1) 在 repo 根目錄新增 `CodeMetricsConfig.txt`，內容 `CA1502(Method): 1`（CA1502 只回報大於門檻的成員，輸出中未出現的方法複雜度視為 1；此語法與輸出格式以 tasks 1.4 實測為準）；(2) 在 `src/ProjectC.Application/ProjectC.Application.csproj` 暫時加入 `<AdditionalFiles Include="..\..\CodeMetricsConfig.txt" />`。取得全部值後兩者都還原（刪除檔案、移除該行）；最終驗收以預設門檻 25 下的建置無 `CA1502` 為準。暫時設定不得進入 commit。

## Risks / Trade-offs

- [搬移時改變提前 return 的優先順序，例如把限購檢查移到實名檢查之前] → I6；對照表要求「同一請求同時違反 A 與 B」的優先順序案例有測試（`…AndLimitAlsoFail…`、`PlaceOrderAsync_WhenMaxTicketsPerOrderAndSeatZoneBothViolated_ReturnsMaxTicketsErrorBeforeOpeningTransaction` 已存在），缺口補測。
- [`await using` 宣告被抽走導致釋放順序改變，commit 後 Redis 呼叫期間連線仍被占用] → 宣告留在協調者（決策 2）；I4 由 `OrderServiceSingleConnectionTests` 守護，並以突變驗證（把 `connection.DisposeAsync()` 移到 Redis 呼叫之後）證明測試會失敗。
- [時間點寫入位置偏移一行，分段耗時失真但功能測試全綠] → I3 的日誌案例逐段斷言 null／非 null；突變驗證移動一個 `timings` 寫入點。
- [階段切分過細，協調者變成長串 `if (error is not null)`，可讀性未必提升] → 以複雜度門檻為客觀停止條件：每個方法不超過 25 即可，不追求更細。
- [`OrderService.cs` 為 CRLF 行尾，`mutate` 的多行 `--from` 若只含 `\n` 會比對不到（腳本回 exit 2，無法判定）] → 第 3 節的 mutation 優先用單行替換；多行替換須含 `\r\n`。
- [補特徵化測試時順手修改既有測試] → 只新增，不改既有測試斷言；若發現既有測試本身有誤，停止並回報。
- [與 `design.md 決策 N` 引用的註解脫離上下文] → 註解隨程式碼搬移、內容不改。
