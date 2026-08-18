using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetAdminEvents;
using ProjectC.Application.Members;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Admin;

public class AdminEventsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AdminEventsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    private async Task<(Guid VenueId, Guid SeatMapId)> CreateVenueWithSeatMapAsync(HttpClient adminClient, string zoneCode = "A")
    {
        var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Test Venue"));
        var venueId = await ReadCreatedIdAsync(venueResponse);

        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps",
            new CreateSeatMapRequest([new SeatRequest(zoneCode, "1")]));
        var seatMapId = await ReadCreatedIdAsync(seatMapResponse);

        return (venueId, seatMapId);
    }

    private async Task<Guid> CreateEventAsync(HttpClient adminClient, Guid venueId, Guid seatMapId)
    {
        var response = await adminClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
        return await ReadCreatedIdAsync(response);
    }

    [Fact]
    public async Task CreateEvent_WithValidVenueAndSeatMap_ReturnsCreated()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient);

        var response = await adminClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateEvent_WithBlankTitle_Returns400()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient);

        var response = await adminClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest("  ", DateTime.UtcNow.AddDays(30), venueId, seatMapId));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateEvent_WithNonExistentVenueOrSeatMap_Returns404()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var response = await adminClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateTicketType_WithExistingZoneAndValidPrice_ReturnsCreated()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient, zoneCode: "A");
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);

        var response = await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new CreateTicketTypeRequest("A", 500m));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateTicketType_WithInvalidPrice_Returns400()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient, zoneCode: "A");
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);

        var response = await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new CreateTicketTypeRequest("A", 0m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTicketType_WithZoneNotInSeatMap_Returns400()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient, zoneCode: "A");
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);

        var response = await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new CreateTicketTypeRequest("B", 500m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTicketType_WithNonExistentEvent_Returns404()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var response = await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{Guid.NewGuid()}/ticket-types",
            new CreateTicketTypeRequest("A", 500m));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- 建立活動記錄建立者（透過 GET /api/admin/events 查詢驗證，POST 的成功回應只有 { id }） ----

    [Fact]
    public async Task CreateEvent_ThenGetAdminEvents_RecordsCreatedByMemberId()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var myProfileResponse = await adminClient.GetAsync("/api/members/me");
        var adminMemberId = (await myProfileResponse.Content.ReadFromJsonAsync<MemberProfileDto>())!.Id;
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient);
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);

        var response = await adminClient.GetAsync("/api/admin/events");

        var events = await response.Content.ReadFromJsonAsync<List<AdminEventSummaryDto>>();
        events.Should().ContainSingle(e => e.Id == eventId && e.CreatedByMemberId == adminMemberId);
    }

    // ---- 查詢活動列表（Admin 專用端點）需要 Admin 角色 ----

    [Fact]
    public async Task GetEvents_AsAdmin_Returns200()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var response = await adminClient.GetAsync("/api/admin/events");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetEvents_AsNonAdminMember_Returns403()
    {
        var email = AuthTestHelper.NewEmail();
        var client = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync("/api/admin/events");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetEvents_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/admin/events");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
