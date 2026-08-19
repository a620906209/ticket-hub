namespace ProjectC.Domain.Orders;

public enum OrderStatus
{
    Pending,
    Paid,
    Cancelled,

    /// <summary>只由 Order.GetStatus(now) 推導回傳，永遠不會寫入 Order 的持久化狀態欄位。</summary>
    Expired,
}
