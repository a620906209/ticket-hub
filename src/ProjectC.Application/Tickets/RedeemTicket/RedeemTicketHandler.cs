using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Tickets.RedeemTicket;

public sealed class RedeemTicketHandler
{
    private readonly ITicketRepository _ticketRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDateTimeProvider _dateTimeProvider;

    public RedeemTicketHandler(ITicketRepository ticketRepository, IUnitOfWork unitOfWork, IDateTimeProvider dateTimeProvider)
    {
        _ticketRepository = ticketRepository;
        _unitOfWork = unitOfWork;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<Result> HandleAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        // 鎖定並讀取（單筆 SELECT ... FOR UPDATE 已同時完成，不需額外 reload，見 design.md 決策 4）。
        var ticket = await _ticketRepository.GetForUpdateAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return Result.Failure(Error.NotFound($"Ticket '{ticketId}' was not found."));
        }

        // 非 Issued 狀態一律拒絕（含 Redeemed；Voided 本次無觸發路徑不可達，但邏輯不特化排除它——見 ticket-redemption spec）。
        if (ticket.Status != TicketStatus.Issued)
        {
            return Result.Failure(Error.Conflict($"Ticket '{ticketId}' is not Issued (current status: '{ticket.Status}')."));
        }

        ticket.Redeem(_dateTimeProvider.UtcNow);

        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }
}
