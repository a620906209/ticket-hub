namespace ProjectC.Application.Venues.CreateSeatMap;

/// <summary>
/// 單一建立座位圖請求的資源邊界。用常數而非 IOptions：上限是安全預設、不是部署可調參數
/// （request-body-size-limits design.md 決策 7）。
/// </summary>
public static class SeatMapLimits
{
    /// <summary>50,000 席最長欄位約 6.5MB，落在 10MB 本文上限內，讓座位數而不是本文大小決定合法範圍。</summary>
    public const int MaxSeats = 50_000;
}
