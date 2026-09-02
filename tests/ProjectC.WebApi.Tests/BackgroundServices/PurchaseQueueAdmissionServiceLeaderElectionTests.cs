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
// Redis 故障期間 fail-open 與故障恢復後的協調行為。
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
        IServiceScopeFactory? scopeFactory = null)
        => new(
            scopeFactory ?? _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<IDateTimeProvider>(),
            new PurchaseQueueOptions
            {
                MaxConcurrentAdmittedBuyers = maxConcurrentAdmittedBuyers,
                AdmissionTtlSeconds = 300,
                PollingIntervalSeconds = pollingIntervalSeconds,
            },
            distributedLock,
            new DistributedLockOptions { LockTtlMultiplier = lockTtlMultiplier },
            NullLogger<PurchaseQueueAdmissionService>.Instance);

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

    // PQLE-007（服務層）：單一服務實例搭配真實 Redis 連線失敗，驗證完整的 fail-open 行為。
    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_WhenRedisUnreachable_ExecutesFullAdvanceWithoutReleasingAndLogsWarning()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        var waiting = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        var logger = new RecordingLogger<RedisDistributedLock>();
        var unreachableOptions = StackExchange.Redis.ConfigurationOptions.Parse("127.0.0.1:1");
        unreachableOptions.AbortOnConnectFail = false;
        unreachableOptions.ConnectTimeout = 300;
        unreachableOptions.SyncTimeout = 300;
        var unreachableConnection = StackExchange.Redis.ConnectionMultiplexer.Connect(unreachableOptions);
        var spyLock = new SpyDistributedLock(new RedisDistributedLock(unreachableConnection, logger));

        await CreateService(spyLock).AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);

        spyLock.AcquireResults.Should().Equal(LockResult.RedisUnavailable);
        spyLock.ReleaseCallCount.Should().Be(0, "RedisUnavailable 代表本來就沒有真的鎖，不應嘗試釋放");
        logger.LoggedLevels.Should().Contain(Microsoft.Extensions.Logging.LogLevel.Warning);
        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "Redis 不可用時仍 MUST 照常執行本輪的完整活動掃描與入場推進");
    }

    // PQLE-009（服務層）：兩個服務實例共用同一個真實 Redis；先故障、fail-open 執行，
    // 恢復連線後不需重啟即可恢復正常互斥。
    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_AfterRedisOutageRecovers_ResumesNormalMutualExclusionWithoutRestart()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        var firstWaiting = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        var lockA = new SpyDistributedLock(CreateRedisDistributedLock());
        var lockB = new SpyDistributedLock(CreateRedisDistributedLock());
        // maxConcurrentAdmittedBuyers: 2（而非預設 1）——刻意留一個空位給下面第二輪新增的等待紀錄，
        // 讓「哪個實例真正執行了推進」不只能從鎖結果推論，還能直接從 DB 的可觀察效果驗證：只有真正
        // 執行 AdvanceQueueOnceAsync 的那個實例（取得鎖的一方）才會讓這筆新紀錄轉為 Admitted。
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
            (await ReadStatusAsync(firstWaiting.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "故障期間兩實例皆 fail-open 執行");
        }
        finally
        {
            await _redisFixture.StartContainerAsync();
        }

        await WaitUntilRedisReconnectedAsync();

        // 留一個空位（maxConcurrentAdmittedBuyers: 2，firstWaiting 已佔用 1 個），新增一筆等待紀錄——
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
        (await ReadStatusAsync(secondWaiting.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted,
            "不只驗證鎖結果，直接驗證業務不變量：真正執行推進的只有取得鎖的那個實例，這筆新等待紀錄才會被推進");
    }

    // PQLE-006a 觸發面（TTL 到期但原持有者仍在執行中）：涵蓋 spec.md PQLE-006a 與 tasks.md 5.4。
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
            "兩個實例的推進邏輯重疊執行期間，最終有效入場人數 MUST NOT 超過上限，由既有資料庫悲觀鎖保證");
    }

    // PQLE-008 觸發面（Redis 不可用）：涵蓋 spec.md PQLE-008 與 tasks.md 5.4。
    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_WhenRedisUnavailable_BothInstancesExecuteButAdmissionStaysWithinLimit()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext, maxConcurrentAdmittedBuyers: 1);
        await PurchaseQueueLeaderElectionTestData.SeedManyWaitingEntriesAsync(dbContext, eventId, count: 5);

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

            (await ReadAdmittedCountAsync(eventId)).Should().Be(1,
                "即使兩個實例都因 Redis 不可用而各自執行推進，資料庫悲觀鎖仍保證不超額入場（呼應既有 PQ-ADMIT-004）");
        }
        finally
        {
            await _redisFixture.StartContainerAsync();
        }

        await WaitUntilRedisReconnectedAsync();
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
