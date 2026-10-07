using FluentAssertions;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Events;

// order-placement-p95-phase2 design.md 決策 1：下單以 FOR SHARE 鎖 Event，下單彼此不互斥，但仍與切換排隊模式的 FOR UPDATE 互斥。
[Collection(PostgresCollection.Name)]
public class EventRepositoryGetForShareAsyncTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private readonly PostgresFixture _fixture;

    public EventRepositoryGetForShareAsyncTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetForShareAsync_WithoutActiveTransaction_ThrowsInvalidOperationException()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 1);
        var repository = new EventRepository(dbContext);

        var act = () => repository.GetForShareAsync(eventId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>("沒有交易時共享鎖一返回就釋放，等於沒有線性化保護");
    }

    [Fact]
    public async Task GetForShareAsync_TwoTransactionsOnSameEvent_DoNotWaitForEachOther()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);

        await using var dbContextA = _fixture.CreateDbContext();
        await using var txA = await dbContextA.Database.BeginTransactionAsync();
        (await new EventRepository(dbContextA).GetForShareAsync(eventId, CancellationToken.None)).Should().NotBeNull();

        await using var dbContextB = _fixture.CreateDbContext();
        await using var txB = await dbContextB.Database.BeginTransactionAsync();

        // A 仍持有共享鎖；若實作成互斥鎖，B 會等到逾時。
        var eventB = await new EventRepository(dbContextB).GetForShareAsync(eventId, CancellationToken.None).WaitAsync(WaitTimeout);

        eventB.Should().NotBeNull();
        eventB!.Id.Should().Be(eventId);
        await txB.CommitAsync();
        await txA.CommitAsync();
    }

    [Fact]
    public async Task GetForUpdateAsync_WhileAnotherTransactionHoldsShareLock_WaitsUntilItEnds()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);

        await using var shareDbContext = _fixture.CreateDbContext();
        await using var shareTx = await shareDbContext.Database.BeginTransactionAsync();
        await new EventRepository(shareDbContext).GetForShareAsync(eventId, CancellationToken.None);

        await using var updateDbContext = _fixture.CreateDbContext();
        await using var updateTx = await updateDbContext.Database.BeginTransactionAsync();
        var updateTask = new EventRepository(updateDbContext).GetForUpdateAsync(eventId, CancellationToken.None);

        await PostgresLockProbe.WaitUntilAnotherBackendWaitsForLockAsync(_fixture, WaitTimeout);
        updateTask.IsCompleted.Should().BeFalse("切換排隊模式的 FOR UPDATE 必須等共享鎖持有者的交易結束");

        await shareTx.CommitAsync();

        (await updateTask.WaitAsync(WaitTimeout)).Should().NotBeNull();
        await updateTx.CommitAsync();
    }
}
