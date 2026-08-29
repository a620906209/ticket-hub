using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.PurchaseQueue;

namespace ProjectC.Application.PurchaseQueue.JoinPurchaseQueue;

public sealed class JoinPurchaseQueueHandler
{
    private readonly IEventRepository _eventRepository;
    private readonly IPurchaseQueueRepository _purchaseQueueRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDateTimeProvider _dateTimeProvider;

    public JoinPurchaseQueueHandler(
        IEventRepository eventRepository,
        IPurchaseQueueRepository purchaseQueueRepository,
        IUnitOfWork unitOfWork,
        IDateTimeProvider dateTimeProvider)
    {
        _eventRepository = eventRepository;
        _purchaseQueueRepository = purchaseQueueRepository;
        _unitOfWork = unitOfWork;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<Result<Guid>> HandleAsync(Guid eventId, Guid memberId, CancellationToken cancellationToken)
    {
        var @event = await _eventRepository.GetByIdAsync(eventId, cancellationToken);
        if (@event is null)
        {
            return Result<Guid>.Failure(Error.NotFound($"Event '{eventId}' was not found."));
        }

        if (!@event.IsQueueModeEnabled)
        {
            return Result<Guid>.Failure(Error.Conflict($"Event '{eventId}' is not in queue mode."));
        }

        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        // Queue Mode 切換的線性化時點，比照 OrderService.PlaceOrderAsync（design.md 決策 4）：上面交易前
        // 的檢查只是快速失敗路徑、不具權威性——若 Admin 在該次讀取之後、本交易鎖定之前關閉熱門搶購模式，
        // 仍會被這裡的重新鎖定攔下，不會讓一筆活動已關閉排隊的 Waiting 紀錄意外建立成功。
        var lockedEvent = await _eventRepository.GetForUpdateAsync(eventId, cancellationToken);
        if (lockedEvent is null)
        {
            return Result<Guid>.Failure(Error.NotFound($"Event '{eventId}' was not found."));
        }

        if (!lockedEvent.IsQueueModeEnabled)
        {
            return Result<Guid>.Failure(Error.Conflict($"Event '{eventId}' is not in queue mode."));
        }

        var now = _dateTimeProvider.UtcNow;

        // 悲觀鎖查詢，依唯一性約束最多一筆（design.md 決策 3 步驟 1-2）。
        var existing = await _purchaseQueueRepository.GetForUpdateAsync(eventId, memberId, cancellationToken);
        if (existing is not null && existing.Status == PurchaseQueueEntryStatus.Admitted && existing.AdmissionExpiresAtUtc <= now)
        {
            // 僅變更追蹤中的 Entity，尚未寫入；視為查無進行中紀錄，繼續下一步（design.md 決策 3 步驟 3）。
            existing.Expire();
            existing = null;
        }

        if (existing is not null)
        {
            // 仍為 Waiting 或未逾時的 Admitted：回傳既有紀錄，不建立新紀錄（Idempotent，決策 3 步驟 4）。
            await transaction.CommitAsync(cancellationToken);
            return Result<Guid>.Success(existing.Id);
        }

        // 查無進行中紀錄（原本就沒有，或剛把逾時紀錄轉為 Expired）：建立新的 Waiting 紀錄。
        // AddOrGetExistingAsync 內部處理併發新增衝突並回傳最終應採用的紀錄，這裡不需要 try/catch
        // unique violation（design.md 決策 3 步驟 5）。
        var newEntry = new PurchaseQueueEntry(Guid.NewGuid(), eventId, memberId, now);
        PurchaseQueueEntry resultEntry;
        try
        {
            resultEntry = await _purchaseQueueRepository.AddOrGetExistingAsync(newEntry, cancellationToken);
        }
        catch (PurchaseQueueJoinConflictException ex)
        {
            // 極端情況：內部重試仍失敗；transaction 未 Commit，await using 區塊結束時自動 Rollback。
            return Result<Guid>.Failure(Error.Conflict(ex.Message));
        }

        await transaction.CommitAsync(cancellationToken);
        return Result<Guid>.Success(resultEntry.Id);
    }
}
