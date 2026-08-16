using FluentValidation;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Venues.CreateSeatMap;

public sealed class CreateSeatMapHandler
{
    private readonly IVenueRepository _venueRepository;
    private readonly ISeatMapRepository _seatMapRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IValidator<CreateSeatMapRequest> _validator;

    public CreateSeatMapHandler(
        IVenueRepository venueRepository,
        ISeatMapRepository seatMapRepository,
        IUnitOfWork unitOfWork,
        IValidator<CreateSeatMapRequest> validator)
    {
        _venueRepository = venueRepository;
        _seatMapRepository = seatMapRepository;
        _unitOfWork = unitOfWork;
        _validator = validator;
    }

    public async Task<Result<Guid>> HandleAsync(Guid venueId, CreateSeatMapRequest request, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<Guid>.Failure(Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        var venue = await _venueRepository.GetByIdAsync(venueId, cancellationToken);
        if (venue is null)
        {
            return Result<Guid>.Failure(Error.NotFound($"Venue '{venueId}' was not found."));
        }

        var hasDuplicateSeat = request.Seats
            .GroupBy(s => (s.ZoneCode, s.SeatNumber))
            .Any(g => g.Count() > 1);
        if (hasDuplicateSeat)
        {
            return Result<Guid>.Failure(Error.Conflict("The seat map contains duplicate zone code and seat number combinations."));
        }

        var seatMap = new SeatMap(Guid.NewGuid(), venueId);
        try
        {
            foreach (var seat in request.Seats)
            {
                seatMap.AddSeat(seat.ZoneCode, seat.SeatNumber);
            }
        }
        catch (InvalidOperationException)
        {
            // 最後防線：正常流程不會走到這裡，因為上面已經檢查過重複組合。
            // 如果還是被觸發，代表預先檢查邏輯本身有漏洞（見 design.md 決策 2）。
            return Result<Guid>.Failure(Error.Conflict("The seat map contains duplicate zone code and seat number combinations."));
        }

        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        _seatMapRepository.Add(seatMap);
        await transaction.CommitAsync(cancellationToken);

        return Result<Guid>.Success(seatMap.Id);
    }
}
