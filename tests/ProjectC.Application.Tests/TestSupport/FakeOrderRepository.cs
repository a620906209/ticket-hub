using ProjectC.Domain.Orders;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeOrderRepository : IOrderRepository
{
    public List<Order> Data { get; } = new();

    public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(o => o.Id == id));

    public Task<IReadOnlyList<Order>> GetAllAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Order>>(Data.ToList());

    public Task<IReadOnlyList<Order>> GetByBuyerIdAsync(Guid buyerId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Order>>(Data.Where(order => order.BuyerId == buyerId).ToList());

    public Task<Order?> GetByOrderItemIdAsync(Guid orderItemId, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(order => order.Items.Any(item => item.Id == orderItemId)));

    public Task<IReadOnlyList<Guid>> GetExpiredPendingOrderIdsAsync(DateTime now, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Guid>>(
            Data.Where(o => o.Status == OrderStatus.Pending && o.HeldUntilUtc <= now).Select(o => o.Id).ToList());

    // 假物件內只有單一份共用的 Order 參考（沒有序列化/反序列化的過程），reload 沒有東西需要重新覆寫；
    // 真正驗證「鎖後重讀」效果的並發情境留給 Infrastructure 層的 Testcontainers 整合測試。
    public Task ReloadAsync(Order order, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Add(Order order) => Data.Add(order);

    // OrderItem 的公開建構子要求 TicketTypeId 為非 null Guid，無法透過正常 Domain API 從 Data 反推出
    // TicketTypeId = null 或指向其他活動票種的分組；這個方法不嘗試從 Data 推導，改由測試直接設定要回傳的
    // 投影結果（design.md 決策 8），資料庫端真正的分組行為交給 Infrastructure Testcontainers 整合測試驗證。
    public IReadOnlyList<OrderItemSalesGroup> PaidItemSalesGroups { get; set; } = [];

    public Task<IReadOnlyList<OrderItemSalesGroup>> GetPaidItemSalesByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
        => Task.FromResult(PaidItemSalesGroups);
}
