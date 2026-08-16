using ProjectC.Domain.Events;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeEventRepository : IEventRepository
{
    public List<Event> Data { get; } = new();

    public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(e => e.Id == id));

    public void Add(Event @event) => Data.Add(@event);
}
