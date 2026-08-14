namespace ProjectC.Domain.Orders;

public sealed class Order
{
    private readonly List<OrderItem> _items = [];

    public Guid Id { get; }
    public DateTime HeldUntilUtc { get; }
    public OrderStatus Status { get; private set; }
    public IReadOnlyList<OrderItem> Items => _items;

    public Order(Guid id, DateTime heldUntilUtc, IEnumerable<OrderItem> items)
    {
        var itemList = items.ToList();
        if (itemList.Count == 0)
            throw new ArgumentException("Order must contain at least one item.", nameof(items));

        Id = id;
        HeldUntilUtc = heldUntilUtc;
        Status = OrderStatus.Pending;
        _items.AddRange(itemList);
    }

    public OrderStatus GetStatus(DateTime now)
    {
        if (Status == OrderStatus.Pending && now >= HeldUntilUtc)
            return OrderStatus.Expired;

        return Status;
    }

    public void Confirm()
    {
        if (Status != OrderStatus.Pending)
            throw new InvalidOperationException($"Order '{Id}' cannot be confirmed because its status is '{Status}'.");

        Status = OrderStatus.Confirmed;
    }

    public void Cancel()
    {
        if (Status == OrderStatus.Confirmed)
            throw new OrderAlreadyConfirmedException(Id);

        Status = OrderStatus.Cancelled;
    }
}
