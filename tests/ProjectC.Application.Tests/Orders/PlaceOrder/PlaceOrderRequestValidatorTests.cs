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
}
