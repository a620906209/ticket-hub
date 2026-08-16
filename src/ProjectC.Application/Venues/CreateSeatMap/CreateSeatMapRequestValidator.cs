using FluentValidation;

namespace ProjectC.Application.Venues.CreateSeatMap;

public sealed class CreateSeatMapRequestValidator : AbstractValidator<CreateSeatMapRequest>
{
    public CreateSeatMapRequestValidator()
    {
        RuleFor(x => x.Seats).NotEmpty();
        RuleForEach(x => x.Seats).SetValidator(new SeatRequestValidator());
    }
}

public sealed class SeatRequestValidator : AbstractValidator<SeatRequest>
{
    public SeatRequestValidator()
    {
        RuleFor(x => x.ZoneCode).NotEmpty().MaximumLength(50);
        RuleFor(x => x.SeatNumber).NotEmpty().MaximumLength(50);
    }
}
