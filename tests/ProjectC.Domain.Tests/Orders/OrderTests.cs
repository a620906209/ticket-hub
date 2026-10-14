using FluentAssertions;
using ProjectC.Domain.Orders;

namespace ProjectC.Domain.Tests.Orders;

public class OrderTests
{
    private static Order CreateOrder(DateTime heldUntilUtc)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, heldUntilUtc, [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 500m)]);

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
    public void Confirm_WhenPending_TransitionsToPaid()
    {
        var now = DateTime.UtcNow;
        var order = CreateOrder(now.AddMinutes(10));

        order.Confirm();

        order.Status.Should().Be(OrderStatus.Paid);
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
    public void Cancel_WhenPaid_ThrowsOrderNotPendingException()
    {
        var now = DateTime.UtcNow;
        var order = CreateOrder(now.AddMinutes(10));
        order.Confirm();

        var act = order.Cancel;

        act.Should().Throw<OrderNotPendingException>();
        order.Status.Should().Be(OrderStatus.Paid);
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
        var act = () => new Order(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow.AddMinutes(10), []);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WhenBuyerIdIsEmpty_ThrowsArgumentException()
    {
        var act = () => new Order(
            Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(10),
            [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 500m)]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_RecordsCreatedAtUtc()
    {
        var createdAtUtc = new DateTime(2026, 10, 14, 3, 0, 0, DateTimeKind.Utc);

        var order = new Order(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), createdAtUtc, createdAtUtc.AddMinutes(10),
            [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 500m)]);

        order.CreatedAtUtc.Should().Be(createdAtUtc);
    }

    [Fact]
    public void Constructor_WhenCreatedAtUtcIsDefault_ThrowsArgumentException()
    {
        var act = () => new Order(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default, DateTime.UtcNow.AddMinutes(10),
            [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 500m)]);

        act.Should().Throw<ArgumentException>().WithParameterName("createdAtUtc");
    }

    [Fact]
    public void GetTicketCountAndGetTotalAmount_WithTwoSingleItems_SumQuantitiesAndPrices()
    {
        var order = CreateOrderWithItems(
            new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1000m),
            new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1500m));

        order.GetTicketCount().Should().Be(2);
        order.GetTotalAmount().Should().Be(2500m);
    }

    [Fact]
    public void GetTicketCountAndGetTotalAmount_WithOneMultiQuantityItem_MultiplyByQuantity()
    {
        // 單一項目 Quantity 3：防止實作只算項目筆數（會得 1）或忽略 Quantity（金額會得 800）。
        var order = CreateOrderWithItems(new OrderItem(Guid.NewGuid(), Guid.NewGuid(), null, 3, 800m));

        order.GetTicketCount().Should().Be(3);
        order.GetTotalAmount().Should().Be(2400m);
    }

    private static Order CreateOrderWithItems(params OrderItem[] items)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow.AddMinutes(10), items);
}
