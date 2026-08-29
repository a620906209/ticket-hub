using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.PurchaseQueue;

namespace ProjectC.Application.PurchaseQueue.GetMyQueueStatus;

public sealed class GetMyQueueStatusHandler
{
    private readonly IEventRepository _eventRepository;
    private readonly IPurchaseQueueRepository _purchaseQueueRepository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public GetMyQueueStatusHandler(
        IEventRepository eventRepository,
        IPurchaseQueueRepository purchaseQueueRepository,
        IDateTimeProvider dateTimeProvider)
    {
        _eventRepository = eventRepository;
        _purchaseQueueRepository = purchaseQueueRepository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<Result<QueueStatusDto>> HandleAsync(Guid eventId, Guid memberId, CancellationToken cancellationToken)
    {
        var @event = await _eventRepository.GetByIdAsync(eventId, cancellationToken);
        if (@event is null)
        {
            return Result<QueueStatusDto>.Failure(Error.NotFound($"Event '{eventId}' was not found."));
        }

        // 「目前紀錄」選取規則：Status IN (Waiting, Admitted, Expired) 範圍內最新一筆；
        // 僅有 Completed 歷史紀錄時查無此範圍，視為尚未加入排隊（design.md 決策 3）。
        var entry = await _purchaseQueueRepository.GetCurrentAsync(eventId, memberId, cancellationToken);
        if (entry is null)
        {
            return Result<QueueStatusDto>.Success(new QueueStatusDto("NotJoined", null, @event.IsQueueModeEnabled));
        }

        var now = _dateTimeProvider.UtcNow;

        if (entry.Status == PurchaseQueueEntryStatus.Waiting)
        {
            var waitingCount = await _purchaseQueueRepository.CountWaitingAheadAsync(eventId, entry.JoinedAtUtc, entry.Id, cancellationToken);
            return Result<QueueStatusDto>.Success(new QueueStatusDto("Waiting", waitingCount, @event.IsQueueModeEnabled));
        }

        if (entry.Status == PurchaseQueueEntryStatus.Admitted)
        {
            // 查詢時即時推導是否已逾時，不落地寫回（design.md 決策 3／purchase-queue spec PQ-STATUS-001）。
            var status = entry.AdmissionExpiresAtUtc <= now ? "Expired" : "Admitted";
            return Result<QueueStatusDto>.Success(new QueueStatusDto(status, null, @event.IsQueueModeEnabled));
        }

        // entry.Status == Expired（已被背景服務或自我修復流程標記）。
        return Result<QueueStatusDto>.Success(new QueueStatusDto("Expired", null, @event.IsQueueModeEnabled));
    }
}
