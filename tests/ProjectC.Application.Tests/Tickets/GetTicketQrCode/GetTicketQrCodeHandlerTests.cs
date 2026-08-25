using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Application.Tickets.GetTicketQrCode;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Tests.Tickets.GetTicketQrCode;

public class GetTicketQrCodeHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HandleAsync_WhenBuyerOwnsIssuedTicket_ReturnsPngBytes()
    {
        var (handler, ticket, buyerId) = CreateFixture();

        var result = await handler.HandleAsync(ticket.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Equal([1, 2, 3]);
    }

    [Fact]
    public async Task HandleAsync_WhenBuyerOwnsRedeemedTicket_ReturnsPngBytes()
    {
        var (handler, ticket, buyerId) = CreateFixture();
        ticket.Redeem(Now);

        var result = await handler.HandleAsync(ticket.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Equal([1, 2, 3]);
    }

    [Fact]
    public async Task HandleAsync_WhenCallerDoesNotOwnTicket_ReturnsForbidden()
    {
        var (handler, ticket, _) = CreateFixture();

        var result = await handler.HandleAsync(ticket.Id, Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
    }

    [Fact]
    public async Task HandleAsync_WhenTicketDoesNotExist_ReturnsNotFound()
    {
        var handler = new GetTicketQrCodeHandler(new FakeTicketRepository(), new FakeOrderRepository(), new FakeTicketQrCodeGenerator());

        var result = await handler.HandleAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    private static (GetTicketQrCodeHandler Handler, Ticket Ticket, Guid BuyerId) CreateFixture()
    {
        var buyerId = Guid.NewGuid();
        var orderItem = new OrderItem(Guid.NewGuid(), Guid.NewGuid(), null, 1, 500m);
        var order = new Order(Guid.NewGuid(), Guid.NewGuid(), buyerId, Now.AddMinutes(15), [orderItem]);
        var ticket = new Ticket(Guid.NewGuid(), orderItem.Id, Now);
        var orderRepository = new FakeOrderRepository();
        orderRepository.Data.Add(order);
        var ticketRepository = new FakeTicketRepository();
        ticketRepository.Data.Add(ticket);
        return (new GetTicketQrCodeHandler(ticketRepository, orderRepository, new FakeTicketQrCodeGenerator()), ticket, buyerId);
    }

    private sealed class FakeTicketQrCodeGenerator : ITicketQrCodeGenerator
    {
        public byte[] GeneratePng(Guid ticketId) => [1, 2, 3];
    }
}
