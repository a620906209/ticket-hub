using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.WebApi.Tests.TestSupport;
using StackExchange.Redis;

namespace ProjectC.WebApi.Tests.Events;

// query-caching tasks.md 第 8 節：存取範圍未變更（本次改動未新增授權檢查），且快取內容在
// 不同身份的呼叫者之間安全共享（同一份快取，不依身份切分）。
public class QueryCachingAccessScopeTests
{
    // 以字面值斷言而非引用正式常數：常數改值後，引用常數的測試無法證明契約是 v2（event-sales-window tasks.md 5.5）。
    private const string EventListCacheKeyV2 = "query-cache:events:list:v2";
    private const string LegacyEventListCacheKey = "query-cache:events:list";
    private const string TicketTypesCacheKeyPattern = "query-cache:ticket-types:event:*";

    private static IConnectionMultiplexer GetRedisConnection(CustomWebApplicationFactory factory)
        => factory.Services.GetRequiredService<IConnectionMultiplexer>();

    private static async Task<List<string>> ScanKeysAsync(CustomWebApplicationFactory factory, string pattern)
    {
        var connection = GetRedisConnection(factory);
        var server = connection.GetServer(connection.GetEndPoints().Single());
        var keys = new List<string>();
        await foreach (var key in server.KeysAsync(pattern: pattern))
        {
            keys.Add(key.ToString());
        }
        return keys;
    }

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    // QC-ACCESS-001（tasks.md 8.1）。
    [Fact]
    public async Task GetEvents_WithoutAuthentication_Returns200WithCreatedEvent()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(factory);
            var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Test Venue"));
            var venueId = await ReadCreatedIdAsync(venueResponse);
            var seatMapResponse = await adminClient.PostAsJsonAsync(
                $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")]));
            var seatMapId = await ReadCreatedIdAsync(seatMapResponse);
            var eventResponse = await adminClient.PostAsJsonAsync(
                "/api/admin/events", new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
            var eventId = await ReadCreatedIdAsync(eventResponse);

            var anonymousClient = factory.CreateClient();
            var eventsResponse = await anonymousClient.GetAsync("/api/events");

            eventsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var events = await eventsResponse.Content.ReadFromJsonAsync<List<EventDto>>();
            events.Should().ContainSingle(e => e.Id == eventId && e.Title == "Concert");
            (await GetRedisConnection(factory).GetDatabase().KeyExistsAsync(EventListCacheKeyV2)).Should().BeTrue("匿名請求同樣寫入 v2 key");
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    // QC-ACCESS-001（tasks.md 8.1）。
    [Fact]
    public async Task GetTicketTypes_WithoutAuthentication_Returns200WithCreatedTicketType()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(factory);
            var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Test Venue"));
            var venueId = await ReadCreatedIdAsync(venueResponse);
            var seatMapResponse = await adminClient.PostAsJsonAsync(
                $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")]));
            var seatMapId = await ReadCreatedIdAsync(seatMapResponse);
            var eventResponse = await adminClient.PostAsJsonAsync(
                "/api/admin/events", new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
            var eventId = await ReadCreatedIdAsync(eventResponse);
            await adminClient.PostAsJsonAsync($"/api/admin/events/{eventId}/ticket-types", new CreateTicketTypeRequest("A", 500m));

            var anonymousClient = factory.CreateClient();
            var ticketTypesResponse = await anonymousClient.GetAsync($"/api/events/{eventId}/ticket-types");

            ticketTypesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var ticketTypes = await ticketTypesResponse.Content.ReadFromJsonAsync<List<TicketTypeDto>>();
            ticketTypes.Should().ContainSingle(t => t.ZoneCode == "A" && t.Price == 500m);
            (await GetRedisConnection(factory).GetDatabase().KeyExistsAsync($"query-cache:ticket-types:event:{eventId}"))
                .Should().BeTrue("匿名請求同樣寫入票種快取");
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    private static async Task<HttpClient> CreateBuyerClientAsync(CachingComponentTestWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    // QC-ACCESS-002（tasks.md 8.2a）。
    [Fact]
    public async Task GetEvents_AcrossAnonymousBuyerAndAdminCallers_ShareTheSameCacheContent()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(factory);
            var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Test Venue"));
            var venueId = await ReadCreatedIdAsync(venueResponse);
            var seatMapResponse = await adminClient.PostAsJsonAsync(
                $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")]));
            var seatMapId = await ReadCreatedIdAsync(seatMapResponse);
            await adminClient.PostAsJsonAsync(
                "/api/admin/events", new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId));

            var anonymousClient = factory.CreateClient();
            var buyerClient = await CreateBuyerClientAsync(factory);

            var anonymousResponse = await anonymousClient.GetAsync("/api/events");
            var buyerResponse = await buyerClient.GetAsync("/api/events");
            var adminResponse = await adminClient.GetAsync("/api/events");

            var anonymousEvents = await anonymousResponse.Content.ReadFromJsonAsync<List<EventDto>>();
            var buyerEvents = await buyerResponse.Content.ReadFromJsonAsync<List<EventDto>>();
            var adminEvents = await adminResponse.Content.ReadFromJsonAsync<List<EventDto>>();

            buyerEvents.Should().BeEquivalentTo(anonymousEvents, options => options.WithStrictOrdering());
            adminEvents.Should().BeEquivalentTo(anonymousEvents, options => options.WithStrictOrdering());
            factory.EventRepositoryCallCounter.GetAllAsyncCallCount.Should().Be(1, "三種身份應共用同一份快取內容，只有第一次真正查詢資料庫");
            (await ScanKeysAsync(factory, "query-cache:events:list*")).Should().Equal(new[] { EventListCacheKeyV2 }, "活動列表只有 v2 一個 key，無依呼叫者區分的變體");
            (await GetRedisConnection(factory).GetDatabase().KeyExistsAsync(LegacyEventListCacheKey)).Should().BeFalse();
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    // QC-ACCESS-002（tasks.md 8.2b）：與 8.2a 是不同的 Controller Action 與快取 key，須獨立驗證。
    [Fact]
    public async Task GetTicketTypes_AcrossAnonymousBuyerAndAdminCallers_ShareTheSameCacheContent()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(factory);
            var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Test Venue"));
            var venueId = await ReadCreatedIdAsync(venueResponse);
            var seatMapResponse = await adminClient.PostAsJsonAsync(
                $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")]));
            var seatMapId = await ReadCreatedIdAsync(seatMapResponse);
            var eventResponse = await adminClient.PostAsJsonAsync(
                "/api/admin/events", new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
            var eventId = await ReadCreatedIdAsync(eventResponse);
            await adminClient.PostAsJsonAsync($"/api/admin/events/{eventId}/ticket-types", new CreateTicketTypeRequest("A", 500m));

            var anonymousClient = factory.CreateClient();
            var buyerClient = await CreateBuyerClientAsync(factory);

            var anonymousResponse = await anonymousClient.GetAsync($"/api/events/{eventId}/ticket-types");
            var buyerResponse = await buyerClient.GetAsync($"/api/events/{eventId}/ticket-types");
            var adminResponse = await adminClient.GetAsync($"/api/events/{eventId}/ticket-types");

            var anonymousTicketTypes = await anonymousResponse.Content.ReadFromJsonAsync<List<TicketTypeDto>>();
            var buyerTicketTypes = await buyerResponse.Content.ReadFromJsonAsync<List<TicketTypeDto>>();
            var adminTicketTypes = await adminResponse.Content.ReadFromJsonAsync<List<TicketTypeDto>>();

            buyerTicketTypes.Should().BeEquivalentTo(anonymousTicketTypes, options => options.WithStrictOrdering());
            adminTicketTypes.Should().BeEquivalentTo(anonymousTicketTypes, options => options.WithStrictOrdering());
            factory.TicketTypeRepositoryCallCounter.GetByEventIdAsyncCallCount.Should().Be(1, "三種身份應共用同一份快取內容，只有第一次真正查詢資料庫");
            (await ScanKeysAsync(factory, TicketTypesCacheKeyPattern))
                .Should().Equal(new[] { $"query-cache:ticket-types:event:{eventId}" }, "票種快取只有一個 key，無依呼叫者區分的變體");
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    // QC-ACCESS-003（tasks.md 8.3）：非 GUID 格式的 id 由路由層擋下，屬於框架保證。
    [Fact]
    public async Task GetTicketTypes_WithNonGuidIdInRoute_Returns404()
    {
        var factory = new CustomWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var client = factory.CreateClient();
            (await client.GetAsync("/api/events")).EnsureSuccessStatusCode();
            var database = GetRedisConnection(factory).GetDatabase();
            var eventListBefore = await database.StringGetAsync(EventListCacheKeyV2);
            eventListBefore.HasValue.Should().BeTrue("前置的活動列表請求應已寫入 v2 key");

            var response = await client.GetAsync("/api/events/abc/ticket-types");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ScanKeysAsync(factory, TicketTypesCacheKeyPattern)).Should().BeEmpty("請求未進入 GetTicketTypesHandler，不應寫入票種快取");
            ((string?)await database.StringGetAsync(EventListCacheKeyV2)).Should().Be((string?)eventListBefore, "路由拒絕不影響 v2 key");
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }
}
