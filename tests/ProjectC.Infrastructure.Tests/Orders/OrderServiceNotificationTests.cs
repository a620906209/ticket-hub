using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectC.Application.Common;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Security;
using ProjectC.Infrastructure.Tests.TestSupport;
using ProjectC.WebApi.BackgroundServices;

namespace ProjectC.Infrastructure.Tests.Orders;

// 驗證 email-notification 能力：訂單確認成功後觸發通知、通知失敗不影響確認結果、
// 取消（買家主動／背景清理）不觸發任何通知（見 email-notification tasks.md 6.4）。
[Collection(PostgresCollection.Name)]
public class OrderServiceNotificationTests
{
    private readonly PostgresFixture _fixture;

    public OrderServiceNotificationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Member> SeedBuyerAsync(ApplicationDbContext dbContext)
    {
        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(buyer);
        await dbContext.SaveChangesAsync();
        return buyer;
    }

    private async Task<(Guid OrderId, Member Buyer, string EventTitle)> SeedPendingSeatOrderAsync(ApplicationDbContext dbContext)
    {
        var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 1);
        var ticketTypeId = await TicketingTestData.SeedTicketTypeAsync(dbContext, eventId);
        var buyer = await SeedBuyerAsync(dbContext);

        var orderService = OrderServiceTestFactory.Create(dbContext);
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatIds[0], ticketTypeId)]);
        var result = await orderService.PlaceOrderAsync(buyer.Id, request, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();

        var @event = await dbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId);
        return (result.Value, buyer, @event.Title);
    }

    private async Task<(Guid OrderId, Member Buyer, string EventTitle, int ExpectedTicketCount)> SeedPendingMixedOrderAsync(ApplicationDbContext dbContext)
    {
        var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 1);
        var seatTicketTypeId = await TicketingTestData.SeedTicketTypeAsync(dbContext, eventId);
        var countTicketTypeId = await TicketingTestData.SeedCountBasedTicketTypeAsync(dbContext, eventId, availableQuantity: 10);
        var buyer = await SeedBuyerAsync(dbContext);

        var orderService = OrderServiceTestFactory.Create(dbContext);
        var request = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(eventSeatIds[0], seatTicketTypeId),
            new PlaceOrderSelectionRequest(null, countTicketTypeId, 3),
        ]);
        var result = await orderService.PlaceOrderAsync(buyer.Id, request, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();

        var @event = await dbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId);
        return (result.Value, buyer, @event.Title, ExpectedTicketCount: 1 + 3);
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenSucceeds_NotifiesBuyerWithCorrectContentAndReportsSuccess()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var (orderId, buyer, eventTitle) = await SeedPendingSeatOrderAsync(dbContext);
        var spy = new SpyEmailNotificationService();
        var orderService = OrderServiceTestFactory.Create(dbContext, emailNotificationService: spy);

        var result = await orderService.ConfirmOrderAsync(orderId, buyer.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        spy.Calls.Should().ContainSingle();
        var call = spy.Calls.Single();
        call.ToEmail.Should().Be(buyer.Email);
        call.EventTitle.Should().Be(eventTitle);
        call.OrderId.Should().Be(orderId);
        call.TicketCount.Should().Be(1);
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenOrderHasMixedSeatAndCountingItems_NotifiesWithSummedTicketCount()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var (orderId, buyer, _, expectedTicketCount) = await SeedPendingMixedOrderAsync(dbContext);
        var spy = new SpyEmailNotificationService();
        var orderService = OrderServiceTestFactory.Create(dbContext, emailNotificationService: spy);

        var result = await orderService.ConfirmOrderAsync(orderId, buyer.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        spy.Calls.Should().ContainSingle();
        spy.Calls.Single().TicketCount.Should().Be(expectedTicketCount);
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenCallerIsNotTheBuyer_DoesNotNotify()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var (orderId, _, _) = await SeedPendingSeatOrderAsync(dbContext);
        var spy = new SpyEmailNotificationService();
        var orderService = OrderServiceTestFactory.Create(dbContext, emailNotificationService: spy);

        var result = await orderService.ConfirmOrderAsync(orderId, Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        spy.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenNotificationThrowsAGeneralException_StillReportsSuccessAndLogsError()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var (orderId, buyer, _) = await SeedPendingSeatOrderAsync(dbContext);
        var thrown = new InvalidOperationException("simulated failure");
        var spy = new SpyEmailNotificationService { ExceptionToThrow = thrown };
        var logger = new ListLogger<OrderService>();
        var orderService = OrderServiceTestFactory.Create(dbContext, emailNotificationService: spy, logger: logger);

        var result = await orderService.ConfirmOrderAsync(orderId, buyer.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var errorEntries = logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        errorEntries.Should().ContainSingle();
        errorEntries.Single().Exception.Should().Be(thrown);
        errorEntries.Single().Message.Should().Contain(orderId.ToString());
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenCancelledByItsOwnTokenDuringNotification_StillReportsSuccessAndDoesNotLogError()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var (orderId, buyer, _) = await SeedPendingSeatOrderAsync(dbContext);
        using var cts = new CancellationTokenSource();
        var spy = new SpyEmailNotificationService
        {
            OnNotifyAsync = ct =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };
        var logger = new ListLogger<OrderService>();
        var orderService = OrderServiceTestFactory.Create(dbContext, emailNotificationService: spy, logger: logger);

        var result = await orderService.ConfirmOrderAsync(orderId, buyer.Id, cts.Token);

        result.IsSuccess.Should().BeTrue();
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenNotificationThrowsCancellationFromAnUnrelatedToken_StillReportsSuccessButLogsError()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var (orderId, buyer, _) = await SeedPendingSeatOrderAsync(dbContext);
        using var unrelatedCts = new CancellationTokenSource();
        unrelatedCts.Cancel();
        var spy = new SpyEmailNotificationService { ExceptionToThrow = new OperationCanceledException(unrelatedCts.Token) };
        var logger = new ListLogger<OrderService>();
        var orderService = OrderServiceTestFactory.Create(dbContext, emailNotificationService: spy, logger: logger);

        var result = await orderService.ConfirmOrderAsync(orderId, buyer.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task CancelOrderAsync_WhenBuyerCancelsOwnPendingOrder_DoesNotNotify()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var (orderId, buyer, _) = await SeedPendingSeatOrderAsync(dbContext);
        var spy = new SpyEmailNotificationService();
        var orderService = OrderServiceTestFactory.Create(dbContext, emailNotificationService: spy);

        var result = await orderService.CancelOrderAsync(orderId, buyer.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        spy.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupOnceAsync_WhenBackgroundServiceCancelsAnExpiredOrder_ResolvesTheSameOrderServiceInstanceAndDoesNotNotify()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var (orderId, buyer, _) = await SeedPendingSeatOrderAsync(dbContext);
        var order = await dbContext.Orders.SingleAsync(o => o.Id == orderId);
        dbContext.Entry(order).Property(o => o.HeldUntilUtc).CurrentValue = DateTime.UtcNow.AddMinutes(-1);
        await dbContext.SaveChangesAsync();
        _ = buyer;

        var spy = new SpyEmailNotificationService();
        var services = new ServiceCollection();
        services.AddSingleton<IOrderRepository>(_ => new OrderRepository(dbContext));
        services.AddSingleton(_ => OrderServiceTestFactory.Create(dbContext, emailNotificationService: spy));
        await using var provider = services.BuildServiceProvider();

        var cleanupService = new ExpiredOrderCleanupService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new SystemDateTimeProvider(),
            new OrderCleanupOptions(),
            NullLogger<ExpiredOrderCleanupService>.Instance);

        await cleanupService.CleanupOnceAsync(CancellationToken.None);

        var reloadedOrder = await dbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        reloadedOrder.Status.Should().Be(OrderStatus.Cancelled);
        spy.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenNotifying_TheOrderIsAlreadyCommittedFromAnIndependentConnection()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var (orderId, buyer, _) = await SeedPendingSeatOrderAsync(dbContext);
        var spy = new SpyEmailNotificationService();
        OrderStatus? statusSeenDuringNotification = null;
        spy.OnNotifyAsync = async _ =>
        {
            await using var independentDbContext = _fixture.CreateDbContext();
            var independentlyReadOrder = await independentDbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            statusSeenDuringNotification = independentlyReadOrder.Status;
        };
        var orderService = OrderServiceTestFactory.Create(dbContext, emailNotificationService: spy);

        var result = await orderService.ConfirmOrderAsync(orderId, buyer.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        statusSeenDuringNotification.Should().Be(OrderStatus.Paid);
    }
}
