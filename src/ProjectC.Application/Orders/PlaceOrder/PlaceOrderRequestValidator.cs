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
        // EventSeatId 改為可為 null 後，先過濾掉 null 再比對，否則兩筆不同計數項目（皆為 null）會被
        // Distinct() 收斂成 1 個，誤判成重複選位（design.md 決策 4 審查後補充）。
        RuleFor(x => x.Selections)
            .Must(selections =>
            {
                var seatIds = selections.Where(s => s.EventSeatId.HasValue).Select(s => s.EventSeatId!.Value).ToList();
                return seatIds.Distinct().Count() == seatIds.Count;
            })
            .WithMessage("The same seat cannot be selected more than once.")
            .When(x => x.Selections.Count > 0);

        // 計數項目（EventSeatId 為空）的 TicketTypeId 之間不可重複——買家想買多張同一計數票種，
        // 須把數量加總成單一選購項目送出，不接受拆成多筆重複的計數項目（design.md 決策 4 審查第三輪補充）。
        RuleFor(x => x.Selections)
            .Must(selections =>
            {
                var countingTicketTypeIds = selections.Where(s => !s.EventSeatId.HasValue).Select(s => s.TicketTypeId).ToList();
                return countingTicketTypeIds.Distinct().Count() == countingTicketTypeIds.Count;
            })
            .WithMessage("The same counting ticket type cannot appear more than once in a single request.")
            .When(x => x.Selections.Count > 0);
    }
}

public sealed class PlaceOrderSelectionRequestValidator : AbstractValidator<PlaceOrderSelectionRequest>
{
    public PlaceOrderSelectionRequestValidator()
    {
        RuleFor(x => x.EventSeatId).NotEqual(Guid.Empty).When(x => x.EventSeatId.HasValue);
        RuleFor(x => x.TicketTypeId).NotEqual(Guid.Empty);
        RuleFor(x => x.Quantity).GreaterThanOrEqualTo(1);
    }
}
