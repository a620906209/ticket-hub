using FluentAssertions;
using ProjectC.Domain.Events;
using ProjectC.Domain.Venues;

namespace ProjectC.Domain.Tests.Tickets;

public class TicketTypeTests
{
    private static (Event Event, SeatMap SeatMap) CreateEventWithZone(string zoneCode)
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        seatMap.AddSeat(zoneCode, "1");
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), seatMap.Id);
        return (@event, seatMap);
    }

    [Fact]
    public void CreateTicketType_WhenZoneExistsAndPriceIsPositive_CreatesTicketType()
    {
        var (@event, seatMap) = CreateEventWithZone("A");

        var ticketType = @event.CreateTicketType("A", 500m, seatMap);

        ticketType.Price.Should().Be(500m);
        ticketType.EventId.Should().Be(@event.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateTicketType_WhenPriceIsZeroOrNegative_ThrowsArgumentOutOfRangeException(decimal price)
    {
        var (@event, seatMap) = CreateEventWithZone("A");

        var act = () => @event.CreateTicketType("A", price, seatMap);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateTicketType_WhenZoneDoesNotExistInSeatMap_ThrowsInvalidOperationException()
    {
        var (@event, seatMap) = CreateEventWithZone("A");

        var act = () => @event.CreateTicketType("B", 500m, seatMap);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CreateTicketType_WhenSeatMapDoesNotBelongToEvent_ThrowsArgumentException()
    {
        var (@event, _) = CreateEventWithZone("A");
        var (_, otherSeatMap) = CreateEventWithZone("A");

        var act = () => @event.CreateTicketType("A", 500m, otherSeatMap);

        act.Should().Throw<ArgumentException>();
    }
}
