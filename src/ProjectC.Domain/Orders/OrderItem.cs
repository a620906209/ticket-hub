namespace ProjectC.Domain.Orders;

public sealed class OrderItem
{
    public Guid Id { get; }
    public Guid? TicketTypeId { get; }
    public Guid? EventSeatId { get; }
    public int Quantity { get; }
    public decimal UnitPrice { get; }

    // 新建立的 OrderItem 只能是以下兩種形狀之一（互斥）：
    // - 座位行項：eventSeatId 有值、quantity = 1
    // - 計數行項：eventSeatId = null、quantity >= 1
    // ticketTypeId 兩種形狀都必填（不可為 Guid.Empty）——這是「新建立」的不變量，不代表 entity 屬性本身不可為 null，
    // 既有舊資料的 TicketTypeId 是 NULL，靠下面的 private 物化建構子相容。
    public OrderItem(Guid id, Guid ticketTypeId, Guid? eventSeatId, int quantity, decimal unitPrice)
    {
        if (ticketTypeId == Guid.Empty)
            throw new ArgumentException("Ticket type is required.", nameof(ticketTypeId));
        if (unitPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "Unit price must be greater than zero.");
        if (eventSeatId.HasValue && eventSeatId.Value == Guid.Empty)
            throw new ArgumentException("Event seat id cannot be Guid.Empty.", nameof(eventSeatId));
        if (eventSeatId.HasValue && quantity != 1)
            throw new ArgumentOutOfRangeException(nameof(quantity), "A seat item must have quantity exactly 1.");
        if (!eventSeatId.HasValue && quantity < 1)
            throw new ArgumentOutOfRangeException(nameof(quantity), "A counting item must have quantity of at least 1.");

        Id = id;
        TicketTypeId = ticketTypeId;
        EventSeatId = eventSeatId;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    // 僅供 EF Core 物化使用：ticketTypeId 接受 Guid?（相容既有舊列 TicketTypeId IS NULL），
    // 不做任何驗證——從資料庫讀回來的資料已經通過當初寫入時的驗證，且舊資料本來就不滿足「必填」這條規則。
    private OrderItem(Guid id, Guid? ticketTypeId, Guid? eventSeatId, int quantity, decimal unitPrice)
    {
        Id = id;
        TicketTypeId = ticketTypeId;
        EventSeatId = eventSeatId;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }
}
