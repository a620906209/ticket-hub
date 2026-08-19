using FluentAssertions;
using ProjectC.Application.Orders.GetOrderById;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Tests.Orders.GetOrderById;

public class GetOrderByIdHandlerCountingItemTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HandleAsync_WhenOrderContainsMixedSeatAndCountingItems_ReturnsBothItemShapesCorrectly()
    {
        var eventSeatId = Guid.NewGuid();
        var seatTicketTypeId = Guid.NewGuid();
        var countTicketTypeId = Guid.NewGuid();
        var order = new Order(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(10),
        [
            new OrderItem(Guid.NewGuid(), seatTicketTypeId, eventSeatId, 1, 500m),
            new OrderItem(Guid.NewGuid(), countTicketTypeId, null, 3, 300m),
        ]);
        var orderRepository = new FakeOrderRepository();
        orderRepository.Data.Add(order);
        var handler = new GetOrderByIdHandler(orderRepository, new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.HandleAsync(order.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().ContainSingle(i =>
            i.EventSeatId == eventSeatId && i.TicketTypeId == seatTicketTypeId && i.Quantity == 1 && i.UnitPrice == 500m);
        result.Value.Items.Should().ContainSingle(i =>
            i.EventSeatId == null && i.TicketTypeId == countTicketTypeId && i.Quantity == 3 && i.UnitPrice == 300m);
    }
}
