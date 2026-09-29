using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetEventSeats;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Tickets.RedeemTicket;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Domain.Tickets;
using ProjectC.Infrastructure.Persistence;

namespace ProjectC.WebApi.Tests.TestSupport;

/// <summary>
/// 訂單／核銷／銷售報表整合測試共用的種子資料：一律以傳入的 <paramref name="organizerClient"/>（已切換 Organizer）
/// 建立活動，使資料歸屬於該 Organizer——建立資料與呼叫被測端點 MUST 使用同一個 Organizer 身份，否則會被歸屬核對
/// 視同找不到（order-report-redemption-organizer-scoping tasks.md 4.1）。
/// </summary>
public static class OrganizerScopedTestData
{
    public sealed record SeededOrder(Guid OrderId, Guid EventId, HttpClient BuyerClient);

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    public static async Task<SeededOrder> SeedPendingOrderAsync(CustomWebApplicationFactory factory, HttpClient organizerClient)
    {
        var venueResponse = await organizerClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Organizer Scoped Test Venue"));
        var venueId = await ReadCreatedIdAsync(venueResponse);
        var seatMapResponse = await organizerClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")]));
        var seatMapId = await ReadCreatedIdAsync(seatMapResponse);
        var eventResponse = await organizerClient.PostAsJsonAsync(
            "/api/admin/events", new CreateEventRequest("Organizer Scoped Test Event", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
        var eventId = await ReadCreatedIdAsync(eventResponse);
        var ticketTypeResponse = await organizerClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types", new CreateTicketTypeRequest("A", 500m));
        var ticketTypeId = await ReadCreatedIdAsync(ticketTypeResponse);

        var seatsResponse = await factory.CreateClient().GetAsync($"/api/events/{eventId}/seats");
        var seats = await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        var eventSeatId = seats!.Single().EventSeatId;

        var buyerClient = await CreateAuthenticatedMemberClientAsync(factory);
        var placeResponse = await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));

        return new SeededOrder(await ReadCreatedIdAsync(placeResponse), eventId, buyerClient);
    }

    // 目前沒有票券查詢端點，走完整的「下單 → 確認付款」觸發真正出票後，直接從 DB 撈出建立的 Ticket。
    public static async Task<Guid> SeedIssuedTicketAsync(CustomWebApplicationFactory factory, HttpClient organizerClient)
    {
        var seeded = await SeedPendingOrderAsync(factory, organizerClient);
        var confirmResponse = await seeded.BuyerClient.PostAsync($"/api/orders/{seeded.OrderId}/confirm", null);
        confirmResponse.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var orderItemIds = await dbContext.OrderItems.AsNoTracking()
            .Where(i => EF.Property<Guid>(i, "OrderId") == seeded.OrderId)
            .Select(i => i.Id)
            .ToListAsync();
        var ticket = await dbContext.Tickets.AsNoTracking().SingleAsync(t => orderItemIds.Contains(t.OrderItemId));
        return ticket.Id;
    }

    /// <summary>已登入、未切換 Organizer、角色為一般 Member 的使用者。</summary>
    public static async Task<HttpClient> CreateAuthenticatedMemberClientAsync(CustomWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    /// <summary>每次以新的 DI scope（新的 DbContext）讀取，確保讀到的是資料庫目前狀態而非追蹤中的快取實體。</summary>
    public static async Task<Ticket> ReadTicketAsync(CustomWebApplicationFactory factory, Guid ticketId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await dbContext.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
    }

    public static string SignTicket(CustomWebApplicationFactory factory, Guid ticketId)
    {
        using var scope = factory.Services.CreateScope();
        var signingService = scope.ServiceProvider.GetRequiredService<ITicketSigningService>();
        var signedContent = signingService.Sign(ticketId);
        return signedContent[(signedContent.IndexOf('.') + 1)..];
    }

    public static Task<HttpResponseMessage> RedeemAsync(HttpClient client, Guid ticketId, string? signature)
        => client.PatchAsJsonAsync($"/api/admin/tickets/{ticketId}/redeem", new RedeemTicketRequest(signature));
}
