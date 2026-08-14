namespace ProjectC.Domain.Venues;

public sealed class Seat
{
    public Guid Id { get; }
    public Guid SeatMapId { get; }
    public string ZoneCode { get; }
    public string SeatNumber { get; }

    internal Seat(Guid id, Guid seatMapId, string zoneCode, string seatNumber)
    {
        Id = id;
        SeatMapId = seatMapId;
        ZoneCode = zoneCode;
        SeatNumber = seatNumber;
    }
}
