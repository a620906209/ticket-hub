namespace ProjectC.Domain.Orders;

public sealed class OrderItem
{
    public Guid Id { get; }
    public Guid EventSeatId { get; }
    public decimal UnitPrice { get; }

    public OrderItem(Guid id, Guid eventSeatId, decimal unitPrice)
    {
        if (eventSeatId == Guid.Empty)
            throw new ArgumentException("Event seat is required.", nameof(eventSeatId));
        if (unitPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "Unit price must be greater than zero.");

        Id = id;
        EventSeatId = eventSeatId;
        UnitPrice = unitPrice;
    }
}
