using FluentAssertions;
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

    public GetAdminEventsHandlerTests()
    {
        _handler = new GetAdminEventsHandler(_eventRepository, _dbContext, _eventSeatRepository, _dateTimeProvider);
    }

    private (Event Event, SeatMap SeatMap) SeedEvent(Guid? createdByMemberId = null)
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        var @event = new Event(
            Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), seatMap.Id,
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

        var result = await _handler.HandleAsync(CancellationToken.None);

        result.Single().CreatedByDisplayName.Should().Be("Admin One");
    }

    [Fact]
    public async Task HandleAsync_WithNullCreatedByMemberId_ReturnsNullDisplayName()
    {
        SeedEvent(createdByMemberId: null);

        var result = await _handler.HandleAsync(CancellationToken.None);

        result.Single().CreatedByDisplayName.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_WithCreatedByMemberIdNotFound_ReturnsNullDisplayNameWithoutThrowing()
    {
        SeedEvent(createdByMemberId: Guid.NewGuid());

        var result = await _handler.HandleAsync(CancellationToken.None);

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

        var result = await _handler.HandleAsync(CancellationToken.None);

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

        var result = await _handler.HandleAsync(CancellationToken.None);

        var summary = result.Single();
        summary.AvailableSeatCount.Should().Be(1);
        summary.HeldSeatCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WithNoSeats_ReturnsZeroCounts()
    {
        SeedEvent();

        var result = await _handler.HandleAsync(CancellationToken.None);

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

        var result = await _handler.HandleAsync(CancellationToken.None);

        result.Single(e => e.Id == eventA.Id).AvailableSeatCount.Should().Be(1);
        result.Single(e => e.Id == eventA.Id).HeldSeatCount.Should().Be(0);
        result.Single(e => e.Id == eventB.Id).AvailableSeatCount.Should().Be(1);
        result.Single(e => e.Id == eventB.Id).HeldSeatCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_WithNoEvents_ReturnsEmptyList()
    {
        var result = await _handler.HandleAsync(CancellationToken.None);

        result.Should().BeEmpty();
    }
}
