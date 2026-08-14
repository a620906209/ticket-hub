using ProjectC.Domain.Common;

namespace ProjectC.Domain.Events;

public sealed class SeatAlreadySoldException : DomainException
{
    public Guid EventSeatId { get; }

    public SeatAlreadySoldException(Guid eventSeatId)
        : base($"Seat '{eventSeatId}' is already sold and cannot be held or released.")
    {
        EventSeatId = eventSeatId;
    }
}
