namespace ProjectC.Domain.Orders;

public interface IOrderRepository
{
    /// <summary>實作 MUST 一併載入 <see cref="Order.Items"/>；<c>ConfirmOrderHandler</c>/<c>CancelOrderHandler</c> 假設這個集合已完整。</summary>
    Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    void Add(Order order);
}
