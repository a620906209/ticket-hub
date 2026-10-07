using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Events.SetEventQueueMode;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Domain.Members;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

// order-placement-p95-phase2 design.md 決策 1：下單以 FOR SHARE 鎖 Event。同一活動不同座位的下單不再逐筆通過，
// 但進行中的下單仍會阻塞切換排隊模式（線性化不變）。
[Collection(PostgresCollection.Name)]
public class OrderServiceEventShareLockTests
{
    // 只用來避免測試掛住，不是產品延遲門檻（design.md 決策 4）。
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private readonly PostgresFixture _fixture;

    public OrderServiceEventShareLockTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenAnotherOrderHoldsEventShareLock_OrderForDifferentSeatCompletesWithoutWaiting()
    {
        // TP-ORDER-034
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 2);
        var ticketTypeId = await TicketingTestData.SeedTicketTypeAsync(seedDbContext, eventId);
        var buyerAId = await SeedBuyerAsync(seedDbContext);
        var buyerBId = await SeedBuyerAsync(seedDbContext);

        var holderLockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var dbContextA = _fixture.CreateDbContext();
        var eventRepositoryA = new InterceptingEventRepository(new EventRepository(dbContextA))
        {
            AfterGetForShareAsync = async () =>
            {
                holderLockAcquired.TrySetResult();
                await releaseHolder.Task;
            },
        };
        var taskA = OrderServiceTestFactory.Create(dbContextA, eventRepository: eventRepositoryA)
            .PlaceOrderAsync(buyerAId, CreateSeatRequest(eventSeatIds[0], ticketTypeId), CancellationToken.None);

        try
        {
            await holderLockAcquired.Task.WaitAsync(WaitTimeout);

            // 判準是事件順序：A 仍暫停在共享鎖之後，B 必須先完成；若仍是互斥鎖，B 會等到 WaitTimeout 失敗。
            await using var dbContextB = _fixture.CreateDbContext();
            var resultB = await OrderServiceTestFactory.Create(dbContextB)
                .PlaceOrderAsync(buyerBId, CreateSeatRequest(eventSeatIds[1], ticketTypeId), CancellationToken.None)
                .WaitAsync(WaitTimeout);

            taskA.IsCompleted.Should().BeFalse("A 尚未被放行，B 的完成不得依賴 A 的交易結束");
            resultB.IsSuccess.Should().BeTrue();

            releaseHolder.SetResult();
            var resultA = await taskA.WaitAsync(WaitTimeout);
            resultA.IsSuccess.Should().BeTrue();

            (await GetOrderIdsHoldingSeatAsync(eventSeatIds[0])).Should().Equal([resultA.Value], "座位 1 只能由 A 的訂單持有");
            (await GetOrderIdsHoldingSeatAsync(eventSeatIds[1])).Should().Equal([resultB.Value], "座位 2 只能由 B 的訂單持有");
        }
        finally
        {
            releaseHolder.TrySetResult();
        }
    }

    [Fact]
    public async Task SetQueueMode_WhileOrderHoldsEventShareLock_WaitsForOrderToFinishAndOrderUsesValueBeforeSwitch()
    {
        // TP-ORDER-035
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var ticketTypeId = await TicketingTestData.SeedTicketTypeAsync(seedDbContext, eventId);
        var buyerId = await SeedBuyerAsync(seedDbContext);
        var organizerId = (await seedDbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId)).OrganizerId;

        var holderLockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var orderDbContext = _fixture.CreateDbContext();
        var orderEventRepository = new InterceptingEventRepository(new EventRepository(orderDbContext))
        {
            AfterGetForShareAsync = async () =>
            {
                holderLockAcquired.TrySetResult();
                await releaseHolder.Task;
            },
        };
        var orderTask = OrderServiceTestFactory.Create(orderDbContext, eventRepository: orderEventRepository)
            .PlaceOrderAsync(buyerId, CreateSeatRequest(eventSeatIds[0], ticketTypeId), CancellationToken.None);

        try
        {
            await holderLockAcquired.Task.WaitAsync(WaitTimeout);

            await using var switchDbContext = _fixture.CreateDbContext();
            var handler = new SetEventQueueModeHandler(
                new EventRepository(switchDbContext), new UnitOfWork(switchDbContext), new SetEventQueueModeRequestValidator(), new FakeQueryCache());
            var switchTask = handler.HandleAsync(eventId, organizerId, new SetEventQueueModeRequest(true), CancellationToken.None);

            await PostgresLockProbe.WaitUntilAnotherBackendWaitsForLockAsync(_fixture, WaitTimeout);
            switchTask.IsCompleted.Should().BeFalse("下單持有共享鎖期間，切換排隊模式的 FOR UPDATE 必須等待");

            releaseHolder.SetResult();
            var orderResult = await orderTask.WaitAsync(WaitTimeout);
            var switchResult = await switchTask.WaitAsync(WaitTimeout);

            orderResult.IsSuccess.Should().BeTrue("下單在切換前讀到 false，不要求排隊資格");
            switchResult.IsSuccess.Should().BeTrue();
            await using var readDbContext = _fixture.CreateDbContext();
            (await readDbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId)).IsQueueModeEnabled.Should().BeTrue();
        }
        finally
        {
            releaseHolder.TrySetResult();
        }
    }

    private static PlaceOrderRequest CreateSeatRequest(Guid eventSeatId, Guid ticketTypeId)
        => new([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]);

    private async Task<List<Guid>> GetOrderIdsHoldingSeatAsync(Guid eventSeatId)
    {
        await using var readDbContext = _fixture.CreateDbContext();
        return await readDbContext.OrderItems.AsNoTracking()
            .Where(i => i.EventSeatId == eventSeatId)
            .Select(i => EF.Property<Guid>(i, "OrderId"))
            .ToListAsync();
    }

    private static async Task<Guid> SeedBuyerAsync(ApplicationDbContext dbContext)
    {
        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(buyer);
        await dbContext.SaveChangesAsync();
        return buyer.Id;
    }
}
