## 1. 系統清理路徑的並發取消測試（OCLE-009、OCLE-010）

- [ ] 1.1 新增 `CancelExpiredOrderAsync` 並發取消測試，固定「正確性不依賴分散式鎖」
  - 測試先行（Infrastructure.Tests，比照 `OrderServiceConcurrencyTests`，真實 PostgreSQL、兩個獨立 `OrderService` scope）：
    - `CancelExpiredOrderAsync_TwoConcurrentCallsOnSameSeatOrder_OnlyOneSucceedsAndSeatReleasedOnce`（009）
    - `CancelExpiredOrderAsync_TwoConcurrentCallsOnSameCountingOrder_OnlyOneSucceedsAndQuantityRestoredOnce`（010）
  - RED 說明：這兩個測試固定的是**既有行為**（`ChangeOrderStatusAsync` 已鎖後重讀），正式程式碼不需修改，故不存在「正式碼修改前失敗」的 RED；改以 `scripts/verify-test-evidence.sh mutate` 證明測試能失敗——暫時移除 `ChangeOrderStatusAsync` 內的 `_orderRepository.ReloadAsync(...)` 呼叫，兩個測試須因目標行為不符而失敗（座位／庫存被重複釋放或兩個都回成功），一次一個改動；若移除 `ReloadAsync` 後測試仍通過（FOR UPDATE 序列化已足以擋下），改以移除 `GetForUpdateAsync` 鎖定作為 mutation，確認測試真的會失敗
  - 最小實作：無正式程式碼變更
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~CancelExpiredOrderAsync_TwoConcurrent"` 全數通過
  - 紀錄（原文）：
    - mutation 驗證（命令、被失敗的測試名稱與訊息、`Failed!` 摘要行、還原後 `git diff` 為空）：
    - GREEN（命令與摘要行）：
    - 重構：N/A（無正式程式碼變更）

## 2. 清理服務取得 leader lock（TDD）

- [ ] 2.1 `ExpiredOrderCleanupService` 每輪取鎖，鎖被持有時跳過、取得時清理並釋放（對應 OCLE-001、003、004）
  - 測試先行（WebApi.Tests `BackgroundServices/ExpiredOrderCleanupServiceLeaderElectionTests.cs`，以假 `IDistributedLock`、假 `IServiceScopeFactory`／`IOrderRepository` 隔離 DB 與 Redis，不碰容器）：
    - `CleanupOnceWithLeaderElectionAsync_WhenLockAcquired_ScansAndReleasesWithOwnerToken`（001）
    - `CleanupOnceWithLeaderElectionAsync_WhenLockHeldByOther_SkipsWithoutScanningOrReleasing`（鎖被持有時 scope factory 與 repository 完全未被呼叫，並以 log 擷取斷言有 Debug 等級的跳過紀錄）
    - `CleanupOnceWithLeaderElectionAsync_AfterSkippedRound_TriesAcquireAgainNextRound`（003；連續兩輪，第一輪 `HeldByOther`、第二輪 `Acquired`）
    - `CleanupOnceWithLeaderElectionAsync_WhenScanThrows_StillReleasesLockAndPropagates`（004；以掃描階段例外驗證 `finally` 釋放）
    - 鎖 key／TTL：以假鎖記錄傳入值，斷言 key 為單一固定值、TTL = `IntervalSeconds × LockTtlMultiplier`；假鎖與假 repository 共用呼叫順序紀錄，001／004 斷言順序為 `TryAcquire → 掃描 → Release`
  - 預期 RED：新方法尚不存在，**編譯失敗不算 RED**——先加方法簽章（回傳 `Task.CompletedTask` 的空殼，建構子已含新參數）使測試可編譯，再執行；RED 為斷言失敗：Expected 假鎖 `TryAcquireAsync` 被呼叫 1 次，but found 0 times 等，須是目標行為不符
  - 最小實作：建構子新增 `IDistributedLock`、`DistributedLockOptions`；新增 `CleanupOnceWithLeaderElectionAsync`；`ExecuteAsync` 改呼叫帶鎖版本（保留外層 TraceId scope，不另 push）；既有 `CleanupOnceAsync` 不變；同步更新 6 處測試建構（`OrderServiceNotificationTests`、`QueryCachingFailOpenTests`、`ExceptionMaskingPipelineTests`、`BackgroundServiceCycleLevelFailureTraceIdTests`、`ExpiredOrderCleanupServiceTests` 與 `BackgroundServiceTraceIdTests` 的 `CreateService()` helper；以型別引用搜尋確認無遺漏）
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --filter "FullyQualifiedName~ExpiredOrderCleanupServiceLeaderElection"` 全數通過
  - 重構後重跑：同上，或標 N/A
  - 紀錄（原文，RED 標明為正式程式碼修改前）：
    - RED：
    - GREEN：
    - 重構：
  - 還原驗證（核心業務邏輯 task，`scripts/verify-test-evidence.sh restore --path src/ProjectC.WebApi/BackgroundServices/ExpiredOrderCleanupService.cs`；因建構子變動連帶的測試檔不入 stash，需確認可編譯，否則改用 `mutate` 暫時移除 `HeldByOther` 分支與 `finally` 釋放，一次一個）：

- [ ] 2.2 Redis 不可用時照跑清理並記 Warning（對應 OCLE-007、007b、007c、008）
  - 測試先行（同 2.1 檔案）：
    - `CleanupOnceWithLeaderElectionAsync_WhenRedisUnavailable_StillScansLogsWarningAndDoesNotRelease`（007；以 log 擷取斷言 Warning 存在且指出無鎖執行，非空前提：同一擷取方法在鎖正常取得時須無此 Warning）
    - `CleanupOnceWithLeaderElectionAsync_WhenAcquireThrowsNonConnectionRedisException_StillScansAndLogsWarning`（007b；假鎖丟 `RedisServerException`，並斷言 `ReleaseAsync` 未被呼叫）
    - `CleanupOnceWithLeaderElectionAsync_WhenScanThrowsAndReleaseThrows_PropagatesScanException`（007c；Warning 不含 owner token）
    - `CleanupOnceWithLeaderElectionAsync_WhenScanSucceedsAndReleaseThrows_DoesNotThrowAndLogsWarning`（007c；獨立測試）
    - `CleanupOnceWithLeaderElectionAsync_AfterRedisRecovers_ResumesLockExclusion`（008；第一輪 `RedisUnavailable`、第二輪 `HeldByOther` 即跳過）
  - 預期 RED：2.1 的最小實作只處理 `Acquired`／`HeldByOther`，若把 `RedisUnavailable` 視同跳過則 007 失敗（Expected 假 repository 被掃描 1 次，but found 0）；實際 RED 以執行結果為準，須是目標行為不符、非編譯錯誤
  - 最小實作：`RedisUnavailable` 與取鎖拋出的非取消例外皆記 Warning 後照跑清理、不呼叫 `ReleaseAsync`；`finally` 內釋放另包 try/catch 只記 Warning
  - GREEN／重構後重跑：同 2.1 命令
  - 紀錄（原文）：
    - RED：
    - GREEN：
    - 重構：

## 3. 多實例整合測試（真實 PostgreSQL + Redis）

- [ ] 3.1 兩個服務實例同時處理同一批逾時訂單（對應 OCLE-002、005、006）
  - 測試先行（Infrastructure.Tests，沿用 `RedisFixture` 與既有 PostgreSQL 夾具；兩個 `ExpiredOrderCleanupService` 實例共用同一個 `RedisDistributedLock`）：
    - `CleanupOnceWithLeaderElectionAsync_TwoInstancesConcurrently_OnlyOneProcessesAndSeatReleasedOnce`（002；斷言每筆訂單取消 1 次、座位釋放 1 次、無 Conflict Warning，至少 3 筆訂單以提高並發命中率）
    - `CleanupOnceWithLeaderElectionAsync_WhenHolderCrashesWithoutRelease_OtherInstanceProceedsAfterTtl`（005；先以同 key 預先持有短 TTL 的鎖模擬當掉，TTL 到期後第二實例成功執行；等待比照 `RedisDistributedLockTests` 既有輪詢手法，不依賴 wall-clock sleep 的單次邊界）
    - `CleanupOnceWithLeaderElectionAsync_WhenLockExpiredWhileHolderStillRunning_OverlapDoesNotDoubleRelease`（006；以可控的阻塞點讓 A 掃描中途停住、鎖過期，B 取得新鎖後**同樣停在掃描中不釋放**；此時放行 A 完成並釋放，斷言 B 持有的鎖仍在（釋放只由持有者完成），再放行 B。真正的重疊取消由 009／010 覆蓋，本測試只驗證鎖層行為）
  - 預期 RED：在 2.1／2.2 完成後這些測試對的是已實作的行為，故 RED 以 `mutate` 取代——暫時讓帶鎖版本略過取鎖（直接執行清理），002 須因 Warning／重複處理斷言失敗（Expected 取消次數 1，but found 2 等）；005／006 另以對應改動驗證能失敗，一次一個
  - 最小實作：無新增正式程式碼（若測試需要可控的阻塞點，優先透過既有 DI 替身，不為測試新增正式碼分支）
  - GREEN：`docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~CleanupOnceWithLeaderElection"` 全數通過
  - 紀錄（原文）：
    - mutation 驗證：
    - GREEN：
    - 重構：N/A

## 4. 文件與收尾

- [ ] 4.1 同步 `docs/review/TODO.md`：H-6 改為實際範圍（leader lock），註明 FOR UPDATE／xmin 部分原本已由現有鎖處理；路線圖 ⑦ 標為已實作（待歸檔）。TDD：N/A（純文件）
- [ ] 4.2 hardener 最終檢查：套用 `.claude/skills/hardener/SKILL.md`——`CancellationToken` 傳遞（取鎖、釋放、清理）、鎖釋放的例外邊界（`finally` 內釋放失敗不得遮蔽清理例外）、取消時是否仍釋放鎖、日誌不含敏感資訊；結果記錄於此：
- [ ] 4.3 全套測試（各專案摘要行貼原文）：`docker compose exec api dotnet test`（Domain／Application／Infrastructure／WebApi／LoadTest.Seeder 各專案）；若 `ServiceRegistrationSnapshotTests` 受影響則更新基準並逐行核對新增項目
- [ ] 4.4 `graphify update .`
- [ ] 4.5 Task 完成後告知：spec 同步狀態、AC 涵蓋（OCLE-001～010 皆有對應測試）、重複邏輯（與 `PurchaseQueueAdmissionService` 的取鎖流程是否值得抽共用，三處以上才抽）、命名一致性
