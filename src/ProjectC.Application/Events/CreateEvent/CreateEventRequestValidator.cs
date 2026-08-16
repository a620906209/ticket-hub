using FluentValidation;

namespace ProjectC.Application.Events.CreateEvent;

public sealed class CreateEventRequestValidator : AbstractValidator<CreateEventRequest>
{
    public CreateEventRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.StartAtUtc).NotEqual(default(DateTime));
        RuleFor(x => x.VenueId).NotEqual(Guid.Empty);
        RuleFor(x => x.SeatMapId).NotEqual(Guid.Empty);
    }
}
