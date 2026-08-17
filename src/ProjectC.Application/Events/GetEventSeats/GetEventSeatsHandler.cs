using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Events.GetEventSeats;

public sealed class GetEventSeatsHandler
{
    private readonly IEventRepository _eventRepository;
    private readonly IEventSeatRepository _eventSeatRepository;
    private readonly ISeatMapRepository _seatMapRepository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public GetEventSeatsHandler(
        IEventRepository eventRepository,
        IEventSeatRepository eventSeatRepository,
        ISeatMapRepository seatMapRepository,
        IDateTimeProvider dateTimeProvider)
    {
        _eventRepository = eventRepository;
        _eventSeatRepository = eventSeatRepository;
        _seatMapRepository = seatMapRepository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<Result<IReadOnlyList<EventSeatDto>>> HandleAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var @event = await _eventRepository.GetByIdAsync(eventId, cancellationToken);
        if (@event is null)
        {
            return Result<IReadOnlyList<EventSeatDto>>.Failure(Error.NotFound($"Event '{eventId}' was not found."));
        }

        // 理論上一定查得到：Events.SeatMapId 有 FK 約束指向 SeatMaps，這次也沒有任何 Delete 端點。
        // 仍防禦性處理，避免對 null 解參考（比照 CreateTicketTypeHandler 的既有慣例）。
        var seatMap = await _seatMapRepository.GetByIdAsync(@event.SeatMapId, cancellationToken);
        if (seatMap is null)
        {
            return Result<IReadOnlyList<EventSeatDto>>.Failure(Error.NotFound($"Seat map '{@event.SeatMapId}' was not found."));
        }

        var seatTemplatesById = seatMap.Seats.ToDictionary(s => s.Id);
        var eventSeats = await _eventSeatRepository.GetByEventIdAsync(eventId, cancellationToken);
        var now = _dateTimeProvider.UtcNow;

        var dtos = new List<EventSeatDto>(eventSeats.Count);
        foreach (var eventSeat in eventSeats)
        {
            if (!seatTemplatesById.TryGetValue(eventSeat.SeatId, out var seatTemplate))
            {
                return Result<IReadOnlyList<EventSeatDto>>.Failure(
                    Error.NotFound($"Seat '{eventSeat.SeatId}' was not found in the seat map."));
            }

            dtos.Add(new EventSeatDto(eventSeat.Id, seatTemplate.ZoneCode, seatTemplate.SeatNumber, eventSeat.GetStatus(now).ToString()));
        }

        return Result<IReadOnlyList<EventSeatDto>>.Success(dtos);
    }
}
