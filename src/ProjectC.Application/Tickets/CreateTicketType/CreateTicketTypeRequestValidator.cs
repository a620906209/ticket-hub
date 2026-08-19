using FluentValidation;

namespace ProjectC.Application.Tickets.CreateTicketType;

public sealed class CreateTicketTypeRequestValidator : AbstractValidator<CreateTicketTypeRequest>
{
    public CreateTicketTypeRequestValidator()
    {
        RuleFor(x => x.ZoneCode).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Price).GreaterThan(0);

        // RequiresSeat = true（綁座位）：不接受 AvailableQuantity，庫存數量須由座位圖決定。
        RuleFor(x => x.AvailableQuantity)
            .Null()
            .WithMessage("綁座位票種不接受指定可售總量。")
            .When(x => x.RequiresSeat);

        // RequiresSeat = false（純計數）：AvailableQuantity 必填且為正整數。
        RuleFor(x => x.AvailableQuantity)
            .NotNull()
            .WithMessage("純計數票種必須指定可售總量。")
            .When(x => !x.RequiresSeat);
        RuleFor(x => x.AvailableQuantity)
            .GreaterThan(0)
            .WithMessage("可售總量必須為正整數。")
            .When(x => !x.RequiresSeat && x.AvailableQuantity.HasValue);
    }
}
