using ProjectC.Domain.Common;

namespace ProjectC.Domain.Orders;

public sealed class OrderAlreadyConfirmedException : DomainException
{
    public Guid OrderId { get; }

    public OrderAlreadyConfirmedException(Guid orderId)
        : base($"Order '{orderId}' is already confirmed and cannot be cancelled.")
    {
        OrderId = orderId;
    }
}
