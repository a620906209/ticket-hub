using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Infrastructure.DistributedLocking;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.BackgroundServices;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.BackgroundServices;

// purchase-queue-leader-election spec：PurchaseQueueAdmissionService 端到端，MUST 透過
// AdvanceQueueOnceWithLeaderElectionAsync 觸發（見 tasks.md 5.3／5.4）。真實 Redis（Testcontainers）
// ＋真實 Postgres（CustomWebApplicationFactory），驗證多實例互斥、TTL 逾時重疊執行下的正確性、
// Redis 故障期間的協調行為。purchase-queue-redis-admission 改動後：Redis Lua Script 已是入場推進
// 唯一的互斥/決策機制，Redis 不可用時的行為從原本的 fail-open（照常執行、靠 Postgres 悲觀鎖兜底）
// 改為 fail-closed（跳過本輪推進與校正），詳見 design.md Decision 6；本檔案 PQLE-007／008／009
// 對應測試已同步改寫（見 tasks.md 8.0／8.4～8.6）。
//
// [Collection(RedisCollection.Name)]：同一 collection 內的測試依序執行（xUnit 保證不平行），
// 這對本檔案是必要前提——多個測試會 Stop/Start 同一個共用 Redis 容器，平行執行會互相干擾。
[Collection(RedisCollection.Name)]
public class PurchaseQueueAdmissionServiceLeaderElectionTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string LockKey = "purchase-queue-admission:lock";

    private readonly CustomWebApplicationFactory _factory;
    private readonly RedisFixture _redisFixture;

    public PurchaseQueueAdmissionServiceLeaderElectionTests(CustomWebApplicationFactory factory, RedisFixture redisFixture)
    {
        _factory = factory;
        _redisFixture = redisFixture;
    }

    private RedisDistributedLock CreateRedisDistributedLock()
        => new(_redisFixture.CreateConnection(), NullLogger<RedisDistributedLock>.Instance);

    private PurchaseQueueAdmissionService CreateService(
        IDistributedLock distributedLock,
        int maxConcurrentAdmittedBuyers = 1,
        int pollingIntervalSeconds = 5,
        int lockTtlMultiplier = 3,
        IServiceScopeFactory? scopeFactory = null,
        StackExchange.Redis.IConnectionMultiplexer? connectionMultiplexer = null,
        Microsoft.Extensions.Logging.ILogger<PurchaseQueueAdmissionService>? logger = null,
        int admissionPendingTtlSeconds = 30)
        => new(
            scopeFactory ?? _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<IDateTimeProvider>(),
            new PurchaseQueueOptions
            {
                MaxConcurrentAdmittedBuyers = maxConcurrentAdmittedBuyers,
                AdmissionTtlSeconds = 300,
                PollingIntervalSeconds = pollingIntervalSeconds,
                AdmissionPendingTtlSeconds = admissionPendingTtlSeconds,
            },
            distributedLock,
            new DistributedLockOptions { LockTtlMultiplier = lockTtlMultiplier },
            // 預設沿用同一個 RedisFixture 連線（Testcontainers）：入場推進 Lua Script 與分散式鎖
            // MUST 指向同一個 Redis 實例，測試才能透過 Stop/StartContainerAsync 讓兩者同時故障/恢復
            // （purchase-queue-redis-admission design.md Decision 6，取代既有 fail-open 假設）。
            connectionMultiplexer ?? _redisFixture.CreateConnection(),
            logger ?? NullLogger<PurchaseQueueAdmissionService>.Instance);

    private async Task<int> ReadAdmittedCountAsync(Guid eventId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;
        return await dbContext.PurchaseQueueEntries.AsNoTracking()
            .CountAsync(e => e.EventId == eventId && e.Status == PurchaseQueueEntryStatus.Admitted && e.AdmissionExpiresAtUtc > now);
    }

    private async Task<PurchaseQueueEntryStatus> ReadStatusAsync(Guid entryId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entry = await dbContext.PurchaseQueueEntries.AsNoTracking().SingleAsync(e => e.Id == entryId);
        return entry.Status;
    }

    private async Task<bool> LockKeyExistsInRedisAsync()
    {
        var database = _redisFixture.CreateConnection().GetDatabase();
        return await database.KeyExistsAsync(LockKey);
    }

    /// <summary>用探測用的鎖（不計入測試斷言）反覆嘗試取鎖，直到成功為止，確認 Redis 連線已恢復可用；
    /// 成功後立即釋放，讓後續測試從乾淨狀態開始（PQLE-009，比照既有 WaitUntilAsync 輪詢等待手法，
    /// 不用固定 Task.Delay 賭時間點）。</summary>
    private async Task WaitUntilRedisReconnectedAsync(int timeoutMs = 15000, int pollIntervalMs = 200)
    {
        var probe = CreateRedisDistributedLock();
        var probeKey = $"pqle-probe:{Guid.NewGuid():N}";
        var elapsed = 0;
        while (true)
        {
            var result = await probe.TryAcquireAsync(probeKey, TimeSpan.FromSeconds(5), CancellationToken.None);
            if (result.LockResult == LockResult.Acquired)
            {
                await probe.ReleaseAsync(probeKey, result.OwnerToken!, CancellationToken.None);
                return;
            }

            if (elapsed >= timeoutMs)
            {
                throw new TimeoutException("等待 Redis 恢復連線逾時");
            }

            await Task.Delay(pollIntervalMs);
            elapsed += pollIntervalMs;
        }
    }

    // PQLE-001 全流程：單一服務實例共用一個真實 Redis。
    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_SingleInstance_AcquiresExecutesThenReleasesLock()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        var waiting = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        var spyLock = new SpyDistributedLock(CreateRedisDistributedLock());
        await CreateService(spyLock).AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);

        spyLock.AcquireResults.Should().Equal(LockResult.Acquired);
        spyLock.ReleaseCallCount.Should().Be(1, "執行完畢後 MUST 釋放該鎖");
        (await LockKeyExistsInRedisAsync()).Should().BeFalse("釋放後 Redis 中的鎖 key 應已不存在");
        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "AdvanceQueueOnceCoreAsync 的推進邏輯確實執行");
    }

    // PQLE-002：兩個服務實例（各自的 IDistributedLock 皆指向同一個真實 Redis）共用同一個真實 Redis。
    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_TwoInstancesConcurrently_OnlyOneAcquiresAndExecutes()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext, maxConcurrentAdmittedBuyers: 2);
        await PurchaseQueueLeaderElectionTestData.SeedManyWaitingEntriesAsync(dbContext, eventId, count: 5);

        var lockA = new SpyDistributedLock(CreateRedisDistributedLock());
        var lockB = new SpyDistributedLock(CreateRedisDistributedLock());
        var serviceA = CreateService(lockA, maxConcurrentAdmittedBuyers: 2);
        var serviceB = CreateService(lockB, maxConcurrentAdmittedBuyers: 2);

        await Task.WhenAll(
            serviceA.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None),
            serviceB.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None));

        var allResults = lockA.AcquireResults.Concat(lockB.AcquireResults).ToList();
        allResults.Should().ContainSingle(r => r == LockResult.Acquired, "只有一個實例應該成功取得鎖並執行推進");
        allResults.Should().ContainSingle(r => r == LockResult.HeldByOther);

        (await ReadAdmittedCountAsync(eventId)).Should().Be(2, "既有 purchase-queue PQ-ADMIT 系列行為不受影響，正確性不變");
    }

    // PQLE-003（服務層）：分三個明確階段，用可控制的同步機制讓實例 A 的推進邏輯確定停留在
    // 「已取得鎖、尚未完成」的狀態，不依賴時序巧合。
    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_WhenHeldByOther_SkipsThenRetriesSuccessfullyNextRound()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        var waiting = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        var gate = new ScanGate();
        var lockA = new SpyDistributedLock(CreateRedisDistributedLock());
        var lockB = new SpyDistributedLock(CreateRedisDistributedLock());
        var blockingScopeFactory = new BlockingScopeFactory(_factory.Services.GetRequiredService<IServiceScopeFactory>(), gate);
        var serviceA = CreateService(lockA, scopeFactory: blockingScopeFactory);
        var serviceB = CreateService(lockB);

        // 第一輪（重疊）：啟動 A，A 的推進邏輯卡在同步點（鎖確定仍被 A 持有、尚未釋放）。
        var taskA = serviceA.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);
        await gate.WaitUntilScanEnteredAsync();

        await serviceB.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);
        lockB.AcquireResults.Should().Equal(LockResult.HeldByOther);
        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "B 被跳過時不應觸發任何推進");

        // 釋放：讓 A 完成推進邏輯並釋放鎖。
        gate.Release();
        await taskA;
        lockA.AcquireResults.Should().Equal(LockResult.Acquired);
        lockA.ReleaseCallCount.Should().Be(1);
        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted);
        (await LockKeyExistsInRedisAsync()).Should().BeFalse();

        // 下一輪：確認鎖已釋放後，實例 B 再次呼叫，驗證這次 B 成功取得鎖並執行推進邏輯
        // （不因上一輪失敗就永久放棄）。
        await serviceB.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);
        lockB.AcquireResults.Should().Equal(LockResult.HeldByOther, LockResult.Acquired);
        lockB.ReleaseCallCount.Should().Be(1, "B 第二輪成功取得鎖後 MUST 執行完整推進流程並釋放");
    }

    // PQLE-004（新增服務層整合測試，見 tasks.md 8.2.1；元件層既有測試
    // ReleaseAsync_AfterNormalAcquire_AllowsImmediateReacquisitionByAnotherCaller 保留不動）：
    // 正常完成後主動釋放鎖，讓下一個實例的下一次輪詢可成功取得鎖並執行。
    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_AfterNormalCompletion_ReleasesLockForNextInstance()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        var lockA = new SpyDistributedLock(CreateRedisDistributedLock());
        var serviceA = CreateService(lockA);
        await serviceA.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);

        lockA.AcquireResults.Should().Equal(LockResult.Acquired);
        lockA.ReleaseCallCount.Should().Be(1, "執行完畢後 MUST 主動釋放鎖");
        (await LockKeyExistsInRedisAsync()).Should().BeFalse("釋放後 Redis 中的鎖 key 應已不存在");

        var lockB = new SpyDistributedLock(CreateRedisDistributedLock());
        var serviceB = CreateService(lockB);
        await serviceB.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);

        lockB.AcquireResults.Should().Equal(LockResult.Acquired);
    }

    /// <summary>TryAcquireAsync 正常轉發；ReleaseAsync 刻意不轉發（no-op），模擬程序在真正送出 Redis
    /// DEL 之前就已當機，Redis 端的鎖 key 不會被刪除（PQLE-005，見 tasks.md 8.2.2）——一般業務例外
    /// 不足以模擬這個情境，因為 AdvanceQueueOnceWithLeaderElectionAsync 的 finally 區塊在正常情況下
    /// 一定會呼叫 ReleaseAsync，注入例外並不能阻止這次呼叫真正送達 Redis。</summary>
    private sealed class ReleaseSuppressingDistributedLock : IDistributedLock
    {
        private readonly IDistributedLock _inner;

        public ReleaseSuppressingDistributedLock(IDistributedLock inner) => _inner = inner;

        public Task<LockAcquisitionResult> TryAcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken)
            => _inner.TryAcquireAsync(key, ttl, cancellationToken);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // PQLE-005（新增服務層整合測試，見 tasks.md 8.2.2；元件層既有測試
    // TryAcquireAsync_AfterTtlExpiresWithoutRelease_AllowsOtherCallerToAcquire 保留不動）：
    // 持有鎖的實例未能主動釋放，TTL 到期後鎖自動可用。
    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_WhenHolderFailsToRelease_TtlExpiryAllowsReacquisition()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        // TTL = pollingIntervalSeconds(1) * lockTtlMultiplier(1) = 1 秒（同既有 PQLE-006a 手法）。
        var lockA = new ReleaseSuppressingDistributedLock(CreateRedisDistributedLock());
        var serviceA = CreateService(lockA, pollingIntervalSeconds: 1, lockTtlMultiplier: 1);
        await serviceA.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);

        (await LockKeyExistsInRedisAsync()).Should().BeTrue("ReleaseAsync 被抑制（模擬程序在送出 DEL 前當機），Redis 端鎖 key 應仍存在");

        await Task.Delay(TimeSpan.FromMilliseconds(1300));

        var lockB = new SpyDistributedLock(CreateRedisDistributedLock());
        var serviceB = CreateService(lockB);
        await serviceB.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);

        lockB.AcquireResults.Should().Equal(LockResult.Acquired);
        lockB.ReleaseCallCount.Should().Be(1);
    }

    // PQLE-006（新增服務層整合測試，見 tasks.md 8.2.3；元件層既有測試
    // ReleaseAsync_WithStaleOwnerTokenAfterAnotherCallerAcquiredNewLock_IsNoOp 保留不動）：
    // 已逾時釋放的鎖不可被原持有者誤釋放新的持有者。用兩個各自獨立的 ScanGate／BlockingScopeFactory
    // 建立「A 的鎖已 TTL 到期」「B 已取得新鎖且尚未釋放」「A 此時才執行遲到的釋放」三個可控制的同步點。
    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_StaleReleaseAfterTtlExpiry_DoesNotDeleteNewHoldersLock()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        var gateA = new ScanGate();
        var gateB = new ScanGate();
        var realScopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var blockingScopeFactoryA = new BlockingScopeFactory(realScopeFactory, gateA);
        var blockingScopeFactoryB = new BlockingScopeFactory(realScopeFactory, gateB);

        var lockA = new SpyDistributedLock(CreateRedisDistributedLock());
        var serviceA = CreateService(lockA, pollingIntervalSeconds: 1, lockTtlMultiplier: 1, scopeFactory: blockingScopeFactoryA);
        var taskA = serviceA.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);
        await gateA.WaitUntilScanEnteredAsync();

        // 等待超過 A 的 TTL（1 秒），Redis 端自動視為 A 的鎖已釋放——A 本身仍卡在同步點，尚未完成。
        await Task.Delay(TimeSpan.FromMilliseconds(1300));

        var lockB = new SpyDistributedLock(CreateRedisDistributedLock());
        var serviceB = CreateService(lockB, scopeFactory: blockingScopeFactoryB);
        var taskB = serviceB.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);
        await gateB.WaitUntilScanEnteredAsync();

        lockB.AcquireResults.Should().Equal(LockResult.Acquired);

        // 讓 A 完成其（遲到的）ReleaseAsync 呼叫，帶的是 A 自己過期前取得的 ownerToken。
        gateA.Release();
        await taskA;

        lockA.ReleaseCallCount.Should().Be(1, "A 仍會呼叫 ReleaseAsync，但這次釋放應是針對已不屬於自己的鎖");
        var database = _redisFixture.CreateConnection().GetDatabase();
        var currentLockValue = await database.StringGetAsync(LockKey);
        currentLockValue.HasValue.Should().BeTrue("A 的釋放應為 no-op，鎖 key 不應被刪除");
        currentLockValue.ToString().Should().Be(lockB.LastAcquiredOwnerToken,
            "鎖 key 的值仍應是 B 的 ownerToken，證明 A 的遲到釋放沒有誤刪 B 的鎖");

        // B 仍卡在 gateB、尚未完成的推進不受 A 的遲到釋放影響，讓 B 正常完成並釋放，避免影響後續測試。
        gateB.Release();
        await taskB;
        lockB.ReleaseCallCount.Should().Be(1);
    }

    // PQLE-007（服務層，取代原本的 fail-open 斷言，見 tasks.md 8.4）：
    // (a) Redis 完全無法連線時，鎖取得本身即回報 RedisUnavailable，MUST 跳過本輪推進與校正
    // （design.md Decision 6，偏離既有 purchase-queue-leader-election 的 fail-open 慣例）。
    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_WhenRedisUnreachable_SkipsAdvanceAndReconciliationAndLogsWarning()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        var waiting = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        var lockLogger = new RecordingLogger<RedisDistributedLock>();
        var unreachableOptions = StackExchange.Redis.ConfigurationOptions.Parse("127.0.0.1:1");
        unreachableOptions.AbortOnConnectFail = false;
        unreachableOptions.ConnectTimeout = 300;
        unreachableOptions.SyncTimeout = 300;
        var unreachableConnection = StackExchange.Redis.ConnectionMultiplexer.Connect(unreachableOptions);
        var spyLock = new SpyDistributedLock(new RedisDistributedLock(unreachableConnection, lockLogger));
        var serviceLogger = new RecordingLogger<PurchaseQueueAdmissionService>();

        await CreateService(spyLock, connectionMultiplexer: unreachableConnection, logger: serviceLogger)
            .AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);

        spyLock.AcquireResults.Should().Equal(LockResult.RedisUnavailable);
        spyLock.ReleaseCallCount.Should().Be(0, "RedisUnavailable 代表本來就沒有真的鎖，不應嘗試釋放");
        serviceLogger.LoggedLevels.Should().Contain(Microsoft.Extensions.Logging.LogLevel.Warning,
            "Redis 不可用時 MUST 記錄 Warning 等級結構化 log");
        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting,
            "Redis 不可用時 MUST 跳過本輪推進與校正（fail-closed），不得再照舊行為執行完整推進");
    }

    // PQLE-007(b)／PQLE-007(a 續)（見 tasks.md 8.4a）：模擬「Lua Script 其實已在 Redis 端執行成功、
    // 只是 App 端沒能觀察到（EVAL 逾時／連線中斷）」的情境——用 FailingPurchaseQueueRepository 讓真實
    // Lua Script（透過完整的 AdvanceQueueOnceAsync 正常路徑）執行成功、但緊接其後的批次條件式 UPDATE
    // 失敗，重現「Redis 端已推進、Postgres 尚未落地」的過渡態，不手動竄改 Redis 資料本身。驗證：
    // (a) 該筆紀錄在 pending 標記存活期間，即使又執行了數輪推進／校正，仍維持 Postgres Waiting／
    // Redis admitted 的合法過渡態，不被提前清除或落地，也不會被 ZPOPMIN 重複選中；
    // (b) 只有 pending TTL 到期後的下一次成功校正，才會以條件式 UPDATE 完成落地為 Admitted。
    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_WhenPromotionDecisionIsAbandoned_StaysInTransitionUntilPendingMarkerExpiresThenReconciliationCompletesIt()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        // pending TTL 設為 1 秒，加速測試（design.md Decision 9：TTL 為可設定契約值）。
        const int pendingTtlSeconds = 1;
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext, maxConcurrentAdmittedBuyers: 2);
        var target = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        var connection = _redisFixture.CreateConnection();
        var realScopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var failingScopeFactory = new PurchaseQueueRepositoryInterceptingScopeFactory(
            realScopeFactory, repo => new FailingPurchaseQueueRepository(repo, failOnAdmitBatchIds: [target.Id]));

        var lockAbandon = new SpyDistributedLock(CreateRedisDistributedLock());
        var abandoningService = CreateService(
            lockAbandon, maxConcurrentAdmittedBuyers: 2, scopeFactory: failingScopeFactory,
            connectionMultiplexer: connection, admissionPendingTtlSeconds: pendingTtlSeconds);

        // 這一輪：真實 Lua Script 把 target 從 waiting 移到 admitted 並寫入 pending 標記，但 target 的
        // 批次條件式 UPDATE 被攔截拋出，Postgres 落地失敗、不做反向補償。「other」刻意在這一輪之後
        // 才建立——若與 target 同一輪成為候選，會被同一次 AdmitBatchAsync 批次呼叫一併攔截失敗
        // （批次呼叫是整批成功或整批拋例外，不是逐筆獨立成功/失敗，見 design.md Decision 4「批次執行
        // 與逐筆結果的落差」），無法反映「target 卡住、other 不受影響」這個測試想驗證的情境。
        await abandoningService.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);

        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "批次 UPDATE 被攔截，Postgres 落地應維持失敗前的狀態");
        var admittedKey = ProjectC.Infrastructure.PurchaseQueue.PurchaseQueueAdmissionRedisKeys.Admitted(eventId);
        var database = connection.GetDatabase();
        (await database.SortedSetScoreAsync(admittedKey, target.Id.ToString())).Should().NotBeNull(
            "Redis 端的推進決策已經確定且有效，target 應已在 admitted 鏡像中");

        // 現在才建立 other——這一輪 target 仍佔用 admitted 鏡像中的一個名額（即使 Postgres 尚未落地），
        // maxConcurrentAdmittedBuyers: 2 還有 1 個空位，other 應能正常、獨立於 target 被推進。
        var other = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-5));

        // pending 標記存活期間：即使再跑數輪（用一個「不攔截」的服務），target 仍應維持過渡態，
        // 不被清除、不被落地、不被 ZPOPMIN 重複選中。
        var lockNormal = new SpyDistributedLock(CreateRedisDistributedLock());
        var normalService = CreateService(
            lockNormal, maxConcurrentAdmittedBuyers: 2, connectionMultiplexer: connection, admissionPendingTtlSeconds: pendingTtlSeconds);
        await normalService.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);
        await normalService.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);

        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "pending 標記存活期間 MUST NOT 被校正機制提前清除或代為完成落地");
        (await database.SortedSetScoreAsync(admittedKey, target.Id.ToString())).Should().NotBeNull("過渡態期間仍應留在 admitted 鏡像");
        (await ReadStatusAsync(other.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "其他候選紀錄的推進不受影響");

        // 等待 pending TTL 到期，下一次成功校正才會代為完成落地。
        await Task.Delay(TimeSpan.FromMilliseconds((pendingTtlSeconds * 1000) + 500));
        await normalService.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);

        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "pending TTL 到期後，下一次成功校正 MUST 代為完成 Postgres 落地");
    }

    // PQLE-008（服務層，取代原本「資料庫悲觀鎖仍保證不超額」的斷言，見 tasks.md 8.5）：
    // Redis 不可用時 fail-closed——Waiting 不被錯誤推進、已逾時的 Admitted 紀錄也不被錯誤標記為
    // Expired（同一 Lua Script 驅動兩者，Redis 不可用時一併跳過）；恢復連線後才由下一輪正常處理。
    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_WhenRedisUnavailable_BothInstancesSkipAndQueueStaysUnchangedUntilRecovery()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext, maxConcurrentAdmittedBuyers: 1);
        await PurchaseQueueLeaderElectionTestData.SeedManyWaitingEntriesAsync(dbContext, eventId, count: 5);
        // 已超過入場逾時時間但 Postgres 仍為 Admitted、尚未標記的紀錄（PQLE-008 本輪審查補充）。
        var overdueAdmitted = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-20));
        using (var admitScope = _factory.Services.CreateScope())
        {
            var admitDbContext = admitScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tracked = await admitDbContext.PurchaseQueueEntries.SingleAsync(e => e.Id == overdueAdmitted.Id);
            tracked.Admit(DateTime.UtcNow.AddMinutes(-15), DateTime.UtcNow.AddMinutes(-1));
            await admitDbContext.SaveChangesAsync();
        }

        var lockA = new SpyDistributedLock(CreateRedisDistributedLock());
        var lockB = new SpyDistributedLock(CreateRedisDistributedLock());
        var serviceA = CreateService(lockA, maxConcurrentAdmittedBuyers: 1);
        var serviceB = CreateService(lockB, maxConcurrentAdmittedBuyers: 1);

        await _redisFixture.StopContainerAsync();
        try
        {
            await Task.WhenAll(
                serviceA.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None),
                serviceB.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None));

            lockA.AcquireResults.Should().Equal(LockResult.RedisUnavailable);
            lockB.AcquireResults.Should().Equal(LockResult.RedisUnavailable);

            (await ReadAdmittedCountAsync(eventId)).Should().Be(0,
                "Redis 不可用時 MUST fail-closed，即使兩個實例都嘗試執行，Waiting 紀錄也不應被錯誤推進");
            (await ReadStatusAsync(overdueAdmitted.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted,
                "入場推進與逾時標記由同一 Lua Script 驅動，Redis 不可用時一併跳過，MUST NOT 被標記為 Expired");
        }
        finally
        {
            await _redisFixture.StartContainerAsync();
        }

        await WaitUntilRedisReconnectedAsync();

        await CreateService(new SpyDistributedLock(CreateRedisDistributedLock()), maxConcurrentAdmittedBuyers: 1)
            .AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);

        (await ReadStatusAsync(overdueAdmitted.Id)).Should().Be(PurchaseQueueEntryStatus.Expired,
            "Redis 恢復連線後，該筆逾時紀錄才由下一輪正常推進流程標記為 Expired");
        (await ReadAdmittedCountAsync(eventId)).Should().Be(1, "釋放的名額正常供其他 Waiting 紀錄使用");
    }

    // PQLE-009（服務層，取代原本「故障期間兩實例皆 fail-open」的斷言，見 tasks.md 8.6）：
    // 兩個服務實例共用同一個真實 Redis；先故障、fail-closed 跳過，恢復連線後不需重啟即可恢復正常互斥。
    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_AfterRedisOutageRecovers_ResumesNormalMutualExclusionWithoutRestart()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        var firstWaiting = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        var lockA = new SpyDistributedLock(CreateRedisDistributedLock());
        var lockB = new SpyDistributedLock(CreateRedisDistributedLock());
        var serviceA = CreateService(lockA, maxConcurrentAdmittedBuyers: 2);
        var serviceB = CreateService(lockB, maxConcurrentAdmittedBuyers: 2);

        await _redisFixture.StopContainerAsync();
        try
        {
            await Task.WhenAll(
                serviceA.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None),
                serviceB.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None));

            lockA.AcquireResults.Should().Equal(LockResult.RedisUnavailable);
            lockB.AcquireResults.Should().Equal(LockResult.RedisUnavailable);
            (await ReadStatusAsync(firstWaiting.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "故障期間兩實例皆 fail-closed 跳過，排隊紀錄不變");
        }
        finally
        {
            await _redisFixture.StartContainerAsync();
        }

        await WaitUntilRedisReconnectedAsync();

        // 留一個空位（maxConcurrentAdmittedBuyers: 2，firstWaiting 尚未入場），新增一筆等待紀錄——
        // 這筆紀錄只有在「真正執行推進的那個實例」跑完 AdvanceQueueOnceAsync 才會轉為 Admitted，
        // 用來直接驗證業務不變量（只有 leader 真正執行了掃描/推進），而不只是看鎖的回傳結果。
        using var secondScope = _factory.Services.CreateScope();
        var secondDbContext = secondScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var secondWaiting = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(secondDbContext, eventId, DateTime.UtcNow.AddMinutes(-5));

        await Task.WhenAll(
            serviceA.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None),
            serviceB.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None));

        var secondRoundResults = new[] { lockA.AcquireResults[^1], lockB.AcquireResults[^1] };
        secondRoundResults.Should().BeEquivalentTo([LockResult.Acquired, LockResult.HeldByOther],
            "Redis 恢復連線後，不需重啟應用程式或任何手動介入即可恢復正常的分散式鎖互斥行為");
        (await ReadStatusAsync(firstWaiting.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "恢復後由取得鎖的一方正常推進兩筆等待紀錄");
        (await ReadStatusAsync(secondWaiting.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted,
            "不只驗證鎖結果，直接驗證業務不變量：真正執行推進的只有取得鎖的那個實例，這筆新等待紀錄才會被推進");
    }

    // PQLE-010（tasks.md 8.7 額外確認）：4.6 新增的啟動時全量校正在 Redis 不可用時同樣不阻塞啟動。
    // Testing 環境不會把 PurchaseQueueAdmissionService 註冊為 IHostedService（見
    // ApplicationStartupWithRedisUnavailableTests 開頭註解），所以這裡直接手動建立、呼叫
    // StartAsync，驗證呼叫本身快速返回（BackgroundService.ExecuteAsync 以背景 Task 執行，
    // 不會被 StartAsync 等待完成），不因 Redis 不可用而卡住。
    [Fact]
    public async Task StartAsync_WhenRedisUnreachable_ReturnsQuicklyWithoutBlockingOnStartupReconciliation()
    {
        var unreachableOptions = StackExchange.Redis.ConfigurationOptions.Parse("127.0.0.1:1");
        unreachableOptions.AbortOnConnectFail = false;
        unreachableOptions.ConnectTimeout = 300;
        unreachableOptions.SyncTimeout = 300;
        var unreachableConnection = StackExchange.Redis.ConnectionMultiplexer.Connect(unreachableOptions);
        var service = CreateService(new FakeDistributedLock(), connectionMultiplexer: unreachableConnection);

        using var cts = new CancellationTokenSource();
        var startTask = service.StartAsync(cts.Token);
        var completed = await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(3)));

        completed.Should().Be(startTask, "StartAsync 不應等待啟動時的全量校正完成，Redis 不可用時也不應卡住啟動流程");
        await service.StopAsync(CancellationToken.None);
    }

    // PQLE-006a（取代既有斷言，見 tasks.md 8.3）：TTL 到期但原持有者仍在執行中，重疊執行期間最終
    // 有效入場數不超過上限——正確性現在由 Redis Lua Script 的原子性保證（design.md Decision 4），
    // 不再是既有資料庫悲觀鎖。「pending 標記存活期間不被另一實例的校正誤判清除」這個更精細的過渡態
    // 不變量，由専門模擬「Redis 已推進、Postgres 未落地」情境的測試涵蓋（見上方 PQLE-007(b) 測試與
    // PurchaseQueueMirrorReconciliationTests 的 PQLE-REBUILD-002／002a），此處不重複模擬。
    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_WhenLockTtlExpiresWhileHolderStillExecuting_OverlapDoesNotExceedAdmissionLimit()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext, maxConcurrentAdmittedBuyers: 1);
        await PurchaseQueueLeaderElectionTestData.SeedManyWaitingEntriesAsync(dbContext, eventId, count: 3);

        var gate = new ScanGate();
        var lockA = new SpyDistributedLock(CreateRedisDistributedLock());
        var blockingScopeFactory = new BlockingScopeFactory(_factory.Services.GetRequiredService<IServiceScopeFactory>(), gate);
        // TTL 是這次取鎖呼叫的參數（PollingIntervalSeconds * LockTtlMultiplier），不是綁定特定實例的
        // 屬性——這裡刻意把 A 的兩個係數都設為最小值 1，讓 TTL = 1 秒，模擬「TTL 抓太短」的情境。
        var serviceA = CreateService(lockA, maxConcurrentAdmittedBuyers: 1, pollingIntervalSeconds: 1, lockTtlMultiplier: 1, scopeFactory: blockingScopeFactory);

        var taskA = serviceA.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);
        await gate.WaitUntilScanEnteredAsync();

        // 等待超過 A 的 TTL（1 秒），讓 Redis 端自動視為 A 的鎖已釋放——A 本身仍卡在同步點，尚未完成。
        await Task.Delay(TimeSpan.FromMilliseconds(1300));

        var lockB = new SpyDistributedLock(CreateRedisDistributedLock());
        var serviceB = CreateService(lockB, maxConcurrentAdmittedBuyers: 1);
        await serviceB.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);
        // A 的鎖已因 TTL 到期而可被視為釋放，B 應該能取得新鎖。
        lockB.AcquireResults.Should().Equal(LockResult.Acquired);

        gate.Release();
        await taskA;

        (await ReadAdmittedCountAsync(eventId)).Should().Be(1,
            "兩個實例的推進邏輯重疊執行期間，最終有效入場人數 MUST NOT 超過上限——即使兩次分散式鎖的取得互相重疊，" +
            "Redis 對單一 Lua Script 的執行仍是單執行緒、原子的，第二次執行時 zset 已反映第一次的結果");
    }

    /// <summary>讓 A 的推進邏輯確定停留在「已取得鎖、尚未完成」的狀態，供 PQLE-003／PQLE-006a
    /// 用來控制重疊時序，不依賴時序巧合。</summary>
    private sealed class ScanGate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilScanEnteredAsync() => _entered.Task;

        public void Release() => _release.TrySetResult();

        public async Task WaitForReleaseAsync()
        {
            _entered.TrySetResult();
            await _release.Task;
        }
    }

    private sealed class BlockingEventRepository : IEventRepository
    {
        private readonly IEventRepository _inner;
        private readonly ScanGate _gate;

        public BlockingEventRepository(IEventRepository inner, ScanGate gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public async Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitForReleaseAsync();
            return await _inner.GetAllAsync(cancellationToken);
        }

        public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => _inner.GetByIdAsync(id, cancellationToken);

        public void Add(Event @event) => _inner.Add(@event);

        public void Update(Event @event) => _inner.Update(@event);

        public Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken)
            => _inner.GetForUpdateAsync(eventId, cancellationToken);
    }

    private sealed class BlockingServiceProvider : IServiceProvider
    {
        private readonly IServiceProvider _inner;
        private readonly ScanGate _gate;

        public BlockingServiceProvider(IServiceProvider inner, ScanGate gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public object? GetService(Type serviceType)
        {
            var service = _inner.GetService(serviceType);
            return service is IEventRepository eventRepository ? new BlockingEventRepository(eventRepository, _gate) : service;
        }
    }

    private sealed class BlockingServiceScope : IServiceScope
    {
        private readonly IServiceScope _inner;

        public BlockingServiceScope(IServiceScope inner, ScanGate gate)
        {
            _inner = inner;
            ServiceProvider = new BlockingServiceProvider(inner.ServiceProvider, gate);
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose() => _inner.Dispose();
    }

    private sealed class BlockingScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;
        private readonly ScanGate _gate;

        public BlockingScopeFactory(IServiceScopeFactory inner, ScanGate gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public IServiceScope CreateScope() => new BlockingServiceScope(_inner.CreateScope(), _gate);
    }
}
