using ProjectC.Domain.Orders;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeOrderRepository : IOrderRepository
{
    public List<Order> Data { get; } = new();

    // 收到的 token 供「token 原樣傳遞」斷言使用（order-display-enrichment tasks.md 1.2c）。
    public CancellationToken? LastGetByIdToken { get; private set; }
    public CancellationToken? LastGetByOrganizerIdToken { get; private set; }
    public CancellationToken? LastGetByBuyerIdToken { get; private set; }

    public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        LastGetByIdToken = cancellationToken;
        return Task.FromResult(Data.FirstOrDefault(o => o.Id == id));
    }

    // Order 只以 EventId 參照 Event，假物件無法自行 join；改由測試直接指定每個 EventId 所屬的 OrganizerId。
    public Dictionary<Guid, Guid> OrganizerIdByEventId { get; } = new();

    public Task<IReadOnlyList<Order>> GetByOrganizerIdAsync(Guid organizerId, CancellationToken cancellationToken)
    {
        LastGetByOrganizerIdToken = cancellationToken;
        return Task.FromResult<IReadOnlyList<Order>>(Data
            .Where(o => OrganizerIdByEventId.TryGetValue(o.EventId, out var owner) && owner == organizerId)
            .ToList());
    }

    // 同 OrganizerIdByEventId：假物件無法 join Events，由測試指定哪些活動需實名。
    public HashSet<Guid> RealNameRequiredEventIds { get; } = new();

    public CancellationToken? LastGetRedemptionContextToken { get; private set; }

    public Task<RedemptionContext?> GetRedemptionContextByOrderItemIdAsync(Guid orderItemId, CancellationToken cancellationToken)
    {
        LastGetRedemptionContextToken = cancellationToken;
        var order = Data.FirstOrDefault(o => o.Items.Any(i => i.Id == orderItemId));
        RedemptionContext? context = order is not null && OrganizerIdByEventId.TryGetValue(order.EventId, out var owner)
            ? new RedemptionContext(owner, RealNameRequiredEventIds.Contains(order.EventId), order.BuyerId)
            : null;
        return Task.FromResult(context);
    }

    public Task<IReadOnlyList<Order>> GetByBuyerIdAsync(Guid buyerId, CancellationToken cancellationToken)
    {
        LastGetByBuyerIdToken = cancellationToken;
        return Task.FromResult<IReadOnlyList<Order>>(Data.Where(order => order.BuyerId == buyerId).ToList());
    }

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
