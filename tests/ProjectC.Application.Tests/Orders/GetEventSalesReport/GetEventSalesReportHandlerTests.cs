using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Orders.GetEventSalesReport;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Orders.GetEventSalesReport;

public class GetEventSalesReportHandlerTests
{
    private readonly FakeEventRepository _eventRepository = new();
    private readonly FakeTicketTypeRepository _ticketTypeRepository = new();
    private readonly FakeOrderRepository _orderRepository = new();
    private readonly GetEventSalesReportHandler _handler;

    public GetEventSalesReportHandlerTests()
    {
        _handler = new GetEventSalesReportHandler(_eventRepository, _ticketTypeRepository, _orderRepository);
    }

    private Event SeedEvent()
    {
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), Guid.NewGuid());
        _eventRepository.Data.Add(@event);
        return @event;
    }

    [Fact]
    public async Task HandleAsync_WhenEventDoesNotExist_ReturnsNotFound()
    {
        var result = await _handler.HandleAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task HandleAsync_WithSalesRecorded_ReturnsCorrectTotalsAndByTicketTypeDetail()
    {
        var @event = SeedEvent();
        var ticketType = @event.CreateCountBasedTicketType("VIP", 300m, 100);
        _ticketTypeRepository.Data.Add(ticketType);
        _orderRepository.PaidItemSalesGroups = [new OrderItemSalesGroup(ticketType.Id, ItemCount: 2, QuantitySold: 5, Revenue: 1500m)];

        var result = await _handler.HandleAsync(@event.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.TotalRevenue.Should().Be(1500m);
        result.Value.TotalTicketsSold.Should().Be(5);
        var detail = result.Value.ByTicketType.Single();
        detail.TicketTypeId.Should().Be(ticketType.Id);
        detail.QuantitySold.Should().Be(5);
        detail.Revenue.Should().Be(1500m);
    }

    [Fact]
    public async Task HandleAsync_WithMixedSeatAndCountTicketTypes_ListsEachDetailAndSumsTotals()
    {
        var venue = new Venue(Guid.NewGuid(), "Test Venue");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        seatMap.AddSeat("A", "1");
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), venue.Id, seatMap.Id);
        _eventRepository.Data.Add(@event);
        var seatTicketType = @event.CreateTicketType("A", 500m, seatMap);
        var countTicketType = @event.CreateCountBasedTicketType("VIP", 300m, 100);
        _ticketTypeRepository.Data.Add(seatTicketType);
        _ticketTypeRepository.Data.Add(countTicketType);
        _orderRepository.PaidItemSalesGroups =
        [
            new OrderItemSalesGroup(seatTicketType.Id, ItemCount: 1, QuantitySold: 1, Revenue: 500m),
            new OrderItemSalesGroup(countTicketType.Id, ItemCount: 1, QuantitySold: 3, Revenue: 900m),
        ];

        var result = await _handler.HandleAsync(@event.Id, CancellationToken.None);

        result.Value!.ByTicketType.Should().HaveCount(2);
        result.Value.TotalRevenue.Should().Be(1400m);
        result.Value.TotalTicketsSold.Should().Be(4);
    }

    [Fact]
    public async Task HandleAsync_WithNoSales_ReturnsZeroTotalsWithoutError()
    {
        var @event = SeedEvent();
        var ticketType = @event.CreateCountBasedTicketType("VIP", 300m, 100);
        _ticketTypeRepository.Data.Add(ticketType);

        var result = await _handler.HandleAsync(@event.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.TotalRevenue.Should().Be(0m);
        result.Value.TotalTicketsSold.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WhenTicketTypeHasNoSales_StillListsItWithZeroValues()
    {
        var @event = SeedEvent();
        var soldTicketType = @event.CreateCountBasedTicketType("VIP", 300m, 100);
        var unsoldTicketType = @event.CreateCountBasedTicketType("General", 100m, 100);
        _ticketTypeRepository.Data.Add(soldTicketType);
        _ticketTypeRepository.Data.Add(unsoldTicketType);
        _orderRepository.PaidItemSalesGroups = [new OrderItemSalesGroup(soldTicketType.Id, ItemCount: 1, QuantitySold: 1, Revenue: 300m)];

        var result = await _handler.HandleAsync(@event.Id, CancellationToken.None);

        var unsoldDetail = result.Value!.ByTicketType.Single(t => t.TicketTypeId == unsoldTicketType.Id);
        unsoldDetail.QuantitySold.Should().Be(0);
        unsoldDetail.Revenue.Should().Be(0m);
    }

    [Fact]
    public async Task HandleAsync_WithPendingOrCancelledOrders_ExcludesThemFromTotals()
    {
        // Pending/Cancelled 訂單不計入是 GetPaidItemSalesByEventIdAsync 查詢本身的職責（只回傳 Paid 訂單的分組，
        // 見 Infrastructure 整合測試），Handler 只單純加總 Repository 回傳的分組——這裡驗證 Handler 不會額外
        // 過濾或重複過濾，Fake 回傳的分組本身已經只代表 Paid 訂單的資料。
        var @event = SeedEvent();
        var ticketType = @event.CreateCountBasedTicketType("VIP", 300m, 100);
        _ticketTypeRepository.Data.Add(ticketType);
        _orderRepository.PaidItemSalesGroups = [new OrderItemSalesGroup(ticketType.Id, ItemCount: 1, QuantitySold: 5, Revenue: 1500m)];

        var result = await _handler.HandleAsync(@event.Id, CancellationToken.None);

        result.Value!.TotalRevenue.Should().Be(1500m);
        result.Value.TotalTicketsSold.Should().Be(5);
    }

    [Fact]
    public async Task HandleAsync_WithNullTicketTypeIdGroup_ExcludesFromDetailButIncludesInTotalsAndUnclassifiedFields()
    {
        var @event = SeedEvent();
        _orderRepository.PaidItemSalesGroups = [new OrderItemSalesGroup(TicketTypeId: null, ItemCount: 1, QuantitySold: 1, Revenue: 500m)];

        var result = await _handler.HandleAsync(@event.Id, CancellationToken.None);

        result.Value!.ByTicketType.Should().BeEmpty();
        result.Value.TotalRevenue.Should().Be(500m);
        result.Value.TotalTicketsSold.Should().Be(1);
        result.Value.UnclassifiedItemCount.Should().Be(1);
        result.Value.UnclassifiedTicketsSold.Should().Be(1);
        result.Value.UnclassifiedRevenue.Should().Be(500m);
    }

    [Fact]
    public async Task HandleAsync_WithGroupForTicketTypeOfAnotherEvent_TreatsAsUnclassified()
    {
        var @event = SeedEvent();
        var otherEvent = SeedEvent();
        var otherEventTicketType = otherEvent.CreateCountBasedTicketType("VIP", 300m, 100);
        // 刻意不把 otherEventTicketType 加進 _ticketTypeRepository 的「本活動」票種清單——
        // FakeTicketTypeRepository.GetByEventIdAsync 依 EventId 過濾，otherEventTicketType.EventId 是 otherEvent.Id，
        // 所以查詢 @event.Id 時不會回傳它，模擬「TicketTypeId 有值但不屬於本活動」的資料異常情境。
        _ticketTypeRepository.Data.Add(otherEventTicketType);
        _orderRepository.PaidItemSalesGroups = [new OrderItemSalesGroup(otherEventTicketType.Id, ItemCount: 1, QuantitySold: 2, Revenue: 600m)];

        var result = await _handler.HandleAsync(@event.Id, CancellationToken.None);

        result.Value!.ByTicketType.Should().BeEmpty();
        result.Value.TotalRevenue.Should().Be(600m);
        result.Value.TotalTicketsSold.Should().Be(2);
        result.Value.UnclassifiedItemCount.Should().Be(1);
        result.Value.UnclassifiedTicketsSold.Should().Be(2);
        result.Value.UnclassifiedRevenue.Should().Be(600m);
    }

    [Fact]
    public async Task HandleAsync_WithAllGroupsMatchingEventTicketTypes_HasZeroUnclassifiedFields()
    {
        var @event = SeedEvent();
        var ticketType = @event.CreateCountBasedTicketType("VIP", 300m, 100);
        _ticketTypeRepository.Data.Add(ticketType);
        _orderRepository.PaidItemSalesGroups = [new OrderItemSalesGroup(ticketType.Id, ItemCount: 1, QuantitySold: 1, Revenue: 300m)];

        var result = await _handler.HandleAsync(@event.Id, CancellationToken.None);

        result.Value!.UnclassifiedItemCount.Should().Be(0);
        result.Value.UnclassifiedTicketsSold.Should().Be(0);
        result.Value.UnclassifiedRevenue.Should().Be(0m);
    }

    [Fact]
    public async Task HandleAsync_WithNoTicketTypesAndNoOrders_ReturnsAllZerosAndEmptyDetail()
    {
        var @event = SeedEvent();

        var result = await _handler.HandleAsync(@event.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.TotalRevenue.Should().Be(0m);
        result.Value.TotalTicketsSold.Should().Be(0);
        result.Value.ByTicketType.Should().BeEmpty();
        result.Value.UnclassifiedItemCount.Should().Be(0);
        result.Value.UnclassifiedTicketsSold.Should().Be(0);
        result.Value.UnclassifiedRevenue.Should().Be(0m);
    }
}
