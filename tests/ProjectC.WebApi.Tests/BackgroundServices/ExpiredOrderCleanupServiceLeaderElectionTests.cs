using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Orders;
using ProjectC.Infrastructure.Security;
using ProjectC.WebApi.BackgroundServices;
using ProjectC.WebApi.Tests.TestSupport;
using StackExchange.Redis;

namespace ProjectC.WebApi.Tests.BackgroundServices;

// order-cleanup-leader-lock tasks.md 2.1：鎖包裝的呼叫順序與參數，以替身隔離 DB 與 Redis
// （真實 Redis／Postgres 的多實例行為見 Infrastructure.Tests，3.1）。
public class ExpiredOrderCleanupServiceLeaderElectionTests
{
    private const string LockKey = "order-cleanup:lock";

    // 每個測試共用一份事件序列，讓「取鎖 → 掃描 → 釋放」的先後順序可被斷言。
    private sealed class Harness
    {
        public List<string> Events { get; } = [];

        public Queue<LockResult> LockResults { get; } = new();

        public Exception? ScanException { get; set; }

        public Exception? AcquireException { get; set; }

        public Exception? ReleaseException { get; set; }

        public Mock<IDistributedLock> DistributedLock { get; } = new();

        public RecordingLogger<ExpiredOrderCleanupService> Logger { get; } = new();

        public List<(string Key, TimeSpan Ttl)> AcquireCalls { get; } = [];

        public List<(string Key, string OwnerToken)> ReleaseCalls { get; } = [];

        public List<CancellationToken> ReleaseTokens { get; } = [];

        public CancellationTokenSource? CancelDuringScan { get; set; }

        public string? IssuedOwnerToken { get; private set; }

        public ExpiredOrderCleanupService Create(int intervalSeconds = 300, int lockTtlMultiplier = 3)
        {
            DistributedLock
                .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, TimeSpan ttl, CancellationToken _) =>
                {
                    Events.Add("acquire");
                    AcquireCalls.Add((key, ttl));
                    if (AcquireException is not null)
                    {
                        throw AcquireException;
                    }

                    var result = LockResults.Count > 0 ? LockResults.Dequeue() : LockResult.Acquired;
                    IssuedOwnerToken = result == LockResult.Acquired ? Guid.NewGuid().ToString("N") : null;
                    return new LockAcquisitionResult(result, IssuedOwnerToken);
                });
            DistributedLock
                .Setup(l => l.ReleaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string key, string ownerToken, CancellationToken releaseToken) =>
                {
                    Events.Add("release");
                    ReleaseTokens.Add(releaseToken);
                    if (releaseToken.IsCancellationRequested)
                    {
                        return Task.FromCanceled(releaseToken);
                    }

                    ReleaseCalls.Add((key, ownerToken));
                    return ReleaseException is null ? Task.CompletedTask : Task.FromException(ReleaseException);
                });

            var orderRepository = new Mock<IOrderRepository>();
            orderRepository
                .Setup(r => r.GetExpiredPendingOrderIdsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    Events.Add("scan");
                    if (CancelDuringScan is not null)
                    {
                        CancelDuringScan.Cancel();
                        return Task.FromCanceled<IReadOnlyList<Guid>>(CancelDuringScan.Token);
                    }

                    return ScanException is null
                        ? Task.FromResult<IReadOnlyList<Guid>>([])
                        : Task.FromException<IReadOnlyList<Guid>>(ScanException);
                });

            var serviceProvider = new Mock<IServiceProvider>();
            serviceProvider.Setup(p => p.GetService(typeof(IOrderRepository))).Returns(orderRepository.Object);
            var scope = new Mock<IServiceScope>();
            scope.SetupGet(s => s.ServiceProvider).Returns(serviceProvider.Object);
            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

            return new ExpiredOrderCleanupService(
                scopeFactory.Object,
                new SystemDateTimeProvider(),
                new OrderCleanupOptions { IntervalSeconds = intervalSeconds },
                DistributedLock.Object,
                new DistributedLockOptions { LockTtlMultiplier = lockTtlMultiplier },
                Logger);
        }
    }

    // 對應 AC: OCLE-001
    [Fact]
    public async Task CleanupOnceWithLeaderElectionAsync_WhenLockAcquired_ScansAndReleasesWithOwnerTokenInOrder()
    {
        var harness = new Harness();
        var service = harness.Create();

        await service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);

        harness.Events.Should().Equal("acquire", "scan", "release");
        harness.ReleaseCalls.Should().ContainSingle()
            .Which.Should().Be((LockKey, harness.IssuedOwnerToken!));
        harness.Logger.LoggedLevels.Should().NotContain(LogLevel.Warning);
    }

    // 對應 AC: OCLE-001（key 與 TTL = Max(1, IntervalSeconds) × LockTtlMultiplier）
    [Theory]
    [InlineData(300, 3, 900)]
    [InlineData(0, 3, 3)]
    public async Task CleanupOnceWithLeaderElectionAsync_AcquiresFixedKeyWithTtlFromIntervalAndMultiplier(
        int intervalSeconds, int lockTtlMultiplier, int expectedTtlSeconds)
    {
        var harness = new Harness();
        var service = harness.Create(intervalSeconds, lockTtlMultiplier);

        await service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);

        harness.AcquireCalls.Should().ContainSingle()
            .Which.Should().Be((LockKey, TimeSpan.FromSeconds(expectedTtlSeconds)));
    }

    // 對應 AC: OCLE-002（未取得鎖的一方不掃描、不釋放）
    [Fact]
    public async Task CleanupOnceWithLeaderElectionAsync_WhenLockHeldByOther_SkipsWithoutScanningOrReleasing()
    {
        var harness = new Harness();
        harness.LockResults.Enqueue(LockResult.HeldByOther);
        var service = harness.Create();

        await service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);

        harness.Events.Should().Equal("acquire");
        harness.ReleaseCalls.Should().BeEmpty();
        harness.Logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Debug)
            .Which.Message.Should().Contain(LockKey);
    }

    // 對應 AC: OCLE-003
    [Fact]
    public async Task CleanupOnceWithLeaderElectionAsync_AfterSkippedRound_TriesAcquireAgainNextRound()
    {
        var harness = new Harness();
        harness.LockResults.Enqueue(LockResult.HeldByOther);
        harness.LockResults.Enqueue(LockResult.Acquired);
        var service = harness.Create();

        await service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);
        await service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);

        harness.Events.Should().Equal("acquire", "acquire", "scan", "release");
    }

    // 對應 AC: OCLE-004
    [Fact]
    public async Task CleanupOnceWithLeaderElectionAsync_WhenScanThrows_StillReleasesLockAndPropagates()
    {
        var harness = new Harness { ScanException = new InvalidOperationException("simulated scan failure") };
        var service = harness.Create();

        var act = () => service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated scan failure");
        harness.Events.Should().Equal("acquire", "scan", "release");
        harness.ReleaseCalls.Should().ContainSingle().Which.OwnerToken.Should().Be(harness.IssuedOwnerToken);
    }

    // 對應 AC: OCLE-004（取消屬例外路徑之一：關機取消時仍須釋放，否則鎖要等 TTL 才失效）
    [Fact]
    public async Task CleanupOnceWithLeaderElectionAsync_WhenCancelledDuringScan_StillReleasesLockWithUncancelledToken()
    {
        using var cancellationSource = new CancellationTokenSource();
        var harness = new Harness { CancelDuringScan = cancellationSource };
        var service = harness.Create();

        var act = () => service.CleanupOnceWithLeaderElectionAsync(cancellationSource.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        harness.Events.Should().Equal("acquire", "scan", "release");
        harness.ReleaseTokens.Should().ContainSingle().Which.IsCancellationRequested.Should().BeFalse("關機取消不得讓釋放跟著被取消，否則鎖會殘留到 TTL 到期");
    }

    // 對應 AC: OCLE-007（fail-open：照跑、記 Warning、無 owner token 故不釋放）
    [Fact]
    public async Task CleanupOnceWithLeaderElectionAsync_WhenRedisUnavailable_StillScansLogsWarningAndDoesNotRelease()
    {
        var harness = new Harness();
        harness.LockResults.Enqueue(LockResult.RedisUnavailable);
        var service = harness.Create();

        await service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);

        harness.Events.Should().Equal("acquire", "scan");
        harness.ReleaseCalls.Should().BeEmpty();
        harness.Logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning)
            .Which.Message.Should().Contain(LockKey);
    }

    // 對應 AC: OCLE-007b（RedisServerException 不被共用鎖攔截，由服務端 fail-open）
    [Fact]
    public async Task CleanupOnceWithLeaderElectionAsync_WhenAcquireThrowsNonConnectionRedisException_StillScansAndLogsWarning()
    {
        var harness = new Harness { AcquireException = new RedisServerException("READONLY You can't write against a read only replica.") };
        var service = harness.Create();

        await service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);

        harness.Events.Should().Equal("acquire", "scan");
        harness.ReleaseCalls.Should().BeEmpty();
        harness.Logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning)
            .Which.Message.Should().Contain(LockKey);
    }

    // 對應 AC: OCLE-007c（釋放失敗不遮蔽清理例外；Warning 不含 owner token）
    [Fact]
    public async Task CleanupOnceWithLeaderElectionAsync_WhenScanThrowsAndReleaseThrows_PropagatesScanException()
    {
        var harness = new Harness
        {
            ScanException = new InvalidOperationException("simulated scan failure"),
            ReleaseException = new RedisServerException("simulated release failure"),
        };
        var service = harness.Create();

        var act = () => service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated scan failure");
        var warning = harness.Logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning).Subject;
        warning.Message.Should().NotContain(harness.IssuedOwnerToken!);
        warning.Properties.Select(property => property.Value?.ToString()).Should().NotContain(harness.IssuedOwnerToken);
    }

    // 對應 AC: OCLE-007c（清理成功的輪次不被記為失敗）
    [Fact]
    public async Task CleanupOnceWithLeaderElectionAsync_WhenScanSucceedsAndReleaseThrows_DoesNotThrowAndLogsWarning()
    {
        var harness = new Harness { ReleaseException = new RedisServerException("simulated release failure") };
        var service = harness.Create();

        var act = () => service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        harness.Events.Should().Equal("acquire", "scan", "release");
        harness.Logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning);
    }

    // 對應 AC: OCLE-008（Redis 恢復後下一輪即回復互斥，不需重啟）
    [Fact]
    public async Task CleanupOnceWithLeaderElectionAsync_AfterRedisRecovers_ResumesLockExclusion()
    {
        var harness = new Harness();
        harness.LockResults.Enqueue(LockResult.RedisUnavailable);
        harness.LockResults.Enqueue(LockResult.HeldByOther);
        var service = harness.Create();

        await service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);
        await service.CleanupOnceWithLeaderElectionAsync(CancellationToken.None);

        harness.Events.Should().Equal("acquire", "scan", "acquire");
    }
}
