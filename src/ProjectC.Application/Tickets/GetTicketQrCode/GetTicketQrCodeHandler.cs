using ProjectC.Application.Common;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Tickets.GetTicketQrCode;

public sealed class GetTicketQrCodeHandler
{
    private readonly ITicketRepository _ticketRepository;
    private readonly IOrderRepository _orderRepository;
    private readonly ITicketQrCodeGenerator _ticketQrCodeGenerator;

    public GetTicketQrCodeHandler(
        ITicketRepository ticketRepository,
        IOrderRepository orderRepository,
        ITicketQrCodeGenerator ticketQrCodeGenerator)
    {
        _ticketRepository = ticketRepository;
        _orderRepository = orderRepository;
        _ticketQrCodeGenerator = ticketQrCodeGenerator;
    }

    public async Task<Result<byte[]>> HandleAsync(Guid ticketId, Guid callerBuyerId, CancellationToken cancellationToken)
    {
        var ticket = await _ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return Result<byte[]>.Failure(Error.NotFound($"Ticket '{ticketId}' was not found."));
        }

        var order = await _orderRepository.GetByOrderItemIdAsync(ticket.OrderItemId, cancellationToken);
        if (order is null)
        {
            return Result<byte[]>.Failure(Error.NotFound($"Order item '{ticket.OrderItemId}' was not found."));
        }

        if (order.BuyerId != callerBuyerId)
        {
            return Result<byte[]>.Failure(Error.Forbidden("You are not the buyer of this ticket."));
        }

        return Result<byte[]>.Success(_ticketQrCodeGenerator.GeneratePng(ticketId));
    }
}
