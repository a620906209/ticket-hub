using ProjectC.Domain.Events;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeEventSeatRepository : IEventSeatRepository
{
    public List<EventSeat> Data { get; } = new();

    public Task<EventSeat?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(es => es.Id == id));

    public void AddRange(IEnumerable<EventSeat> eventSeats) => Data.AddRange(eventSeats);

    public Task<IReadOnlyList<EventSeat>> GetForUpdateAsync(IReadOnlyList<Guid> eventSeatIds, CancellationToken cancellationToken)
        => throw new NotSupportedException("Not needed for event-management tests.");
}
