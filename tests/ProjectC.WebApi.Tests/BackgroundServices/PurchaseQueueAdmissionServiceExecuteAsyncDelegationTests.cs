using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.BackgroundServices;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.BackgroundServices;

// purchase-queue-leader-election tasks.md 5.6：驗證 ExecuteAsync（正式輪詢迴圈）真的委派至
// AdvanceQueueOnceWithLeaderElectionAsync，而非只測新方法本身——5.3 只直接呼叫新方法，無法證明
// 這件事；若實作只新增方法卻忘記改 ExecuteAsync，5.1~5.4 仍可能全數通過，但正式輪詢完全不會經過
// 分散式鎖。一律透過 IHostedService.StartAsync 啟動真正的服務，不直接呼叫 private ExecuteAsync
// 或既有的 AdvanceQueueOnceAsync／AdvanceQueueOnceWithLeaderElectionAsync。
public class PurchaseQueueAdmissionServiceExecuteAsyncDelegationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PurchaseQueueAdmissionServiceExecuteAsyncDelegationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private PurchaseQueueAdmissionService CreateService(FakeDistributedLock distributedLock, int maxConcurrentAdmittedBuyers = 1)
        => new(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<IDateTimeProvider>(),
            new PurchaseQueueOptions
            {
                MaxConcurrentAdmittedBuyers = maxConcurrentAdmittedBuyers,
                AdmissionTtlSeconds = 300,
                PollingIntervalSeconds = 1,
            },
            distributedLock,
            new DistributedLockOptions(),
            NullLogger<PurchaseQueueAdmissionService>.Instance);

    private async Task<PurchaseQueueEntry> SeedWaitingEntryAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        return await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));
    }

    private async Task<PurchaseQueueEntryStatus> ReadStatusAsync(Guid entryId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entry = await dbContext.PurchaseQueueEntries.AsNoTracking().SingleAsync(e => e.Id == entryId);
        return entry.Status;
    }

    [Fact]
    public async Task ExecuteAsync_ThroughRealStartAsync_ActuallyCallsDistributedLockTryAcquireAsync()
    {
        var fakeLock = new FakeDistributedLock { NextResult = LockResult.HeldByOther };
        var service = CreateService(fakeLock);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            await WaitUntilAsync(() => fakeLock.AcquireCalls.Count >= 1,
                "ExecuteAsync 應該已改為呼叫 AdvanceQueueOnceWithLeaderElectionAsync，而非仍呼叫舊的 AdvanceQueueOnceCoreAsync／AdvanceQueueOnceAsync（否則 TryAcquireAsync 永遠不會被呼叫）");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ThroughRealStartAsync_WhenLockHeldByOther_NeverAdvancesTheSeededEntry()
    {
        var waiting = await SeedWaitingEntryAsync();
        var fakeLock = new FakeDistributedLock { NextResult = LockResult.HeldByOther };
        var service = CreateService(fakeLock);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            await WaitUntilAsync(() => fakeLock.AcquireCalls.Count >= 3, "至少確認連續幾輪都被跳過，不是單一輪的巧合");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        fakeLock.ReleaseCalls.Should().BeEmpty("鎖被其他實例持有時，任何一輪都不應該呼叫 ReleaseAsync");
        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "鎖被其他實例持有的每一輪都不應該觸發任何推進");
    }

    [Fact]
    public async Task ExecuteAsync_ThroughRealStartAsync_WhenLockAcquired_ExecutesAdvanceAndReleasesEachRound()
    {
        var waiting = await SeedWaitingEntryAsync();
        var fakeLock = new FakeDistributedLock { NextResult = LockResult.Acquired };
        var service = CreateService(fakeLock);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            await WaitUntilAsync(() => fakeLock.ReleaseCalls.Count >= 1, "取得鎖時應該執行推進並在完成後釋放");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted);
        fakeLock.AcquireCalls.Count.Should().BeGreaterThanOrEqualTo(fakeLock.ReleaseCalls.Count, "每次釋放都必須有對應的一次取得");
    }

    [Fact]
    public async Task ExecuteAsync_ThroughRealStartAsync_WhenRedisUnavailable_ExecutesAdvanceButNeverReleases()
    {
        var waiting = await SeedWaitingEntryAsync();
        var fakeLock = new FakeDistributedLock { NextResult = LockResult.RedisUnavailable };
        var service = CreateService(fakeLock);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            await WaitUntilAsync(
                async () => await ReadStatusAsync(waiting.Id) == PurchaseQueueEntryStatus.Admitted,
                "Redis 不可用時仍應照常執行本輪推進（fail-open）");

            await WaitUntilAsync(() => fakeLock.AcquireCalls.Count >= 3, "至少確認連續幾輪都是 RedisUnavailable，不是單一輪的巧合");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        fakeLock.ReleaseCalls.Should().BeEmpty("RedisUnavailable 代表本來就沒有真的鎖，任何一輪都不應該呼叫 ReleaseAsync");
    }

    [Fact]
    public async Task StopAsync_StopsWithinReasonableTimeWithoutHanging()
    {
        var fakeLock = new FakeDistributedLock { NextResult = LockResult.Acquired };
        var service = CreateService(fakeLock);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await WaitUntilAsync(() => fakeLock.AcquireCalls.Count >= 1, "先確認至少跑過一輪，才有意義驗證停止行為");

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var act = () => service.StopAsync(stopCts.Token);

        await act.Should().NotThrowAsync("既有的 catch (OperationCanceledException) 例外處理與 Task.Delay 節奏不應該被破壞，StopAsync 應在合理時間內正常完成");
    }

    /// <summary>輪詢等待條件成立，比照 BackgroundServiceCycleLevelFailureTraceIdTests.WaitUntilAsync
    /// 的既定手法——背景服務的執行本質上是非同步、非固定時間的，逾時直接拋出明確例外。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string timeoutMessage, int timeoutMs = 10000, int pollIntervalMs = 25)
        => await WaitUntilAsync(() => Task.FromResult(condition()), timeoutMessage, timeoutMs, pollIntervalMs);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string timeoutMessage, int timeoutMs = 10000, int pollIntervalMs = 25)
    {
        var elapsed = 0;
        while (!await condition())
        {
            if (elapsed >= timeoutMs)
            {
                throw new TimeoutException($"等待條件在 {timeoutMs}ms 內未成立：{timeoutMessage}");
            }

            await Task.Delay(pollIntervalMs);
            elapsed += pollIntervalMs;
        }
    }
}
