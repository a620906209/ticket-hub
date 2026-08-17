using FluentAssertions;
using ProjectC.Domain.Orders;

namespace ProjectC.Domain.Tests.Orders;

public class OrderTests
{
    private static Order CreateOrder(DateTime heldUntilUtc)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), heldUntilUtc, [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), 500m)]);

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
    public void GetStatus_WhenNowEqualsHeldUntilUtc_ReturnsExpired()
    {
        var now = DateTime.UtcNow;
        var heldUntilUtc = now.AddMinutes(10);
        var order = CreateOrder(heldUntilUtc);

        order.GetStatus(heldUntilUtc).Should().Be(OrderStatus.Expired);
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
    public void Confirm_WhenNotPending_ThrowsOrderNotPendingException()
    {
        var now = DateTime.UtcNow;
        var order = CreateOrder(now.AddMinutes(10));
        order.Cancel();

        var act = order.Confirm;

        act.Should().Throw<OrderNotPendingException>();
        order.Status.Should().Be(OrderStatus.Cancelled);
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
    public void Cancel_WhenConfirmed_ThrowsOrderNotPendingException()
    {
        var now = DateTime.UtcNow;
        var order = CreateOrder(now.AddMinutes(10));
        order.Confirm();

        var act = order.Cancel;

        act.Should().Throw<OrderNotPendingException>();
        order.Status.Should().Be(OrderStatus.Confirmed);
    }

    [Fact]
    public void Cancel_WhenAlreadyCancelled_ThrowsOrderNotPendingException()
    {
        var now = DateTime.UtcNow;
        var order = CreateOrder(now.AddMinutes(10));
        order.Cancel();

        var act = order.Cancel;

        act.Should().Throw<OrderNotPendingException>();
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public void Constructor_WhenNoItemsProvided_ThrowsArgumentException()
    {
        var act = () => new Order(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow.AddMinutes(10), []);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WhenBuyerIdIsEmpty_ThrowsArgumentException()
    {
        var act = () => new Order(
            Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, DateTime.UtcNow.AddMinutes(10),
            [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), 500m)]);

        act.Should().Throw<ArgumentException>();
    }
}
