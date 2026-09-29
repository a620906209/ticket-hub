using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Tickets.RedeemTicket;

public sealed class RedeemTicketHandler
{
    private readonly ITicketRepository _ticketRepository;
    private readonly IOrderRepository _orderRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ITicketSigningService _ticketSigningService;

    public RedeemTicketHandler(
        ITicketRepository ticketRepository,
        IOrderRepository orderRepository,
        IUnitOfWork unitOfWork,
        IDateTimeProvider dateTimeProvider,
        ITicketSigningService ticketSigningService)
    {
        _ticketRepository = ticketRepository;
        _orderRepository = orderRepository;
        _unitOfWork = unitOfWork;
        _dateTimeProvider = dateTimeProvider;
        _ticketSigningService = ticketSigningService;
    }

    public async Task<Result> HandleAsync(Guid ticketId, Guid organizerId, string? signature, CancellationToken cancellationToken)
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
        // 不存在與不屬於呼叫端 Organizer 共用同一個 Error，避免以不同回應洩漏票券存在性（RDM-AUTHZ-004）。
        var notFound = Error.NotFound($"Ticket '{ticketId}' was not found.");

        var ticket = await _ticketRepository.GetForUpdateAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return Result.Failure(notFound);
        }

        // 歸屬核對 MUST 在狀態檢查之前，否則其他 Organizer 已核銷的票會以 409 洩漏存在與狀態（RDM-AUTHZ-005）；
        // 用單一投影查詢是為了縮短持鎖時間（design.md Decision 1）。
        var ticketOrganizerId = await _orderRepository.GetOrganizerIdByOrderItemIdAsync(ticket.OrderItemId, cancellationToken)
            ?? throw new InvalidOperationException($"Organizer of ticket '{ticketId}' could not be resolved (order item '{ticket.OrderItemId}').");

        if (ticketOrganizerId != organizerId)
        {
            return Result.Failure(notFound);
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
