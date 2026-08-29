using ProjectC.Domain.Common;

namespace ProjectC.Domain.PurchaseQueue;

/// <summary>
/// 加入排隊的併發衝突在 Infrastructure 內部重試一次仍失敗時拋出（極端情況，見 rate-limiting-queue
/// design.md 決策 3）；Application 層捕捉並映射為 Error.Conflict。
/// </summary>
public sealed class PurchaseQueueJoinConflictException : DomainException
{
    public Guid EventId { get; }
    public Guid MemberId { get; }

    public PurchaseQueueJoinConflictException(Guid eventId, Guid memberId)
        : base($"Could not resolve purchase queue entry for event '{eventId}' and member '{memberId}' after retrying.")
    {
        EventId = eventId;
        MemberId = memberId;
    }
}
