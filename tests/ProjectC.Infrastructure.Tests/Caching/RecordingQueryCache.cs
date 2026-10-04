using System.Diagnostics;
using System.Text.Json;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Infrastructure.Caching;
using StackExchange.Redis;

namespace ProjectC.Infrastructure.Tests.Caching;

public enum QueryCacheEventType
{
    CommitObserved,
    CommitNotObserved,
    RemoveCompleted,
    SetStarted,
    SetCompleted,
    SetSucceeded,
    SetNotCommittedToRedis,
}

/// <summary>Timestamp 為 <see cref="Stopwatch.GetTimestamp"/>，與測試自己記錄的時刻共用同一個單調時鐘。</summary>
public sealed record QueryCacheEvent(
    QueryCacheEventType EventType,
    long Sequence,
    Guid? OperationId,
    long Timestamp,
    string Key,
    string? Detail);

/// <summary>
/// QC-TTL-004 的測試替身（event-sales-window tasks.md 5.5、design.md 決策 6）：包裝真正的 <see cref="RedisQueryCache"/>，
/// 依發生順序記錄提交、失效與寫入事件，讓測試以事件序號證明競態先後，並以 Set 前後的時刻夾住 Redis 實際寫入時刻。
/// </summary>
public sealed class RecordingQueryCache : IQueryCache
{
    private readonly RedisQueryCache _inner;
    private readonly IConnectionMultiplexer _connection;
    private readonly string _targetKey;
    private readonly Func<CancellationToken, Task<bool>> _isWriteCommittedAsync;
    private readonly object _eventsLock = new();
    private readonly List<QueryCacheEvent> _events = new();
    private long _sequence;

    public RecordingQueryCache(
        RedisQueryCache inner,
        IConnectionMultiplexer connection,
        string targetKey,
        Func<CancellationToken, Task<bool>> isWriteCommittedAsync)
    {
        _inner = inner;
        _connection = connection;
        _targetKey = targetKey;
        _isWriteCommittedAsync = isWriteCommittedAsync;
    }

    public TaskCompletionSource RemoveCompletedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<QueryCacheEvent> Events
    {
        get
        {
            lock (_eventsLock)
            {
                return _events.ToList();
            }
        }
    }

    public Task<QueryCacheResult<T>> GetAsync<T>(string key, CancellationToken cancellationToken)
        => _inner.GetAsync<T>(key, cancellationToken);

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        Record(QueryCacheEventType.SetStarted, key, operationId);
        await _inner.SetAsync(key, value, ttl, cancellationToken);
        Record(QueryCacheEventType.SetCompleted, key, operationId);

        // RedisQueryCache 對 Redis 例外 fail-open（記 Warning 後正常返回），內層返回不代表已寫入 Redis，須讀回確認。
        var database = _connection.GetDatabase();
        var failures = new List<string>();
        if (!await database.KeyExistsAsync(key))
        {
            failures.Add("KeyExistsAsync returned false");
        }

        var storedValue = await database.StringGetAsync(key);
        if ((string?)storedValue != JsonSerializer.Serialize(value))
        {
            failures.Add("stored value differs from this call's serialized value");
        }

        var timeToLive = await database.KeyTimeToLiveAsync(key);
        if (timeToLive is null || timeToLive.Value <= TimeSpan.Zero)
        {
            failures.Add($"PTTL is not positive ({timeToLive?.TotalMilliseconds.ToString() ?? "null"})");
        }

        if (failures.Count == 0)
        {
            Record(QueryCacheEventType.SetSucceeded, key, operationId);
        }
        else
        {
            Record(QueryCacheEventType.SetNotCommittedToRedis, key, operationId, string.Join("; ", failures));
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        if (key != _targetKey)
        {
            await _inner.RemoveAsync(key, cancellationToken);
            Record(QueryCacheEventType.RemoveCompleted, key);
            return;
        }

        var isCommitted = await _isWriteCommittedAsync(cancellationToken);
        Record(isCommitted ? QueryCacheEventType.CommitObserved : QueryCacheEventType.CommitNotObserved, key);
        await _inner.RemoveAsync(key, cancellationToken);
        Record(QueryCacheEventType.RemoveCompleted, key);
        RemoveCompletedSignal.TrySetResult();
    }

    private void Record(QueryCacheEventType eventType, string key, Guid? operationId = null, string? detail = null)
    {
        lock (_eventsLock)
        {
            // 序號與時刻在同一把鎖內取得，確保清單順序、序號與時刻三者一致。
            var sequence = Interlocked.Increment(ref _sequence);
            _events.Add(new QueryCacheEvent(eventType, sequence, operationId, Stopwatch.GetTimestamp(), key, detail));
        }
    }
}

/// <summary>QC-TTL-004 的檢查落在時間窗口外：結果不可判定，與 key 狀態或內容錯誤的一般斷言失敗區分，不得當成通過。</summary>
public sealed class QueryCacheTimingInconclusiveException : Exception
{
    public QueryCacheTimingInconclusiveException(string message) : base(message)
    {
    }
}
