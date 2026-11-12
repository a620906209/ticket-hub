using System.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Infrastructure.DistributedLocking;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Security;
using ProjectC.Infrastructure.Tests.TestSupport;
using ProjectC.WebApi.BackgroundServices;
using StackExchange.Redis;

namespace ProjectC.Infrastructure.Tests.Orders;

// order-cleanup-leader-lock tasks.md 3.1：兩個 ExpiredOrderCleanupService 實例共用真實 Redis 鎖與真實 PostgreSQL。
// 掃描回傳訂單編號後才停在可控的閘門上，讓「A 持鎖處理到一半」是確定的狀態，而不是靠執行緒排程碰巧重疊。
// Redis 用本類別專屬的容器（IClassFixture），不與其他測試類別共用固定的鎖 key。
[Collection(PostgresCollection.Name)]
public class ExpiredOrderCleanupLeaderElectionTests : IClassFixture<RedisFixture>
{
    private const string LockKey = "order-cleanup:lock";
    private const int InitialQuantity = 100;
    private const int OrderedQuantity = 3;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    // WSL2 VM 牆上時鐘實測會回跳數秒，Redis 以牆上時鐘計算到期（同 RedisDistributedLockTests）。
    private static readonly TimeSpan ClockStepBackTolerance = TimeSpan.FromSeconds(6);

    private readonly PostgresFixture _postgres;
    private readonly RedisFixture _redis;

    public ExpiredOrderCleanupLeaderElectionTests(PostgresFixture postgres, RedisFixture redis)
    {
        _postgres = postgres;
        _redis = redis;
    }

    private sealed class ScanProbe
    {
        private int _scanCount;

        public int ScanCount => Volatile.Read(ref _scanCount);

        public TaskCompletionSource ScanReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? Gate { get; init; }

        public async Task<IReadOnlyList<Guid>> ObserveAsync(IReadOnlyList<Guid> expiredOrderIds)
        {
            Interlocked.Increment(ref _scanCount);
            ScanReturned.TrySetResult();
            if (Gate is not null)
            {
                await Gate;
            }

            return expiredOrderIds;
        }
    }

    private sealed class CleanupInstance : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public CleanupInstance(ExpiredOrderCleanupService service, ScanProbe probe, ListLogger<ExpiredOrderCleanupService> logger, ServiceProvider provider)
        {
            Service = service;
            Probe = probe;
            Logger = logger;
            _provider = provider;
        }

        public ExpiredOrderCleanupService Service { get; }

        public ScanProbe Probe { get; }

        public ListLogger<ExpiredOrderCleanupService> Logger { get; }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    private CleanupInstance CreateInstance(IDistributedLock distributedLock, int intervalSeconds = 300, int lockTtlMultiplier = 3, Task? gate = null)
    {
        var probe = new ScanProbe { Gate = gate };
        var services = new ServiceCollection();
        services.AddScoped(_ => _postgres.CreateDbContext());
        services.AddScoped<OrderService>(provider => OrderServiceTestFactory.Create(provider.GetRequiredService<ApplicationDbContext>()));
        services.AddScoped<IOrderRepository>(provider =>
        {
            var realRepository = new OrderRepository(provider.GetRequiredService<ApplicationDbContext>());
            var observedRepository = new Mock<IOrderRepository>();
            observedRepository
                .Setup(repository => repository.GetExpiredPendingOrderIdsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns(async (DateTime now, CancellationToken cancellationToken) =>
                    await probe.ObserveAsync(await realRepository.GetExpiredPendingOrderIdsAsync(now, cancellationToken)));
            return observedRepository.Object;
        });
        var serviceProvider = services.BuildServiceProvider();

        var logger = new ListLogger<ExpiredOrderCleanupService>();
        var service = new ExpiredOrderCleanupService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new SystemDateTimeProvider(),
            new OrderCleanupOptions { IntervalSeconds = intervalSeconds },
            distributedLock,
            new DistributedLockOptions { LockTtlMultiplier = lockTtlMultiplier },
            logger);
        return new CleanupInstance(service, probe, logger, serviceProvider);
    }

    private RedisDistributedLock CreateRedisLock()
        => new(_redis.CreateConnection(), NullLogger<RedisDistributedLock>.Instance);

    private async Task<IDatabase> PrepareEmptyLockKeyAsync()
    {
        var database = _redis.CreateConnection().GetDatabase();
        await database.KeyDeleteAsync(LockKey);
        return database;
    }

    // 計數票種的訂單：庫存是否「只回補一次」能直接由可售數量觀察（回補兩次會變成 103）。
    private async Task<(Guid TicketTypeId, List<Guid> OrderIds)> SeedExpiredCountingOrdersAsync(int orderCount)
    {
        await using var dbContext = _postgres.CreateDbContext();
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 1);
        var ticketTypeId = await TicketingTestData.SeedCountBasedTicketTypeAsync(dbContext, eventId, availableQuantity: InitialQuantity);

        var orderIds = new List<Guid>();
        for (var index = 0; index < orderCount; index++)
        {
            var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
            dbContext.Members.Add(buyer);
            await dbContext.SaveChangesAsync();
            var placeResult = await OrderServiceTestFactory.Create(dbContext).PlaceOrderAsync(
                buyer.Id, new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketTypeId, OrderedQuantity)]), CancellationToken.None);
            placeResult.IsSuccess.Should().BeTrue();
            orderIds.Add(placeResult.Value);
        }

        foreach (var orderId in orderIds)
        {
            var order = await dbContext.Orders.SingleAsync(o => o.Id == orderId);
            dbContext.Entry(order).Property(o => o.HeldUntilUtc).CurrentValue = DateTime.UtcNow.AddMinutes(-1);
        }

        await dbContext.SaveChangesAsync();
        (await dbContext.TicketTypes.AsNoTracking().SingleAsync(t => t.Id == ticketTypeId)).AvailableQuantity
            .Should().Be(InitialQuantity - orderCount * OrderedQuantity, "前提：下單後庫存已扣除");
        return (ticketTypeId, orderIds);
    }

    private async Task AssertAllCancelledAndQuantityRestoredOnceAsync(Guid ticketTypeId, IReadOnlyList<Guid> orderIds)
    {
        await using var dbContext = _postgres.CreateDbContext();
        foreach (var orderId in orderIds)
        {
            (await dbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.Cancelled);
        }

        (await dbContext.TicketTypes.AsNoTracking().SingleAsync(t => t.Id == ticketTypeId)).AvailableQuantity
            .Should().Be(InitialQuantity, "每筆訂單的數量只能回補一次，不得多回補");
    }

    private static List<LogEntry> WarningsMentioning(CleanupInstance instance, IReadOnlyList<Guid> orderIds)
        => instance.Logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning && orderIds.Any(orderId => entry.Message.Contains(orderId.ToString(), StringComparison.Ordinal)))
            .ToList();

    private static async Task WaitUntilKeyExpiredAsync(IDatabase database, TimeSpan ttl)
    {
        // Stopwatch 為單調時鐘，不受牆上時鐘回跳影響；不用單次固定 Task.Delay，時鐘回跳時固定等待可能不足以跨過 TTL。
        var stopwatch = Stopwatch.StartNew();
        while (await database.KeyExistsAsync(LockKey))
        {
            stopwatch.Elapsed.Should().BeLessThan(ttl + ClockStepBackTolerance, "鎖應在 TTL（加時鐘回跳餘裕）內被 Redis 淘汰");
            await Task.Delay(100);
        }
    }

    // 對應 AC: OCLE-002（A 持鎖處理到一半時 B 進入同一輪）
    [Fact]
    public async Task CleanupOnceWithLeaderElectionAsync_TwoInstancesOverlapping_OnlyOneProcessesAndQuantityRestoredOnce()
    {
        await PrepareEmptyLockKeyAsync();
        var (ticketTypeId, orderIds) = await SeedExpiredCountingOrdersAsync(orderCount: 3);
        var redisLock = CreateRedisLock();
        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var instanceA = CreateInstance(redisLock, gate: gateA.Task);
        await using var instanceB = CreateInstance(redisLock);

        var taskA = instanceA.Service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);
        try
        {
            await instanceA.Probe.ScanReturned.Task.WaitAsync(WaitTimeout);

            await instanceB.Service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);

            instanceB.Probe.ScanCount.Should().Be(0, "A 持有鎖，B 本輪不得掃描");
        }
        finally
        {
            gateA.TrySetResult();
        }

        await taskA.WaitAsync(WaitTimeout);

        instanceA.Probe.ScanCount.Should().Be(1);
        await AssertAllCancelledAndQuantityRestoredOnceAsync(ticketTypeId, orderIds);
        WarningsMentioning(instanceA, orderIds).Should().BeEmpty();
        WarningsMentioning(instanceB, orderIds).Should().BeEmpty("沒有重複取消，就不該出現 Conflict Warning");
        (await _redis.CreateConnection().GetDatabase().KeyExistsAsync(LockKey)).Should().BeFalse("A 正常結束後應釋放鎖");
    }

    // 對應 AC: OCLE-005
    [Fact]
    public async Task CleanupOnceWithLeaderElectionAsync_WhenHolderCrashesWithoutRelease_OtherInstanceProceedsAfterTtl()
    {
        var database = await PrepareEmptyLockKeyAsync();
        var (ticketTypeId, orderIds) = await SeedExpiredCountingOrdersAsync(orderCount: 1);
        var redisLock = CreateRedisLock();
        var crashedHolderTtl = TimeSpan.FromSeconds(12);
        (await redisLock.TryAcquireAsync(LockKey, crashedHolderTtl, CancellationToken.None)).LockResult
            .Should().Be(LockResult.Acquired, "前提：模擬當掉的持有者佔著鎖、不釋放");
        await using var instance = CreateInstance(redisLock);

        await instance.Service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);
        instance.Probe.ScanCount.Should().Be(0, "TTL 未到期前鎖仍被持有，應跳過");

        await WaitUntilKeyExpiredAsync(database, crashedHolderTtl);
        await instance.Service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);

        instance.Probe.ScanCount.Should().Be(1);
        await AssertAllCancelledAndQuantityRestoredOnceAsync(ticketTypeId, orderIds);
    }

    // 對應 AC: OCLE-006（只驗證鎖層行為：過期持有者的釋放不得釋放新持有者的鎖；重疊取消本身見 OrderServiceConcurrencyTests）
    [Fact]
    public async Task CleanupOnceWithLeaderElectionAsync_WhenLockExpiredWhileHolderStillRunning_OverlapDoesNotDoubleRelease()
    {
        var database = await PrepareEmptyLockKeyAsync();
        var (ticketTypeId, orderIds) = await SeedExpiredCountingOrdersAsync(orderCount: 1);
        var redisLock = CreateRedisLock();
        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A 的 TTL 只有 1 秒（會在處理中途過期）；B 的 TTL 夠長，確保 A 釋放後仍能檢查 B 的鎖還在。
        await using var instanceA = CreateInstance(redisLock, intervalSeconds: 1, lockTtlMultiplier: 1, gate: gateA.Task);
        await using var instanceB = CreateInstance(redisLock, intervalSeconds: 60, lockTtlMultiplier: 1, gate: gateB.Task);

        var taskA = instanceA.Service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);
        Task? taskB = null;
        try
        {
            await instanceA.Probe.ScanReturned.Task.WaitAsync(WaitTimeout);
            await WaitUntilKeyExpiredAsync(database, TimeSpan.FromSeconds(1));
            taskB = instanceB.Service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);
            await instanceB.Probe.ScanReturned.Task.WaitAsync(WaitTimeout);

            gateA.SetResult();
            await taskA.WaitAsync(WaitTimeout);

            (await database.KeyExistsAsync(LockKey)).Should().BeTrue("A 的鎖早已過期，A 的釋放不得刪掉 B 現在持有的鎖");
        }
        finally
        {
            gateA.TrySetResult();
            gateB.TrySetResult();
        }

        await taskB.WaitAsync(WaitTimeout);

        await AssertAllCancelledAndQuantityRestoredOnceAsync(ticketTypeId, orderIds);
        WarningsMentioning(instanceA, orderIds).Should().BeEmpty();
        WarningsMentioning(instanceB, orderIds).Should().ContainSingle("B 手上的訂單編號已被 A 取消，B 的取消應被拒絕而不是再回補一次");
        (await database.KeyExistsAsync(LockKey)).Should().BeFalse("B 正常結束後釋放自己的鎖");
    }
}
