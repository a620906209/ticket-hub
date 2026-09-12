using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProjectC.Application.Common.Interfaces;
using StackExchange.Redis;

namespace ProjectC.Infrastructure.Caching;

// 見 openspec/changes/query-caching/design.md 決策 1。錯誤處理比照 RedisDistributedLock 的
// fail-open 慣例：Redis 連線／逾時例外與反序列化例外皆在此吸收，永遠不對呼叫端拋出。
public sealed class RedisQueryCache : IQueryCache
{
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly ILogger<RedisQueryCache> _logger;

    public RedisQueryCache(IConnectionMultiplexer connectionMultiplexer, ILogger<RedisQueryCache> logger)
    {
        _connectionMultiplexer = connectionMultiplexer;
        _logger = logger;
    }

    public async Task<QueryCacheResult<T>> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var database = _connectionMultiplexer.GetDatabase();
            var cachedValue = await database.StringGetAsync(key);
            if (!cachedValue.HasValue)
            {
                return QueryCacheResult<T>.Miss();
            }

            var deserialized = JsonSerializer.Deserialize<T>((string)cachedValue!);
            return deserialized is null ? QueryCacheResult<T>.Miss() : QueryCacheResult<T>.Hit(deserialized);
        }
        // RedisException 涵蓋 RedisConnectionException／RedisServerException；RedisTimeoutException
        // 繼承自 TimeoutException、不在 RedisException 階層下，須另外列出。RedisCommandException（呼叫端
        // 命令用法錯誤）刻意不捕捉，理由同下方 SetAsync 對 JsonSerializer.Serialize 的說明：那是程式錯誤而非
        // 執行期故障。
        catch (Exception exception) when (exception is RedisException or RedisTimeoutException)
        {
            _logger.LogWarning(exception, "Failed to read query cache key {QueryCacheKey} because Redis is unavailable.", key);
            return QueryCacheResult<T>.Miss();
        }
        catch (JsonException exception)
        {
            // 快取值存在、Redis 連線正常，但反序列化失敗（不合法 JSON 或型別不相容），視為未命中
            // 而非往外拋出——這不是 DTO 版本升級策略，只是 fail-open 的另一種失敗來源（design.md 決策 1）。
            _logger.LogWarning(exception, "Failed to deserialize query cache key {QueryCacheKey}.", key);
            return QueryCacheResult<T>.Miss();
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var database = _connectionMultiplexer.GetDatabase();
            // JsonSerializer.Serialize 的失敗（例如 T 含循環參考）不在 fail-open 範圍內：這代表呼叫端
            // 傳入了不可序列化的型別，是程式錯誤而非執行期故障，MUST 讓它往外拋，不吞掉當成快取未命中。
            var serialized = JsonSerializer.Serialize(value);
            await database.StringSetAsync(key, serialized, ttl);
        }
        catch (Exception exception) when (exception is RedisException or RedisTimeoutException)
        {
            _logger.LogWarning(exception, "Failed to write query cache key {QueryCacheKey} because Redis is unavailable.", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var database = _connectionMultiplexer.GetDatabase();
            await database.KeyDeleteAsync(key);
        }
        catch (Exception exception) when (exception is RedisException or RedisTimeoutException)
        {
            _logger.LogWarning(exception, "Failed to remove query cache key {QueryCacheKey} because Redis is unavailable.", key);
        }
    }
}
