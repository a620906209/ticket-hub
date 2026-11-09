using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.WebApi.Tests.TestSupport;
using StackExchange.Redis;

namespace ProjectC.WebApi.Tests.Events;

// event-catalog spec EVT-DETAIL-003／004／005：GET /api/events/{id} 與列表共用同一份快取，真實 Redis／真實資料庫。
// 每個測試方法各自建立獨立的 factory，理由同 QueryCachingComponentTests（固定快取 key 不可跨測試共用）。
public class QueryCachingEventDetailTests
{
    // 以字面值鎖定列表快取 key，不引用正式常數。
    private const string EventListCacheKeyV2 = "query-cache:events:list:v2";

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    private static async Task<Guid> SeedEventAsync(CachingComponentTestWebApplicationFactory factory)
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(factory);
        var venueId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Test Venue")));
        var seatMapId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")])));
        return await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync(
            "/api/admin/events", new CreateEventRequest("Concert", DateTime.UtcNow.Date.AddDays(30), venueId, seatMapId)));
    }

    private static IDatabase GetRedisDatabase(CachingComponentTestWebApplicationFactory factory)
        => factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    // EVT-DETAIL-003：路由 {id:guid} 約束擋下非 GUID，不進入 Handler（比照 QC-ACCESS-003）。
    [Fact]
    public async Task GetEventById_WithNonGuidId_Returns404WithoutTouchingRepositoryOrCache()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var client = factory.CreateClient();

            var response = await client.GetAsync("/api/events/not-a-guid");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            factory.EventRepositoryCallCounter.GetAllAsyncCallCount.Should().Be(0);
            factory.EventRepositoryCallCounter.GetByIdAsyncCallCount.Should().Be(0);
            (await GetRedisDatabase(factory).KeyExistsAsync(EventListCacheKeyV2)).Should().BeFalse("請求未進入 Handler，不應寫入列表快取");
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    // EVT-DETAIL-004：列表快取已預熱時，單筆查詢不查庫。
    [Fact]
    public async Task GetEventById_WhenListCacheIsWarm_DoesNotQueryRepository()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var eventId = await SeedEventAsync(factory);
            var client = factory.CreateClient();
            (await client.GetAsync("/api/events")).EnsureSuccessStatusCode();
            factory.EventRepositoryCallCounter.GetAllAsyncCallCount.Should().Be(1);

            var response = await client.GetAsync($"/api/events/{eventId}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<EventDto>())!.Id.Should().Be(eventId);
            factory.EventRepositoryCallCounter.GetAllAsyncCallCount.Should().Be(1, "命中列表快取，不再查詢資料庫");
            factory.EventRepositoryCallCounter.GetByIdAsyncCallCount.Should().Be(0);
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    // EVT-DETAIL-005：冷快取時查一次 GetAllAsync、寫入列表 key，之後列表請求命中。
    [Fact]
    public async Task GetEventById_WhenListCacheIsCold_QueriesAllOnceWritesListCacheAndLaterListHitsIt()
    {
        var factory = new CachingComponentTestWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var eventId = await SeedEventAsync(factory);
            var client = factory.CreateClient();
            (await GetRedisDatabase(factory).KeyExistsAsync(EventListCacheKeyV2)).Should().BeFalse("前置：建立活動後列表快取為冷");

            var response = await client.GetAsync($"/api/events/{eventId}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            factory.EventRepositoryCallCounter.GetAllAsyncCallCount.Should().Be(1);
            factory.EventRepositoryCallCounter.GetByIdAsyncCallCount.Should().Be(0);
            (await GetRedisDatabase(factory).KeyExistsAsync(EventListCacheKeyV2)).Should().BeTrue();

            var listResponse = await client.GetAsync("/api/events");

            listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            (await listResponse.Content.ReadFromJsonAsync<List<EventDto>>()).Should().Contain(e => e.Id == eventId);
            factory.EventRepositoryCallCounter.GetAllAsyncCallCount.Should().Be(1, "列表請求命中單筆查詢寫入的快取");
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }
}
