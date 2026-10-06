using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Domain.Members;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

// order-placement-p95-optimization design.md 決策 5：交易前提早 409 只拒絕不放行，鎖內判斷仍是唯一權威。
// 這裡驗證「交易外判斷座位可售、之後才排到 Event 鎖」的請求，在等鎖期間座位被別人暫扣時，鎖內仍回 409 且不超賣。
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
    public async Task PlaceOrderAsync_WhenSeatHeldByLockHolderWhileWaitingForEventLock_ReturnsConflictAndDoesNotOversell()
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
        var holderEventRepository = new InterceptingEventRepository(new EventRepository(holderDbContext))
        {
            AfterGetForUpdateAsync = async () =>
            {
                holderLockAcquired.TrySetResult();
                await releaseHolder.Task;
            },
        };
        var holderTask = OrderServiceTestFactory.Create(holderDbContext, eventRepository: holderEventRepository)
            .PlaceOrderAsync(holderBuyerId, request, CancellationToken.None);
        await holderLockAcquired.Task.WaitAsync(WaitTimeout);

        // 持鎖者還沒暫扣座位，等鎖的請求在交易外讀到「可售」，通過提早判斷後排進 Event 鎖。
        await using var waiterDbContext = _fixture.CreateDbContext();
        var waiterTask = OrderServiceTestFactory.Create(waiterDbContext)
            .PlaceOrderAsync(waiterBuyerId, request, CancellationToken.None);
        await WaitUntilAnotherBackendWaitsForLockAsync();
        waiterTask.IsCompleted.Should().BeFalse("等鎖的請求必須卡在 Event 列鎖，而不是在交易前就結束");

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

    private static async Task<Guid> SeedBuyerAsync(ApplicationDbContext dbContext)
    {
        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(buyer);
        await dbContext.SaveChangesAsync();
        return buyer.Id;
    }

    /// <summary>
    /// 以「確實有連線在等鎖」作為放行條件，不用固定睡眠（design.md 決策 5）。pg_locks 是系統檢視表，EF Core 沒有對應模型，
    /// 只能用 SqlQuery；查詢沒有任何外部輸入。等 FOR UPDATE 列鎖的連線是在等持鎖交易的 transactionid，限定 locktype
    /// 避免其他種類的鎖等待提早放行；同一個 collection 的測試依序執行，這段期間的等鎖連線只會是本測試的請求。
    /// </summary>
    private async Task WaitUntilAnotherBackendWaitsForLockAsync()
    {
        await using var probeDbContext = _fixture.CreateDbContext();
        using var timeout = new CancellationTokenSource(WaitTimeout);
        while (true)
        {
            var waitingLockCount = await probeDbContext.Database
                .SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM pg_locks WHERE locktype = 'transactionid' AND NOT granted AND pid <> pg_backend_pid()""")
                .SingleAsync(timeout.Token);
            if (waitingLockCount > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }
}
