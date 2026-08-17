using ProjectC.Application.Common;
using ProjectC.Domain.Events;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Tickets.GetTicketTypes;

public sealed class GetTicketTypesHandler
{
    private readonly IEventRepository _eventRepository;
    private readonly ITicketTypeRepository _ticketTypeRepository;

    public GetTicketTypesHandler(IEventRepository eventRepository, ITicketTypeRepository ticketTypeRepository)
    {
        _eventRepository = eventRepository;
        _ticketTypeRepository = ticketTypeRepository;
    }

    public async Task<Result<IReadOnlyList<TicketTypeDto>>> HandleAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var @event = await _eventRepository.GetByIdAsync(eventId, cancellationToken);
        if (@event is null)
        {
            return Result<IReadOnlyList<TicketTypeDto>>.Failure(Error.NotFound($"Event '{eventId}' was not found."));
        }

        var ticketTypes = await _ticketTypeRepository.GetByEventIdAsync(eventId, cancellationToken);
        IReadOnlyList<TicketTypeDto> dtos = ticketTypes.Select(t => new TicketTypeDto(t.Id, t.ZoneCode, t.Price)).ToList();

        return Result<IReadOnlyList<TicketTypeDto>>.Success(dtos);
    }
}
