namespace ProjectC.Domain.Tickets;

public sealed class Ticket
{
    public Guid Id { get; }
    public Guid OrderItemId { get; }
    public TicketStatus Status { get; private set; }
    public DateTime IssuedAtUtc { get; }
    public DateTime? RedeemedAtUtc { get; private set; }

    // 出票用：初始狀態固定為 Issued，比照 Order/OrderItem 既有模式，公開建構子只接受新建立時需要的欄位。
    public Ticket(Guid id, Guid orderItemId, DateTime issuedAtUtc)
    {
        if (orderItemId == Guid.Empty)
            throw new ArgumentException("Order item is required.", nameof(orderItemId));

        Id = id;
        OrderItemId = orderItemId;
        Status = TicketStatus.Issued;
        IssuedAtUtc = issuedAtUtc;
    }

    // 僅供 EF Core 物化使用：直接還原歷史狀態，不重新驗證——從資料庫讀回來的資料已經通過
    // 當初寫入時的驗證（比照 Order/OrderItem 既有模式）。
    private Ticket(Guid id, Guid orderItemId, TicketStatus status, DateTime issuedAtUtc, DateTime? redeemedAtUtc)
    {
        Id = id;
        OrderItemId = orderItemId;
        Status = status;
        IssuedAtUtc = issuedAtUtc;
        RedeemedAtUtc = redeemedAtUtc;
    }

    public void Redeem(DateTime now)
    {
        if (Status != TicketStatus.Issued)
            throw new TicketNotIssuedException(Id, Status);

        Status = TicketStatus.Redeemed;
        RedeemedAtUtc = now;
    }
}
