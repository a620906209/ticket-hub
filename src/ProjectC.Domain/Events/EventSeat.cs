namespace ProjectC.Domain.Events;

public sealed class EventSeat
{
    public Guid Id { get; }
    public Guid EventId { get; }
    public Guid SeatId { get; }

    // 依 seat-reservation spec 的「座位狀態存取限制」，這三個欄位刻意不對外暴露，
    // 呼叫端一律透過 GetStatus / IsAvailableForHold / IsHeldBy 存取。
    private Guid? _heldByOrderId;
    private DateTime? _heldUntilUtc;
    private Guid? _soldByOrderId;

    internal EventSeat(Guid id, Guid eventId, Guid seatId)
    {
        Id = id;
        EventId = eventId;
        SeatId = seatId;
    }

    public EventSeatStatus GetStatus(DateTime now)
    {
        if (_soldByOrderId is not null)
            return EventSeatStatus.Sold;

        if (_heldByOrderId is not null && now < _heldUntilUtc)
            return EventSeatStatus.Held;

        return EventSeatStatus.Available;
    }

    public bool IsAvailableForHold(DateTime now) => GetStatus(now) == EventSeatStatus.Available;

    public bool IsHeldBy(Guid orderId, DateTime now)
        => _soldByOrderId is null && _heldByOrderId == orderId && now < _heldUntilUtc;

    public bool IsSoldBy(Guid orderId) => _soldByOrderId == orderId;

    public void Hold(Guid orderId, DateTime heldUntilUtc, DateTime now)
    {
        var status = GetStatus(now);
        if (status == EventSeatStatus.Sold)
            throw new SeatAlreadySoldException(Id);
        if (status == EventSeatStatus.Held)
            throw new SeatAlreadyHeldException(Id);

        _heldByOrderId = orderId;
        _heldUntilUtc = heldUntilUtc;
    }

    public void ConfirmSold(Guid orderId, DateTime now)
    {
        if (!IsHeldBy(orderId, now))
            throw new SeatNotHeldException(Id, orderId);

        _soldByOrderId = orderId;
        _heldByOrderId = null;
        _heldUntilUtc = null;
    }

    public void ReleaseHold(Guid orderId)
    {
        if (_soldByOrderId is not null)
            throw new SeatAlreadySoldException(Id);

        if (_heldByOrderId != orderId)
            return;

        _heldByOrderId = null;
        _heldUntilUtc = null;
    }
}
