using ProjectC.Domain.Events;

namespace ProjectC.Infrastructure.Tests.TestSupport;

/// <summary>
/// 包住真正的 <see cref="IEventSeatRepository"/>，讓測試能在「已取得座位 FOR UPDATE 列鎖、尚未 commit」這個確定的時間點暫停
/// （order-placement-p95-phase2 design.md 決策 4 改寫 TP-ORDER-023：Event 改共享鎖後，只有座位列鎖還會讓同座位的下單互等）。
/// </summary>
public sealed class InterceptingEventSeatRepository : IEventSeatRepository
{
    private readonly IEventSeatRepository _inner;

    public InterceptingEventSeatRepository(IEventSeatRepository inner)
    {
        _inner = inner;
    }

    public Func<Task>? AfterGetForUpdateAsync { get; init; }

    public Task<EventSeat?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => _inner.GetByIdAsync(id, cancellationToken);

    public Task<IReadOnlyList<EventSeat>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
        => _inner.GetByEventIdAsync(eventId, cancellationToken);

    public Task<IReadOnlyList<EventSeat>> GetByIdsAsync(IReadOnlyList<Guid> eventSeatIds, CancellationToken cancellationToken)
        => _inner.GetByIdsAsync(eventSeatIds, cancellationToken);

    public Task<IReadOnlyList<EventSeat>> GetByEventIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken)
        => _inner.GetByEventIdsAsync(eventIds, cancellationToken);

    public void AddRange(IEnumerable<EventSeat> eventSeats) => _inner.AddRange(eventSeats);

    public async Task<IReadOnlyList<EventSeat>> GetForUpdateAsync(IReadOnlyList<Guid> eventSeatIds, CancellationToken cancellationToken)
    {
        var eventSeats = await _inner.GetForUpdateAsync(eventSeatIds, cancellationToken);
        if (AfterGetForUpdateAsync is not null)
        {
            await AfterGetForUpdateAsync();
        }

        return eventSeats;
    }
}
