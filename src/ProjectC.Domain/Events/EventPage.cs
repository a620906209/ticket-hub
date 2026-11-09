namespace ProjectC.Domain.Events;

public sealed record EventPage(IReadOnlyList<Event> Items, int TotalCount);
