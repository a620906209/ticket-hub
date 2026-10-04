using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using ProjectC.Application.Common;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Caching;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Security;
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
    // 以字面值斷言而非引用正式常數：常數改值後，引用常數的測試無法證明契約是 v2（event-sales-window tasks.md 5.5）。
    private const string EventListCacheKeyV2 = "query-cache:events:list:v2";

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
        public Task<IReadOnlyList<Event>> GetByIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken) => throw new NotSupportedException();
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

    private static string BuildTicketTypesCacheKey(Guid eventId) => $"query-cache:ticket-types:event:{eventId}";

    private async Task<Guid> SeedEventAsync(Guid venueId, Guid seatMapId, string title = "TTL Test Event")
    {
        await using var dbContext = _postgresFixture.CreateDbContext();
        var organizerId = await OrganizerTestData.SeedApprovedOrganizerAsync(dbContext);
        var @event = new Event(Guid.NewGuid(), title, DateTime.UtcNow.AddDays(1), venueId, seatMapId, organizerId);
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

            await WaitUntilKeyExpiredAsync(connection, EventListCacheKeyV2, TimeSpan.FromSeconds(options.EventListTtlSeconds));

            await using (var dbContext = _postgresFixture.CreateDbContext())
            {
                var events = await new GetEventsHandler(new EventRepository(dbContext), queryCache, options).HandleAsync(CancellationToken.None);
                events.Should().Contain(e => e.Id == newEventId, "TTL 已過期，第二次呼叫應重新查詢資料庫並看到剛新增的活動");
            }
            (await connection.GetDatabase().KeyExistsAsync(EventListCacheKeyV2)).Should().BeTrue("重新查詢後應重新寫入 v2 key");

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

            await WaitUntilKeyExpiredAsync(connection, BuildTicketTypesCacheKey(eventId), TimeSpan.FromSeconds(options.TicketTypesTtlSeconds));

            await using (var dbContext = _postgresFixture.CreateDbContext())
            {
                var handler = new GetTicketTypesHandler(new EventRepository(dbContext), new TicketTypeRepository(dbContext), queryCache, options);
                var result = await handler.HandleAsync(eventId, CancellationToken.None);
                result.Value.Should().Contain(t => t.Id == newTicketTypeId, "TTL 已過期，應重新查詢資料庫");
            }
            (await connection.GetDatabase().KeyExistsAsync(BuildTicketTypesCacheKey(eventId))).Should().BeTrue("重新查詢後應重新寫入票種 key");
            (await connection.GetDatabase().KeyTimeToLiveAsync(BuildTicketTypesCacheKey(eventId))).Should().BePositive();

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
            var connection = redis.CreateConnection();
            var queryCache = new RedisQueryCache(connection, NullLogger<RedisQueryCache>.Instance);

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
            (await connection.GetDatabase().KeyExistsAsync(EventListCacheKeyV2)).Should().BeTrue("TTL 內 v2 key 應仍存在");
        }
        finally
        {
            await redis.DisposeAsync();
        }
    }

    // QC-TTL-003：活動列表 v2 key，TTL 前後。
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
            (await connection.GetDatabase().KeyExistsAsync(EventListCacheKeyV2)).Should().BeTrue("明顯小於 TTL 時 v2 key 應仍存在");

            // 「合理容忍範圍」以累計等待時間計（WaitUntilKeyExpiredAsync 上限為 TTL + 時鐘回跳餘裕），而非固定 Task.Delay：
            // Redis 以牆上時鐘計算到期，WSL2 時鐘回跳 2–3 秒時固定等待可能不足。
            await WaitUntilKeyExpiredAsync(connection, EventListCacheKeyV2, TimeSpan.FromSeconds(options.EventListTtlSeconds));
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

    // QC-TTL-001（event-sales-window tasks.md 5.5）：兩個 TTL 刻意設不同值，避免把其中一個 key 的 TTL 誤認成另一個。
    [Fact]
    public async Task Handlers_OnCacheMiss_WriteKeysWithConfiguredPositiveTtl()
    {
        var redis = new RedisFixture();
        await redis.InitializeAsync();
        try
        {
            var options = new QueryCacheOptions { EventListTtlSeconds = 30, TicketTypesTtlSeconds = 20 };
            var connection = redis.CreateConnection();
            var queryCache = new RedisQueryCache(connection, NullLogger<RedisQueryCache>.Instance);
            var (venueId, seatMapId) = await SeedVenueAndSeatMapAsync();
            var eventId = await SeedEventAsync(venueId, seatMapId);
            await SeedTicketTypeAsync(eventId, $"TTL-{Guid.NewGuid():N}");

            await using (var dbContext = _postgresFixture.CreateDbContext())
            {
                await new GetEventsHandler(new EventRepository(dbContext), queryCache, options).HandleAsync(CancellationToken.None);
                await new GetTicketTypesHandler(new EventRepository(dbContext), new TicketTypeRepository(dbContext), queryCache, options)
                    .HandleAsync(eventId, CancellationToken.None);
            }

            var database = connection.GetDatabase();
            var eventListTtl = await database.KeyTimeToLiveAsync(EventListCacheKeyV2);
            eventListTtl.Should().NotBeNull();
            eventListTtl!.Value.Should().BePositive().And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
            var ticketTypesTtl = await database.KeyTimeToLiveAsync(BuildTicketTypesCacheKey(eventId));
            ticketTypesTtl.Should().NotBeNull();
            ticketTypesTtl!.Value.Should().BePositive().And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(20));
        }
        finally
        {
            await redis.DisposeAsync();
        }
    }

    // QC-TTL-003（票種，event-sales-window tasks.md 5.5）：既有測試只涵蓋活動列表。
    [Fact]
    public async Task GetTicketTypesHandler_BeforeTtlExpires_StaysHitAndReturnsStaleContent()
    {
        var redis = new RedisFixture();
        await redis.InitializeAsync();
        try
        {
            var options = new QueryCacheOptions { EventListTtlSeconds = 60, TicketTypesTtlSeconds = 10 };
            var connection = redis.CreateConnection();
            var queryCache = new RedisQueryCache(connection, NullLogger<RedisQueryCache>.Instance);
            var (venueId, seatMapId) = await SeedVenueAndSeatMapAsync();
            var eventId = await SeedEventAsync(venueId, seatMapId);
            var existingTicketTypeId = await SeedTicketTypeAsync(eventId, $"Old-{Guid.NewGuid():N}");

            await using var rDbContext = _postgresFixture.CreateDbContext();
            var countingRepository = new CountingTicketTypeRepository(new TicketTypeRepository(rDbContext));
            var handler = new GetTicketTypesHandler(new EventRepository(rDbContext), countingRepository, queryCache, options);
            await handler.HandleAsync(eventId, CancellationToken.None);

            // 直接寫資料庫、不經 CreateTicketTypeHandler，因此不觸發失效。
            var newTicketTypeId = await SeedTicketTypeAsync(eventId, $"New-{Guid.NewGuid():N}");

            var secondResult = await handler.HandleAsync(eventId, CancellationToken.None);
            var thirdResult = await handler.HandleAsync(eventId, CancellationToken.None);

            foreach (var result in new[] { secondResult, thirdResult })
            {
                result.Value.Should().Contain(t => t.Id == existingTicketTypeId);
                result.Value.Should().NotContain(t => t.Id == newTicketTypeId, "TTL 內應持續命中快取，回傳異動前的舊內容");
            }
            countingRepository.GetByEventIdAsyncCallCount.Should().Be(1, "只有第一次未命中會查詢票種 Repository");
            (await connection.GetDatabase().KeyExistsAsync(BuildTicketTypesCacheKey(eventId))).Should().BeTrue();
        }
        finally
        {
            await redis.DisposeAsync();
        }
    }

    // QC-TTL-004 測試控制值（event-sales-window tasks.md 5.5）：非業務契約，只決定測試如何安排時間；
    // 依 tasks.md 失敗處理政策，實作者不得自行調整。
    private static readonly TimeSpan RaceTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RaceGap = TimeSpan.FromMilliseconds(2000);
    private static readonly TimeSpan ExistenceMargin = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ExistenceCheckBudget = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RedisMillisecondTruncation = TimeSpan.FromMilliseconds(1);

    // QC-TTL-004（活動列表，event-sales-window tasks.md 5.5）：既有競態測試只涵蓋票種。
    [Fact]
    public async Task GetEventsHandler_WhenInvalidationHappensWhileQueryInFlight_CacheTemporarilyResurrectsStaleValue_ButExpiresWithinTtl()
    {
        var redis = new RedisFixture();
        await redis.InitializeAsync();
        try
        {
            var options = new QueryCacheOptions { EventListTtlSeconds = (int)RaceTtl.TotalSeconds, TicketTypesTtlSeconds = 60 };
            var connection = redis.CreateConnection();
            var existingTitle = $"Old-{Guid.NewGuid():N}";
            var newTitle = $"New-{Guid.NewGuid():N}";
            var (venueId, seatMapId) = await SeedVenueAndSeatMapAsync();
            await SeedEventAsync(venueId, seatMapId, existingTitle);
            var memberId = await SeedMemberAsync();
            Guid organizerId;
            await using (var organizerDbContext = _postgresFixture.CreateDbContext())
            {
                organizerId = await OrganizerTestData.SeedApprovedOrganizerAsync(organizerDbContext);
            }

            var recordingQueryCache = new RecordingQueryCache(
                new RedisQueryCache(connection, NullLogger<RedisQueryCache>.Instance),
                connection,
                EventListCacheKeyV2,
                async cancellationToken =>
                {
                    await using var probeDbContext = _postgresFixture.CreateDbContext();
                    return await probeDbContext.Events.AsNoTracking().AnyAsync(e => e.Title == newTitle, cancellationToken);
                });

            var reachedSyncPoint = new TaskCompletionSource();
            var releaseSyncPoint = new TaskCompletionSource();
            await using var rDbContext = _postgresFixture.CreateDbContext();
            var handlerR = new GetEventsHandler(
                new SlowEventRepository(new EventRepository(rDbContext), reachedSyncPoint, releaseSyncPoint), recordingQueryCache, options);

            // (1) R 讀完舊資料後停在同步點。
            var taskR = handlerR.HandleAsync(CancellationToken.None);
            await reachedSyncPoint.Task;

            // (2) 寫入方提交並失效；以失效完成訊號確認，不以固定等待推測。
            await using (var wDbContext = _postgresFixture.CreateDbContext())
            {
                var createEventHandler = new CreateEventHandler(
                    new VenueRepository(wDbContext),
                    new SeatMapRepository(wDbContext),
                    new EventRepository(wDbContext),
                    new EventSeatRepository(wDbContext),
                    new UnitOfWork(wDbContext),
                    new CreateEventRequestValidator(),
                    new SystemDateTimeProvider(),
                    recordingQueryCache);
                var result = await createEventHandler.HandleAsync(
                    memberId, organizerId, new CreateEventRequest(newTitle, DateTime.UtcNow.AddDays(30), venueId, seatMapId), CancellationToken.None);
                result.IsSuccess.Should().BeTrue();
            }
            await recordingQueryCache.RemoveCompletedSignal.Task;

            // (3) 間隔 G 後釋放 R，讓它把舊資料寫入快取。
            await Task.Delay(RaceGap);
            releaseSyncPoint.SetResult();
            var rResult = await taskR;
            rResult.Should().Contain(e => e.Title == existingTitle);
            rResult.Should().NotContain(e => e.Title == newTitle, "R 讀到的必須是尚未包含新活動的舊資料");

            await AssertStaleWriteResurrectedThenExpiresWithinTtlAsync(
                recordingQueryCache,
                connection,
                EventListCacheKeyV2,
                async () =>
                {
                    await using var queryDbContext = _postgresFixture.CreateDbContext();
                    var events = await new GetEventsHandler(new EventRepository(queryDbContext), recordingQueryCache, options)
                        .HandleAsync(CancellationToken.None);
                    return (events.Any(e => e.Title == existingTitle), events.Any(e => e.Title == newTitle));
                });
        }
        finally
        {
            await redis.DisposeAsync();
        }
    }

    // QC-TTL-004（票種，tasks.md 6.2e；event-sales-window tasks.md 5.5 改寫）：以 TaskCompletionSource 決定性地構造
    // 「查詢途中發生異動」的競態，並以 RecordingQueryCache 的事件紀錄證明先後與完整 TTL。
    [Fact]
    public async Task GetTicketTypesHandler_WhenInvalidationHappensWhileQueryInFlight_CacheTemporarilyResurrectsStaleValue_ButExpiresWithinTtl()
    {
        var redis = new RedisFixture();
        await redis.InitializeAsync();
        try
        {
            var options = new QueryCacheOptions { EventListTtlSeconds = 60, TicketTypesTtlSeconds = (int)RaceTtl.TotalSeconds };
            var connection = redis.CreateConnection();
            var (venueId, seatMapId) = await SeedVenueAndSeatMapAsync();
            var eventId = await SeedEventAsync(venueId, seatMapId);
            var targetKey = $"query-cache:ticket-types:event:{eventId}";
            // 既有票種是 R 的唯一 payload：活動沒有票種時 R 讀到空清單，無法與其他寫入區分。
            var existingZoneCode = $"Old-{Guid.NewGuid():N}";
            var newZoneCode = $"New-{Guid.NewGuid():N}";
            await SeedTicketTypeAsync(eventId, existingZoneCode);

            var recordingQueryCache = new RecordingQueryCache(
                new RedisQueryCache(connection, NullLogger<RedisQueryCache>.Instance),
                connection,
                targetKey,
                async cancellationToken =>
                {
                    await using var probeDbContext = _postgresFixture.CreateDbContext();
                    return await probeDbContext.TicketTypes.AsNoTracking().AnyAsync(t => t.ZoneCode == newZoneCode, cancellationToken);
                });

            var reachedSyncPoint = new TaskCompletionSource();
            var releaseSyncPoint = new TaskCompletionSource();
            await using var rDbContext = _postgresFixture.CreateDbContext();
            var slowTicketTypeRepository = new SlowTicketTypeRepository(new TicketTypeRepository(rDbContext), reachedSyncPoint, releaseSyncPoint);
            var handlerR = new GetTicketTypesHandler(new EventRepository(rDbContext), slowTicketTypeRepository, recordingQueryCache, options);

            // (1) R 讀完舊資料後停在同步點。
            var taskR = handlerR.HandleAsync(eventId, CancellationToken.None);
            await reachedSyncPoint.Task;

            // (2) 寫入方提交並失效；以失效完成訊號確認，不以固定等待推測。
            await using (var writeDbContext = _postgresFixture.CreateDbContext())
            {
                var createHandler = new CreateTicketTypeHandler(
                    new EventRepository(writeDbContext), new SeatMapRepository(writeDbContext), new TicketTypeRepository(writeDbContext),
                    new UnitOfWork(writeDbContext), new CreateTicketTypeRequestValidator(), recordingQueryCache);
                var organizerId = (await writeDbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId)).OrganizerId;
                var result = await createHandler.HandleAsync(
                    eventId, organizerId, new CreateTicketTypeRequest(newZoneCode, 500m, RequiresSeat: false, AvailableQuantity: 20), CancellationToken.None);
                result.IsSuccess.Should().BeTrue();
            }
            await recordingQueryCache.RemoveCompletedSignal.Task;

            // (3) 間隔 G 後釋放 R，讓它把舊資料寫入快取。
            await Task.Delay(RaceGap);
            releaseSyncPoint.SetResult();
            var rResult = await taskR;
            rResult.Value.Should().Contain(t => t.ZoneCode == existingZoneCode);
            rResult.Value.Should().NotContain(t => t.ZoneCode == newZoneCode, "R 讀到的必須是尚未包含新票種的舊資料");

            await AssertStaleWriteResurrectedThenExpiresWithinTtlAsync(
                recordingQueryCache,
                connection,
                targetKey,
                async () =>
                {
                    await using var queryDbContext = _postgresFixture.CreateDbContext();
                    var result = await new GetTicketTypesHandler(
                            new EventRepository(queryDbContext), new TicketTypeRepository(queryDbContext), recordingQueryCache, options)
                        .HandleAsync(eventId, CancellationToken.None);
                    return (result.Value!.Any(t => t.ZoneCode == existingZoneCode), result.Value!.Any(t => t.ZoneCode == newZoneCode));
                });
        }
        finally
        {
            await redis.DisposeAsync();
        }
    }

    /// <summary>
    /// QC-TTL-004 步驟 (4)～(7)（event-sales-window tasks.md 5.5、design.md 決策 6）。
    /// <paramref name="queryAsync"/> 回傳查詢結果是否含舊資料／新資料。
    /// </summary>
    private static async Task AssertStaleWriteResurrectedThenExpiresWithinTtlAsync(
        RecordingQueryCache recordingQueryCache,
        IConnectionMultiplexer connection,
        string targetKey,
        Func<Task<(bool ContainsOld, bool ContainsNew)>> queryAsync)
    {
        var database = connection.GetDatabase();

        // (4) 事件順序。
        var events = recordingQueryCache.Events;
        var eventLog = DescribeEvents(events);
        events.Should().NotContain(e => e.EventType == QueryCacheEventType.SetNotCommittedToRedis, "寫入未確認落在 Redis。事件紀錄：\n{0}", eventLog);
        events.Should().NotContain(e => e.EventType == QueryCacheEventType.CommitNotObserved, "失效時寫入方交易尚未提交。事件紀錄：\n{0}", eventLog);

        var commitObserved = events.Single(e => e.Key == targetKey && e.EventType == QueryCacheEventType.CommitObserved);
        var removeCompleted = events.First(e => e.Key == targetKey && e.EventType == QueryCacheEventType.RemoveCompleted);
        var rSetStarted = events.First(e => e.Key == targetKey && e.EventType == QueryCacheEventType.SetStarted && e.Sequence > removeCompleted.Sequence);
        var rSetCompleted = events.Single(e => e.EventType == QueryCacheEventType.SetCompleted && e.OperationId == rSetStarted.OperationId);
        var rSetSucceeded = events.Single(e => e.EventType == QueryCacheEventType.SetSucceeded && e.OperationId == rSetStarted.OperationId);

        events.Count(e => e.Key == targetKey && e.EventType == QueryCacheEventType.RemoveCompleted && e.Sequence < rSetStarted.Sequence)
            .Should().Be(1, "R 寫入前受測 key 恰好失效一次。事件紀錄：\n{0}", eventLog);
        new[] { commitObserved.Sequence, removeCompleted.Sequence, rSetStarted.Sequence, rSetCompleted.Sequence, rSetSucceeded.Sequence }
            .Should().BeInAscendingOrder("順序須為 CommitObserved < RemoveCompleted < SetStarted < SetCompleted < SetSucceeded。事件紀錄：\n{0}", eventLog);

        // (5) PTTL 閉區間。
        var ws = rSetStarted.Timestamp;
        var we = rSetCompleted.Timestamp;
        var rs = Stopwatch.GetTimestamp();
        var pttl = await database.KeyTimeToLiveAsync(targetKey);
        var re = Stopwatch.GetTimestamp();
        var timing = new RaceTiming(ws, we, rs, re, eventLog);
        if (!(ws <= we && we <= rs && rs <= re))
        {
            throw new QueryCacheTimingInconclusiveException($"結果不可判定：檢查落在窗口外（前提 ws ≤ we ≤ rs ≤ re 不成立）。{timing.Describe()}");
        }

        var lowerBound = RaceTtl - Stopwatch.GetElapsedTime(ws, re) - RedisMillisecondTruncation;
        var upperBound = RaceTtl - Stopwatch.GetElapsedTime(we, rs) + RedisMillisecondTruncation;
        timing.Pttl = pttl;
        timing.PttlBounds = (lowerBound, upperBound);
        pttl.Should().NotBeNull("R 寫入的 key 應存在且帶 TTL。{0}", timing.Describe());
        pttl!.Value.Should().BeGreaterThanOrEqualTo(lowerBound, "TTL 必須自這次寫入起完整套用。{0}", timing.Describe())
            .And.BeLessThanOrEqualTo(upperBound, "TTL 不得被延長或套用更長的值。{0}", timing.Describe());

        // (6) 到期前存在性檢查。
        var existenceDeadline = ws + ToStopwatchTicks(RaceTtl - ExistenceMargin);
        await WaitUntilStopwatchTimestampAsync(existenceDeadline - ToStopwatchTicks(ExistenceCheckBudget));
        timing.C6s = Stopwatch.GetTimestamp();
        var existsBeforeExpiry = await database.KeyExistsAsync(targetKey);
        var beforeExpiryResult = await queryAsync();
        timing.C6e = Stopwatch.GetTimestamp();
        if (timing.C6e > existenceDeadline)
        {
            throw new QueryCacheTimingInconclusiveException(
                $"結果不可判定：檢查落在窗口外（(6) c6e > ws + T − M1 = {timing.ToMilliseconds(existenceDeadline)}ms）。{timing.Describe()}");
        }

        existsBeforeExpiry.Should().BeTrue("到期前 key 應仍存在。{0}", timing.Describe());
        beforeExpiryResult.ContainsOld.Should().BeTrue("到期前應讀到 R 寫入的舊資料。{0}", timing.Describe());
        beforeExpiryResult.ContainsNew.Should().BeFalse("到期前應讀到 R 寫入的舊資料（短暫復活的陳舊快取）。{0}", timing.Describe());

        var setOperationIdsUntilC6e = recordingQueryCache.Events
            .Where(e => e.Key == targetKey && e.Sequence > removeCompleted.Sequence && e.Timestamp <= timing.C6e && e.OperationId is not null)
            .Select(e => e.OperationId)
            .Distinct()
            .ToList();
        setOperationIdsUntilC6e.Should().Equal(new Guid?[] { rSetStarted.OperationId }, "失效後到 (6) 結束為止只能有 R 的那一次寫入。{0}", timing.Describe());

        // (7) 到期後過期檢查。
        var expiryWindowStart = we + ToStopwatchTicks(RaceTtl + ExpiryMargin);
        await WaitUntilStopwatchTimestampAsync(expiryWindowStart);
        timing.C7s = Stopwatch.GetTimestamp();
        if (timing.C7s < expiryWindowStart)
        {
            throw new QueryCacheTimingInconclusiveException(
                $"結果不可判定：檢查落在窗口外（(7) c7s < we + T + M2 = {timing.ToMilliseconds(expiryWindowStart)}ms）。{timing.Describe()}");
        }

        (await database.KeyExistsAsync(targetKey)).Should().BeFalse("陳舊快取應在 TTL 到期後自然過期。{0}", timing.Describe());
        var afterExpiryResult = await queryAsync();
        afterExpiryResult.ContainsNew.Should().BeTrue("過期後應重新查詢資料庫並看到新資料。{0}", timing.Describe());
    }

    private static long ToStopwatchTicks(TimeSpan duration) => (long)(duration.TotalSeconds * Stopwatch.Frequency);

    private static async Task WaitUntilStopwatchTimestampAsync(long targetTimestamp)
    {
        while (Stopwatch.GetTimestamp() < targetTimestamp)
        {
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), targetTimestamp);
            await Task.Delay(remaining > TimeSpan.FromMilliseconds(1) ? remaining : TimeSpan.FromMilliseconds(1));
        }
    }

    private static string DescribeEvents(IReadOnlyList<QueryCacheEvent> events)
    {
        var origin = events.Count > 0 ? events[0].Timestamp : 0;
        return string.Join("\n", events.Select(e =>
            $"#{e.Sequence} {e.EventType} +{Stopwatch.GetElapsedTime(origin, e.Timestamp).TotalMilliseconds:F1}ms key={e.Key} op={e.OperationId} {e.Detail}"));
    }

    /// <summary>失敗訊息附上 QC-TTL-004 失敗處理政策要求記錄的時刻與區間，時刻以 ws 為原點換算成毫秒。</summary>
    private sealed class RaceTiming
    {
        private readonly long _ws;
        private readonly long _we;
        private readonly long _rs;
        private readonly long _re;
        private readonly string _eventLog;

        public RaceTiming(long ws, long we, long rs, long re, string eventLog)
        {
            _ws = ws;
            _we = we;
            _rs = rs;
            _re = re;
            _eventLog = eventLog;
        }

        public TimeSpan? Pttl { get; set; }
        public (TimeSpan Lower, TimeSpan Upper)? PttlBounds { get; set; }
        public long? C6s { get; set; }
        public long? C6e { get; set; }
        public long? C7s { get; set; }

        public string ToMilliseconds(long timestamp) => Stopwatch.GetElapsedTime(_ws, timestamp).TotalMilliseconds.ToString("F1");

        private string ToMilliseconds(long? timestamp) => timestamp is null ? "n/a" : ToMilliseconds(timestamp.Value);

        public string Describe() =>
            $"\nws=0ms we={ToMilliseconds(_we)}ms rs={ToMilliseconds(_rs)}ms re={ToMilliseconds(_re)}ms " +
            $"PTTL={Pttl?.TotalMilliseconds.ToString() ?? "n/a"}ms bounds=[{PttlBounds?.Lower.TotalMilliseconds}, {PttlBounds?.Upper.TotalMilliseconds}]ms " +
            $"c6s={ToMilliseconds(C6s)}ms c6e={ToMilliseconds(C6e)}ms c7s={ToMilliseconds(C7s)}ms " +
            $"T={RaceTtl.TotalMilliseconds}ms G={RaceGap.TotalMilliseconds}ms M1={ExistenceMargin.TotalMilliseconds}ms B={ExistenceCheckBudget.TotalMilliseconds}ms M2={ExpiryMargin.TotalMilliseconds}ms" +
            $"\n事件紀錄：\n{_eventLog}";
    }

    private async Task<Guid> SeedMemberAsync()
    {
        await using var dbContext = _postgresFixture.CreateDbContext();
        var member = Member.Register($"{Guid.NewGuid():N}@example.com", "Admin", "hash");
        dbContext.Members.Add(member);
        await dbContext.SaveChangesAsync();
        return member.Id;
    }

    private async Task<Guid> SeedTicketTypeAsync(Guid eventId, string zoneCode)
    {
        await using var dbContext = _postgresFixture.CreateDbContext();
        var @event = await dbContext.Events.FindAsync(eventId);
        var ticketType = @event!.CreateCountBasedTicketType(zoneCode, 300m, 10);
        dbContext.TicketTypes.Add(ticketType);
        await dbContext.SaveChangesAsync();
        return ticketType.Id;
    }

    private sealed class SlowEventRepository : IEventRepository
    {
        private readonly IEventRepository _inner;
        private readonly TaskCompletionSource _reachedSyncPoint;
        private readonly TaskCompletionSource _releaseSyncPoint;

        public SlowEventRepository(IEventRepository inner, TaskCompletionSource reachedSyncPoint, TaskCompletionSource releaseSyncPoint)
        {
            _inner = inner;
            _reachedSyncPoint = reachedSyncPoint;
            _releaseSyncPoint = releaseSyncPoint;
        }

        public async Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken)
        {
            var result = await _inner.GetAllAsync(cancellationToken);
            _reachedSyncPoint.SetResult();
            await _releaseSyncPoint.Task;
            return result;
        }

        public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => _inner.GetByIdAsync(id, cancellationToken);
        public Task<IReadOnlyList<Event>> GetByIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken) => _inner.GetByIdsAsync(eventIds, cancellationToken);
        public Task<IReadOnlyList<Event>> GetByOrganizerIdAsync(Guid organizerId, CancellationToken cancellationToken) => _inner.GetByOrganizerIdAsync(organizerId, cancellationToken);
        public void Add(Event @event) => _inner.Add(@event);
        public void Update(Event @event) => _inner.Update(@event);
        public Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken) => _inner.GetForUpdateAsync(eventId, cancellationToken);
    }

    private sealed class CountingTicketTypeRepository : ITicketTypeRepository
    {
        private readonly ITicketTypeRepository _inner;

        public CountingTicketTypeRepository(ITicketTypeRepository inner)
        {
            _inner = inner;
        }

        public int GetByEventIdAsyncCallCount { get; private set; }

        public Task<IReadOnlyList<TicketType>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
        {
            GetByEventIdAsyncCallCount++;
            return _inner.GetByEventIdAsync(eventId, cancellationToken);
        }

        public Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => _inner.GetByIdAsync(id, cancellationToken);
        public void Add(TicketType ticketType) => _inner.Add(ticketType);
        public Task<IReadOnlyList<TicketType>> GetForUpdateAsync(IReadOnlyList<Guid> ticketTypeIds, CancellationToken cancellationToken)
            => _inner.GetForUpdateAsync(ticketTypeIds, cancellationToken);
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
