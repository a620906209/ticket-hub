using FluentValidation;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Events.CreateEvent;

public sealed class CreateEventHandler
{
    private readonly IVenueRepository _venueRepository;
    private readonly ISeatMapRepository _seatMapRepository;
    private readonly IEventRepository _eventRepository;
    private readonly IEventSeatRepository _eventSeatRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IValidator<CreateEventRequest> _validator;

    public CreateEventHandler(
        IVenueRepository venueRepository,
        ISeatMapRepository seatMapRepository,
        IEventRepository eventRepository,
        IEventSeatRepository eventSeatRepository,
        IUnitOfWork unitOfWork,
        IValidator<CreateEventRequest> validator)
    {
        _venueRepository = venueRepository;
        _seatMapRepository = seatMapRepository;
        _eventRepository = eventRepository;
        _eventSeatRepository = eventSeatRepository;
        _unitOfWork = unitOfWork;
        _validator = validator;
    }

    public async Task<Result<Guid>> HandleAsync(CreateEventRequest request, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<Guid>.Failure(Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        var venue = await _venueRepository.GetByIdAsync(request.VenueId, cancellationToken);
        if (venue is null)
        {
            return Result<Guid>.Failure(Error.NotFound($"Venue '{request.VenueId}' was not found."));
        }

        var seatMap = await _seatMapRepository.GetByIdAsync(request.SeatMapId, cancellationToken);
        if (seatMap is null || seatMap.VenueId != request.VenueId)
        {
            return Result<Guid>.Failure(Error.NotFound($"Seat map '{request.SeatMapId}' was not found."));
        }

        var @event = new Event(
            Guid.NewGuid(),
            request.Title,
            request.StartAtUtc,
            request.VenueId,
            request.SeatMapId,
            request.Description,
            request.PosterUrl,
            request.MaxTicketsPerOrder);
        var eventSeats = @event.CreateEventSeats(seatMap);

        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        _eventRepository.Add(@event);
        _eventSeatRepository.AddRange(eventSeats);
        await transaction.CommitAsync(cancellationToken);

        return Result<Guid>.Success(@event.Id);
    }
}
