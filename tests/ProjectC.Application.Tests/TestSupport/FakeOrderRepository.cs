using ProjectC.Domain.Orders;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeOrderRepository : IOrderRepository
{
    public List<Order> Data { get; } = new();

    public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(o => o.Id == id));

    // 假物件內只有單一份共用的 Order 參考（沒有序列化/反序列化的過程），reload 沒有東西需要重新覆寫；
    // 真正驗證「鎖後重讀」效果的並發情境留給 Infrastructure 層的 Testcontainers 整合測試。
    public Task ReloadAsync(Order order, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Add(Order order) => Data.Add(order);
}
