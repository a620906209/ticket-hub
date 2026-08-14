using ProjectC.Domain.Common;

namespace ProjectC.Domain.Events;

public sealed class SeatNotHeldException : DomainException
{
    public Guid EventSeatId { get; }
    public Guid OrderId { get; }

    public SeatNotHeldException(Guid eventSeatId, Guid orderId)
        : base($"Seat '{eventSeatId}' is not currently held by order '{orderId}'.")
    {
        EventSeatId = eventSeatId;
        OrderId = orderId;
    }
}
