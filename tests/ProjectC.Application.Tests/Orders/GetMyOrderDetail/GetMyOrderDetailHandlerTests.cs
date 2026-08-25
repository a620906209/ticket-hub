using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Orders.GetMyOrderDetail;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Tests.Orders.GetMyOrderDetail;

public class GetMyOrderDetailHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HandleAsync_WhenBuyerOwnsPaidOrderWithIssuedTickets_ReturnsItemsWithTicketStatuses()
    {
        var buyerId = Guid.NewGuid();
        var order = CreateOrder(buyerId);
        order.Confirm();
        var ticket = new Ticket(Guid.NewGuid(), order.Items[0].Id, Now);
        var fixture = CreateFixture(order, [ticket]);

        var result = await fixture.Handler.HandleAsync(order.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("Paid");
        result.Value.Items.Should().ContainSingle();
        result.Value.Items[0].Tickets.Should().ContainSingle(ticketDto => ticketDto.Id == ticket.Id && ticketDto.Status == "Issued");
    }

    [Fact]
    public async Task HandleAsync_WhenBuyerOwnsPendingOrderWithoutTickets_ReturnsEmptyTicketList()
    {
        var buyerId = Guid.NewGuid();
        var order = CreateOrder(buyerId);
        var fixture = CreateFixture(order, []);

        var result = await fixture.Handler.HandleAsync(order.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("Pending");
        result.Value.Items.Should().OnlyContain(item => item.Tickets.Count == 0);
    }

    [Fact]
    public async Task HandleAsync_WhenCallerDoesNotOwnOrder_ReturnsForbidden()
    {
        var order = CreateOrder(Guid.NewGuid());
        var fixture = CreateFixture(order, []);

        var result = await fixture.Handler.HandleAsync(order.Id, Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
    }

    [Fact]
    public async Task HandleAsync_WhenOrderDoesNotExist_ReturnsNotFound()
    {
        var handler = new GetMyOrderDetailHandler(
            new FakeOrderRepository(),
            new FakeTicketRepository(),
            new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.HandleAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    private static (GetMyOrderDetailHandler Handler, FakeTicketRepository TicketRepository) CreateFixture(Order order, IEnumerable<Ticket> tickets)
    {
        var orderRepository = new FakeOrderRepository();
        orderRepository.Data.Add(order);
        var ticketRepository = new FakeTicketRepository();
        ticketRepository.Data.AddRange(tickets);
        return (new GetMyOrderDetailHandler(orderRepository, ticketRepository, new FakeDateTimeProvider { UtcNow = Now }), ticketRepository);
    }

    private static Order CreateOrder(Guid buyerId)
        => new(Guid.NewGuid(), Guid.NewGuid(), buyerId, Now.AddMinutes(15), [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), null, 1, 500m)]);
}
