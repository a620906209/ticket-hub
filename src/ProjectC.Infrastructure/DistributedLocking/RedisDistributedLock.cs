using Microsoft.Extensions.Logging;
using ProjectC.Application.Common.Interfaces;
using StackExchange.Redis;

namespace ProjectC.Infrastructure.DistributedLocking;

// 見 openspec/changes/purchase-queue-leader-election/design.md 決策 2、4。
public sealed class RedisDistributedLock : IDistributedLock
{
    // compare-and-delete：先比對 value 是否等於自己的 ownerToken，相等才 DEL，避免「先 GET 再 DEL」
    // 兩個指令在極端時序下（本次執行超時、TTL 已過期、另一個實例已取得新鎖）誤刪別人剛取得的鎖
    // （design.md 決策 2，Redis 官方文件記載的分散式鎖釋放標準寫法）。
    private const string ReleaseIfOwnerScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly ILogger<RedisDistributedLock> _logger;

    public RedisDistributedLock(IConnectionMultiplexer connectionMultiplexer, ILogger<RedisDistributedLock> logger)
    {
        _connectionMultiplexer = connectionMultiplexer;
        _logger = logger;
    }

    public async Task<LockAcquisitionResult> TryAcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var ownerToken = Guid.NewGuid().ToString("N");

        try
        {
            var database = _connectionMultiplexer.GetDatabase();
            var acquired = await database.StringSetAsync(key, ownerToken, ttl, When.NotExists);

            return acquired
                ? new LockAcquisitionResult(LockResult.Acquired, ownerToken)
                : new LockAcquisitionResult(LockResult.HeldByOther, null);
        }
        catch (Exception exception) when (exception is RedisConnectionException or RedisTimeoutException)
        {
            // 無法連線 Redis 時 MUST NOT 讓例外往外拋、MUST NOT 讓呼叫端因此中斷本輪推進——
            // fail-open，回傳 RedisUnavailable 讓呼叫端視為已取得執行資格（design.md 決策 4）。
            _logger.LogWarning(exception, "Failed to acquire distributed lock {LockKey} because Redis is unavailable.", key);
            return new LockAcquisitionResult(LockResult.RedisUnavailable, null);
        }
    }

    public async Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        try
        {
            var database = _connectionMultiplexer.GetDatabase();
            await database.ScriptEvaluateAsync(ReleaseIfOwnerScript, [key], [ownerToken]);
        }
        catch (Exception exception) when (exception is RedisConnectionException or RedisTimeoutException)
        {
            // 釋放失敗視為可觀察的降級結果，不是靜默失敗——不影響本輪推進已完成的結果，
            // TTL 到期後仍會自動釋放（design.md 決策 3）。
            _logger.LogWarning(exception, "Failed to release distributed lock {LockKey} because Redis is unavailable.", key);
        }
    }
}
