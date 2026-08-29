using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Members;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Events;

// PQ-JOIN-007~009（design.md 決策 7；purchase-queue spec）——這幾個 Scenario 涉及 [Authorize] 中介軟體
// 層級的角色/未登入判斷與 HTTP request body 的實際繫結行為，MUST 走真正的 HTTP 呼叫驗證，
// 不能只在 JoinPurchaseQueueHandler 層級測（見 tasks.md 12.4b）。
public class EventQueueControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public EventQueueControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    private async Task<Guid> SeedQueueModeEnabledEventAsync(HttpClient adminClient)
    {
        var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Queue Test Venue"));
        var venueId = await ReadCreatedIdAsync(venueResponse);
        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")]));
        var seatMapId = await ReadCreatedIdAsync(seatMapResponse);
        var eventResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/events", new CreateEventRequest("Queue Test Event", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
        var eventId = await ReadCreatedIdAsync(eventResponse);

        var patchResponse = await adminClient.PatchAsJsonAsync($"/api/admin/events/{eventId}/queue-mode", new { enabled = true });
        patchResponse.EnsureSuccessStatusCode();

        return eventId;
    }

    private async Task<HttpClient> CreateAuthenticatedMemberClientAsync(string? email = null)
    {
        var client = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client, email ?? AuthTestHelper.NewEmail());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private async Task<Guid> ReadOwnMemberIdAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/members/me");
        var profile = await response.Content.ReadFromJsonAsync<MemberProfileDto>();
        return profile!.Id;
    }

    [Fact]
    public async Task JoinQueue_AsAdminRole_Returns201AndCreatesEntry()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var eventId = await SeedQueueModeEnabledEventAsync(adminClient);

        var response = await adminClient.PostAsync($"/api/events/{eventId}/queue/entries", null);

        response.StatusCode.Should().Be(HttpStatusCode.Created, "Admin 角色帳號應依一般會員的既定規則處理，不因角色而被拒絕");
    }

    [Fact]
    public async Task JoinQueue_WithoutAuthentication_Returns401()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var eventId = await SeedQueueModeEnabledEventAsync(adminClient);
        var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.PostAsync($"/api/events/{eventId}/queue/entries", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task JoinQueue_WhenRequestBodyCarriesAnotherMemberId_IgnoresItAndUsesCallersOwnJwtIdentity()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var eventId = await SeedQueueModeEnabledEventAsync(adminClient);

        var otherClient = await CreateAuthenticatedMemberClientAsync();
        var otherMemberId = await ReadOwnMemberIdAsync(otherClient);

        var callerClient = await CreateAuthenticatedMemberClientAsync();
        var callerMemberId = await ReadOwnMemberIdAsync(callerClient);

        // 端點本身沒有宣告任何接受 request body 的參數（型別層級即不接受），這裡刻意夾帶一個看似合法的
        // memberId 欄位，確認 model binding 會忽略它，不會被拿來覆寫排隊紀錄的會員身份。
        var response = await callerClient.PostAsJsonAsync($"/api/events/{eventId}/queue/entries", new { memberId = otherMemberId });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entryId = (await response.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;
        var entry = await dbContext.PurchaseQueueEntries.AsNoTracking().SingleAsync(e => e.Id == entryId);
        entry.MemberId.Should().Be(callerMemberId);
        entry.MemberId.Should().NotBe(otherMemberId);
    }
}
