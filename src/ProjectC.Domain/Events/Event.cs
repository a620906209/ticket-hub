using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;

namespace ProjectC.Domain.Events;

public sealed class Event
{
    public Guid Id { get; }
    public string Title { get; }
    public DateTime StartAtUtc { get; }
    public Guid VenueId { get; }
    public Guid SeatMapId { get; }
    public string? Description { get; }
    public string? PosterUrl { get; }
    public int? MaxTicketsPerOrder { get; }

    public Event(
        Guid id,
        string title,
        DateTime startAtUtc,
        Guid venueId,
        Guid seatMapId,
        string? description = null,
        string? posterUrl = null,
        int? maxTicketsPerOrder = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Event title is required.", nameof(title));
        if (startAtUtc == default)
            throw new ArgumentException("Event start time is required.", nameof(startAtUtc));
        if (venueId == Guid.Empty)
            throw new ArgumentException("Venue is required.", nameof(venueId));
        if (seatMapId == Guid.Empty)
            throw new ArgumentException("Seat map is required.", nameof(seatMapId));
        if (maxTicketsPerOrder is <= 0)
            throw new ArgumentException("Max tickets per order must be positive when set.", nameof(maxTicketsPerOrder));

        Id = id;
        Title = title;
        StartAtUtc = startAtUtc;
        VenueId = venueId;
        SeatMapId = seatMapId;
        Description = description;
        PosterUrl = posterUrl;
        MaxTicketsPerOrder = maxTicketsPerOrder;
    }

    /// <summary>為此活動的座位圖建立專屬 EventSeat 庫存；不會被儲存在 Event 上，呼叫端自行保存回傳結果。</summary>
    public IReadOnlyList<EventSeat> CreateEventSeats(SeatMap seatMap)
    {
        if (seatMap.Id != SeatMapId)
            throw new ArgumentException("Seat map does not belong to this event.", nameof(seatMap));

        return seatMap.Seats
            .Select(seat => new EventSeat(Guid.NewGuid(), Id, seat.Id))
            .ToList();
    }

    /// <summary>建立票種前核對 seatMap 確實是此活動使用的座位圖，避免用別的活動的座位圖建立票種。</summary>
    public TicketType CreateTicketType(string zoneCode, decimal price, SeatMap seatMap)
    {
        if (seatMap.Id != SeatMapId)
            throw new ArgumentException("Seat map does not belong to this event.", nameof(seatMap));

        return new TicketType(Guid.NewGuid(), Id, zoneCode, price, seatMap);
    }
}
