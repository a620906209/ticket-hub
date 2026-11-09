using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Common.Paging;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Events;

// 公開列表是全域資料，這些情境依賴精確總數與絕對位置，所以每個測試各自建立全新的 factory（獨立 DB 與 Redis），
// 不與 EventsControllerTests 共用 fixture（event-catalog EVT-LIST-001～003、EVT-LIST-006、EVT-LIST-ORDER-001～004）。
public class EventsListPagingTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new();

    async Task IAsyncLifetime.InitializeAsync() => await ((IAsyncLifetime)_factory).InitializeAsync();

    async Task IAsyncLifetime.DisposeAsync() => await ((IAsyncLifetime)_factory).DisposeAsync();

    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // EVT-LIST-001：匿名、未帶參數；items 欄位與單一活動端點一致。
    [Fact]
    public async Task GetEvents_AsAnonymousWithoutParameters_ReturnsDefaultsAndSameFieldsAsDetailEndpoint()
    {
        var eventIds = await SeedEventsAsync(1);
        var anonymousClient = _factory.CreateClient();

        var page = await anonymousClient.GetFromJsonAsync<PagedResultDto<EventDto>>("/api/events");
        var detail = await anonymousClient.GetFromJsonAsync<EventDto>($"/api/events/{eventIds[0]}");

        page!.Page.Should().Be(1);
        page.PageSize.Should().Be(20);
        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle().Which.Should().Be(detail);
    }

    // EVT-LIST-002／003：45 筆中第 3 頁 5 筆；超過最後一頁回 200、空 items、totalCount 不變。
    [Fact]
    public async Task GetEvents_OverFortyFiveEvents_ReturnsPartialLastPageAndEmptyPageBeyondIt()
    {
        var eventIds = await SeedEventsAsync(45);
        await SetCreatedAtAsync(eventIds, i => BaseTime.AddMinutes(i));
        var newestFirst = eventIds.Reverse().ToList();
        var client = _factory.CreateClient();

        var lastPage = await client.GetFromJsonAsync<PagedResultDto<EventDto>>("/api/events?page=3&pageSize=20");
        var beyond = await client.GetAsync("/api/events?page=4&pageSize=20");

        lastPage!.TotalCount.Should().Be(45);
        lastPage.Items.Select(e => e.Id).Should().Equal(newestFirst.Skip(40));
        beyond.StatusCode.Should().Be(HttpStatusCode.OK);
        var beyondPage = await beyond.Content.ReadFromJsonAsync<PagedResultDto<EventDto>>();
        beyondPage!.Items.Should().BeEmpty();
        beyondPage.TotalCount.Should().Be(45);
    }

    // EVT-LIST-ORDER-001～004：新到舊、同時間依 Id 升冪、null 最後；逐頁讀取每筆恰好一次。
    [Fact]
    public async Task GetEvents_WalkingAllPages_OrdersByCreatedAtDescWithNullLastAndIdTiebreak()
    {
        var eventIds = await SeedEventsAsync(7);
        var older = BaseTime;
        var newer = older.AddDays(1);
        // 0,1 最新且同時間；2,3 較舊且同時間；4 更舊；5,6 為 null。
        var createdAtByIndex = new DateTime?[] { newer, newer, older, older, older.AddDays(-1), null, null };
        await SetCreatedAtAsync(eventIds, i => createdAtByIndex[i]);
        var expected = eventIds.Take(2).Order()
            .Concat(eventIds.Skip(2).Take(2).Order())
            .Concat([eventIds[4]])
            .Concat(eventIds.Skip(5).Order())
            .ToList();
        var client = _factory.CreateClient();

        var walked = new List<Guid>();
        for (var pageNumber = 1; pageNumber <= 4; pageNumber++)
        {
            var page = await client.GetFromJsonAsync<PagedResultDto<EventDto>>($"/api/events?page={pageNumber}&pageSize=2");
            walked.AddRange(page!.Items.Select(e => e.Id));
        }

        walked.Should().Equal(expected);
    }

    // EVT-LIST-006：排在第 2 頁之後的活動，單一活動端點仍回 200。
    [Fact]
    public async Task GetEventById_WhenEventIsBeyondFirstListPage_Returns200()
    {
        var eventIds = await SeedEventsAsync(25);
        await SetCreatedAtAsync(eventIds, i => BaseTime.AddMinutes(i));
        var oldestId = eventIds[0];
        var client = _factory.CreateClient();
        var firstPage = await client.GetFromJsonAsync<PagedResultDto<EventDto>>("/api/events?page=1&pageSize=20");
        firstPage!.Items.Should().NotContain(e => e.Id == oldestId, "前置條件：最舊的活動不在第 1 頁");

        var response = await client.GetAsync($"/api/events/{oldestId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<EventDto>())!.Id.Should().Be(oldestId);
    }

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<IReadOnlyList<Guid>> SeedEventsAsync(int count)
    {
        var client = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var venueId = await ReadCreatedIdAsync(await client.PostAsJsonAsync(
            "/api/admin/venues", new CreateVenueRequest($"Public Paging Venue {Guid.NewGuid():N}")));
        var seatMapId = await ReadCreatedIdAsync(await client.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")])));
        var eventIds = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            eventIds.Add(await ReadCreatedIdAsync(await client.PostAsJsonAsync(
                "/api/admin/events", new CreateEventRequest($"Public Paging Event {i}", DateTime.UtcNow.AddDays(30), venueId, seatMapId))));
        }

        return eventIds;
    }

    // API 一律寫入現在時間，造不出 null 與相同時間，所以直接改資料庫；此時不得已有任何 GET /api/events
    // 寫入 v3 快取，否則更新不會反映（本類別每個測試都在讀取列表前先完成資料準備）。
    private async Task SetCreatedAtAsync(IReadOnlyList<Guid> eventIds, Func<int, DateTime?> createdAtByIndex)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        for (var i = 0; i < eventIds.Count; i++)
        {
            var id = eventIds[i];
            var createdAt = createdAtByIndex(i);
            await dbContext.Events.Where(e => e.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.CreatedAtUtc, createdAt));
        }
    }
}
