using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

// 驗證 ticketing-purchase design.md 決策 3 的「鎖後重讀」（ReloadAsync）——比照 GetForUpdateAsyncTests
// 的並發測試手法，用兩個獨立的 DbContext/OrderService instance 模擬兩個並發請求。
[Collection(PostgresCollection.Name)]
public class OrderServiceConcurrencyTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private readonly PostgresFixture _fixture;

    public OrderServiceConcurrencyTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static OrderService CreateOrderService(ApplicationDbContext dbContext)
        => OrderServiceTestFactory.Create(dbContext);

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

        // 確認贏了才 MUST 出票（design.md 決策 1）；取消贏了代表確認那側必然失敗，MUST NOT 留下任何 Ticket。
        var confirmWon = results[0].IsSuccess;
        await using var readDbContext = _fixture.CreateDbContext();
        var orderItemIds = await readDbContext.OrderItems.AsNoTracking()
            .Where(i => EF.Property<Guid>(i, "OrderId") == orderId)
            .Select(i => i.Id)
            .ToListAsync();
        var ticketCount = await readDbContext.Tickets.AsNoTracking().CountAsync(t => orderItemIds.Contains(t.OrderItemId));
        ticketCount.Should().Be(confirmWon ? 1 : 0, confirmWon
            ? "確認訂單贏得競態時 MUST 出票"
            : "取消訂單贏得競態時，確認訂單那側必然失敗，MUST NOT 建立任何 Ticket");
    }

    [Fact]
    public async Task PlaceOrderAsync_TwoOrdersHoldingEventShareLockCompeteForSameSeat_OnlyOneSucceedsAndSeatIsNotOversold()
    {
        // TP-ORDER-036（order-placement-p95-phase2 design.md 決策 4）：B1 前兩筆在 Event 鎖就序列化；B1 後兩者都持有共享鎖，
        // 才會真正同時競爭座位列鎖。以掛點讓兩者都取得共享鎖後才同時放行。
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var ticketTypeId = await TicketingTestData.SeedTicketTypeAsync(seedDbContext, eventId);
        var buyerIds = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
            seedDbContext.Members.Add(buyer);
            buyerIds.Add(buyer.Id);
        }

        await seedDbContext.SaveChangesAsync();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatIds[0], ticketTypeId)]);

        var shareLocksAcquired = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var releaseBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var dbContextA = _fixture.CreateDbContext();
        await using var dbContextB = _fixture.CreateDbContext();
        var tasks = new[] { dbContextA, dbContextB }.Select((dbContext, index) =>
        {
            var eventRepository = new InterceptingEventRepository(new EventRepository(dbContext))
            {
                AfterGetForShareAsync = async () =>
                {
                    shareLocksAcquired[index].TrySetResult();
                    await releaseBoth.Task;
                },
            };
            return OrderServiceTestFactory.Create(dbContext, eventRepository: eventRepository)
                .PlaceOrderAsync(buyerIds[index], request, CancellationToken.None);
        }).ToList();

        try
        {
            await Task.WhenAll(shareLocksAcquired.Select(t => t.Task)).WaitAsync(WaitTimeout);
            releaseBoth.SetResult();
            var results = await Task.WhenAll(tasks).WaitAsync(WaitTimeout);

            results.Count(r => r.IsSuccess).Should().Be(1, "同一座位只能有一筆下單成功");
            results.Single(r => !r.IsSuccess).Error.Should().BeEquivalentTo(PlaceOrderConflictErrors.SeatNoLongerAvailable(eventSeatIds[0]),
                "共享鎖不保護座位，座位列鎖內的判斷才是權威");
            await using var readDbContext = _fixture.CreateDbContext();
            (await readDbContext.OrderItems.AsNoTracking().CountAsync(i => i.EventSeatId == eventSeatIds[0]))
                .Should().Be(1, "同一席座位只能被一筆訂單持有");
        }
        finally
        {
            releaseBoth.TrySetResult();
        }
    }
}
