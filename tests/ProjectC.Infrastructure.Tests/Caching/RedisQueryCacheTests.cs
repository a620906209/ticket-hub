using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using ProjectC.Infrastructure.Caching;
using ProjectC.Infrastructure.Tests.TestSupport;
using StackExchange.Redis;

namespace ProjectC.Infrastructure.Tests.Caching;

// query-caching design.md 決策 1／6a；tasks.md 1.5／1.6／1.6a。
[Collection(RedisCollection.Name)]
public class RedisQueryCacheTests
{
    private readonly RedisFixture _fixture;

    public RedisQueryCacheTests(RedisFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record SampleCacheItem(Guid Id, string Name);

    private RedisQueryCache CreateCache(IConnectionMultiplexer? connectionMultiplexer = null, ILogger<RedisQueryCache>? logger = null)
        => new(connectionMultiplexer ?? _fixture.CreateConnection(), logger ?? new RecordingLogger<RedisQueryCache>());

    private static string NewKey() => $"query-cache-test:{Guid.NewGuid():N}";

    // 用假的 IConnectionMultiplexer，讓 GetDatabase() 直接拋出指定型別的例外——這一步本身就在
    // RedisQueryCache 的 try 區塊內，效果等同於底層命令拋出同一種例外，但不需要仰賴真實網路逾時
    // 這種難以決定性重現的時序（tasks.md 1.6：「用會拋出例外的假連線」）。
    private static IConnectionMultiplexer CreateThrowingConnection(Exception exceptionToThrow)
    {
        var mock = new Mock<IConnectionMultiplexer>();
        mock.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Throws(exceptionToThrow);
        return mock.Object;
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_ReturnsSameObjectCorrectlyDeserialized()
    {
        var key = NewKey();
        var cache = CreateCache();
        var value = new SampleCacheItem(Guid.NewGuid(), "台北小巨蛋");

        await cache.SetAsync(key, value, TimeSpan.FromSeconds(30), CancellationToken.None);
        var result = await cache.GetAsync<SampleCacheItem>(key, CancellationToken.None);

        result.IsHit.Should().BeTrue();
        result.Value.Should().Be(value);
    }

    [Fact]
    public async Task RemoveAsync_AfterSet_GetAsyncReturnsMiss()
    {
        var key = NewKey();
        var cache = CreateCache();
        await cache.SetAsync(key, new SampleCacheItem(Guid.NewGuid(), "x"), TimeSpan.FromSeconds(30), CancellationToken.None);

        await cache.RemoveAsync(key, CancellationToken.None);
        var result = await cache.GetAsync<SampleCacheItem>(key, CancellationToken.None);

        result.IsHit.Should().BeFalse();
    }

    [Fact]
    public async Task SetAsync_WritesKeyWithSpecifiedTtl()
    {
        var key = NewKey();
        var cache = CreateCache();
        var connection = _fixture.CreateConnection();

        await cache.SetAsync(key, new SampleCacheItem(Guid.NewGuid(), "x"), TimeSpan.FromSeconds(30), CancellationToken.None);

        var ttl = await connection.GetDatabase().KeyTimeToLiveAsync(key);
        ttl.Should().NotBeNull();
        ttl!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    private static RedisConnectionException NewConnectionException()
        => new(ConnectionFailureType.UnableToConnect, CommandFlags.None, "simulated connection failure", null, CommandStatus.Unknown);

    private static RedisTimeoutException NewTimeoutException()
        => new(CommandFlags.None, "simulated timeout", CommandStatus.Unknown);

    [Fact]
    public async Task GetAsync_WhenRedisConnectionFails_ReturnsMissWithoutThrowingAndLogsWarning()
    {
        var logger = new RecordingLogger<RedisQueryCache>();
        var cache = CreateCache(CreateThrowingConnection(NewConnectionException()), logger);

        var result = await cache.GetAsync<SampleCacheItem>(NewKey(), CancellationToken.None);

        result.IsHit.Should().BeFalse();
        logger.LoggedLevels.Should().Contain(LogLevel.Warning);
    }

    [Fact]
    public async Task GetAsync_WhenRedisTimesOut_ReturnsMissWithoutThrowingAndLogsWarning()
    {
        // 逾時（RedisTimeoutException）與連線失敗（RedisConnectionException）在 RedisQueryCache
        // 內部走同一個 catch 分支，但仍須各自建構會拋出對應例外型別的假連線（tasks.md 1.6）。
        var logger = new RecordingLogger<RedisQueryCache>();
        var cache = CreateCache(CreateThrowingConnection(NewTimeoutException()), logger);

        var result = await cache.GetAsync<SampleCacheItem>(NewKey(), CancellationToken.None);

        result.IsHit.Should().BeFalse();
        logger.LoggedLevels.Should().Contain(LogLevel.Warning);
    }

    [Fact]
    public async Task SetAsync_WhenRedisConnectionFails_CompletesWithoutThrowingAndLogsWarning()
    {
        var logger = new RecordingLogger<RedisQueryCache>();
        var cache = CreateCache(CreateThrowingConnection(NewConnectionException()), logger);

        var act = () => cache.SetAsync(NewKey(), new SampleCacheItem(Guid.NewGuid(), "x"), TimeSpan.FromSeconds(30), CancellationToken.None);

        await act.Should().NotThrowAsync();
        logger.LoggedLevels.Should().Contain(LogLevel.Warning);
    }

    [Fact]
    public async Task SetAsync_WhenRedisTimesOut_CompletesWithoutThrowingAndLogsWarning()
    {
        var logger = new RecordingLogger<RedisQueryCache>();
        var cache = CreateCache(CreateThrowingConnection(NewTimeoutException()), logger);

        var act = () => cache.SetAsync(NewKey(), new SampleCacheItem(Guid.NewGuid(), "x"), TimeSpan.FromSeconds(30), CancellationToken.None);

        await act.Should().NotThrowAsync();
        logger.LoggedLevels.Should().Contain(LogLevel.Warning);
    }

    [Fact]
    public async Task RemoveAsync_WhenRedisConnectionFails_CompletesWithoutThrowingAndLogsWarning()
    {
        var logger = new RecordingLogger<RedisQueryCache>();
        var cache = CreateCache(CreateThrowingConnection(NewConnectionException()), logger);

        var act = () => cache.RemoveAsync(NewKey(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        logger.LoggedLevels.Should().Contain(LogLevel.Warning);
    }

    [Fact]
    public async Task RemoveAsync_WhenRedisTimesOut_CompletesWithoutThrowingAndLogsWarning()
    {
        var logger = new RecordingLogger<RedisQueryCache>();
        var cache = CreateCache(CreateThrowingConnection(NewTimeoutException()), logger);

        var act = () => cache.RemoveAsync(NewKey(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        logger.LoggedLevels.Should().Contain(LogLevel.Warning);
    }

    [Fact]
    public async Task GetAsync_WhenCachedValueIsMalformedJson_ReturnsMissWithoutThrowingAndLogsWarning()
    {
        var key = NewKey();
        var connection = _fixture.CreateConnection();
        // 不合法 JSON 語法（截斷字串），直接透過真實連線寫入，繞過 RedisQueryCache.SetAsync。
        await connection.GetDatabase().StringSetAsync(key, "{\"Id\":\"not-closed");
        var logger = new RecordingLogger<RedisQueryCache>();
        var cache = CreateCache(connection, logger);

        var result = await cache.GetAsync<SampleCacheItem>(key, CancellationToken.None);

        result.IsHit.Should().BeFalse();
        logger.LoggedLevels.Should().Contain(LogLevel.Warning);
    }

    [Fact]
    public async Task GetAsync_WhenCachedValueHasTypeIncompatibleField_ReturnsMissWithoutThrowingAndLogsWarning()
    {
        var key = NewKey();
        var connection = _fixture.CreateConnection();
        // 合法 JSON，但 Id 欄位（目標型別 Guid）是不成 GUID 格式的字串——與「單純多/缺欄位」不同，
        // 這種型別不相容才會讓 System.Text.Json 真正拋出 JsonException（design.md 決策 1 訂正）。
        await connection.GetDatabase().StringSetAsync(key, "{\"Id\":\"not-a-guid\",\"Name\":\"x\"}");
        var logger = new RecordingLogger<RedisQueryCache>();
        var cache = CreateCache(connection, logger);

        var result = await cache.GetAsync<SampleCacheItem>(key, CancellationToken.None);

        result.IsHit.Should().BeFalse();
        logger.LoggedLevels.Should().Contain(LogLevel.Warning);
    }

    private static RedisServerException NewServerException()
        => new("simulated server error");

    // RedisServerException 是 RedisException 的子類別（RedisConnectionException 的手足），
    // 但不是 RedisConnectionException／RedisTimeoutException 兩者之一——須獨立驗證會被 fail-open 吸收。
    [Fact]
    public async Task GetAsync_WhenRedisServerErrors_ReturnsMissWithoutThrowingAndLogsWarning()
    {
        var logger = new RecordingLogger<RedisQueryCache>();
        var cache = CreateCache(CreateThrowingConnection(NewServerException()), logger);

        var result = await cache.GetAsync<SampleCacheItem>(NewKey(), CancellationToken.None);

        result.IsHit.Should().BeFalse();
        logger.LoggedLevels.Should().Contain(LogLevel.Warning);
    }

    [Fact]
    public async Task SetAsync_WhenRedisServerErrors_CompletesWithoutThrowingAndLogsWarning()
    {
        var logger = new RecordingLogger<RedisQueryCache>();
        var cache = CreateCache(CreateThrowingConnection(NewServerException()), logger);

        var act = () => cache.SetAsync(NewKey(), new SampleCacheItem(Guid.NewGuid(), "x"), TimeSpan.FromSeconds(30), CancellationToken.None);

        await act.Should().NotThrowAsync();
        logger.LoggedLevels.Should().Contain(LogLevel.Warning);
    }

    [Fact]
    public async Task RemoveAsync_WhenRedisServerErrors_CompletesWithoutThrowingAndLogsWarning()
    {
        var logger = new RecordingLogger<RedisQueryCache>();
        var cache = CreateCache(CreateThrowingConnection(NewServerException()), logger);

        var act = () => cache.RemoveAsync(NewKey(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        logger.LoggedLevels.Should().Contain(LogLevel.Warning);
    }

    // 呼叫端主動取消時必須讓 OperationCanceledException 往外拋，不能被 fail-open 的 catch 吞掉
    // 當成快取未命中／靜默完成（design.md 決策 1；CLAUDE.md CancellationToken 傳遞規則）。
    [Fact]
    public async Task GetAsync_WhenCancellationAlreadyRequested_ThrowsWithoutFailingOpen()
    {
        var cache = CreateCache();
        using var cancelledSource = new CancellationTokenSource();
        await cancelledSource.CancelAsync();

        var act = () => cache.GetAsync<SampleCacheItem>(NewKey(), cancelledSource.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SetAsync_WhenCancellationAlreadyRequested_ThrowsWithoutFailingOpen()
    {
        var cache = CreateCache();
        using var cancelledSource = new CancellationTokenSource();
        await cancelledSource.CancelAsync();

        var act = () => cache.SetAsync(NewKey(), new SampleCacheItem(Guid.NewGuid(), "x"), TimeSpan.FromSeconds(30), cancelledSource.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RemoveAsync_WhenCancellationAlreadyRequested_ThrowsWithoutFailingOpen()
    {
        var cache = CreateCache();
        using var cancelledSource = new CancellationTokenSource();
        await cancelledSource.CancelAsync();

        var act = () => cache.RemoveAsync(NewKey(), cancelledSource.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
