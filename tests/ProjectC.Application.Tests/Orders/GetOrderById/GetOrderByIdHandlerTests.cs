using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Orders.GetOrderById;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Tests.Orders.GetOrderById;

public class GetOrderByIdHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly FakeOrderRepository _orderRepository = new();
    private readonly FakeEventRepository _eventRepository = new();

    private GetOrderByIdHandler CreateHandler()
        => new(_orderRepository, _eventRepository, new FakeDateTimeProvider { UtcNow = Now });

    private Event SeedEvent()
    {
        var @event = new Event(Guid.NewGuid(), "Concert", Now.AddDays(1), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        _eventRepository.Data.Add(@event);
        return @event;
    }

    [Fact]
    public async Task HandleAsync_WhenOrderExists_ReturnsDetailWithItemsAndLiveStatus()
    {
        var @event = SeedEvent();
        var eventSeatId = Guid.NewGuid();
        // 已逾時但持久化狀態仍是 Pending，驗證明細的 Status 也是即時推導值，跟列表端點語意一致。
        var order = new Order(Guid.NewGuid(), @event.Id, Guid.NewGuid(), Now.AddMinutes(-1), [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), eventSeatId, 1, 500m)]);
        _orderRepository.Data.Add(order);

        var result = await CreateHandler().HandleAsync(order.Id, @event.OrganizerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(order.Id);
        result.Value.Status.Should().Be("Expired");
        result.Value.Items.Should().ContainSingle(i => i.EventSeatId == eventSeatId && i.UnitPrice == 500m);
    }

    [Fact]
    public async Task HandleAsync_WhenOrderDoesNotExist_ReturnsNotFound()
    {
        var result = await CreateHandler().HandleAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    // 對應 AC: ORD-DETAIL-003（其他 Organizer 的訂單與不存在的訂單回傳完全相同的 Error，不洩漏存在性）
    [Fact]
    public async Task HandleAsync_WhenOrderBelongsToOtherOrganizer_ReturnsSameNotFoundErrorAsMissingOrder()
    {
        var @event = SeedEvent();
        var order = new Order(Guid.NewGuid(), @event.Id, Guid.NewGuid(), Now.AddMinutes(10), [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 500m)]);
        _orderRepository.Data.Add(order);
        var handler = CreateHandler();

        var otherOrganizerResult = await handler.HandleAsync(order.Id, Guid.NewGuid(), CancellationToken.None);
        _orderRepository.Data.Clear();
        var missingResult = await handler.HandleAsync(order.Id, @event.OrganizerId, CancellationToken.None);

        otherOrganizerResult.IsSuccess.Should().BeFalse();
        otherOrganizerResult.Value.Should().BeNull();
        otherOrganizerResult.Error.Should().BeEquivalentTo(missingResult.Error);
        otherOrganizerResult.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    // 對應 AC: ORD-DETAIL-004（訂單所屬活動查無代表資料毀損，MUST 大聲失敗，不得當成 404 或放行）
    [Fact]
    public async Task HandleAsync_WhenEventNotFound_ThrowsInvalidOperationException()
    {
        var order = new Order(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(10), [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 500m)]);
        _orderRepository.Data.Add(order);

        var act = () => CreateHandler().HandleAsync(order.Id, Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
