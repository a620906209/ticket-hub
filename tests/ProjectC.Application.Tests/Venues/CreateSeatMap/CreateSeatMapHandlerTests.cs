using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Venues.CreateSeatMap;

public class CreateSeatMapHandlerTests
{
    private readonly FakeVenueRepository _venueRepository = new();
    private readonly FakeSeatMapRepository _seatMapRepository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly CreateSeatMapHandler _handler;

    public CreateSeatMapHandlerTests()
    {
        _handler = new CreateSeatMapHandler(_venueRepository, _seatMapRepository, _unitOfWork, new CreateSeatMapRequestValidator());
    }

    private Guid SeedVenue()
    {
        var venue = new Venue(Guid.NewGuid(), "Test Venue");
        _venueRepository.Data.Add(venue);
        return venue.Id;
    }

    [Fact]
    public async Task HandleAsync_WithNoDuplicateSeats_CreatesSeatMap()
    {
        var venueId = SeedVenue();
        var request = new CreateSeatMapRequest([new SeatRequest("A", "1"), new SeatRequest("A", "2")]);

        var result = await _handler.HandleAsync(venueId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _seatMapRepository.Data.Should().ContainSingle(m => m.Id == result.Value && m.Seats.Count == 2);
    }

    [Fact]
    public async Task HandleAsync_WithDuplicateSeatInRequest_ReturnsConflict()
    {
        var venueId = SeedVenue();
        var request = new CreateSeatMapRequest([new SeatRequest("A", "1"), new SeatRequest("A", "1")]);

        var result = await _handler.HandleAsync(venueId, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        _seatMapRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithNonExistentVenue_ReturnsNotFound()
    {
        var request = new CreateSeatMapRequest([new SeatRequest("A", "1")]);

        var result = await _handler.HandleAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        _seatMapRepository.Data.Should().BeEmpty();
    }
}
