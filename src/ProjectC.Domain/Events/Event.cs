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
    public Guid OrganizerId { get; }
    public string? Description { get; }
    public string? PosterUrl { get; }
    public int? MaxTicketsPerOrder { get; }
    public Guid? CreatedByMemberId { get; }
    public DateTime? CreatedAtUtc { get; }
    public bool IsQueueModeEnabled { get; private set; }

    /// <summary>建構時指定、之後不可變（不變量 I1）；下單閘門依此只在交易外讀一次，
    /// 若未來新增變更方法，須改以交易內鎖定讀取為權威（real-name-verification design.md 決策 3）。</summary>
    public bool IsRealNameRequired { get; }

    /// <summary>開賣時間；null 表示不設開賣下界。建構時指定、之後不可變（event-sales-window design.md 決策 1）。</summary>
    public DateTime? SalesStartAtUtc { get; }

    /// <summary>停售時間；null 表示停售時間沿用 <see cref="StartAtUtc"/>。建構時指定、之後不可變。</summary>
    public DateTime? SalesEndAtUtc { get; }

    public Event(
        Guid id,
        string title,
        DateTime startAtUtc,
        Guid venueId,
        Guid seatMapId,
        Guid organizerId,
        string? description = null,
        string? posterUrl = null,
        int? maxTicketsPerOrder = null,
        Guid? createdByMemberId = null,
        DateTime? createdAtUtc = null,
        bool isRealNameRequired = false,
        DateTime? salesStartAtUtc = null,
        DateTime? salesEndAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Event title is required.", nameof(title));
        if (startAtUtc == default)
            throw new ArgumentException("Event start time is required.", nameof(startAtUtc));
        if (venueId == Guid.Empty)
            throw new ArgumentException("Venue is required.", nameof(venueId));
        if (seatMapId == Guid.Empty)
            throw new ArgumentException("Seat map is required.", nameof(seatMapId));
        if (organizerId == Guid.Empty)
            throw new ArgumentException("Organizer is required.", nameof(organizerId));
        if (maxTicketsPerOrder is <= 0)
            throw new ArgumentException("Max tickets per order must be positive when set.", nameof(maxTicketsPerOrder));
        if (salesStartAtUtc is { Kind: not DateTimeKind.Utc })
            throw new ArgumentException("Sales start time must be UTC.", nameof(salesStartAtUtc));
        if (salesEndAtUtc is { Kind: not DateTimeKind.Utc })
            throw new ArgumentException("Sales end time must be UTC.", nameof(salesEndAtUtc));
        if (salesEndAtUtc > startAtUtc)
            throw new ArgumentException("Sales end time must not be after event start time.", nameof(salesEndAtUtc));
        if (salesStartAtUtc >= (salesEndAtUtc ?? startAtUtc))
            throw new ArgumentException("Sales start time must be before the effective sales end time.", nameof(salesStartAtUtc));

        Id = id;
        Title = title;
        StartAtUtc = startAtUtc;
        VenueId = venueId;
        SeatMapId = seatMapId;
        OrganizerId = organizerId;
        Description = description;
        PosterUrl = posterUrl;
        MaxTicketsPerOrder = maxTicketsPerOrder;
        CreatedByMemberId = createdByMemberId;
        CreatedAtUtc = createdAtUtc;
        IsRealNameRequired = isRealNameRequired;
        SalesStartAtUtc = salesStartAtUtc;
        SalesEndAtUtc = salesEndAtUtc;
    }

    /// <summary>依左閉右開區間 [SalesStartAtUtc ?? -∞, SalesEndAtUtc ?? StartAtUtc) 判斷 nowUtc 的販售狀態。</summary>
    public EventSalesStatus GetSalesStatus(DateTime nowUtc)
    {
        if (nowUtc < SalesStartAtUtc)
            return EventSalesStatus.NotOpen;
        if (nowUtc >= (SalesEndAtUtc ?? StartAtUtc))
            return EventSalesStatus.Closed;
        return EventSalesStatus.Open;
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

    /// <summary>建立綁座位票種前核對 seatMap 確實是此活動使用的座位圖，避免用別的活動的座位圖建立票種。</summary>
    public TicketType CreateTicketType(string zoneCode, decimal price, SeatMap seatMap)
    {
        if (seatMap.Id != SeatMapId)
            throw new ArgumentException("Seat map does not belong to this event.", nameof(seatMap));

        return new TicketType(Guid.NewGuid(), Id, zoneCode, price, seatMap);
    }

    /// <summary>建立純計數（不綁座位）票種，不需要座位圖，庫存以 availableQuantity 為初始可售總量。</summary>
    public TicketType CreateCountBasedTicketType(string zoneCode, decimal price, int availableQuantity)
        => new(Guid.NewGuid(), Id, zoneCode, price, availableQuantity);

    public void EnableQueueMode() => IsQueueModeEnabled = true;

    public void DisableQueueMode() => IsQueueModeEnabled = false;
}
