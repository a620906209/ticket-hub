using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Tickets.RedeemTicket;

public sealed class RedeemTicketHandler
{
    private readonly ITicketRepository _ticketRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ITicketSigningService _ticketSigningService;

    public RedeemTicketHandler(
        ITicketRepository ticketRepository,
        IUnitOfWork unitOfWork,
        IDateTimeProvider dateTimeProvider,
        ITicketSigningService ticketSigningService)
    {
        _ticketRepository = ticketRepository;
        _unitOfWork = unitOfWork;
        _dateTimeProvider = dateTimeProvider;
        _ticketSigningService = ticketSigningService;
    }

    public async Task<Result> HandleAsync(Guid ticketId, string? signature, CancellationToken cancellationToken)
    {
        // signature 非 null 時一律呼叫 TryVerify 讓其自然判定空字串/空白/竄改為失敗，不額外寫特殊分支
        // （design.md 決策 2）；驗證失敗時 MUST NOT 查詢或鎖定 Ticket。
        if (signature is not null)
        {
            var signedContent = $"{ticketId:D}.{signature}";
            if (!_ticketSigningService.TryVerify(signedContent, out _))
            {
                return Result.Failure(Error.InvalidTicketSignature($"Ticket '{ticketId}' signature verification failed."));
            }
        }

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
