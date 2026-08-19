using FluentAssertions;
using ProjectC.Domain.Orders;

namespace ProjectC.Domain.Tests.Orders;

public class OrderItemTests
{
    [Fact]
    public void Constructor_WhenSeatShape_CreatesOrderItem()
    {
        var eventSeatId = Guid.NewGuid();

        var item = new OrderItem(Guid.NewGuid(), Guid.NewGuid(), eventSeatId, 1, 500m);

        item.EventSeatId.Should().Be(eventSeatId);
        item.Quantity.Should().Be(1);
    }

    [Fact]
    public void Constructor_WhenCountingShape_CreatesOrderItem()
    {
        var ticketTypeId = Guid.NewGuid();

        var item = new OrderItem(Guid.NewGuid(), ticketTypeId, null, 3, 500m);

        item.TicketTypeId.Should().Be(ticketTypeId);
        item.EventSeatId.Should().BeNull();
        item.Quantity.Should().Be(3);
    }

    [Fact]
    public void Constructor_WhenSeatItemHasQuantityGreaterThanOne_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, 500m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_WhenCountingItemHasQuantityZero_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new OrderItem(Guid.NewGuid(), Guid.NewGuid(), null, 0, 500m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_WhenTicketTypeIdIsEmpty_ThrowsArgumentException()
    {
        var act = () => new OrderItem(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), 1, 500m);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WhenUnitPriceIsZeroOrNegative_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 0m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
