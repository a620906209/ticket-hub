namespace ProjectC.Domain.Venues;

public interface ISeatMapRepository
{
    /// <summary>實作 MUST 一併載入 <see cref="SeatMap.Seats"/>；Domain 邏輯（如 <c>Event.CreateEventSeats</c>）假設這個集合已完整。</summary>
    Task<SeatMap?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>實作 MUST 一併載入 <see cref="SeatMap.Seats"/>，供 Admin 場地明細組出座位圖摘要（座位總數）使用；不保證回傳順序。</summary>
    Task<IReadOnlyList<SeatMap>> GetByVenueIdAsync(Guid venueId, CancellationToken cancellationToken);

    /// <summary>唯讀批次查詢（no-tracking）座位範本，只讀取指定的座位、不載入 <see cref="SeatMap"/>——
    /// 供訂單明細取分區與號碼，資料量只與訂單大小有關、不隨場館座位數成長（order-display-enrichment design.md 決策 1）。
    /// 查不到的 Id 不回傳也不拋例外，比對缺失是呼叫端的責任；<paramref name="seatIds"/> 為空清單時
    /// MUST 直接回傳空清單，不得執行任何資料庫查詢。</summary>
    Task<IReadOnlyList<Seat>> GetSeatsByIdsAsync(IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken);

    void Add(SeatMap seatMap);
}
