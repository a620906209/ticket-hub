using FluentAssertions;
using ProjectC.Domain.Events;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Events;

/// <summary>list-pagination：分頁、排序、總數必須在資料庫端完成，且 <c>TotalCount</c> 與 <c>Items</c> 使用同一個 Organizer 範圍條件。</summary>
[Collection(PostgresCollection.Name)]
public class EventRepositoryPagingTests
{
    private static readonly DateTime BaseTime = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public EventRepositoryPagingTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class Seeder
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly Venue _venue;
        private readonly SeatMap _seatMap;
        private readonly int _idPrefix = Random.Shared.Next(1, int.MaxValue);

        public Guid OrganizerId { get; }

        private Seeder(ApplicationDbContext dbContext, Guid organizerId)
        {
            _dbContext = dbContext;
            OrganizerId = organizerId;
            _venue = new Venue(Guid.NewGuid(), $"Paging Venue {Guid.NewGuid():N}");
            _seatMap = new SeatMap(Guid.NewGuid(), _venue.Id);
            dbContext.Venues.Add(_venue);
            dbContext.SeatMaps.Add(_seatMap);
        }

        public static async Task<Seeder> CreateAsync(ApplicationDbContext dbContext)
            => new(dbContext, await OrganizerTestData.SeedApprovedOrganizerAsync(dbContext));

        /// <summary>sequence 決定 Id 的大小順序（Id 升冪 = sequence 升冪），讓同時間／同為 null 的排序可預測。</summary>
        public Guid AddEvent(int sequence, DateTime? createdAtUtc)
        {
            var id = Guid.Parse($"{_idPrefix:x8}-0000-0000-0000-{sequence:x12}");
            _dbContext.Events.Add(new Event(id, $"Event {sequence}", BaseTime.AddYears(1), _venue.Id, _seatMap.Id, OrganizerId, createdAtUtc: createdAtUtc));
            return id;
        }
    }

    // 對應 AC: EVT-LIST-001 / EVT-LIST-006（切頁，且 TotalCount 不含他人活動）
    [Fact]
    public async Task GetPageByOrganizerIdAsync_WhenOtherOrganizerHasEvents_PagesAndCountsOnlyCallerOrganizerEvents()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var seederA = await Seeder.CreateAsync(seedDbContext);
        var seederB = await Seeder.CreateAsync(seedDbContext);
        var eventsA = Enumerable.Range(0, 5).Select(i => seederA.AddEvent(i, BaseTime.AddMinutes(i))).ToList();
        var eventsB = Enumerable.Range(0, 4).Select(i => seederB.AddEvent(i, BaseTime.AddMinutes(i))).ToList();
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var page = await new EventRepository(readDbContext).GetPageByOrganizerIdAsync(seederA.OrganizerId, skip: 2, take: 2, CancellationToken.None);

        page.TotalCount.Should().Be(5);
        // 新到舊：A4, A3 | A2, A1 | A0；skip 2 take 2 = 第二頁。
        page.Items.Select(e => e.Id).Should().Equal(eventsA[2], eventsA[1]);
        page.Items.Select(e => e.Id).Should().NotIntersectWith(eventsB);
    }

    // 對應 AC: EVT-LIST-008（寫入順序打亂，結果仍新到舊）
    [Fact]
    public async Task GetPageByOrganizerIdAsync_WhenInsertedOutOfOrder_ReturnsNewestFirst()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var seeder = await Seeder.CreateAsync(seedDbContext);
        int[] insertOrder = [3, 1, 4, 0, 2];
        var idByOffset = insertOrder.ToDictionary(offset => offset, offset => seeder.AddEvent(offset, BaseTime.AddMinutes(offset)));
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var page = await new EventRepository(readDbContext).GetPageByOrganizerIdAsync(seeder.OrganizerId, skip: 0, take: 10, CancellationToken.None);

        page.Items.Select(e => e.Id).Should().Equal(new[] { 4, 3, 2, 1, 0 }.Select(offset => idByOffset[offset]));
    }

    // 對應 AC: EVT-LIST-008（建立時間相同 → Id 升冪）
    [Fact]
    public async Task GetPageByOrganizerIdAsync_WhenCreatedAtEqual_OrdersByIdAscending()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var seeder = await Seeder.CreateAsync(seedDbContext);
        var ids = new[] { 3, 1, 2 }.Select(sequence => (sequence, id: seeder.AddEvent(sequence, BaseTime))).OrderBy(x => x.sequence).Select(x => x.id).ToList();
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var page = await new EventRepository(readDbContext).GetPageByOrganizerIdAsync(seeder.OrganizerId, skip: 0, take: 10, CancellationToken.None);

        page.Items.Select(e => e.Id).Should().Equal(ids);
    }

    // 對應 AC: EVT-LIST-008（null 建立時間排在所有有建立時間的活動之後，null 之間依 Id 升冪；逐頁合併每筆恰好一次）
    [Fact]
    public async Task GetPageByOrganizerIdAsync_WhenSomeCreatedAtNull_PutsNullLastAndEachEventAppearsOnceAcrossPages()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var seeder = await Seeder.CreateAsync(seedDbContext);
        var nullB = seeder.AddEvent(6, null);
        var oldest = seeder.AddEvent(1, BaseTime);
        var nullA = seeder.AddEvent(5, null);
        var newest = seeder.AddEvent(2, BaseTime.AddMinutes(10));
        var middle = seeder.AddEvent(3, BaseTime.AddMinutes(5));
        await seedDbContext.SaveChangesAsync();

        var collected = new List<Guid>();
        for (var skip = 0; skip < 5; skip += 2)
        {
            await using var readDbContext = _fixture.CreateDbContext();
            var page = await new EventRepository(readDbContext).GetPageByOrganizerIdAsync(seeder.OrganizerId, skip, take: 2, CancellationToken.None);
            collected.AddRange(page.Items.Select(e => e.Id));
        }

        collected.Should().Equal(newest, middle, oldest, nullA, nullB);
    }

    // 對應 AC: LP-RESP-003（skip 超過總數：空 Items、TotalCount 為實際總數）
    [Fact]
    public async Task GetPageByOrganizerIdAsync_WhenSkipBeyondTotal_ReturnsEmptyItemsWithActualTotalCount()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var seeder = await Seeder.CreateAsync(seedDbContext);
        for (var i = 0; i < 3; i++) seeder.AddEvent(i, BaseTime.AddMinutes(i));
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var page = await new EventRepository(readDbContext).GetPageByOrganizerIdAsync(seeder.OrganizerId, skip: 20, take: 10, CancellationToken.None);

        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(3);
    }

    // 對應 AC: LP-RESP-004（無資料）
    [Fact]
    public async Task GetPageByOrganizerIdAsync_WhenOrganizerHasNoEvents_ReturnsEmptyAndZero()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var seeder = await Seeder.CreateAsync(seedDbContext);
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var page = await new EventRepository(readDbContext).GetPageByOrganizerIdAsync(seeder.OrganizerId, skip: 0, take: 10, CancellationToken.None);

        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
    }
}
