using FluentValidation;

namespace ProjectC.Application.Orders.PlaceOrder;

public sealed class PlaceOrderRequestValidator : AbstractValidator<PlaceOrderRequest>
{
    public PlaceOrderRequestValidator()
    {
        RuleFor(x => x.Selections).NotEmpty();
        RuleForEach(x => x.Selections).SetValidator(new PlaceOrderSelectionRequestValidator());

        // EventSeatId 不可重複：同一個座位配兩個不同票種，對 CreateOrderHandler 來說仍是「同一座位被選兩次」
        // （見 ticketing-purchase design.md 決策 2）；「配對不重複」不夠，這裡直接要求 EventSeatId 本身唯一。
        RuleFor(x => x.Selections)
            .Must(selections => selections.Select(s => s.EventSeatId).Distinct().Count() == selections.Count)
            .WithMessage("The same seat cannot be selected more than once.")
            .When(x => x.Selections.Count > 0);
    }
}

public sealed class PlaceOrderSelectionRequestValidator : AbstractValidator<PlaceOrderSelectionRequest>
{
    public PlaceOrderSelectionRequestValidator()
    {
        RuleFor(x => x.EventSeatId).NotEqual(Guid.Empty);
        RuleFor(x => x.TicketTypeId).NotEqual(Guid.Empty);
    }
}
