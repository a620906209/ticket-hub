using FluentAssertions;
using ProjectC.Application.Orders.PlaceOrder;

namespace ProjectC.Application.Tests.Orders.PlaceOrder;

public class PlaceOrderRequestValidatorTests
{
    private readonly PlaceOrderRequestValidator _validator = new();

    [Fact]
    public void Validate_WhenSelectionsEmpty_IsInvalid()
    {
        var request = new PlaceOrderRequest([]);

        _validator.Validate(request).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WhenEventSeatIdIsEmpty_IsInvalid()
    {
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(Guid.Empty, Guid.NewGuid())]);

        _validator.Validate(request).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WhenTicketTypeIdIsEmpty_IsInvalid()
    {
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(Guid.NewGuid(), Guid.Empty)]);

        _validator.Validate(request).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WhenSameEventSeatIdSelectedWithTwoDifferentTicketTypeIds_IsInvalid()
    {
        var eventSeatId = Guid.NewGuid();
        var request = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(eventSeatId, Guid.NewGuid()),
            new PlaceOrderSelectionRequest(eventSeatId, Guid.NewGuid()),
        ]);

        _validator.Validate(request).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WhenSelectionsAreDistinctAndValid_IsValid()
    {
        var request = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(Guid.NewGuid(), Guid.NewGuid()),
            new PlaceOrderSelectionRequest(Guid.NewGuid(), Guid.NewGuid()),
        ]);

        _validator.Validate(request).IsValid.Should().BeTrue();
    }

    // ---- place-order-request-limits：系統上限 50（TP-LIMIT-*） ----

    private const string SelectionCountLimitMessage = "An order cannot contain more than 50 selections.";
    private const string TotalQuantityLimitMessage = "An order cannot contain more than 50 tickets in total.";
    private const string NullSelectionItemMessage = "Selections must not contain null items.";

    private static List<PlaceOrderSelectionRequest> SeatSelections(int count)
        => Enumerable.Range(0, count).Select(_ => new PlaceOrderSelectionRequest(Guid.NewGuid(), Guid.NewGuid())).ToList();

    private static IEnumerable<string> ValidateErrorMessages(PlaceOrderRequest request)
        => new PlaceOrderRequestValidator().Validate(request).Errors.Select(e => e.ErrorMessage);

    // TP-LIMIT-001
    [Fact]
    public void Validate_WhenTotalQuantityIs51_IsInvalidWithTotalMessage()
    {
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, Guid.NewGuid(), 51)]);

        ValidateErrorMessages(request).Should().Contain(TotalQuantityLimitMessage);
    }

    // TP-LIMIT-002
    [Fact]
    public void Validate_WhenTotalQuantityIs50_IsValid()
    {
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, Guid.NewGuid(), 50)]);

        _validator.Validate(request).IsValid.Should().BeTrue();
    }

    // TP-LIMIT-002
    [Fact]
    public void Validate_WhenSelectionCountIs50_IsValid()
    {
        _validator.Validate(new PlaceOrderRequest(SeatSelections(50))).IsValid.Should().BeTrue();
    }

    // TP-LIMIT-003：每筆也都不合格，錯誤若不只 1 則就代表回應會隨請求項目數放大。
    [Fact]
    public void Validate_WhenSelectionCountIs51_ReturnsOnlyCountError()
    {
        var selections = Enumerable.Range(0, 51).Select(_ => new PlaceOrderSelectionRequest(null, Guid.Empty, 0)).ToList();

        ValidateErrorMessages(new PlaceOrderRequest(selections)).Should().Equal(SelectionCountLimitMessage);
    }

    // TP-LIMIT-004：int 加總會拋 OverflowException。
    [Fact]
    public void Validate_WhenQuantitiesSumBeyondIntMax_IsInvalidWithTotalMessage()
    {
        var request = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(null, Guid.NewGuid(), int.MaxValue),
            new PlaceOrderSelectionRequest(null, Guid.NewGuid(), int.MaxValue),
        ]);

        ValidateErrorMessages(request).Should().Contain(TotalQuantityLimitMessage);
    }

    // TP-LIMIT-006：重複檢查與加總規則會存取元素屬性，未先擋 null 元素會拋 NullReferenceException。
    [Fact]
    public void Validate_WhenSelectionsContainNullItem_IsInvalidWithoutThrowing()
    {
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, Guid.NewGuid()), null!]);

        ValidateErrorMessages(request).Should().Contain(NullSelectionItemMessage);
    }

    // TP-LIMIT-007：NotEmpty 失敗不會阻止其他規則評估 When，When 存取 Selections.Count 會拋例外。
    [Fact]
    public void Validate_WhenSelectionsIsNull_IsInvalidWithoutThrowing()
    {
        _validator.Validate(new PlaceOrderRequest(null!)).IsValid.Should().BeFalse();
    }

    // TP-LIMIT-008：不指定 Quantity，由參數預設值 1 補上。
    [Fact]
    public void Validate_WhenFiftySeatItemsUseDefaultQuantity_IsValid()
    {
        var selections = Enumerable.Range(0, 50).Select(_ => new PlaceOrderSelectionRequest(Guid.NewGuid(), Guid.NewGuid())).ToList();

        _validator.Validate(new PlaceOrderRequest(selections)).IsValid.Should().BeTrue();
    }

    // TP-LIMIT-009
    [Fact]
    public void Validate_WhenFiftySelectionsTotalFiftyOne_ReturnsOnlyTotalError()
    {
        var selections = SeatSelections(49);
        selections.Add(new PlaceOrderSelectionRequest(null, Guid.NewGuid(), 2));

        ValidateErrorMessages(new PlaceOrderRequest(selections)).Should().Equal(TotalQuantityLimitMessage);
    }

    // TP-LIMIT-010：座位項目數量須為 1 需要查票種的 RequiresSeat，由 OrderService 負責，Validator 不得重複判斷。
    [Fact]
    public void Validate_WhenSeatItemQuantityIsTwoWithinLimit_IsValid()
    {
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(Guid.NewGuid(), Guid.NewGuid(), 2)]);

        _validator.Validate(request).IsValid.Should().BeTrue();
    }

    // 加總規則不得取代逐項 Quantity ≥ 1 規則：[100, -60] 加總 40 未超過上限，但 -60 本身不合格。
    [Fact]
    public void Validate_WhenNegativeQuantityKeepsSumUnderLimit_IsInvalid()
    {
        var request = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(null, Guid.NewGuid(), 100),
            new PlaceOrderSelectionRequest(null, Guid.NewGuid(), -60),
        ]);

        _validator.Validate(request).IsValid.Should().BeFalse();
    }

    // 筆數超過時連 null 元素規則都不跑，錯誤只剩 1 則。
    [Fact]
    public void Validate_WhenSelectionCountIs51WithNullItem_ReturnsOnlyCountError()
    {
        var selections = SeatSelections(50);
        selections.Add(null!);

        ValidateErrorMessages(new PlaceOrderRequest(selections)).Should().Equal(SelectionCountLimitMessage);
    }

    // 有 null 元素時不跑加總規則（加總會存取 null 元素的 Quantity）。
    [Fact]
    public void Validate_WhenNullItemAndQuantityIsIntMax_ReturnsOnlyNullItemError()
    {
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, Guid.NewGuid(), int.MaxValue), null!]);

        ValidateErrorMessages(request).Should().Equal(NullSelectionItemMessage);
    }
}
