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
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Tickets;

public class TicketsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public TicketsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetQrCode_WithoutAuthentication_Returns401()
    {
        var response = await _factory.CreateClient().GetAsync($"/api/tickets/{Guid.NewGuid()}/qr-code");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetQrCode_WhenBuyerOwnsIssuedTicket_ReturnsPngContent()
    {
        var (buyerClient, ticketId) = await SeedIssuedTicketAsync();

        var response = await buyerClient.GetAsync($"/api/tickets/{ticketId}/qr-code");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await response.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();
    }

    private async Task<(HttpClient BuyerClient, Guid TicketId)> SeedIssuedTicketAsync()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var venueId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Buyer Ticket Test Venue")));
        var seatMapId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")])));
        var eventId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync(
            "/api/admin/events", new CreateEventRequest("Buyer Ticket Test Event", DateTime.UtcNow.AddDays(30), venueId, seatMapId)));
        var ticketTypeId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types", new CreateTicketTypeRequest("A", 500m)));
        var publicClient = _factory.CreateClient();
        var seats = await publicClient.GetFromJsonAsync<List<EventSeatDto>>($"/api/events/{eventId}/seats");
        var buyerClient = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(buyerClient);
        buyerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var orderId = await ReadCreatedIdAsync(await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(seats!.Single().EventSeatId, ticketTypeId)])));
        (await buyerClient.PostAsync($"/api/orders/{orderId}/confirm", null)).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var orderItemIds = await dbContext.OrderItems.AsNoTracking()
            .Where(item => EF.Property<Guid>(item, "OrderId") == orderId)
            .Select(item => item.Id)
            .ToListAsync();
        var ticketId = await dbContext.Tickets.AsNoTracking()
            .Where(ticket => orderItemIds.Contains(ticket.OrderItemId))
            .Select(ticket => ticket.Id)
            .SingleAsync();

        return (buyerClient, ticketId);
    }

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }
}
