using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Application.Venues.GetVenueById;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Venues.GetVenueById;

public class GetVenueByIdHandlerTests
{
    private readonly FakeVenueRepository _venueRepository = new();
    private readonly FakeSeatMapRepository _seatMapRepository = new();
    private readonly GetVenueByIdHandler _handler;

    public GetVenueByIdHandlerTests()
    {
        _handler = new GetVenueByIdHandler(_venueRepository, _seatMapRepository);
    }

    [Fact]
    public async Task HandleAsync_WithMultipleSeatMaps_ReturnsEachSeatMapWithItsOwnSeatCount()
    {
        var venue = new Venue(Guid.NewGuid(), "Test Venue");
        _venueRepository.Data.Add(venue);

        var seatMapWithTwoSeats = new SeatMap(Guid.NewGuid(), venue.Id);
        seatMapWithTwoSeats.AddSeat("A", "1");
        seatMapWithTwoSeats.AddSeat("A", "2");
        var seatMapWithOneSeat = new SeatMap(Guid.NewGuid(), venue.Id);
        seatMapWithOneSeat.AddSeat("B", "1");
        _seatMapRepository.Data.Add(seatMapWithTwoSeats);
        _seatMapRepository.Data.Add(seatMapWithOneSeat);

        var result = await _handler.HandleAsync(venue.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.SeatMaps.Should().BeEquivalentTo(
        [
            new SeatMapSummaryDto(seatMapWithTwoSeats.Id, 2),
            new SeatMapSummaryDto(seatMapWithOneSeat.Id, 1),
        ]);
    }

    [Fact]
    public async Task HandleAsync_WithVenueWithoutSeatMaps_ReturnsEmptySeatMapsList()
    {
        var venue = new Venue(Guid.NewGuid(), "Test Venue");
        _venueRepository.Data.Add(venue);

        var result = await _handler.HandleAsync(venue.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.SeatMaps.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithNonExistentVenue_ReturnsNotFound()
    {
        var result = await _handler.HandleAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task HandleAsync_WithSeatMapWithoutAnySeats_ReturnsZeroSeatCount()
    {
        var venue = new Venue(Guid.NewGuid(), "Test Venue");
        _venueRepository.Data.Add(venue);
        var emptySeatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        _seatMapRepository.Data.Add(emptySeatMap);

        var result = await _handler.HandleAsync(venue.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.SeatMaps.Should().ContainSingle(s => s.Id == emptySeatMap.Id && s.SeatCount == 0);
    }
}
