namespace ProjectC.Domain.Events;

public interface IEventRepository
{
    Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    void Add(Event @event);
}
