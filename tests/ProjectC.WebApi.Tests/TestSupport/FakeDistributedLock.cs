using ProjectC.Application.Common.Interfaces;

namespace ProjectC.WebApi.Tests.TestSupport;

// 預設永遠回傳 Acquired（比照真實 Redis 平時可用時「每次都取得到鎖」的行為），讓既有不關心
// 多實例協調的 purchase-queue 測試不需要真正連線 Redis 仍可維持原本「每輪都執行」的行為
// （purchase-queue-leader-election design.md 決策 6）。同時記錄呼叫次數／參數，供驗證
// AdvanceQueueOnceWithLeaderElectionAsync 與 ExecuteAsync 的委派行為使用。
public sealed class FakeDistributedLock : IDistributedLock
{
    public LockResult NextResult { get; set; } = LockResult.Acquired;

    public List<(string Key, TimeSpan Ttl)> AcquireCalls { get; } = [];

    public List<(string Key, string OwnerToken)> ReleaseCalls { get; } = [];

    public Task<LockAcquisitionResult> TryAcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken)
    {
        AcquireCalls.Add((key, ttl));

        var ownerToken = NextResult == LockResult.Acquired ? Guid.NewGuid().ToString("N") : null;
        return Task.FromResult(new LockAcquisitionResult(NextResult, ownerToken));
    }

    public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        ReleaseCalls.Add((key, ownerToken));
        return Task.CompletedTask;
    }
}
