using FluentValidation;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Domain.Events;

namespace ProjectC.Application.Events.SetEventQueueMode;

public sealed class SetEventQueueModeHandler
{
    private readonly IEventRepository _eventRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IValidator<SetEventQueueModeRequest> _validator;
    private readonly IQueryCache _queryCache;

    public SetEventQueueModeHandler(
        IEventRepository eventRepository,
        IUnitOfWork unitOfWork,
        IValidator<SetEventQueueModeRequest> validator,
        IQueryCache queryCache)
    {
        _eventRepository = eventRepository;
        _unitOfWork = unitOfWork;
        _validator = validator;
        _queryCache = queryCache;
    }

    public async Task<Result> HandleAsync(Guid eventId, Guid organizerId, SetEventQueueModeRequest request, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure(Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        // 以 FOR UPDATE 鎖定並讀取活動，而非交易前的 no-tracking GetByIdAsync：兩個後台操作者同時切換同一活動
        // 時，後到者的鎖定讀取會等待先到者提交後才能繼續，讀到的必定是提交後的最新值，不會用切換前的過時
        // 快照覆寫對方剛提交的結果（read-modify-write 遺失更新，審查後發現的問題）。後台切換操作頻率低，不需要
        // 像買家端點那樣另外設計交易前快速失敗路徑。
        var @event = await _eventRepository.GetForUpdateAsync(eventId, cancellationToken);
        // 不屬於呼叫端 Organizer 時視同不存在，共用同一句訊息：避免藉由不同的錯誤結果得知其他 Organizer 的活動存在。
        // 早退時未 commit，交易由 await using 的 DisposeAsync 自動 rollback 並釋放列鎖。
        if (@event is null || @event.OrganizerId != organizerId)
        {
            return Result.Failure(Error.NotFound($"Event '{eventId}' was not found."));
        }

        if (request.Enabled!.Value)
        {
            @event.EnableQueueMode();
        }
        else
        {
            @event.DisableQueueMode();
        }

        _eventRepository.Update(@event);
        await transaction.CommitAsync(cancellationToken);

        await _queryCache.RemoveAsync(GetEventsHandler.CacheKey, cancellationToken);
        return Result.Success();
    }
}
