using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;
using StackExchange.Redis;

namespace ProjectC.WebApi.Tests.Events;

// query-caching tasks.md 2.6／3.7：HTTP 層補充，真實 Redis／真實資料庫，驗證快取命中時
// Repository 未被重複查詢。每個測試方法各自建立獨立的 factory（獨立的 Postgres／Redis 容器），
// 不透過 IClassFixture 共用——固定的快取 key（如 query-cache:events:list）若跨測試方法共用同一個
// Redis，會被其他測試方法的殘留快取內容污染（見 CustomWebApplicationFactory 的 Redis 隔離註解）。
public class QueryCachingComponentTests
{
    // 以字面值鎖定 v2 契約，不引用 GetEventsHandler.CacheKey：常數被改回舊值時測試必須失敗（event-sales-window tasks 5.4／5.5）。
    private const string EventListCacheKeyV2 = "query-cache:events:list:v2";
    private const string LegacyEventListCacheKey = "query-cache:events:list";

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    // 回傳建立活動的同一個 client：建立票種會核對活動是否屬於呼叫端目前 Organizer（EVT-TICKET-004），
    // 換成另一個 client 會被視同活動不存在。
    private static async Task<(Guid EventId, Guid VenueId, HttpClient AdminClient)> SeedEventAsync(
        CachingComponentTestWebApplicationFactory factory, string zoneCode = "A", DateTime? salesStartAtUtc = null, DateTime? salesEndAtUtc = null)
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(factory);

        var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Test Venue"));
        var venueId = await ReadCreatedIdAsync(venueResponse);

        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps",
            new CreateSeatMapRequest([new SeatRequest(zoneCode, "1")]));
        var seatMapId = await ReadCreatedIdAsync(seatMapResponse);

        var eventResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest("Concert", DateTime.UtcNow.Date.AddDays(30), venueId, seatMapId, SalesStartAtUtc: salesStartAtUtc, SalesEndAtUtc: salesEndAtUtc));
        var eventId = await ReadCreatedIdAsync(eventResponse);

        return (eventId, venueId, adminClient);
    }

    // QC-EVT-001／002（tasks.md 2.6）。
    [Fact]
    public async Task GetEvents_CalledTwice_SecondCallHitsCacheAndDoesNotQueryDatabaseAgain()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var (eventId, _, _) = await SeedEventAsync(factory);
            var client = factory.CreateClient();

            var firstResponse = await client.GetAsync("/api/events");
            firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var firstEvents = await firstResponse.Content.ReadFromJsonAsync<List<EventDto>>();
            firstEvents.Should().Contain(e => e.Id == eventId);
            (await GetRedisDatabase(factory).KeyExistsAsync(EventListCacheKeyV2)).Should().BeTrue();

            var secondResponse = await client.GetAsync("/api/events");
            secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var secondEvents = await secondResponse.Content.ReadFromJsonAsync<List<EventDto>>();

            secondEvents.Should().BeEquivalentTo(firstEvents, options => options.WithStrictOrdering());
            factory.EventRepositoryCallCounter.GetAllAsyncCallCount.Should().Be(1, "第二次呼叫應該命中快取，不再查詢資料庫");
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    // QC-TT-001／002（tasks.md 3.7）。
    [Fact]
    public async Task GetTicketTypes_CalledTwiceForSameEvent_SecondCallHitsCacheAndDoesNotQueryDatabaseAgain()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var (eventId, _, adminClient) = await SeedEventAsync(factory, zoneCode: "A");
            await adminClient.PostAsJsonAsync($"/api/admin/events/{eventId}/ticket-types", new CreateTicketTypeRequest("A", 500m));
            var client = factory.CreateClient();

            var firstResponse = await client.GetAsync($"/api/events/{eventId}/ticket-types");
            firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var firstTicketTypes = await firstResponse.Content.ReadFromJsonAsync<List<TicketTypeDto>>();
            firstTicketTypes.Should().ContainSingle(t => t.ZoneCode == "A" && t.Price == 500m);

            var secondResponse = await client.GetAsync($"/api/events/{eventId}/ticket-types");
            secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var secondTicketTypes = await secondResponse.Content.ReadFromJsonAsync<List<TicketTypeDto>>();

            secondTicketTypes.Should().BeEquivalentTo(firstTicketTypes, options => options.WithStrictOrdering());
            factory.TicketTypeRepositoryCallCounter.GetByEventIdAsyncCallCount.Should().Be(1, "第二次呼叫應該命中快取，不再查詢資料庫");
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    private static IDatabase GetRedisDatabase(CachingComponentTestWebApplicationFactory factory)
        => factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    private static Task<bool> EventListCacheKeyExistsAsync(CachingComponentTestWebApplicationFactory factory)
        => GetRedisDatabase(factory).KeyExistsAsync(EventListCacheKeyV2);

    private static async Task<bool> ReadIsQueueModeEnabledFromDatabaseAsync(CachingComponentTestWebApplicationFactory factory, Guid eventId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (await dbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId)).IsQueueModeEnabled;
    }

    // QC-EVT-INV-002（purchase-queue-organizer-scoping tasks.md 4.14a）：經 HTTP 授權與歸屬核對的合法切換會清除活動列表快取。
    [Fact]
    public async Task SetQueueMode_ByOwningOrganizer_InvalidatesEventListCache()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var (eventId, _, ownerClient) = await SeedEventAsync(factory);
            var anonymousClient = factory.CreateClient();
            var cachedEvents = await anonymousClient.GetFromJsonAsync<List<EventDto>>("/api/events");
            cachedEvents!.Single(e => e.Id == eventId).IsQueueModeEnabled.Should().BeFalse();
            (await EventListCacheKeyExistsAsync(factory)).Should().BeTrue();

            var response = await ownerClient.PatchAsJsonAsync($"/api/admin/events/{eventId}/queue-mode", new { enabled = true });

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await EventListCacheKeyExistsAsync(factory)).Should().BeFalse();
            var refreshedEvents = await anonymousClient.GetFromJsonAsync<List<EventDto>>("/api/events");
            refreshedEvents!.Single(e => e.Id == eventId).IsQueueModeEnabled.Should().BeTrue();
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    // QC-EVT-INV-002（tasks.md 4.14b）：跨 Organizer 的 404 早退不得清除快取，也不得變更 DB。
    [Fact]
    public async Task SetQueueMode_ByOtherOrganizer_ReturnsNotFoundAndDoesNotInvalidateCache()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var (eventId, _, _) = await SeedEventAsync(factory);
            var anonymousClient = factory.CreateClient();
            var cachedEvents = await anonymousClient.GetFromJsonAsync<List<EventDto>>("/api/events");
            cachedEvents!.Single(e => e.Id == eventId).IsQueueModeEnabled.Should().BeFalse();
            (await EventListCacheKeyExistsAsync(factory)).Should().BeTrue();
            var (otherOrganizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(factory);

            var response = await otherOrganizerClient.PatchAsJsonAsync($"/api/admin/events/{eventId}/queue-mode", new { enabled = true });

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await EventListCacheKeyExistsAsync(factory)).Should().BeTrue("不一致時不 commit，也不清除快取");
            (await ReadIsQueueModeEnabledFromDatabaseAsync(factory, eventId)).Should().BeFalse();
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    // ---- 活動列表快取 key 版本化（event-sales-window QC-EVT-VER-*）----

    // QC-EVT-VER-001：滾動部署期間舊版本寫入的舊形狀內容不含販售期間，新版本若讀到會把有販售期間的活動誤顯示為無限制。
    [Fact]
    public async Task GetEvents_WhenOnlyLegacyKeyExists_IgnoresItAndWritesV2()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var salesStartAtUtc = DateTime.UtcNow.Date.AddDays(1);
            var salesEndAtUtc = DateTime.UtcNow.Date.AddDays(20);
            var (eventId, venueId, _) = await SeedEventAsync(factory, salesStartAtUtc: salesStartAtUtc, salesEndAtUtc: salesEndAtUtc);
            var redis = GetRedisDatabase(factory);
            var legacyValue = JsonSerializer.Serialize(new[]
            {
                new { Id = eventId, Title = "Concert", StartAtUtc = DateTime.UtcNow.AddDays(30), VenueId = venueId, SeatMapId = Guid.NewGuid(),
                    Description = (string?)null, PosterUrl = (string?)null, MaxTicketsPerOrder = (int?)null, IsQueueModeEnabled = false, IsRealNameRequired = false },
            });
            await redis.StringSetAsync(LegacyEventListCacheKey, legacyValue);
            (await redis.KeyExistsAsync(EventListCacheKeyV2)).Should().BeFalse();

            var response = await factory.CreateClient().GetAsync("/api/events");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var events = await response.Content.ReadFromJsonAsync<List<EventDto>>();
            var listedEvent = events!.Single(e => e.Id == eventId);
            (listedEvent.SalesStartAtUtc, listedEvent.SalesEndAtUtc).Should().Be((salesStartAtUtc, salesEndAtUtc));
            (await redis.KeyExistsAsync(EventListCacheKeyV2)).Should().BeTrue();
            ((string?)await redis.StringGetAsync(LegacyEventListCacheKey)).Should().Be(legacyValue);
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    // QC-EVT-VER-004：v2 讀取時不檢查形狀，正確性依賴「寫入一定是完整形狀」；序列化設定若改成省略 null 屬性，本測試失敗。
    [Fact]
    public async Task GetEvents_OnCacheMiss_WritesV2WithBothSalesWindowPropertiesEvenWhenNull()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var salesStartAtUtc = DateTime.UtcNow.Date.AddDays(1);
            var salesEndAtUtc = DateTime.UtcNow.Date.AddDays(20);
            var (eventXId, _, _) = await SeedEventAsync(factory, salesStartAtUtc: salesStartAtUtc, salesEndAtUtc: salesEndAtUtc);
            var (eventYId, _, _) = await SeedEventAsync(factory, salesStartAtUtc: salesStartAtUtc, salesEndAtUtc: salesEndAtUtc);
            using (var scope = factory.Services.CreateScope())
            {
                // 模擬遷移前的舊活動（兩欄位 NULL）；EF ExecuteUpdate 是參數化 UPDATE，不經過 Domain 建構子。
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await dbContext.Events.Where(e => e.Id == eventYId).ExecuteUpdateAsync(setters => setters
                    .SetProperty(e => e.SalesStartAtUtc, (DateTime?)null)
                    .SetProperty(e => e.SalesEndAtUtc, (DateTime?)null));
            }
            var redis = GetRedisDatabase(factory);
            await redis.KeyDeleteAsync(EventListCacheKeyV2);

            var response = await factory.CreateClient().GetAsync("/api/events");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var rawValue = (string?)await redis.StringGetAsync(EventListCacheKeyV2);
            rawValue.Should().NotBeNull();
            using var document = JsonDocument.Parse(rawValue!);
            var elements = document.RootElement.EnumerateArray().ToList();
            var eventX = elements.Single(e => e.GetProperty("Id").GetGuid() == eventXId);
            var eventY = elements.Single(e => e.GetProperty("Id").GetGuid() == eventYId);
            foreach (var element in new[] { eventX, eventY })
            {
                element.TryGetProperty("SalesStartAtUtc", out _).Should().BeTrue();
                element.TryGetProperty("SalesEndAtUtc", out _).Should().BeTrue();
            }
            eventX.GetProperty("SalesStartAtUtc").GetDateTime().Should().Be(salesStartAtUtc);
            eventX.GetProperty("SalesEndAtUtc").GetDateTime().Should().Be(salesEndAtUtc);
            eventY.GetProperty("SalesStartAtUtc").ValueKind.Should().Be(JsonValueKind.Null);
            eventY.GetProperty("SalesEndAtUtc").ValueKind.Should().Be(JsonValueKind.Null);
            (await redis.KeyExistsAsync(LegacyEventListCacheKey)).Should().BeFalse();
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    // QC-EVT-VER-002：新版本只清除 v2；舊 key 屬於仍在執行的舊版本，由舊版本自行失效或 TTL 到期。
    [Fact]
    public async Task CreateEvent_WhenBothV1AndV2KeysExist_RemovesOnlyV2Key()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var redis = GetRedisDatabase(factory);
            var legacyValue = $"legacy-{Guid.NewGuid():N}";
            await redis.StringSetAsync(LegacyEventListCacheKey, legacyValue);
            await redis.StringSetAsync(EventListCacheKeyV2, $"v2-{Guid.NewGuid():N}");

            await SeedEventAsync(factory);

            (await redis.KeyExistsAsync(EventListCacheKeyV2)).Should().BeFalse();
            ((string?)await redis.StringGetAsync(LegacyEventListCacheKey)).Should().Be(legacyValue);
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    // QC-EVT-VER-002
    [Fact]
    public async Task SetEventQueueMode_WhenBothV1AndV2KeysExist_RemovesOnlyV2Key()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var (eventId, _, ownerClient) = await SeedEventAsync(factory);
            var redis = GetRedisDatabase(factory);
            var legacyValue = $"legacy-{Guid.NewGuid():N}";
            await redis.StringSetAsync(LegacyEventListCacheKey, legacyValue);
            await redis.StringSetAsync(EventListCacheKeyV2, $"v2-{Guid.NewGuid():N}");
            (await redis.KeyExistsAsync(LegacyEventListCacheKey)).Should().BeTrue();
            (await redis.KeyExistsAsync(EventListCacheKeyV2)).Should().BeTrue();

            var response = await ownerClient.PatchAsJsonAsync($"/api/admin/events/{eventId}/queue-mode", new { enabled = true });

            response.IsSuccessStatusCode.Should().BeTrue();
            (await redis.KeyExistsAsync(EventListCacheKeyV2)).Should().BeFalse();
            (await redis.KeyExistsAsync(LegacyEventListCacheKey)).Should().BeTrue();
            ((string?)await redis.StringGetAsync(LegacyEventListCacheKey)).Should().Be(legacyValue);
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    // QC-EVT-VER-003：新版本在任何讀取、寫入、失效路徑上都不得碰觸舊 key，否則回滾後舊版本會讀到新形狀或被意外清除。
    [Fact]
    public async Task NewVersion_SequenceNeverReadsOrWritesLegacyEventListKey()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var redis = GetRedisDatabase(factory);
            var anonymousClient = factory.CreateClient();
            async Task AssertLegacyKeyAbsentAsync(string step)
                => (await redis.KeyExistsAsync(LegacyEventListCacheKey)).Should().BeFalse($"步驟「{step}」之後舊 key 不得存在");

            await AssertLegacyKeyAbsentAsync("初始");
            (await anonymousClient.GetAsync("/api/events")).EnsureSuccessStatusCode();
            await AssertLegacyKeyAbsentAsync("GET 未命中");
            (await anonymousClient.GetAsync("/api/events")).EnsureSuccessStatusCode();
            await AssertLegacyKeyAbsentAsync("GET 命中");
            var (eventId, _, ownerClient) = await SeedEventAsync(factory);
            await AssertLegacyKeyAbsentAsync("建立活動");
            (await ownerClient.PatchAsJsonAsync($"/api/admin/events/{eventId}/queue-mode", new { enabled = true })).EnsureSuccessStatusCode();
            await AssertLegacyKeyAbsentAsync("開啟熱門搶購模式");
            (await ownerClient.PatchAsJsonAsync($"/api/admin/events/{eventId}/queue-mode", new { enabled = false })).EnsureSuccessStatusCode();
            await AssertLegacyKeyAbsentAsync("關閉熱門搶購模式");
            (await anonymousClient.GetAsync("/api/events")).EnsureSuccessStatusCode();
            await AssertLegacyKeyAbsentAsync("再次 GET");
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }
}
