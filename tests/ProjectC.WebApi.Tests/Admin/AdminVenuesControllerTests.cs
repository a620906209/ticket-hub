using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
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
}
