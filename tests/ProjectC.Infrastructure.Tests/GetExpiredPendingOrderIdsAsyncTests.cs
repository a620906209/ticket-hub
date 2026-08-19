using FluentAssertions;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public class GetExpiredPendingOrderIdsAsyncTests
{
    private readonly PostgresFixture _fixture;

    public GetExpiredPendingOrderIdsAsyncTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetExpiredPendingOrderIdsAsync_OnlyReturnsExpiredPendingOrders()
    {
        var now = DateTime.UtcNow;

        await using var dbContext = _fixture.CreateDbContext();
        var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 5);
        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(buyer);

        var expiredPending = new Order(Guid.NewGuid(), eventId, buyer.Id, now.AddMinutes(-1),
            [new OrderItem(Guid.NewGuid(), eventSeatIds[0], 500m)]);
        var boundaryPending = new Order(Guid.NewGuid(), eventId, buyer.Id, now,
            [new OrderItem(Guid.NewGuid(), eventSeatIds[1], 500m)]);
        var notYetExpiredPending = new Order(Guid.NewGuid(), eventId, buyer.Id, now.AddMinutes(10),
            [new OrderItem(Guid.NewGuid(), eventSeatIds[2], 500m)]);
        var expiredButPaid = new Order(Guid.NewGuid(), eventId, buyer.Id, now.AddMinutes(-1),
            [new OrderItem(Guid.NewGuid(), eventSeatIds[3], 500m)]);
        expiredButPaid.Confirm();
        var expiredButCancelled = new Order(Guid.NewGuid(), eventId, buyer.Id, now.AddMinutes(-1),
            [new OrderItem(Guid.NewGuid(), eventSeatIds[4], 500m)]);
        expiredButCancelled.Cancel();

        dbContext.Orders.AddRange(expiredPending, boundaryPending, notYetExpiredPending, expiredButPaid, expiredButCancelled);
        await dbContext.SaveChangesAsync();

        var repository = new OrderRepository(dbContext);
        var result = await repository.GetExpiredPendingOrderIdsAsync(now, CancellationToken.None);

        // 這個查詢掃描整張 Orders 表，不限定本測試種的資料；Infrastructure.Tests 底下同一個
        // PostgresCollection 共用一個資料庫，所以用「包含/不包含」而非「恰好等於」比對，
        // 避免跟其他測試類別留下的訂單資料互相影響（見 PostgresFixture 的共用資料庫設計）。
        result.Should().Contain([expiredPending.Id, boundaryPending.Id]);
        result.Should().NotContain([notYetExpiredPending.Id, expiredButPaid.Id, expiredButCancelled.Id]);
    }
}
