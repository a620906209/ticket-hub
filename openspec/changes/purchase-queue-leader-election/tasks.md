## 1. Application 層 — 分散式鎖介面

- [ ] 1.1 新增 `IDistributedLock` 介面於 `src/ProjectC.Application/Common/Interfaces/`（比照 `IDateTimeProvider` 放置位置；此放置為 design.md 決策 5「經核准例外」下的明確決定，**非**對 CLAUDE.md「外部服務介面一律定義在 Domain」規則的私自偏離——理由：純技術性基礎設施關注點，不承載業務語意，不放 `Domain`；不得依此案例類推放寬其他外部服務介面的放置規則）：
  - `Task<LockAcquisitionResult> TryAcquireAsync(string key, TimeSpan ttl, CancellationToken)`：回傳三態結果（見 1.2）
  - `Task ReleaseAsync(string key, string ownerToken, CancellationToken)`：僅在目前持有者的 `ownerToken` 相符時才實際釋放（見 design.md 決策 2）
- [ ] 1.2 新增 `LockAcquisitionResult`（record，含 `LockResult`（`Acquired` / `HeldByOther` / `RedisUnavailable` 三態列舉）與 `OwnerToken`（僅 `Acquired` 時有值，供後續 `ReleaseAsync` 使用））

## 2. Infrastructure 層 — Redis 分散式鎖實作

- [ ] 2.1 新增 NuGet 套件 `StackExchange.Redis` 至 `ProjectC.Infrastructure.csproj`
- [ ] 2.2 新增 `RedisDistributedLock`（實作 `IDistributedLock`），注入 `IConnectionMultiplexer`：
  - `TryAcquireAsync`：`IDatabase.StringSetAsync(key, ownerToken, ttl, When.NotExists)`；產生新的 `Guid` 作為 `ownerToken`；`StringSetAsync` 回傳 `false` 時視為 `HeldByOther`；捕捉 Redis 連線例外（`RedisConnectionException`／`RedisTimeoutException`）時記錄 `LogWarning` 並回傳 `RedisUnavailable`（見 design.md 決策 4，MUST NOT 讓例外往外拋、MUST NOT 讓呼叫端因此中斷本輪推進）
  - `ReleaseAsync`：以 Lua script 執行 compare-and-delete（`if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end`，見 design.md 決策 2），MUST NOT 用「先 GET 再 DEL」兩個指令；連線例外時 MUST 記錄 `LogWarning`（將釋放失敗視為可觀察的降級結果，不是靜默失敗——不影響本輪推進已完成的結果，TTL 到期後仍會自動釋放，見 design.md 決策 3），不得只是空 catch 或不記錄理由
- [ ] 2.3 `Program.cs` 註冊 `IConnectionMultiplexer` 為 Singleton（`ConnectionMultiplexer.Connect(...)`，連線字串取自 `ConnectionStrings:Redis`，**MUST** 透過 `ConfigurationOptions` 明確設定 `AbortOnConnectFail = false`——`StackExchange.Redis` 預設值為 `true`，Redis 尚未就緒時 `Connect(...)` 會直接拋出例外並阻塞應用程式啟動，違反 design.md 決策 4／Migration Plan 的 fail-open 原則，見 spec.md PQLE-010），註冊 `IDistributedLock` → `RedisDistributedLock` 為 Singleton（`IConnectionMultiplexer` 官方建議整個應用程式共用單一實例，比照本身即是 thread-safe 的既有 Singleton 註冊慣例，如 `IMemoryCache`）
- [ ] 2.4 新增 `DistributedLockOptions`（`LockTtlMultiplier`，`int`，DataAnnotations 標註須為正整數，預設 `3`，見 design.md Migration Plan），註冊方式比照 `RateLimitingOptions`（`AddOptions<T>().Bind(...).ValidateDataAnnotations()`，不鏈 `ValidateOnStart()`，有安全預設值）

## 3. WebApi 層 — 背景服務整合

- [ ] 3.1 `PurchaseQueueAdmissionService` 建構子新增注入 `IDistributedLock`、`DistributedLockOptions`
- [ ] 3.2 新增 public 方法 `AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken)`（見 design.md 決策 6，**不修改既有 `AdvanceQueueOnceAsync`**，避免既有 `purchase-queue` 測試意外依賴 Redis）：
  1. 呼叫 `_distributedLock.TryAcquireAsync("purchase-queue-admission:lock", TimeSpan.FromSeconds(_options.PollingIntervalSeconds * _lockOptions.LockTtlMultiplier), ct)`
  2. `Acquired` 或 `RedisUnavailable`：呼叫既有 `AdvanceQueueOnceAsync(ct)`；`finally` 區塊僅在 `Acquired` 時呼叫 `ReleaseAsync`（`RedisUnavailable` 代表本來就沒有真的鎖，不需釋放）
  3. `HeldByOther`：記錄 Debug 等級 log，直接跳過，不呼叫 `AdvanceQueueOnceAsync`
- [ ] 3.3 `ExecuteAsync` 每輪輪詢改為呼叫 `AdvanceQueueOnceWithLeaderElectionAsync(stoppingToken)`（取代原本直接呼叫 `AdvanceQueueOnceCoreAsync`）；既有的 `catch (OperationCanceledException)` / `catch (Exception)` 例外處理結構與 TraceId scope 維持包住整個新方法呼叫，不變更既有錯誤處理與 `Task.Delay` 節奏

## 4. 部署設定

- [ ] 4.1 `docker-compose.yml` 新增 `redis` 服務（`image: redis:7-alpine`，不掛載 volume——鎖為過程性資料，容器重建可清空，比照 `seq` 服務的既定取捨；不對外映射 port，只有 `api` 透過 compose 網路內部連線）
- [ ] 4.2 `api` 服務新增環境變數 `ConnectionStrings__Redis: "redis:6379"`（compose service name，禁止 `localhost`）；`depends_on` 新增 `redis: condition: service_started`（比照 `seq` 的既定寫法，Redis 連線失敗不得阻塞 API 啟動，見 design.md Migration Plan）
- [ ] 4.3 `appsettings.json`／`appsettings.Development.json` 補上 `DistributedLock:LockTtlMultiplier` 起始值 `3`

## 5. 後端測試

- [ ] 5.1 單元測試：`AdvanceQueueOnceWithLeaderElectionAsync` 對 `IDistributedLock` 三態回傳的分支行為（用假的 `IDistributedLock` 實作或 mock，不連真實 Redis）——`Acquired` 執行推進並釋放、`HeldByOther` 跳過不執行、`RedisUnavailable` 執行推進但不釋放
- [ ] 5.2 整合測試（Testcontainers 起真實 Redis 容器，比照既有 `PostgresFixture` 模式新增 `RedisFixture`）：`RedisDistributedLock` 元件層（不經過 `PurchaseQueueAdmissionService`，直接測 `TryAcquireAsync`／`ReleaseAsync`，驗證鎖原語本身正確）
  - PQLE-001（元件層子項）：單一呼叫端成功取得鎖
  - PQLE-002／PQLE-003（元件層子項）：兩個獨立的 `RedisDistributedLock` 實例（模擬兩個應用程式實例）以 `Task.WhenAll` 同時嘗試取得同一個 key，驗證僅一個回傳 `Acquired`、另一個回傳 `HeldByOther`；`HeldByOther` 的那一方於鎖釋放後重新嘗試可成功取得（此為鎖原語本身可重新取得的驗證，服務層「下一輪會重新嘗試」的驗證見 5.3）
  - PQLE-004：正常釋放後，同一 key 可被同一或其他呼叫端立即重新取得
  - PQLE-005：設定極短 TTL，不呼叫釋放，等待 TTL 到期後驗證其他呼叫端可重新取得
  - PQLE-006：呼叫端 A 取得鎖後，人為讓其 TTL 到期、呼叫端 B 取得新鎖，此時呼叫端 A 才呼叫 `ReleaseAsync`（帶自己過期前的 `ownerToken`），驗證此次釋放為無操作、呼叫端 B 持有的鎖不受影響（`GET` 該 key 驗證值仍是 B 的 `ownerToken`）
  - PQLE-007（元件層子項）／009（元件層子項）：以 Testcontainers 容器啟動後立即 `Dispose` 或阻斷連線模擬 Redis 不可用，驗證 `TryAcquireAsync` 回傳 `RedisUnavailable` 且不拋例外，並驗證對應的 `LogWarning` 確實被記錄（例如以 in-memory logger provider 或 `ILogger` mock 斷言呼叫，見 spec.md PQLE-007 的日誌要求）；恢復連線後驗證回到正常互斥行為（此為鎖原語本身的恢復驗證，服務層「不需重啟即可恢復正常互斥」的驗證見 5.3）
- [ ] 5.3 整合測試：`PurchaseQueueAdmissionService` 端到端，**MUST 呼叫新增的 `AdvanceQueueOnceWithLeaderElectionAsync`（見 design.md 決策 6／tasks.md 3.2），不得呼叫既有的 `AdvanceQueueOnceAsync`——後者不含取鎖邏輯，呼叫它無法測到任何鎖相關行為**（比照既有 `PurchaseQueueAdmissionServiceTests.cs` 手法）：
  - PQLE-001 全流程：單一服務實例共用一個真實 Redis，呼叫 `AdvanceQueueOnceWithLeaderElectionAsync`，驗證（1）分散式鎖確實被取得（呼叫前後可觀察 Redis 該 key 存在→不存在的變化，或以 spy `IDistributedLock` 斷言 `TryAcquireAsync`／`ReleaseAsync` 皆被呼叫恰一次）、（2）`AdvanceQueueOnceCoreAsync` 的推進邏輯確實執行（資料庫查詢/推進有發生）、（3）執行完畢後鎖已被釋放（該 key 在 Redis 中已不存在，供下一輪或其他實例立即取得）
  - PQLE-002：建立兩個服務實例（兩個獨立的 `PurchaseQueueAdmissionService`，各自的 `IDistributedLock` 皆指向同一個真實 Redis）共用同一個真實 Redis，`Task.WhenAll` 同時呼叫兩者的 `AdvanceQueueOnceWithLeaderElectionAsync`，驗證只有一個實例的呼叫實際執行了資料庫查詢/推進（可用 spy repository 或計數斷言），另一個直接返回且未執行推進；驗證既有 `purchase-queue` 能力的 PQ-ADMIT 系列行為不受影響（正確性不變）
  - **PQLE-003（服務層，本次新增，補上先前只驗證鎖原語、未驗證服務行為的缺口）**：分三個明確階段，**MUST 用可控制的同步機制（例如 spy repository 的 `GetAllAsync` 內部以 `TaskCompletionSource` 卡住，等測試主動釋放才返回）讓實例 A 的推進邏輯確定停留在「已取得鎖、尚未完成」的狀態，不得依賴時序巧合**：
    1. **第一輪（重疊）**：啟動實例 A 呼叫 `AdvanceQueueOnceWithLeaderElectionAsync`（A 的推進邏輯卡在同步點，鎖確定仍被 A 持有、尚未釋放）；此時啟動實例 B 呼叫同一方法，驗證 B 得到 `HeldByOther` 而跳過（斷言可用 spy repository 驗證 B 這次呼叫沒有觸發任何資料庫查詢）
    2. **釋放**：釋放 A 的同步點，讓 A 的呼叫完成推進邏輯並釋放鎖（等待 A 的 `Task` 完成）
    3. **下一輪**：確認鎖已釋放後（例如查 Redis 該 key 已不存在），**實例 B 再次呼叫 `AdvanceQueueOnceWithLeaderElectionAsync`**（代表下一輪輪詢），驗證這次 B 成功取得鎖並執行推進邏輯（資料庫查詢/推進確實發生）——這是驗證 `PurchaseQueueAdmissionService` 本身（而非鎖元件）在上一輪跳過後，下一輪會重新呼叫 `TryAcquireAsync`，不會因為上一輪失敗就永久放棄
  - **PQLE-009（服務層，本次新增，補上先前只驗證鎖原語恢復、未驗證服務行為的缺口）**：建立兩個服務實例共用同一個真實 Redis；先模擬 Redis 不可用（阻斷連線或關閉容器），兩個實例的 `AdvanceQueueOnceWithLeaderElectionAsync` 皆走 `RedisUnavailable` 分支各自執行推進；接著恢復 Redis 連線（不重啟任何服務實例、不重新建構物件）；等待前一輪兩個實例的呼叫皆已完成後，`Task.WhenAll` 兩個實例再次呼叫 `AdvanceQueueOnceWithLeaderElectionAsync`（代表下一輪輪詢），驗證此時恢復正常互斥行為——僅一個實例的呼叫實際執行了推進，另一個回傳 `HeldByOther` 並跳過，證明服務不需要重啟或任何手動介入即可恢復正常協調
  - **PQLE-007（服務層，本次新增，補上先前 5.1／5.2 皆未覆蓋的缺口——5.1 用假的 `IDistributedLock` 不觸發真實 Redis 故障，5.2 明確不經過 `PurchaseQueueAdmissionService`）**：單一服務實例搭配真實 Redis（以 Testcontainers 啟動後立即關閉容器或阻斷連線，模擬連線失敗），呼叫 `AdvanceQueueOnceWithLeaderElectionAsync`，驗證：（1）`TryAcquireAsync` 回傳 `RedisUnavailable`、（2）對應的 `LogWarning` 確實被記錄（斷言方式同 5.2 PQLE-007 元件層）、（3）`AdvanceQueueOnceCoreAsync` 的推進邏輯確實完整執行（資料庫查詢/推進有發生，非跳過）、（4）`ReleaseAsync` 未被呼叫（`RedisUnavailable` 代表本來就沒有真的鎖，不應嘗試釋放）
- [ ] 5.4 整合測試：重疊執行下的正確性（涵蓋 PQLE-006a 與 PQLE-008 兩種不同觸發成因，共用同一組核心斷言：兩個服務實例對同一活動的推進邏輯真的同時執行時，資料庫層是否仍保證不超額入場）——**同樣 MUST 透過 `AdvanceQueueOnceWithLeaderElectionAsync` 觸發**，預先設定一個 `MaxConcurrentAdmittedBuyers = 1` 的活動，並建立多筆 `Waiting` 紀錄：
  - PQLE-006a 觸發面（TTL 到期但原持有者仍在執行中）：以人為延遲（例如在 `AdvanceEventQueueAsync` 執行路徑中插入可控制的延遲點，或對實例 A 這次 `TryAcquireAsync` 呼叫設定極短 TTL 以模擬到期——TTL 是該次取鎖呼叫的參數，不是綁定特定實例的屬性），讓兩個服務實例的 `AdvanceQueueOnceWithLeaderElectionAsync` 呼叫出現時間重疊，`Task.WhenAll` 等待兩者完成後，查詢資料庫驗證有效入場人數（`Status = Admitted AND AdmissionExpiresAtUtc > now`）不超過上限 `1`
  - PQLE-008 觸發面（Redis 不可用）：模擬 Redis 不可用（阻斷連線或關閉 Testcontainers 容器），兩個服務實例各自的 `AdvanceQueueOnceWithLeaderElectionAsync` 皆因 `RedisUnavailable` 分支照常執行推進邏輯，`Task.WhenAll` 兩者的呼叫，驗證兩者皆確實執行了推進邏輯（非其中一個被跳過），且查詢資料庫驗證有效入場人數不超過上限 `1`（呼應既有 `purchase-queue` 能力 PQ-ADMIT-004「併發推進不超額入場」的資料庫層保證延伸適用於本情境）

- [ ] 5.5 整合測試：應用程式啟動時 Redis 不可用（對應 spec.md PQLE-010，**範圍僅限「Host 啟動不被阻塞」，不涉及背景服務執行**——`Testing` 環境依既有慣例不註冊 `PurchaseQueueAdmissionService`（見 design.md 第 12 行），若在同一測試內斷言背景服務首輪輪詢會與此慣例衝突，故本任務不驗證輪詢行為；`AdvanceQueueOnceWithLeaderElectionAsync` 在 Redis 不可用時的 fail-open 執行行為已由 5.1／5.3（PQLE-007）涵蓋，不重複測試）：
  - **MUST** 指向實際不可達的 Redis endpoint（例如以 Testcontainers 啟動 Redis 容器後立即關閉/不啟動，取得一個真實但無法連線的 `host:port`），透過 `WebApplicationFactory.WithWebHostBuilder` 的 `ConfigureAppConfiguration` 覆寫 `ConnectionStrings:Redis` 指向該 endpoint——**MUST NOT** 以 `WithWebHostBuilder` 的 `ConfigureTestServices` 將 `IConnectionMultiplexer`／`IDistributedLock` 替換為 mock 或移除其註冊，否則等同沒有真的觸發 `Program.cs` 的連線建立路徑，無法證明 PQLE-010
  - `Testing` 環境保留正式的 Redis client 註冊（即 `Program.cs` 2.3 的 `ConnectionMultiplexer.Connect(...)` 仍會實際執行），僅背景服務依既有慣例不註冊，兩者互不影響
  - 驗證啟動流程成功完成：Host 建置與 `StartAsync` 不拋出例外、正常進入 Running 狀態（可用 `WebApplicationFactory.CreateClient()` 成功發出一次請求佐證 Host 已就緒）
  - 驗證連線初期確實失敗（而非「根本沒有嘗試連線」）：斷言已解析出的 `IConnectionMultiplexer.IsConnected == false`，或斷言 Redis 連線失敗的 `LogWarning`／`RedisConnectionException` 已被記錄／捕捉（斷言方式同既有 `ILogger` mock／in-memory provider 手法，見 5.2）

- [ ] 5.6 整合測試：`ExecuteAsync` 確實委派至 `AdvanceQueueOnceWithLeaderElectionAsync`（驗證 tasks.md 3.3 的委派關係本身——5.3 只直接呼叫新方法，無法證明正式輪詢迴圈真的改為呼叫它，若實作只新增方法卻忘記改 `ExecuteAsync`，5.1~5.4 仍可能全數通過但正式輪詢完全不會經過分散式鎖）：透過 `IHostedService.StartAsync` 啟動真正的 `PurchaseQueueAdmissionService`（不得直接呼叫 private `ExecuteAsync` 或既有的 `AdvanceQueueOnceAsync`／`AdvanceQueueOnceWithLeaderElectionAsync`），注入 spy `IDistributedLock`，設定極短 `PollingIntervalSeconds`：
  1. 等待服務跑滿至少一輪輪詢，驗證 spy 的 `TryAcquireAsync` 確實被呼叫過（證明 `ExecuteAsync` 已改為呼叫 `AdvanceQueueOnceWithLeaderElectionAsync`，而非仍呼叫舊的 `AdvanceQueueOnceCoreAsync`／`AdvanceQueueOnceAsync`）
  2. 令 spy 回傳 `HeldByOther`，驗證該輪未觸發任何資料庫推進查詢（spy repository 呼叫次數為 0）
  3. 令 spy 回傳 `Acquired`，驗證該輪確實執行推進且 `ReleaseAsync` 被呼叫
  4. 令 spy 回傳 `RedisUnavailable`，驗證該輪確實執行推進但不呼叫 `ReleaseAsync`
  5. 呼叫 `StopAsync` 觸發取消，驗證服務可在合理時間內正常停止（不掛住），既有的 `catch (OperationCanceledException)` 例外處理與 `Task.Delay` 節奏未被破壞

## 6. Spec 同步確認

- [ ] 6.1 實作完成後比對本次 `openspec/changes/purchase-queue-leader-election/specs/` 與最終實作行為是否一致，如有偏差回報並更新 spec
- [ ] 6.2 更新 `docs/project-scope.md` 第 51 節，將「Redis 分散式鎖 / Queue 排隊室」的 Leader Election 部分標記為已完成並附上 archive 連結（Queue 排隊室的 Redis 資料結構重寫維持未排定，不在本次範圍）

## 7. AC ↔ Test Traceability

> 每一列對應 spec 中一個 `#### Scenario:` 標題的 AC ID。「Test task」欄位指向本文件第 5 節的測試任務編號。

### purchase-queue-leader-election（新能力）

| AC ID | Requirement | Scenario | Test task |
|---|---|---|---|
| PQLE-001 | 背景推進服務的多實例互斥執行 | 單一實例取得鎖並執行推進 | 5.2／5.3／5.6（`ExecuteAsync` 委派驗證） |
| PQLE-002 | 背景推進服務的多實例互斥執行 | 多實例同時輪詢，僅一個實例執行本輪推進 | 5.2／5.3／5.6（`ExecuteAsync` 委派驗證） |
| PQLE-003 | 背景推進服務的多實例互斥執行 | 未取得鎖的實例不影響下一輪重新競爭 | 5.2（鎖原語）／5.3（服務層）／5.6（`ExecuteAsync` 委派驗證） |
| PQLE-004 | 分散式鎖的租約與逾時自動釋放 | 正常完成後主動釋放鎖 | 5.2 |
| PQLE-005 | 分散式鎖的租約與逾時自動釋放 | 持有鎖的實例未能主動釋放，TTL 到期後鎖自動可用 | 5.2 |
| PQLE-006 | 分散式鎖的租約與逾時自動釋放 | 已逾時釋放的鎖不可被原持有者誤釋放新的持有者 | 5.2 |
| PQLE-006a | 分散式鎖的租約與逾時自動釋放 | 原持有者仍在執行中、TTL 到期，另一實例取得鎖並重疊執行 | 5.4 |
| PQLE-007 | Redis 不可用時的降級行為 | Redis 無法連線時仍執行本輪推進 | 5.1（分支單元測試）／5.3（服務層完整驗證）／5.4（多實例情境下維持正確性，即 PQLE-008） |
| PQLE-008 | Redis 不可用時的降級行為 | Redis 故障期間多實例重複執行仍維持正確性 | 5.4 |
| PQLE-009 | Redis 不可用時的降級行為 | Redis 恢復連線後回復正常互斥行為 | 5.2（鎖原語）／5.3（服務層） |
| PQLE-010 | Redis 不可用時的降級行為 | 應用程式啟動時 Redis 不可用，API 仍可正常啟動 | 5.5（Host 啟動）／5.1／5.3（fail-open 執行行為，即 PQLE-007） |
