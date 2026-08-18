namespace ProjectC.Domain.Venues;

public interface ISeatMapRepository
{
    /// <summary>實作 MUST 一併載入 <see cref="SeatMap.Seats"/>；Domain 邏輯（如 <c>Event.CreateEventSeats</c>）假設這個集合已完整。</summary>
    Task<SeatMap?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>實作 MUST 一併載入 <see cref="SeatMap.Seats"/>，供 Admin 場地明細組出座位圖摘要（座位總數）使用；不保證回傳順序。</summary>
    Task<IReadOnlyList<SeatMap>> GetByVenueIdAsync(Guid venueId, CancellationToken cancellationToken);

    void Add(SeatMap seatMap);
}
