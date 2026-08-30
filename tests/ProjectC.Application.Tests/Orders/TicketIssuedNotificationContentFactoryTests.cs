using FluentAssertions;
using ProjectC.Application.Orders;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Tests.Orders;

public class TicketIssuedNotificationContentFactoryTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private static Order CreateOrderWithItems(params (Guid? EventSeatId, int Quantity)[] items)
    {
        var orderItems = items.Select(i => new OrderItem(Guid.NewGuid(), Guid.NewGuid(), i.EventSeatId, i.Quantity, 500m));
        return new Order(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(15), orderItems);
    }

    private static Event CreateEvent(string title = "Concert")
        => new(Guid.NewGuid(), title, Now.AddDays(1), Guid.NewGuid(), Guid.NewGuid());

    private static Member CreateBuyer(string email = "buyer@example.com")
        => Member.Register(email, "Test Buyer", "hash");

    [Fact]
    public void Create_WhenOrderIsNull_ThrowsInvalidOperationExceptionContainingOrderId()
    {
        var orderId = Guid.NewGuid();

        var act = () => TicketIssuedNotificationContentFactory.Create(orderId, order: null, CreateEvent(), CreateBuyer());

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{orderId}*");
    }

    [Fact]
    public void Create_WhenEventIsNull_ThrowsInvalidOperationExceptionContainingOrderId()
    {
        var order = CreateOrderWithItems((Guid.NewGuid(), 1));

        var act = () => TicketIssuedNotificationContentFactory.Create(order.Id, order, @event: null, CreateBuyer());

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{order.Id}*");
    }

    [Fact]
    public void Create_WhenBuyerIsNull_ThrowsInvalidOperationExceptionContainingOrderId()
    {
        var order = CreateOrderWithItems((Guid.NewGuid(), 1));

        var act = () => TicketIssuedNotificationContentFactory.Create(order.Id, order, CreateEvent(), buyer: null);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{order.Id}*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WhenBuyerEmailIsEmptyOrWhitespace_ThrowsInvalidOperationExceptionContainingOrderId(string email)
    {
        var order = CreateOrderWithItems((Guid.NewGuid(), 1));
        var buyer = CreateBuyer(email);

        var act = () => TicketIssuedNotificationContentFactory.Create(order.Id, order, CreateEvent(), buyer);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{order.Id}*");
    }

    [Fact]
    public void Create_WithValidInput_ReturnsContentWithCorrectFieldsAndSummedTicketCount()
    {
        var order = CreateOrderWithItems((Guid.NewGuid(), 1), (null, 3));
        var @event = CreateEvent("Concert");
        var buyer = CreateBuyer("buyer@example.com");

        var content = TicketIssuedNotificationContentFactory.Create(order.Id, order, @event, buyer);

        content.ToEmail.Should().Be("buyer@example.com");
        content.EventTitle.Should().Be("Concert");
        content.OrderId.Should().Be(order.Id);
        content.TicketCount.Should().Be(4);
    }
}
