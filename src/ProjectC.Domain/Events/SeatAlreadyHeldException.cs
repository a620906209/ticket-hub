using ProjectC.Domain.Common;

namespace ProjectC.Domain.Events;

public sealed class SeatAlreadyHeldException : DomainException
{
    public Guid EventSeatId { get; }

    public SeatAlreadyHeldException(Guid eventSeatId)
        : base($"Seat '{eventSeatId}' is already held by a pending order.")
    {
        EventSeatId = eventSeatId;
    }
}
