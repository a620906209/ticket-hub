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
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);

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

    // [EVT-LIST-005] event-management-organizer-scoping 起 organizerId 也屬後台專用欄位，公開端點同樣不得回傳。
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
            "organizerId",
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

    // [TP-BROWSE-RN-001] 未登入的瀏覽者也要能在下單前得知活動需實名。
    [Fact]
    public async Task GetEvents_AsAnonymous_ReturnsIsRealNameRequiredPerEvent()
    {
        var organizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var realNameEvent = await RealNameTestData.SeedEventAsync(_factory, organizerClient, isRealNameRequired: true);
        var plainEvent = await RealNameTestData.SeedEventAsync(_factory, organizerClient, isRealNameRequired: false);

        var events = await _factory.CreateClient().GetFromJsonAsync<List<EventDto>>("/api/events");

        events!.Single(e => e.Id == realNameEvent.EventId).IsRealNameRequired.Should().BeTrue();
        events!.Single(e => e.Id == plainEvent.EventId).IsRealNameRequired.Should().BeFalse();
    }

    // TP-BROWSE-SALES-001：前台只帶原始值，販售狀態由前端依時間自行推導；屬性名集合鎖定，避免伺服器端推導的狀態欄位
    // 被快取 TTL 凍結而與實際可否購買不一致（event-sales-window design.md 決策 6）。
    [Fact]
    public async Task GetEvents_WithAndWithoutSalesWindow_ReturnsRawValuesWithoutSalesStatusField()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var venueId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Test Venue")));
        var seatMapId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")])));
        var startAtUtc = DateTime.UtcNow.Date.AddDays(30);
        var salesStartAtUtc = DateTime.UtcNow.Date.AddDays(1);
        var salesEndAtUtc = DateTime.UtcNow.Date.AddDays(20);
        var withWindowId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync("/api/admin/events",
            new CreateEventRequest("With Window", startAtUtc, venueId, seatMapId, SalesStartAtUtc: salesStartAtUtc, SalesEndAtUtc: salesEndAtUtc)));
        var withoutWindowId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync("/api/admin/events",
            new CreateEventRequest("Without Window", startAtUtc, venueId, seatMapId)));

        var response = await _factory.CreateClient().GetAsync("/api/events");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var events = await response.Content.ReadFromJsonAsync<List<EventDto>>();
        var withWindow = events!.Single(e => e.Id == withWindowId);
        (withWindow.SalesStartAtUtc, withWindow.SalesEndAtUtc).Should().Be((salesStartAtUtc, salesEndAtUtc));
        var withoutWindow = events!.Single(e => e.Id == withoutWindowId);
        (withoutWindow.SalesStartAtUtc, withoutWindow.SalesEndAtUtc).Should().Be(((DateTime?)null, (DateTime?)null));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string[] expectedPropertyNames =
        [
            "id", "title", "startAtUtc", "venueId", "seatMapId", "description", "posterUrl",
            "maxTicketsPerOrder", "isQueueModeEnabled", "isRealNameRequired", "salesStartAtUtc", "salesEndAtUtc",
        ];
        foreach (var element in document.RootElement.EnumerateArray())
        {
            element.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(expectedPropertyNames);
        }
    }
}
