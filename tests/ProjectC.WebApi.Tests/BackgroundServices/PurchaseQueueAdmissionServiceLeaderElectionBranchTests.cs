using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.BackgroundServices;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.BackgroundServices;

// purchase-queue-leader-election spec：AdvanceQueueOnceWithLeaderElectionAsync 對 IDistributedLock
// 三態回傳的分支行為（PQLE-001／PQLE-007，見 tasks.md 5.1）。用假的 IDistributedLock，不連真實 Redis
// ——只驗證分支邏輯本身，真正的多實例互斥／Redis 故障情境見
// PurchaseQueueAdmissionServiceLeaderElectionTests（使用真實 Redis）。
public class PurchaseQueueAdmissionServiceLeaderElectionBranchTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PurchaseQueueAdmissionServiceLeaderElectionBranchTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private PurchaseQueueAdmissionService CreateService(FakeDistributedLock distributedLock, int maxConcurrentAdmittedBuyers = 2)
        => new(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<IDateTimeProvider>(),
            new PurchaseQueueOptions
            {
                MaxConcurrentAdmittedBuyers = maxConcurrentAdmittedBuyers,
                AdmissionTtlSeconds = 300,
                PollingIntervalSeconds = 5,
            },
            distributedLock,
            new DistributedLockOptions(),
            NullLogger<PurchaseQueueAdmissionService>.Instance);

    private async Task<(Guid EventId, PurchaseQueueEntry Waiting)> SeedWaitingEventAsync(ApplicationDbContext dbContext)
    {
        var venue = new Venue(Guid.NewGuid(), $"Test Venue {Guid.NewGuid():N}");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        var @event = new Event(Guid.NewGuid(), "Test Event", DateTime.UtcNow.AddDays(30), venue.Id, seatMap.Id);
        @event.EnableQueueMode();
        var member = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        var entry = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, member.Id, DateTime.UtcNow.AddMinutes(-10));

        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        dbContext.Events.Add(@event);
        dbContext.Members.Add(member);
        dbContext.PurchaseQueueEntries.Add(entry);
        await dbContext.SaveChangesAsync();

        return (@event.Id, entry);
    }

    private async Task<PurchaseQueueEntryStatus> ReadStatusAsync(Guid entryId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entry = await dbContext.PurchaseQueueEntries.AsNoTracking().SingleAsync(e => e.Id == entryId);
        return entry.Status;
    }

    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_WhenLockAcquired_ExecutesAdvanceAndReleasesLock()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var (_, waiting) = await SeedWaitingEventAsync(dbContext);

        var fakeLock = new FakeDistributedLock { NextResult = LockResult.Acquired };
        await CreateService(fakeLock).AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);

        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "取得鎖時應該照常執行本輪推進");
        fakeLock.AcquireCalls.Should().ContainSingle();
        fakeLock.ReleaseCalls.Should().ContainSingle("取得鎖並執行完畢後 MUST 釋放該鎖");
    }

    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_WhenLockHeldByOther_SkipsAdvanceAndDoesNotRelease()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var (_, waiting) = await SeedWaitingEventAsync(dbContext);

        var fakeLock = new FakeDistributedLock { NextResult = LockResult.HeldByOther };
        await CreateService(fakeLock).AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);

        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "鎖被其他實例持有時本輪不應執行任何推進邏輯");
        fakeLock.ReleaseCalls.Should().BeEmpty("未取得鎖不應嘗試釋放");
    }

    [Fact]
    public async Task AdvanceQueueOnceWithLeaderElectionAsync_WhenRedisUnavailable_ExecutesAdvanceButDoesNotRelease()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var (_, waiting) = await SeedWaitingEventAsync(dbContext);

        var fakeLock = new FakeDistributedLock { NextResult = LockResult.RedisUnavailable };
        await CreateService(fakeLock).AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None);

        (await ReadStatusAsync(waiting.Id)).Should().Be(
            PurchaseQueueEntryStatus.Admitted, "Redis 不可用時 MUST 視為已取得執行資格，照常執行本輪推進（fail-open）");
        fakeLock.ReleaseCalls.Should().BeEmpty("RedisUnavailable 代表本來就沒有真的鎖，不應嘗試釋放");
    }
}
