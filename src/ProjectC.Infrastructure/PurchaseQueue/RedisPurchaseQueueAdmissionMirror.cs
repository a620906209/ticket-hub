using Microsoft.Extensions.Logging;
using ProjectC.Application.Common.Interfaces;
using StackExchange.Redis;

namespace ProjectC.Infrastructure.PurchaseQueue;

// best-effort：MUST NOT 讓例外往外拋，失敗不影響 PQ-JOIN／PQ-COMPLETE 本身的成功與否，只記錄
// Warning，收斂交由 PurchaseQueueAdmissionService 的鏡像校正機制負責（design.md Decision 3／8，
// 兩者皆為無條件敘述，不限定於連線／逾時例外）。捕捉範圍刻意比既有 RedisDistributedLock 的
// fail-open 慣例更寬（涵蓋任何例外，而非僅 RedisConnectionException／RedisTimeoutException）：
// RedisDistributedLock 的呼叫端（背景服務的 leader election）本身已有外層例外處理與重試機制，
// 但這裡的呼叫端（JoinPurchaseQueueHandler／OrderService）是使用者請求路徑，任何未預期的例外
// （含非連線類的 Redis 例外、或本類別自身潛在的邏輯錯誤）洩漏出去都會讓一個已經成功 commit 的
// 使用者操作回報失敗，直接違反 Decision 3／8「此操作失敗不影響呼叫端本身的成功與否」的保證，
// 故意外仍應記錄後吞掉，不重新拋出——僅 OperationCanceledException 例外，讓呼叫端能感知取消。
public sealed class RedisPurchaseQueueAdmissionMirror : IPurchaseQueueAdmissionMirror
{
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly ILogger<RedisPurchaseQueueAdmissionMirror> _logger;

    public RedisPurchaseQueueAdmissionMirror(IConnectionMultiplexer connectionMultiplexer, ILogger<RedisPurchaseQueueAdmissionMirror> logger)
    {
        _connectionMultiplexer = connectionMultiplexer;
        _logger = logger;
    }

    public async Task SyncJoinAsync(Guid eventId, Guid entryId, DateTime joinedAtUtc, CancellationToken cancellationToken)
    {
        try
        {
            var database = _connectionMultiplexer.GetDatabase();
            await database.SortedSetAddAsync(
                PurchaseQueueAdmissionRedisKeys.Waiting(eventId),
                entryId.ToString(),
                PurchaseQueueAdmissionTimeConversion.ToUnixMilliseconds(joinedAtUtc));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception,
                "Failed to sync purchase queue entry {EntryId} into Redis waiting mirror for event {EventId}; will be reconciled later.",
                entryId, eventId);
        }
    }

    public async Task SyncCompletionAsync(Guid eventId, Guid entryId, CancellationToken cancellationToken)
    {
        try
        {
            var database = _connectionMultiplexer.GetDatabase();
            await database.SortedSetRemoveAsync(PurchaseQueueAdmissionRedisKeys.Admitted(eventId), entryId.ToString());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception,
                "Failed to remove purchase queue entry {EntryId} from Redis admitted mirror for event {EventId}; will be reconciled later.",
                entryId, eventId);
        }
    }
}
