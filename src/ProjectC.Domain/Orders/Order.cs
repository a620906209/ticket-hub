namespace ProjectC.Domain.Orders;

public sealed class Order
{
    private readonly List<OrderItem> _items = [];

    public Guid Id { get; }
    public Guid EventId { get; }
    public Guid BuyerId { get; }
    public DateTime CreatedAtUtc { get; }
    public DateTime HeldUntilUtc { get; }
    public OrderStatus Status { get; private set; }
    public IReadOnlyList<OrderItem> Items => _items;

    public Order(Guid id, Guid eventId, Guid buyerId, DateTime createdAtUtc, DateTime heldUntilUtc, IEnumerable<OrderItem> items)
    {
        var itemList = items.ToList();
        if (itemList.Count == 0)
            throw new ArgumentException("Order must contain at least one item.", nameof(items));
        if (buyerId == Guid.Empty)
            throw new ArgumentException("Buyer is required.", nameof(buyerId));
        if (createdAtUtc == default)
            throw new ArgumentException("Created time is required.", nameof(createdAtUtc));

        Id = id;
        EventId = eventId;
        BuyerId = buyerId;
        CreatedAtUtc = createdAtUtc;
        HeldUntilUtc = heldUntilUtc;
        Status = OrderStatus.Pending;
        _items.AddRange(itemList);
    }

    // 僅供 EF Core 物化使用：只吃純量參數，刻意不接受 items 集合，
    // 避免跟上面吃 IEnumerable&lt;OrderItem&gt; 的公開建構子在 constructor binding 時產生歧義。
    // _items 完全透過 backing field mapping 處理。
    private Order(Guid id, Guid eventId, Guid buyerId, DateTime createdAtUtc, DateTime heldUntilUtc, OrderStatus status)
    {
        Id = id;
        EventId = eventId;
        BuyerId = buyerId;
        CreatedAtUtc = createdAtUtc;
        HeldUntilUtc = heldUntilUtc;
        Status = status;
    }

    public OrderStatus GetStatus(DateTime now)
    {
        if (Status == OrderStatus.Pending && now >= HeldUntilUtc)
            return OrderStatus.Expired;

        return Status;
    }

    public int GetTicketCount() => _items.Sum(item => item.Quantity);

    public decimal GetTotalAmount() => _items.Sum(item => item.Quantity * item.UnitPrice);

    public void Confirm()
    {
        if (Status != OrderStatus.Pending)
            throw new OrderNotPendingException(Id, Status);

        Status = OrderStatus.Paid;
    }

    public void Cancel()
    {
        if (Status != OrderStatus.Pending)
            throw new OrderNotPendingException(Id, Status);

        Status = OrderStatus.Cancelled;
    }
}
