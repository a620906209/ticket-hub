using ProjectC.Domain.Common;

namespace ProjectC.Domain.PurchaseQueue;

public sealed class PurchaseQueueEntryNotAdmittedException : DomainException
{
    public Guid PurchaseQueueEntryId { get; }
    public PurchaseQueueEntryStatus Status { get; }

    public PurchaseQueueEntryNotAdmittedException(Guid purchaseQueueEntryId, PurchaseQueueEntryStatus status)
        : base($"Purchase queue entry '{purchaseQueueEntryId}' requires status Admitted but is currently '{status}'.")
    {
        PurchaseQueueEntryId = purchaseQueueEntryId;
        Status = status;
    }
}
