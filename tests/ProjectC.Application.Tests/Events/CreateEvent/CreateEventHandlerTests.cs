using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Events.CreateEvent;

public class CreateEventHandlerTests
{
    private readonly FakeVenueRepository _venueRepository = new();
    private readonly FakeSeatMapRepository _seatMapRepository = new();
    private readonly FakeEventRepository _eventRepository = new();
    private readonly FakeEventSeatRepository _eventSeatRepository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly CreateEventHandler _handler;

    public CreateEventHandlerTests()
    {
        _handler = new CreateEventHandler(
            _venueRepository, _seatMapRepository, _eventRepository, _eventSeatRepository, _unitOfWork, new CreateEventRequestValidator());
    }

    private (Guid VenueId, Guid SeatMapId) SeedVenueAndSeatMap(int seatCount)
    {
        var venue = new Venue(Guid.NewGuid(), "Test Venue");
        _venueRepository.Data.Add(venue);

        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        for (var i = 0; i < seatCount; i++)
        {
            seatMap.AddSeat("A", $"{i + 1}");
        }
        _seatMapRepository.Data.Add(seatMap);

        return (venue.Id, seatMap.Id);
    }

    [Fact]
    public async Task HandleAsync_WithValidVenueAndSeatMap_CreatesEventAndEventSeats()
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 3);
        var request = new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId);

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _eventRepository.Data.Should().ContainSingle(e => e.Id == result.Value);
        _eventSeatRepository.Data.Should().HaveCount(3);
        _eventSeatRepository.Data.Should().OnlyContain(es => es.EventId == result.Value);
    }

    [Fact]
    public async Task HandleAsync_WithBlankTitle_ReturnsValidationError()
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("  ", DateTime.UtcNow.AddDays(30), venueId, seatMapId);

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _eventRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithDefaultStartAtUtc_ReturnsValidationError()
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("Concert", default, venueId, seatMapId);

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _eventRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithNonExistentVenue_ReturnsNotFound()
    {
        var (_, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), seatMapId);

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        _eventRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithNonExistentSeatMap_ReturnsNotFound()
    {
        var (venueId, _) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, Guid.NewGuid());

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        _eventRepository.Data.Should().BeEmpty();
    }
}
