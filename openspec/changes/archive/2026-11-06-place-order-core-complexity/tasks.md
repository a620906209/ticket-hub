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

- [x] 1.1 建立「分支 → 測試」對照表：逐段讀 `PlaceOrderCoreAsync` 的每個失敗 return、每個條件分支與每個副作用（commit、連線釋放、鏡像同步、快取失效、`timings` 寫入），在 `OrderServiceTests`（Application.Tests）與 Infrastructure.Tests 的 `OrderService*Tests` 中找到對應測試，並標出 I1–I6 各自的守護測試；對照表加兩欄：「失敗訊息是否逐字斷言」（I6 範圍含訊息）與「優先順序組合」（列出所有可獨立對調的相鄰檢查，至少：形狀檢查 :169–190 ↔ 驗證用座位載入 :194–209、票種載入 ↔ 形狀檢查、驗證用座位載入 ↔ 跨活動檢查、跨活動檢查 ↔ 活動讀取（跨活動失敗時斷言 `EventRepository.GetByIdCallCount == 0`，因活動讀取本身不會失敗）、座位配置比對 ↔ 提早 409（:287–290 ↔ :295–299）、限購 ↔ 座位配置、限購 ↔ 提早 409、實名主檢查 ↔ 限購、販售期間 ↔ 實名、階段 8 內實名補位 ↔ 座位配置補位；每組須有「同一請求同時違反兩者」案例，缺者在 1.2 補，例如計數票種帶不存在的 EventSeatId 須回 Validation 而非 NotFound）（I1 的 EventSeat → TicketType 順序目前無呼叫順序斷言，列為缺口；各不變量對應的主 spec 編號見 design.md 決策 4）。疑似缺口（以實際對照為準，不憑印象）：鎖內 `lockedEvent is null` → NotFound（:314–317）、鎖內「座位不存在」（OrderService.cs:375、:418）、鎖內「票種不存在」（:395）、鎖內 `ticketType.EventId != eventSeat.EventId`（:423）、`orderEvent` 非 null 但鎖內讀到不同 `IsRealNameRequired` 的兩個方向（已有）、`CreateOrderHandler` 失敗後 `queueEntry.Complete()` 不被呼叫。驗證：對照表寫入完成紀錄，每條分支列出測試名稱或「缺口」。
  - 完成紀錄：對照表（測試皆在 `OrderServiceTests`；★＝本 change 1.2 新增）：
    - I1 鎖定順序：★`…LocksEventThenQueueEntryThenSeatThenTicketType`（Event→Queue→EventSeat→TicketType，補上原有 EventSeat→TicketType 缺口）。
    - I2 交易外驗證在 BeginTransactionAsync 前：既有 `…ReturnsConflictBeforeOpeningTransaction`、★`…MaxTicketsExceededAndSeatAlreadySold…`、★`…SeatFromAnotherSeatMapIsAlsoSold…`。
    - I3 timings：★`…WhenLockedEventIsNull_ReturnsNotFoundAndRecordsEventLockedTimestamp`、★`…TransactionAndEventLockAreSlow_Records…Segments`、既有 `…WhenWallClockJu…`。
    - I4 commit 後先釋放連線再呼叫 Redis：Infrastructure `OrderServiceSingleConnectionTests…RedisCallsRunAfterConnectionReturned`。
    - I5 `now` 於 GetForShareAsync 後取得：既有 `…WhenSalesClosesWhileWaitingForEventLock…`；排隊時鐘：★`…AdmissionExpiresWhileWaitingForQueueEntryLock…`、既有 `…AdmissionExpiresExactlyAtCheckTime`。
    - I6 訊息逐字：★`…CountingTicketTypeSpecifiesEventSeatId…`、★`…SeatTicketTypeHasNoEventSeatId…`、★`…SeatDoesNotExistOutsideTransaction…`、★`…SeatsDisappearBetweenOutsideReadAndLock…`、★`…CountingTicketTypesDisappear…`、★`…QueueModeEnabledAndNotAdmitted…`、★`…RealNameRequired…OutsideTransaction／UnderLock`。
    - I6 優先順序：★形狀 vs 座位載入、★票種載入 vs 形狀、★座位載入 vs 跨活動、★跨活動 vs 活動讀取（`GetByIdCallCount == 0`）、★座位配置 vs 提早 409、★限購 vs 提早 409、★鎖內實名 vs 座位配置、既有 `…RealNameMissingAndMaxTicketsExceeded…`。
    - I6 既有順序守護（相鄰檢查）：限購 vs 座位分區比對：既有 `…WhenMaxTicketsPerOrderAndSeatZoneBothViolated_ReturnsMaxTicketsErrorBeforeOpeningTransaction`；販售期間快速失敗 vs 實名 vs 限購：既有 `…WhenSalesNotOpenAndRealNameAndLimitAlsoFail_ReturnsSalesNotOpen`。
    - 副作用：commit／`Complete()`：★`…HandlerRejectsWithConflict_DoesNotCompleteQueueEntry`；鏡像同步與票種快取失效：既有 `…WithSeatOnlySelection_DoesNotInvalidateTicketTypesCache` 與 Infrastructure `OrderService*Tests`。
    - 不可達判定：BuildSelections 內 `ticketType.EventId != eventSeat.EventId`（原 :423）不可達——階段 4 以票種與座位活動的聯集大小 1 保證每組相同；`TryGetValue` 失敗（原 :418）亦不可達——LockSeatsAsync 的數量比對已先擋下。階段 11 可觀察分支為「計數項目 Quantity／鎖定實例」，由 3.7 階段 11 mutation（Quantity 改 1）守護。
  - TDD：N/A（唯讀分析）。
- [x] 1.2 對 1.1 列出的缺口新增特徵化測試（只新增，不改既有測試斷言），放在 `tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs` 既有風格內；若某缺口分支在現行程式碼下不可達（例如被交易外檢查完全擋下），記錄不可達理由，不硬寫測試。必須新增一個鎖定呼叫順序特徵化測試。既有 Fixture 用手寫 Fake（只有 `GetForUpdateCallCount`，無跨 repository 的呼叫紀錄），採用方式：Fake 皆為 sealed 且方法非 virtual，無法子類別化；改為最小幅度修改 `tests/**/TestSupport/Fake*.cs`（GetForShare、PurchaseQueueEntry、EventSeat、TicketType 四個 Fake 加共用順序紀錄掛點，例如可選的 `List<string>`，只加紀錄、不改既有行為）或寫實作同一介面的 decorator，二擇一並於完成紀錄說明（proposal Impact 已列入 `TestSupport/Fake*.cs`）。斷言 Event（`GetForShareAsync`）→ PurchaseQueueEntry → EventSeat → TicketType 的整體順序。**另一個無條件必做**：`FakePurchaseQueueRepository` 加可選、預設不啟用的 `GetForUpdate` callback（`FakeDateTimeProvider.UtcNow` 有 public setter，可在 callback 內推進時鐘），新增特徵化測試：在 callback 內把時鐘推進到超過 `AdmissionExpiresAtUtc`，斷言回傳 QueueAdmissionRequired 且 `OrderRepository.Data` 為空（守 TP-ORDER-014「以檢查當下最新狀態為準」；階段 9 須自行讀 now，不得重用階段 8 的 now）。測試情境必須是：排隊模式開啟且已 Admitted，且同一請求同時含座位項目與計數票種項目（鎖座位只在有座位時發生 :372、鎖票種只在有計數票種時發生 :392，缺一則順序斷言空轉，3.1 的對調 mutation 不會失敗）。另有三組**無條件必做**的特徵化測試（缺口現在即可確認）：(1) `GetForShareOverride` 回傳 null 的案例，斷言回 NotFound 且 `EventLocked` 已寫入（現有日誌測試只斷言 `BeOfType<double>` 或 null，且無 `lockedEvent == null` 案例）；(2) timings 分段測試（timings 用 static `Stopwatch.GetTimestamp()`，無注入點，只能斷言對應分段毫秒值 ≥ 0 且各分段存在／為 null 的條件；`FakeUnitOfWork.BeginTransactionAsync` 同步完成，兩次 timestamp 可能相等，故在 Fake 的 `BeginTransactionAsync` 與 `GetForShareAsync` 加極短 `Task.Delay` 讓分段可區分，僅限該測試啟用）；(3) 失敗訊息逐字斷言：下列片段目前在 tests/** 完全無斷言，須以表格驅動測試逐條斷言 `Error.Message`——"does not require a seat"、"requires a seat but none"、"One or more selected seats were not found"、"One or more selected ticket types"、"requires purchase queue admission"、"requires real-name registration"；其中 "does not belong to the same event"（:423–425）因 `EventSeat` 建構子為 internal 且交易外已保證單一活動，公開 API 不可達：由 1.1 判定可達性並記錄不可達理由，**不納入無條件清單、不用反射硬造**（:418 的 TryGetValue 同樣由 1.1 判定）。鎖內數量比對 :377（座位）與 :397（票種）**可達**（"One or more selected seats were not found." 與 "…ticket types were not found."）：在 `GetForShareOverride`（交易外讀取之後、鎖內 `GetForUpdateAsync` 之前執行）的副作用裡清空 `EventSeatRepository.Data`／`TicketTypeRepository.Data` 再回傳活動，即可觸發，須各寫一個特徵化測試，斷言 NotFound 與逐字訊息；1.1 對照表另列其餘失敗訊息是否已逐字斷言，缺者併入此測試。驗證：`docker compose exec api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~OrderServiceTests"` 通過；新測試在**未修改 OrderService.cs** 的狀態下通過。
  - 完成紀錄：採 Fake 掛鉤做法（全部預設關閉、不影響既有測試）：FakeEventRepository `LockCallLog`／`GetForShareDelay`；FakeEventSeatRepository、FakeTicketTypeRepository `LockCallLog`；FakePurchaseQueueRepository `LockCallLog`／`GetForUpdateCallback`；FakeUnitOfWork `BeginTransactionDelay`。新增特徵化測試 20 個（名稱見 1.1 對照表★）。這些測試在重構前原碼（`git stash push -- src/ProjectC.Application/Orders/OrderService.cs` 移開重構）上全套通過，原文見 1.3 的「測試」區塊（Application.Tests 547 全數通過，含 20 個新增案例）。
  - TDD：N/A（特徵化測試，理由見上方）。
- [x] 1.3 重構前基準：`docker compose exec api dotnet test` 全套各測試專案摘要行；`docker compose exec -T api dotnet build --no-incremental` 的 warning 清單（預期含 `CA1502` 48 與 `S3776` 28 兩筆，數值以本 task 實測原文為準），作為第 5 節比對基準（`RealNameLoggingTests`、`HostShutdownSeqFlushTests`、時間相關測試有已知偶發失敗，失敗時照實記錄並單獨重跑）。
  - 完成紀錄：以下為重構前（stash 只移開 `OrderService.cs`，測試與 Fake 為新增後版本）的實際執行；build 輸出以 `grep -E 'warning |Warning\(s\)|Error\(s\)|^exit='` 過濾（每一筆 warning 原文行皆保留，僅略去其餘建置訊息），測試輸出以 `grep -E 'Passed!|Failed!|^exit='` 過濾（每個測試專案一行摘要原文）。
    ```
    $ git diff --stat -- src/ProjectC.Application/Orders/OrderService.cs   # 還原前（重構版）
     src/ProjectC.Application/Orders/OrderService.cs | 426 ++++++++++++++++--------
     1 file changed, 295 insertions(+), 131 deletions(-)
    $ git stash push -- src/ProjectC.Application/Orders/OrderService.cs
    Saved working directory and index state WIP on feature/place-order-core-complexity: adb72dd place-order-core-complexity：依 Opus 交叉審查修正提案文件
    $ git stash list
    stash@{0}: WIP on feature/place-order-core-complexity: adb72dd place-order-core-complexity：依 Opus 交叉審查修正提案文件
    $ git diff --stat -- src   # 還原後，src 應無差異
    --- (空行代表 src 無差異)
    $ docker compose exec -T api dotnet build --no-incremental   # 以下為 grep 過濾後的原文行
    /src/src/ProjectC.Application/Orders/OrderService.cs(138,38): warning CA1502: 'PlaceOrderCoreAsync' has a cyclomatic complexity of '48'. Rewrite or refactor the code to decrease its complexity below '26'. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1502) [/src/src/ProjectC.Application/ProjectC.Application.csproj]
    /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs(411,24): warning S3776: Refactor this method to reduce its Cognitive Complexity from 28 to the 15 allowed. (https://rules.sonarsource.com/csharp/RSPEC-3776) [/src/src/ProjectC.WebApi/ProjectC.WebApi.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: Found conflicts between different versions of "Microsoft.EntityFrameworkCore.Relational" that could not be resolved. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: There was a conflict between "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was chosen because it was primary and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was not. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" [/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll]. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on or have been unified to "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" []. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: Found conflicts between different versions of "Microsoft.EntityFrameworkCore.Relational" that could not be resolved. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: There was a conflict between "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was chosen because it was primary and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was not. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" [/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll]. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on or have been unified to "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" []. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /src/src/ProjectC.Application/Orders/OrderService.cs(138,38): warning CA1502: 'PlaceOrderCoreAsync' has a cyclomatic complexity of '48'. Rewrite or refactor the code to decrease its complexity below '26'. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1502) [/src/src/ProjectC.Application/ProjectC.Application.csproj]
    /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs(411,24): warning S3776: Refactor this method to reduce its Cognitive Complexity from 28 to the 15 allowed. (https://rules.sonarsource.com/csharp/RSPEC-3776) [/src/src/ProjectC.WebApi/ProjectC.WebApi.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: Found conflicts between different versions of "Microsoft.EntityFrameworkCore.Relational" that could not be resolved. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: There was a conflict between "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was chosen because it was primary and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was not. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" [/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll]. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on or have been unified to "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" []. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: Found conflicts between different versions of "Microsoft.EntityFrameworkCore.Relational" that could not be resolved. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: There was a conflict between "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was chosen because it was primary and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was not. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" [/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll]. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on or have been unified to "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" []. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
        4 Warning(s)
        0 Error(s)
    exit=0
    $ docker compose exec -T api dotnet test   # 以下為 grep 過濾後的原文行
    Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 103 ms - ProjectC.Domain.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   547, Skipped:     0, Total:   547, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 9 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: 52 s - ProjectC.Infrastructure.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   676, Skipped:     0, Total:   676, Duration: 3 m 43 s - ProjectC.WebApi.Tests.dll (net10.0)
    exit=0
    $ git stash pop
    On branch feature/place-order-core-complexity
    Changes not staged for commit:
      (use "git add <file>..." to update what will be committed)
      (use "git restore <file>..." to discard changes in working directory)
    	modified:   docs/project-scope.md
    	modified:   openspec/changes/place-order-core-complexity/design.md
    	modified:   openspec/changes/place-order-core-complexity/tasks.md
    	modified:   src/ProjectC.Application/Orders/OrderService.cs
    	modified:   tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs
    	modified:   tests/ProjectC.Application.Tests/TestSupport/FakeEventRepository.cs
    	modified:   tests/ProjectC.Application.Tests/TestSupport/FakeEventSeatRepository.cs
    	modified:   tests/ProjectC.Application.Tests/TestSupport/FakePurchaseQueueRepository.cs
    	modified:   tests/ProjectC.Application.Tests/TestSupport/FakeTicketTypeRepository.cs
    	modified:   tests/ProjectC.Application.Tests/TestSupport/FakeUnitOfWork.cs
    
    no changes added to commit (use "git add" and/or "git commit -a")
    Dropped refs/stash@{0} (d16226ca942b315870d69c5d96ce7fc2a3bb64cb)
    $ git diff --stat -- src/ProjectC.Application/Orders/OrderService.cs   # pop 後
     src/ProjectC.Application/Orders/OrderService.cs | 426 ++++++++++++++++--------
     1 file changed, 295 insertions(+), 131 deletions(-)
    ```
- [x] 1.4 量測各階段目前的複雜度基準：依 design.md 決策 5 暫時量測：新增根目錄 `CodeMetricsConfig.txt`（`CA1502(Method): 1`；CA1502 只回報大於門檻的成員，輸出中未出現的方法複雜度視為 1，量測方式是否可用以本 task 實測為準），並在 `src/ProjectC.Application/ProjectC.Application.csproj` 暫時加入 `<AdditionalFiles Include="..\..\CodeMetricsConfig.txt" />`；記錄 `PlaceOrderCoreAsync`（48）與 `GetSeatPlacementErrorOrNull`、`GetEarlyConflictOrNull` 等既有成員的值，確認量測方式可用後兩者都還原。量測命令：`docker compose exec -T api dotnet build --no-incremental 2>&1 | grep '/OrderService.cs(.*CA1502' | sort -u`（門檻設 1 時會冒出大量警告且 solution 建置會重複印出，故過濾並去重）。還原：刪除 `CodeMetricsConfig.txt` 並 `git checkout -- src/ProjectC.Application/ProjectC.Application.csproj`（避免手動刪行動到 BOM／CRLF）。驗證：`git status --short -- .editorconfig CodeMetricsConfig.txt src/ProjectC.Application/ProjectC.Application.csproj` 還原後為空。
  - 完成紀錄：    ```
    量測（CodeMetricsConfig.txt：CA1502(Method): 1）OrderService.cs 重構前：
    PlaceOrderCoreAsync 48, ChangeOrderStatusAsync 11, LogPhaseTimings 7, ConfirmOrderAsync 4, GetEarlyConflictOrNull 4, GetSeatPlacementErrorOrNull 3, PlaceOrderAsync 3, GetElapsedMilliseconds 3（未列出者為 1）
    量測方式可用；量測後已刪除 CodeMetricsConfig.txt 並 git checkout 還原 csproj。
    ```
  - TDD：N/A（量測）。

## 2. 拆分 PlaceOrderCoreAsync

> 每個子 task 完成後立即執行：`docker compose exec api dotnet build` 與
> `docker compose exec api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~OrderServiceTests"`；不等到全部拆完才驗證。搬移為逐行剪下貼上，禁止順手改名、重排條件、改寫註解或調整檢查順序。階段切分見 design.md 決策 2，可依實際程式微調，但須遵守決策 3 不變量。

- [x] 2.1 交易外前半（階段 1–4）：抽出票種載入、RequiresSeat 形狀驗證、驗證用座位載入、跨活動檢查與活動讀取。引入 private record 承載中間資料。驗證：build、`OrderServiceTests` 通過，且 diff 核對各階段呼叫順序（含 I/O 與提前 return）與原碼一致。
  - 完成紀錄：抽出 LoadTicketTypesAsync、GetSelectionShapeErrorOrNull、LoadValidationSeatsAsync、GetDistinctEventIds／GetCrossEventErrorOrNull；呼叫順序與提前 return 與原碼逐段核對一致。2.1–2.4 同批驗證。驗證結果：build 0 Error(s)；`Passed!  - Failed:     0, Passed:   129, Skipped:     0, Total:   129 - ProjectC.Application.Tests.dll`；Infrastructure `OrderService` 篩選 `Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31 - ProjectC.Infrastructure.Tests.dll`。
  - TDD：N/A（行為不變重構，保護網為 1.2 與第 3 節）。
- [x] 2.2 交易外後半（階段 5–7）：抽出販售期間／實名／限購閘門（順序固定）、座位配置載入與座位圖比對；提早 409 的呼叫維持原位置。驗證：build、`OrderServiceTests` 通過，且 diff 核對各階段呼叫順序（含 I/O 與提前 return）與原碼一致。
  - 完成紀錄：抽出 GetPreTransactionGateErrorOrNullAsync（販售期間→實名→限購）、LoadSeatPlacementsAsync；座位圖比對與提早 409 維持協調者原位置。驗證結果：build 0 Error(s)；`Passed!  - Failed:     0, Passed:   129, Skipped:     0, Total:   129 - ProjectC.Application.Tests.dll`；Infrastructure `OrderService` 篩選 `Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31 - ProjectC.Infrastructure.Tests.dll`。
- [x] 2.3 鎖內（階段 8–11）：抽出鎖內販售期間重驗與實名／座位配置補位（實名旗標不變量例外原樣丟出，非本 change 的 I1）、排隊資格、鎖座位、鎖計數票種、組 selections。`GetForShareAsync`、緊接的 `EventLocked` 時間點寫入與 `lockedEvent is null` → NotFound 留在協調者。驗證：build、`OrderServiceTests` 與 `docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~OrderService"` 通過，且 diff 核對呼叫順序（含 `now` 取得位置）與原碼一致。
  - 完成紀錄：抽出 GetLockedEventErrorOrNullAsync（record LockedEventCheck）、LockQueueEntryAsync（自行於 GetForUpdateAsync 後讀 UtcNow）、LockSeatsAsync、LockCountingTicketTypesAsync、BuildSelections（records SelectionSource／BuiltSelections）。GetForShareAsync、EventLocked 寫入與 NotFound 留在協調者，`var now` 單行在其後。驗證結果：build 0 Error(s)；`Passed!  - Failed:     0, Passed:   129, Skipped:     0, Total:   129 - ProjectC.Application.Tests.dll`；Infrastructure `OrderService` 篩選 `Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31 - ProjectC.Infrastructure.Tests.dll`。
- [x] 2.4 commit 後（階段 12）與協調者收尾：抽出鏡像同步與快取失效；確認 `await using` 宣告、`timings` 寫入、`CommitAsync`、`connection.DisposeAsync()` 仍在協調者且順序不變。驗證：build、上述兩組測試通過，且 diff 核對呼叫順序與原碼一致。
  - 完成紀錄：抽出 SyncAfterCommitAsync（record PostCommitSync）；協調者仍持有 await using connection／transaction、timings 寫入、CommitAsync、connection.DisposeAsync()（在 Redis 同步之前）。驗證結果：build 0 Error(s)；`Passed!  - Failed:     0, Passed:   129, Skipped:     0, Total:   129 - ProjectC.Application.Tests.dll`；Infrastructure `OrderService` 篩選 `Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31 - ProjectC.Infrastructure.Tests.dll`。
- [x] 2.5 複雜度驗收（C1）：`docker compose exec -T api dotnet build --no-incremental` warning 清單不再含 `CA1502`，且與 1.3 基準相比只少了 `CA1502` 一筆（`S3776` 屬 WebApi 的 `ReconcileEventQueueAsync`，Application 不受 Sonar 分析，不作為驗收；若新方法引入新警告，於本 task 修掉後重跑，不得留待後續）；再依 design.md 決策 5 暫時量測並記錄 `PlaceOrderCoreAsync` 與每個新方法的實際值（全部 ≤ 25），還原設定。驗證：warning 清單與各方法數值原文；`git status --short -- .editorconfig CodeMetricsConfig.txt src/ProjectC.Application/ProjectC.Application.csproj` 為空。
  - 完成紀錄：重構後（最終程式碼）`dotnet build --no-incremental`，同一過濾條件；與 1.3 比較：Warning(s) 4 → 3，唯一差異是 `OrderService.cs(138,38): warning CA1502` 消失，其餘每筆 warning 原文行相同。
    ```
    $ docker compose exec -T api dotnet build --no-incremental   # grep 過濾後的原文行
    /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs(411,24): warning S3776: Refactor this method to reduce its Cognitive Complexity from 28 to the 15 allowed. (https://rules.sonarsource.com/csharp/RSPEC-3776) [/src/src/ProjectC.WebApi/ProjectC.WebApi.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: Found conflicts between different versions of "Microsoft.EntityFrameworkCore.Relational" that could not be resolved. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: There was a conflict between "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was chosen because it was primary and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was not. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" [/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll]. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on or have been unified to "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" []. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: Found conflicts between different versions of "Microsoft.EntityFrameworkCore.Relational" that could not be resolved. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: There was a conflict between "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was chosen because it was primary and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was not. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" [/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll]. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on or have been unified to "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" []. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs(411,24): warning S3776: Refactor this method to reduce its Cognitive Complexity from 28 to the 15 allowed. (https://rules.sonarsource.com/csharp/RSPEC-3776) [/src/src/ProjectC.WebApi/ProjectC.WebApi.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: Found conflicts between different versions of "Microsoft.EntityFrameworkCore.Relational" that could not be resolved. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: There was a conflict between "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was chosen because it was primary and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was not. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" [/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll]. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on or have been unified to "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" []. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: Found conflicts between different versions of "Microsoft.EntityFrameworkCore.Relational" that could not be resolved. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: There was a conflict between "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was chosen because it was primary and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was not. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" [/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll]. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on or have been unified to "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" []. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
        3 Warning(s)
        0 Error(s)
    exit=0
    $ diff <(1.3 的 build 區塊) <(本區塊)   # 僅 CA1502 一筆與 Warning(s) 計數不同
    1d0
    < /src/src/ProjectC.Application/Orders/OrderService.cs(138,38): warning CA1502: 'PlaceOrderCoreAsync' has a cyclomatic complexity of '48'. Rewrite or refactor the code to decrease its complexity below '26'. (https://learn.microsoft.com/dotnet/fundamentals/code
    33d31
    < /src/src/ProjectC.Application/Orders/OrderService.cs(138,38): warning CA1502: 'PlaceOrderCoreAsync' has a cyclomatic complexity of '48'. Rewrite or refactor the code to decrease its complexity below '26'. (https://learn.microsoft.com/dotnet/fundamentals/code
    65c63
    <     4 Warning(s)
    ---
    >     3 Warning(s)
    量測（CodeMetricsConfig.txt：CA1502(Method): 1，量測於 S6 微調 `ticketTypesById` 區域變數之前；該微調不含分支，不影響複雜度）：
    PlaceOrderCoreAsync 20, ChangeOrderStatusAsync 11, GetSelectionShapeErrorOrNull 8, GetPreTransactionGateErrorOrNullAsync 7, GetLockedEventErrorOrNullAsync 7, LogPhaseTimings 7, BuildSelections 5, LockQueueEntryAsync 4, ConfirmOrderAsync 4, GetEarlyConflictOrNull 4, LoadTicketTypesAsync 3, LoadValidationSeatsAsync 3, LoadSeatPlacementsAsync 3, LockSeatsAsync 3, LockCountingTicketTypesAsync 3, SyncAfterCommitAsync 3, GetSeatPlacementErrorOrNull 3, PlaceOrderAsync 3, GetElapsedMilliseconds 3, GetCrossEventErrorOrNull 2
    最大值 20 ≤ 25；已刪除 CodeMetricsConfig.txt 並還原 csproj。
    ```

## 3. 突變驗證（證明保護網有效；使用 `bash scripts/verify-test-evidence.sh mutate`，一次一個改動，改完還原並重跑確認恢復通過）

> 適用 3.1–3.7 全部：任一 mutation 若沒有測試失敗，停止並回報（保護網缺口），回 1.2 補測試後重做該項。**回頭補的測試必須先在原碼上驗證**：因第 2 節已重構，補測試前先還原到重構前的 OrderService.cs，確認新測試在原碼上**通過**，再還原回重構碼並確認也通過。做法：第 2、3 節進行期間不得 commit（否則 stash 無法回到原碼）；以 `git stash push -- src/ProjectC.Application/Orders/OrderService.cs` 暫存重構版，跑新測試（預期通過），再 `git stash pop`。注意 `verify-test-evidence.sh restore` 的語意相反（測試失敗＝exit 0、測試仍通過＝exit 1「證據不成立」），此處預期通過，不要用它判定，只能用它的 stash 機制或直接手動 stash（CLAUDE.md 要求未 commit 修改用該腳本，此處為例外並於完成紀錄說明）；兩次輸出與 `git stash list`／pop 輸出都貼原文，否則新測試可能把重構造成的回歸固定成「正確行為」。pop 失敗或有衝突時立即停止並回報。`OrderService.cs` 為 CRLF，優先用單行替換；多行 `--from` 須含 `\r\n`，bash 寫法如 `--from $'line1\r\nline2'`（3.5 的做法：`--from` 為協調者內「`var now = _dateTimeProvider.UtcNow;`」那一整行加 `\r\n`，`--to` 為空字串＝刪除該行；另需把同樣一行插到 `GetForShareAsync` 呼叫行之前，故 3.5 用單一多行 `--from`＝「`GetForShareAsync` 呼叫行＋ `EventLocked` 寫入＋ null 檢查＋ `var now` 行」連續區段、`--to`＝把 `var now` 行置於最前的同一區段；實際字串以重構後內容為準，必須恰好匹配一次且可編譯，否則 mutate exit 2）。各項的測試命令：3.1–3.3、3.5、3.6a／3.6b、3.7 跑 `docker compose exec api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~OrderServiceTests"`；3.4 只用 `docker compose exec api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~OrderServiceSingleConnectionTests"`（Testcontainers；mutate 一次只接一個測試命令）。mutation 必須在目前（未重構或已重構）的程式碼結構上可達成，無法達成時改選等價的其他破壞方式並記錄。

- [x] 3.1 I1：暫時對調鎖內「鎖座位」與「鎖計數票種」的順序；至少一個測試（1.2 新增的鎖定順序測試）須失敗。驗證：命令、失敗測試名稱與訊息原文、還原後摘要行。
  - 完成紀錄：mutate：鎖座位前先鎖計數票種（I1）。
    ```
    【3.1】 --from 原文：
            var eventSeatsResult = await LockSeatsAsync(request, cancellationToken);
    【3.1】 --to 原文：
            var preLockedTicketTypes = await LockCountingTicketTypesAsync(request, cancellationToken);
            var eventSeatsResult = await LockSeatsAsync(request, cancellationToken);
    【3.1】 完整 mutate 輸出：
    === 3.1
    mutation:         var eventSeatsResult = await LockSeatsAsync(request, cancellationToken); ->         var preLockedTicketTypes = await LockCountingTicketTypesAsync(request, cancellationToken);
            var eventSeatsResult = await LockSeatsAsync(request, cancellationToken);
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.49]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountingTicketTypesDisappearBetweenOutsideReadAndLock_ReturnsVerbatimNotFoundMessage [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountingTicketTypesDisappearBetweenOutsideReadAndLock_ReturnsVerbatimNotFoundMessage [24 ms]
      Error Message:
       Expected fixture.TicketTypeRepository.GetForUpdateCallCount to be 1, but found 2.
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountingTicketTypesDisappearBetweenOutsideReadAndLock_ReturnsVerbatimNotFoundMessage() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 2038
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.92]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_LocksEventThenQueueEntryThenSeatThenTicketType [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_LocksEventThenQueueEntryThenSeatThenTicketType [13 ms]
      Error Message:
       Expected lockCallLog to be equal to {"Event", "PurchaseQueueEntry", "EventSeat", "TicketType"}, but {"Event", "PurchaseQueueEntry", "TicketType", "EventSeat", "TicketType"} contains 1 item(s) too many.
      Stack Trace:
         at FluentAssertions.Collections.GenericCollectionAssertions`3.<>c__DisplayClass106_0`1.<AssertSubjectEquality>b__0(AssertionChain chain)
       at FluentAssertions.Collections.GenericCollectionAssertions`3.AssertSubjectEquality[TExpectation](IEnumerable`1 expectation, Func`3 equalityComparison, String because, Object[] becauseArgs)
       at FluentAssertions.Collections.GenericCollectionAssertions`3.Equal(IEnumerable`1 expected, String because, Object[] becauseArgs)
       at FluentAssertions.Collections.StringCollectionAssertions`2.Equal(String[] expected)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_LocksEventThenQueueEntryThenSeatThenTicketType() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1889
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     2, Passed:   127, Skipped:     0, Total:   129, Duration: 787 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    ```
- [x] 3.2 I2：暫時把某個交易外驗證（例如座位配置比對）移到 `BeginTransactionAsync` 之後；至少一個「BeforeOpeningTransaction」測試須失敗。
  - 完成紀錄：mutate：在座位配置比對之前提前開交易（I2）。
    ```
    【3.2】 --from 原文：
            var seatPlacements = seatPlacementsResult.Value!;
    【3.2】 --to 原文：
            var seatPlacements = seatPlacementsResult.Value!;
            await using var earlyTransaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
    【3.2】 完整 mutate 輸出：
    === 3.2
    mutation:         var seatPlacements = seatPlacementsResult.Value!; ->         var seatPlacements = seatPlacementsResult.Value!;
            await using var earlyTransaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.43]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatHoldAlreadyExpired_IsNotRejectedEarlyAndSucceeds [FAIL]
    [xUnit.net 00:00:00.44]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatHeldByAnotherUnexpiredOrder_ReturnsConflictBeforeOpeningTransaction [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatHoldAlreadyExpired_IsNotRejectedEarlyAndSucceeds [34 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 1, but found 2.
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatHoldAlreadyExpired_IsNotRejectedEarlyAndSucceeds() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1505
    --- End of stack trace from previous location ---
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatHeldByAnotherUnexpiredOrder_ReturnsConflictBeforeOpeningTransaction [9 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 0, but found 1 (difference of 1).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatHeldByAnotherUnexpiredOrder_ReturnsConflictBeforeOpeningTransaction() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1491
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.50]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeReadOutsideTransactionAndNotAdmittedAndSeatSold_ReturnsQueueAdmissionRequired [FAIL]
    [xUnit.net 00:00:00.53]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenRealNameRequiredAndBuyerNotRegisteredUnderLock_ReturnsVerbatimMessage [FAIL]
    [xUnit.net 00:00:00.54]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeEnabledAndNotAdmittedAndSeatZoneMismatches_ReturnsValidationErrorNotQueueAdmissionRequired [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeReadOutsideTransactionAndNotAdmittedAndSeatSold_ReturnsQueueAdmissionRequired [6 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 1, but found 2.
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeReadOutsideTransactionAndNotAdmittedAndSeatSold_ReturnsQueueAdmissionRequired() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1557
    --- End of stack trace from previous location ---
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenRealNameRequiredAndBuyerNotRegisteredUnderLock_ReturnsVerbatimMessage [8 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 1, but found 2.
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenRealNameRequiredAndBuyerNotRegisteredUnderLock_ReturnsVerbatimMessage() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 2076
    --- End of stack trace from previous location ---
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeEnabledAndNotAdmittedAndSeatZoneMismatches_ReturnsValidationErrorNotQueueAdmissionRequired [7 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 0, but found 1 (difference of 1).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeEnabledAndNotAdmittedAndSeatZoneMismatches_ReturnsValidationErrorNotQueueAdmissionRequired() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 233
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.59]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatTemplateBelongsToAnotherSeatMap_ReturnsNotFoundBeforeOpeningTransaction [FAIL]
    [xUnit.net 00:00:00.60]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSalesClosesWhileWaitingForEventLock_ReturnsSalesClosedAndRollsBack [FAIL]
    [xUnit.net 00:00:00.62]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountTicketTypeInventoryInsufficient_ReturnsConflictBeforeOpeningTransaction [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatTemplateBelongsToAnotherSeatMap_ReturnsNotFoundBeforeOpeningTransaction [5 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 0, but found 1 (difference of 1).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatTemplateBelongsToAnotherSeatMap_ReturnsNotFoundBeforeOpeningTransaction() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 266
    --- End of stack trace from previous location ---
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSalesClosesWhileWaitingForEventLock_ReturnsSalesClosedAndRollsBack [7 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 1 because 交易外檢查時仍可售，必須進入交易才會被權威檢查擋下, but found 2.
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSalesClosesWhileWaitingForEventLock_ReturnsSalesClosedAndRollsBack() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1351
    --- End of stack trace from previous location ---
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountTicketTypeInventoryInsufficient_ReturnsConflictBeforeOpeningTransaction [20 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 0, but found 1 (difference of 1).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountTicketTypeInventoryInsufficient_ReturnsConflictBeforeOpeningTransaction() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1520
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.66]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatAvailableButCountInventoryInsufficient_ReturnsConflictAndHoldsNothing [FAIL]
    [xUnit.net 00:00:00.67]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatSoldAndCountInventoryInsufficient_ReturnsSeatConflictLikeInLockHandler [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatAvailableButCountInventoryInsufficient_ReturnsConflictAndHoldsNothing [7 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 0, but found 1 (difference of 1).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatAvailableButCountInventoryInsufficient_ReturnsConflictAndHoldsNothing() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1628
    --- End of stack trace from previous location ---
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatSoldAndCountInventoryInsufficient_ReturnsSeatConflictLikeInLockHandler [6 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 0, but found 1 (difference of 1).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatSoldAndCountInventoryInsufficient_ReturnsSeatConflictLikeInLockHandler() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1542
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.68]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatFromAnotherSeatMapIsAlsoSold_ReturnsSeatMapNotFoundNotConflict [FAIL]
    [xUnit.net 00:00:00.69]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeOffOutsideButOnUnderLockAndSeatSold_ReturnsConflictBecauseEarlyCheckUsesOutsideFlag [FAIL]
    [xUnit.net 00:00:00.70]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenRejectedEarlyOrUnderLock_ReturnsSameConflictMessage [FAIL]
    [xUnit.net 00:00:00.71]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatsDisappearBetweenOutsideReadAndLock_ReturnsVerbatimNotFoundMessage [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatFromAnotherSeatMapIsAlsoSold_ReturnsSeatMapNotFoundNotConflict [12 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 0, but found 1 (difference of 1).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatFromAnotherSeatMapIsAlsoSold_ReturnsSeatMapNotFoundNotConflict() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 2148
    --- End of stack trace from previous location ---
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeOffOutsideButOnUnderLockAndSeatSold_ReturnsConflictBecauseEarlyCheckUsesOutsideFlag [7 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 0, but found 1 (difference of 1).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeOffOutsideButOnUnderLockAndSeatSold_ReturnsConflictBecauseEarlyCheckUsesOutsideFlag() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1591
    --- End of stack trace from previous location ---
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenRejectedEarlyOrUnderLock_ReturnsSameConflictMessage [7 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 0 because 第一次應在交易前被拒, but found 1 (difference of 1).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenRejectedEarlyOrUnderLock_ReturnsSameConflictMessage() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1644
    --- End of stack trace from previous location ---
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatsDisappearBetweenOutsideReadAndLock_ReturnsVerbatimNotFoundMessage [8 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 1, but found 2.
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatsDisappearBetweenOutsideReadAndLock_ReturnsVerbatimNotFoundMessage() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 2018
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.73]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatZoneDoesNotMatchTicketTypeZone_RejectsBeforeOpeningTransaction [FAIL]
    [xUnit.net 00:00:00.74]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenEventUnreadOutsideTransactionAndSeatTemplateBelongsToAnotherSeatMap_ReturnsNotFoundBeforeQueueCheckAndLocks [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatZoneDoesNotMatchTicketTypeZone_RejectsBeforeOpeningTransaction [4 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 0 because 分區不一致在交易前就能判斷，不得排進 Event 列鎖, but found 1 (difference of 1).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatZoneDoesNotMatchTicketTypeZone_RejectsBeforeOpeningTransaction() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 218
    --- End of stack trace from previous location ---
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenEventUnreadOutsideTransactionAndSeatTemplateBelongsToAnotherSeatMap_ReturnsNotFoundBeforeQueueCheckAndLocks [5 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 1, but found 2.
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenEventUnreadOutsideTransactionAndSeatTemplateBelongsToAnotherSeatMap_ReturnsNotFoundBeforeQueueCheckAndLocks() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 285
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.92]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenOneOfThreeSeatsSold_ReturnsConflictForSoldSeatAndHoldsNothing [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenOneOfThreeSeatsSold_ReturnsConflictForSoldSeatAndHoldsNothing [6 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 0, but found 1 (difference of 1).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenOneOfThreeSeatsSold_ReturnsConflictForSoldSeatAndHoldsNothing() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1606
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.95]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenDebugEnabledAndRejectedEarlyWithConflict_LogsConflictWithNullInTransactionPhases [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenDebugEnabledAndRejectedEarlyWithConflict_LogsConflictWithNullInTransactionPhases [7 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 0, but found 1 (difference of 1).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenDebugEnabledAndRejectedEarlyWithConflict_LogsConflictWithNullInTransactionPhases() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1668
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:01.00]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenEventUnreadOutsideTransactionAndSeatSold_OpensTransactionAndReturnsConflictFromLock [FAIL]
    [xUnit.net 00:00:01.02]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatAlreadySold_ReturnsConflictBeforeOpeningTransaction [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenEventUnreadOutsideTransactionAndSeatSold_OpensTransactionAndReturnsConflictFromLock [6 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 1, but found 2.
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenEventUnreadOutsideTransactionAndSeatSold_OpensTransactionAndReturnsConflictFromLock() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1572
    --- End of stack trace from previous location ---
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatAlreadySold_ReturnsConflictBeforeOpeningTransaction [6 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 0, but found 1 (difference of 1).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatAlreadySold_ReturnsConflictBeforeOpeningTransaction() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1476
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:01.06]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenEventUnreadOutsideTransactionAndSeatZoneMismatches_ReturnsValidationErrorBeforeQueueCheckAndLocks [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenEventUnreadOutsideTransactionAndSeatZoneMismatches_ReturnsValidationErrorBeforeQueueCheckAndLocks [4 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 1, but found 2.
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenEventUnreadOutsideTransactionAndSeatZoneMismatches_ReturnsValidationErrorBeforeQueueCheckAndLocks() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 304
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:    21, Passed:   108, Skipped:     0, Total:   129, Duration: 898 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    ```
- [x] 3.3 I3：暫時把 `timings.EventLocked = …` 移到 `lockedEvent is null` 檢查之後；1.2 新增的「`lockedEvent == null` 時 `EventLocked` 已寫入」測試須失敗。再各做一次：把 `TransactionStarting` 與 `TransactionBegun` 的寫入互換，1.2 新增的時間點先後順序測試須失敗。其餘時間點（`ConnectionOpened`、`CommitStarting`、`Committed`）因 Stopwatch 為真實時鐘無法完整以測試守護，由 2.x 的 diff 審查守護，並在完成紀錄寫明。
  - 完成紀錄：mutate：(a) EventLocked 寫入移到 null 檢查之後；(b) TransactionStarting／TransactionBegun 寫入互換。ConnectionOpened、CommitStarting、Committed 因真實時鐘無法完整以測試守護，由 2.x diff 審查守護。
    ```
    【3.3a】 --from 原文：
            timings.EventLocked = Stopwatch.GetTimestamp();
            if (lockedEvent is null)
            {
                return Result<Guid>.Failure(Error.NotFound($"Event '{eventId}' was not found."));
            }
    【3.3a】 --to 原文：
            if (lockedEvent is null)
            {
                return Result<Guid>.Failure(Error.NotFound($"Event '{eventId}' was not found."));
            }
            timings.EventLocked = Stopwatch.GetTimestamp();
    【3.3a】 完整 mutate 輸出：
    === 3.3a
    mutation:         timings.EventLocked = Stopwatch.GetTimestamp();
            if (lockedEvent is null)
            {
                return Result<Guid>.Failure(Error.NotFound($"Event '{eventId}' was not found."));
            } ->         if (lockedEvent is null)
            {
                return Result<Guid>.Failure(Error.NotFound($"Event '{eventId}' was not found."));
            }
            timings.EventLocked = Stopwatch.GetTimestamp();
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.87]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenLockedEventIsNull_ReturnsNotFoundAndRecordsEventLockedTimestamp [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenLockedEventIsNull_ReturnsNotFoundAndRecordsEventLockedTimestamp [23 ms]
      Error Message:
       Expected entry.Properties["EventLockWaitMs"] to be System.Double because EventLocked 須在 lockedEvent is null 檢查之前寫入, but found <null>.
      Stack Trace:
         at FluentAssertions.Primitives.ReferenceTypeAssertions`2.BeOfType(Type expectedType, String because, Object[] becauseArgs)
       at FluentAssertions.Primitives.ReferenceTypeAssertions`2.BeOfType[T](String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenLockedEventIsNull_ReturnsNotFoundAndRecordsEventLockedTimestamp() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1944
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     1, Passed:   128, Skipped:     0, Total:   129, Duration: 760 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    
    【3.3b】 --from 原文：
            timings.TransactionStarting = Stopwatch.GetTimestamp();
            await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
            timings.TransactionBegun = Stopwatch.GetTimestamp();
    【3.3b】 --to 原文：
            timings.TransactionBegun = Stopwatch.GetTimestamp();
            await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
            timings.TransactionStarting = Stopwatch.GetTimestamp();
    【3.3b】 完整 mutate 輸出：
    === 3.3b
    mutation:         timings.TransactionStarting = Stopwatch.GetTimestamp();
            await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
            timings.TransactionBegun = Stopwatch.GetTimestamp(); ->         timings.TransactionBegun = Stopwatch.GetTimestamp();
            await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
            timings.TransactionStarting = Stopwatch.GetTimestamp();
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.73]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenTransactionAndEventLockAreSlow_RecordsPositiveBeginTransactionAndEventLockWaitSegments [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenTransactionAndEventLockAreSlow_RecordsPositiveBeginTransactionAndEventLockWaitSegments [86 ms]
      Error Message:
       Expected entry.Properties["BeginTransactionMs"] to be greater than 0.0 because TransactionStarting 須早於 TransactionBegun, but found -31.6538 (difference of -31.6538).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.BeGreaterThan(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenTransactionAndEventLockAreSlow_RecordsPositiveBeginTransactionAndEventLockWaitSegments() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1962
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.80]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenWallClockJumpsDuringOrder_PhaseTimingsUnaffected [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenWallClockJumpsDuringOrder_PhaseTimingsUnaffected [8 ms]
      Error Message:
       Expected entry.Properties[field] to be between 0.0 and 10000.0 because BeginTransactionMs 不得受牆上時鐘跳動影響（跳動一小時）, but found -0.0011.
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.BeInRange(T minimumValue, T maximumValue, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenWallClockJumpsDuringOrder_PhaseTimingsUnaffected() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1825
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     2, Passed:   127, Skipped:     0, Total:   129, Duration: 755 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    ```
- [x] 3.4 I4：暫時把 `connection.DisposeAsync()` 移到鏡像同步之後；`OrderServiceSingleConnectionTests` 須失敗。
  - 完成紀錄：mutate：connection.DisposeAsync() 移到鏡像同步之後（測試命令為 Infrastructure OrderServiceSingleConnectionTests）。
    ```
    【3.4】 --from 原文：
            await connection.DisposeAsync();
    
            await SyncAfterCommitAsync(
                new PostCommitSync(queueEntry, lockedEvent.Id, eventId, selections.QuantitySelections.Count > 0), cancellationToken);
    【3.4】 --to 原文：
            await SyncAfterCommitAsync(
                new PostCommitSync(queueEntry, lockedEvent.Id, eventId, selections.QuantitySelections.Count > 0), cancellationToken);
            await connection.DisposeAsync();
    【3.4】 完整 mutate 輸出：
    === 3.4
    mutation:         await connection.DisposeAsync();
    
            await SyncAfterCommitAsync(
                new PostCommitSync(queueEntry, lockedEvent.Id, eventId, selections.QuantitySelections.Count > 0), cancellationToken); ->         await SyncAfterCommitAsync(
                new PostCommitSync(queueEntry, lockedEvent.Id, eventId, selections.QuantitySelections.Count > 0), cancellationToken);
            await connection.DisposeAsync();
    $ docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter FullyQualifiedName~OrderServiceSingleConnectionTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Infrastructure -> /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll
      ProjectC.WebApi -> /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll
      ProjectC.Infrastructure.Tests -> /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll
    Test run for /src/tests/ProjectC.Infrastructure.Tests/bin/Debug/net10.0/ProjectC.Infrastructure.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:03.78]     ProjectC.Infrastructure.Tests.OrderServiceSingleConnectionTests.PlaceOrderAsync_WhenCommittedInQueueModeWithCountingSelection_RedisCallsRunAfterConnectionReturned [FAIL]
      Failed ProjectC.Infrastructure.Tests.OrderServiceSingleConnectionTests.PlaceOrderAsync_WhenCommittedInQueueModeWithCountingSelection_RedisCallsRunAfterConnectionReturned [109 ms]
      Error Message:
       Expected stateDuringMirrorSync to be ConnectionState.Closed {value: 0} because 入場鏡像同步期間不得占用資料庫連線, but found ConnectionState.Open {value: 1}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.Infrastructure.Tests.OrderServiceSingleConnectionTests.PlaceOrderAsync_WhenCommittedInQueueModeWithCountingSelection_RedisCallsRunAfterConnectionReturned() in /src/tests/ProjectC.Infrastructure.Tests/OrderServiceSingleConnectionTests.cs:line 195
       at ProjectC.Infrastructure.Tests.OrderServiceSingleConnectionTests.PlaceOrderAsync_WhenCommittedInQueueModeWithCountingSelection_RedisCallsRunAfterConnectionReturned() in /src/tests/ProjectC.Infrastructure.Tests/OrderServiceSingleConnectionTests.cs:line 196
       at ProjectC.Infrastructure.Tests.OrderServiceSingleConnectionTests.PlaceOrderAsync_WhenCommittedInQueueModeWithCountingSelection_RedisCallsRunAfterConnectionReturned() in /src/tests/ProjectC.Infrastructure.Tests/OrderServiceSingleConnectionTests.cs:line 196
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     1, Passed:    13, Skipped:     0, Total:    14, Duration: 1 s - ProjectC.Infrastructure.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    ```
- [x] 3.5 I5：暫時把鎖內販售期間檢查的 `_dateTimeProvider.UtcNow` 取得移到 `GetForShareAsync` 之前（2.3 實作時 `now` 由協調者在 `GetForShareAsync` 之後以單行 `var now = _dateTimeProvider.UtcNow;` 取得並傳入階段 8；mutation 以 mutate 的一次字面替換把 `var now = …` 那行移到 `GetForShareAsync` 呼叫行之前，`--from` 含 `\r\n`）；`…WhenSalesClosesWhileWaitingForEventLock…` 須失敗。
  - 完成紀錄：mutate：把 var now 移到 GetForShareAsync 之前（單一多行替換）。
    ```
    【3.5】 --from 原文：
            var lockedEvent = await _eventRepository.GetForShareAsync(eventId, cancellationToken);
            timings.EventLocked = Stopwatch.GetTimestamp();
            if (lockedEvent is null)
            {
                return Result<Guid>.Failure(Error.NotFound($"Event '{eventId}' was not found."));
            }
    
            // now 必須在 GetForShareAsync 返回之後取得：等鎖期間可能跨過開賣／停售時點，用等鎖前的時間會誤判。
            var now = _dateTimeProvider.UtcNow;
    【3.5】 --to 原文：
            var now = _dateTimeProvider.UtcNow;
            var lockedEvent = await _eventRepository.GetForShareAsync(eventId, cancellationToken);
            timings.EventLocked = Stopwatch.GetTimestamp();
            if (lockedEvent is null)
            {
                return Result<Guid>.Failure(Error.NotFound($"Event '{eventId}' was not found."));
            }
    
            // now 必須在 GetForShareAsync 返回之後取得：等鎖期間可能跨過開賣／停售時點，用等鎖前的時間會誤判。
    【3.5】 完整 mutate 輸出：
    === 3.5
    mutation:         var lockedEvent = await _eventRepository.GetForShareAsync(eventId, cancellationToken);
            timings.EventLocked = Stopwatch.GetTimestamp();
            if (lockedEvent is null)
            {
                return Result<Guid>.Failure(Error.NotFound($"Event '{eventId}' was not found."));
            }
    
            // now 必須在 GetForShareAsync 返回之後取得：等鎖期間可能跨過開賣／停售時點，用等鎖前的時間會誤判。
            var now = _dateTimeProvider.UtcNow; ->         var now = _dateTimeProvider.UtcNow;
            var lockedEvent = await _eventRepository.GetForShareAsync(eventId, cancellationToken);
            timings.EventLocked = Stopwatch.GetTimestamp();
            if (lockedEvent is null)
            {
                return Result<Guid>.Failure(Error.NotFound($"Event '{eventId}' was not found."));
            }
    
            // now 必須在 GetForShareAsync 返回之後取得：等鎖期間可能跨過開賣／停售時點，用等鎖前的時間會誤判。
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.56]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSalesClosesWhileWaitingForEventLock_ReturnsSalesClosedAndRollsBack [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSalesClosesWhileWaitingForEventLock_ReturnsSalesClosedAndRollsBack [2 ms]
      Error Message:
       System.NullReferenceException : Object reference not set to an instance of an object.
      Stack Trace:
         at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSalesClosesWhileWaitingForEventLock_ReturnsSalesClosedAndRollsBack() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1350
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     1, Passed:   128, Skipped:     0, Total:   129, Duration: 798 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    ```
- [x] 3.6a I6 優先順序：暫時對調實名檢查與限購檢查；至少 `…RealNameMissingAndMaxTicketsExceeded…` 須失敗（`…AndLimitAlsoFail…` 守的是販售期間優先，不以它判定）。
  - 完成紀錄：mutate：實名檢查與限購檢查區塊對調。
    ```
    【3.6a】 --from 原文：
            if (orderEvent is { IsRealNameRequired: true } &&
                await _memberRealNameRepository.GetAsync(buyerId, cancellationToken) is null)
            {
                return Error.RealNameRequired($"Event '{orderEvent.Id}' requires real-name registration.");
            }
    
            if (orderEvent is { MaxTicketsPerOrder: { } maxTicketsPerOrder })
            {
                // Quantity 是外部輸入；Sum(int) 用的是 checked int 累加，兩個接近 int.MaxValue 的 Quantity 會拋
                // OverflowException、變成未預期的 500（外部審查抓到）。validator 現已限制總張數 <= 50
                // （place-order-request-limits），long 累加保留為縱深防禦，避免日後 validator 調整時溢位問題重現。
                var totalQuantity = request.Selections.Sum(s => (long)s.Quantity);
                if (totalQuantity > maxTicketsPerOrder)
                {
                    return Error.Validation($"This event allows at most {maxTicketsPerOrder} ticket(s) per order.");
                }
            }
    【3.6a】 --to 原文：
            if (orderEvent is { MaxTicketsPerOrder: { } maxTicketsPerOrder })
            {
                // Quantity 是外部輸入；Sum(int) 用的是 checked int 累加，兩個接近 int.MaxValue 的 Quantity 會拋
                // OverflowException、變成未預期的 500（外部審查抓到）。validator 現已限制總張數 <= 50
                // （place-order-request-limits），long 累加保留為縱深防禦，避免日後 validator 調整時溢位問題重現。
                var totalQuantity = request.Selections.Sum(s => (long)s.Quantity);
                if (totalQuantity > maxTicketsPerOrder)
                {
                    return Error.Validation($"This event allows at most {maxTicketsPerOrder} ticket(s) per order.");
                }
            }
    
            if (orderEvent is { IsRealNameRequired: true } &&
                await _memberRealNameRepository.GetAsync(buyerId, cancellationToken) is null)
            {
                return Error.RealNameRequired($"Event '{orderEvent.Id}' requires real-name registration.");
            }
    【3.6a】 完整 mutate 輸出：
    === 3.6a
    mutation:         if (orderEvent is { IsRealNameRequired: true } &&
                await _memberRealNameRepository.GetAsync(buyerId, cancellationToken) is null)
            {
                return Error.RealNameRequired($"Event '{orderEvent.Id}' requires real-name registration.");
            }
    
            if (orderEvent is { MaxTicketsPerOrder: { } maxTicketsPerOrder })
            {
                // Quantity 是外部輸入；Sum(int) 用的是 checked int 累加，兩個接近 int.MaxValue 的 Quantity 會拋
                // OverflowException、變成未預期的 500（外部審查抓到）。validator 現已限制總張數 <= 50
                // （place-order-request-limits），long 累加保留為縱深防禦，避免日後 validator 調整時溢位問題重現。
                var totalQuantity = request.Selections.Sum(s => (long)s.Quantity);
                if (totalQuantity > maxTicketsPerOrder)
                {
                    return Error.Validation($"This event allows at most {maxTicketsPerOrder} ticket(s) per order.");
                }
            } ->         if (orderEvent is { MaxTicketsPerOrder: { } maxTicketsPerOrder })
            {
                // Quantity 是外部輸入；Sum(int) 用的是 checked int 累加，兩個接近 int.MaxValue 的 Quantity 會拋
                // OverflowException、變成未預期的 500（外部審查抓到）。validator 現已限制總張數 <= 50
                // （place-order-request-limits），long 累加保留為縱深防禦，避免日後 validator 調整時溢位問題重現。
                var totalQuantity = request.Selections.Sum(s => (long)s.Quantity);
                if (totalQuantity > maxTicketsPerOrder)
                {
                    return Error.Validation($"This event allows at most {maxTicketsPerOrder} ticket(s) per order.");
                }
            }
    
            if (orderEvent is { IsRealNameRequired: true } &&
                await _memberRealNameRepository.GetAsync(buyerId, cancellationToken) is null)
            {
                return Error.RealNameRequired($"Event '{orderEvent.Id}' requires real-name registration.");
            }
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.59]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenRealNameMissingAndMaxTicketsExceeded_ReturnsRealNameRequired [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenRealNameMissingAndMaxTicketsExceeded_ReturnsRealNameRequired [21 ms]
      Error Message:
       Expected result.Error!.Type to be ErrorType.RealNameRequired {value: 8}, but found ErrorType.Validation {value: 0}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenRealNameMissingAndMaxTicketsExceeded_ReturnsRealNameRequired() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1144
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     1, Passed:   128, Skipped:     0, Total:   129, Duration: 761 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    ```
- [x] 3.6b I6 訊息：暫時互換兩則 Validation 訊息文字（例如 "does not require a seat" 與 "requires a seat but none" 兩則，位置以方法內容為準、不以重構前行號為準）；1.2 新增的訊息逐字斷言測試須失敗。
  - 完成紀錄：mutate：互換兩則 Validation 訊息文字。
    ```
    【3.6b】 --from 原文：
                        $"Ticket type '{ticketType.Id}' does not require a seat but an event seat was specified.");
                }
    
                if (ticketType.RequiresSeat && !selectionRequest.EventSeatId.HasValue)
                {
                    return Error.Validation(
                        $"Ticket type '{ticketType.Id}' requires a seat but none was specified.");
    【3.6b】 --to 原文：
                        $"Ticket type '{ticketType.Id}' requires a seat but none was specified.");
                }
    
                if (ticketType.RequiresSeat && !selectionRequest.EventSeatId.HasValue)
                {
                    return Error.Validation(
                        $"Ticket type '{ticketType.Id}' does not require a seat but an event seat was specified.");
    【3.6b】 完整 mutate 輸出：
    === 3.6b
    mutation:                     $"Ticket type '{ticketType.Id}' does not require a seat but an event seat was specified.");
                }
    
                if (ticketType.RequiresSeat && !selectionRequest.EventSeatId.HasValue)
                {
                    return Error.Validation(
                        $"Ticket type '{ticketType.Id}' requires a seat but none was specified."); ->                     $"Ticket type '{ticketType.Id}' requires a seat but none was specified.");
                }
    
                if (ticketType.RequiresSeat && !selectionRequest.EventSeatId.HasValue)
                {
                    return Error.Validation(
                        $"Ticket type '{ticketType.Id}' does not require a seat but an event seat was specified.");
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.50]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountingTicketTypeSpecifiesEventSeatId_ReturnsVerbatimValidationMessage [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountingTicketTypeSpecifiesEventSeatId_ReturnsVerbatimValidationMessage [20 ms]
      Error Message:
       Expected result.Error.Message to be a match with the expectation, but it differs at index 51:
                  ↓ (actual)
      "…2854cd55' requires a seat but none was specified."
      "…2854cd55' does not require a seat but an event seat was specified."
                  ↑ (expected)
      Stack Trace:
         at FluentAssertions.Primitives.StringAssertions`1.Be(String expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.AssertFailure(Result`1 result, ErrorType expectedType, String expectedMessage) in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1865
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountingTicketTypeSpecifiesEventSeatId_ReturnsVerbatimValidationMessage() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1977
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.78]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatTicketTypeHasNoEventSeatId_ReturnsVerbatimValidationMessage [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatTicketTypeHasNoEventSeatId_ReturnsVerbatimValidationMessage [7 ms]
      Error Message:
       Expected result.Error.Message to be a match with the expectation, but it differs at index 51:
                  ↓ (actual)
      "…c53f228e' does not require a seat but an event seat was specified."
      "…c53f228e' requires a seat but none was specified."
                  ↑ (expected)
      Stack Trace:
         at FluentAssertions.Primitives.StringAssertions`1.Be(String expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.AssertFailure(Result`1 result, ErrorType expectedType, String expectedMessage) in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1865
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatTicketTypeHasNoEventSeatId_ReturnsVerbatimValidationMessage() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1988
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.88]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountingTicketTypeHasNonexistentSeatId_ReturnsShapeValidationNotSeatNotFound [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountingTicketTypeHasNonexistentSeatId_ReturnsShapeValidationNotSeatNotFound [11 ms]
      Error Message:
       Expected result.Error.Message "Ticket type '5516a142-9ea9-4cd8-a2f9-795b7761b067' requires a seat but none was specified." to contain "does not require a seat".
      Stack Trace:
         at FluentAssertions.Primitives.StringAssertions`1.Contain(String expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountingTicketTypeHasNonexistentSeatId_ReturnsShapeValidationNotSeatNotFound() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 2090
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     3, Passed:   126, Skipped:     0, Total:   129, Duration: 780 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    ```
- [x] 3.7 階段覆蓋突變（design 決策 4 承諾每個被搬動的階段都有突變；停止規則與還原流程同本節前言）：各做一次，各至少一個測試須失敗——階段 1：票種載入後移除「票種不存在」比對；階段 3：移除驗證用座位不存在比對（原碼無此分支則記錄並改選等價分支）；階段 4：跨活動檢查的 `> 1` 改 `> 2`；階段 9a：排隊資格 `>` 改 `>=`（AdmissionExpiresAtUtc 邊界，`OrderServiceTests.cs:679` 的 `…AdmissionExpiresExactlyAtCheckTime` 須失敗）；階段 9b：**單一次 mutate**（mutate 每次只做一次替換並於結束時還原，不可拆兩步疊加）：`--from` 取階段 9 內「`GetForUpdateAsync` 呼叫行 ＋ 其後到 `AdmissionExpiresAtUtc > _dateTimeProvider.UtcNow` 比較」的連續多行（原碼 :351–353，該比較全檔僅出現一次；重構後兩者若不相鄰，仍用單一 `--from` 涵蓋兩者），`--to` 在呼叫前插入 `var staleNow = _dateTimeProvider.UtcNow;` 並把比較改為 `> staleNow`；以 `$'…\r\n…'` 寫成完整字串，實際內容以重構後的 `LockQueueEntryAsync` 為準，須編譯通過（編譯失敗不算突變被抓到）（1.2 新增的 queue `GetForUpdate` callback 推進時鐘測試須失敗）；階段 10：移除鎖內數量比對，:375 與 :397 各一次，且 `--from` 須帶上下文（`eventSeats.Count < eventSeatIds.Count` 在 :609 也出現）；階段 11（1.1 須先判定 `BuildSelections` 是否存在可達且可被測試觀察的分支；若沒有，於 1.1 記錄「階段 11 無可達突變」並改由 diff 審查守護，不在第 3 節才卡住）：`BuildSelections` 的 `EventId` 比對（:423 預期不可達，改選等價且可達的分支並記錄，不選 :418）；階段 12：拿掉 `quantitySelections.Count > 0` 條件。測試命令同 3.1。
  - 完成紀錄：階段 1／3／4／9a／9b／10（座位與票種各一）／11（Quantity 改 1）／12 皆有測試失敗。階段 11 的 EventId 比對（s11b）測試全通過（exit=1），與 1.1 判定「不可達」一致（跨活動已在階段 4 以聯集大小保證），改以 s11a 作為階段 11 的可達突變；此為預期的不可達分支，非保護網缺口。s10a 首次因 --from 只帶兩行而與 ChangeOrderStatusAsync 的相同兩行重複匹配，exit=2（原文見下方 3.7s10a 首次嘗試），改帶第三行後重跑成功。每筆 mutation 結束後皆 `touch OrderService.cs`（mutate 還原後檔案 mtime 未更新，容器增量建置曾沿用 s10a 的過期組件使 1 個測試短暫失敗（該次失敗輸出未擷取，無法貼原文；判定為過期組件的依據：只 touch OrderService.cs、未改任何檔案後重跑即全數通過，且當時 OrderService.cs 的 grep 殘留檢查為 0。已知失敗測試為 `…WhenSeatsDisappearBetweenOutsideReadAndLock_ReturnsVerbatimNotFoundMessage`，原始訊息未保存））。
    ```
    【3.7s1】 --from 原文：
                if (!ticketTypesById.ContainsKey(ticketTypeId))
    【3.7s1】 --to 原文：
                if (false && !ticketTypesById.ContainsKey(ticketTypeId))
    【3.7s1】 完整 mutate 輸出：
    === 3.7s1
    mutation:             if (!ticketTypesById.ContainsKey(ticketTypeId)) ->             if (false && !ticketTypesById.ContainsKey(ticketTypeId))
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.71]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenTicketTypeDoesNotExist_ReturnsNotFound [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenTicketTypeDoesNotExist_ReturnsNotFound [2 ms]
      Error Message:
       System.Collections.Generic.KeyNotFoundException : The given key 'e763881b-7836-46f5-b753-dee9da58594b' was not present in the dictionary.
      Stack Trace:
         at System.Collections.Generic.Dictionary`2.get_Item(TKey key)
       at ProjectC.Application.Orders.OrderService.GetSelectionShapeErrorOrNull(PlaceOrderRequest request, IReadOnlyDictionary`2 ticketTypesById) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 330
       at ProjectC.Application.Orders.OrderService.PlaceOrderCoreAsync(Guid buyerId, PlaceOrderRequest request, PlaceOrderPhaseTimings timings, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 160
       at ProjectC.Application.Orders.OrderService.PlaceOrderCoreAsync(Guid buyerId, PlaceOrderRequest request, PlaceOrderPhaseTimings timings, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 288
       at ProjectC.Application.Orders.OrderService.PlaceOrderAsync(Guid buyerId, PlaceOrderRequest request, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 87
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenTicketTypeDoesNotExist_ReturnsNotFound() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 169
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.73]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenTwoTicketTypesMissing_ReportsFirstMissingInRequestOrder [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenTwoTicketTypesMissing_ReportsFirstMissingInRequestOrder [2 ms]
      Error Message:
       System.Collections.Generic.KeyNotFoundException : The given key 'c7e57d04-06f2-4a2b-8bf5-bc73576bcd9f' was not present in the dictionary.
      Stack Trace:
         at System.Collections.Generic.Dictionary`2.get_Item(TKey key)
       at ProjectC.Application.Orders.OrderService.GetSelectionShapeErrorOrNull(PlaceOrderRequest request, IReadOnlyDictionary`2 ticketTypesById) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 330
       at ProjectC.Application.Orders.OrderService.PlaceOrderCoreAsync(Guid buyerId, PlaceOrderRequest request, PlaceOrderPhaseTimings timings, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 160
       at ProjectC.Application.Orders.OrderService.PlaceOrderCoreAsync(Guid buyerId, PlaceOrderRequest request, PlaceOrderPhaseTimings timings, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 288
       at ProjectC.Application.Orders.OrderService.PlaceOrderAsync(Guid buyerId, PlaceOrderRequest request, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 87
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenTwoTicketTypesMissing_ReportsFirstMissingInRequestOrder() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 2281
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.80]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenFirstSelectionViolatesShapeAndSecondTicketTypeMissing_ReturnsTicketTypeNotFound [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenFirstSelectionViolatesShapeAndSecondTicketTypeMissing_ReturnsTicketTypeNotFound [18 ms]
      Error Message:
       Expected result.Error!.Type to be ErrorType.NotFound {value: 1}, but found ErrorType.Validation {value: 0}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.AssertFailure(Result`1 result, ErrorType expectedType, String expectedMessage) in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1864
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenFirstSelectionViolatesShapeAndSecondTicketTypeMissing_ReturnsTicketTypeNotFound() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 2106
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.91]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenDebugEnabledAndTicketTypeNotFound_LogsConnectionOpenWithNullInTransactionPhases [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenDebugEnabledAndTicketTypeNotFound_LogsConnectionOpenWithNullInTransactionPhases [2 ms]
      Error Message:
       System.Collections.Generic.KeyNotFoundException : The given key '1a687ad2-a35b-4b05-9114-9b855838619c' was not present in the dictionary.
      Stack Trace:
         at System.Collections.Generic.Dictionary`2.get_Item(TKey key)
       at ProjectC.Application.Orders.OrderService.GetSelectionShapeErrorOrNull(PlaceOrderRequest request, IReadOnlyDictionary`2 ticketTypesById) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 330
       at ProjectC.Application.Orders.OrderService.PlaceOrderCoreAsync(Guid buyerId, PlaceOrderRequest request, PlaceOrderPhaseTimings timings, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 160
       at ProjectC.Application.Orders.OrderService.PlaceOrderCoreAsync(Guid buyerId, PlaceOrderRequest request, PlaceOrderPhaseTimings timings, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 288
       at ProjectC.Application.Orders.OrderService.PlaceOrderAsync(Guid buyerId, PlaceOrderRequest request, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 87
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenDebugEnabledAndTicketTypeNotFound_LogsConnectionOpenWithNullInTransactionPhases() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1685
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     4, Passed:   125, Skipped:     0, Total:   129, Duration: 754 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    
    【3.7s3】 --from 原文：
                if (validationSeats.Count < seatSelectionEventSeatIds.Count)
    【3.7s3】 --to 原文：
                if (false && validationSeats.Count < seatSelectionEventSeatIds.Count)
    【3.7s3】 完整 mutate 輸出：
    === 3.7s3
    mutation:             if (validationSeats.Count < seatSelectionEventSeatIds.Count) ->             if (false && validationSeats.Count < seatSelectionEventSeatIds.Count)
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.57]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatDoesNotExistOutsideTransaction_ReturnsVerbatimNotFoundMessage [FAIL]
    [xUnit.net 00:00:00.59]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatDoesNotExist_ReturnsNotFound [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatDoesNotExistOutsideTransaction_ReturnsVerbatimNotFoundMessage [2 ms]
      Error Message:
       System.Collections.Generic.KeyNotFoundException : The given key '88eb23ef-bc39-4769-b0a5-43b623ad15fb' was not present in the dictionary.
      Stack Trace:
         at System.Collections.Generic.Dictionary`2.get_Item(TKey key)
       at ProjectC.Application.Orders.OrderService.<>c__DisplayClass45_0.<GetEarlyConflictOrNull>b__2(Guid id) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 715
       at System.Linq.Enumerable.TryGetFirst[TSource](IEnumerable`1 source, Func`2 predicate, Boolean& found)
       at System.Linq.Enumerable.FirstOrDefault[TSource](IEnumerable`1 source, Func`2 predicate)
       at ProjectC.Application.Orders.OrderService.GetEarlyConflictOrNull(PlaceOrderRequest request, IReadOnlyDictionary`2 seatsById, IReadOnlyDictionary`2 ticketTypesById, DateTime now) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 712
       at ProjectC.Application.Orders.OrderService.PlaceOrderCoreAsync(Guid buyerId, PlaceOrderRequest request, PlaceOrderPhaseTimings timings, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 207
       at ProjectC.Application.Orders.OrderService.PlaceOrderCoreAsync(Guid buyerId, PlaceOrderRequest request, PlaceOrderPhaseTimings timings, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 288
       at ProjectC.Application.Orders.OrderService.PlaceOrderAsync(Guid buyerId, PlaceOrderRequest request, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 87
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatDoesNotExistOutsideTransaction_ReturnsVerbatimNotFoundMessage() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1997
    --- End of stack trace from previous location ---
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatDoesNotExist_ReturnsNotFound [2 ms]
      Error Message:
       System.Collections.Generic.KeyNotFoundException : The given key '8f020c4b-1ba6-422f-a178-5ee14d8e7879' was not present in the dictionary.
      Stack Trace:
         at System.Collections.Generic.Dictionary`2.get_Item(TKey key)
       at ProjectC.Application.Orders.OrderService.<>c__DisplayClass45_0.<GetEarlyConflictOrNull>b__2(Guid id) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 715
       at System.Linq.Enumerable.TryGetFirst[TSource](IEnumerable`1 source, Func`2 predicate, Boolean& found)
       at System.Linq.Enumerable.FirstOrDefault[TSource](IEnumerable`1 source, Func`2 predicate)
       at ProjectC.Application.Orders.OrderService.GetEarlyConflictOrNull(PlaceOrderRequest request, IReadOnlyDictionary`2 seatsById, IReadOnlyDictionary`2 ticketTypesById, DateTime now) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 712
       at ProjectC.Application.Orders.OrderService.PlaceOrderCoreAsync(Guid buyerId, PlaceOrderRequest request, PlaceOrderPhaseTimings timings, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 207
       at ProjectC.Application.Orders.OrderService.PlaceOrderCoreAsync(Guid buyerId, PlaceOrderRequest request, PlaceOrderPhaseTimings timings, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 288
       at ProjectC.Application.Orders.OrderService.PlaceOrderAsync(Guid buyerId, PlaceOrderRequest request, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 87
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatDoesNotExist_ReturnsNotFound() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 182
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.81]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatMissingAndTicketTypesSpanEvents_ReturnsSeatNotFoundNotCrossEvent [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatMissingAndTicketTypesSpanEvents_ReturnsSeatNotFoundNotCrossEvent [18 ms]
      Error Message:
       Expected result.Error!.Type to be ErrorType.NotFound {value: 1}, but found ErrorType.Validation {value: 0}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.AssertFailure(Result`1 result, ErrorType expectedType, String expectedMessage) in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1864
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatMissingAndTicketTypesSpanEvents_ReturnsSeatNotFoundNotCrossEvent() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 2121
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     3, Passed:   126, Skipped:     0, Total:   129, Duration: 748 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    
    【3.7s4】 --from 原文：
            => distinctEventIds.Count > 1 ? Error.Validation("All selected items must belong to the same event.") : null;
    【3.7s4】 --to 原文：
            => distinctEventIds.Count > 2 ? Error.Validation("All selected items must belong to the same event.") : null;
    【3.7s4】 完整 mutate 輸出：
    === 3.7s4
    mutation:         => distinctEventIds.Count > 1 ? Error.Validation("All selected items must belong to the same event.") : null; ->         => distinctEventIds.Count > 2 ? Error.Validation("All selected items must belong to the same event.") : null;
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.37]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenDebugEnabledAndItemsSpanEvents_LogsConnectionOpenWithNullInTransactionPhases [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenDebugEnabledAndItemsSpanEvents_LogsConnectionOpenWithNullInTransactionPhases [184 ms]
      Error Message:
       Expected result.Error!.Type to be ErrorType.Validation {value: 0}, but found ErrorType.NotFound {value: 1}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenDebugEnabledAndItemsSpanEvents_LogsConnectionOpenWithNullInTransactionPhases() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1703
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.43]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenItemsSpanEventsAndOneIsNotOpen_ReturnsCrossEventValidation [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenItemsSpanEventsAndOneIsNotOpen_ReturnsCrossEventValidation [6 ms]
      Error Message:
       Expected result.Error!.Type to be ErrorType.Validation {value: 0}, but found ErrorType.SalesNotOpen {value: 10}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenItemsSpanEventsAndOneIsNotOpen_ReturnsCrossEventValidation() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1331
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.61]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountingSelectionsSpanDifferentEvents_ReturnsValidationErrorBeforeTakingAnyLock [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountingSelectionsSpanDifferentEvents_ReturnsValidationErrorBeforeTakingAnyLock [5 ms]
      Error Message:
       Expected fixture.UnitOfWork.BeginTransactionCallCount to be 0 because 跨活動驗證失敗時不應該開始交易，更不該取得任何鎖, but found 1 (difference of 1).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountingSelectionsSpanDifferentEvents_ReturnsValidationErrorBeforeTakingAnyLock() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 478
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.68]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatAndItsPairedTicketTypeBelongToDifferentEvents_ReturnsValidationErrorBeforeTakingAnyLock [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatAndItsPairedTicketTypeBelongToDifferentEvents_ReturnsValidationErrorBeforeTakingAnyLock [4 ms]
      Error Message:
       Expected result.Error!.Type to be ErrorType.Validation {value: 0}, but found ErrorType.NotFound {value: 1}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatAndItsPairedTicketTypeBelongToDifferentEvents_ReturnsValidationErrorBeforeTakingAnyLock() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 495
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.89]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenItemsSpanEvents_ReturnsCrossEventValidationWithoutReadingEvent [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenItemsSpanEvents_ReturnsCrossEventValidationWithoutReadingEvent [6 ms]
      Error Message:
       Expected result.Error!.Type to be ErrorType.Validation {value: 0}, but found ErrorType.NotFound {value: 1}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.AssertFailure(Result`1 result, ErrorType expectedType, String expectedMessage) in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1864
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenItemsSpanEvents_ReturnsCrossEventValidationWithoutReadingEvent() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 2133
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.93]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenItemsSpanRealNameEventAndOtherEvent_ReturnsCrossEventValidationWithoutQueryingRealName [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenItemsSpanRealNameEventAndOtherEvent_ReturnsCrossEventValidationWithoutQueryingRealName [5 ms]
      Error Message:
       Expected result.Error!.Type to be ErrorType.Validation {value: 0}, but found ErrorType.RealNameRequired {value: 8}.
      Stack Trace:
         at FluentAssertions.Primitives.EnumAssertions`2.Be(TEnum expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenItemsSpanRealNameEventAndOtherEvent_ReturnsCrossEventValidationWithoutQueryingRealName() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1160
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     6, Passed:   123, Skipped:     0, Total:   129, Duration: 775 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    
    【3.7s9a】 --from 原文：
                queueEntry.AdmissionExpiresAtUtc > _dateTimeProvider.UtcNow;
    【3.7s9a】 --to 原文：
                queueEntry.AdmissionExpiresAtUtc >= _dateTimeProvider.UtcNow;
    【3.7s9a】 完整 mutate 輸出：
    === 3.7s9a
    mutation:             queueEntry.AdmissionExpiresAtUtc > _dateTimeProvider.UtcNow; ->             queueEntry.AdmissionExpiresAtUtc >= _dateTimeProvider.UtcNow;
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.47]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenAdmissionExpiresWhileWaitingForQueueEntryLock_ReturnsQueueAdmissionRequired [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenAdmissionExpiresWhileWaitingForQueueEntryLock_ReturnsQueueAdmissionRequired [5 ms]
      Error Message:
       System.NullReferenceException : Object reference not set to an instance of an object.
      Stack Trace:
         at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenAdmissionExpiresWhileWaitingForQueueEntryLock_ReturnsQueueAdmissionRequired() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1905
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.86]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenAdmissionExpiresExactlyAtCheckTime_ReturnsQueueAdmissionRequiredAndDoesNotExpireEntry [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenAdmissionExpiresExactlyAtCheckTime_ReturnsQueueAdmissionRequiredAndDoesNotExpireEntry [15 ms]
      Error Message:
       Expected result.IsSuccess to be False, but found True.
      Stack Trace:
         at FluentAssertions.Primitives.BooleanAssertions`1.BeFalse(String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenAdmissionExpiresExactlyAtCheckTime_ReturnsQueueAdmissionRequiredAndDoesNotExpireEntry() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 694
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     2, Passed:   127, Skipped:     0, Total:   129, Duration: 739 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    
    【3.7s9b】 --from 原文：
            var queueEntry = await _purchaseQueueRepository.GetForUpdateAsync(lockedEvent.Id, buyerId, cancellationToken);
            var isAdmitted = queueEntry is { Status: PurchaseQueueEntryStatus.Admitted } &&
                queueEntry.AdmissionExpiresAtUtc > _dateTimeProvider.UtcNow;
    【3.7s9b】 --to 原文：
            var staleNow = _dateTimeProvider.UtcNow;
            var queueEntry = await _purchaseQueueRepository.GetForUpdateAsync(lockedEvent.Id, buyerId, cancellationToken);
            var isAdmitted = queueEntry is { Status: PurchaseQueueEntryStatus.Admitted } &&
                queueEntry.AdmissionExpiresAtUtc > staleNow;
    【3.7s9b】 完整 mutate 輸出：
    === 3.7s9b
    mutation:         var queueEntry = await _purchaseQueueRepository.GetForUpdateAsync(lockedEvent.Id, buyerId, cancellationToken);
            var isAdmitted = queueEntry is { Status: PurchaseQueueEntryStatus.Admitted } &&
                queueEntry.AdmissionExpiresAtUtc > _dateTimeProvider.UtcNow; ->         var staleNow = _dateTimeProvider.UtcNow;
            var queueEntry = await _purchaseQueueRepository.GetForUpdateAsync(lockedEvent.Id, buyerId, cancellationToken);
            var isAdmitted = queueEntry is { Status: PurchaseQueueEntryStatus.Admitted } &&
                queueEntry.AdmissionExpiresAtUtc > staleNow;
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.49]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenAdmissionExpiresWhileWaitingForQueueEntryLock_ReturnsQueueAdmissionRequired [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenAdmissionExpiresWhileWaitingForQueueEntryLock_ReturnsQueueAdmissionRequired [3 ms]
      Error Message:
       System.NullReferenceException : Object reference not set to an instance of an object.
      Stack Trace:
         at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenAdmissionExpiresWhileWaitingForQueueEntryLock_ReturnsQueueAdmissionRequired() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1905
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     1, Passed:   128, Skipped:     0, Total:   129, Duration: 741 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    
    【3.7s10a 首次嘗試（兩行 --from）】完整輸出：
    # 命令（轉述，--from／--to 以下方 from／to 原文為準）
    $ bash scripts/verify-test-evidence.sh mutate --file src/ProjectC.Application/Orders/OrderService.cs --from <2 行 if (eventSeats.Count < eventSeatIds.Count) + {> --to <if (false && ...)> -- docker compose exec -T api dotnet test ...
    預期原字串恰好出現 1 次，實際 2 次
    exit=2
    
    【3.7s10a】 --from 原文：
                if (eventSeats.Count < eventSeatIds.Count)
                {
                    return Result<Dictionary<Guid, EventSeat>>.Failure(Error.NotFound("One or more selected seats were not found."));
    【3.7s10a】 --to 原文：
                if (false && eventSeats.Count < eventSeatIds.Count)
                {
                    return Result<Dictionary<Guid, EventSeat>>.Failure(Error.NotFound("One or more selected seats were not found."));
    【3.7s10a】 完整 mutate 輸出：
    === 3.7s10a
    mutation:             if (eventSeats.Count < eventSeatIds.Count)
                {
                    return Result<Dictionary<Guid, EventSeat>>.Failure(Error.NotFound("One or more selected seats were not found.")); ->             if (false && eventSeats.Count < eventSeatIds.Count)
                {
                    return Result<Dictionary<Guid, EventSeat>>.Failure(Error.NotFound("One or more selected seats were not found."));
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.63]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatsDisappearBetweenOutsideReadAndLock_ReturnsVerbatimNotFoundMessage [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatsDisappearBetweenOutsideReadAndLock_ReturnsVerbatimNotFoundMessage [27 ms]
      Error Message:
       Expected result.Error.Message to be a match with the expectation, but it differs at index 0:
       ↓ (actual)
      "Seat '043b86c1-b2bb-41f9-8b7f-3a80e399bbf5' was not found."
      "One or more selected seats were not found."
       ↑ (expected)
      Stack Trace:
         at FluentAssertions.Primitives.StringAssertions`1.Be(String expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.AssertFailure(Result`1 result, ErrorType expectedType, String expectedMessage) in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1865
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSeatsDisappearBetweenOutsideReadAndLock_ReturnsVerbatimNotFoundMessage() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 2017
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     1, Passed:   128, Skipped:     0, Total:   129, Duration: 759 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    
    【3.7s10b】 --from 原文：
                var lockedTicketTypes = await _ticketTypeRepository.GetForUpdateAsync(countingTicketTypeIds, cancellationToken);
                if (lockedTicketTypes.Count < countingTicketTypeIds.Count)
    【3.7s10b】 --to 原文：
                var lockedTicketTypes = await _ticketTypeRepository.GetForUpdateAsync(countingTicketTypeIds, cancellationToken);
                if (false && lockedTicketTypes.Count < countingTicketTypeIds.Count)
    【3.7s10b】 完整 mutate 輸出：
    === 3.7s10b
    mutation:             var lockedTicketTypes = await _ticketTypeRepository.GetForUpdateAsync(countingTicketTypeIds, cancellationToken);
                if (lockedTicketTypes.Count < countingTicketTypeIds.Count) ->             var lockedTicketTypes = await _ticketTypeRepository.GetForUpdateAsync(countingTicketTypeIds, cancellationToken);
                if (false && lockedTicketTypes.Count < countingTicketTypeIds.Count)
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.46]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountingTicketTypesDisappearBetweenOutsideReadAndLock_ReturnsVerbatimNotFoundMessage [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountingTicketTypesDisappearBetweenOutsideReadAndLock_ReturnsVerbatimNotFoundMessage [2 ms]
      Error Message:
       System.Collections.Generic.KeyNotFoundException : The given key '3226624f-a7ee-49c5-82b2-b86ae87f1f21' was not present in the dictionary.
      Stack Trace:
         at System.Collections.Generic.Dictionary`2.get_Item(TKey key)
       at ProjectC.Application.Orders.OrderService.BuildSelections(SelectionSource source) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 590
       at ProjectC.Application.Orders.OrderService.PlaceOrderCoreAsync(Guid buyerId, PlaceOrderRequest request, PlaceOrderPhaseTimings timings, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 260
       at ProjectC.Application.Orders.OrderService.PlaceOrderCoreAsync(Guid buyerId, PlaceOrderRequest request, PlaceOrderPhaseTimings timings, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 288
       at ProjectC.Application.Orders.OrderService.PlaceOrderCoreAsync(Guid buyerId, PlaceOrderRequest request, PlaceOrderPhaseTimings timings, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 288
       at ProjectC.Application.Orders.OrderService.PlaceOrderAsync(Guid buyerId, PlaceOrderRequest request, CancellationToken cancellationToken) in /src/src/ProjectC.Application/Orders/OrderService.cs:line 87
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenCountingTicketTypesDisappearBetweenOutsideReadAndLock_ReturnsVerbatimNotFoundMessage() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 2035
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     1, Passed:   128, Skipped:     0, Total:   129, Duration: 748 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    
    【3.7s11a】 --from 原文：
                    quantitySelections.Add(new QuantitySelection(source.LockedTicketTypesById[ticketType.Id], selectionRequest.Quantity));
    【3.7s11a】 --to 原文：
                    quantitySelections.Add(new QuantitySelection(source.LockedTicketTypesById[ticketType.Id], 1));
    【3.7s11a】 完整 mutate 輸出：
    === 3.7s11a
    mutation:                 quantitySelections.Add(new QuantitySelection(source.LockedTicketTypesById[ticketType.Id], selectionRequest.Quantity)); ->                 quantitySelections.Add(new QuantitySelection(source.LockedTicketTypesById[ticketType.Id], 1));
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.47]     ProjectC.Application.Tests.Orders.OrderServiceTests.ConfirmOrderAsync_WhenPureCountingOrder_SucceedsWithoutFurtherReducingAvailableQuantity [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.ConfirmOrderAsync_WhenPureCountingOrder_SucceedsWithoutFurtherReducingAvailableQuantity [43 ms]
      Error Message:
       Expected ticketType.AvailableQuantity to be 7, but found 9.
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.ConfirmOrderAsync_WhenPureCountingOrder_SucceedsWithoutFurtherReducingAvailableQuantity() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 877
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.59]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WithMixedSeatAndCountingSelections_CreatesSingleOrderWithBothItemShapes [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WithMixedSeatAndCountingSelections_CreatesSingleOrderWithBothItemShapes [8 ms]
      Error Message:
       Expected order.Items to contain a single item matching (i.EventSeatId == null) AndAlso (i.Quantity == 2), but no such item was found.
      Stack Trace:
         at FluentAssertions.Collections.GenericCollectionAssertions`3.ContainSingle(Expression`1 predicate, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WithMixedSeatAndCountingSelections_CreatesSingleOrderWithBothItemShapes() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 455
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.74]     ProjectC.Application.Tests.Orders.OrderServiceTests.ConfirmOrderAsync_WhenMixedOrder_ChargesSumOfSeatUnitPriceAndCountingUnitPriceTimesQuantity [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.ConfirmOrderAsync_WhenMixedOrder_ChargesSumOfSeatUnitPriceAndCountingUnitPriceTimesQuantity [10 ms]
      Error Message:
       Expected fixture.PaymentGateway.LastAmount to be 1100M, but found 800M (difference of -300).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.ConfirmOrderAsync_WhenMixedOrder_ChargesSumOfSeatUnitPriceAndCountingUnitPriceTimesQuantity() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1047
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.85]     ProjectC.Application.Tests.Orders.OrderServiceTests.ConfirmOrderAsync_WhenPureCountingOrderWithQuantityGreaterThanOne_ChargesUnitPriceTimesQuantity [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.ConfirmOrderAsync_WhenPureCountingOrderWithQuantityGreaterThanOne_ChargesUnitPriceTimesQuantity [8 ms]
      Error Message:
       Expected fixture.PaymentGateway.LastAmount to be 900M, but found 300M (difference of -600).
      Stack Trace:
         at FluentAssertions.Numeric.NumericAssertionsBase`3.Be(T expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.ConfirmOrderAsync_WhenPureCountingOrderWithQuantityGreaterThanOne_ChargesUnitPriceTimesQuantity() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 887
    --- End of stack trace from previous location ---
    [xUnit.net 00:00:00.90]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WithPureCountingSelection_CreatesOrderAndReducesAvailableQuantity [FAIL]
    [xUnit.net 00:00:00.91]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSecondCountingSelectionFailsAndCompensatingRollbackOccurs_DoesNotInvalidateCacheOrCommit [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WithPureCountingSelection_CreatesOrderAndReducesAvailableQuantity [7 ms]
      Error Message:
       Expected order.Items to contain a single item matching (i.TicketTypeId == Convert(af35a755-371a-4528-ad40-c845792a82f4, Nullable`1)) AndAlso (i.EventSeatId == null) AndAlso (i.Quantity == 3), but no such item was found.
      Stack Trace:
         at FluentAssertions.Collections.GenericCollectionAssertions`3.ContainSingle(Expression`1 predicate, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WithPureCountingSelection_CreatesOrderAndReducesAvailableQuantity() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 413
    --- End of stack trace from previous location ---
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSecondCountingSelectionFailsAndCompensatingRollbackOccurs_DoesNotInvalidateCacheOrCommit [6 ms]
      Error Message:
       Expected result.IsSuccess to be False, but found True.
      Stack Trace:
         at FluentAssertions.Primitives.BooleanAssertions`1.BeFalse(String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenSecondCountingSelectionFailsAndCompensatingRollbackOccurs_DoesNotInvalidateCacheOrCommit() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 959
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     6, Passed:   123, Skipped:     0, Total:   129, Duration: 768 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    
    【3.7s11b】 --from 原文：
                if (ticketType.EventId != eventSeat.EventId)
    【3.7s11b】 --to 原文：
                if (false && ticketType.EventId != eventSeat.EventId)
    【3.7s11b】 完整 mutate 輸出：
    === 3.7s11b
    mutation:             if (ticketType.EventId != eventSeat.EventId) ->             if (false && ticketType.EventId != eventSeat.EventId)
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    
    Passed!  - Failed:     0, Passed:   129, Skipped:     0, Total:   129, Duration: 826 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試仍通過，證據不成立
    exit=1
    
    【3.7s12】 --from 原文：
            if (sync.HasQuantitySelections)
    【3.7s12】 --to 原文：
            if (sync.HasQuantitySelections || true)
    【3.7s12】 完整 mutate 輸出：
    === 3.7s12
    mutation:         if (sync.HasQuantitySelections) ->         if (sync.HasQuantitySelections || true)
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.80]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WithSeatOnlySelection_DoesNotInvalidateTicketTypesCache [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WithSeatOnlySelection_DoesNotInvalidateTicketTypesCache [22 ms]
      Error Message:
       Expected fixture.QueryCache.RemoveCalls to be empty, but found at least one item {"query-cache:ticket-types:event:6f45126e-bde1-4a51-90f2-8b283c62797b"}.
      Stack Trace:
         at FluentAssertions.Collections.GenericCollectionAssertions`3.<>c__DisplayClass10_0.<BeEmpty>b__0(AssertionChain chain)
       at FluentAssertions.Collections.GenericCollectionAssertions`3.BeEmpty(String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WithSeatOnlySelection_DoesNotInvalidateTicketTypesCache() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 940
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     1, Passed:   128, Skipped:     0, Total:   129, Duration: 794 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    ```
  - 全部 mutation 還原後驗證（原文）：
    ```
    $ touch src/ProjectC.Application/Orders/OrderService.cs   # mutate 還原後 mtime 未更新，重跑前必須 touch
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
    Passed!  - Failed:     0, Passed:   129, Skipped:     0, Total:   129, Duration: 839 ms - ProjectC.Application.Tests.dll (net10.0)
    $ docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter FullyQualifiedName~OrderServiceSingleConnectionTests
    Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14, Duration: 1 s - ProjectC.Infrastructure.Tests.dll (net10.0)
    $ grep -cE "false &&|staleNow|earlyTransaction|preLocked" src/ProjectC.Application/Orders/OrderService.cs
    0
    ```

- [x] 3.8 補強測試的突變驗證（Opus 第二輪建議 S1：CancellationToken 轉送；adversarial 缺口：鏡像同步參數）：對 `…SyncsCompletionAndInvalidatesCacheWithEventAndEntryIds`（m4.1、m4.6 之一）與 `…ForwardsCancellationTokenToEveryLockAndMirrorCall`（m4.2–m4.6）各做 mutation，皆須失敗。
  - 完成紀錄：6 筆 mutation 全數被抓到（各恰 1 個失敗，失敗測試名稱與斷言訊息見下方原文）。m4.1 由鏡像參數測試抓到；m4.2–m4.6 由 token 轉送測試抓到；m4.6 的 `LastSyncCompletionToken` 同時是 token 測試的斷言。每筆結束後皆 `touch OrderService.cs`。更正：adversarial 審查稱 I1 例外分支（實名旗標在鎖內外讀取間翻轉 → InvalidOperationException）無測試，經查已有 `…WhenRealNameFlagFlipsFromFalseToTrueBetweenReads_Throws…` 與 `…TrueToFalse…`，該宣稱不成立。　適用範圍：這 2 個測試為本 change 新增，未在 HEAD 原碼上執行（第 3 節前言的還原流程適用 3.1–3.7）。token 轉送的守護範圍僅 6 個呼叫（GetByIdAsync、GetForShareAsync、佇列／座位／票種的 GetForUpdateAsync、SyncCompletionAsync）；`TicketType.GetByIdsAsync`、`EventSeat.GetByIdsAsync`、鎖內補位的 `MemberRealName.GetAsync`、`QueryCache.RemoveAsync` 的 token 轉送未被測試守護，由 diff 審查守護。
    ```
    【m4.1】 SyncCompletionAsync 兩個 Guid 引數互換（OrderService.cs 階段 12） --from 原文：
                await _admissionMirror.SyncCompletionAsync(sync.LockedEventId, sync.QueueEntry.Id, cancellationToken);
    【m4.1】 SyncCompletionAsync 兩個 Guid 引數互換（OrderService.cs 階段 12） --to 原文：
                await _admissionMirror.SyncCompletionAsync(sync.QueueEntry.Id, sync.LockedEventId, cancellationToken);
    【m4.1】 SyncCompletionAsync 兩個 Guid 引數互換（OrderService.cs 階段 12） 完整 mutate 輸出：
    === m4.1
    mutation:             await _admissionMirror.SyncCompletionAsync(sync.LockedEventId, sync.QueueEntry.Id, cancellationToken); ->             await _admissionMirror.SyncCompletionAsync(sync.QueueEntry.Id, sync.LockedEventId, cancellationToken);
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.56]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithCountingItems_SyncsCompletionAndInvalidatesCacheWithEventAndEntryIds [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithCountingItems_SyncsCompletionAndInvalidatesCacheWithEventAndEntryIds [33 ms]
      Error Message:
       Expected fixture.AdmissionMirror.SyncCompletionCalls to be equal to {
        {
            Item1 = {95d71b40-61b6-49e5-8835-741476be9a1f},
            Item2 = {40e02b5b-4186-4d12-8d6a-07adbef79e7b}
        }
    }, but {
        {
            Item1 = {40e02b5b-4186-4d12-8d6a-07adbef79e7b},
            Item2 = {95d71b40-61b6-49e5-8835-741476be9a1f}
        }
    } differs at index 0.
      Stack Trace:
         at FluentAssertions.Collections.GenericCollectionAssertions`3.<>c__DisplayClass106_0`1.<AssertSubjectEquality>b__0(AssertionChain chain)
       at FluentAssertions.Collections.GenericCollectionAssertions`3.AssertSubjectEquality[TExpectation](IEnumerable`1 expectation, Func`3 equalityComparison, String because, Object[] becauseArgs)
       at FluentAssertions.Collections.GenericCollectionAssertions`3.Equal(T[] elements)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithCountingItems_SyncsCompletionAndInvalidatesCacheWithEventAndEntryIds() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1908
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     1, Passed:   130, Skipped:     0, Total:   131, Duration: 746 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    
    【m4.2】 GetForShareAsync 改傳 CancellationToken.None --from 原文：
            var lockedEvent = await _eventRepository.GetForShareAsync(eventId, cancellationToken);
    【m4.2】 GetForShareAsync 改傳 CancellationToken.None --to 原文：
            var lockedEvent = await _eventRepository.GetForShareAsync(eventId, CancellationToken.None);
    【m4.2】 GetForShareAsync 改傳 CancellationToken.None 完整 mutate 輸出：
    === m4.2
    mutation:         var lockedEvent = await _eventRepository.GetForShareAsync(eventId, cancellationToken); ->         var lockedEvent = await _eventRepository.GetForShareAsync(eventId, CancellationToken.None);
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.66]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_ForwardsCancellationTokenToEveryLockAndMirrorCall [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_ForwardsCancellationTokenToEveryLockAndMirrorCall [29 ms]
      Error Message:
       Expected fixture.EventRepository.LastGetForShareToken to be System.Threading.CancellationToken
    {
        CanBeCanceled = True,
        IsCancellationRequested = False,
        WaitHandle = System.Threading.ManualResetEvent
        {
            Handle = 1160,
            SafeWaitHandle = Microsoft.Win32.SafeHandles.SafeWaitHandle
            {
                IsClosed = False,
                IsInvalid = False
            }
        }
    }, but found System.Threading.CancellationToken
    {
        CanBeCanceled = False,
        IsCancellationRequested = False,
        WaitHandle = System.Threading.ManualResetEvent
        {
            Handle = 1164,
            SafeWaitHandle = Microsoft.Win32.SafeHandles.SafeWaitHandle
            {
                IsClosed = False,
                IsInvalid = False
            }
        }
    }.
      Stack Trace:
         at FluentAssertions.Primitives.ObjectAssertions`2.Be(TSubject expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_ForwardsCancellationTokenToEveryLockAndMirrorCall() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1932
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     1, Passed:   130, Skipped:     0, Total:   131, Duration: 779 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    
    【m4.3】 佇列列鎖 GetForUpdateAsync 改傳 CancellationToken.None --from 原文：
            var queueEntry = await _purchaseQueueRepository.GetForUpdateAsync(lockedEvent.Id, buyerId, cancellationToken);
    【m4.3】 佇列列鎖 GetForUpdateAsync 改傳 CancellationToken.None --to 原文：
            var queueEntry = await _purchaseQueueRepository.GetForUpdateAsync(lockedEvent.Id, buyerId, CancellationToken.None);
    【m4.3】 佇列列鎖 GetForUpdateAsync 改傳 CancellationToken.None 完整 mutate 輸出：
    === m4.3
    mutation:         var queueEntry = await _purchaseQueueRepository.GetForUpdateAsync(lockedEvent.Id, buyerId, cancellationToken); ->         var queueEntry = await _purchaseQueueRepository.GetForUpdateAsync(lockedEvent.Id, buyerId, CancellationToken.None);
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.69]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_ForwardsCancellationTokenToEveryLockAndMirrorCall [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_ForwardsCancellationTokenToEveryLockAndMirrorCall [29 ms]
      Error Message:
       Expected fixture.PurchaseQueueRepository.LastGetForUpdateToken to be System.Threading.CancellationToken
    {
        CanBeCanceled = True,
        IsCancellationRequested = False,
        WaitHandle = System.Threading.ManualResetEvent
        {
            Handle = 1160,
            SafeWaitHandle = Microsoft.Win32.SafeHandles.SafeWaitHandle
            {
                IsClosed = False,
                IsInvalid = False
            }
        }
    }, but found System.Threading.CancellationToken
    {
        CanBeCanceled = False,
        IsCancellationRequested = False,
        WaitHandle = System.Threading.ManualResetEvent
        {
            Handle = 1164,
            SafeWaitHandle = Microsoft.Win32.SafeHandles.SafeWaitHandle
            {
                IsClosed = False,
                IsInvalid = False
            }
        }
    }.
      Stack Trace:
         at FluentAssertions.Primitives.ObjectAssertions`2.Be(TSubject expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_ForwardsCancellationTokenToEveryLockAndMirrorCall() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1933
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     1, Passed:   130, Skipped:     0, Total:   131, Duration: 795 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    
    【m4.4】 座位鎖 GetForUpdateAsync 改傳 CancellationToken.None（--from 含 LockSeatsAsync 的上下文以唯一匹配） --from 原文：
            {
                var eventSeats = await _eventSeatRepository.GetForUpdateAsync(eventSeatIds, cancellationToken);
                if (eventSeats.Count < eventSeatIds.Count)
                {
                    return Result<Dictionary<Guid, EventSeat>>
    【m4.4】 座位鎖 GetForUpdateAsync 改傳 CancellationToken.None（--from 含 LockSeatsAsync 的上下文以唯一匹配） --to 原文：
            {
                var eventSeats = await _eventSeatRepository.GetForUpdateAsync(eventSeatIds, CancellationToken.None);
                if (eventSeats.Count < eventSeatIds.Count)
                {
                    return Result<Dictionary<Guid, EventSeat>>
    【m4.4】 座位鎖 GetForUpdateAsync 改傳 CancellationToken.None（--from 含 LockSeatsAsync 的上下文以唯一匹配） 完整 mutate 輸出：
    === m4.4
    mutation:         {
                var eventSeats = await _eventSeatRepository.GetForUpdateAsync(eventSeatIds, cancellationToken);
                if (eventSeats.Count < eventSeatIds.Count)
                {
                    return Result<Dictionary<Guid, EventSeat>> ->         {
                var eventSeats = await _eventSeatRepository.GetForUpdateAsync(eventSeatIds, CancellationToken.None);
                if (eventSeats.Count < eventSeatIds.Count)
                {
                    return Result<Dictionary<Guid, EventSeat>>
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.70]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_ForwardsCancellationTokenToEveryLockAndMirrorCall [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_ForwardsCancellationTokenToEveryLockAndMirrorCall [35 ms]
      Error Message:
       Expected fixture.EventSeatRepository.LastGetForUpdateToken to be System.Threading.CancellationToken
    {
        CanBeCanceled = True,
        IsCancellationRequested = False,
        WaitHandle = System.Threading.ManualResetEvent
        {
            Handle = 1160,
            SafeWaitHandle = Microsoft.Win32.SafeHandles.SafeWaitHandle
            {
                IsClosed = False,
                IsInvalid = False
            }
        }
    }, but found System.Threading.CancellationToken
    {
        CanBeCanceled = False,
        IsCancellationRequested = False,
        WaitHandle = System.Threading.ManualResetEvent
        {
            Handle = 1164,
            SafeWaitHandle = Microsoft.Win32.SafeHandles.SafeWaitHandle
            {
                IsClosed = False,
                IsInvalid = False
            }
        }
    }.
      Stack Trace:
         at FluentAssertions.Primitives.ObjectAssertions`2.Be(TSubject expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_ForwardsCancellationTokenToEveryLockAndMirrorCall() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1934
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     1, Passed:   130, Skipped:     0, Total:   131, Duration: 801 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    
    【m4.5】 計數票種鎖 GetForUpdateAsync 改傳 CancellationToken.None --from 原文：
                var lockedTicketTypes = await _ticketTypeRepository.GetForUpdateAsync(countingTicketTypeIds, cancellationToken);
    【m4.5】 計數票種鎖 GetForUpdateAsync 改傳 CancellationToken.None --to 原文：
                var lockedTicketTypes = await _ticketTypeRepository.GetForUpdateAsync(countingTicketTypeIds, CancellationToken.None);
    【m4.5】 計數票種鎖 GetForUpdateAsync 改傳 CancellationToken.None 完整 mutate 輸出：
    === m4.5
    mutation:             var lockedTicketTypes = await _ticketTypeRepository.GetForUpdateAsync(countingTicketTypeIds, cancellationToken); ->             var lockedTicketTypes = await _ticketTypeRepository.GetForUpdateAsync(countingTicketTypeIds, CancellationToken.None);
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.69]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_ForwardsCancellationTokenToEveryLockAndMirrorCall [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_ForwardsCancellationTokenToEveryLockAndMirrorCall [31 ms]
      Error Message:
       Expected fixture.TicketTypeRepository.LastGetForUpdateToken to be System.Threading.CancellationToken
    {
        CanBeCanceled = True,
        IsCancellationRequested = False,
        WaitHandle = System.Threading.ManualResetEvent
        {
            Handle = 1160,
            SafeWaitHandle = Microsoft.Win32.SafeHandles.SafeWaitHandle
            {
                IsClosed = False,
                IsInvalid = False
            }
        }
    }, but found System.Threading.CancellationToken
    {
        CanBeCanceled = False,
        IsCancellationRequested = False,
        WaitHandle = System.Threading.ManualResetEvent
        {
            Handle = 1164,
            SafeWaitHandle = Microsoft.Win32.SafeHandles.SafeWaitHandle
            {
                IsClosed = False,
                IsInvalid = False
            }
        }
    }.
      Stack Trace:
         at FluentAssertions.Primitives.ObjectAssertions`2.Be(TSubject expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_ForwardsCancellationTokenToEveryLockAndMirrorCall() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1935
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     1, Passed:   130, Skipped:     0, Total:   131, Duration: 817 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    
    【m4.6】 SyncCompletionAsync 改傳 CancellationToken.None --from 原文：
                await _admissionMirror.SyncCompletionAsync(sync.LockedEventId, sync.QueueEntry.Id, cancellationToken);
    【m4.6】 SyncCompletionAsync 改傳 CancellationToken.None --to 原文：
                await _admissionMirror.SyncCompletionAsync(sync.LockedEventId, sync.QueueEntry.Id, CancellationToken.None);
    【m4.6】 SyncCompletionAsync 改傳 CancellationToken.None 完整 mutate 輸出：
    === m4.6
    mutation:             await _admissionMirror.SyncCompletionAsync(sync.LockedEventId, sync.QueueEntry.Id, cancellationToken); ->             await _admissionMirror.SyncCompletionAsync(sync.LockedEventId, sync.QueueEntry.Id, CancellationToken.None);
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
      Determining projects to restore...
      All projects are up-to-date for restore.
      ProjectC.Domain -> /src/src/ProjectC.Domain/bin/Debug/net10.0/ProjectC.Domain.dll
      ProjectC.Application -> /src/src/ProjectC.Application/bin/Debug/net10.0/ProjectC.Application.dll
      ProjectC.Application.Tests -> /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll
    Test run for /src/tests/ProjectC.Application.Tests/bin/Debug/net10.0/ProjectC.Application.Tests.dll (.NETCoreApp,Version=v10.0)
    A total of 1 test files matched the specified pattern.
    [xUnit.net 00:00:00.66]     ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_ForwardsCancellationTokenToEveryLockAndMirrorCall [FAIL]
      Failed ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_ForwardsCancellationTokenToEveryLockAndMirrorCall [29 ms]
      Error Message:
       Expected fixture.AdmissionMirror.LastSyncCompletionToken to be System.Threading.CancellationToken
    {
        CanBeCanceled = True,
        IsCancellationRequested = False,
        WaitHandle = System.Threading.ManualResetEvent
        {
            Handle = 1160,
            SafeWaitHandle = Microsoft.Win32.SafeHandles.SafeWaitHandle
            {
                IsClosed = False,
                IsInvalid = False
            }
        }
    }, but found System.Threading.CancellationToken
    {
        CanBeCanceled = False,
        IsCancellationRequested = False,
        WaitHandle = System.Threading.ManualResetEvent
        {
            Handle = 1164,
            SafeWaitHandle = Microsoft.Win32.SafeHandles.SafeWaitHandle
            {
                IsClosed = False,
                IsInvalid = False
            }
        }
    }.
      Stack Trace:
         at FluentAssertions.Primitives.ObjectAssertions`2.Be(TSubject expected, String because, Object[] becauseArgs)
       at ProjectC.Application.Tests.Orders.OrderServiceTests.PlaceOrderAsync_WhenQueueModeAdmittedWithSeatAndCountingItems_ForwardsCancellationTokenToEveryLockAndMirrorCall() in /src/tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs:line 1936
    --- End of stack trace from previous location ---
    
    Failed!  - Failed:     1, Passed:   130, Skipped:     0, Total:   131, Duration: 775 ms - ProjectC.Application.Tests.dll (net10.0)
    結果：測試如預期失敗
    exit=0
    ```
  - 3.8 全部 mutation 還原後驗證（原文）：
    ```
    $ touch src/ProjectC.Application/Orders/OrderService.cs
    $ docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter FullyQualifiedName~OrderServiceTests
    Passed!  - Failed:     0, Passed:   131, Skipped:     0, Total:   131, Duration: 776 ms - ProjectC.Application.Tests.dll (net10.0)
    $ grep -cE "CancellationToken.None\)|QueueEntry.Id, sync.LockedEventId" src/ProjectC.Application/Orders/OrderService.cs
    0
    $ git ls-files --eol src/ProjectC.Application/Orders/OrderService.cs
    i/lf    w/crlf  attr/                 	src/ProjectC.Application/Orders/OrderService.cs
    ```

## 4. 文件

- [x] 4.1 更新 `docs/project-scope.md`「已知技術債：下單核心方法複雜度」條目：標示已由 `place-order-core-complexity` 處理（日期、結果：複雜度 48 → 實測最大值、`CA1502` 已消失），其餘敘述不動；`ReconcileEventQueueAsync` 條目不動。驗證：`git diff -- docs/project-scope.md` 僅該條目。
  - 完成紀錄：docs/project-scope.md 第 208 行標頭加註「已處理 2026-11-06，place-order-core-complexity：複雜度 48 → 實測最大值 20，CA1502 已消失」，其餘敘述與 ReconcileEventQueueAsync 條目不動；`git diff --stat -- docs`：1 file changed, 1 insertion(+), 1 deletion(-)。
  - TDD：N/A（純文件）。

## 5. 完整驗證與審查前檢查

- [x] 5.1 全套回歸：`docker compose exec api dotnet test` 各測試專案，與 1.3 基準逐專案比對；與 1.3 基準（已含 1.2 新增的案例）相比，Application.Tests 的總數只增加第 3 節回頭補的案例數（沒有補則相同），其他專案總數不變。偶發失敗須單獨重跑並貼出兩次結果。驗證：各 DLL 摘要行原文。
  - 完成紀錄：補強 3.8 的 2 個測試後重跑最終程式碼 `docker compose exec -T api dotnet test`，以 `grep -E 'Passed!|Failed!|^exit='` 過濾的每個測試專案摘要原文；與 1.3 基準相比僅 Application.Tests 547 → 549（+2 即 3.8 補強案例），其餘專案相同，Failed 0、Skipped 0。同次 `dotnet build --no-incremental` 以 grep 過濾的原文如下（含 CA1502 比對，無命中）。
    ```
    $ docker compose exec -T api dotnet build --no-incremental   # grep -E "warning |Warning(s)|Error(s)|CA1502|^exit="
    /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs(411,24): warning S3776: Refactor this method to reduce its Cognitive Complexity from 28 to the 15 allowed. (https://rules.sonarsource.com/csharp/RSPEC-3776) [/src/src/ProjectC.WebApi/ProjectC.WebApi.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: Found conflicts between different versions of "Microsoft.EntityFrameworkCore.Relational" that could not be resolved. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: There was a conflict between "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was chosen because it was primary and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was not. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" [/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll]. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on or have been unified to "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" []. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: Found conflicts between different versions of "Microsoft.EntityFrameworkCore.Relational" that could not be resolved. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: There was a conflict between "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was chosen because it was primary and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was not. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" [/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll]. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on or have been unified to "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" []. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /src/src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs(411,24): warning S3776: Refactor this method to reduce its Cognitive Complexity from 28 to the 15 allowed. (https://rules.sonarsource.com/csharp/RSPEC-3776) [/src/src/ProjectC.WebApi/ProjectC.WebApi.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: Found conflicts between different versions of "Microsoft.EntityFrameworkCore.Relational" that could not be resolved. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: There was a conflict between "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was chosen because it was primary and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was not. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" [/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll]. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on or have been unified to "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" []. [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll". [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.WebApi.Tests/ProjectC.WebApi.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: Found conflicts between different versions of "Microsoft.EntityFrameworkCore.Relational" that could not be resolved. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277: There was a conflict between "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was chosen because it was primary and "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" was not. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on "Microsoft.EntityFrameworkCore.Relational, Version=10.0.4.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" [/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll]. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /root/.nuget/packages/microsoft.entityframeworkcore.relational/10.0.4/lib/net10.0/Microsoft.EntityFrameworkCore.Relational.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:     References which depend on or have been unified to "Microsoft.EntityFrameworkCore.Relational, Version=10.0.11.0, Culture=neutral, PublicKeyToken=adb9793829ddae60" []. [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.Infrastructure/bin/Debug/net10.0/ProjectC.Infrastructure.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:         /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:           Project file item includes which caused reference "/src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll". [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
    /usr/share/dotnet/sdk/10.0.401/Microsoft.Common.CurrentVersion.targets(2453,5): warning MSB3277:             /src/src/ProjectC.WebApi/bin/Debug/net10.0/ProjectC.WebApi.dll [/src/tests/ProjectC.Infrastructure.Tests/ProjectC.Infrastructure.Tests.csproj]
        3 Warning(s)
        0 Error(s)
    exit=0
    ```
    ```
    $ docker compose exec -T api dotnet test
    Passed!  - Failed:     0, Passed:   151, Skipped:     0, Total:   151, Duration: 109 ms - ProjectC.Domain.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   549, Skipped:     0, Total:   549, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 9 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: 53 s - ProjectC.Infrastructure.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   676, Skipped:     0, Total:   676, Duration: 2 m 53 s - ProjectC.WebApi.Tests.dll (net10.0)
    exit=0
    ```
- [x] 5.2 hardener 最終檢查：`OrderService` 屬 I/O 與交易管理程式碼，適用 `.claude/skills/hardener/SKILL.md` 清單；因本 change 不改行為，預期是核對既有防禦分支未在搬移中遺失（參數驗證、Entity 存在性、`CancellationToken` 傳遞到每個新方法的 I/O 呼叫、例外邊界、日誌遮蔽）。若發現缺口，依 TDD 新增「hardener 補強」task。驗證：檢查清單逐項結果。
  - 完成紀錄：hardener 清單核對：參數驗證（FluentValidation 留協調者）、Entity 存在性（各 NotFound 分支與訊息由★測試逐字守護）、CancellationToken 已傳入每個新方法的每個 I/O 呼叫、例外邊界（IsRealNameRequired 不變量例外原樣丟出）、日誌遮蔽未變。未發現缺口，程式碼無因此修改。
- [x] 5.3 `graphify update .` 更新知識圖。驗證：命令成功結束。
  - 完成紀錄：`graphify update .` → Code graph updated.

## Workflow follow-up

- 依 CLAUDE.md 流程：strict-reviewer 與全新對抗式 subagent 依序審查（呼叫 strict-reviewer 時須把 1.2、第 3 節與 5.1 的證據完整附在 prompt），修到兩者皆無 blocking。
- 審查通過後由使用者明確指示才 `/opsx:archive`（`skip_specs`）並 merge 回 master；任何步驟都不自動 commit／push。
