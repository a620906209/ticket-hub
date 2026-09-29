using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ProjectC.Application.Common;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetAdminEvents;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Application.Venues.GetVenueById;
using ProjectC.Application.Venues.GetVenues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Security;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Admin;

/// <summary>
/// event-management-organizer-scoping tasks.md 7.1～7.3b：spec.md「後台管理 API 需要已切換至一個 Approved
/// Organizer」Requirement 明確列出的 8 個端點逐一驗證授權結果。之所以逐一列舉而不是只抽測一兩個：
/// 授權屬性是逐端點掛的（AdminEventsController 上另有維持 AdminOnly 的端點），只要漏掛或掛錯一個，
/// 該端點就會繞過多租戶邊界，抽測無法發現。
/// </summary>
public class EventManagementAuthorizationMatrixTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public EventManagementAuthorizationMatrixTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<string> AllEndpoints =>
    [
        "CreateVenue", "CreateSeatMap", "GetVenues", "GetVenueById", "GetSeatMapById",
        "CreateEvent", "CreateTicketType", "GetAdminEvents",
    ];

    public static TheoryData<string> WriteEndpoints => ["CreateVenue", "CreateSeatMap", "CreateEvent", "CreateTicketType"];

    private sealed record SeededResources(Guid VenueId, Guid SeatMapId, Guid EventId);

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    private static async Task<SeededResources> SeedResourcesAsync(HttpClient organizerClient)
    {
        var venueId = await ReadCreatedIdAsync(
            await organizerClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Matrix Venue")));
        var seatMapId = await ReadCreatedIdAsync(await organizerClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")])));
        var eventId = await ReadCreatedIdAsync(await organizerClient.PostAsJsonAsync(
            "/api/admin/events", new CreateEventRequest("Matrix Event", DateTime.UtcNow.AddDays(30), venueId, seatMapId)));
        return new SeededResources(venueId, seatMapId, eventId);
    }

    /// <summary>寫入端點一律帶可辨識的探針值（<paramref name="probe"/>），供事後查詢確認資源確實未被建立。</summary>
    private static Task<HttpResponseMessage> CallEndpointAsync(HttpClient client, string endpoint, SeededResources seeded, string probe)
        => endpoint switch
        {
            "CreateVenue" => client.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest(probe)),
            "CreateSeatMap" => client.PostAsJsonAsync(
                $"/api/admin/venues/{seeded.VenueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest(probe, "1")])),
            "GetVenues" => client.GetAsync("/api/admin/venues"),
            "GetVenueById" => client.GetAsync($"/api/admin/venues/{seeded.VenueId}"),
            "GetSeatMapById" => client.GetAsync($"/api/admin/venues/{seeded.VenueId}/seat-maps/{seeded.SeatMapId}"),
            "CreateEvent" => client.PostAsJsonAsync(
                "/api/admin/events", new CreateEventRequest(probe, DateTime.UtcNow.AddDays(30), seeded.VenueId, seeded.SeatMapId)),
            "CreateTicketType" => client.PostAsJsonAsync(
                $"/api/admin/events/{seeded.EventId}/ticket-types",
                new CreateTicketTypeRequest(probe, 500m, RequiresSeat: false, AvailableQuantity: 10)),
            "GetAdminEvents" => client.GetAsync("/api/admin/events"),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null),
        };

    /// <summary>以有權限的 organizerClient 事後查詢，確認帶 <paramref name="probe"/> 的資源不存在。</summary>
    private async Task AssertWriteDidNotHappenAsync(HttpClient organizerClient, string endpoint, SeededResources seeded, string probe)
    {
        switch (endpoint)
        {
            case "CreateVenue":
                var venues = await organizerClient.GetFromJsonAsync<List<VenueSummaryDto>>("/api/admin/venues");
                venues.Should().NotContain(v => v.Name == probe);
                break;
            case "CreateSeatMap":
                var venue = await organizerClient.GetFromJsonAsync<VenueDetailDto>($"/api/admin/venues/{seeded.VenueId}");
                venue!.SeatMaps.Should().ContainSingle("只應有 SeedResourcesAsync 建立的那一張座位圖");
                break;
            case "CreateEvent":
                var events = await organizerClient.GetFromJsonAsync<List<AdminEventSummaryDto>>("/api/admin/events");
                events.Should().NotContain(e => e.Title == probe);
                break;
            case "CreateTicketType":
                var ticketTypes = await _factory.CreateClient()
                    .GetFromJsonAsync<List<TicketTypeDto>>($"/api/events/{seeded.EventId}/ticket-types");
                ticketTypes.Should().NotContain(t => t.ZoneCode == probe);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null);
        }
    }

    private static string NewProbe() => $"probe-{Guid.NewGuid():N}"[..20];

    // [EVT-AUTHZ-001] 已切換至 Approved Organizer 的使用者可以成功呼叫全部 8 個端點。
    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task Endpoint_AsSwitchedApprovedOrganizerMember_Succeeds(string endpoint)
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await SeedResourcesAsync(organizerClient);

        var response = await CallEndpointAsync(organizerClient, endpoint, seeded, NewProbe());

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
    }

    // [EVT-AUTHZ-002] 已登入但未帶 OrganizerId claim（單純 Admin 角色、未切換）呼叫全部 8 個端點皆 403。
    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task Endpoint_AsAdminWithoutSwitchingOrganizer_Returns403(string endpoint)
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await SeedResourcesAsync(organizerClient);
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var response = await CallEndpointAsync(adminClient, endpoint, seeded, NewProbe());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // [EVT-AUTHZ-002] 寫入端點被 403 拒絕後，目標資源 MUST 未被建立。
    [Theory]
    [MemberData(nameof(WriteEndpoints))]
    public async Task WriteEndpoint_AsAdminWithoutSwitchingOrganizer_DoesNotCreateResource(string endpoint)
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await SeedResourcesAsync(organizerClient);
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var probe = NewProbe();

        var response = await CallEndpointAsync(adminClient, endpoint, seeded, probe);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertWriteDidNotHappenAsync(organizerClient, endpoint, seeded, probe);
    }

    // [EVT-AUTHZ-003] 未帶 Token 呼叫全部 8 個端點皆 401。
    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task Endpoint_WithoutToken_Returns401(string endpoint)
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await SeedResourcesAsync(organizerClient);

        var response = await CallEndpointAsync(_factory.CreateClient(), endpoint, seeded, NewProbe());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // [EVT-AUTHZ-003] 寫入端點被 401 拒絕後，目標資源 MUST 未被建立。
    [Theory]
    [MemberData(nameof(WriteEndpoints))]
    public async Task WriteEndpoint_WithoutToken_DoesNotCreateResource(string endpoint)
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await SeedResourcesAsync(organizerClient);
        var probe = NewProbe();

        var response = await CallEndpointAsync(_factory.CreateClient(), endpoint, seeded, probe);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertWriteDidNotHappenAsync(organizerClient, endpoint, seeded, probe);
    }

    // [EVT-AUTHZ-004] RequireOrganizerContext 不即時查表：停權前核發、未過期的 Access Token 在過期前仍可通過。
    // 這是 design.md Decision 4 的既知有界延遲視窗；若此測試失敗，代表 Policy 行為改變，spec 必須同步修改。
    // 「停權後換發／切換即被拒絕」的負向路徑由 organizer-management 的 ORG-REFRESH-003／ORG-SUSPEND-001 覆蓋。
    [Fact]
    public async Task GetAdminEvents_WithTokenIssuedBeforeOrganizerSuspended_IsStillAcceptedUntilExpiry()
    {
        var (organizerClient, organizerId) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await SeedResourcesAsync(organizerClient);
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var suspendResponse = await adminClient.PatchAsync($"/api/admin/organizers/{organizerId}/suspend", null);
        suspendResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await organizerClient.GetAsync("/api/admin/events");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var events = await response.Content.ReadFromJsonAsync<List<AdminEventSummaryDto>>();
        events.Should().Contain(e => e.Id == seeded.EventId);
    }

    // [EVT-AUTHZ-005] 已過期的 Access Token MUST 由 JWT Bearer 驗證擋下回 401，CreateEventHandler 不得被執行。
    // 對照組以「同一組 claim、僅過期時間不同」的有效 Token 呼叫同一端點成功，佐證 401 只源自過期。
    [Fact]
    public async Task CreateEvent_WithExpiredAccessToken_Returns401AndDoesNotCreateEvent()
    {
        var email = AuthTestHelper.NewEmail();
        var (organizerClient, organizerId) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory, email);
        var seeded = await SeedResourcesAsync(organizerClient);
        var memberId = await GetMemberIdAsync(email);
        var expiredProbe = NewProbe();
        var validProbe = NewProbe();

        var expiredClient = CreateClientWithToken(
            CreateSignedAccessToken(memberId, email, organizerId, expiresAtUtc: DateTime.UtcNow.AddMinutes(-5)));
        var validClient = CreateClientWithToken(
            CreateSignedAccessToken(memberId, email, organizerId, expiresAtUtc: DateTime.UtcNow.AddMinutes(5)));

        var expiredResponse = await CallEndpointAsync(expiredClient, "CreateEvent", seeded, expiredProbe);
        var validResponse = await CallEndpointAsync(validClient, "CreateEvent", seeded, validProbe);

        expiredResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        validResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        // 必須用同一個 Organizer 的身份查詢：後台活動列表依 OrganizerId 過濾，換成其他 Organizer 查詢永遠查不到，
        // 斷言會變成恆真、無法證明資料庫未被寫入。
        await AssertWriteDidNotHappenAsync(organizerClient, "CreateEvent", seeded, expiredProbe);
        var events = await organizerClient.GetFromJsonAsync<List<AdminEventSummaryDto>>("/api/admin/events");
        events.Should().Contain(e => e.Title == validProbe, "同一查詢必須看得到對照組建立的活動，否則「查無過期探針」不具證明力");
    }

    private async Task<Guid> GetMemberIdAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (await dbContext.Members.SingleAsync(m => m.Email == email)).Id;
    }

    private HttpClient CreateClientWithToken(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>比照 JwtTokenService 的 claim 組成自行簽發，只為了能指定過去的過期時間（不真實等待，避免測試變慢）。
    /// 過期時間刻意設在 ClockSkew（30 秒）之外。</summary>
    private string CreateSignedAccessToken(Guid memberId, string email, Guid organizerId, DateTime expiresAtUtc)
    {
        var jwtOptions = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, memberId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(ClaimTypes.Role, "Member"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(CustomClaimTypes.OrganizerId, organizerId.ToString()),
        };
        var token = new JwtSecurityToken(
            issuer: jwtOptions.Issuer,
            audience: jwtOptions.Audience,
            claims: claims,
            notBefore: expiresAtUtc.AddMinutes(-30),
            expires: expiresAtUtc,
            signingCredentials: signingCredentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
