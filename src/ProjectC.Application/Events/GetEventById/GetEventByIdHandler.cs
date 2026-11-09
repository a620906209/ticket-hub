using ProjectC.Application.Common;
using ProjectC.Application.Events.GetEvents;

namespace ProjectC.Application.Events.GetEventById;

public sealed class GetEventByIdHandler
{
    private readonly GetEventsHandler _getEventsHandler;

    public GetEventByIdHandler(GetEventsHandler getEventsHandler)
    {
        _getEventsHandler = getEventsHandler;
    }

    // 從整份列表（含其快取）取單筆而非 GetByIdAsync：確保與列表同源、失效同步（design 決策 1）。
    public async Task<Result<EventDto>> HandleAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var events = await _getEventsHandler.HandleAsync(cancellationToken);
        var match = events.FirstOrDefault(e => e.Id == eventId);
        return match is null
            ? Result<EventDto>.Failure(Error.NotFound($"Event '{eventId}' was not found."))
            : Result<EventDto>.Success(match);
    }
}
