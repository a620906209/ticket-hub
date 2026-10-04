using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetEventSeats;
using ProjectC.Application.Members;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.PurchaseQueue.JoinPurchaseQueue;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Infrastructure.Persistence;

namespace ProjectC.WebApi.Tests.TestSupport;

/// <summary>
/// real-name-verification 整合測試共用的種子資料。一律走真實 HTTP 端點建立（含 <c>PUT /api/members/me/real-name</c>），
/// 讓閘門讀到的是真正落地的資料，而不是測試直接寫 DB 繞過條件式 UPDATE 的結果。
/// </summary>
public static class RealNameTestData
{
    public sealed record SeededEvent(Guid EventId, Guid EventSeatId, Guid TicketTypeId);

    public sealed record SeededMember(HttpClient Client, Guid MemberId);

    public sealed record SeededTicket(Guid TicketId, Guid EventId, SeededMember Buyer);

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    // 呼叫端多以 DateTime.UtcNow 加減產生時間（含次微秒）；設定開賣時間時 API 會拒絕次微秒（EVT-SALES-017），
    // 而資料庫本來就只存到微秒，截斷不改變任何測試所依賴的時間關係。
    private static DateTime TruncateToMicroseconds(DateTime value)
        => new(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, value.Kind);

    public static async Task<SeededEvent> SeedEventAsync(
        WebApplicationFactory<Program> factory,
        HttpClient organizerClient,
        bool isRealNameRequired,
        bool isQueueModeEnabled = false,
        DateTime? startsAtUtc = null,
        DateTime? salesStartAtUtc = null,
        DateTime? salesEndAtUtc = null)
    {
        var venueResponse = await organizerClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Real Name Test Venue"));
        var venueId = await ReadCreatedIdAsync(venueResponse);
        var seatMapResponse = await organizerClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")]));
        var seatMapId = await ReadCreatedIdAsync(seatMapResponse);
        var eventResponse = await organizerClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest(
                "Real Name Test Event", TruncateToMicroseconds(startsAtUtc ?? DateTime.UtcNow.AddDays(30)), venueId, seatMapId,
                IsRealNameRequired: isRealNameRequired,
                SalesStartAtUtc: salesStartAtUtc is { } salesStart ? TruncateToMicroseconds(salesStart) : null,
                SalesEndAtUtc: salesEndAtUtc is { } salesEnd ? TruncateToMicroseconds(salesEnd) : null));
        var eventId = await ReadCreatedIdAsync(eventResponse);
        var ticketTypeResponse = await organizerClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types", new CreateTicketTypeRequest("A", 500m));
        var ticketTypeId = await ReadCreatedIdAsync(ticketTypeResponse);

        if (isQueueModeEnabled)
        {
            var patchResponse = await organizerClient.PatchAsJsonAsync($"/api/admin/events/{eventId}/queue-mode", new { enabled = true });
            patchResponse.EnsureSuccessStatusCode();
        }

        var seatsResponse = await factory.CreateClient().GetAsync($"/api/events/{eventId}/seats");
        var seats = await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        return new SeededEvent(eventId, seats!.Single().EventSeatId, ticketTypeId);
    }

    public static async Task<SeededMember> CreateMemberAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var profile = await client.GetFromJsonAsync<MemberProfileDto>("/api/members/me");
        return new SeededMember(client, profile!.Id);
    }

    public static async Task<SeededMember> CreateRegisteredMemberAsync(
        WebApplicationFactory<Program> factory, string realName = "王小明", string nationalIdLast4 = "1234")
    {
        var member = await CreateMemberAsync(factory);
        var response = await member.Client.PutAsJsonAsync("/api/members/me/real-name", new { realName, nationalIdLast4 });
        response.EnsureSuccessStatusCode();
        return member;
    }

    public static Task<HttpResponseMessage> PlaceOrderAsync(HttpClient buyerClient, SeededEvent seededEvent)
        => buyerClient.PostAsJsonAsync(
            "/api/orders",
            new PlaceOrderRequest([new PlaceOrderSelectionRequest(seededEvent.EventSeatId, seededEvent.TicketTypeId)]));

    public static async Task<string?> ReadProblemTitleAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null;
    }

    public static Task<HttpResponseMessage> JoinQueueAsync(HttpClient memberClient, Guid eventId)
        => memberClient.PostAsJsonAsync(
            $"/api/events/{eventId}/queue/entries",
            new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));

    /// <summary>需實名活動時買家先登記實名（下單閘門要求），再走「下單 → 確認付款」真正出票。</summary>
    public static async Task<SeededTicket> SeedIssuedTicketAsync(
        WebApplicationFactory<Program> factory,
        HttpClient organizerClient,
        bool isRealNameRequired,
        string realName = "王小明",
        string nationalIdLast4 = "1234",
        DateTime? startsAtUtc = null)
    {
        var seededEvent = await SeedEventAsync(factory, organizerClient, isRealNameRequired, startsAtUtc: startsAtUtc);
        var buyer = await CreateRegisteredMemberAsync(factory, realName, nationalIdLast4);
        var orderId = await ReadCreatedIdAsync(await PlaceOrderAsync(buyer.Client, seededEvent));
        var confirmResponse = await buyer.Client.PostAsync($"/api/orders/{orderId}/confirm", null);
        confirmResponse.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var orderItemIds = await dbContext.OrderItems.AsNoTracking()
            .Where(i => EF.Property<Guid>(i, "OrderId") == orderId)
            .Select(i => i.Id)
            .ToListAsync();
        var ticket = await dbContext.Tickets.AsNoTracking().SingleAsync(t => orderItemIds.Contains(t.OrderItemId));
        return new SeededTicket(ticket.Id, seededEvent.EventId, buyer);
    }
}
