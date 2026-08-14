using FluentAssertions;
using ProjectC.Domain.Orders;

namespace ProjectC.Domain.Tests.Orders;

public class OrderTests
{
    private static Order CreateOrder(DateTime heldUntilUtc)
        => new(Guid.NewGuid(), heldUntilUtc, [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), 500m)]);

    [Fact]
    public void GetStatus_WhenPendingAndNotExpired_ReturnsPending()
    {
        var now = DateTime.UtcNow;
        var order = CreateOrder(now.AddMinutes(10));

        order.GetStatus(now).Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public void GetStatus_WhenPendingAndPastHeldUntilUtc_ReturnsExpiredWithoutMutatingStoredStatus()
    {
        var now = DateTime.UtcNow;
        var order = CreateOrder(now.AddMinutes(10));

        var afterExpiry = now.AddMinutes(11);
        order.GetStatus(afterExpiry).Should().Be(OrderStatus.Expired);
        order.Status.Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public void Confirm_WhenPending_TransitionsToConfirmed()
    {
        var now = DateTime.UtcNow;
        var order = CreateOrder(now.AddMinutes(10));

        order.Confirm();

        order.Status.Should().Be(OrderStatus.Confirmed);
    }

    [Fact]
    public void Cancel_WhenPending_TransitionsToCancelled()
    {
        var now = DateTime.UtcNow;
        var order = CreateOrder(now.AddMinutes(10));

        order.Cancel();

        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public void Cancel_WhenAlreadyExpiredButStillPending_StillTransitionsToCancelled()
    {
        var now = DateTime.UtcNow;
        var order = CreateOrder(now.AddMinutes(10));
        var afterExpiry = now.AddMinutes(11);
        order.GetStatus(afterExpiry).Should().Be(OrderStatus.Expired);

        order.Cancel();

        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public void Cancel_WhenConfirmed_ThrowsOrderAlreadyConfirmedException()
    {
        var now = DateTime.UtcNow;
        var order = CreateOrder(now.AddMinutes(10));
        order.Confirm();

        var act = order.Cancel;

        act.Should().Throw<OrderAlreadyConfirmedException>();
        order.Status.Should().Be(OrderStatus.Confirmed);
    }

    [Fact]
    public void Constructor_WhenNoItemsProvided_ThrowsArgumentException()
    {
        var act = () => new Order(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(10), []);

        act.Should().Throw<ArgumentException>();
    }
}
