using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Application.Venues.GetSeatMapById;
using ProjectC.Application.Venues.GetVenueById;
using ProjectC.Application.Venues.GetVenues;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Admin;

public class AdminVenuesControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AdminVenuesControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<Guid> CreateVenueAsync(HttpClient adminClient, string name = "Test Venue")
    {
        var response = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest(name));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    [Fact]
    public async Task CreateVenue_AsAdmin_ReturnsCreated()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var response = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Taipei Arena"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateVenue_AsNonAdminMember_Returns403()
    {
        var email = AuthTestHelper.NewEmail();
        var client = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Taipei Arena"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateVenue_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Taipei Arena"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateSeatMap_WithUniqueSeats_ReturnsCreated()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var venueId = await CreateVenueAsync(adminClient);

        var response = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps",
            new CreateSeatMapRequest([new SeatRequest("A", "1"), new SeatRequest("A", "2")]));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateSeatMap_WithDuplicateSeat_ReturnsConflict()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var venueId = await CreateVenueAsync(adminClient);

        var response = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps",
            new CreateSeatMapRequest([new SeatRequest("A", "1"), new SeatRequest("A", "1")]));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateSeatMap_WithNonExistentVenue_Returns404()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var response = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{Guid.NewGuid()}/seat-maps",
            new CreateSeatMapRequest([new SeatRequest("A", "1")]));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- 查詢場地列表／明細／座位圖明細 ----

    [Fact]
    public async Task GetVenues_AsAdmin_ReturnsOk()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var venueId = await CreateVenueAsync(adminClient, "GetVenues Test Venue");

        var response = await adminClient.GetAsync("/api/admin/venues");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var venues = await response.Content.ReadFromJsonAsync<List<VenueSummaryDto>>();
        venues.Should().Contain(v => v.Id == venueId && v.Name == "GetVenues Test Venue");
    }

    [Fact]
    public async Task GetVenueById_AsAdmin_ReturnsVenueWithSeatMaps()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var venueId = await CreateVenueAsync(adminClient, "GetVenueById Test Venue");
        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps",
            new CreateSeatMapRequest([new SeatRequest("A", "1"), new SeatRequest("A", "2")]));
        var seatMapId = (await seatMapResponse.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;

        var response = await adminClient.GetAsync($"/api/admin/venues/{venueId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await response.Content.ReadFromJsonAsync<VenueDetailDto>();
        detail!.Id.Should().Be(venueId);
        detail.SeatMaps.Should().ContainSingle(s => s.Id == seatMapId && s.SeatCount == 2);
    }

    [Fact]
    public async Task GetVenueById_WithNonExistentVenue_Returns404()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var response = await adminClient.GetAsync($"/api/admin/venues/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetSeatMapById_AsAdmin_ReturnsSeats()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var venueId = await CreateVenueAsync(adminClient, "GetSeatMapById Test Venue");
        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps",
            new CreateSeatMapRequest([new SeatRequest("A", "1")]));
        var seatMapId = (await seatMapResponse.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;

        var response = await adminClient.GetAsync($"/api/admin/venues/{venueId}/seat-maps/{seatMapId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await response.Content.ReadFromJsonAsync<SeatMapDetailDto>();
        detail!.Id.Should().Be(seatMapId);
        detail.Seats.Should().ContainSingle(s => s.ZoneCode == "A" && s.SeatNumber == "1");
    }

    [Fact]
    public async Task GetSeatMapById_WithSeatMapBelongingToAnotherVenue_Returns404()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var venueId = await CreateVenueAsync(adminClient, "GetSeatMapById Owning Venue");
        var otherVenueId = await CreateVenueAsync(adminClient, "GetSeatMapById Other Venue");
        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps",
            new CreateSeatMapRequest([new SeatRequest("A", "1")]));
        var seatMapId = (await seatMapResponse.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;

        var response = await adminClient.GetAsync($"/api/admin/venues/{otherVenueId}/seat-maps/{seatMapId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetVenues_AsNonAdminMember_Returns403()
    {
        var email = AuthTestHelper.NewEmail();
        var client = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync("/api/admin/venues");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetVenues_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/admin/venues");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
