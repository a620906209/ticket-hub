using System.Reflection;
using FluentAssertions;
using ProjectC.Domain.Events;
using ProjectC.Domain.Tickets;
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

    private static TicketType CreateCountBasedTicketType(int availableQuantity = 10)
    {
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), Guid.NewGuid());
        return @event.CreateCountBasedTicketType("站票", 500m, availableQuantity);
    }

    /// <summary>刻意繞過封裝，透過反射呼叫 EF Core 物化專用的 private 建構子，建立
    /// RequiresSeat = false 但 AvailableQuantity = null 的不一致實體——這個狀態透過任何公開/internal
    /// 建構方式都無法產生，只用來測試 Reserve/Release 的防禦性資料完整性檢查（design.md 決策 3）。</summary>
    private static TicketType CreateInconsistentTicketTypeViaReflection()
    {
        var constructor = typeof(TicketType).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: [typeof(Guid), typeof(Guid), typeof(string), typeof(decimal), typeof(bool), typeof(int?)],
            modifiers: null)
            ?? throw new InvalidOperationException("EF materialization constructor not found.");

        return (TicketType)constructor.Invoke([Guid.NewGuid(), Guid.NewGuid(), "站票", 500m, false, null]);
    }

    [Fact]
    public void CreateTicketType_WhenZoneExistsAndPriceIsPositive_CreatesTicketType()
    {
        var (@event, seatMap) = CreateEventWithZone("A");

        var ticketType = @event.CreateTicketType("A", 500m, seatMap);

        ticketType.Price.Should().Be(500m);
        ticketType.EventId.Should().Be(@event.Id);
        ticketType.RequiresSeat.Should().BeTrue();
        ticketType.AvailableQuantity.Should().BeNull();
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

    [Fact]
    public void CreateCountBasedTicketType_WhenQuantityIsPositive_CreatesTicketType()
    {
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), Guid.NewGuid());

        var ticketType = @event.CreateCountBasedTicketType("站票", 500m, 100);

        ticketType.RequiresSeat.Should().BeFalse();
        ticketType.AvailableQuantity.Should().Be(100);
        ticketType.ZoneCode.Should().Be("站票");
        ticketType.Price.Should().Be(500m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateCountBasedTicketType_WhenQuantityIsZeroOrNegative_ThrowsArgumentOutOfRangeException(int quantity)
    {
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), Guid.NewGuid());

        var act = () => @event.CreateCountBasedTicketType("站票", 500m, quantity);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateCountBasedTicketType_WhenPriceIsZeroOrNegative_ThrowsArgumentOutOfRangeException(decimal price)
    {
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), Guid.NewGuid());

        var act = () => @event.CreateCountBasedTicketType("站票", price, 10);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Reserve_WhenQuantityExactlyMatchesAvailable_ReducesToZero()
    {
        var ticketType = CreateCountBasedTicketType(5);

        ticketType.Reserve(5);

        ticketType.AvailableQuantity.Should().Be(0);
    }

    [Fact]
    public void Reserve_WhenQuantityExceedsAvailable_ThrowsTicketTypeInventoryInsufficientException()
    {
        var ticketType = CreateCountBasedTicketType(5);

        var act = () => ticketType.Reserve(6);

        act.Should().Throw<TicketTypeInventoryInsufficientException>();
        ticketType.AvailableQuantity.Should().Be(5);
    }

    [Fact]
    public void Release_AfterReserve_RestoresOriginalQuantity()
    {
        var ticketType = CreateCountBasedTicketType(10);
        ticketType.Reserve(4);

        ticketType.Release(4);

        ticketType.AvailableQuantity.Should().Be(10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Reserve_WhenQuantityIsZeroOrNegative_ThrowsArgumentOutOfRangeException(int quantity)
    {
        var ticketType = CreateCountBasedTicketType();

        var act = () => ticketType.Reserve(quantity);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Release_WhenQuantityIsZeroOrNegative_ThrowsArgumentOutOfRangeException(int quantity)
    {
        var ticketType = CreateCountBasedTicketType();

        var act = () => ticketType.Release(quantity);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Reserve_WhenTicketTypeRequiresSeat_ThrowsTicketTypeRequiresSeatException()
    {
        var (@event, seatMap) = CreateEventWithZone("A");
        var ticketType = @event.CreateTicketType("A", 500m, seatMap);

        var act = () => ticketType.Reserve(1);

        act.Should().Throw<TicketTypeRequiresSeatException>();
    }

    [Fact]
    public void Release_WhenTicketTypeRequiresSeat_ThrowsTicketTypeRequiresSeatException()
    {
        var (@event, seatMap) = CreateEventWithZone("A");
        var ticketType = @event.CreateTicketType("A", 500m, seatMap);

        var act = () => ticketType.Release(1);

        act.Should().Throw<TicketTypeRequiresSeatException>();
    }

    [Fact]
    public void Reserve_WhenAvailableQuantityNotConfigured_ThrowsTicketTypeInventoryNotConfiguredException()
    {
        // 刻意繞過封裝測防禦性檢查，非正常業務路徑——見 CreateInconsistentTicketTypeViaReflection 說明。
        var ticketType = CreateInconsistentTicketTypeViaReflection();

        var act = () => ticketType.Reserve(1);

        act.Should().Throw<TicketTypeInventoryNotConfiguredException>();
    }

    [Fact]
    public void Release_WhenAvailableQuantityNotConfigured_ThrowsTicketTypeInventoryNotConfiguredException()
    {
        // 刻意繞過封裝測防禦性檢查，非正常業務路徑——見 CreateInconsistentTicketTypeViaReflection 說明。
        var ticketType = CreateInconsistentTicketTypeViaReflection();

        var act = () => ticketType.Release(1);

        act.Should().Throw<TicketTypeInventoryNotConfiguredException>();
    }
}
