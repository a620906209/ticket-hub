using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Events;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;

namespace ProjectC.Infrastructure.Tests.TestSupport;

/// <summary>建立測試用的售票資料（跟被測試的鎖定/交易邏輯無關，直接用 DbContext 存檔，不透過 Repository/UnitOfWork）。</summary>
public static class TicketingTestData
{
    public static async Task<(Guid EventId, List<Guid> EventSeatIds)> SeedEventWithSeatsAsync(
        ApplicationDbContext dbContext, int seatCount, CancellationToken ct = default)
    {
        var venue = new Venue(Guid.NewGuid(), $"Test Venue {Guid.NewGuid():N}");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        for (var i = 0; i < seatCount; i++)
            seatMap.AddSeat("A", $"{i + 1}");

        var @event = new Event(Guid.NewGuid(), "Test Event", DateTime.UtcNow.AddDays(30), venue.Id, seatMap.Id);
        var eventSeats = @event.CreateEventSeats(seatMap);

        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        dbContext.Events.Add(@event);
        dbContext.EventSeats.AddRange(eventSeats);

        await dbContext.SaveChangesAsync(ct);

        return (@event.Id, eventSeats.Select(es => es.Id).ToList());
    }

    /// <summary>幫 <see cref="SeedEventWithSeatsAsync"/> 建立的活動（座位一律在 "A" 分區）補一個對應的
    /// 綁座位 TicketType——OrderItem.TicketTypeId 是 nullable FK，非 null 值仍須指向真實存在的列。</summary>
    public static async Task<Guid> SeedTicketTypeAsync(
        ApplicationDbContext dbContext, Guid eventId, decimal price = 500m, CancellationToken ct = default)
    {
        var @event = await dbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId, ct);
        var seatMap = await dbContext.SeatMaps.AsNoTracking().Include(s => s.Seats).SingleAsync(s => s.Id == @event.SeatMapId, ct);

        var ticketType = @event.CreateTicketType("A", price, seatMap);
        dbContext.TicketTypes.Add(ticketType);
        await dbContext.SaveChangesAsync(ct);

        return ticketType.Id;
    }

    public static async Task<EventSeat> ReloadAsync(ApplicationDbContext dbContext, Guid eventSeatId, CancellationToken ct = default)
        => await dbContext.EventSeats.AsNoTracking().SingleAsync(es => es.Id == eventSeatId, ct);

    /// <summary>建立純計數（不綁座位）票種，不需要座位圖，供不需要座位細節的測試情境使用。</summary>
    public static async Task<Guid> SeedCountBasedTicketTypeAsync(
        ApplicationDbContext dbContext, Guid eventId, decimal price = 300m, int availableQuantity = 100, CancellationToken ct = default)
    {
        var @event = await dbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId, ct);
        var ticketType = @event.CreateCountBasedTicketType("VIP", price, availableQuantity);
        dbContext.TicketTypes.Add(ticketType);
        await dbContext.SaveChangesAsync(ct);

        return ticketType.Id;
    }
}
