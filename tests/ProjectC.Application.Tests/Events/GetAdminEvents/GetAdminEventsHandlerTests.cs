using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Paging;
using ProjectC.Application.Events.GetAdminEvents;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Events.GetAdminEvents;

public class GetAdminEventsHandlerTests
{
    private readonly FakeEventRepository _eventRepository = new();
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly FakeEventSeatRepository _eventSeatRepository = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();
    private readonly GetAdminEventsHandler _handler;
    private static readonly Guid OrganizerId = Guid.NewGuid();

    public GetAdminEventsHandlerTests()
    {
        _handler = new GetAdminEventsHandler(_eventRepository, _dbContext, _eventSeatRepository, _dateTimeProvider, new PageRequestValidator());
    }

    private async Task<IReadOnlyList<AdminEventSummaryDto>> ListAsync(PageRequest? pageRequest = null)
    {
        var result = await _handler.HandleAsync(OrganizerId, pageRequest ?? new PageRequest(1, 100), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        return result.Value!.Items;
    }

    private (Event Event, SeatMap SeatMap) SeedEvent(Guid? createdByMemberId = null, Guid? organizerId = null)
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        var @event = new Event(
            Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), seatMap.Id, organizerId ?? OrganizerId,
            createdByMemberId: createdByMemberId, createdAtUtc: createdByMemberId is null ? null : _dateTimeProvider.UtcNow);
        _eventRepository.Data.Add(@event);
        return (@event, seatMap);
    }

    [Fact]
    public async Task HandleAsync_WithCreatedByMemberIdMatchingExistingMember_ReturnsDisplayName()
    {
        var member = Member.Register("admin@example.com", "Admin One", "hash");
        _dbContext.MemberData.Add(member);
        SeedEvent(createdByMemberId: member.Id);

        var result = await ListAsync();

        result.Single().CreatedByDisplayName.Should().Be("Admin One");
    }

    [Fact]
    public async Task HandleAsync_WithNullCreatedByMemberId_ReturnsNullDisplayName()
    {
        SeedEvent(createdByMemberId: null);

        var result = await ListAsync();

        result.Single().CreatedByDisplayName.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_WithCreatedByMemberIdNotFound_ReturnsNullDisplayNameWithoutThrowing()
    {
        SeedEvent(createdByMemberId: Guid.NewGuid());

        var result = await ListAsync();

        result.Single().CreatedByDisplayName.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_WithSeatsInEachStatus_ReturnsCorrectCounts()
    {
        var (@event, seatMap) = SeedEvent();
        seatMap.AddSeat("A", "1");
        var heldSeat = seatMap.AddSeat("A", "2");
        var soldSeat = seatMap.AddSeat("A", "3");
        var eventSeats = @event.CreateEventSeats(seatMap);
        var now = _dateTimeProvider.UtcNow;
        eventSeats.Single(s => s.SeatId == heldSeat.Id).Hold(Guid.NewGuid(), now.AddMinutes(10), now);
        var soldOrderId = Guid.NewGuid();
        var soldEventSeat = eventSeats.Single(s => s.SeatId == soldSeat.Id);
        soldEventSeat.Hold(soldOrderId, now.AddMinutes(10), now);
        soldEventSeat.ConfirmSold(soldOrderId, now);
        _eventSeatRepository.Data.AddRange(eventSeats);

        var result = await ListAsync();

        var summary = result.Single();
        summary.AvailableSeatCount.Should().Be(1);
        summary.HeldSeatCount.Should().Be(1);
        summary.SoldSeatCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_WithExpiredHeldSeat_CountsAsAvailable()
    {
        var (@event, seatMap) = SeedEvent();
        var seat = seatMap.AddSeat("A", "1");
        var eventSeats = @event.CreateEventSeats(seatMap);
        var now = _dateTimeProvider.UtcNow;
        eventSeats.Single(s => s.SeatId == seat.Id).Hold(Guid.NewGuid(), now.AddMinutes(-1), now.AddMinutes(-10));
        _eventSeatRepository.Data.AddRange(eventSeats);

        var result = await ListAsync();

        var summary = result.Single();
        summary.AvailableSeatCount.Should().Be(1);
        summary.HeldSeatCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WithNoSeats_ReturnsZeroCounts()
    {
        SeedEvent();

        var result = await ListAsync();

        var summary = result.Single();
        summary.AvailableSeatCount.Should().Be(0);
        summary.HeldSeatCount.Should().Be(0);
        summary.SoldSeatCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WithMultipleEvents_KeepsSeatCountsIndependent()
    {
        var (eventA, seatMapA) = SeedEvent();
        seatMapA.AddSeat("A", "1");
        _eventSeatRepository.Data.AddRange(eventA.CreateEventSeats(seatMapA));

        var (eventB, seatMapB) = SeedEvent();
        seatMapB.AddSeat("A", "1");
        seatMapB.AddSeat("A", "2");
        var eventSeatsB = eventB.CreateEventSeats(seatMapB);
        var now = _dateTimeProvider.UtcNow;
        eventSeatsB[0].Hold(Guid.NewGuid(), now.AddMinutes(10), now);
        _eventSeatRepository.Data.AddRange(eventSeatsB);

        var result = await ListAsync();

        result.Single(e => e.Id == eventA.Id).AvailableSeatCount.Should().Be(1);
        result.Single(e => e.Id == eventA.Id).HeldSeatCount.Should().Be(0);
        result.Single(e => e.Id == eventB.Id).AvailableSeatCount.Should().Be(1);
        result.Single(e => e.Id == eventB.Id).HeldSeatCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_WithNoEvents_ReturnsEmptyList()
    {
        var result = await ListAsync();

        result.Should().BeEmpty();
    }

    // [EVT-LIST-001] 僅回傳呼叫端目前 Organizer 名下的活動，不含其他 Organizer 的活動。
    [Fact]
    public async Task HandleAsync_WithEventsFromMultipleOrganizers_ReturnsOnlyCallersOrganizerEvents()
    {
        var (ownEvent, _) = SeedEvent(organizerId: OrganizerId);
        SeedEvent(organizerId: Guid.NewGuid());

        var result = await ListAsync();

        result.Should().ContainSingle(e => e.Id == ownEvent.Id);
    }

    // EVT-SALES-009：管理端 DTO 帶原始販售期間值，null 不展開成 StartAtUtc
    [Fact]
    public async Task HandleAsync_WithAndWithoutSalesWindow_MapsRawSalesWindowToDto()
    {
        var startAt = DateTime.UtcNow.AddDays(10);
        var salesStart = startAt.AddDays(-9);
        var salesEnd = startAt.AddDays(-1);
        var withWindow = new Event(Guid.NewGuid(), "With Window", startAt, Guid.NewGuid(), Guid.NewGuid(), OrganizerId,
            salesStartAtUtc: salesStart, salesEndAtUtc: salesEnd);
        _eventRepository.Data.Add(withWindow);
        var (withoutWindow, _) = SeedEvent();

        var result = await ListAsync();

        var withWindowDto = result.Single(e => e.Id == withWindow.Id);
        withWindowDto.SalesStartAtUtc.Should().Be(salesStart);
        withWindowDto.SalesEndAtUtc.Should().Be(salesEnd);
        var withoutWindowDto = result.Single(e => e.Id == withoutWindow.Id);
        withoutWindowDto.SalesStartAtUtc.Should().BeNull();
        withoutWindowDto.SalesEndAtUtc.Should().BeNull();
    }

    // [LP-VALID] 越界回 Validation，Repository 未被呼叫。
    [Theory]
    [InlineData(1, 101, new[] { "PageSize" })]
    [InlineData(0, 20, new[] { "Page" })]
    [InlineData(10001, 20, new[] { "Page" })]
    [InlineData(0, 101, new[] { "Page", "PageSize" })]
    public async Task HandleAsync_WhenPageRequestOutOfRange_ReturnsValidationErrorWithoutQueryingRepository(int page, int pageSize, string[] expectedFields)
    {
        var result = await _handler.HandleAsync(OrganizerId, new PageRequest(page, pageSize), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        result.Error.FieldErrors!.Keys.Should().BeEquivalentTo(expectedFields);
        _eventRepository.GetPageCallCount.Should().Be(0);
        _eventSeatRepository.GetByEventIdsCallCount.Should().Be(0);
    }

    // [EVT-LIST-006] envelope 欄位、最後一頁不足、totalCount 為該 Organizer 的實際總數。
    [Fact]
    public async Task HandleAsync_OnLastPartialPage_ReturnsEnvelopeWithRemainingItemsAndTotalCount()
    {
        for (var i = 0; i < 5; i++) SeedEventCreatedAt(_dateTimeProvider.UtcNow.AddMinutes(i));
        SeedEvent(organizerId: Guid.NewGuid());

        var result = await _handler.HandleAsync(OrganizerId, new PageRequest(3, 2), CancellationToken.None);

        var page = result.Value!;
        page.Page.Should().Be(3);
        page.PageSize.Should().Be(2);
        page.TotalCount.Should().Be(5);
        page.Items.Should().HaveCount(1);
    }

    // [EVT-LIST-006] 超過最後一頁：空 items 與實際 totalCount，不查座位。
    [Fact]
    public async Task HandleAsync_WhenPageBeyondLast_ReturnsEmptyItemsWithActualTotalCount()
    {
        SeedEvent();
        SeedEvent();

        var result = await _handler.HandleAsync(OrganizerId, new PageRequest(5, 20), CancellationToken.None);

        result.Value!.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(2);
        _eventSeatRepository.LastGetByEventIdsIds.Should().BeNullOrEmpty();
    }

    // [EVT-LIST-009] 座位批次查詢只收到當頁活動的 Id。
    [Fact]
    public async Task HandleAsync_OnSecondPage_QueriesSeatsOnlyForEventsOnThatPage()
    {
        var now = _dateTimeProvider.UtcNow;
        var events = Enumerable.Range(0, 5).Select(i => SeedEventCreatedAt(now.AddMinutes(i))).ToList();
        // 新到舊：events[4], [3] | [2], [1] | [0]
        var result = await _handler.HandleAsync(OrganizerId, new PageRequest(2, 2), CancellationToken.None);

        result.Value!.Items.Select(i => i.Id).Should().Equal(events[2].Id, events[1].Id);
        _eventSeatRepository.LastGetByEventIdsIds.Should().BeEquivalentTo(new[] { events[2].Id, events[1].Id });
    }

    // [EVT-LIST-009] 當頁為空時座位查詢收到空集合（不帶任何活動 Id）。
    [Fact]
    public async Task HandleAsync_WhenPageIsEmpty_PassesNoEventIdsToSeatQuery()
    {
        var result = await _handler.HandleAsync(OrganizerId, new PageRequest(1, 20), CancellationToken.None);

        result.Value!.Items.Should().BeEmpty();
        _eventSeatRepository.LastGetByEventIdsIds.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task HandleAsync_PassesCancellationTokenToRepository()
    {
        using var cts = new CancellationTokenSource();
        SeedEvent();

        await _handler.HandleAsync(OrganizerId, new PageRequest(1, 20), cts.Token);

        _eventRepository.LastGetPageToken.Should().Be(cts.Token);
    }

    private Event SeedEventCreatedAt(DateTime createdAtUtc)
    {
        var @event = new Event(
            Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), Guid.NewGuid(), OrganizerId,
            createdByMemberId: Guid.NewGuid(), createdAtUtc: createdAtUtc);
        _eventRepository.Data.Add(@event);
        return @event;
    }
}
