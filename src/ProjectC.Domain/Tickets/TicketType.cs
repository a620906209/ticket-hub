using ProjectC.Domain.Venues;

namespace ProjectC.Domain.Tickets;

public sealed class TicketType
{
    public Guid Id { get; }
    public Guid EventId { get; }
    public string ZoneCode { get; }
    public decimal Price { get; }

    internal TicketType(Guid id, Guid eventId, string zoneCode, decimal price, SeatMap seatMap)
    {
        if (string.IsNullOrWhiteSpace(zoneCode))
            throw new ArgumentException("Zone code is required.", nameof(zoneCode));
        if (price <= 0)
            throw new ArgumentOutOfRangeException(nameof(price), "Ticket price must be greater than zero.");
        if (!seatMap.Seats.Any(s => s.ZoneCode == zoneCode))
            throw new InvalidOperationException($"Zone '{zoneCode}' does not exist in the event's seat map.");

        Id = id;
        EventId = eventId;
        ZoneCode = zoneCode;
        Price = price;
    }

    // 僅供 EF Core 物化使用：不吃 SeatMap（物化時沒有這個物件可傳），
    // 略過建構時的 zone 驗證——從資料庫讀回來的資料已經通過當初寫入時的驗證。
    private TicketType(Guid id, Guid eventId, string zoneCode, decimal price)
    {
        Id = id;
        EventId = eventId;
        ZoneCode = zoneCode;
        Price = price;
    }
}
