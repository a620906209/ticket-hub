using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Tickets;

// query-caching tasks.md 5.4／5.5／5.6a／5.6b／5.7：驗證票種列表快取失效「交易提交後才觸發」，
// 須用獨立資料庫連線在 RemoveAsync 回呼內確認已讀到 commit 後的新值。
[Collection(PostgresCollection.Name)]
public class QueryCacheTicketTypeInvalidationOrderingTests
{
    private readonly PostgresFixture _fixture;
    private static readonly QueryCacheOptions Options = new() { EventListTtlSeconds = 30, TicketTypesTtlSeconds = 10 };

    public QueryCacheTicketTypeInvalidationOrderingTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Guid> SeedMemberAsync()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var member = Member.Register($"{Guid.NewGuid():N}@example.com", "Buyer", "hash");
        dbContext.Members.Add(member);
        await dbContext.SaveChangesAsync();
        return member.Id;
    }

    private async Task<Guid> SeedEventAsync()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var venue = new Venue(Guid.NewGuid(), "Test Venue");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), venue.Id, seatMap.Id);
        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        dbContext.Events.Add(@event);
        await dbContext.SaveChangesAsync();
        return @event.Id;
    }

    private async Task<Guid> SeedCountBasedTicketTypeAsync(Guid eventId, int availableQuantity)
    {
        await using var dbContext = _fixture.CreateDbContext();
        var @event = await dbContext.Events.SingleAsync(e => e.Id == eventId);
        var ticketType = @event.CreateCountBasedTicketType("站票", 300m, availableQuantity);
        dbContext.TicketTypes.Add(ticketType);
        await dbContext.SaveChangesAsync();
        return ticketType.Id;
    }

    private static GetTicketTypesHandler CreateGetTicketTypesHandler(ApplicationDbContext dbContext, FakeQueryCache queryCache)
        => new(new EventRepository(dbContext), new TicketTypeRepository(dbContext), queryCache, Options);

    // QC-TT-INV-001（tasks.md 5.4）。
    [Fact]
    public async Task CreateTicketTypeHandler_AfterCommit_InvalidatesTicketTypesCache_AndCommitPrecedesInvalidation()
    {
        var eventId = await SeedEventAsync();
        var queryCache = new FakeQueryCache();
        var cacheKey = GetTicketTypesHandler.BuildCacheKey(eventId);

        await using (var readDbContext = _fixture.CreateDbContext())
        {
            await CreateGetTicketTypesHandler(readDbContext, queryCache).HandleAsync(eventId, CancellationToken.None);
        }
        queryCache.ContainsKey(cacheKey).Should().BeTrue();

        TicketType? committedNewTicketTypeSeenDuringCallback = null;
        queryCache.OnRemoveAsync = async key =>
        {
            if (key != cacheKey) return;
            await using var independentDbContext = _fixture.CreateDbContext();
            committedNewTicketTypeSeenDuringCallback = await independentDbContext.TicketTypes
                .FirstOrDefaultAsync(t => t.EventId == eventId && t.ZoneCode == "新票種");
        };

        Guid newTicketTypeId;
        await using (var writeDbContext = _fixture.CreateDbContext())
        {
            var handler = new CreateTicketTypeHandler(
                new EventRepository(writeDbContext),
                new SeatMapRepository(writeDbContext),
                new TicketTypeRepository(writeDbContext),
                new UnitOfWork(writeDbContext),
                new CreateTicketTypeRequestValidator(),
                queryCache);

            var result = await handler.HandleAsync(
                eventId, new CreateTicketTypeRequest("新票種", 500m, RequiresSeat: false, AvailableQuantity: 20), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            newTicketTypeId = result.Value;
        }

        queryCache.ContainsKey(cacheKey).Should().BeFalse();
        committedNewTicketTypeSeenDuringCallback.Should().NotBeNull("commit 必須先於 invalidation 發生");

        await using var verifyDbContext = _fixture.CreateDbContext();
        var ticketTypesAfter = await CreateGetTicketTypesHandler(verifyDbContext, queryCache).HandleAsync(eventId, CancellationToken.None);
        ticketTypesAfter.Value.Should().Contain(t => t.Id == newTicketTypeId);
    }

    // QC-TT-INV-002（tasks.md 5.5）。
    [Fact]
    public async Task PlaceOrderAsync_WithCountingSelection_AfterCommit_InvalidatesTicketTypesCache_AndCommitPrecedesInvalidation()
    {
        var eventId = await SeedEventAsync();
        var ticketTypeId = await SeedCountBasedTicketTypeAsync(eventId, availableQuantity: 10);
        var buyerId = await SeedMemberAsync();
        var queryCache = new FakeQueryCache();
        var cacheKey = GetTicketTypesHandler.BuildCacheKey(eventId);

        await using (var readDbContext = _fixture.CreateDbContext())
        {
            await CreateGetTicketTypesHandler(readDbContext, queryCache).HandleAsync(eventId, CancellationToken.None);
        }
        queryCache.ContainsKey(cacheKey).Should().BeTrue();

        int? committedAvailableQuantitySeenDuringCallback = null;
        queryCache.OnRemoveAsync = async key =>
        {
            if (key != cacheKey) return;
            await using var independentDbContext = _fixture.CreateDbContext();
            var ticketType = await independentDbContext.TicketTypes.AsNoTracking().SingleAsync(t => t.Id == ticketTypeId);
            committedAvailableQuantitySeenDuringCallback = ticketType.AvailableQuantity;
        };

        await using (var writeDbContext = _fixture.CreateDbContext())
        {
            var orderService = OrderServiceTestFactory.Create(writeDbContext, queryCache: queryCache);
            var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketTypeId, 3)]);
            var result = await orderService.PlaceOrderAsync(buyerId, request, CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }

        queryCache.ContainsKey(cacheKey).Should().BeFalse();
        committedAvailableQuantitySeenDuringCallback.Should().Be(7, "commit 必須先於 invalidation 發生，回呼當下應已看到扣減後的新數量");

        await using var verifyDbContext = _fixture.CreateDbContext();
        var ticketTypesAfter = await CreateGetTicketTypesHandler(verifyDbContext, queryCache).HandleAsync(eventId, CancellationToken.None);
        ticketTypesAfter.Value.Should().ContainSingle(t => t.Id == ticketTypeId && t.AvailableQuantity == 7);
    }

    // QC-TT-INV-003a（tasks.md 5.6a）。
    [Fact]
    public async Task CancelOrderAsync_AfterCommit_InvalidatesTicketTypesCache_AndCommitPrecedesInvalidation()
    {
        var eventId = await SeedEventAsync();
        var ticketTypeId = await SeedCountBasedTicketTypeAsync(eventId, availableQuantity: 10);
        var queryCache = new FakeQueryCache();
        var cacheKey = GetTicketTypesHandler.BuildCacheKey(eventId);
        var buyerId = await SeedMemberAsync();

        Guid orderId;
        await using (var placeDbContext = _fixture.CreateDbContext())
        {
            var orderService = OrderServiceTestFactory.Create(placeDbContext, queryCache: queryCache);
            var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketTypeId, 3)]);
            var placeResult = await orderService.PlaceOrderAsync(buyerId, request, CancellationToken.None);
            placeResult.IsSuccess.Should().BeTrue();
            orderId = placeResult.Value;
        }

        // 重新讓快取寫入歸還前（已扣減）的 AvailableQuantity。
        await using (var readDbContext = _fixture.CreateDbContext())
        {
            await CreateGetTicketTypesHandler(readDbContext, queryCache).HandleAsync(eventId, CancellationToken.None);
        }
        queryCache.ContainsKey(cacheKey).Should().BeTrue();

        int? committedAvailableQuantitySeenDuringCallback = null;
        queryCache.OnRemoveAsync = async key =>
        {
            if (key != cacheKey) return;
            await using var independentDbContext = _fixture.CreateDbContext();
            var ticketType = await independentDbContext.TicketTypes.AsNoTracking().SingleAsync(t => t.Id == ticketTypeId);
            committedAvailableQuantitySeenDuringCallback = ticketType.AvailableQuantity;
        };

        await using (var cancelDbContext = _fixture.CreateDbContext())
        {
            var orderService = OrderServiceTestFactory.Create(cancelDbContext, queryCache: queryCache);
            var cancelResult = await orderService.CancelOrderAsync(orderId, buyerId, CancellationToken.None);
            cancelResult.IsSuccess.Should().BeTrue();
        }

        queryCache.ContainsKey(cacheKey).Should().BeFalse();
        committedAvailableQuantitySeenDuringCallback.Should().Be(10, "commit 必須先於 invalidation 發生，回呼當下應已看到歸還後的新數量");

        await using var verifyDbContext = _fixture.CreateDbContext();
        var ticketTypesAfter = await CreateGetTicketTypesHandler(verifyDbContext, queryCache).HandleAsync(eventId, CancellationToken.None);
        ticketTypesAfter.Value.Should().ContainSingle(t => t.Id == ticketTypeId && t.AvailableQuantity == 10);
    }

    // QC-TT-INV-003b（tasks.md 5.6b）：與 5.6a 是兩個不同的呼叫入口，須各自獨立驗證。
    [Fact]
    public async Task CancelExpiredOrderAsync_AfterCommit_InvalidatesTicketTypesCache_AndCommitPrecedesInvalidation()
    {
        var eventId = await SeedEventAsync();
        var ticketTypeId = await SeedCountBasedTicketTypeAsync(eventId, availableQuantity: 10);
        var buyerId = await SeedMemberAsync();
        var queryCache = new FakeQueryCache();
        var cacheKey = GetTicketTypesHandler.BuildCacheKey(eventId);
        var fakeDateTimeProvider = new FakeDateTimeProvider { UtcNow = DateTime.UtcNow };

        Guid orderId;
        DateTime heldUntilUtc;
        await using (var placeDbContext = _fixture.CreateDbContext())
        {
            var orderService = OrderServiceTestFactory.Create(placeDbContext, queryCache: queryCache, dateTimeProvider: fakeDateTimeProvider);
            var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketTypeId, 3)]);
            var placeResult = await orderService.PlaceOrderAsync(buyerId, request, CancellationToken.None);
            placeResult.IsSuccess.Should().BeTrue();
            orderId = placeResult.Value;
            heldUntilUtc = (await placeDbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId)).HeldUntilUtc;
        }

        fakeDateTimeProvider.UtcNow = heldUntilUtc.AddSeconds(1);

        await using (var readDbContext = _fixture.CreateDbContext())
        {
            await CreateGetTicketTypesHandler(readDbContext, queryCache).HandleAsync(eventId, CancellationToken.None);
        }
        queryCache.ContainsKey(cacheKey).Should().BeTrue();

        int? committedAvailableQuantitySeenDuringCallback = null;
        queryCache.OnRemoveAsync = async key =>
        {
            if (key != cacheKey) return;
            await using var independentDbContext = _fixture.CreateDbContext();
            var ticketType = await independentDbContext.TicketTypes.AsNoTracking().SingleAsync(t => t.Id == ticketTypeId);
            committedAvailableQuantitySeenDuringCallback = ticketType.AvailableQuantity;
        };

        await using (var cleanupDbContext = _fixture.CreateDbContext())
        {
            var orderService = OrderServiceTestFactory.Create(cleanupDbContext, queryCache: queryCache, dateTimeProvider: fakeDateTimeProvider);
            var cleanupResult = await orderService.CancelExpiredOrderAsync(orderId, CancellationToken.None);
            cleanupResult.IsSuccess.Should().BeTrue();
        }

        queryCache.ContainsKey(cacheKey).Should().BeFalse();
        committedAvailableQuantitySeenDuringCallback.Should().Be(10, "commit 必須先於 invalidation 發生");

        await using var verifyDbContext = _fixture.CreateDbContext();
        var ticketTypesAfter = await CreateGetTicketTypesHandler(verifyDbContext, queryCache).HandleAsync(eventId, CancellationToken.None);
        ticketTypesAfter.Value.Should().ContainSingle(t => t.Id == ticketTypeId && t.AvailableQuantity == 10);
    }

    // QC-TT-INV-004（tasks.md 5.7）：活動 A 的異動不得影響活動 B 的快取。
    [Fact]
    public async Task PlaceOrderAsync_ForEventA_DoesNotInvalidateEventBsTicketTypesCache()
    {
        var eventAId = await SeedEventAsync();
        var ticketTypeAId = await SeedCountBasedTicketTypeAsync(eventAId, availableQuantity: 10);
        var eventBId = await SeedEventAsync();
        await SeedCountBasedTicketTypeAsync(eventBId, availableQuantity: 5);
        var buyerId = await SeedMemberAsync();
        var queryCache = new FakeQueryCache();
        var cacheKeyA = GetTicketTypesHandler.BuildCacheKey(eventAId);
        var cacheKeyB = GetTicketTypesHandler.BuildCacheKey(eventBId);

        await using (var readDbContext = _fixture.CreateDbContext())
        {
            var handler = CreateGetTicketTypesHandler(readDbContext, queryCache);
            await handler.HandleAsync(eventAId, CancellationToken.None);
            await handler.HandleAsync(eventBId, CancellationToken.None);
        }
        queryCache.ContainsKey(cacheKeyA).Should().BeTrue();
        queryCache.ContainsKey(cacheKeyB).Should().BeTrue();

        await using (var writeDbContext = _fixture.CreateDbContext())
        {
            var orderService = OrderServiceTestFactory.Create(writeDbContext, queryCache: queryCache);
            var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketTypeAId, 2)]);
            var result = await orderService.PlaceOrderAsync(buyerId, request, CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }

        queryCache.ContainsKey(cacheKeyA).Should().BeFalse();
        queryCache.ContainsKey(cacheKeyB).Should().BeTrue("活動 A 的庫存異動不應影響活動 B 的快取 entry");

        // 直接命中快取，不查資料庫也能證明——把活動 B 對應的 ITicketTypeRepository 換成一個永遠回傳
        // 空列表的假實作：若真的查了資料庫，回傳值會是空列表而非快取內容，斷言會失敗。
        await using var verifyDbContext = _fixture.CreateDbContext();
        var handlerWithBrokenRepository = new GetTicketTypesHandler(
            new EventRepository(verifyDbContext), new AlwaysEmptyTicketTypeRepository(), queryCache, Options);
        var ticketTypesForB = await handlerWithBrokenRepository.HandleAsync(eventBId, CancellationToken.None);
        ticketTypesForB.Value.Should().ContainSingle(t => t.AvailableQuantity == 5, "應直接命中快取，未實際查詢（刻意壞掉的）Repository");
    }

    private sealed class AlwaysEmptyTicketTypeRepository : ITicketTypeRepository
    {
        public Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<TicketType?>(null);

        public Task<IReadOnlyList<TicketType>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<TicketType>>([]);

        public void Add(TicketType ticketType) => throw new NotSupportedException();

        public Task<IReadOnlyList<TicketType>> GetForUpdateAsync(IReadOnlyList<Guid> ticketTypeIds, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
