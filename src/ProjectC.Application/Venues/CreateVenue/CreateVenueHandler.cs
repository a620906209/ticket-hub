using FluentValidation;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Venues.CreateVenue;

public sealed class CreateVenueHandler
{
    private readonly IVenueRepository _venueRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IValidator<CreateVenueRequest> _validator;

    public CreateVenueHandler(IVenueRepository venueRepository, IUnitOfWork unitOfWork, IValidator<CreateVenueRequest> validator)
    {
        _venueRepository = venueRepository;
        _unitOfWork = unitOfWork;
        _validator = validator;
    }

    public async Task<Result<Guid>> HandleAsync(CreateVenueRequest request, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<Guid>.Failure(Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        var venue = new Venue(Guid.NewGuid(), request.Name);

        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        _venueRepository.Add(venue);
        await transaction.CommitAsync(cancellationToken);

        return Result<Guid>.Success(venue.Id);
    }
}
