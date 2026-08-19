using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Orders.GetOrderById;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Tests.Orders.GetOrderById;

public class GetOrderByIdHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HandleAsync_WhenOrderExists_ReturnsDetailWithItemsAndLiveStatus()
    {
        var eventSeatId = Guid.NewGuid();
        // 已逾時但持久化狀態仍是 Pending，驗證明細的 Status 也是即時推導值，跟列表端點語意一致。
        var order = new Order(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(-1), [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), eventSeatId, 1, 500m)]);
        var orderRepository = new FakeOrderRepository();
        orderRepository.Data.Add(order);
        var handler = new GetOrderByIdHandler(orderRepository, new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.HandleAsync(order.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(order.Id);
        result.Value.Status.Should().Be("Expired");
        result.Value.Items.Should().ContainSingle(i => i.EventSeatId == eventSeatId && i.UnitPrice == 500m);
    }

    [Fact]
    public async Task HandleAsync_WhenOrderDoesNotExist_ReturnsNotFound()
    {
        var handler = new GetOrderByIdHandler(new FakeOrderRepository(), new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.HandleAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }
}
