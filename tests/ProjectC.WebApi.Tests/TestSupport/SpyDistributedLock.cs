using ProjectC.Application.Common.Interfaces;

namespace ProjectC.WebApi.Tests.TestSupport;

// 包住真正的 IDistributedLock（例如指向真實 Redis 的 RedisDistributedLock），記錄每次
// TryAcquireAsync／ReleaseAsync 的呼叫結果，讓測試能斷言「哪個實例取得了鎖」而不需要另外
// 建立 spy repository（purchase-queue-leader-election tasks.md 5.3／5.4）。
public sealed class SpyDistributedLock : IDistributedLock
{
    private readonly IDistributedLock _inner;

    public SpyDistributedLock(IDistributedLock inner)
    {
        _inner = inner;
    }

    public List<LockResult> AcquireResults { get; } = [];

    public int ReleaseCallCount { get; private set; }

    /// <summary>最近一次成功取得鎖時的 ownerToken，供測試直接比對 Redis 端鎖 value 是否確實歸屬這個
    /// 實例（PQLE-006，見 tasks.md 8.2.3）；未曾成功取得鎖時為 null。</summary>
    public string? LastAcquiredOwnerToken { get; private set; }

    public async Task<LockAcquisitionResult> TryAcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var result = await _inner.TryAcquireAsync(key, ttl, cancellationToken);
        AcquireResults.Add(result.LockResult);
        if (result.LockResult == LockResult.Acquired)
        {
            LastAcquiredOwnerToken = result.OwnerToken;
        }

        return result;
    }

    public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        ReleaseCallCount++;
        return _inner.ReleaseAsync(key, ownerToken, cancellationToken);
    }
}
