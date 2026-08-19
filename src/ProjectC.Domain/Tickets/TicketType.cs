using ProjectC.Domain.Venues;

namespace ProjectC.Domain.Tickets;

public sealed class TicketType
{
    public Guid Id { get; }
    public Guid EventId { get; }
    public string ZoneCode { get; }
    public decimal Price { get; }
    public bool RequiresSeat { get; private set; }
    public int? AvailableQuantity { get; private set; }

    // 綁座位模式：ZoneCode 必須存在於座位圖分區，AvailableQuantity 恆為 null（庫存概念是 EventSeat 的狀態機，不是計數）。
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
        RequiresSeat = true;
        AvailableQuantity = null;
    }

    // 純計數模式：ZoneCode 僅作票種顯示名稱，不驗證是否對應座位圖分區；AvailableQuantity 必須為正整數。
    internal TicketType(Guid id, Guid eventId, string zoneCode, decimal price, int availableQuantity)
    {
        if (string.IsNullOrWhiteSpace(zoneCode))
            throw new ArgumentException("Zone code is required.", nameof(zoneCode));
        if (price <= 0)
            throw new ArgumentOutOfRangeException(nameof(price), "Ticket price must be greater than zero.");
        if (availableQuantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(availableQuantity), "Available quantity must be greater than zero.");

        Id = id;
        EventId = eventId;
        ZoneCode = zoneCode;
        Price = price;
        RequiresSeat = false;
        AvailableQuantity = availableQuantity;
    }

    // 僅供 EF Core 物化使用：不吃 SeatMap（物化時沒有這個物件可傳），略過建構時的驗證——
    // 從資料庫讀回來的資料已經通過當初寫入時的驗證。
    private TicketType(Guid id, Guid eventId, string zoneCode, decimal price, bool requiresSeat, int? availableQuantity)
    {
        Id = id;
        EventId = eventId;
        ZoneCode = zoneCode;
        Price = price;
        RequiresSeat = requiresSeat;
        AvailableQuantity = availableQuantity;
    }

    /// <summary>下單建立訂單時呼叫，代表庫存被這筆 Pending 訂單佔用。呼叫端 MUST 對交易內鎖定查詢
    /// （悲觀鎖）回傳的實例呼叫，不可對交易前的唯讀查詢結果呼叫，否則鎖定形同虛設。</summary>
    public void Reserve(int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        if (RequiresSeat)
            throw new TicketTypeRequiresSeatException(Id);
        if (AvailableQuantity is null)
            throw new TicketTypeInventoryNotConfiguredException(Id);
        if (AvailableQuantity < quantity)
            throw new TicketTypeInventoryInsufficientException(Id, quantity, AvailableQuantity.Value);

        AvailableQuantity -= quantity;
    }

    /// <summary>取消/逾時清理時呼叫，歸還建立訂單時 Reserve 掉的數量。</summary>
    public void Release(int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        if (RequiresSeat)
            throw new TicketTypeRequiresSeatException(Id);
        if (AvailableQuantity is null)
            throw new TicketTypeInventoryNotConfiguredException(Id);

        AvailableQuantity += quantity;
    }
}
