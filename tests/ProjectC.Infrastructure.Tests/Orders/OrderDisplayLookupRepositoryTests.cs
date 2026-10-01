using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Orders;

/// <summary>
/// 訂單顯示資訊用的批次查詢（order-display-enrichment tasks.md 1.2b／1.6／1.7）。查詢次數測試抓不到「讀取過多列」，
/// 因此直接驗證只回傳要求的 Id；空清單不查詢與已取消 token 會丟例外則是 Handler 單元測試無法涵蓋的 repository 層保證。
/// </summary>
[Collection(PostgresCollection.Name)]
public class OrderDisplayLookupRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public OrderDisplayLookupRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetSeatsByIdsAsync_WhenSeatMapHasManySeats_ReturnsOnlyRequestedSeats()
    {
        var seatMap = await SeedSeatMapAsync(seatCount: 5);
        var requestedSeats = new[] { seatMap.Seats[1], seatMap.Seats[3] };

        await using var dbContext = _fixture.CreateDbContext();
        var seats = await new SeatMapRepository(dbContext).GetSeatsByIdsAsync(requestedSeats.Select(s => s.Id).ToList(), CancellationToken.None);

        seats.Select(s => (s.Id, s.ZoneCode, s.SeatNumber))
            .Should().BeEquivalentTo(requestedSeats.Select(s => (s.Id, s.ZoneCode, s.SeatNumber)));
    }

    [Fact]
    public async Task GetSeatsByIdsAsync_WithEmptyList_ReturnsEmptyWithoutQuery()
    {
        var counter = new CommandCounter();
        await using var dbContext = CreateDbContext(counter);

        var seats = await new SeatMapRepository(dbContext).GetSeatsByIdsAsync([], CancellationToken.None);

        seats.Should().BeEmpty();
        counter.Count.Should().Be(0);

        // 正向對照：同一個 counter 在非空清單時必須計到查詢，證明 0 不是因為 interceptor 沒掛上。
        await new SeatMapRepository(dbContext).GetSeatsByIdsAsync([Guid.NewGuid()], CancellationToken.None);
        counter.Count.Should().Be(1);
    }

    [Fact]
    public async Task GetByIdsAsync_WhenOtherEventsExist_ReturnsOnlyRequestedEvents()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (firstEventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var (secondEventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var (unrequestedEventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);

        await using var dbContext = _fixture.CreateDbContext();
        var events = await new EventRepository(dbContext).GetByIdsAsync([firstEventId, secondEventId], CancellationToken.None);

        events.Select(e => e.Id).Should().BeEquivalentTo([firstEventId, secondEventId]);
        events.Select(e => e.Id).Should().NotContain(unrequestedEventId);
    }

    [Fact]
    public async Task GetByIdsAsync_WithEmptyList_ReturnsEmptyWithoutQuery()
    {
        var counter = new CommandCounter();
        await using var dbContext = CreateDbContext(counter);

        var events = await new EventRepository(dbContext).GetByIdsAsync([], CancellationToken.None);

        events.Should().BeEmpty();
        counter.Count.Should().Be(0);

        await new EventRepository(dbContext).GetByIdsAsync([Guid.NewGuid()], CancellationToken.None);
        counter.Count.Should().Be(1);
    }

    [Fact]
    public async Task GetDisplayNamesByIdsAsync_WhenOtherMembersExist_ReturnsOnlyRequestedMembers()
    {
        var alice = Member.Register($"alice-{Guid.NewGuid():N}@example.com", "Alice", "hash");
        var bob = Member.Register($"bob-{Guid.NewGuid():N}@example.com", "Bob", "hash");
        var unrequested = Member.Register($"carol-{Guid.NewGuid():N}@example.com", "Carol", "hash");
        await using (var seedDbContext = _fixture.CreateDbContext())
        {
            seedDbContext.Members.AddRange(alice, bob, unrequested);
            await seedDbContext.SaveChangesAsync();
        }

        await using var dbContext = _fixture.CreateDbContext();
        var displayNames = await new MemberDisplayNameReader(dbContext).GetDisplayNamesByIdsAsync([alice.Id, bob.Id], CancellationToken.None);

        displayNames.Should().BeEquivalentTo(new Dictionary<Guid, string> { [alice.Id] = "Alice", [bob.Id] = "Bob" });
    }

    [Fact]
    public async Task GetDisplayNamesByIdsAsync_WithEmptyList_ReturnsEmptyWithoutQuery()
    {
        var counter = new CommandCounter();
        await using var dbContext = CreateDbContext(counter);

        var displayNames = await new MemberDisplayNameReader(dbContext).GetDisplayNamesByIdsAsync([], CancellationToken.None);

        displayNames.Should().BeEmpty();
        counter.Count.Should().Be(0);

        await new MemberDisplayNameReader(dbContext).GetDisplayNamesByIdsAsync([Guid.NewGuid()], CancellationToken.None);
        counter.Count.Should().Be(1);
    }

    // design.md 決策 4：取消 MUST 以例外往外拋，不得被當成「查無資料」回傳空集合——否則 Handler 會誤判為資料不一致。
    [Fact]
    public async Task GetByIdsAsync_WithCancelledToken_ThrowsOperationCanceledException()
    {
        await using var dbContext = _fixture.CreateDbContext();

        var act = () => new EventRepository(dbContext).GetByIdsAsync([Guid.NewGuid()], new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetSeatsByIdsAsync_WithCancelledToken_ThrowsOperationCanceledException()
    {
        await using var dbContext = _fixture.CreateDbContext();

        var act = () => new SeatMapRepository(dbContext).GetSeatsByIdsAsync([Guid.NewGuid()], new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetDisplayNamesByIdsAsync_WithCancelledToken_ThrowsOperationCanceledException()
    {
        await using var dbContext = _fixture.CreateDbContext();

        var act = () => new MemberDisplayNameReader(dbContext).GetDisplayNamesByIdsAsync([Guid.NewGuid()], new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private async Task<SeatMap> SeedSeatMapAsync(int seatCount)
    {
        var venue = new Venue(Guid.NewGuid(), $"Test Venue {Guid.NewGuid():N}");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        for (var i = 0; i < seatCount; i++)
            seatMap.AddSeat(i % 2 == 0 ? "A" : "B", $"{i + 1}");

        await using var dbContext = _fixture.CreateDbContext();
        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        await dbContext.SaveChangesAsync();
        return seatMap;
    }

    private ApplicationDbContext CreateDbContext(CommandCounter counter)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(counter)
            .Options;
        return new ApplicationDbContext(options);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => _count;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
    }
}
