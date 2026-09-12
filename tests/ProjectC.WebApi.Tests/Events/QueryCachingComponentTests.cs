using System.Net;
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

// query-caching tasks.md 2.6／3.7：HTTP 層補充，真實 Redis／真實資料庫，驗證快取命中時
// Repository 未被重複查詢。每個測試方法各自建立獨立的 factory（獨立的 Postgres／Redis 容器），
// 不透過 IClassFixture 共用——固定的快取 key（如 query-cache:events:list）若跨測試方法共用同一個
// Redis，會被其他測試方法的殘留快取內容污染（見 CustomWebApplicationFactory 的 Redis 隔離註解）。
public class QueryCachingComponentTests
{
    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    private static async Task<(Guid EventId, Guid VenueId)> SeedEventAsync(CachingComponentTestWebApplicationFactory factory, string zoneCode = "A")
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(factory);

        var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Test Venue"));
        var venueId = await ReadCreatedIdAsync(venueResponse);

        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps",
            new CreateSeatMapRequest([new SeatRequest(zoneCode, "1")]));
        var seatMapId = await ReadCreatedIdAsync(seatMapResponse);

        var eventResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
        var eventId = await ReadCreatedIdAsync(eventResponse);

        return (eventId, venueId);
    }

    // QC-EVT-001／002（tasks.md 2.6）。
    [Fact]
    public async Task GetEvents_CalledTwice_SecondCallHitsCacheAndDoesNotQueryDatabaseAgain()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var (eventId, _) = await SeedEventAsync(factory);
            var client = factory.CreateClient();

            var firstResponse = await client.GetAsync("/api/events");
            firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var firstEvents = await firstResponse.Content.ReadFromJsonAsync<List<EventDto>>();
            firstEvents.Should().Contain(e => e.Id == eventId);

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
            var (eventId, _) = await SeedEventAsync(factory, zoneCode: "A");
            var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(factory);
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
}
