# Design

## Context

動機見 proposal.md「Why」。現況（已讀 `OrderService.cs:138-464` 確認）：

- `PlaceOrderCoreAsync` 依序做：FluentValidation → 向連線池借一次連線 → 交易外驗證（票種存在性、RequiresSeat 與請求形狀交叉驗證、座位存在性、跨活動、販售期間快速失敗、實名主要檢查、限購、座位配置與座位圖成員／分區、提早 409）→ `BeginTransactionAsync` → `GetForShareAsync` 鎖 Event 並重驗販售期間 → 實名／座位配置補位（交易外讀不到活動時）與 I1 不變量檢查 → 排隊資格（`GetForUpdateAsync`）→ 鎖座位 → 鎖計數票種 → 組 `SeatSelection`／`QuantitySelection` → `CreateOrderHandler.Handle` → `queueEntry?.Complete()` → `Add` → commit → 先釋放連線 → Redis 入場鏡像同步 → 票種列表快取失效。
- 多個階段帶提前 `return`，加上 `foreach`、`?.`、`is { } x` 模式、`&&` 條件，累計循環複雜度 48（`.editorconfig` 的 `CA1502` 門檻為預設 25）。
- `timings` 在 6 個時間點被寫入（`PlaceOrderAsync` 的 finally 依這些點輸出分段耗時），位置有測試守護（`PlaceOrderAsync_WhenDebugEnabled…` 系列）。
- `await using var connection` 與 `await using var transaction` 的宣告位置決定釋放順序（離開方法時先回滾交易再關連線），commit 後會手動 `connection.DisposeAsync()`。
- 既有測試：`OrderServiceTests`（Application.Tests，PlaceOrder 案例以 1.1 對照表實數為準，含鎖定前拒絕、日誌分段、鎖內重驗、快取失效）與 Infrastructure.Tests 的 `OrderServiceConcurrencyTests`、`OrderServiceEarlyConflictTests`、`OrderServiceEventShareLockTests`、`OrderServiceSingleConnectionTests`、`OrderServiceQueueModeLinearizationTests`。

本 change 設 `skip_specs: true`：沒有任何 Requirement 新增或修改（proposal.md「Capabilities」）。

## Goals / Non-Goals

**Goals:**
- `PlaceOrderCoreAsync` 與每個新抽出的方法循環複雜度都低於 25，CI 不再出現 `CA1502`，也不新增 `S3776`。
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

| 階段 | 建議方法 | 內容 | 回傳 |
| --- | --- | --- | --- |
| 1 | `LoadTicketTypesAsync` | 批次讀票種、依請求順序回報第一個缺漏 | `Result<Dictionary<Guid, TicketType>>` |
| 2 | `GetSelectionShapeErrorOrNull`（static） | RequiresSeat 與請求形狀交叉驗證 | `Error?` |
| 3 | `LoadValidationSeatsAsync` | 座位存在性批次讀取 | `Result<Dictionary<Guid, EventSeat>>` |
| 4 | `GetCrossEventErrorOrNull`（static）＋ 讀活動 | 票種／座位活動聯集檢查，取得唯一 `eventId` 與交易外 `orderEvent` | `Error?` |
| 5 | `GetPreTransactionGateErrorAsync` | 販售期間快速失敗 → 實名主要檢查 → 限購（順序固定） | `Error?` |
| 6 | `LoadSeatPlacementsAsync` ＋ 既有 `GetSeatPlacementErrorOrNull` | 座位樣板讀取、配置、座位圖成員／分區比對 | `Result<IReadOnlyList<SeatPlacement>>` |
| 7 | 保留既有 `GetEarlyConflictOrNull` 呼叫 | 提早 409 | `Error?` |
| 8 | `GetLockedEventErrorOrNull` | 鎖內販售期間重驗、實名／座位配置補位、I1 不變量（丟 `InvalidOperationException`） | `Error?`（例外照丟） |
| 9 | `LockQueueEntryAsync` | 排隊模式資格檢查 | `Result<PurchaseQueueEntry?>` |
| 10 | `LockSeatsAsync` / `LockCountingTicketTypesAsync` | 鎖座位、鎖計數票種 | `Result<Dictionary<…>>` |
| 11 | `BuildSelections` | 組 `SeatSelection`／`QuantitySelection` | `Result<…>` |
| 12 | `SyncAfterCommitAsync` | commit 後：鏡像同步、票種快取失效 | `Task` |

協調者 `PlaceOrderCoreAsync` 保留：FluentValidation、`await using var connection`、`await using var transaction` 的宣告、所有 `timings.X = …` 寫入、`GetForShareAsync` 呼叫與其緊接的 `EventLocked` 時間點、`CreateOrderHandler.Handle`、`queueEntry?.Complete()`、`Add`、`CommitAsync`、`connection.DisposeAsync()`。理由：資源宣告位置與時間點寫入位置是不變量 2–4 的載體，抽走會讓不變量跨方法而難以審查。

### 決策 3：不變量（行為不變準則，取代 Acceptance Criteria）

| # | 不變量 | 守護測試（apply 階段逐條核對並補缺口） |
| --- | --- | --- |
| I1 | 鎖定順序固定 Event → PurchaseQueueEntry → EventSeat → TicketType | `OrderServiceQueueModeLinearizationTests`、`OrderServiceEventShareLockTests`、`OrderServiceConcurrencyTests`，及 `OrderServiceTests` 中「ReturnsX…BeforeQueueCheckAndLocks」系列 |
| I2 | 全部交易外驗證在 `BeginTransactionAsync` 之前（失敗時不開交易） | `…RejectsBeforeOpeningTransaction`、`…BeforeOpeningTransaction`、`…BeforeTakingAnyLock` 系列 |
| I3 | `timings` 6 個時間點的取得位置與順序不變（含「未到達為 null」） | `PlaceOrderAsync_WhenDebugEnabled…` 系列、`…WhenWallClockJumpsDuringOrder…`、`OrderService_DoesNotReadWallClockDirectly` |
| I4 | commit 後先 `connection.DisposeAsync()`，再呼叫 Redis | `OrderServiceSingleConnectionTests` |
| I5 | `now` 在 `GetForShareAsync` 返回之後才取得（等鎖期間跨過停售時點須回 SalesClosed） | `…WhenSalesClosesWhileWaitingForEventLock…` |
| I6 | 所有失敗路徑的錯誤類型與訊息逐字相同；同一請求觸發多個失敗時的優先順序不變 | `OrderServiceTests` 各 `Returns…` 案例（含 `…AndLimitAlsoFail_ReturnsSalesNotOpen`、`…RealNameMissingAndMaxTicketsExceeded…`） |

額外須守住（不屬於上列但同樣可被誤改）：`ConnectionOpened` 之前的 FluentValidation 失敗不借連線；Redis 呼叫與快取失效只在 commit 成功後；純座位訂單不失效票種快取；`RealNameFlag` 前後讀不一致丟 `InvalidOperationException`。

### 決策 4：驗證方式——特徵化測試＋突變驗證，TDD 的 RED 標 N/A

沒有新行為，RED 不存在。保護網為：(1) 重構前先建立「分支 → 測試」對照表，缺口補特徵化測試（在現有碼上就通過，不是 RED）；(2) 記錄重構前全套基準；(3) 重構後重跑比對；(4) 每個被搬動的階段用 `scripts/verify-test-evidence.sh mutate` 暫時改壞一個分支（一次一個），確認至少一個測試失敗，證明保護網有效。理由：特徵化測試通過只證明「現在綠」，只有突變驗證能證明「改壞會紅」。

### 決策 5：複雜度量測方式

CA1502 只在超過門檻時警告，且訊息帶實際值（現況：`'PlaceOrderCoreAsync' has a cyclomatic complexity of '48'`）。要量每個新方法的實際值，暫時新增 `CodeMetricsConfig.txt`（目前不存在）設 `CA1502: 1`，或暫時調低 `.editorconfig` 的 CA1502 設定，取得全部值後還原（刪除該檔）；最終驗收仍以門檻 25 下的建置無 `CA1502`、無新增 `S3776` 為準。暫時設定不得進入 commit。

## Risks / Trade-offs

- [搬移時改變提前 return 的優先順序，例如把限購檢查移到實名檢查之前] → I6；對照表要求「同一請求同時違反 A 與 B」的優先順序案例有測試（`…AndLimitAlsoFail…`、`PlaceOrderAsync_WhenMaxTicketsPerOrderAndSeatZoneBothViolated_ReturnsMaxTicketsErrorBeforeOpeningTransaction` 已存在），缺口補測。
- [`await using` 宣告被抽走導致釋放順序改變，commit 後 Redis 呼叫期間連線仍被占用] → 宣告留在協調者（決策 2）；I4 由 `OrderServiceSingleConnectionTests` 守護，並以突變驗證（把 `connection.DisposeAsync()` 移到 Redis 呼叫之後）證明測試會失敗。
- [時間點寫入位置偏移一行，分段耗時失真但功能測試全綠] → I3 的日誌案例逐段斷言 null／非 null；突變驗證移動一個 `timings` 寫入點。
- [階段切分過細，協調者變成長串 `if (error is not null)`，可讀性未必提升] → 以複雜度門檻為客觀停止條件：每個方法低於 25 即可，不追求更細。
- [補特徵化測試時順手修改既有測試] → 只新增，不改既有測試斷言；若發現既有測試本身有誤，停止並回報。
- [與 `design.md 決策 N` 引用的註解脫離上下文] → 註解隨程式碼搬移、內容不改。
