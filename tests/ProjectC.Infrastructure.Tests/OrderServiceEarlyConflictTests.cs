using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Domain.Members;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

// order-placement-p95-optimization design.md 決策 5：交易前提早 409 只拒絕不放行，鎖內判斷仍是唯一權威。
// 這裡驗證「交易外判斷座位可售、之後才排到座位列鎖」的請求，在等鎖期間座位被別人暫扣時，鎖內仍回 409 且不超賣。
// Event 改共享鎖後（order-placement-p95-phase2 design.md 決策 4），同座位的下單只會在座位列鎖互等，持鎖者因此暫停在座位列鎖之後。
[Collection(PostgresCollection.Name)]
public class OrderServiceEarlyConflictTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private readonly PostgresFixture _fixture;

    public OrderServiceEarlyConflictTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSeatHeldByLockHolderWhileWaitingForSeatLock_ReturnsConflictAndDoesNotOversell()
    {
        // TP-ORDER-023
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var ticketTypeId = await TicketingTestData.SeedTicketTypeAsync(seedDbContext, eventId);
        var holderBuyerId = await SeedBuyerAsync(seedDbContext);
        var waiterBuyerId = await SeedBuyerAsync(seedDbContext);
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatIds[0], ticketTypeId)]);

        var holderLockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var holderDbContext = _fixture.CreateDbContext();
        var holderEventSeatRepository = new InterceptingEventSeatRepository(new EventSeatRepository(holderDbContext))
        {
            AfterGetForUpdateAsync = async () =>
            {
                holderLockAcquired.TrySetResult();
                await releaseHolder.Task;
            },
        };
        var holderTask = OrderServiceTestFactory.Create(holderDbContext, eventSeatRepository: holderEventSeatRepository)
            .PlaceOrderAsync(holderBuyerId, request, CancellationToken.None);

        try
        {
            await holderLockAcquired.Task.WaitAsync(WaitTimeout);

            // 持鎖者已鎖座位但還沒暫扣（未提交），等鎖的請求在交易外讀到「可售」，通過提早判斷後排進座位列鎖。
            await using var waiterDbContext = _fixture.CreateDbContext();
            var waiterTask = OrderServiceTestFactory.Create(waiterDbContext)
                .PlaceOrderAsync(waiterBuyerId, request, CancellationToken.None);
            await PostgresLockProbe.WaitUntilAnotherBackendWaitsForLockAsync(_fixture, WaitTimeout);
            waiterTask.IsCompleted.Should().BeFalse("等鎖的請求必須卡在座位列鎖，而不是在交易前就結束");

            releaseHolder.SetResult();
            var holderResult = await holderTask.WaitAsync(WaitTimeout);
            var waiterResult = await waiterTask.WaitAsync(WaitTimeout);

            holderResult.IsSuccess.Should().BeTrue();
            waiterResult.Error.Should().BeEquivalentTo(PlaceOrderConflictErrors.SeatNoLongerAvailable(eventSeatIds[0]),
                "交易外讀到可售不代表能成功，鎖內重新判斷才是權威");
            await using var readDbContext = _fixture.CreateDbContext();
            (await readDbContext.OrderItems.AsNoTracking().CountAsync(i => i.EventSeatId == eventSeatIds[0]))
                .Should().Be(1, "同一席座位只能被一筆訂單持有");
        }
        finally
        {
            releaseHolder.TrySetResult();
        }
    }

    private static async Task<Guid> SeedBuyerAsync(ApplicationDbContext dbContext)
    {
        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(buyer);
        await dbContext.SaveChangesAsync();
        return buyer.Id;
    }
}
