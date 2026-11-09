using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Common.Paging;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetAdminEvents;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Admin;

public class AdminEventsPagingTests : IClassFixture<QueryCountingWebApplicationFactory>
{
    private readonly QueryCountingWebApplicationFactory _factory;

    public AdminEventsPagingTests(QueryCountingWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<string, string[]> InvalidPagingQueries => PaginationTestSupport.InvalidPagingQueries;

    // [LP-PARAM-001][LP-RESP-001] 未帶參數：page=1、pageSize=20，回應為 camelCase 的四個欄位。
    [Fact]
    public async Task GetEvents_WithoutPagingParameters_ReturnsEnvelopeWithDefaults()
    {
        var (client, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var eventIds = await SeedEventsAsync(client, 1);

        var response = await client.GetAsync("/api/admin/events");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(["items", "page", "pageSize", "totalCount"]);
        document.RootElement.GetProperty("page").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("pageSize").GetInt32().Should().Be(20);
        document.RootElement.GetProperty("totalCount").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().Should().Be(eventIds[0]);
    }

    // [LP-PARAM-002] 只帶其中一個參數，另一個用預設值。
    [Fact]
    public async Task GetEvents_WithOnlyOneParameter_UsesDefaultForTheOther()
    {
        var (client, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        await SeedEventsAsync(client, 1);

        var pageSizeOnly = await client.GetFromJsonAsync<PagedResultDto<AdminEventSummaryDto>>("/api/admin/events?pageSize=5");
        var pageOnly = await client.GetFromJsonAsync<PagedResultDto<AdminEventSummaryDto>>("/api/admin/events?page=2");

        pageSizeOnly!.Page.Should().Be(1);
        pageSizeOnly.PageSize.Should().Be(5);
        pageOnly!.Page.Should().Be(2);
        pageOnly.PageSize.Should().Be(20);
    }

    // [LP-VALID-002][LP-VALID-007] 上界合法，資料不足時 items 為空。
    [Fact]
    public async Task GetEvents_WithUpperBoundPagingParameters_Returns200()
    {
        var (client, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        await SeedEventsAsync(client, 1);

        var maxPage = await client.GetFromJsonAsync<PagedResultDto<AdminEventSummaryDto>>("/api/admin/events?page=10000&pageSize=100");

        maxPage!.Page.Should().Be(10000);
        maxPage.PageSize.Should().Be(100);
        maxPage.Items.Should().BeEmpty();
        maxPage.TotalCount.Should().Be(1);
    }

    // [EVT-LIST-007][LP-VALID-001～008] 越界、非整數、int 溢位一律 400 Validation。
    [Theory]
    [MemberData(nameof(InvalidPagingQueries))]
    public async Task GetEvents_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(string query, string[] expectedErrorKeys)
    {
        var (client, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);

        var response = await client.GetAsync($"/api/admin/events{query}");

        await PaginationTestSupport.AssertValidationErrorAsync(response, expectedErrorKeys);
    }

    // [EVT-LIST-001][EVT-LIST-006] 45 筆中取第 2 頁（pageSize=20）；totalCount 不含其他 Organizer 的活動。
    [Fact]
    public async Task GetEvents_WithPageTwoOverFortyFiveEvents_ReturnsItemsTwentyOneToForty_AndTotalExcludesOtherOrganizer()
    {
        var (client, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (otherClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        await SeedEventsAsync(otherClient, 2);
        var eventIds = await SeedEventsAsync(client, 45);
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SetCreatedAtAsync(eventIds, i => baseTime.AddMinutes(i));
        var newestFirst = eventIds.Reverse().ToList();

        var page = await client.GetFromJsonAsync<PagedResultDto<AdminEventSummaryDto>>("/api/admin/events?page=2&pageSize=20");

        page!.Page.Should().Be(2);
        page.PageSize.Should().Be(20);
        page.TotalCount.Should().Be(45);
        page.Items.Select(e => e.Id).Should().Equal(newestFirst.Skip(20).Take(20));
    }

    // [EVT-LIST-008] 建立時間新到舊、相同者 Id 升冪、null 最後；所有頁合併後每筆恰好一次。
    [Fact]
    public async Task GetEvents_WalkingAllPages_OrdersByCreatedAtDescWithNullLastAndIdTiebreak()
    {
        var (client, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var eventIds = await SeedEventsAsync(client, 7);
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = older.AddDays(1);
        // 0,1 最新且同時間；2,3 較舊且同時間；4 更舊；5,6 為 null。
        var createdAtByIndex = new DateTime?[] { newer, newer, older, older, older.AddDays(-1), null, null };
        await SetCreatedAtAsync(eventIds, i => createdAtByIndex[i]);
        var expected = eventIds.Take(2).Order()
            .Concat(eventIds.Skip(2).Take(2).Order())
            .Concat([eventIds[4]])
            .Concat(eventIds.Skip(5).Order())
            .ToList();

        var walked = new List<Guid>();
        for (var pageNumber = 1; pageNumber <= 4; pageNumber++)
        {
            var page = await client.GetFromJsonAsync<PagedResultDto<AdminEventSummaryDto>>($"/api/admin/events?page={pageNumber}&pageSize=2");
            walked.AddRange(page!.Items.Select(e => e.Id));
        }

        walked.Should().Equal(expected);
    }

    // [EVT-LIST-009] Events/Members/EventSeats 查詢次數與活動總數、頁內筆數無關。
    [Fact]
    public async Task GetEvents_WithThreeVersusTwentyFiveEvents_IssuesSameQueriesPerTable()
    {
        var (smallClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (largeClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        await SeedEventsAsync(smallClient, 3);
        await SeedEventsAsync(largeClient, 25);

        (await smallClient.GetAsync("/api/admin/events")).EnsureSuccessStatusCode();
        var smallCounts = await GetQueryCountsByTableAsync(smallClient, "/api/admin/events");
        var largeCounts = await GetQueryCountsByTableAsync(largeClient, "/api/admin/events");

        largeCounts.Should().BeEquivalentTo(smallCounts);
        // 正向錨點：Events 為 COUNT＋當頁各一次，Members 與 EventSeats 各一次批次查詢。
        smallCounts.GetValueOrDefault("Events").Should().Be(2);
        smallCounts.GetValueOrDefault("Members").Should().Be(1);
        smallCounts.GetValueOrDefault("EventSeats").Should().Be(1);
    }

    // [EVT-LIST-009] 超過最後一頁（空頁）不查 Members 與 EventSeats。
    [Fact]
    public async Task GetEvents_WithEmptyPage_SkipsMemberAndSeatQueries()
    {
        var (client, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        await SeedEventsAsync(client, 3);
        (await client.GetAsync("/api/admin/events")).EnsureSuccessStatusCode();

        var counts = await GetQueryCountsByTableAsync(client, "/api/admin/events?page=5");

        counts.GetValueOrDefault("Members").Should().Be(0);
        counts.GetValueOrDefault("EventSeats").Should().Be(0);
        counts.GetValueOrDefault("Events").Should().Be(2);
    }

    // [EVT-LIST-009] 當頁的統計與建立者顯示名稱正確。
    [Fact]
    public async Task GetEvents_ReturnsSeatStatisticsAndCreatorNameForPageItems()
    {
        var (client, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        await SeedEventsAsync(client, 2);

        var page = await client.GetFromJsonAsync<PagedResultDto<AdminEventSummaryDto>>("/api/admin/events");

        page!.Items.Should().HaveCount(2);
        page.Items.Should().OnlyContain(e => e.AvailableSeatCount == 2 && e.HeldSeatCount == 0 && e.SoldSeatCount == 0);
        page.Items.Should().OnlyContain(e => e.CreatedByDisplayName == "Test User" && e.CreatedByMemberId != null);
    }

    [Fact]
    public async Task GetEvents_WithoutAuthentication_Returns401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/admin/events");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<IReadOnlyList<Guid>> SeedEventsAsync(HttpClient client, int count)
    {
        var venueId = await ReadCreatedIdAsync(await client.PostAsJsonAsync(
            "/api/admin/venues", new CreateVenueRequest($"Paging Venue {Guid.NewGuid():N}")));
        var seatMapId = await ReadCreatedIdAsync(await client.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1"), new SeatRequest("A", "2")])));
        var eventIds = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            eventIds.Add(await ReadCreatedIdAsync(await client.PostAsJsonAsync(
                "/api/admin/events", new CreateEventRequest($"Paging Event {i}", DateTime.UtcNow.AddDays(30), venueId, seatMapId))));
        }

        return eventIds;
    }

    /// <summary>直接改資料庫的建立時間（API 一律寫入現在時間，無法造出 null 與相同時間）。</summary>
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

    private async Task<IReadOnlyDictionary<string, int>> GetQueryCountsByTableAsync(HttpClient client, string url)
    {
        var tag = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(QueryCountingInterceptor.TagHeaderName, tag);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.QueryCounter.GetCommandTexts(tag).Should().NotBeEmpty("interceptor 必須確實攔截到被測請求的查詢");
        return _factory.QueryCounter.GetQueryCountsByTable(tag);
    }
}
