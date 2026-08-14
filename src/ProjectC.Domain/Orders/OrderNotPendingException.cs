using ProjectC.Domain.Common;

namespace ProjectC.Domain.Orders;

public sealed class OrderNotPendingException : DomainException
{
    public Guid OrderId { get; }
    public OrderStatus Status { get; }

    public OrderNotPendingException(Guid orderId, OrderStatus status)
        : base($"Order '{orderId}' requires status Pending but is currently '{status}'.")
    {
        OrderId = orderId;
        Status = status;
    }
}
