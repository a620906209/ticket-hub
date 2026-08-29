using ProjectC.Domain.Common;

namespace ProjectC.Domain.PurchaseQueue;

public sealed class PurchaseQueueEntryNotWaitingException : DomainException
{
    public Guid PurchaseQueueEntryId { get; }
    public PurchaseQueueEntryStatus Status { get; }

    public PurchaseQueueEntryNotWaitingException(Guid purchaseQueueEntryId, PurchaseQueueEntryStatus status)
        : base($"Purchase queue entry '{purchaseQueueEntryId}' requires status Waiting but is currently '{status}'.")
    {
        PurchaseQueueEntryId = purchaseQueueEntryId;
        Status = status;
    }
}
