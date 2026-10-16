using FluentValidation;

namespace ProjectC.Application.Orders.PlaceOrder;

public sealed class PlaceOrderRequestValidator : AbstractValidator<PlaceOrderRequest>
{
    public PlaceOrderRequestValidator()
    {
        RuleFor(x => x.Selections).NotEmpty();

        // 筆數與張數共用同一上限：每筆至少 1 張，筆數超過時總張數必然也超過。筆數超過就不再做後面的逐項規則，
        // 否則每個不合格項目各產生一則訊息，回應 detail 隨請求放大（place-order-request-limits design.md 決策 2）。
        // 所有 When 都自行處理 Selections 為 null：NotEmpty 失敗不會阻止其他規則評估 When。
        RuleFor(x => x.Selections)
            .Must(selections => selections.Count <= PlaceOrderLimits.MaxTicketsPerOrder)
            .WithMessage($"An order cannot contain more than {PlaceOrderLimits.MaxTicketsPerOrder} selections.")
            .When(x => x.Selections is not null);

        // System.Text.Json 不會依 nullable 註記拒絕 [null]，SetValidator 也略過 null 元素，
        // 但重複檢查與加總規則會存取元素屬性，不擋會變成 500。
        RuleFor(x => x.Selections)
            .Must(selections => selections.All(s => s is not null))
            .WithMessage("Selections must not contain null items.")
            .When(IsWithinSelectionCountLimit);

        // long 累加：兩筆接近 int.MaxValue 的 Quantity 用 int 加總會溢位，與 OrderService 限購檢查同理。
        RuleFor(x => x.Selections)
            .Must(selections => selections.Sum(s => (long)s.Quantity) <= PlaceOrderLimits.MaxTicketsPerOrder)
            .WithMessage($"An order cannot contain more than {PlaceOrderLimits.MaxTicketsPerOrder} tickets in total.")
            .When(HasCheckableSelections);

        RuleForEach(x => x.Selections).SetValidator(new PlaceOrderSelectionRequestValidator()).When(HasCheckableSelections);

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
            .When(HasCheckableSelections);

        // 計數項目（EventSeatId 為空）的 TicketTypeId 之間不可重複——買家想買多張同一計數票種，
        // 須把數量加總成單一選購項目送出，不接受拆成多筆重複的計數項目（design.md 決策 4 審查第三輪補充）。
        RuleFor(x => x.Selections)
            .Must(selections =>
            {
                var countingTicketTypeIds = selections.Where(s => !s.EventSeatId.HasValue).Select(s => s.TicketTypeId).ToList();
                return countingTicketTypeIds.Distinct().Count() == countingTicketTypeIds.Count;
            })
            .WithMessage("The same counting ticket type cannot appear more than once in a single request.")
            .When(HasCheckableSelections);
    }

    private static bool IsWithinSelectionCountLimit(PlaceOrderRequest request)
        => request.Selections is { Count: <= PlaceOrderLimits.MaxTicketsPerOrder };

    private static bool HasCheckableSelections(PlaceOrderRequest request)
        => IsWithinSelectionCountLimit(request) && request.Selections.All(s => s is not null);
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
