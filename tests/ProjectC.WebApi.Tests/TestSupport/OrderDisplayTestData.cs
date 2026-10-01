using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetEventSeats;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Infrastructure.Persistence;

namespace ProjectC.WebApi.Tests.TestSupport;

/// <summary>
/// order-display-enrichment 整合測試共用的種子資料：一場活動含 A 區多個座位、A 區綁座位票種，以及多個不同名稱的
/// 計數票種（同一訂單的多個計數項目須對應不同票種）。一律透過真實 API 建立，確保 FK 與 Domain 驗證都走過一遍。
/// </summary>
public static class OrderDisplayTestData
{
    public sealed record SeededEvent(
        Guid EventId,
        string Title,
        IReadOnlyList<SeededSeat> Seats,
        Guid SeatTicketTypeId,
        IReadOnlyList<Guid> CountTicketTypeIds);

    public sealed record SeededSeat(Guid EventSeatId, Guid SeatId, string ZoneCode, string SeatNumber);

    public const string SeatTicketTypeZoneCode = "A";

    public static string CountTicketTypeName(int index) => $"站票{index + 1}";

    public static async Task<SeededEvent> SeedEventAsync(
        CustomWebApplicationFactory factory, HttpClient organizerClient, string title, int seatCount, int countTicketTypeCount)
    {
        var venueId = await ReadCreatedIdAsync(await organizerClient.PostAsJsonAsync(
            "/api/admin/venues", new CreateVenueRequest($"Display Test Venue {Guid.NewGuid():N}")));
        var seatRequests = Enumerable.Range(1, seatCount).Select(i => new SeatRequest(SeatTicketTypeZoneCode, $"{i}")).ToList();
        var seatMapId = await ReadCreatedIdAsync(await organizerClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest(seatRequests)));
        var eventId = await ReadCreatedIdAsync(await organizerClient.PostAsJsonAsync(
            "/api/admin/events", new CreateEventRequest(title, DateTime.UtcNow.AddDays(30), venueId, seatMapId)));
        var seatTicketTypeId = await ReadCreatedIdAsync(await organizerClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types", new CreateTicketTypeRequest(SeatTicketTypeZoneCode, 500m)));

        var countTicketTypeIds = new List<Guid>();
        for (var i = 0; i < countTicketTypeCount; i++)
        {
            countTicketTypeIds.Add(await ReadCreatedIdAsync(await organizerClient.PostAsJsonAsync(
                $"/api/admin/events/{eventId}/ticket-types",
                new CreateTicketTypeRequest(CountTicketTypeName(i), 300m, RequiresSeat: false, AvailableQuantity: 100))));
        }

        var seatsResponse = await factory.CreateClient().GetAsync($"/api/events/{eventId}/seats");
        seatsResponse.EnsureSuccessStatusCode();
        var seatDtos = (await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>())!;
        // 公開座位端點不回傳座位範本 Id，注入「座位範本查不到」時需要它，改從資料庫讀取。
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventSeatIds = seatDtos.Select(s => s.EventSeatId).ToList();
        var seatIdByEventSeatId = await dbContext.EventSeats.AsNoTracking()
            .Where(es => eventSeatIds.Contains(es.Id))
            .ToDictionaryAsync(es => es.Id, es => es.SeatId);
        var seats = seatDtos
            .OrderBy(s => int.Parse(s.SeatNumber))
            .Select(s => new SeededSeat(s.EventSeatId, seatIdByEventSeatId[s.EventSeatId], s.ZoneCode, s.SeatNumber))
            .ToList();

        return new SeededEvent(eventId, title, seats, seatTicketTypeId, countTicketTypeIds);
    }

    /// <summary>已登入、未切換 Organizer 的買家；<paramref name="displayName"/> 供後台列表的買家名稱斷言使用。</summary>
    public static async Task<(HttpClient Client, string Email)> CreateBuyerClientAsync(CustomWebApplicationFactory factory, string displayName = "Test User")
    {
        var client = factory.CreateClient();
        var email = AuthTestHelper.NewEmail();
        await AuthTestHelper.RegisterAsync(client, email, displayName: displayName);
        var tokens = await AuthTestHelper.LoginAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return (client, email);
    }

    /// <summary>以前 <paramref name="seatItemCount"/> 個座位與前 <paramref name="countItemCount"/> 個計數票種下單，
    /// 座位從 <paramref name="firstSeatIndex"/> 開始取，讓同一場活動可建立多筆互不衝突的訂單。</summary>
    public static async Task<Guid> PlaceOrderAsync(
        HttpClient buyerClient, SeededEvent seededEvent, int seatItemCount, int countItemCount, int firstSeatIndex = 0)
    {
        var selections = seededEvent.Seats.Skip(firstSeatIndex).Take(seatItemCount)
            .Select(seat => new PlaceOrderSelectionRequest(seat.EventSeatId, seededEvent.SeatTicketTypeId))
            .Concat(seededEvent.CountTicketTypeIds.Take(countItemCount)
                .Select(ticketTypeId => new PlaceOrderSelectionRequest(null, ticketTypeId, 1)))
            .ToList();

        return await ReadCreatedIdAsync(await buyerClient.PostAsJsonAsync("/api/orders", new PlaceOrderRequest(selections)));
    }

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }
}
