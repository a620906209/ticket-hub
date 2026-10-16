using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Organizers.SwitchOrganizerContext;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Domain.Organizers;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Admin;

// request-body-size-limits design.md 決策 8：建立座位圖放寬到 10MB 本文後，以 Organizer 分區限制頻率。
public class SeatMapRateLimitingTests : IClassFixture<SeatMapRateLimitedWebApplicationFactory>
{
    private readonly SeatMapRateLimitedWebApplicationFactory _factory;

    public SeatMapRateLimitingTests(SeatMapRateLimitedWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateSeatMap_WhenOrganizerExceedsLimit_Returns429ProblemDetailsAndCreatesNoSeatMap()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var venueId = await CreateVenueAsync(organizerClient);

        // 授權早於限流：401／403 不得消耗任何分區的額度（含取不到 OrganizerId 時的共用 fail-closed 鍵）。
        var anonymousClient = _factory.CreateClient();
        for (var i = 0; i <= SeatMapRateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            (await PostSeatMapAsync(anonymousClient, venueId)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        var (memberClient, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);
        for (var i = 0; i <= SeatMapRateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            (await PostSeatMapAsync(memberClient, venueId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        for (var i = 0; i < SeatMapRateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            (await PostSeatMapAsync(organizerClient, venueId)).StatusCode.Should().Be(HttpStatusCode.Created);
        }
        using var rejected = await PostSeatMapAsync(organizerClient, venueId);

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.Contains("Retry-After").Should().BeTrue();
        using (var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()))
        {
            problem.RootElement.GetProperty("status").GetInt32().Should().Be(429);
            problem.RootElement.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
            problem.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        }
        (await CountSeatMapsAsync(venueId)).Should().Be(SeatMapRateLimitedWebApplicationFactory.PermitLimit);

        // 語法錯誤的本文仍得到 429 而非模型繫結的 400：限流早於本文讀取。
        using var malformed = await organizerClient.PostAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new StringContent("""{"seats":""", Encoding.UTF8, "application/json"));
        malformed.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task CreateSeatMap_WhenOtherOrganizerExhaustedLimit_StillCreatesSeatMap()
    {
        var (organizerAClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory, organizerName: "Organizer A");
        var venueAId = await CreateVenueAsync(organizerAClient);
        for (var i = 0; i < SeatMapRateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            (await PostSeatMapAsync(organizerAClient, venueAId)).StatusCode.Should().Be(HttpStatusCode.Created);
        }
        (await PostSeatMapAsync(organizerAClient, venueAId)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var (organizerBClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory, organizerName: "Organizer B");
        var venueBId = await CreateVenueAsync(organizerBClient);
        using var response = await PostSeatMapAsync(organizerBClient, venueBId);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // 分區鍵若誤用 MemberId，多帳號即可聚合額度；同一 Organizer 的第二個成員必須被同一個分區擋下。
    [Fact]
    public async Task CreateSeatMap_WhenSameOrganizerOtherMemberExhaustedLimit_Returns429()
    {
        var (firstMemberClient, organizerId) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var venueId = await CreateVenueAsync(firstMemberClient);
        for (var i = 0; i < SeatMapRateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            (await PostSeatMapAsync(firstMemberClient, venueId)).StatusCode.Should().Be(HttpStatusCode.Created);
        }
        var secondMemberClient = await CreateSecondMemberClientAsync(organizerId);

        using var response = await PostSeatMapAsync(secondMemberClient, venueId);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await CountSeatMapsAsync(venueId)).Should().Be(SeatMapRateLimitedWebApplicationFactory.PermitLimit);
    }

    private async Task<HttpClient> CreateSecondMemberClientAsync(Guid organizerId)
    {
        var email = AuthTestHelper.NewEmail();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(_factory.CreateClient(), email);
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var member = await dbContext.Members.SingleAsync(m => m.Email == email);
            dbContext.OrganizerMembers.Add(new OrganizerMember(Guid.NewGuid(), organizerId, member.Id, OrganizerMemberRole.Owner));
            await dbContext.SaveChangesAsync();
        }

        var switchClient = _factory.CreateClient();
        switchClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var switchResponse = await switchClient.PostAsJsonAsync(
            $"/api/organizers/{organizerId}/switch-context", new SwitchOrganizerContextRequest(tokens.RefreshToken));
        switchResponse.EnsureSuccessStatusCode();
        var switchResult = (await switchResponse.Content.ReadFromJsonAsync<SwitchOrganizerContextResultDto>())!;

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", switchResult.AccessToken);
        return client;
    }

    private static Task<HttpResponseMessage> PostSeatMapAsync(HttpClient client, Guid venueId) =>
        client.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")]));

    private static async Task<Guid> CreateVenueAsync(HttpClient organizerClient)
    {
        var response = await organizerClient.PostAsJsonAsync(
            "/api/admin/venues", new CreateVenueRequest($"Rate Limit Venue {Guid.NewGuid():N}"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;
    }

    private async Task<int> CountSeatMapsAsync(Guid venueId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await dbContext.SeatMaps.CountAsync(s => s.VenueId == venueId);
    }
}
