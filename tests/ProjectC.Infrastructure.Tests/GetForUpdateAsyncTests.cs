using FluentAssertions;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public class GetForUpdateAsyncTests
{
    private readonly PostgresFixture _fixture;

    public GetForUpdateAsyncTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetForUpdateAsync_WithoutActiveTransaction_ThrowsInvalidOperationException()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var (_, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 1);
        var repository = new EventSeatRepository(dbContext);

        var act = () => repository.GetForUpdateAsync([eventSeatIds[0]], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetForUpdateAsync_WithEmptyIdList_ThrowsArgumentException()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var repository = new EventSeatRepository(dbContext);
        await using var tx = await dbContext.Database.BeginTransactionAsync();

        var act = () => repository.GetForUpdateAsync([], CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetForUpdateAsync_TwoConcurrentTransactionsLockingSameSeat_SecondWaitsForFirst()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (_, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var seatId = eventSeatIds[0];

        await using var dbContextA = _fixture.CreateDbContext();
        var repositoryA = new EventSeatRepository(dbContextA);
        await using var txA = await dbContextA.Database.BeginTransactionAsync();
        await repositoryA.GetForUpdateAsync([seatId], CancellationToken.None);

        await using var dbContextB = _fixture.CreateDbContext();
        var repositoryB = new EventSeatRepository(dbContextB);
        await using var txB = await dbContextB.Database.BeginTransactionAsync();

        var lockTaskB = repositoryB.GetForUpdateAsync([seatId], CancellationToken.None);

        var winnerBeforeCommit = await Task.WhenAny(lockTaskB, Task.Delay(TimeSpan.FromSeconds(2)));
        winnerBeforeCommit.Should().NotBe(lockTaskB, "交易 B 應該還在等交易 A 釋放鎖，不該提早拿到列");

        await txA.CommitAsync();

        var winnerAfterCommit = await Task.WhenAny(lockTaskB, Task.Delay(TimeSpan.FromSeconds(10)));
        winnerAfterCommit.Should().Be(lockTaskB, "交易 A 提交後，交易 B 應該能取得鎖並完成查詢");
        (await lockTaskB).Should().HaveCount(1);

        await txB.CommitAsync();
    }

    [Fact]
    public async Task GetForUpdateAsync_WhileAnotherTransactionConfirmsSold_WaitsForThatTransaction()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (_, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var seatId = eventSeatIds[0];
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // 先讓座位處於 Held 狀態，才能合法呼叫 ConfirmSold。
        await using (var setupDbContext = _fixture.CreateDbContext())
        {
            var setupRepository = new EventSeatRepository(setupDbContext);
            await using var setupTx = await setupDbContext.Database.BeginTransactionAsync();
            var seat = (await setupRepository.GetForUpdateAsync([seatId], CancellationToken.None)).Single();
            seat.Hold(orderId, now.AddMinutes(10), now);
            await setupDbContext.SaveChangesAsync();
            await setupTx.CommitAsync();
        }

        await using var dbContextA = _fixture.CreateDbContext();
        var repositoryA = new EventSeatRepository(dbContextA);
        await using var txA = await dbContextA.Database.BeginTransactionAsync();
        var seatForConfirm = (await repositoryA.GetForUpdateAsync([seatId], CancellationToken.None)).Single();
        seatForConfirm.ConfirmSold(orderId, now);
        await dbContextA.SaveChangesAsync();

        await using var dbContextB = _fixture.CreateDbContext();
        var repositoryB = new EventSeatRepository(dbContextB);
        await using var txB = await dbContextB.Database.BeginTransactionAsync();

        var lockTaskB = repositoryB.GetForUpdateAsync([seatId], CancellationToken.None);

        var winnerBeforeCommit = await Task.WhenAny(lockTaskB, Task.Delay(TimeSpan.FromSeconds(2)));
        winnerBeforeCommit.Should().NotBe(lockTaskB, "交易 A 正在 ConfirmSold 期間，交易 B 應該等待");

        await txA.CommitAsync();

        var winnerAfterCommit = await Task.WhenAny(lockTaskB, Task.Delay(TimeSpan.FromSeconds(10)));
        winnerAfterCommit.Should().Be(lockTaskB);

        await txB.CommitAsync();
    }

    [Fact]
    public async Task GetForUpdateAsync_ConcurrentOverlappingRequestsStartedSimultaneouslyInOppositeOrder_NeitherDeadlocksNorHangs()
    {
        // 比下面 CrossLockingOverlappingSeatSets 那個測試更貼近 spec：兩筆交易幾乎同時發起、
        // 輸入的座位 ID 順序刻意相反（模擬呼叫端沒有事先協調順序的真實情境），
        // 驗證鎖定順序是由資料庫的 ORDER BY 決定、不受呼叫端輸入順序影響，因此不會死鎖。
        await using var seedDbContext = _fixture.CreateDbContext();
        var (_, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 2);
        var (seatX, seatY) = (eventSeatIds[0], eventSeatIds[1]);

        await using var dbContextA = _fixture.CreateDbContext();
        var repositoryA = new EventSeatRepository(dbContextA);
        await using var txA = await dbContextA.Database.BeginTransactionAsync();

        await using var dbContextB = _fixture.CreateDbContext();
        var repositoryB = new EventSeatRepository(dbContextB);
        await using var txB = await dbContextB.Database.BeginTransactionAsync();

        // 兩邊幾乎同時發起，中間沒有互相 await，輸入順序相反：A 用 [X, Y]，B 用 [Y, X]。
        var lockTaskA = repositoryA.GetForUpdateAsync([seatX, seatY], CancellationToken.None);
        var lockTaskB = repositoryB.GetForUpdateAsync([seatY, seatX], CancellationToken.None);

        var firstToFinish = await Task.WhenAny(lockTaskA, lockTaskB, Task.Delay(TimeSpan.FromSeconds(5)));
        (firstToFinish == lockTaskA || firstToFinish == lockTaskB).Should()
            .BeTrue("兩邊要求的座位有重疊，資料庫必須讓其中一邊先成功，不該兩邊都卡住逾時");

        var winnerIsA = firstToFinish == lockTaskA;
        var loserTask = winnerIsA ? lockTaskB : lockTaskA;
        var winnerTx = winnerIsA ? txA : txB;
        var loserTx = winnerIsA ? txB : txA;

        // 用等待式驗證取代即時的 IsCompleted 檢查：WhenAny 返回的當下做同步檢查，
        // 理論上仍可能因為 task 排程時機巧合而不穩定；改成實際等一小段時間確認 loser 真的沒完成。
        var loserBeforeCommit = await Task.WhenAny(loserTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
        loserBeforeCommit.Should().NotBe(loserTask, "兩邊要求的座位有重疊，不可能同時都成功鎖定");

        await winnerTx.CommitAsync();

        var loserCompletion = await Task.WhenAny(loserTask, Task.Delay(TimeSpan.FromSeconds(10)));
        loserCompletion.Should().Be(loserTask, "贏家提交後，輸家應該能完成，不會死鎖或無限期卡住");

        await loserTx.CommitAsync();
    }

    [Fact]
    public async Task GetForUpdateAsync_CrossLockingOverlappingSeatSets_DoesNotDeadlock()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (_, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 3);
        var (seatX, seatY, seatZ) = (eventSeatIds[0], eventSeatIds[1], eventSeatIds[2]);

        await using var dbContextA = _fixture.CreateDbContext();
        var repositoryA = new EventSeatRepository(dbContextA);
        await using var txA = await dbContextA.Database.BeginTransactionAsync();
        // A 先鎖住 [X, Y]，包含跟 B 重疊的座位（Y）。
        await repositoryA.GetForUpdateAsync([seatX, seatY], CancellationToken.None);

        await using var dbContextB = _fixture.CreateDbContext();
        var repositoryB = new EventSeatRepository(dbContextB);
        await using var txB = await dbContextB.Database.BeginTransactionAsync();
        // B 要鎖 [Y, Z]，跟 A 的鎖定集合有重疊（Y）但不完全相同，且鎖定順序（依 Id 排序）在兩邊都一致。
        var lockTaskB = repositoryB.GetForUpdateAsync([seatY, seatZ], CancellationToken.None);

        var winnerBeforeCommit = await Task.WhenAny(lockTaskB, Task.Delay(TimeSpan.FromSeconds(2)));
        winnerBeforeCommit.Should().NotBe(lockTaskB, "B 應該卡在跟 A 重疊的座位 Y，而不是立刻完成");

        await txA.CommitAsync();

        var winnerAfterCommit = await Task.WhenAny(lockTaskB, Task.Delay(TimeSpan.FromSeconds(10)));
        winnerAfterCommit.Should().Be(lockTaskB, "A 提交後 B 應該能完成，不應該發生死鎖或無限期卡住");
        (await lockTaskB).Should().HaveCount(2);

        await txB.CommitAsync();
    }
}
