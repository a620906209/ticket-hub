using ProjectC.Domain.Tickets;

namespace ProjectC.WebApi.Tests.TestSupport;

// 理由同 EventRepositoryCallCounter：計數器須為跨 scope 共用的 Singleton。
public sealed class TicketTypeRepositoryCallCounter
{
    private int _getByEventIdAsyncCallCount;

    public int GetByEventIdAsyncCallCount => _getByEventIdAsyncCallCount;

    public void RecordGetByEventIdAsyncCall() => Interlocked.Increment(ref _getByEventIdAsyncCallCount);
}

// 裝飾器，理由與用法同 CountingEventRepository（query-caching tasks.md 第 2 節開頭）。
public sealed class CountingTicketTypeRepository : ITicketTypeRepository
{
    private readonly ITicketTypeRepository _inner;
    private readonly TicketTypeRepositoryCallCounter _counter;

    public CountingTicketTypeRepository(ITicketTypeRepository inner, TicketTypeRepositoryCallCounter counter)
    {
        _inner = inner;
        _counter = counter;
    }

    public Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => _inner.GetByIdAsync(id, cancellationToken);

    public Task<IReadOnlyList<TicketType>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
    {
        _counter.RecordGetByEventIdAsyncCall();
        return _inner.GetByEventIdAsync(eventId, cancellationToken);
    }

    public void Add(TicketType ticketType) => _inner.Add(ticketType);

    public Task<IReadOnlyList<TicketType>> GetForUpdateAsync(IReadOnlyList<Guid> ticketTypeIds, CancellationToken cancellationToken)
        => _inner.GetForUpdateAsync(ticketTypeIds, cancellationToken);
}
