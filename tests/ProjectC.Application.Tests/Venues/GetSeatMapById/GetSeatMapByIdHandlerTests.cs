using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Application.Venues.GetSeatMapById;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Venues.GetSeatMapById;

public class GetSeatMapByIdHandlerTests
{
    private readonly FakeSeatMapRepository _seatMapRepository = new();
    private readonly GetSeatMapByIdHandler _handler;

    public GetSeatMapByIdHandlerTests()
    {
        _handler = new GetSeatMapByIdHandler(_seatMapRepository);
    }

    [Fact]
    public async Task HandleAsync_WithSeatMapBelongingToVenue_ReturnsFullSeatList()
    {
        var venueId = Guid.NewGuid();
        var seatMap = new SeatMap(Guid.NewGuid(), venueId);
        seatMap.AddSeat("A", "1");
        seatMap.AddSeat("A", "2");
        _seatMapRepository.Data.Add(seatMap);

        var result = await _handler.HandleAsync(venueId, seatMap.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Seats.Should().HaveCount(2);
        result.Value!.Seats.Should().Contain(s => s.ZoneCode == "A" && s.SeatNumber == "1");
        result.Value!.Seats.Should().Contain(s => s.ZoneCode == "A" && s.SeatNumber == "2");
    }

    [Fact]
    public async Task HandleAsync_WithNonExistentSeatMap_ReturnsNotFound()
    {
        var result = await _handler.HandleAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task HandleAsync_WithSeatMapBelongingToAnotherVenue_ReturnsNotFound()
    {
        var actualVenueId = Guid.NewGuid();
        var otherVenueId = Guid.NewGuid();
        var seatMap = new SeatMap(Guid.NewGuid(), actualVenueId);
        seatMap.AddSeat("A", "1");
        _seatMapRepository.Data.Add(seatMap);

        var result = await _handler.HandleAsync(otherVenueId, seatMap.Id, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task HandleAsync_WithSeatMapWithoutAnySeats_ReturnsSuccessWithEmptyList()
    {
        var venueId = Guid.NewGuid();
        var seatMap = new SeatMap(Guid.NewGuid(), venueId);
        _seatMapRepository.Data.Add(seatMap);

        var result = await _handler.HandleAsync(venueId, seatMap.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Seats.Should().BeEmpty();
    }
}
