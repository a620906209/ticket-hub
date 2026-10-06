using ProjectC.Domain.Events;

namespace ProjectC.Infrastructure.Tests.TestSupport;

/// <summary>
/// 包住真正的 <see cref="IEventRepository"/>，讓測試能在「已取得 FOR UPDATE 列鎖、尚未 commit」這個確定的時間點
/// 注入取消或例外，不依賴計時（purchase-queue-organizer-scoping tasks.md 4.15b／4.15c）。
/// </summary>
public sealed class InterceptingEventRepository : IEventRepository
{
    private readonly IEventRepository _inner;

    public InterceptingEventRepository(IEventRepository inner)
    {
        _inner = inner;
    }

    public Action? AfterGetForUpdate { get; init; }

    /// <summary>非同步版本：持有列鎖期間等待測試放行，不必阻塞執行緒（order-placement-p95-optimization TP-ORDER-023）。</summary>
    public Func<Task>? AfterGetForUpdateAsync { get; init; }

    public Action? OnUpdate { get; init; }

    public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => _inner.GetByIdAsync(id, cancellationToken);

    public Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken)
        => _inner.GetAllAsync(cancellationToken);

    public Task<IReadOnlyList<Event>> GetByOrganizerIdAsync(Guid organizerId, CancellationToken cancellationToken)
        => _inner.GetByOrganizerIdAsync(organizerId, cancellationToken);

    public Task<IReadOnlyList<Event>> GetByIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken)
        => _inner.GetByIdsAsync(eventIds, cancellationToken);

    public void Add(Event @event) => _inner.Add(@event);

    public void Update(Event @event)
    {
        OnUpdate?.Invoke();
        _inner.Update(@event);
    }

    public async Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var @event = await _inner.GetForUpdateAsync(eventId, cancellationToken);
        AfterGetForUpdate?.Invoke();
        if (AfterGetForUpdateAsync is not null)
        {
            await AfterGetForUpdateAsync();
        }

        return @event;
    }
}
