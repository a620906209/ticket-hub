using FluentAssertions;
using Microsoft.Extensions.Logging;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Infrastructure.DistributedLocking;
using ProjectC.Infrastructure.Tests.TestSupport;
using StackExchange.Redis;

namespace ProjectC.Infrastructure.Tests.DistributedLocking;

// purchase-queue-leader-election spec：分散式鎖元件層本身的正確性（PQLE-001~009 元件層子項，
// 見 tasks.md 5.2）。不經過 PurchaseQueueAdmissionService，直接測 TryAcquireAsync／ReleaseAsync。
[Collection(RedisCollection.Name)]
public class RedisDistributedLockTests
{
    private readonly RedisFixture _fixture;

    public RedisDistributedLockTests(RedisFixture fixture)
    {
        _fixture = fixture;
    }

    private RedisDistributedLock CreateLock(IConnectionMultiplexer? connectionMultiplexer = null, ILogger<RedisDistributedLock>? logger = null)
        => new(connectionMultiplexer ?? _fixture.CreateConnection(), logger ?? new RecordingLogger<RedisDistributedLock>());

    private static string NewKey() => $"pqle-test:{Guid.NewGuid():N}";

    [Fact]
    public async Task TryAcquireAsync_SingleCaller_ReturnsAcquired()
    {
        var key = NewKey();
        var result = await CreateLock().TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);

        result.LockResult.Should().Be(LockResult.Acquired);
        result.OwnerToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TryAcquireAsync_TwoCallersSameKeyConcurrently_OnlyOneAcquires_OtherCanAcquireAfterRelease()
    {
        var key = NewKey();
        var lockA = CreateLock();
        var lockB = CreateLock();

        var results = await Task.WhenAll(
            lockA.TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None),
            lockB.TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None));

        results.Should().ContainSingle(r => r.LockResult == LockResult.Acquired,
            "同一時間只能有一個呼叫端取得同一把鎖");
        var loser = results.Single(r => r.LockResult == LockResult.HeldByOther);
        loser.OwnerToken.Should().BeNull("未取得鎖時不應該有 OwnerToken");

        var winner = results.Single(r => r.LockResult == LockResult.Acquired);
        await (winner == results[0] ? lockA : lockB).ReleaseAsync(key, winner.OwnerToken!, CancellationToken.None);

        var retryResult = await lockB.TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);
        retryResult.LockResult.Should().Be(LockResult.Acquired, "鎖釋放後，先前取得失敗的一方應該可以重新取得");
    }

    [Fact]
    public async Task ReleaseAsync_AfterNormalAcquire_AllowsImmediateReacquisitionByAnotherCaller()
    {
        var key = NewKey();
        var distributedLock = CreateLock();
        var acquireResult = await distributedLock.TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);

        await distributedLock.ReleaseAsync(key, acquireResult.OwnerToken!, CancellationToken.None);

        var reacquireResult = await CreateLock().TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);
        reacquireResult.LockResult.Should().Be(LockResult.Acquired);
    }

    [Fact]
    public async Task TryAcquireAsync_AfterTtlExpiresWithoutRelease_AllowsOtherCallerToAcquire()
    {
        var key = NewKey();
        await CreateLock().TryAcquireAsync(key, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(600));

        var result = await CreateLock().TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);
        result.LockResult.Should().Be(LockResult.Acquired, "TTL 到期後鎖應自動釋放，不需任何實例手動介入");
    }

    [Fact]
    public async Task ReleaseAsync_WithStaleOwnerTokenAfterAnotherCallerAcquiredNewLock_IsNoOp()
    {
        var key = NewKey();
        var lockA = CreateLock();
        var lockB = CreateLock();

        var resultA = await lockA.TryAcquireAsync(key, TimeSpan.FromMilliseconds(300), CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        var resultB = await lockB.TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);
        resultB.LockResult.Should().Be(LockResult.Acquired, "前提：A 的鎖已逾時，B 應該能取得新鎖");

        // A 的釋放操作「遲到」——這時鎖的持有者其實已經是 B。
        await lockA.ReleaseAsync(key, resultA.OwnerToken!, CancellationToken.None);

        var database = _fixture.CreateConnection().GetDatabase();
        var currentValue = await database.StringGetAsync(key);
        currentValue.ToString().Should().Be(resultB.OwnerToken, "A 的遲到釋放不得誤刪 B 目前持有的鎖");
    }

    [Fact]
    public async Task TryAcquireAsync_WhenRedisUnreachable_ReturnsRedisUnavailableWithoutThrowingAndLogsWarning()
    {
        // 指向真實但無人監聽的 port，模擬連線失敗；ConnectTimeout／SyncTimeout 調短避免測試被拖慢。
        var options = ConfigurationOptions.Parse("127.0.0.1:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 300;
        options.SyncTimeout = 300;
        var unreachableConnection = ConnectionMultiplexer.Connect(options);
        var logger = new RecordingLogger<RedisDistributedLock>();

        var result = await CreateLock(unreachableConnection, logger).TryAcquireAsync(NewKey(), TimeSpan.FromSeconds(30), CancellationToken.None);

        result.LockResult.Should().Be(LockResult.RedisUnavailable);
        logger.LoggedLevels.Should().Contain(LogLevel.Warning, "無法連線 Redis 時 MUST 記錄可觀察的 Warning 等級 log（PQLE-007）");
    }

    // hardener 檢查清單 6️⃣／8️⃣：ReleaseAsync 對 Redis 連線例外是「情境 B」（post-commit best-effort，
    // 見 design.md 決策 3）——連線失敗時 MUST NOT 拋出例外、MUST 記錄 Warning，不得靜默失敗，
    // 也不能讓呼叫端（AdvanceQueueOnceWithLeaderElectionAsync）的 finally 區塊因此中斷。
    [Fact]
    public async Task ReleaseAsync_WhenRedisUnreachable_DoesNotThrowAndLogsWarning()
    {
        var key = NewKey();
        var acquireResult = await CreateLock().TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);

        var options = ConfigurationOptions.Parse("127.0.0.1:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 300;
        options.SyncTimeout = 300;
        var unreachableConnection = ConnectionMultiplexer.Connect(options);
        var logger = new RecordingLogger<RedisDistributedLock>();

        var act = () => CreateLock(unreachableConnection, logger).ReleaseAsync(key, acquireResult.OwnerToken!, CancellationToken.None);

        await act.Should().NotThrowAsync("連線失敗時釋放操作 MUST NOT 拋出例外，不得影響呼叫端已完成的推進結果");
        logger.LoggedLevels.Should().Contain(LogLevel.Warning, "釋放失敗 MUST 記錄可觀察的 Warning，不是靜默失敗");
    }

    [Fact]
    public async Task TryAcquireAsync_AfterRedisRecoversFromOutage_ReturnsToNormalBehaviorWithoutRecreatingConnection()
    {
        // 使用共用 fixture 的容器本身模擬真實故障後恢復（停止／重新啟動，連線位址不變），
        // 而非另外建立、丟棄容器——比「連不到的假位址」更貼近 PQLE-009 描述的真實情境。
        var key = NewKey();
        var connection = _fixture.CreateConnection();
        var distributedLock = CreateLock(connection);

        await _fixture.StopContainerAsync();
        try
        {
            var duringOutage = await distributedLock.TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);
            duringOutage.LockResult.Should().Be(LockResult.RedisUnavailable);
        }
        finally
        {
            await _fixture.StartContainerAsync();
        }

        // StackExchange.Redis 背景重連需要一點時間，比照既有測試的輪詢等待手法（見
        // BackgroundServiceCycleLevelFailureTraceIdTests.WaitUntilAsync）而非固定 Task.Delay 賭時間點。
        var recoveredResult = await WaitForAcquiredAsync(distributedLock, key);
        recoveredResult.LockResult.Should().Be(LockResult.Acquired, "Redis 恢復連線後，不需重新建立連線即可恢復正常互斥行為");
    }

    private static async Task<LockAcquisitionResult> WaitForAcquiredAsync(
        IDistributedLock distributedLock, string key, int timeoutMs = 15000, int pollIntervalMs = 200)
    {
        var elapsed = 0;
        while (true)
        {
            var result = await distributedLock.TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);
            if (result.LockResult == LockResult.Acquired)
            {
                return result;
            }

            if (elapsed >= timeoutMs)
            {
                return result;
            }

            await Task.Delay(pollIntervalMs);
            elapsed += pollIntervalMs;
        }
    }
}
