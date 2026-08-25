using FluentAssertions;
using ProjectC.Application.Orders.GetMyOrders;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Tests.Orders.GetMyOrders;

public class GetMyOrdersHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HandleAsync_WhenBuyerHasOrders_ReturnsOnlyThatBuyersOrderSummaries()
    {
        var buyerId = Guid.NewGuid();
        var ownOrder = CreateOrder(buyerId);
        var otherOrder = CreateOrder(Guid.NewGuid());
        var orderRepository = new FakeOrderRepository();
        orderRepository.Data.AddRange([ownOrder, otherOrder]);
        var handler = new GetMyOrdersHandler(orderRepository, new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.HandleAsync(buyerId, CancellationToken.None);

        result.Should().ContainSingle();
        result.Single().Id.Should().Be(ownOrder.Id);
        result.Single().EventId.Should().Be(ownOrder.EventId);
        result.Single().Status.Should().Be("Pending");
        result.Single().HeldUntilUtc.Should().Be(ownOrder.HeldUntilUtc);
    }

    [Fact]
    public async Task HandleAsync_WhenBuyerHasNoOrders_ReturnsEmptyList()
    {
        var handler = new GetMyOrdersHandler(new FakeOrderRepository(), new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.HandleAsync(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    private static Order CreateOrder(Guid buyerId)
        => new(Guid.NewGuid(), Guid.NewGuid(), buyerId, Now.AddMinutes(15), [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), null, 1, 500m)]);
}
