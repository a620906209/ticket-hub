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
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.PosterUrl).MaximumLength(500);
        RuleFor(x => x.MaxTicketsPerOrder).GreaterThan(0).When(x => x.MaxTicketsPerOrder.HasValue);
    }
}
