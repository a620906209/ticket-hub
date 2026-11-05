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
> | I6：失敗路徑的錯誤類型、訊息與同請求多重違規的優先順序不變 | 1.1 對照表、3.6a／3.6b 突變驗證 |
> | C1：`PlaceOrderCoreAsync` 與所有新方法循環複雜度 ≤ 25（CA1502 僅在大於門檻時警告）；建置無 `CA1502`（Application 專案不受 `S3776` 分析，與 1.3 基準相比警告清單只少 `CA1502` 一筆；`S3776` 屬 WebApi 既有的 `ReconcileEventQueueAsync`，不變） | 2.5 |
>
> 證據一律貼原文（CLAUDE.md「證據貼原文」）：每個失敗測試的名稱與訊息、每個測試專案的摘要行、`git stash` 指令輸出；不得以數字、「全數通過」或「與 X 相同」代替。
>
> TDD：純重構、無新增可觀察行為，RED 標示 N/A（特徵化測試在重構前就通過是預期，不是 RED）；「測試在行為被破壞時會失敗」改由第 3 節突變驗證證明（design.md 決策 4）。CLAUDE.md 的「RED 還原驗證」（`restore`）同樣 N/A：還原後測試照樣通過是行為不變重構的預期結果，其責任由 `mutate` 承擔。未標 TDD 的 task 一律視為 TDD：N/A（理由同上），不再逐項重複。
>
> 前端：無變更，不需真實瀏覽器驗證。
>
> 提交規則：不自動 commit／push；暫時性設定（複雜度量測、突變）不得進入 commit。

## 1. 保護網（先於任何 OrderService.cs 修改）

- [ ] 1.1 建立「分支 → 測試」對照表：逐段讀 `PlaceOrderCoreAsync` 的每個失敗 return、每個條件分支與每個副作用（commit、連線釋放、鏡像同步、快取失效、`timings` 寫入），在 `OrderServiceTests`（Application.Tests）與 Infrastructure.Tests 的 `OrderService*Tests` 中找到對應測試，並標出 I1–I6 各自的守護測試；對照表加兩欄：「失敗訊息是否逐字斷言」（I6 範圍含訊息）與「優先順序組合」（列出所有可獨立對調的相鄰檢查，至少：形狀檢查 :169–190 ↔ 驗證用座位載入 :194–209、票種載入 ↔ 形狀檢查、驗證用座位載入 ↔ 跨活動檢查、跨活動檢查 ↔ 活動讀取（跨活動失敗時斷言 `EventRepository.GetByIdCallCount == 0`，因活動讀取本身不會失敗）、座位配置比對 ↔ 提早 409（:287–290 ↔ :295–299）、限購 ↔ 座位配置、限購 ↔ 提早 409、實名主檢查 ↔ 限購、販售期間 ↔ 實名、階段 8 內實名補位 ↔ 座位配置補位；每組須有「同一請求同時違反兩者」案例，缺者在 1.2 補，例如計數票種帶不存在的 EventSeatId 須回 Validation 而非 NotFound）（I1 的 EventSeat → TicketType 順序目前無呼叫順序斷言，列為缺口；各不變量對應的主 spec 編號見 design.md 決策 4）。疑似缺口（以實際對照為準，不憑印象）：鎖內 `lockedEvent is null` → NotFound（:314–317）、鎖內「座位不存在」（OrderService.cs:375、:418）、鎖內「票種不存在」（:395）、鎖內 `ticketType.EventId != eventSeat.EventId`（:423）、`orderEvent` 非 null 但鎖內讀到不同 `IsRealNameRequired` 的兩個方向（已有）、`CreateOrderHandler` 失敗後 `queueEntry.Complete()` 不被呼叫。驗證：對照表寫入完成紀錄，每條分支列出測試名稱或「缺口」。
  - 完成紀錄：（待填）
  - TDD：N/A（唯讀分析）。
- [ ] 1.2 對 1.1 列出的缺口新增特徵化測試（只新增，不改既有測試斷言），放在 `tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs` 既有風格內；若某缺口分支在現行程式碼下不可達（例如被交易外檢查完全擋下），記錄不可達理由，不硬寫測試。必須新增一個鎖定呼叫順序特徵化測試。既有 Fixture 用手寫 Fake（只有 `GetForUpdateCallCount`，無跨 repository 的呼叫紀錄），採用方式：Fake 皆為 sealed 且方法非 virtual，無法子類別化；改為最小幅度修改 `tests/**/TestSupport/Fake*.cs`（GetForShare、PurchaseQueueEntry、EventSeat、TicketType 四個 Fake 加共用順序紀錄掛點，例如可選的 `List<string>`，只加紀錄、不改既有行為）或寫實作同一介面的 decorator，二擇一並於完成紀錄說明（proposal Impact 已列入 `TestSupport/Fake*.cs`）。斷言 Event（`GetForShareAsync`）→ PurchaseQueueEntry → EventSeat → TicketType 的整體順序。**另一個無條件必做**：`FakePurchaseQueueRepository` 加可選、預設不啟用的 `GetForUpdate` callback（`FakeDateTimeProvider.UtcNow` 有 public setter，可在 callback 內推進時鐘），新增特徵化測試：在 callback 內把時鐘推進到超過 `AdmissionExpiresAtUtc`，斷言回傳 QueueAdmissionRequired 且 `OrderRepository.Data` 為空（守 TP-ORDER-014「以檢查當下最新狀態為準」；階段 9 須自行讀 now，不得重用階段 8 的 now）。測試情境必須是：排隊模式開啟且已 Admitted，且同一請求同時含座位項目與計數票種項目（鎖座位只在有座位時發生 :372、鎖票種只在有計數票種時發生 :392，缺一則順序斷言空轉，3.1 的對調 mutation 不會失敗）。另有三組**無條件必做**的特徵化測試（缺口現在即可確認）：(1) `GetForShareOverride` 回傳 null 的案例，斷言回 NotFound 且 `EventLocked` 已寫入（現有日誌測試只斷言 `BeOfType<double>` 或 null，且無 `lockedEvent == null` 案例）；(2) timings 分段測試（timings 用 static `Stopwatch.GetTimestamp()`，無注入點，只能斷言對應分段毫秒值 ≥ 0 且各分段存在／為 null 的條件；`FakeUnitOfWork.BeginTransactionAsync` 同步完成，兩次 timestamp 可能相等，故在 Fake 的 `BeginTransactionAsync` 與 `GetForShareAsync` 加極短 `Task.Delay` 讓分段可區分，僅限該測試啟用）；(3) 失敗訊息逐字斷言：下列片段目前在 tests/** 完全無斷言，須以表格驅動測試逐條斷言 `Error.Message`——"does not require a seat"、"requires a seat but none"、"One or more selected seats were not found"、"One or more selected ticket types"、"requires purchase queue admission"、"requires real-name registration"；其中 "does not belong to the same event"（:423–425）因 `EventSeat` 建構子為 internal 且交易外已保證單一活動，公開 API 不可達：由 1.1 判定可達性並記錄不可達理由，**不納入無條件清單、不用反射硬造**（:418 的 TryGetValue 同樣由 1.1 判定）。鎖內數量比對 :377（座位）與 :397（票種）**可達**（"One or more selected seats were not found." 與 "…ticket types were not found."）：在 `GetForShareOverride`（交易外讀取之後、鎖內 `GetForUpdateAsync` 之前執行）的副作用裡清空 `EventSeatRepository.Data`／`TicketTypeRepository.Data` 再回傳活動，即可觸發，須各寫一個特徵化測試，斷言 NotFound 與逐字訊息；1.1 對照表另列其餘失敗訊息是否已逐字斷言，缺者併入此測試。驗證：`docker compose exec api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~OrderServiceTests"` 通過；新測試在**未修改 OrderService.cs** 的狀態下通過。
  - 完成紀錄：（待填：新增測試名稱、命令與摘要行原文）
  - TDD：N/A（特徵化測試，理由見上方）。
- [ ] 1.3 重構前基準：`docker compose exec api dotnet test` 全套各測試專案摘要行；`docker compose exec -T api dotnet build --no-incremental` 的 warning 清單（預期含 `CA1502` 48 與 `S3776` 28 兩筆，數值以本 task 實測原文為準），作為第 5 節比對基準（`RealNameLoggingTests`、`HostShutdownSeqFlushTests`、時間相關測試有已知偶發失敗，失敗時照實記錄並單獨重跑）。
  - 完成紀錄：（待填）
- [ ] 1.4 量測各階段目前的複雜度基準：依 design.md 決策 5 暫時量測：新增根目錄 `CodeMetricsConfig.txt`（`CA1502(Method): 1`；CA1502 只回報大於門檻的成員，輸出中未出現的方法複雜度視為 1，量測方式是否可用以本 task 實測為準），並在 `src/ProjectC.Application/ProjectC.Application.csproj` 暫時加入 `<AdditionalFiles Include="..\..\CodeMetricsConfig.txt" />`；記錄 `PlaceOrderCoreAsync`（48）與 `GetSeatPlacementErrorOrNull`、`GetEarlyConflictOrNull` 等既有成員的值，確認量測方式可用後兩者都還原。量測命令：`docker compose exec -T api dotnet build --no-incremental 2>&1 | grep '/OrderService.cs(.*CA1502' | sort -u`（門檻設 1 時會冒出大量警告且 solution 建置會重複印出，故過濾並去重）。還原：刪除 `CodeMetricsConfig.txt` 並 `git checkout -- src/ProjectC.Application/ProjectC.Application.csproj`（避免手動刪行動到 BOM／CRLF）。驗證：`git status --short -- .editorconfig CodeMetricsConfig.txt src/ProjectC.Application/ProjectC.Application.csproj` 還原後為空。
  - 完成紀錄：（待填）
  - TDD：N/A（量測）。

## 2. 拆分 PlaceOrderCoreAsync

> 每個子 task 完成後立即執行：`docker compose exec api dotnet build` 與
> `docker compose exec api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~OrderServiceTests"`；不等到全部拆完才驗證。搬移為逐行剪下貼上，禁止順手改名、重排條件、改寫註解或調整檢查順序。階段切分見 design.md 決策 2，可依實際程式微調，但須遵守決策 3 不變量。

- [ ] 2.1 交易外前半（階段 1–4）：抽出票種載入、RequiresSeat 形狀驗證、驗證用座位載入、跨活動檢查與活動讀取。引入 private record 承載中間資料。驗證：build、`OrderServiceTests` 通過，且 diff 核對各階段呼叫順序（含 I/O 與提前 return）與原碼一致。
  - 完成紀錄：（待填）
  - TDD：N/A（行為不變重構，保護網為 1.2 與第 3 節）。
- [ ] 2.2 交易外後半（階段 5–7）：抽出販售期間／實名／限購閘門（順序固定）、座位配置載入與座位圖比對；提早 409 的呼叫維持原位置。驗證：build、`OrderServiceTests` 通過，且 diff 核對各階段呼叫順序（含 I/O 與提前 return）與原碼一致。
  - 完成紀錄：（待填）
- [ ] 2.3 鎖內（階段 8–11）：抽出鎖內販售期間重驗與實名／座位配置補位（實名旗標不變量例外原樣丟出，非本 change 的 I1）、排隊資格、鎖座位、鎖計數票種、組 selections。`GetForShareAsync`、緊接的 `EventLocked` 時間點寫入與 `lockedEvent is null` → NotFound 留在協調者。驗證：build、`OrderServiceTests` 與 `docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~OrderService"` 通過，且 diff 核對呼叫順序（含 `now` 取得位置）與原碼一致。
  - 完成紀錄：（待填）
- [ ] 2.4 commit 後（階段 12）與協調者收尾：抽出鏡像同步與快取失效；確認 `await using` 宣告、`timings` 寫入、`CommitAsync`、`connection.DisposeAsync()` 仍在協調者且順序不變。驗證：build、上述兩組測試通過，且 diff 核對呼叫順序與原碼一致。
  - 完成紀錄：（待填）
- [ ] 2.5 複雜度驗收（C1）：`docker compose exec -T api dotnet build --no-incremental` warning 清單不再含 `CA1502`，且與 1.3 基準相比只少了 `CA1502` 一筆（`S3776` 屬 WebApi 的 `ReconcileEventQueueAsync`，Application 不受 Sonar 分析，不作為驗收；若新方法引入新警告，於本 task 修掉後重跑，不得留待後續）；再依 design.md 決策 5 暫時量測並記錄 `PlaceOrderCoreAsync` 與每個新方法的實際值（全部 ≤ 25），還原設定。驗證：warning 清單與各方法數值原文；`git status --short -- .editorconfig CodeMetricsConfig.txt src/ProjectC.Application/ProjectC.Application.csproj` 為空。
  - 完成紀錄：（待填）

## 3. 突變驗證（證明保護網有效；使用 `bash scripts/verify-test-evidence.sh mutate`，一次一個改動，改完還原並重跑確認恢復通過）

> 適用 3.1–3.7 全部：任一 mutation 若沒有測試失敗，停止並回報（保護網缺口），回 1.2 補測試後重做該項。**回頭補的測試必須先在原碼上驗證**：因第 2 節已重構，補測試前先還原到重構前的 OrderService.cs，確認新測試在原碼上**通過**，再還原回重構碼並確認也通過。做法：第 2、3 節進行期間不得 commit（否則 stash 無法回到原碼）；以 `git stash push -- src/ProjectC.Application/Orders/OrderService.cs` 暫存重構版，跑新測試（預期通過），再 `git stash pop`。注意 `verify-test-evidence.sh restore` 的語意相反（測試失敗＝exit 0、測試仍通過＝exit 1「證據不成立」），此處預期通過，不要用它判定，只能用它的 stash 機制或直接手動 stash（CLAUDE.md 要求未 commit 修改用該腳本，此處為例外並於完成紀錄說明）；兩次輸出與 `git stash list`／pop 輸出都貼原文，否則新測試可能把重構造成的回歸固定成「正確行為」。pop 失敗或有衝突時立即停止並回報。`OrderService.cs` 為 CRLF，優先用單行替換；多行 `--from` 須含 `\r\n`，bash 寫法如 `--from $'line1\r\nline2'`（3.5 的做法：`--from` 為協調者內「`var now = _dateTimeProvider.UtcNow;`」那一整行加 `\r\n`，`--to` 為空字串＝刪除該行；另需把同樣一行插到 `GetForShareAsync` 呼叫行之前，故 3.5 用單一多行 `--from`＝「`GetForShareAsync` 呼叫行＋ `EventLocked` 寫入＋ null 檢查＋ `var now` 行」連續區段、`--to`＝把 `var now` 行置於最前的同一區段；實際字串以重構後內容為準，必須恰好匹配一次且可編譯，否則 mutate exit 2）。各項的測試命令：3.1–3.3、3.5、3.6a／3.6b、3.7 跑 `docker compose exec api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~OrderServiceTests"`；3.4 只用 `docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~OrderServiceSingleConnectionTests"`（Testcontainers；mutate 一次只接一個測試命令）。mutation 必須在目前（未重構或已重構）的程式碼結構上可達成，無法達成時改選等價的其他破壞方式並記錄。

- [ ] 3.1 I1：暫時對調鎖內「鎖座位」與「鎖計數票種」的順序；至少一個測試（1.2 新增的鎖定順序測試）須失敗。驗證：命令、失敗測試名稱與訊息原文、還原後摘要行。
  - 完成紀錄：（待填）
- [ ] 3.2 I2：暫時把某個交易外驗證（例如座位配置比對）移到 `BeginTransactionAsync` 之後；至少一個「BeforeOpeningTransaction」測試須失敗。
  - 完成紀錄：（待填）
- [ ] 3.3 I3：暫時把 `timings.EventLocked = …` 移到 `lockedEvent is null` 檢查之後；1.2 新增的「`lockedEvent == null` 時 `EventLocked` 已寫入」測試須失敗。再各做一次：把 `TransactionStarting` 與 `TransactionBegun` 的寫入互換，1.2 新增的時間點先後順序測試須失敗。其餘時間點（`ConnectionOpened`、`CommitStarting`、`Committed`）因 Stopwatch 為真實時鐘無法完整以測試守護，由 2.x 的 diff 審查守護，並在完成紀錄寫明。
  - 完成紀錄：（待填）
- [ ] 3.4 I4：暫時把 `connection.DisposeAsync()` 移到鏡像同步之後；`OrderServiceSingleConnectionTests` 須失敗。
  - 完成紀錄：（待填）
- [ ] 3.5 I5：暫時把鎖內販售期間檢查的 `_dateTimeProvider.UtcNow` 取得移到 `GetForShareAsync` 之前（2.3 實作時 `now` 由協調者在 `GetForShareAsync` 之後以單行 `var now = _dateTimeProvider.UtcNow;` 取得並傳入階段 8；mutation 以 mutate 的一次字面替換把 `var now = …` 那行移到 `GetForShareAsync` 呼叫行之前，`--from` 含 `\r\n`）；`…WhenSalesClosesWhileWaitingForEventLock…` 須失敗。
  - 完成紀錄：（待填）
- [ ] 3.6a I6 優先順序：暫時對調實名檢查與限購檢查；至少 `…RealNameMissingAndMaxTicketsExceeded…` 須失敗（`…AndLimitAlsoFail…` 守的是販售期間優先，不以它判定）。
  - 完成紀錄：（待填）
- [ ] 3.6b I6 訊息：暫時互換兩則 Validation 訊息文字（例如 "does not require a seat" 與 "requires a seat but none" 兩則，位置以方法內容為準、不以重構前行號為準）；1.2 新增的訊息逐字斷言測試須失敗。
  - 完成紀錄：（待填）
- [ ] 3.7 階段覆蓋突變（design 決策 4 承諾每個被搬動的階段都有突變；停止規則與還原流程同本節前言）：各做一次，各至少一個測試須失敗——階段 1：票種載入後移除「票種不存在」比對；階段 3：移除驗證用座位不存在比對（原碼無此分支則記錄並改選等價分支）；階段 4：跨活動檢查的 `> 1` 改 `> 2`；階段 9a：排隊資格 `>` 改 `>=`（AdmissionExpiresAtUtc 邊界，`OrderServiceTests.cs:679` 的 `…AdmissionExpiresExactlyAtCheckTime` 須失敗）；階段 9b：**單一次 mutate**（mutate 每次只做一次替換並於結束時還原，不可拆兩步疊加）：`--from` 取階段 9 內「`GetForUpdateAsync` 呼叫行 ＋ 其後到 `AdmissionExpiresAtUtc > _dateTimeProvider.UtcNow` 比較」的連續多行（原碼 :351–353，該比較全檔僅出現一次；重構後兩者若不相鄰，仍用單一 `--from` 涵蓋兩者），`--to` 在呼叫前插入 `var staleNow = _dateTimeProvider.UtcNow;` 並把比較改為 `> staleNow`；以 `$'…\r\n…'` 寫成完整字串，實際內容以重構後的 `LockQueueEntryAsync` 為準，須編譯通過（編譯失敗不算突變被抓到）（1.2 新增的 queue `GetForUpdate` callback 推進時鐘測試須失敗）；階段 10：移除鎖內數量比對，:375 與 :397 各一次，且 `--from` 須帶上下文（`eventSeats.Count < eventSeatIds.Count` 在 :609 也出現）；階段 11（1.1 須先判定 `BuildSelections` 是否存在可達且可被測試觀察的分支；若沒有，於 1.1 記錄「階段 11 無可達突變」並改由 diff 審查守護，不在第 3 節才卡住）：`BuildSelections` 的 `EventId` 比對（:423 預期不可達，改選等價且可達的分支並記錄，不選 :418）；階段 12：拿掉 `quantitySelections.Count > 0` 條件。測試命令同 3.1。
  - 完成紀錄：（待填）

## 4. 文件

- [ ] 4.1 更新 `docs/project-scope.md`「已知技術債：下單核心方法複雜度」條目：標示已由 `place-order-core-complexity` 處理（日期、結果：複雜度 48 → 實測最大值、`CA1502` 已消失），其餘敘述不動；`ReconcileEventQueueAsync` 條目不動。驗證：`git diff -- docs/project-scope.md` 僅該條目。
  - 完成紀錄：（待填）
  - TDD：N/A（純文件）。

## 5. 完整驗證與審查前檢查

- [ ] 5.1 全套回歸：`docker compose exec api dotnet test` 各測試專案，與 1.3 基準逐專案比對；與 1.3 基準（已含 1.2 新增的案例）相比，Application.Tests 的總數只增加第 3 節回頭補的案例數（沒有補則相同），其他專案總數不變。偶發失敗須單獨重跑並貼出兩次結果。驗證：各 DLL 摘要行原文。
  - 完成紀錄：（待填）
- [ ] 5.2 hardener 最終檢查：`OrderService` 屬 I/O 與交易管理程式碼，適用 `.claude/skills/hardener/SKILL.md` 清單；因本 change 不改行為，預期是核對既有防禦分支未在搬移中遺失（參數驗證、Entity 存在性、`CancellationToken` 傳遞到每個新方法的 I/O 呼叫、例外邊界、日誌遮蔽）。若發現缺口，依 TDD 新增「hardener 補強」task。驗證：檢查清單逐項結果。
  - 完成紀錄：（待填）
- [ ] 5.3 `graphify update .` 更新知識圖。驗證：命令成功結束。
  - 完成紀錄：（待填）

## Workflow follow-up

- 依 CLAUDE.md 流程：strict-reviewer 與全新對抗式 subagent 依序審查（呼叫 strict-reviewer 時須把 1.2、第 3 節與 5.1 的證據完整附在 prompt），修到兩者皆無 blocking。
- 審查通過後由使用者明確指示才 `/opsx:archive`（`skip_specs`）並 merge 回 master；任何步驟都不自動 commit／push。
