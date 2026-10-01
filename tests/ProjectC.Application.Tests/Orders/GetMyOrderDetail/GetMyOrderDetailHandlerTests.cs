using System.Reflection;
using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Orders.GetMyOrderDetail;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Orders.GetMyOrderDetail;

public class GetMyOrderDetailHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly Guid _buyerId = Guid.NewGuid();
    private readonly FakeOrderRepository _orderRepository = new();
    private readonly FakeTicketRepository _ticketRepository = new();
    private readonly FakeEventRepository _eventRepository = new();
    private readonly FakeTicketTypeRepository _ticketTypeRepository = new();
    private readonly FakeEventSeatRepository _eventSeatRepository = new();
    private readonly FakeSeatMapRepository _seatMapRepository = new();
    private readonly Event _event;
    private readonly SeatMap _seatMap;
    private readonly IReadOnlyList<EventSeat> _eventSeats;
    private readonly TicketType _seatTicketType;
    private readonly TicketType _countTicketType;

    public GetMyOrderDetailHandlerTests()
    {
        _seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        foreach (var seatNumber in new[] { "12", "13", "14", "15", "16" })
        {
            _seatMap.AddSeat("A", seatNumber);
        }

        _event = new Event(Guid.NewGuid(), "Spring Concert", Now.AddDays(30), _seatMap.VenueId, _seatMap.Id, Guid.NewGuid());
        _eventSeats = _event.CreateEventSeats(_seatMap);
        _seatTicketType = _event.CreateTicketType("A", 500m, _seatMap);
        _countTicketType = _event.CreateCountBasedTicketType("站票", 300m, 100);

        _eventRepository.Data.Add(_event);
        _seatMapRepository.Data.Add(_seatMap);
        _eventSeatRepository.AddRange(_eventSeats);
        _ticketTypeRepository.Data.AddRange([_seatTicketType, _countTicketType]);
    }

    // [BOQ-DETAIL-001]
    [Fact]
    public async Task HandleAsync_WhenBuyerOwnsPaidOrderWithIssuedTickets_ReturnsItemsWithTicketStatuses()
    {
        var order = SeedOrder([SeatItem(0)]);
        order.Confirm();
        var ticket = new Ticket(Guid.NewGuid(), order.Items[0].Id, Now);
        _ticketRepository.Data.Add(ticket);

        var result = await CreateHandler().HandleAsync(order.Id, _buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("Paid");
        result.Value.EventTitle.Should().Be("Spring Concert");
        result.Value.Items.Should().ContainSingle();
        result.Value.Items[0].Tickets.Should().ContainSingle(ticketDto => ticketDto.Id == ticket.Id && ticketDto.Status == "Issued");
    }

    // [BOQ-DETAIL-002]
    [Fact]
    public async Task HandleAsync_WhenBuyerOwnsPendingOrderWithoutTickets_ReturnsEmptyTicketList()
    {
        var order = SeedOrder([SeatItem(0)]);

        var result = await CreateHandler().HandleAsync(order.Id, _buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("Pending");
        result.Value.Items.Should().OnlyContain(item => item.Tickets.Count == 0);
    }

    // [BOQ-DETAIL-003] 本人檢查先於顯示資訊查詢：非本人時不得觸發任何顯示資訊查詢（spec 明訂）。
    [Fact]
    public async Task HandleAsync_WhenCallerDoesNotOwnOrder_ReturnsForbidden()
    {
        var order = SeedOrder([SeatItem(0), CountItem(2)]);

        var result = await CreateHandler().HandleAsync(order.Id, Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
        AssertNoDisplayQueryWasMade();
    }

    // [BOQ-DETAIL-004]
    [Fact]
    public async Task HandleAsync_WhenOrderDoesNotExist_ReturnsNotFound()
    {
        var result = await CreateHandler().HandleAsync(Guid.NewGuid(), _buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        AssertNoDisplayQueryWasMade();
    }

    // [BOQ-DETAIL-DISPLAY-001]
    [Fact]
    public async Task HandleAsync_WithSeatAndCountItems_ReturnsEventTitleSeatLabelAndTicketTypeNames()
    {
        var order = SeedOrder([SeatItem(0), CountItem(2)]);

        var result = await CreateHandler().HandleAsync(order.Id, _buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.EventTitle.Should().Be("Spring Concert");
        var seatItem = result.Value.Items.Single(item => item.EventSeatId is not null);
        seatItem.SeatZoneCode.Should().Be("A");
        seatItem.SeatNumber.Should().Be("12");
        seatItem.TicketTypeName.Should().Be("A");
        var countItem = result.Value.Items.Single(item => item.EventSeatId is null);
        countItem.SeatZoneCode.Should().BeNull();
        countItem.SeatNumber.Should().BeNull();
        countItem.TicketTypeName.Should().Be("站票");
    }

    // [BOQ-DETAIL-DISPLAY-001] 多個不同座位時每個項目要對到自己的座位——單一座位的案例抓不到對錯 key 或取錯座位。
    [Fact]
    public async Task HandleAsync_WithMultipleDistinctSeats_MapsEachItemToItsOwnSeat()
    {
        var order = SeedOrder([SeatItem(2), SeatItem(0), SeatItem(4)]);

        var result = await CreateHandler().HandleAsync(order.Id, _buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Select(item => (item.EventSeatId, item.SeatZoneCode, item.SeatNumber))
            .Should().Equal(
                (_eventSeats[2].Id, "A", "14"),
                (_eventSeats[0].Id, "A", "12"),
                (_eventSeats[4].Id, "A", "16"));
    }

    // [BOQ-DETAIL-DISPLAY-002]
    [Fact]
    public async Task HandleAsync_WhenItemHasNoTicketTypeId_ReturnsNullTicketTypeNameWithoutError()
    {
        var order = SeedOrder([CreateLegacyItemWithoutTicketType(_eventSeats[0].Id)]);

        var result = await CreateHandler().HandleAsync(order.Id, _buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var item = result.Value!.Items.Single();
        item.TicketTypeName.Should().BeNull();
        item.SeatZoneCode.Should().Be("A");
        item.SeatNumber.Should().Be("12");
        result.Value.EventTitle.Should().Be("Spring Concert");
    }

    // [BOQ-DETAIL-DISPLAY-003] 訊息 MUST 帶訂單 Id 與查不到的關聯 Id，是維運定位損毀資料的唯一線索（design.md 決策 2）。
    [Fact]
    public async Task HandleAsync_WhenOrderEventMissing_ThrowsWithOrderAndEventIds()
    {
        var order = SeedOrder([SeatItem(0)]);
        _eventRepository.Data.Clear();

        var act = () => CreateHandler().HandleAsync(order.Id, _buyerId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(order.Id.ToString()).And.Contain(_event.Id.ToString());
    }

    [Fact]
    public async Task HandleAsync_WhenEventSeatMissing_ThrowsWithOrderAndEventSeatIds()
    {
        var order = SeedOrder([SeatItem(0), SeatItem(1)]);
        var missingEventSeat = _eventSeats[1];
        _eventSeatRepository.Data.Remove(missingEventSeat);

        var act = () => CreateHandler().HandleAsync(order.Id, _buyerId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(order.Id.ToString()).And.Contain(missingEventSeat.Id.ToString());
    }

    [Fact]
    public async Task HandleAsync_WhenTicketTypeMissing_ThrowsWithOrderAndTicketTypeIds()
    {
        var order = SeedOrder([SeatItem(0), CountItem(1)]);
        _ticketTypeRepository.Data.Remove(_countTicketType);

        var act = () => CreateHandler().HandleAsync(order.Id, _buyerId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(order.Id.ToString()).And.Contain(_countTicketType.Id.ToString());
    }

    [Fact]
    public async Task HandleAsync_WhenSeatTemplateMissing_ThrowsWithOrderAndSeatIds()
    {
        var order = SeedOrder([SeatItem(0)]);
        var missingSeatId = _eventSeats[0].SeatId;
        _seatMapRepository.Data.Clear();

        var act = () => CreateHandler().HandleAsync(order.Id, _buyerId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(order.Id.ToString()).And.Contain(missingSeatId.ToString());
    }

    // [BOQ-DETAIL-DISPLAY-004] 單元層只能證明 Handler 不逐項呼叫 repository；repository 內部不逐筆查詢由整合測試驗證。
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task HandleAsync_WithSeatItems_QueriesEachDisplaySourceExactlyOnceRegardlessOfItemCount(int seatItemCount)
    {
        var order = SeedOrder(Enumerable.Range(0, seatItemCount).Select(SeatItem).ToList());

        var result = await CreateHandler().HandleAsync(order.Id, _buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _eventRepository.GetByIdCallCount.Should().Be(1);
        _ticketTypeRepository.GetByEventIdCallCount.Should().Be(1);
        _eventSeatRepository.GetByIdsCallCount.Should().Be(1);
        _eventSeatRepository.LastGetByIdsIds.Should().BeEquivalentTo(_eventSeats.Take(seatItemCount).Select(es => es.Id));
        _seatMapRepository.GetSeatsByIdsCallCount.Should().Be(1);
        _seatMapRepository.LastGetSeatsByIdsIds.Should().BeEquivalentTo(_eventSeats.Take(seatItemCount).Select(es => es.SeatId));
        // 不得載入整張座位圖（萬席場館每次開明細都讀上萬筆，design.md 決策 1）。
        _seatMapRepository.GetByIdCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WithOnlyCountItems_DoesNotQuerySeatData()
    {
        var order = SeedOrder([CountItem(1), CountItem(3)]);

        var result = await CreateHandler().HandleAsync(order.Id, _buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _eventSeatRepository.GetByIdsCallCount.Should().Be(0);
        _seatMapRepository.GetSeatsByIdsCallCount.Should().Be(0);
        _seatMapRepository.GetByIdCallCount.Should().Be(0);
    }

    // design.md 決策 4：Handler 只傳遞呼叫端的 token，不建立新的 CancellationTokenSource。
    [Fact]
    public async Task HandleAsync_WithCallerToken_ForwardsTokenToEveryQuery()
    {
        var order = SeedOrder([SeatItem(0), CountItem(1)]);
        using var cancellationTokenSource = new CancellationTokenSource();
        var token = cancellationTokenSource.Token;

        await CreateHandler().HandleAsync(order.Id, _buyerId, token);

        _orderRepository.LastGetByIdToken.Should().Be(token);
        _ticketRepository.LastGetByOrderItemIdsToken.Should().Be(token);
        _eventRepository.LastGetByIdToken.Should().Be(token);
        _ticketTypeRepository.LastGetByEventIdToken.Should().Be(token);
        _eventSeatRepository.LastGetByIdsToken.Should().Be(token);
        _seatMapRepository.LastGetSeatsByIdsToken.Should().Be(token);
    }

    // 取消不得被轉成資料不一致（InvalidOperationException）、空結果或 Result.Failure（design.md 決策 4）。
    [Fact]
    public async Task HandleAsync_WhenQueryIsCancelled_PropagatesOperationCanceledException()
    {
        var order = SeedOrder([SeatItem(0)]);
        var handler = CreateHandler(seatMapRepository: new CancellingSeatMapRepository());

        var act = () => handler.HandleAsync(order.Id, _buyerId, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<OperationCanceledException>();
    }

    // design.md 決策 4：批次查詢先去重，再以 Id 集合比對缺失，不以筆數比較。
    [Fact]
    public async Task HandleAsync_WithDuplicateEventSeatIds_PassesDistinctIdsToRepository()
    {
        var order = SeedOrder([SeatItem(0), SeatItem(0)]);

        var result = await CreateHandler().HandleAsync(order.Id, _buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _eventSeatRepository.LastGetByIdsIds.Should().Equal(_eventSeats[0].Id);
        _seatMapRepository.LastGetSeatsByIdsIds.Should().Equal(_eventSeats[0].SeatId);
    }

    [Fact]
    public async Task HandleAsync_WhenSeatQueryReturnsSameCountButWrongId_ThrowsWithMissingSeatId()
    {
        var order = SeedOrder([SeatItem(0), SeatItem(1)]);
        var missingSeatId = _eventSeats[1].SeatId;
        var unrelatedSeat = _seatMap.Seats[4];
        var handler = CreateHandler(seatMapRepository: new SubstitutingSeatMapRepository(_seatMapRepository, missingSeatId, unrelatedSeat));

        var act = () => handler.HandleAsync(order.Id, _buyerId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(missingSeatId.ToString());
    }

    [Fact]
    public async Task HandleAsync_WhenEventSeatQueryReturnsSameCountButWrongId_ThrowsWithMissingEventSeatId()
    {
        var order = SeedOrder([SeatItem(0), SeatItem(1)]);
        var missingEventSeatId = _eventSeats[1].Id;
        var handler = CreateHandler(eventSeatRepository: new SubstitutingEventSeatRepository(_eventSeatRepository, missingEventSeatId, _eventSeats[4]));

        var act = () => handler.HandleAsync(order.Id, _buyerId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(missingEventSeatId.ToString());
    }

    private void AssertNoDisplayQueryWasMade()
    {
        _eventRepository.GetByIdCallCount.Should().Be(0);
        _ticketTypeRepository.GetByEventIdCallCount.Should().Be(0);
        _eventSeatRepository.GetByIdsCallCount.Should().Be(0);
        _seatMapRepository.GetSeatsByIdsCallCount.Should().Be(0);
        _seatMapRepository.GetByIdCallCount.Should().Be(0);
    }

    private GetMyOrderDetailHandler CreateHandler(
        IEventSeatRepository? eventSeatRepository = null,
        ISeatMapRepository? seatMapRepository = null)
        => new(
            _orderRepository,
            _ticketRepository,
            _eventRepository,
            _ticketTypeRepository,
            eventSeatRepository ?? _eventSeatRepository,
            seatMapRepository ?? _seatMapRepository,
            new FakeDateTimeProvider { UtcNow = Now });

    private Order SeedOrder(IReadOnlyList<OrderItem> items)
    {
        var order = new Order(Guid.NewGuid(), _event.Id, _buyerId, Now.AddMinutes(15), items);
        _orderRepository.Data.Add(order);
        return order;
    }

    private OrderItem SeatItem(int eventSeatIndex)
        => new(Guid.NewGuid(), _seatTicketType.Id, _eventSeats[eventSeatIndex].Id, 1, 500m);

    private OrderItem CountItem(int quantity)
        => new(Guid.NewGuid(), _countTicketType.Id, null, quantity, 300m);

    // TicketTypeId 為 null 只存在於 ticket-type-requires-seat 之前的舊資料，公開建構子不允許建立，
    // 只能走 EF Core 物化用的 private 建構子重現。
    private static OrderItem CreateLegacyItemWithoutTicketType(Guid eventSeatId)
    {
        var constructor = typeof(OrderItem).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            [typeof(Guid), typeof(Guid?), typeof(Guid?), typeof(int), typeof(decimal)])!;
        return (OrderItem)constructor.Invoke([Guid.NewGuid(), null, (Guid?)eventSeatId, 1, 500m]);
    }

    private sealed class CancellingSeatMapRepository : ISeatMapRepository
    {
        public Task<SeatMap?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<SeatMap>> GetByVenueIdAsync(Guid venueId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Seat>> GetSeatsByIdsAsync(IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken)
            => throw new OperationCanceledException();

        public void Add(SeatMap seatMap) => throw new NotSupportedException();
    }

    // 既有 Fake 依傳入 Id 過濾，做不出「筆數相同但 Id 不同」的回傳；以 decorator 把指定 Id 換成無關的座位。
    private sealed class SubstitutingSeatMapRepository(ISeatMapRepository inner, Guid removedSeatId, Seat extraSeat) : ISeatMapRepository
    {
        public Task<SeatMap?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => inner.GetByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<SeatMap>> GetByVenueIdAsync(Guid venueId, CancellationToken cancellationToken)
            => inner.GetByVenueIdAsync(venueId, cancellationToken);

        public async Task<IReadOnlyList<Seat>> GetSeatsByIdsAsync(IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken)
            => (await inner.GetSeatsByIdsAsync(seatIds, cancellationToken)).Where(s => s.Id != removedSeatId).Append(extraSeat).ToList();

        public void Add(SeatMap seatMap) => inner.Add(seatMap);
    }

    private sealed class SubstitutingEventSeatRepository(IEventSeatRepository inner, Guid removedEventSeatId, EventSeat extraEventSeat) : IEventSeatRepository
    {
        public Task<EventSeat?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => inner.GetByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<EventSeat>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
            => inner.GetByEventIdAsync(eventId, cancellationToken);

        public async Task<IReadOnlyList<EventSeat>> GetByIdsAsync(IReadOnlyList<Guid> eventSeatIds, CancellationToken cancellationToken)
            => (await inner.GetByIdsAsync(eventSeatIds, cancellationToken)).Where(es => es.Id != removedEventSeatId).Append(extraEventSeat).ToList();

        public Task<IReadOnlyList<EventSeat>> GetByEventIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken)
            => inner.GetByEventIdsAsync(eventIds, cancellationToken);

        public void AddRange(IEnumerable<EventSeat> eventSeats) => inner.AddRange(eventSeats);

        public Task<IReadOnlyList<EventSeat>> GetForUpdateAsync(IReadOnlyList<Guid> eventSeatIds, CancellationToken cancellationToken)
            => inner.GetForUpdateAsync(eventSeatIds, cancellationToken);
    }
}
