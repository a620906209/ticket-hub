using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Infrastructure.Payments;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Security;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

// 驗證 ticketing-purchase design.md 決策 3 的「鎖後重讀」（ReloadAsync）——比照 GetForUpdateAsyncTests
// 的並發測試手法，用兩個獨立的 DbContext/OrderService instance 模擬兩個並發請求。
[Collection(PostgresCollection.Name)]
public class OrderServiceConcurrencyTests
{
    private readonly PostgresFixture _fixture;

    public OrderServiceConcurrencyTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static OrderService CreateOrderService(ApplicationDbContext dbContext)
    {
        var dateTimeProvider = new SystemDateTimeProvider();
        return new OrderService(
            new TicketTypeRepository(dbContext),
            new EventSeatRepository(dbContext),
            new EventRepository(dbContext),
            new SeatMapRepository(dbContext),
            new OrderRepository(dbContext),
            new UnitOfWork(dbContext),
            new PlaceOrderRequestValidator(),
            dateTimeProvider,
            new CreateOrderHandler(dateTimeProvider),
            new ConfirmOrderHandler(dateTimeProvider, new MockPaymentGateway(new MockPaymentGatewayOptions())),
            new CancelOrderHandler(dateTimeProvider));
    }

    private async Task<(Guid OrderId, Guid BuyerId)> SeedPendingOrderAsync(ApplicationDbContext dbContext)
    {
        var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 1);
        var @event = await dbContext.Events.SingleAsync(e => e.Id == eventId);
        var seatMap = await dbContext.SeatMaps.Include(s => s.Seats).SingleAsync(s => s.Id == @event.SeatMapId);
        var ticketType = @event.CreateTicketType("A", 500m, seatMap);
        dbContext.TicketTypes.Add(ticketType);

        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(buyer);
        await dbContext.SaveChangesAsync();

        var orderService = CreateOrderService(dbContext);
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatIds[0], ticketType.Id)]);
        var result = await orderService.PlaceOrderAsync(buyer.Id, request, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();

        return (result.Value, buyer.Id);
    }

    [Fact]
    public async Task CancelOrderAsync_TwoConcurrentCancelsOnSameOrder_OnlyOneSucceedsAndTheOtherIsRejected()
    {
        // 主要情境：這是唯一真正依賴 ReloadAsync 才會正確的組合（見 design.md 決策 3）——
        // CancelOrderHandler 對「座位已不是自己持有」是靜默略過而非回錯，若沒有鎖後重讀，
        // 輸家會誤報 Success 而非被正確拒絕。
        await using var seedDbContext = _fixture.CreateDbContext();
        var (orderId, buyerId) = await SeedPendingOrderAsync(seedDbContext);

        await using var dbContextA = _fixture.CreateDbContext();
        await using var dbContextB = _fixture.CreateDbContext();
        var serviceA = CreateOrderService(dbContextA);
        var serviceB = CreateOrderService(dbContextB);

        var taskA = serviceA.CancelOrderAsync(orderId, buyerId, CancellationToken.None);
        var taskB = serviceB.CancelOrderAsync(orderId, buyerId, CancellationToken.None);
        var results = await Task.WhenAll(taskA, taskB);
        results.Count(r => r.IsSuccess).Should().Be(1, "兩個並發取消同一筆訂單，只能有一個成功，另一個 MUST 被拒絕而非誤報成功");

        await using var readDbContext = _fixture.CreateDbContext();
        var reloadedOrder = await readDbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        reloadedOrder.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public async Task ConfirmAndCancel_ConcurrentlyOnSameOrder_OnlyOneSucceeds()
    {
        // 次要情境：這個組合即使沒有 ReloadAsync，也已被既有的座位狀態檢查（IsHeldBy/IsSoldBy）攔住
        // （見 design.md 決策 3），保留這個測試是為了涵蓋 spec「並發確認與取消同一筆訂單」Scenario，
        // 不能取代上面兩個並發 Cancel 的測試。
        await using var seedDbContext = _fixture.CreateDbContext();
        var (orderId, buyerId) = await SeedPendingOrderAsync(seedDbContext);

        await using var dbContextA = _fixture.CreateDbContext();
        await using var dbContextB = _fixture.CreateDbContext();
        var serviceA = CreateOrderService(dbContextA);
        var serviceB = CreateOrderService(dbContextB);

        var confirmTask = serviceA.ConfirmOrderAsync(orderId, buyerId, CancellationToken.None);
        var cancelTask = serviceB.CancelOrderAsync(orderId, buyerId, CancellationToken.None);
        var results = await Task.WhenAll(confirmTask, cancelTask);
        results.Count(r => r.IsSuccess).Should().Be(1, "同一筆訂單被並發的確認與取消操作，只能有一個成功");
    }
}
