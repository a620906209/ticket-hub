using Microsoft.Extensions.Logging;
using ProjectC.Application.Common;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Tickets.GetTicketHolder;

public sealed class GetTicketHolderHandler
{
    private readonly ITicketRepository _ticketRepository;
    private readonly IOrderRepository _orderRepository;
    private readonly IMemberRealNameRepository _memberRealNameRepository;
    private readonly ILogger<GetTicketHolderHandler> _logger;

    public GetTicketHolderHandler(
        ITicketRepository ticketRepository,
        IOrderRepository orderRepository,
        IMemberRealNameRepository memberRealNameRepository,
        ILogger<GetTicketHolderHandler> logger)
    {
        _ticketRepository = ticketRepository;
        _orderRepository = orderRepository;
        _memberRealNameRepository = memberRealNameRepository;
        _logger = logger;
    }

    public async Task<Result<TicketHolderDto>> HandleAsync(Guid ticketId, Guid organizerId, Guid callerMemberId, CancellationToken cancellationToken)
    {
        // 唯讀查詢不加鎖；不存在與不屬於呼叫端共用同一個 Error（與核銷一致），避免洩漏票券存在性（RDM-HOLDER-003／010）。
        var ticket = await _ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            LogAudit(ticketId, callerMemberId, organizerId, "NotFound");
            return Result<TicketHolderDto>.Failure(TicketErrors.NotFound(ticketId));
        }

        var redemptionContext = await _orderRepository.GetRedemptionContextByOrderItemIdAsync(ticket.OrderItemId, cancellationToken)
            ?? throw new InvalidOperationException($"Organizer of ticket '{ticketId}' could not be resolved (order item '{ticket.OrderItemId}').");

        if (redemptionContext.OrganizerId != organizerId)
        {
            LogAudit(ticketId, callerMemberId, organizerId, "NotFound");
            return Result<TicketHolderDto>.Failure(TicketErrors.NotFound(ticketId));
        }

        // 只限 Issued：核銷後即不可再讀實名，縮小個資暴露窗口（RDM-HOLDER-008）。
        if (ticket.Status != TicketStatus.Issued)
        {
            LogAudit(ticketId, callerMemberId, organizerId, "Conflict");
            return Result<TicketHolderDto>.Failure(Error.Conflict($"Ticket '{ticketId}' is not Issued."));
        }

        MemberRealName? holderRealName = null;
        if (redemptionContext.IsRealNameRequired)
        {
            // 下單閘門保證需實名活動的買家必已登記，且實名只增不減；查不到代表資料損毀（RDM-HOLDER-007）。
            holderRealName = await _memberRealNameRepository.GetAsync(redemptionContext.BuyerId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Ticket '{ticketId}' requires real name but buyer '{redemptionContext.BuyerId}' has none registered.");
        }

        _logger.LogInformation(
            "Ticket holder lookup for ticket {TicketId} by member {CallerMemberId} under organizer {OrganizerId}: {Result}, holder member {HolderMemberId}.",
            ticketId, callerMemberId, organizerId, "Success", redemptionContext.BuyerId);

        return Result<TicketHolderDto>.Success(new TicketHolderDto(
            ticketId,
            ticket.Status.ToString(),
            redemptionContext.IsRealNameRequired,
            holderRealName?.RealName,
            holderRealName?.NationalIdLast4));
    }

    private void LogAudit(Guid ticketId, Guid callerMemberId, Guid organizerId, string result)
        => _logger.LogInformation(
            "Ticket holder lookup for ticket {TicketId} by member {CallerMemberId} under organizer {OrganizerId}: {Result}.",
            ticketId, callerMemberId, organizerId, result);
}
