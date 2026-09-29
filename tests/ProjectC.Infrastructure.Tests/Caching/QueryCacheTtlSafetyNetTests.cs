using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectC.Application.Common;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Domain.Events;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Caching;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;
using StackExchange.Redis;

namespace ProjectC.Infrastructure.Tests.Caching;

// query-caching tasks.md 第 6 節：TTL 作為安全網，須用真實 Redis 容器驗證（不使用 FakeQueryCache，
// 那個 fake 沒有 TTL 到期的概念）。每個測試方法各自建立獨立的 Redis 容器（不透過共用的
// RedisCollection），理由同 CustomWebApplicationFactory 的 Redis 隔離——固定的快取 key 若共用
// 同一個 Redis，會被其他測試方法的殘留快取內容污染。
[Collection(PostgresCollection.Name)]
public class QueryCacheTtlSafetyNetTests
{
    private readonly PostgresFixture _postgresFixture;

    public QueryCacheTtlSafetyNetTests(PostgresFixture postgresFixture)
    {
        _postgresFixture = postgresFixture;
    }

    private sealed class AlwaysThrowingEventRepository : IEventRepository
    {
        public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => throw new InvalidOperationException("MUST NOT query the database on a cache hit.");
        public Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("MUST NOT query the database on a cache hit.");
        public Task<IReadOnlyList<Event>> GetByOrganizerIdAsync(Guid organizerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Add(Event @event) => throw new NotSupportedException();
        public void Update(Event @event) => throw new NotSupportedException();
        public Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class AlwaysThrowingTicketTypeRepository : ITicketTypeRepository
    {
        public Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => throw new InvalidOperationException("MUST NOT query the database on a cache hit.");
        public Task<IReadOnlyList<TicketType>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken) => throw new InvalidOperationException("MUST NOT query the database on a cache hit.");
        public void Add(TicketType ticketType) => throw new NotSupportedException();
        public Task<IReadOnlyList<TicketType>> GetForUpdateAsync(IReadOnlyList<Guid> ticketTypeIds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private async Task<(Guid VenueId, Guid SeatMapId)> SeedVenueAndSeatMapAsync()
    {
        await using var dbContext = _postgresFixture.CreateDbContext();
        var venue = new Venue(Guid.NewGuid(), "TTL Test Venue");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        await dbContext.SaveChangesAsync();
        return (venue.Id, seatMap.Id);
    }

    private async Task<Guid> SeedEventAsync(Guid venueId, Guid seatMapId)
    {
        await using var dbContext = _postgresFixture.CreateDbContext();
        var organizerId = await OrganizerTestData.SeedApprovedOrganizerAsync(dbContext);
        var @event = new Event(Guid.NewGuid(), "TTL Test Event", DateTime.UtcNow.AddDays(1), venueId, seatMapId, organizerId);
        dbContext.Events.Add(@event);
        await dbContext.SaveChangesAsync();
        return @event.Id;
    }

    // WSL2 VM 牆上時鐘實測約每 30 秒回跳 2–3 秒，Redis 以牆上時鐘計算到期，key 實際存活時間可能比 TTL 長這麼多。
    private static readonly TimeSpan ClockStepBackTolerance = TimeSpan.FromSeconds(6);

    /// <summary>
    /// 輪詢直到 Redis 自身已淘汰該 key，且必須在 TTL + 時鐘回跳餘裕內完成。不用固定 Task.Delay：時鐘回跳時
    /// 固定等待可能不足以跨過 TTL 而誤判（flaky）。上限貼近 TTL 而非寬鬆的固定值，TTL 若被誤設為過長的值仍會失敗。
    /// </summary>
    private static async Task WaitUntilKeyExpiredAsync(IConnectionMultiplexer connection, string key, TimeSpan ttl)
    {
        // Stopwatch 為單調時鐘，不受牆上時鐘回跳影響。
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (await connection.GetDatabase().KeyExistsAsync(key))
        {
            stopwatch.Elapsed.Should().BeLessThan(ttl + ClockStepBackTolerance, "快取必須在 TTL（加上時鐘回跳餘裕）內由 Redis 淘汰");
            await Task.Delay(100);
        }
    }

    // QC-TTL-002a（tasks.md 6.2a）。
    [Fact]
    public async Task GetEventsHandler_AfterTtlExpires_RequeriesDatabaseAndRefillsCache()
    {
        var redis = new RedisFixture();
        await redis.InitializeAsync();
        try
        {
            // TTL 3 秒：最後一步須在 TTL 內命中重新寫入的快取，過短的 TTL 在高負載下會提前過期而誤判。
            var options = new QueryCacheOptions { EventListTtlSeconds = 3, TicketTypesTtlSeconds = 10 };
            var connection = redis.CreateConnection();
            var queryCache = new RedisQueryCache(connection, NullLogger<RedisQueryCache>.Instance);

            await using (var dbContext = _postgresFixture.CreateDbContext())
            {
                await new GetEventsHandler(new EventRepository(dbContext), queryCache, options).HandleAsync(CancellationToken.None);
            }

            var (venueId, seatMapId) = await SeedVenueAndSeatMapAsync();
            var newEventId = await SeedEventAsync(venueId, seatMapId);

            await WaitUntilKeyExpiredAsync(connection, GetEventsHandler.CacheKey, TimeSpan.FromSeconds(options.EventListTtlSeconds));

            await using (var dbContext = _postgresFixture.CreateDbContext())
            {
                var events = await new GetEventsHandler(new EventRepository(dbContext), queryCache, options).HandleAsync(CancellationToken.None);
                events.Should().Contain(e => e.Id == newEventId, "TTL 已過期，第二次呼叫應重新查詢資料庫並看到剛新增的活動");
            }

            var eventsFromCache = await new GetEventsHandler(new AlwaysThrowingEventRepository(), queryCache, options).HandleAsync(CancellationToken.None);
            eventsFromCache.Should().Contain(e => e.Id == newEventId, "第二次呼叫應已重新寫入快取，第三次呼叫應直接命中，不查詢資料庫");
        }
        finally
        {
            await redis.DisposeAsync();
        }
    }

    // QC-TTL-002b（tasks.md 6.2b）。
    [Fact]
    public async Task GetTicketTypesHandler_AfterTtlExpires_RequeriesDatabaseAndRefillsCache()
    {
        var redis = new RedisFixture();
        await redis.InitializeAsync();
        try
        {
            // TicketTypesTtlSeconds 與 6.2a 的 EventListTtlSeconds 是兩個獨立設定值：這裡刻意把
            // EventListTtlSeconds 設一個很長的值，證明本測試只依賴 TicketTypesTtlSeconds。
            var options = new QueryCacheOptions { EventListTtlSeconds = 60, TicketTypesTtlSeconds = 3 };
            var connection = redis.CreateConnection();
            var queryCache = new RedisQueryCache(connection, NullLogger<RedisQueryCache>.Instance);
            var (venueId, seatMapId) = await SeedVenueAndSeatMapAsync();
            var eventId = await SeedEventAsync(venueId, seatMapId);

            await using (var dbContext = _postgresFixture.CreateDbContext())
            {
                var handler = new GetTicketTypesHandler(new EventRepository(dbContext), new TicketTypeRepository(dbContext), queryCache, options);
                await handler.HandleAsync(eventId, CancellationToken.None);
            }

            Guid newTicketTypeId;
            await using (var dbContext = _postgresFixture.CreateDbContext())
            {
                var @event = await dbContext.Events.FindAsync(eventId);
                var ticketType = @event!.CreateCountBasedTicketType("站票", 300m, 10);
                dbContext.TicketTypes.Add(ticketType);
                await dbContext.SaveChangesAsync();
                newTicketTypeId = ticketType.Id;
            }

            await WaitUntilKeyExpiredAsync(connection, GetTicketTypesHandler.BuildCacheKey(eventId), TimeSpan.FromSeconds(options.TicketTypesTtlSeconds));

            await using (var dbContext = _postgresFixture.CreateDbContext())
            {
                var handler = new GetTicketTypesHandler(new EventRepository(dbContext), new TicketTypeRepository(dbContext), queryCache, options);
                var result = await handler.HandleAsync(eventId, CancellationToken.None);
                result.Value.Should().Contain(t => t.Id == newTicketTypeId, "TTL 已過期，應重新查詢資料庫");
            }

            // 活動存在性檢查（GetByIdAsync）本身也會查資料庫；活動存在性檢查發生在快取讀取之前
            // （見 3.1／GetTicketTypesHandler 實作），這裡用真正的 EventRepository 查一次存在性
            // （開銷可忽略），只驗證 ITicketTypeRepository 沒有被重新呼叫（命中快取）。
            await using var verifyDbContext = _postgresFixture.CreateDbContext();
            var handlerVerifyCacheHit = new GetTicketTypesHandler(
                new EventRepository(verifyDbContext), new AlwaysThrowingTicketTypeRepository(), queryCache, options);
            var cachedResult = await handlerVerifyCacheHit.HandleAsync(eventId, CancellationToken.None);
            cachedResult.Value.Should().Contain(t => t.Id == newTicketTypeId, "第二次呼叫應已重新寫入快取，第三次呼叫應直接命中，不查詢票種 Repository");
        }
        finally
        {
            await redis.DisposeAsync();
        }
    }

    // QC-TTL-003（tasks.md 6.2c）。
    [Fact]
    public async Task GetEventsHandler_BeforeTtlExpires_StaysHitAndReturnsStaleContent()
    {
        var redis = new RedisFixture();
        await redis.InitializeAsync();
        try
        {
            var options = new QueryCacheOptions { EventListTtlSeconds = 5, TicketTypesTtlSeconds = 10 };
            var queryCache = new RedisQueryCache(redis.CreateConnection(), NullLogger<RedisQueryCache>.Instance);

            IReadOnlyList<EventDto> firstResult;
            await using (var dbContext = _postgresFixture.CreateDbContext())
            {
                firstResult = await new GetEventsHandler(new EventRepository(dbContext), queryCache, options).HandleAsync(CancellationToken.None);
            }

            var (venueId, seatMapId) = await SeedVenueAndSeatMapAsync();
            var newEventId = await SeedEventAsync(venueId, seatMapId);

            await Task.Delay(TimeSpan.FromSeconds(1));

            var secondResult = await new GetEventsHandler(new AlwaysThrowingEventRepository(), queryCache, options).HandleAsync(CancellationToken.None);

            secondResult.Should().BeEquivalentTo(firstResult, options => options.WithStrictOrdering(), "TTL 未到期前應持續命中快取，不因時間流逝而提前失效");
            secondResult.Should().NotContain(e => e.Id == newEventId, "第二次呼叫回傳的仍是異動前的舊內容");
        }
        finally
        {
            await redis.DisposeAsync();
        }
    }

    // TTL 邊界（tasks.md 6.2d）：不要求精確命中「剛好等於 TTL」，只要求「明顯小於 TTL 持續命中」與
    // 「明顯大於 TTL、在合理容忍範圍內必定失效」。
    [Fact]
    public async Task GetEventsHandler_WithinToleranceAroundTtl_HitsBeforeAndMissesAfter()
    {
        var redis = new RedisFixture();
        await redis.InitializeAsync();
        try
        {
            var options = new QueryCacheOptions { EventListTtlSeconds = 2, TicketTypesTtlSeconds = 10 };
            var connection = redis.CreateConnection();
            var queryCache = new RedisQueryCache(connection, NullLogger<RedisQueryCache>.Instance);

            await using (var dbContext = _postgresFixture.CreateDbContext())
            {
                await new GetEventsHandler(new EventRepository(dbContext), queryCache, options).HandleAsync(CancellationToken.None);
            }

            await Task.Delay(TimeSpan.FromSeconds(0.5));
            var actBeforeExpiry = () => new GetEventsHandler(new AlwaysThrowingEventRepository(), queryCache, options).HandleAsync(CancellationToken.None);
            await actBeforeExpiry.Should().NotThrowAsync("明顯小於 TTL（0.5 秒 < 2 秒）應仍命中快取，不查詢資料庫");

            // 「合理容忍範圍」以累計等待時間計（WaitUntilKeyExpiredAsync 上限為 TTL + 時鐘回跳餘裕），而非固定 Task.Delay：
            // Redis 以牆上時鐘計算到期，WSL2 時鐘回跳 2–3 秒時固定等待可能不足。
            await WaitUntilKeyExpiredAsync(connection, GetEventsHandler.CacheKey, TimeSpan.FromSeconds(options.EventListTtlSeconds));
            // 用 AlwaysThrowingEventRepository 證明真的回到資料庫查詢；改前使用真正的 Repository，
            // 命中與未命中都不會拋例外，這個斷言無法失敗。
            var actAfterExpiry = () => new GetEventsHandler(new AlwaysThrowingEventRepository(), queryCache, options).HandleAsync(CancellationToken.None);
            await actAfterExpiry.Should().ThrowAsync<InvalidOperationException>("超過 TTL 後快取已淘汰，應重新查詢資料庫");
        }
        finally
        {
            await redis.DisposeAsync();
        }
    }

    // QC-TTL-004（tasks.md 6.2e）：以 TaskCompletionSource 決定性地構造「查詢途中發生異動」的競態。
    [Fact]
    public async Task GetTicketTypesHandler_WhenInvalidationHappensWhileQueryInFlight_CacheTemporarilyResurrectsStaleValue_ButExpiresWithinTtl()
    {
        var redis = new RedisFixture();
        await redis.InitializeAsync();
        try
        {
            // TTL 取 5 秒而非更短：步驟 5 必須在 TTL 內命中 R 寫入的舊快取，完整測試套件平行執行時負載高、
            // WSL2 時鐘也會跳動，過短的 TTL 會讓快取在 S 查詢前就過期而誤判。
            var options = new QueryCacheOptions { EventListTtlSeconds = 60, TicketTypesTtlSeconds = 5 };
            var connection = redis.CreateConnection();
            var queryCache = new RedisQueryCache(connection, NullLogger<RedisQueryCache>.Instance);
            var (venueId, seatMapId) = await SeedVenueAndSeatMapAsync();
            var eventId = await SeedEventAsync(venueId, seatMapId);

            var reachedSyncPoint = new TaskCompletionSource();
            var releaseSyncPoint = new TaskCompletionSource();

            await using var rDbContext = _postgresFixture.CreateDbContext();
            var slowTicketTypeRepository = new SlowTicketTypeRepository(new TicketTypeRepository(rDbContext), reachedSyncPoint, releaseSyncPoint);
            var handlerR = new GetTicketTypesHandler(new EventRepository(rDbContext), slowTicketTypeRepository, queryCache, options);

            // 步驟 2：啟動 R，卡在同步點（此時尚未包含新票種）。
            var taskR = handlerR.HandleAsync(eventId, CancellationToken.None);
            await reachedSyncPoint.Task;

            // 步驟 3：R 仍卡住期間，建立新票種並完成失效（此時快取仍是空的）。
            Guid newTicketTypeId;
            await using (var writeDbContext = _postgresFixture.CreateDbContext())
            {
                var createHandler = new CreateTicketTypeHandler(
                    new EventRepository(writeDbContext), new SeatMapRepository(writeDbContext), new TicketTypeRepository(writeDbContext),
                    new UnitOfWork(writeDbContext), new CreateTicketTypeRequestValidator(), queryCache);
                var result = await createHandler.HandleAsync(
                    eventId, (await writeDbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId)).OrganizerId, new CreateTicketTypeRequest("新票種", 500m, RequiresSeat: false, AvailableQuantity: 20), CancellationToken.None);
                result.IsSuccess.Should().BeTrue();
                newTicketTypeId = result.Value;
            }

            // 步驟 4：釋放 R，讓它把舊資料（不含新票種）寫入快取。
            releaseSyncPoint.SetResult();
            var rResult = await taskR;
            rResult.Value.Should().NotContain(t => t.Id == newTicketTypeId, "R 讀到的必須是尚未包含新票種的舊資料");

            // 步驟 5：立即再查一次（S），應命中快取、拿到 R 寫入的舊資料——證明競態確實發生。
            // 活動存在性檢查會先查一次 EventRepository（GetTicketTypesHandler 既有邏輯，見 3.1），
            // 只有票種列表本身須命中快取、不查 ITicketTypeRepository。
            await using var sDbContext = _postgresFixture.CreateDbContext();
            var sResult = await new GetTicketTypesHandler(new EventRepository(sDbContext), new AlwaysThrowingTicketTypeRepository(), queryCache, options)
                .HandleAsync(eventId, CancellationToken.None);
            sResult.Value.Should().NotContain(t => t.Id == newTicketTypeId, "這是本次改動接受的既知限制：短暫復活的舊快取");

            // 步驟 6：等待 TTL 到期，確認陳舊快取最終仍會過期並看到新票種。
            await WaitUntilKeyExpiredAsync(connection, GetTicketTypesHandler.BuildCacheKey(eventId), TimeSpan.FromSeconds(options.TicketTypesTtlSeconds));

            await using var verifyDbContext = _postgresFixture.CreateDbContext();
            var finalResult = await new GetTicketTypesHandler(new EventRepository(verifyDbContext), new TicketTypeRepository(verifyDbContext), queryCache, options)
                .HandleAsync(eventId, CancellationToken.None);
            finalResult.Value.Should().Contain(t => t.Id == newTicketTypeId, "即使發生競態，陳舊快取仍會在有限時間內自然過期");
        }
        finally
        {
            await redis.DisposeAsync();
        }
    }

    private sealed class SlowTicketTypeRepository : ITicketTypeRepository
    {
        private readonly ITicketTypeRepository _inner;
        private readonly TaskCompletionSource _reachedSyncPoint;
        private readonly TaskCompletionSource _releaseSyncPoint;

        public SlowTicketTypeRepository(ITicketTypeRepository inner, TaskCompletionSource reachedSyncPoint, TaskCompletionSource releaseSyncPoint)
        {
            _inner = inner;
            _reachedSyncPoint = reachedSyncPoint;
            _releaseSyncPoint = releaseSyncPoint;
        }

        public Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => _inner.GetByIdAsync(id, cancellationToken);

        public async Task<IReadOnlyList<TicketType>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
        {
            var result = await _inner.GetByEventIdAsync(eventId, cancellationToken);
            _reachedSyncPoint.SetResult();
            await _releaseSyncPoint.Task;
            return result;
        }

        public void Add(TicketType ticketType) => _inner.Add(ticketType);

        public Task<IReadOnlyList<TicketType>> GetForUpdateAsync(IReadOnlyList<Guid> ticketTypeIds, CancellationToken cancellationToken)
            => _inner.GetForUpdateAsync(ticketTypeIds, cancellationToken);
    }

    // 6.3：RedisQueryCache 底層 TTL 到期行為，補充驗證，不依賴 Handler 層。
    [Fact]
    public async Task RedisQueryCache_AfterTtlExpires_KeyNoLongerExistsInRedis()
    {
        var redis = new RedisFixture();
        await redis.InitializeAsync();
        try
        {
            var connection = redis.CreateConnection();
            var queryCache = new RedisQueryCache(connection, NullLogger<RedisQueryCache>.Instance);
            var key = $"ttl-test:{Guid.NewGuid():N}";

            var ttl = TimeSpan.FromSeconds(1);
            await queryCache.SetAsync(key, "value", ttl, CancellationToken.None);
            await WaitUntilKeyExpiredAsync(connection, key, ttl);

            var result = await queryCache.GetAsync<string>(key, CancellationToken.None);
            result.IsHit.Should().BeFalse();

            var exists = await connection.GetDatabase().KeyExistsAsync(key);
            exists.Should().BeFalse("TTL 到期後應由 Redis 自動淘汰，而非程式邏輯主動清除");
        }
        finally
        {
            await redis.DisposeAsync();
        }
    }
}
