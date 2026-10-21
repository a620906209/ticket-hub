using System.Diagnostics;
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

    // WSL2 VM 牆上時鐘實測約每 20–30 秒回跳約 1–3 秒，Redis 以牆上時鐘計算到期，key 實際存活時間可能比 TTL 長這麼多
    // （同一問題見 QueryCacheTtlSafetyNetTests）。
    private static readonly TimeSpan ClockStepBackTolerance = TimeSpan.FromSeconds(6);

    // PEXPIRETIME（Redis 7）回傳到期的 Unix 毫秒；key 不存在回 -2、沒有 TTL 回 -1。
    private const long RedisKeyMissing = -2;
    private const long RedisNoExpiry = -1;
    private static readonly TimeSpan RedisMillisecondTruncation = TimeSpan.FromMilliseconds(1);

    // 牆上時鐘前進速度相對單調時鐘的上限倍率（WSL2 實測偏快 2–3%，取 5%）。牆上時鐘另會往前跳（實測約 60ms），往前跳會反映在
    // 窗口內牆上經過時間，故容許的前進量取「單調經過時間 × 倍率」與「牆上經過時間」的較大者。
    private const double MaxWallClockRate = 1.05;

    private readonly record struct RedisClockReading(long UnixMilliseconds, long StopwatchTimestamp);

    // Stopwatch 先於 TIME 取得，讓之後算出的單調經過時間涵蓋整段牆上經過時間；連線由呼叫端事先建好，建連時間才不會放大容許前進量、放寬區間。
    private static async Task<RedisClockReading> ReadClockBeforeWriteAsync(IDatabase database)
    {
        var stopwatchTimestamp = Stopwatch.GetTimestamp();
        return new RedisClockReading(await ReadRedisUnixMillisecondsAsync(database), stopwatchTimestamp);
    }

    private static async Task<long> ReadRedisUnixMillisecondsAsync(IDatabase database)
    {
        var reply = (RedisResult[])(await database.ExecuteAsync("TIME"))!;
        return (long)reply[0] * 1000 + (long)reply[1] / 1000;
    }

    /// <summary>
    /// 確認 Redis 記錄的 TTL 等於要求值。Redis 以寫入當下的牆上時鐘加 TTL 記錄到期時刻，因此「到期時刻 − 要求的 TTL」即寫入時刻；
    /// TTL 設錯時這個反推值會偏離真正的寫入時刻。不能只要求它落在寫入前後兩次 Redis TIME 之間：回跳 δ 落在寫入前會讓寫入時刻
    /// 早於寫入前 TIME，落在寫入後會讓讀取後 TIME 早於寫入時刻，若各自放寬 δ，TTL 偏差 δ 以內的錯誤（例如 ×2、÷10）都會通過。
    /// 改為要求反推值落在兩個窄區間之一：回跳不在寫入前時，寫入時刻在寫入前 TIME 之後、前進量以內；回跳不在寫入後時，
    /// 寫入時刻在讀取後 TIME 之前、前進量以內。窗口內至多一次回跳時真正的寫入時刻必在其一，TTL 設錯則須偏差恰好約等於 δ 才會通過。
    /// 補上 <see cref="WaitUntilKeyExpiredAsync"/> 放寬後抓不到的「TTL 被設得比要求長」錯誤，也抓「鎖提早過期、失去互斥」錯誤。
    /// </summary>
    private static async Task AssertKeyTtlWithinRequestedAsync(IDatabase database, string key, TimeSpan ttl, RedisClockReading beforeWrite)
    {
        var expireAt = (long)await database.ExecuteAsync("PEXPIRETIME", key);
        expireAt.Should().NotBe(RedisKeyMissing, "取得鎖後到讀取 TTL 之間鎖不應已過期");
        expireAt.Should().NotBe(RedisNoExpiry, "鎖必須設定 TTL，持有者當機時才會自動釋放");
        var afterRead = await ReadRedisUnixMillisecondsAsync(database);
        var monotonicElapsed = Stopwatch.GetElapsedTime(beforeWrite.StopwatchTimestamp);
        var wallElapsed = TimeSpan.FromMilliseconds(afterRead - beforeWrite.UnixMilliseconds);
        var maxWallAdvance = (wallElapsed > monotonicElapsed * MaxWallClockRate ? wallElapsed : monotonicElapsed * MaxWallClockRate) + RedisMillisecondTruncation;

        var inferredWriteAt = expireAt - (long)ttl.TotalMilliseconds;
        var maxWallAdvanceMilliseconds = (long)Math.Ceiling(maxWallAdvance.TotalMilliseconds);
        var truncationMilliseconds = (long)RedisMillisecondTruncation.TotalMilliseconds;
        var isNearBeforeWrite = inferredWriteAt >= beforeWrite.UnixMilliseconds - truncationMilliseconds
            && inferredWriteAt <= beforeWrite.UnixMilliseconds + maxWallAdvanceMilliseconds;
        var isNearAfterRead = inferredWriteAt >= afterRead - maxWallAdvanceMilliseconds
            && inferredWriteAt <= afterRead + truncationMilliseconds;
        (isNearBeforeWrite || isNearAfterRead).Should().BeTrue(
            "鎖的 TTL 必須等於呼叫端要求的值（太短會提早失去互斥，太長則持有者當機後遲遲不釋放）：到期時刻 − TTL = {0}，應在寫入前 TIME {1} 之後或讀取後 TIME {2} 之前 {3}ms 內",
            inferredWriteAt, beforeWrite.UnixMilliseconds, afterRead, maxWallAdvanceMilliseconds);
    }

    /// <summary>
    /// 輪詢直到 Redis 自身已淘汰該 key，且必須在 TTL + 時鐘回跳餘裕內完成。不用固定 Task.Delay：時鐘回跳時
    /// 固定等待可能不足以跨過 TTL 而誤判（flaky）。
    /// </summary>
    private static async Task WaitUntilKeyExpiredAsync(IDatabase database, string key, TimeSpan ttl)
    {
        // Stopwatch 為單調時鐘，不受牆上時鐘回跳影響。
        var stopwatch = Stopwatch.StartNew();
        // 每次觀察（含觀察到已淘汰的那一次）都檢查期限；只在 key 仍存在時檢查，會放過「超過期限後才觀察到淘汰」。
        while (true)
        {
            var isKeyPresent = await database.KeyExistsAsync(key);
            stopwatch.Elapsed.Should().BeLessThan(ttl + ClockStepBackTolerance, "鎖必須在 TTL（加上時鐘回跳餘裕）內由 Redis 淘汰");
            if (!isKeyPresent)
            {
                return;
            }
            await Task.Delay(100);
        }
    }

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
        var ttl = TimeSpan.FromSeconds(1);
        using var connection = _fixture.CreateConnection();
        using var holderConnection = _fixture.CreateConnection();
        using var otherCallerConnection = _fixture.CreateConnection();
        var database = connection.GetDatabase();
        var beforeWrite = await ReadClockBeforeWriteAsync(database);
        var holderResult = await CreateLock(holderConnection).TryAcquireAsync(key, ttl, CancellationToken.None);
        holderResult.LockResult.Should().Be(LockResult.Acquired, "前提：持有者須先取得鎖，否則後續 TTL 檢查的失敗訊息會誤指向鎖提早過期");
        await AssertKeyTtlWithinRequestedAsync(database, key, ttl, beforeWrite);

        await WaitUntilKeyExpiredAsync(database, key, ttl);

        var result = await CreateLock(otherCallerConnection).TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);
        result.LockResult.Should().Be(LockResult.Acquired, "TTL 到期後鎖應自動釋放，不需任何實例手動介入");
    }

    [Fact]
    public async Task ReleaseAsync_WithStaleOwnerTokenAfterAnotherCallerAcquiredNewLock_IsNoOp()
    {
        var key = NewKey();
        using var connectionA = _fixture.CreateConnection();
        using var connectionB = _fixture.CreateConnection();
        var lockA = CreateLock(connectionA);
        var lockB = CreateLock(connectionB);

        var ttl = TimeSpan.FromSeconds(1);
        using var connection = _fixture.CreateConnection();
        var database = connection.GetDatabase();
        var beforeWrite = await ReadClockBeforeWriteAsync(database);
        var resultA = await lockA.TryAcquireAsync(key, ttl, CancellationToken.None);
        await AssertKeyTtlWithinRequestedAsync(database, key, ttl, beforeWrite);
        await WaitUntilKeyExpiredAsync(database, key, ttl);
        var resultB = await lockB.TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);
        resultB.LockResult.Should().Be(LockResult.Acquired, "前提：A 的鎖已逾時，B 應該能取得新鎖");

        // A 的釋放操作「遲到」——這時鎖的持有者其實已經是 B。
        await lockA.ReleaseAsync(key, resultA.OwnerToken!, CancellationToken.None);

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
        // 以實際經過時間計算逾時：每次取鎖在 Redis 不可用時本身就要等連線逾時（約 5 秒），
        // 只累加 pollIntervalMs 會讓名義上 15 秒的上限實際拖到數分鐘。Stopwatch 為單調時鐘。
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var result = await distributedLock.TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);
            if (result.LockResult == LockResult.Acquired)
            {
                return result;
            }

            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                return result;
            }

            await Task.Delay(pollIntervalMs);
        }
    }
}
