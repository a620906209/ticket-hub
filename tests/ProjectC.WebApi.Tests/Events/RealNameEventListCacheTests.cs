using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Events.GetEvents;
using ProjectC.WebApi.Tests.TestSupport;
using StackExchange.Redis;

namespace ProjectC.WebApi.Tests.Events;

/// <summary>
/// 公開活動列表快取 key 是固定字串，寫入假內容會汙染同一 Redis 的其他測試，因此每個測試建立自己的 factory（獨立容器）。
/// 假內容一律由「先讓 API 自己寫入真快取、再改寫那份 JSON」產生，確保除了被測欄位外格式與正式寫入完全一致。
/// </summary>
public class RealNameEventListCacheTests
{
    private const string IsRealNameRequiredProperty = nameof(EventDto.IsRealNameRequired);

    private static async Task RewriteCachedEventListAsync(CustomWebApplicationFactory factory, Action<JsonObject> rewriteEvent)
    {
        var warmUpResponse = await factory.CreateClient().GetAsync("/api/events");
        warmUpResponse.EnsureSuccessStatusCode();

        var database = factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var cached = await database.StringGetAsync(GetEventsHandler.CacheKey);
        cached.HasValue.Should().BeTrue("暖機請求應已寫入活動列表快取，否則以下改寫沒有意義");
        var events = JsonNode.Parse((string)cached!)!.AsArray();
        events.Should().NotBeEmpty();
        foreach (var eventNode in events) rewriteEvent(eventNode!.AsObject());
        await database.StringSetAsync(GetEventsHandler.CacheKey, events.ToJsonString());
    }

    private static async Task RunWithIsolatedFactoryAsync(Func<CustomWebApplicationFactory, Task> test)
    {
        var factory = new CustomWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            await test(factory);
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    // [TP-BROWSE-RN-002] 功能上線前寫入、不含此欄位的快取內容不得造成 500，缺漏時視為 false。
    [Fact]
    public async Task GetEvents_WhenCachedEntriesLackIsRealNameRequired_Returns200WithFalse()
    {
        await RunWithIsolatedFactoryAsync(async factory =>
        {
            var organizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(factory);
            var seededEvent = await RealNameTestData.SeedEventAsync(factory, organizerClient, isRealNameRequired: false);
            await RewriteCachedEventListAsync(factory, eventNode => eventNode.Remove(IsRealNameRequiredProperty));

            var response = await factory.CreateClient().GetAsync("/api/events");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var events = await response.Content.ReadFromJsonAsync<List<EventDto>>();
            events!.Single(e => e.Id == seededEvent.EventId).IsRealNameRequired.Should().BeFalse();
        });
    }

    // [RNV-CACHE-001] 列表快取只用於顯示；閘門一律讀 DB，過期快取說「不需實名」也不得放行未登記會員。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Gates_WhenCachedEventListSaysRealNameNotRequired_StillRejectUnregisteredMember(bool isLegacyFormat)
    {
        await RunWithIsolatedFactoryAsync(async factory =>
        {
            var organizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(factory);
            var seededEvent = await RealNameTestData.SeedEventAsync(factory, organizerClient, isRealNameRequired: true, isQueueModeEnabled: true);
            var unregisteredMember = await RealNameTestData.CreateMemberAsync(factory);
            await RewriteCachedEventListAsync(factory, eventNode =>
            {
                if (isLegacyFormat) eventNode.Remove(IsRealNameRequiredProperty);
                else eventNode[IsRealNameRequiredProperty] = false;
            });

            var events = await factory.CreateClient().GetFromJsonAsync<List<EventDto>>("/api/events");
            events!.Single(e => e.Id == seededEvent.EventId).IsRealNameRequired.Should().BeFalse("確認列表確實命中了被改寫的快取");

            var orderResponse = await RealNameTestData.PlaceOrderAsync(unregisteredMember.Client, seededEvent);
            var joinResponse = await RealNameTestData.JoinQueueAsync(unregisteredMember.Client, seededEvent.EventId);

            orderResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await RealNameTestData.ReadProblemTitleAsync(orderResponse)).Should().Be("RealNameRequired");
            joinResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await RealNameTestData.ReadProblemTitleAsync(joinResponse)).Should().Be("RealNameRequired");
        });
    }
}
