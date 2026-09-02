namespace ProjectC.Application.Common.Interfaces;

// 經核准例外，放置於 Application/Common/Interfaces 而非 Domain：見 openspec/changes/
// purchase-queue-leader-election/design.md 決策 5——IDistributedLock 不承載業務語意，
// 比照 IDateTimeProvider 的既有放置模式；不得依此案例類推放寬其他外部服務介面的放置規則。
public interface IDistributedLock
{
    Task<LockAcquisitionResult> TryAcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken);

    Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken);
}
