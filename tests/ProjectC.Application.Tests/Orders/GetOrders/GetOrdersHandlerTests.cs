using FluentAssertions;
using ProjectC.Application.Orders.GetOrders;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Tests.Orders.GetOrders;

public class GetOrdersHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private static Order CreateOrder(DateTime heldUntilUtc)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), heldUntilUtc, [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 500m)]);

    [Fact]
    public async Task HandleAsync_ReturnsAllOrdersWithLiveStatus()
    {
        var orderRepository = new FakeOrderRepository();
        var pendingOrder = CreateOrder(Now.AddMinutes(10));
        var expiredOrder = CreateOrder(Now.AddMinutes(-1));
        orderRepository.Data.Add(pendingOrder);
        orderRepository.Data.Add(expiredOrder);
        var handler = new GetOrdersHandler(orderRepository, new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.HandleAsync(CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().ContainSingle(o => o.Id == pendingOrder.Id && o.Status == "Pending");
        // 已逾時但持久化狀態仍是 Pending 的訂單，即時狀態 MUST 回報 Expired，不是持久化欄位本身。
        result.Should().ContainSingle(o => o.Id == expiredOrder.Id && o.Status == "Expired");
    }

    [Fact]
    public async Task HandleAsync_WhenNoOrders_ReturnsEmptyList()
    {
        var handler = new GetOrdersHandler(new FakeOrderRepository(), new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.HandleAsync(CancellationToken.None);

        result.Should().BeEmpty();
    }
}
