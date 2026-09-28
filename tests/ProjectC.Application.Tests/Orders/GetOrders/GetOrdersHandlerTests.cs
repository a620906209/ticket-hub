using FluentAssertions;
using ProjectC.Application.Orders.GetOrders;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Tests.Orders.GetOrders;

public class GetOrdersHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OrganizerId = Guid.NewGuid();

    private readonly FakeOrderRepository _orderRepository = new();

    private Order SeedOrder(Guid organizerId, DateTime heldUntilUtc)
    {
        var eventId = Guid.NewGuid();
        _orderRepository.OrganizerIdByEventId[eventId] = organizerId;
        var order = new Order(Guid.NewGuid(), eventId, Guid.NewGuid(), heldUntilUtc, [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 500m)]);
        _orderRepository.Data.Add(order);
        return order;
    }

    [Fact]
    public async Task HandleAsync_ReturnsCallerOrganizerOrdersWithLiveStatus()
    {
        var pendingOrder = SeedOrder(OrganizerId, Now.AddMinutes(10));
        var expiredOrder = SeedOrder(OrganizerId, Now.AddMinutes(-1));
        var otherOrganizerOrder = SeedOrder(Guid.NewGuid(), Now.AddMinutes(10));
        var handler = new GetOrdersHandler(_orderRepository, new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.HandleAsync(OrganizerId, CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().NotContain(o => o.Id == otherOrganizerOrder.Id);
        result.Should().ContainSingle(o => o.Id == pendingOrder.Id && o.Status == "Pending");
        // 已逾時但持久化狀態仍是 Pending 的訂單，即時狀態 MUST 回報 Expired，不是持久化欄位本身。
        result.Should().ContainSingle(o => o.Id == expiredOrder.Id && o.Status == "Expired");
    }

    [Fact]
    public async Task HandleAsync_WhenNoOrders_ReturnsEmptyList()
    {
        var handler = new GetOrdersHandler(_orderRepository, new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.HandleAsync(OrganizerId, CancellationToken.None);

        result.Should().BeEmpty();
    }
}
