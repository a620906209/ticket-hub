namespace ProjectC.Domain.Venues;

public interface ISeatMapRepository
{
    /// <summary>實作 MUST 一併載入 <see cref="SeatMap.Seats"/>；Domain 邏輯（如 <c>Event.CreateEventSeats</c>）假設這個集合已完整。</summary>
    Task<SeatMap?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    void Add(SeatMap seatMap);
}
