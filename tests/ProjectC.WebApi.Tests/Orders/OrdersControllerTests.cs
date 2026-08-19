using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using ProjectC.Application.Events.GetEventSeats;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Orders;

public class OrdersControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public OrdersControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    /// <summary>建立一場活動，含一個 A 區座位與對應票種，回傳 (EventId, EventSeatId, TicketTypeId)。</summary>
    private async Task<(Guid EventId, Guid EventSeatId, Guid TicketTypeId)> SeedEventWithSeatAndTicketTypeAsync(string zoneCode = "A")
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

        var ticketTypeResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new CreateTicketTypeRequest(zoneCode, 500m));
        var ticketTypeId = await ReadCreatedIdAsync(ticketTypeResponse);

        var publicClient = _factory.CreateClient();
        var seatsResponse = await publicClient.GetAsync($"/api/events/{eventId}/seats");
        var seats = await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        var eventSeatId = seats!.Single(s => s.ZoneCode == zoneCode).EventSeatId;

        return (eventId, eventSeatId, ticketTypeId);
    }

    private async Task<HttpClient> CreateAuthenticatedMemberClientAsync()
    {
        var client = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    // ---- 買家需登入 ----

    [Fact]
    public async Task PlaceOrder_WithoutAuthentication_Returns401()
    {
        var (_, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- 建立訂單 ----

    [Fact]
    public async Task PlaceOrder_WithAvailableSeatAndMatchingTicketType_ReturnsCreatedAndHoldsSeat()
    {
        var (eventId, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var buyerClient = await CreateAuthenticatedMemberClientAsync();

        var response = await buyerClient.PostAsJsonAsync(
            "/api/orders",
            new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var publicClient = _factory.CreateClient();
        var seatsResponse = await publicClient.GetAsync($"/api/events/{eventId}/seats");
        var seats = await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        seats!.Single(s => s.EventSeatId == eventSeatId).Status.Should().Be("Held");
    }

    [Fact]
    public async Task PlaceOrder_WithLegacyPayloadMissingQuantity_TreatsAsOneAndHoldsSeat()
    {
        // 外部審查第四輪抓到的阻斷問題：MUST 用匿名物件送出只有舊欄位的原始 JSON，
        // 用強型別 PlaceOrderSelectionRequest 物件建構測不出「欄位缺失」這個情境
        // （強型別物件永遠會序列化出 Quantity 的預設值，不是真的缺欄位）。
        var (eventId, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var buyerClient = await CreateAuthenticatedMemberClientAsync();

        var response = await buyerClient.PostAsJsonAsync(
            "/api/orders",
            new { Selections = new[] { new { EventSeatId = eventSeatId, TicketTypeId = ticketTypeId } } });

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "缺 Quantity 欄位的舊格式座位選購請求 MUST 視為購買數量 1，成功建立訂單");

        var publicClient = _factory.CreateClient();
        var seatsResponse = await publicClient.GetAsync($"/api/events/{eventId}/seats");
        var seats = await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        seats!.Single(s => s.EventSeatId == eventSeatId).Status.Should().Be("Held");
    }

    [Fact]
    public async Task PlaceOrder_WithNonExistentSeatOrTicketType_Returns404()
    {
        var (_, eventSeatId, _) = await SeedEventWithSeatAndTicketTypeAsync();
        var buyerClient = await CreateAuthenticatedMemberClientAsync();

        var response = await buyerClient.PostAsJsonAsync(
            "/api/orders",
            new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, Guid.NewGuid())]));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PlaceOrder_WithSeatAlreadyHeldByAnotherOrder_ReturnsConflict()
    {
        var (_, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var firstBuyer = await CreateAuthenticatedMemberClientAsync();
        var secondBuyer = await CreateAuthenticatedMemberClientAsync();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]);

        var firstResponse = await firstBuyer.PostAsJsonAsync("/api/orders", request);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var secondResponse = await secondBuyer.PostAsJsonAsync("/api/orders", request);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PlaceOrder_WithSeatAndTicketTypeFromDifferentEvents_Returns400()
    {
        var (_, eventSeatId, _) = await SeedEventWithSeatAndTicketTypeAsync(zoneCode: "A");
        var (_, _, otherEventTicketTypeId) = await SeedEventWithSeatAndTicketTypeAsync(zoneCode: "A");

        var buyerClient = await CreateAuthenticatedMemberClientAsync();
        var response = await buyerClient.PostAsJsonAsync(
            "/api/orders",
            new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, otherEventTicketTypeId)]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PlaceOrder_WithSeatZoneNotMatchingTicketTypeZoneWithinSameEvent_Returns400()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Two Zone Venue"));
        var venueId = await ReadCreatedIdAsync(venueResponse);
        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps",
            new CreateSeatMapRequest([new SeatRequest("A", "1"), new SeatRequest("B", "1")]));
        var seatMapId = await ReadCreatedIdAsync(seatMapResponse);
        var eventResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/events", new CreateEventRequest("Two Zone Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
        var eventId = await ReadCreatedIdAsync(eventResponse);
        var ticketTypeBResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types", new CreateTicketTypeRequest("B", 500m));
        var ticketTypeBId = await ReadCreatedIdAsync(ticketTypeBResponse);

        var publicClient = _factory.CreateClient();
        var seatsResponse = await publicClient.GetAsync($"/api/events/{eventId}/seats");
        var seats = await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        var seatAId = seats!.Single(s => s.ZoneCode == "A").EventSeatId;

        var buyerClient = await CreateAuthenticatedMemberClientAsync();
        var response = await buyerClient.PostAsJsonAsync(
            "/api/orders",
            new PlaceOrderRequest([new PlaceOrderSelectionRequest(seatAId, ticketTypeBId)]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- 確認訂單 ----

    [Fact]
    public async Task ConfirmOrder_ByBuyerOnOwnPendingOrder_Returns204AndSellsSeat()
    {
        var (eventId, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var buyerClient = await CreateAuthenticatedMemberClientAsync();
        var placeResponse = await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));
        var orderId = await ReadCreatedIdAsync(placeResponse);

        var response = await buyerClient.PostAsync($"/api/orders/{orderId}/confirm", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var publicClient = _factory.CreateClient();
        var seatsResponse = await publicClient.GetAsync($"/api/events/{eventId}/seats");
        var seats = await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        seats!.Single(s => s.EventSeatId == eventSeatId).Status.Should().Be("Sold");
    }

    [Fact]
    public async Task ConfirmOrder_ByNonBuyer_Returns403()
    {
        var (_, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var buyerClient = await CreateAuthenticatedMemberClientAsync();
        var otherClient = await CreateAuthenticatedMemberClientAsync();
        var placeResponse = await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));
        var orderId = await ReadCreatedIdAsync(placeResponse);

        var response = await otherClient.PostAsync($"/api/orders/{orderId}/confirm", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ConfirmOrder_WithNonExistentOrder_Returns404()
    {
        var buyerClient = await CreateAuthenticatedMemberClientAsync();

        var response = await buyerClient.PostAsync($"/api/orders/{Guid.NewGuid()}/confirm", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- 取消訂單 ----

    [Fact]
    public async Task CancelOrder_ByBuyerOnOwnPendingOrder_Returns204AndReleasesSeat()
    {
        var (eventId, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var buyerClient = await CreateAuthenticatedMemberClientAsync();
        var placeResponse = await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));
        var orderId = await ReadCreatedIdAsync(placeResponse);

        var response = await buyerClient.PostAsync($"/api/orders/{orderId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var publicClient = _factory.CreateClient();
        var seatsResponse = await publicClient.GetAsync($"/api/events/{eventId}/seats");
        var seats = await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        seats!.Single(s => s.EventSeatId == eventSeatId).Status.Should().Be("Available");
    }

    [Fact]
    public async Task CancelOrder_ByNonBuyer_Returns403()
    {
        var (_, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var buyerClient = await CreateAuthenticatedMemberClientAsync();
        var otherClient = await CreateAuthenticatedMemberClientAsync();
        var placeResponse = await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));
        var orderId = await ReadCreatedIdAsync(placeResponse);

        var response = await otherClient.PostAsync($"/api/orders/{orderId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CancelOrder_WithNonExistentOrder_Returns404()
    {
        var buyerClient = await CreateAuthenticatedMemberClientAsync();

        var response = await buyerClient.PostAsync($"/api/orders/{Guid.NewGuid()}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
