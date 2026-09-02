## Context

`PurchaseQueueAdmissionService`（`src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs`）以 `BackgroundService` 實作週期性輪詢：每輪先在交易外快速掃描出 `IsQueueModeEnabled = true` 的活動 Id 清單，再逐一以 DB 悲觀鎖（`IEventRepository.GetForUpdateAsync`）鎖定並處理每個活動。這個設計原本就假設「只有一份執行個體」——`rate-limiting-queue` 的 design.md 在 Non-Goals／Risks 明確記錄了這個假設（「不做跨機器多實例協調優化」「目前假設單一 api container」），並將其列為「未來擴充時參考」的已知風險，非本次之前就打算解決。

`docs/project-scope.md` 第 51 節將「Redis 分散式鎖 / Queue 排隊室」列為 Phase 3 Could，本 change 是該項目排定實作的第一步，聚焦於背景推進服務的多實例協調（leader election），不涉及座位鎖定機制本身的變更。

**現行部署現況的誠實說明**：`docker-compose.yml` 目前的 `api` 服務只有一個 replica，本機開發/展示環境並未真正水平擴展。這次改動的價值是「補上正確的多實例協調機制、消除文件已記錄的已知技術債」，同時作為履歷/面試情境展示分散式鎖設計能力的技術深化項目——不是在修一個當下環境正在發生的正確性 bug（現況下正確性本來就沒問題，只是效率有浪費）。

專案現況（相關既有慣例，本設計沿用）：
- Options 綁定慣例：有安全預設值的設定用 `Configure<T>` + 解包；必要設定用 `AddOptions<T>().Bind(...).ValidateDataAnnotations().ValidateOnStart()`（`JwtOptions`／`TicketSigningOptions`／`PurchaseQueueOptions` 模式）
- 輔助基礎設施故障不得阻斷核心業務流程的既定原則：`observability` 能力明確要求「Seq sink 連線失敗不得影響應用程式啟動或請求處理」——本設計對 Redis 故障採相同精神
- 背景服務慣例：`AddHostedService`，`Testing` 環境不註冊，避免整合測試意外跑背景服務（`ExpiredOrderCleanupService`／`PurchaseQueueAdmissionService` 皆如此）
- 連線字串／服務位址一律用 compose service name，禁止 `localhost`

## Goals / Non-Goals

**Goals:**
- 多實例部署下，**在鎖的租約（TTL）尚未到期、且持有者正常運作的情況下**，`PurchaseQueueAdmissionService` 每一輪輪詢最多只有一個實例真正執行 `AdvanceQueueOnceCoreAsync`（掃描全部活動並推進），其餘實例該輪跳過——這是效率層面的目標，減少多實例重複執行；若持有者的執行時間超過 TTL，另一實例可能取得新鎖並與其重疊執行（見 Decision 3、spec.md PQLE-006a），此時「只有一個實例執行」不再成立，正確性改由既有 `purchase-queue` 能力的資料庫悲觀鎖保證，非本分散式鎖的保證範圍
- Redis 不可用時，系統 MUST 有明確、可觀察的降級行為，不得讓背景服務停止運作或應用程式無法啟動
- 不改變 `purchase-queue` 既有對外可觀察行為（API 回應、入場順序、逾時判斷皆不變）

**Non-Goals:**
- 不引入 Redlock 多節點演算法（需要多個獨立 Redis 節點才有意義）；本專案只部署單一 Redis 實例，用單節點 `SET NX PX` 已足夠達成本次目的，屬於「基礎」等級的分散式鎖，非金融級強一致性鎖
- 不將 `seat-reservation` 的座位鎖定改為 Redis 鎖——現有 DB 悲觀鎖已正確支援多實例，沒有問題要修（見 proposal.md「Why」）
- 不解決 `api-rate-limiting` 記憶體限流跨實例不共用計數的問題（獨立範疇，見 proposal.md Impact）
- 不實際把 `docker-compose.yml` 的 `api` 服務改成多 replica 常態部署；本次只確保機制正確，多實例驗證以整合測試涵蓋（見 Migration Plan／tasks）

## Decisions

**1. 鎖的粒度：單一全域鎖涵蓋整輪推進，不做逐活動的 Redis 鎖**
- 考慮過「比照 DB 悲觀鎖，對每個活動各自加一把 Redis 鎖」，但這會是重工：每個活動在 `AdvanceEventQueueAsync` 內本來就已經有 DB `GetForUpdateAsync` 保護正確性，逐活動加 Redis 鎖除了多一層網路往返，不會提升任何正確性保證
- 改為在 `AdvanceQueueOnceCoreAsync` 執行之前，先嘗試取得單一固定 key（例如 `purchase-queue-admission:lock`）的鎖；取得成功才執行整輪掃描與推進，取得失敗直接跳過本輪。這精確對應「leader election」的語意：贏得本輪 leadership 的實例才做事

**2. 鎖的實作方式：`StackExchange.Redis` 的 `SET key value NX PX ttl`，釋放時用 Lua script 做 compare-and-delete**
- 新增 NuGet 套件 `StackExchange.Redis`（.NET 生態系事實標準 Redis client）
- 取鎖：`IDatabase.StringSetAsync(key, ownerToken, ttl, When.NotExists)`，`ownerToken` 為每次嘗試產生的 `Guid`（區分「這是我持有的鎖」）
- 釋放：MUST 用 Lua script 做「先比對 value 是否等於自己的 ownerToken，相等才 DEL」的原子操作（`EVAL "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end"`），不可用「先 GET 再 DEL」兩個指令——後者在極端時序下（本次執行超時、TTL 已過期、另一個實例已取得新鎖）可能誤刪別人剛取得的鎖，這是 Redis 官方文件記載的分散式鎖釋放標準寫法
- 不使用現成的 `RedLock.net` 等第三方分散式鎖套件：本次只需單節點鎖，套件的多數功能（多節點 quorum、自動續租）用不到，直接用 `StackExchange.Redis` 的兩個指令即可完整實作，符合 CLAUDE.md 簡化原則（不為單一用途引入額外抽象層）

**3. 鎖的 TTL 與輪詢節奏的關係**
- TTL 設為 `PollingIntervalSeconds` 的固定倍數（例如 `PollingIntervalSeconds * 3`，透過設定值調整，不寫死），確保：
  - 正常情況下，持有鎖的實例會在自己那一輪執行完畢後主動釋放（Decision 2），下一輪由任一實例重新競爭，TTL 通常用不到
  - 若持有鎖的實例在執行中當掉（未能主動釋放），TTL 到期後鎖自動釋放，其他實例最多等待 TTL 時間即可恢復推進，不會永久卡住
- 不做「自動續租」（lock renewal / heartbeat）：`AdvanceQueueOnceCoreAsync` 是有限時間內會結束的一次性工作（掃描+逐活動處理），不是長時間持有的鎖，只要 TTL 抓得比「正常執行時間」寬裕（3 倍輪詢間隔），沒有續租的必要；續租機制只在鎖需要保護「執行時間不確定的長任務」時才有價值，本場景引入只會增加複雜度
- **TTL 到期時原持有者仍在執行中的重疊執行，是明確接受、不阻止的行為，非未定義狀態**：不做續租代表若某一輪推進真的耗時超過 TTL（原持有者未當掉，只是還沒做完），另一實例會在 TTL 到期後合法取得新鎖並開始執行，與原持有者的執行重疊。本設計刻意不引入「延長租約」或「拒絕重疊」機制去阻止這件事，因為要正確判斷是否要阻止，需要知道原持有者是否還活著（心跳／續租），這正是本次決定不做的複雜度；重疊執行的正確性完全外包給既有 `purchase-queue` 能力的資料庫悲觀鎖（不是本次新增的保證），本分散式鎖的唯一職責是「正常情況下減少重複執行的效率浪費」，不是「保證任何時刻都只有一個實例在執行」。此行為已在 spec.md 新增 Scenario PQLE-006a 明確定義並要求測試驗證（見 tasks.md 5.4）

**4. Redis 不可用時的降級行為：Fail-open（跳過取鎖、直接執行），並記錄警告 log**
- 呼應既有 `observability` 能力的原則：輔助基礎設施故障不得阻斷核心業務流程。若因為 Redis 不可用而讓每個實例的每一輪都取鎖失敗、直接跳過，結果是「Redis 掛掉期間，所有實例都不執行任何推進」——這比「沒有分散式鎖之前」更差（退化前好歹單實例仍能正常運作），對買家排隊入場這種時間敏感的業務流程是不可接受的降級
- 因此鎖服務的取鎖方法區分兩種失敗：「鎖已被別的實例持有」（正常競爭失敗，回傳「未取得」，本輪跳過）與「無法連線 Redis」（基礎設施故障，回傳「不確定」，此時 MUST 視為取得鎖、照常執行本輪推進，並記錄 `LogWarning`）
- 這個設計代表：Redis 故障期間，系統退化回「多實例各自重複執行」的原始狀態（正確性仍由 DB 悲觀鎖保證，只是恢復效率浪費），而不是「完全停止運作」——與新增 Redis 之前的行為一致，符合「新增的基礎設施只能讓情況變好或持平，不能讓情況變差」的原則
- 此 fail-open 原則同樣適用於**應用程式啟動階段**的 Redis 連線建立：`Program.cs` 呼叫 `ConnectionMultiplexer.Connect(...)` 時 MUST 透過 `ConfigurationOptions` 明確設定 `AbortOnConnectFail = false`——`StackExchange.Redis` 的預設值為 `true`，Redis 尚未就緒時 `Connect(...)` 會直接拋出 `RedisConnectionException` 並阻塞應用程式啟動，這與本決策「Redis 故障只能造成效率降級、不能讓服務完全停止」的原則矛盾，也違反 Migration Plan「Redis 連線失敗不得阻塞 API 啟動」的承諾（見 spec.md PQLE-010）

**5. 介面放置位置：`Application/Common/Interfaces`，比照 `IDateTimeProvider`，不放 `Domain`**
- CLAUDE.md 的既定規則是「外部服務介面一律定義在 Domain」，既有 `IPaymentGateway`（`Domain/Payments/`）、`IEmailNotificationService`（`Domain/Notifications/`）都遵循此規則——但這兩者代表的是**有業務語意的外部能力**（付款、通知），Domain Entity／Application 的業務流程需要透過它們表達業務規則（例如訂單付款成功、票券產出後通知買家）
- 分散式鎖不同：它不承載任何業務語意，純粹是「這段程式碼在多實例環境下需要互斥執行」的技術性基礎設施關注點，性質與 `IDateTimeProvider`（同樣是純技術性的環境抽象，放在 `Application/Common/Interfaces`，不在 Domain）一致，且唯一呼叫端是 WebApi 層的背景服務，不涉及 Domain／Application 的業務邏輯。故新增 `IDistributedLock` 介面於 `Application/Common/Interfaces`，實作 `RedisDistributedLock` 於 `Infrastructure`，比照 `IDateTimeProvider`／`SystemDateTimeProvider` 的既有放置模式

**經核准例外（對照 CLAUDE.md「外部服務介面一律定義在 Domain」的強制規則）**：CLAUDE.md 明文規定「Repository／外部服務介面一律定義在 Domain，實作放 Infrastructure」，`IDistributedLock` 字面上屬於外部服務介面，理應落在此規則管轄範圍。但比照專案既有 `IDateTimeProvider`（同樣是純技術性的環境抽象，非業務語意介面）的既定放置慣例，本 change 明確宣告核准以下例外：

> 本 change 經核准例外，不將 `IDistributedLock` 放入 `Domain`。
> 理由：`IDistributedLock` 不代表業務語意，也不會被 Domain Entity 或 Application 業務規則使用；它只由 WebApi 背景服務作為執行協調機制使用，性質與 `IDateTimeProvider` 一致。因此依專案架構規則中的例外判斷，介面放置於 `Application/Common/Interfaces`。此例外僅適用於本次技術性分散式鎖介面，不修改其他外部服務介面（如 `IPaymentGateway`／`IEmailNotificationService`）的既定規則——那些介面承載業務語意，仍須留在 `Domain`。

此例外不是實作者自行解讀的空間，`tasks.md` 1.1 直接引用本段核准依據，避免實作時對規則產生疑義。

**6. `PurchaseQueueAdmissionService.ExecuteAsync` 的整合方式：新增 `AdvanceQueueOnceWithLeaderElectionAsync`，不修改既有 `AdvanceQueueOnceAsync`**
- 既有 `AdvanceQueueOnceAsync`（public，供整合測試直接呼叫「一輪完整推進」，見既有程式碼註解）目前純粹是 TraceId scope 包住 `AdvanceQueueOnceCoreAsync`，**完全不含任何鎖的概念**。既有 `purchase-queue` 能力的測試（`PurchaseQueueAdmissionServiceTests.cs` 等）大量呼叫這個方法，關注的是入場推進邏輯本身，不關心多實例協調——如果直接把取鎖邏輯塞進這個既有方法，會讓這些既有測試意外開始依賴 Redis 連線才能通過，屬於不必要的耦合與回歸風險
- 因此新增一個獨立的 public 方法 `AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken)`，內部依序：
  1. 嘗試 `IDistributedLock.TryAcquireAsync("purchase-queue-admission:lock", ttl, ct)`
  2. `LockResult.Acquired` 或 `LockResult.RedisUnavailable`：呼叫既有 `AdvanceQueueOnceAsync(ct)`（沿用其既有的 TraceId scope／`AdvanceQueueOnceCoreAsync` 呼叫，不重複實作）；`finally` 區塊僅在 `Acquired` 時呼叫 `ReleaseAsync`（`RedisUnavailable` 代表本來就沒有真的鎖，跳過釋放）
  3. `LockResult.HeldByOther`：跳過，記錄 Debug 等級 log（非異常情況，不用 Warning／Error），不呼叫 `AdvanceQueueOnceAsync`
- `ExecuteAsync` 的輪詢迴圈改為呼叫這個新方法（取代原本直接呼叫 `AdvanceQueueOnceCoreAsync`／`AdvanceQueueOnceAsync`），既有的例外處理結構（`catch OperationCanceledException`／`catch Exception`）包住整個新方法呼叫，不變更既有錯誤處理與 `Task.Delay` 節奏
- **這個新方法同時也是本次所有涉及分散式鎖的整合測試（PQLE-001~009／006a）的正確測試進入點**——測試必須呼叫這個方法，而非既有的 `AdvanceQueueOnceAsync`，否則完全不會經過取鎖邏輯，測不到任何鎖相關行為（見 tasks.md 5.3／5.4 的方法名稱更正）
- `LockResult` 設計為明確的三態列舉（`Acquired` / `HeldByOther` / `RedisUnavailable`），不用單純的 `bool`——呼叫端需要區分「別人持有」與「無法判斷」兩種語意完全不同的失敗（見 Decision 4），`bool` 無法表達第三態

## Risks / Trade-offs

- [Risk] Fail-open 降級（Decision 4）代表 Redis 故障期間鎖形同虛設，多實例仍會重複執行 → Mitigation：這是刻意接受的取捨，正確性由 DB 悲觀鎖保證不受影響，只犧牲效率；且會有明確 Warning log 可觀察，不是靜默降級
- [Risk] 單節點 Redis 是新的單點故障來源（雖然 fail-open 已避免它阻斷業務，但仍是新增的維運依賴）→ Mitigation：`docker-compose.yml` 只在本機開發/展示情境使用，非正式高可用部署；若未來真正上生產環境且需要 Redis 高可用，屬於超出本次「基礎 leader election」範疇的部署層優化
- [Risk] TTL 抓太短（例如推進邏輯因活動數量增加而變慢，超過 TTL）會讓鎖提前釋放，導致同一輪出現短暫的雙實例重疊執行 → Mitigation：正確性不受影響（DB 悲觀鎖兜底，見 spec.md PQLE-006a、tasks.md 5.4 的明確測試驗證）；TTL 為可設定值，可依實際活動數量／推進耗時調整，不寫死
- [Risk] 本機開發環境目前只有單一 `api` 服務，本次改動缺乏「真的水平擴展」的環境驗證 → Mitigation：以整合測試模擬多實例（兩個 `PurchaseQueueAdmissionService` 邏輯上並行競爭同一把鎖，見 tasks），驗證鎖互斥/TTL 逾時釋放/fail-open 三種路徑；不需要真的把 compose 改成多 replica 才能驗證機制正確性

## Migration Plan

- 新增 `redis` 服務至 `docker-compose.yml`（無需 volume，鎖為過程性資料、容器重建可清空即可，比照 `seq` 服務的既定取捨）
- `api` 服務新增環境變數 `ConnectionStrings__Redis`（compose service name `redis`，格式 `redis:6379`）
- 新增 `DistributedLockOptions`（`LockTtlMultiplier`，即 TTL = `PollingIntervalSeconds * LockTtlMultiplier`，預設 `3`）
- 部署順序：先啟動 `redis` 服務 → 部署新版 API（`api` 的 `depends_on` 新增 `redis: condition: service_started`，比照 `seq` 的既定寫法——Redis 連線失敗不得阻塞 API 啟動，理由同 Decision 4）
- Rollback：移除鎖的取用邏輯即可讓 `PurchaseQueueAdmissionService` 回到本次之前的行為（無條件執行），無資料庫 schema 變更，無需 migration down

## Open Questions

- `LockTtlMultiplier` 的預設值（`3`）是本次直接決定的起始猜測，比照既有 `PurchaseQueueOptions` 起始值的定位（`rate-limiting-queue` design.md 決策 3），非最終調優值，待實際觀察推進耗時後可調整，調整只需改設定值
- 是否需要為 Redis 連線本身也做健康檢查／可觀測性（例如失敗次數計入既有 Serilog 結構化 log 的哪個欄位）：本次沿用既有 `ILogger` 的 `LogWarning`／`LogError` 慣例即可，未來若需要更細緻的監控指標，留待 `observability` 能力後續擴充
