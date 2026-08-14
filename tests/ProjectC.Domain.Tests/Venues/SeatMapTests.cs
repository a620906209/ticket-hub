using FluentAssertions;
using ProjectC.Domain.Venues;

namespace ProjectC.Domain.Tests.Venues;

public class SeatMapTests
{
    [Fact]
    public void AddSeat_WhenZoneAndSeatNumberAreDistinct_AddsAllSeats()
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());

        seatMap.AddSeat("A", "1");
        seatMap.AddSeat("A", "2");
        seatMap.AddSeat("B", "1");

        seatMap.Seats.Should().HaveCount(3);
    }

    [Fact]
    public void AddSeat_WhenZoneAndSeatNumberAlreadyExist_ThrowsInvalidOperationException()
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        seatMap.AddSeat("A", "1");

        var act = () => seatMap.AddSeat("A", "1");

        act.Should().Throw<InvalidOperationException>();
        seatMap.Seats.Should().HaveCount(1);
    }
}
