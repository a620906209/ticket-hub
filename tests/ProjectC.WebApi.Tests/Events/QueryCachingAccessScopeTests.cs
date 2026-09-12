using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Events;

// query-caching tasks.md 第 8 節：存取範圍未變更（本次改動未新增授權檢查），且快取內容在
// 不同身份的呼叫者之間安全共享（同一份快取，不依身份切分）。
public class QueryCachingAccessScopeTests
{
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
            var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(factory);
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
            var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(factory);
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
            var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(factory);
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
            var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(factory);
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

            var response = await client.GetAsync("/api/events/abc/ticket-types");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }
}
