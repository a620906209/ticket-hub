# Tasks

> 所有指令都在容器內執行（`docker compose exec api dotnet test ...`），不在本機執行 dotnet。
>
> 本 change 為純重構，無新增 Acceptance Criteria（`skip_specs: true`，見 proposal.md）。以下「行為不變準則」取代 AC（定義見 design.md 決策 3），每一條都對應驗證 task：
>
> | 行為不變準則 | 驗證 |
> | --- | --- |
> | I1：鎖定順序 Event → PurchaseQueueEntry → EventSeat → TicketType | 1.1 對照表、3.1 突變驗證 |
> | I2：交易外驗證都在 `BeginTransactionAsync` 之前 | 1.1 對照表、3.2 突變驗證 |
> | I3：`timings` 6 個時間點取得位置與順序不變 | 1.1 對照表、3.3 突變驗證 |
> | I4：commit 後先 `connection.DisposeAsync()` 再呼叫 Redis | 1.1 對照表、3.4 突變驗證 |
> | I5：`now` 在 `GetForShareAsync` 返回之後才取得 | 1.1 對照表、3.5 突變驗證 |
> | I6：失敗路徑的錯誤類型、訊息與同請求多重違規的優先順序不變 | 1.1 對照表、3.6 突變驗證 |
> | C1：`PlaceOrderCoreAsync` 與所有新方法循環複雜度 < 25；無新增 `S3776` | 2.5 |
>
> 證據一律貼原文（CLAUDE.md「證據貼原文」）：每個失敗測試的名稱與訊息、每個測試專案的摘要行、`git stash` 指令輸出；不得以數字、「全數通過」或「與 X 相同」代替。
>
> TDD：純重構、無新增可觀察行為，RED 標示 N/A（特徵化測試在重構前就通過是預期，不是 RED）；「測試在行為被破壞時會失敗」改由第 3 節突變驗證證明（design.md 決策 4）。
>
> 提交規則：不自動 commit／push；暫時性設定（複雜度量測、突變）不得進入 commit。

## 1. 保護網（先於任何 OrderService.cs 修改）

- [ ] 1.1 建立「分支 → 測試」對照表：逐段讀 `PlaceOrderCoreAsync` 的每個失敗 return、每個條件分支與每個副作用（commit、連線釋放、鏡像同步、快取失效、`timings` 寫入），在 `OrderServiceTests`（Application.Tests）與 Infrastructure.Tests 的 `OrderService*Tests` 中找到對應測試，並標出 I1–I6 各自的守護測試。疑似缺口（以實際對照為準，不憑印象）：鎖內「座位不存在」（OrderService.cs:375、:418）、鎖內「票種不存在」（:395）、鎖內 `ticketType.EventId != eventSeat.EventId`（:423）、`orderEvent` 非 null 但鎖內讀到不同 `IsRealNameRequired` 的兩個方向（已有）、`CreateOrderHandler` 失敗後 `queueEntry.Complete()` 不被呼叫。驗證：對照表寫入完成紀錄，每條分支列出測試名稱或「缺口」。
  - 完成紀錄：（待填）
  - TDD：N/A（唯讀分析）。
- [ ] 1.2 對 1.1 列出的缺口新增特徵化測試（只新增，不改既有測試斷言），放在 `tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs` 既有風格內；若某缺口分支在現行程式碼下不可達（例如被交易外檢查完全擋下），記錄不可達理由，不硬寫測試。驗證：`docker compose exec api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~OrderServiceTests"` 通過；新測試在**未修改 OrderService.cs** 的狀態下通過。
  - 完成紀錄：（待填：新增測試名稱、命令與摘要行原文）
  - TDD：N/A（特徵化測試，理由見上方）。
- [ ] 1.3 重構前基準：`docker compose exec api dotnet test` 全套各測試專案摘要行；`docker compose exec -T api dotnet build --no-incremental` 的 warning 清單（含 `CA1502` 48 與 `S3776` 28 兩筆），作為第 5 節比對基準（`RealNameLoggingTests`、`HostShutdownSeqFlushTests`、時間相關測試有已知偶發失敗，失敗時照實記錄並單獨重跑）。
  - 完成紀錄：（待填）
- [ ] 1.4 量測各階段目前的複雜度基準：依 design.md 決策 5 暫時量測（新增 `CodeMetricsConfig.txt` 設 `CA1502: 1`），記錄 `PlaceOrderCoreAsync`（48）與 `GetSeatPlacementErrorOrNull`、`GetEarlyConflictOrNull` 等既有成員的值，確認量測方式可用後還原設定。驗證：`git status --short -- .editorconfig CodeMetricsConfig.txt` 還原後為空。
  - 完成紀錄：（待填）
  - TDD：N/A（量測）。

## 2. 拆分 PlaceOrderCoreAsync

> 每個子 task 完成後立即執行：`docker compose exec api dotnet build` 與
> `docker compose exec api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~OrderServiceTests"`；不等到全部拆完才驗證。搬移為逐行剪下貼上，禁止順手改名、重排條件、改寫註解或調整檢查順序。階段切分見 design.md 決策 2，可依實際程式微調，但須遵守決策 3 不變量。

- [ ] 2.1 交易外前半（階段 1–4）：抽出票種載入、RequiresSeat 形狀驗證、驗證用座位載入、跨活動檢查與活動讀取。引入 private record 承載中間資料。驗證：build、`OrderServiceTests` 通過。
  - 完成紀錄：（待填）
  - TDD：N/A（行為不變重構，保護網為 1.2 與第 3 節）。
- [ ] 2.2 交易外後半（階段 5–7）：抽出販售期間／實名／限購閘門（順序固定）、座位配置載入與座位圖比對；提早 409 的呼叫維持原位置。驗證：build、`OrderServiceTests` 通過。
  - 完成紀錄：（待填）
- [ ] 2.3 鎖內（階段 8–11）：抽出鎖內販售期間重驗與實名／座位配置補位（I1 不變量例外原樣丟出）、排隊資格、鎖座位、鎖計數票種、組 selections。`GetForShareAsync` 與緊接的 `EventLocked` 時間點寫入留在協調者。驗證：build、`OrderServiceTests` 與 `docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~OrderService"` 通過。
  - 完成紀錄：（待填）
- [ ] 2.4 commit 後（階段 12）與協調者收尾：抽出鏡像同步與快取失效；確認 `await using` 宣告、`timings` 寫入、`CommitAsync`、`connection.DisposeAsync()` 仍在協調者且順序不變。驗證：build、上述兩組測試通過。
  - 完成紀錄：（待填）
- [ ] 2.5 複雜度驗收（C1）：`docker compose exec -T api dotnet build --no-incremental` warning 清單不再含 `CA1502`，且 `S3776` 只剩既有的 `ReconcileEventQueueAsync` 一筆；再依 design.md 決策 5 暫時量測並記錄 `PlaceOrderCoreAsync` 與每個新方法的實際值（全部 < 25），還原設定。驗證：warning 清單與各方法數值原文；`git status --short -- .editorconfig CodeMetricsConfig.txt` 為空。
  - 完成紀錄：（待填）

## 3. 突變驗證（證明保護網有效；使用 `bash scripts/verify-test-evidence.sh mutate`，一次一個改動，改完還原並重跑確認恢復通過）

- [ ] 3.1 I1：暫時對調鎖內「鎖座位」與「鎖計數票種」的順序，或把排隊資格檢查移到 `GetForShareAsync` 之前；至少一個測試須失敗。驗證：命令、失敗測試名稱與訊息原文、還原後摘要行。若沒有測試失敗，停止並回報（保護網缺口，回 1.2 補測試）。
  - 完成紀錄：（待填）
- [ ] 3.2 I2：暫時把某個交易外驗證（例如座位配置比對）移到 `BeginTransactionAsync` 之後；至少一個「BeforeOpeningTransaction」測試須失敗。
  - 完成紀錄：（待填）
- [ ] 3.3 I3：暫時移動一個 `timings` 寫入點（例如 `EventLocked` 移到 null 檢查之後或 `TransactionBegun` 移到 `BeginTransactionAsync` 之前）；至少一個日誌分段測試須失敗。
  - 完成紀錄：（待填）
- [ ] 3.4 I4：暫時把 `connection.DisposeAsync()` 移到鏡像同步之後；`OrderServiceSingleConnectionTests` 須失敗。
  - 完成紀錄：（待填）
- [ ] 3.5 I5：暫時把鎖內販售期間檢查的 `_dateTimeProvider.UtcNow` 取得移到 `GetForShareAsync` 之前；`…WhenSalesClosesWhileWaitingForEventLock…` 須失敗。
  - 完成紀錄：（待填）
- [ ] 3.6 I6：暫時對調實名檢查與限購檢查，以及修改一則失敗訊息的文字；`…AndLimitAlsoFail…`／`…RealNameMissingAndMaxTicketsExceeded…` 與對應訊息斷言的測試須失敗。
  - 完成紀錄：（待填）

## 4. 文件

- [ ] 4.1 更新 `docs/project-scope.md`「已知技術債：下單核心方法複雜度」條目：標示已由 `place-order-core-complexity` 處理（日期、結果：複雜度 48 → 實測最大值、`CA1502` 已消失），其餘敘述不動；`ReconcileEventQueueAsync` 條目不動。驗證：`git diff -- docs/project-scope.md` 僅該條目。
  - 完成紀錄：（待填）
  - TDD：N/A（純文件）。

## 5. 完整驗證與審查前檢查

- [ ] 5.1 全套回歸：`docker compose exec api dotnet test` 各測試專案，與 1.3 基準逐專案比對；Application.Tests 的總數只增加 1.2 新增的案例數。偶發失敗須單獨重跑並貼出兩次結果。驗證：各 DLL 摘要行原文。
  - 完成紀錄：（待填）
- [ ] 5.2 hardener 最終檢查：`OrderService` 屬 I/O 與交易管理程式碼，適用 `.claude/skills/hardener/SKILL.md` 清單；因本 change 不改行為，預期是核對既有防禦分支未在搬移中遺失（參數驗證、Entity 存在性、`CancellationToken` 傳遞到每個新方法的 I/O 呼叫、例外邊界、日誌遮蔽）。若發現缺口，依 TDD 新增「hardener 補強」task。驗證：檢查清單逐項結果。
  - 完成紀錄：（待填）
- [ ] 5.3 `graphify update .` 更新知識圖。驗證：命令成功結束。
  - 完成紀錄：（待填）

## Workflow follow-up

- 依 CLAUDE.md 流程：strict-reviewer 與全新對抗式 subagent 依序審查（呼叫 strict-reviewer 時須把 1.2、第 3 節與 5.1 的證據完整附在 prompt），修到兩者皆無 blocking。
- 審查通過後由使用者明確指示才 `/opsx:archive`（`skip_specs`）並 merge 回 master；任何步驟都不自動 commit／push。
