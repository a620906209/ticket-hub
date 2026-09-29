using System.Net;
using System.Net.Http.Json;
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
    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    // 回傳建立活動的同一個 client：建立票種會核對活動是否屬於呼叫端目前 Organizer（EVT-TICKET-004），
    // 換成另一個 client 會被視同活動不存在。
    private static async Task<(Guid EventId, Guid VenueId, HttpClient AdminClient)> SeedEventAsync(CachingComponentTestWebApplicationFactory factory, string zoneCode = "A")
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
            new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
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

    private static Task<bool> EventListCacheKeyExistsAsync(CachingComponentTestWebApplicationFactory factory)
        => factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase().KeyExistsAsync(GetEventsHandler.CacheKey);

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
}
