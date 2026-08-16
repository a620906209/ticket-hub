using FluentValidation;

namespace ProjectC.Application.Tickets.CreateTicketType;

public sealed class CreateTicketTypeRequestValidator : AbstractValidator<CreateTicketTypeRequest>
{
    public CreateTicketTypeRequestValidator()
    {
        RuleFor(x => x.ZoneCode).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Price).GreaterThan(0);
    }
}
