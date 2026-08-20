using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetEventSeats;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Domain.Tickets;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Admin;

public class AdminTicketsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AdminTicketsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    // 走完整的「建立活動 → 下單 → 確認付款」流程觸發真正出票，再直接從 DB 撈出建立的 Ticket——
    // 目前沒有票券查詢端點（買家訂單查詢屬於已知缺口，見 docs/project-scope.md 第 9 節），
    // 這是唯一能取得真實 Ticket ID 的方式，比照 AdminOrdersControllerTests 的種子資料手法。
    private async Task<Guid> SeedIssuedTicketAsync()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Admin Tickets Test Venue"));
        var venueId = await ReadCreatedIdAsync(venueResponse);
        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")]));
        var seatMapId = await ReadCreatedIdAsync(seatMapResponse);
        var eventResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/events", new CreateEventRequest("Admin Tickets Test Event", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
        var eventId = await ReadCreatedIdAsync(eventResponse);
        var ticketTypeResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types", new CreateTicketTypeRequest("A", 500m));
        var ticketTypeId = await ReadCreatedIdAsync(ticketTypeResponse);

        var publicClient = _factory.CreateClient();
        var seatsResponse = await publicClient.GetAsync($"/api/events/{eventId}/seats");
        var seats = await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        var eventSeatId = seats!.Single().EventSeatId;

        var buyerClient = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(buyerClient);
        buyerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var placeResponse = await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));
        var orderId = await ReadCreatedIdAsync(placeResponse);

        var confirmResponse = await buyerClient.PostAsync($"/api/orders/{orderId}/confirm", null);
        confirmResponse.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var orderItemIds = await dbContext.OrderItems.AsNoTracking()
            .Where(i => EF.Property<Guid>(i, "OrderId") == orderId)
            .Select(i => i.Id)
            .ToListAsync();
        var ticket = await dbContext.Tickets.AsNoTracking().SingleAsync(t => orderItemIds.Contains(t.OrderItemId));
        return ticket.Id;
    }

    private async Task<HttpClient> CreateAuthenticatedMemberClientAsync()
    {
        var client = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private static Task<HttpResponseMessage> RedeemAsync(HttpClient client, string ticketIdPathSegment)
        => client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/admin/tickets/{ticketIdPathSegment}/redeem"));

    [Fact]
    public async Task Redeem_AsAdminOnIssuedTicket_Returns204AndTransitionsTicketToRedeemed()
    {
        var ticketId = await SeedIssuedTicketAsync();
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var response = await RedeemAsync(adminClient, ticketId.ToString());

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ticket = await dbContext.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        ticket.Status.Should().Be(TicketStatus.Redeemed);
        ticket.RedeemedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Redeem_AsNonAdminMember_Returns403AndDoesNotChangeTicketStatus()
    {
        var ticketId = await SeedIssuedTicketAsync();
        var memberClient = await CreateAuthenticatedMemberClientAsync();

        var response = await RedeemAsync(memberClient, ticketId.ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ticket = await dbContext.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        ticket.Status.Should().Be(TicketStatus.Issued);
    }

    [Fact]
    public async Task Redeem_WithoutAuthentication_Returns401AndDoesNotChangeTicketStatus()
    {
        var ticketId = await SeedIssuedTicketAsync();
        var client = _factory.CreateClient();

        var response = await RedeemAsync(client, ticketId.ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ticket = await dbContext.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        ticket.Status.Should().Be(TicketStatus.Issued);
    }

    [Fact]
    public async Task Redeem_WithNonGuidPathSegment_Returns404()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var response = await RedeemAsync(adminClient, "not-a-guid");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
