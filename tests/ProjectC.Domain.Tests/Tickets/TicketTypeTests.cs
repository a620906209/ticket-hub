using FluentAssertions;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;

namespace ProjectC.Domain.Tests.Tickets;

public class TicketTypeTests
{
    private static SeatMap CreateSeatMapWithZone(string zoneCode)
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        seatMap.AddSeat(zoneCode, "1");
        return seatMap;
    }

    [Fact]
    public void Constructor_WhenZoneExistsAndPriceIsPositive_CreatesTicketType()
    {
        var seatMap = CreateSeatMapWithZone("A");

        var ticketType = new TicketType(Guid.NewGuid(), Guid.NewGuid(), "A", 500m, seatMap);

        ticketType.Price.Should().Be(500m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WhenPriceIsZeroOrNegative_ThrowsArgumentOutOfRangeException(decimal price)
    {
        var seatMap = CreateSeatMapWithZone("A");

        var act = () => new TicketType(Guid.NewGuid(), Guid.NewGuid(), "A", price, seatMap);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_WhenZoneDoesNotExistInSeatMap_ThrowsInvalidOperationException()
    {
        var seatMap = CreateSeatMapWithZone("A");

        var act = () => new TicketType(Guid.NewGuid(), Guid.NewGuid(), "B", 500m, seatMap);

        act.Should().Throw<InvalidOperationException>();
    }
}
