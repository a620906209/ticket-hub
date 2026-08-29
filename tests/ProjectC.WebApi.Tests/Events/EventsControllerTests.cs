using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ProjectC.Application.Events.GetEventSeats;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Events;

public class EventsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public EventsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    private async Task<Guid> SeedEventWithSeatAndTicketTypeAsync(string zoneCode = "A")
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Test Venue"));
        var venueId = await ReadCreatedIdAsync(venueResponse);

        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps",
            new CreateSeatMapRequest([new SeatRequest(zoneCode, "1")]));
        var seatMapId = await ReadCreatedIdAsync(seatMapResponse);

        var eventResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
        var eventId = await ReadCreatedIdAsync(eventResponse);

        await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new CreateTicketTypeRequest(zoneCode, 500m));

        return eventId;
    }

    [Fact]
    public async Task GetEvents_ReturnsCreatedEvent()
    {
        var eventId = await SeedEventWithSeatAndTicketTypeAsync();
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/events");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var events = await response.Content.ReadFromJsonAsync<List<EventDto>>();
        events.Should().Contain(e => e.Id == eventId);
        // TP-BROWSE-001：每筆活動附帶 IsQueueModeEnabled，新建立的活動預設關閉。
        events!.Single(e => e.Id == eventId).IsQueueModeEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task GetEventSeats_ReturnsSeatsWithZoneCodeAndAvailableStatus()
    {
        var eventId = await SeedEventWithSeatAndTicketTypeAsync(zoneCode: "A");
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/events/{eventId}/seats");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var seats = await response.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        seats.Should().ContainSingle(s => s.ZoneCode == "A" && s.Status == "Available");
    }

    [Fact]
    public async Task GetTicketTypes_ReturnsCreatedTicketType()
    {
        var eventId = await SeedEventWithSeatAndTicketTypeAsync(zoneCode: "A");
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/events/{eventId}/ticket-types");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var ticketTypes = await response.Content.ReadFromJsonAsync<List<TicketTypeDto>>();
        ticketTypes.Should().ContainSingle(t => t.ZoneCode == "A" && t.Price == 500m);
    }

    [Fact]
    public async Task GetEventSeats_WithNonExistentEvent_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/events/{Guid.NewGuid()}/seats");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetTicketTypes_WithNonExistentEvent_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/events/{Guid.NewGuid()}/ticket-types");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- 安全回歸測試：公開端點不得洩漏 Admin 專用的稽核/售票統計欄位（見 admin-event-audit-and-
    // sales-status design.md 決策 8）----

    [Fact]
    public async Task GetEvents_AsAnonymous_DoesNotExposeAdminOnlyFields()
    {
        await SeedEventWithSeatAndTicketTypeAsync();
        await SeedEventWithSeatAndTicketTypeAsync();
        var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.GetAsync("/api/events");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var events = document.RootElement.EnumerateArray().ToList();
        events.Should().HaveCountGreaterThanOrEqualTo(2);

        string[] adminOnlyFields =
        [
            "createdByMemberId", "createdByDisplayName", "createdAtUtc",
            "availableSeatCount", "heldSeatCount", "soldSeatCount",
        ];
        foreach (var eventElement in events)
        {
            foreach (var field in adminOnlyFields)
            {
                eventElement.TryGetProperty(field, out _).Should().BeFalse(
                    $"公開的 GET /api/events 不應該回傳 Admin 專用欄位 '{field}'");
            }
        }
    }
}
