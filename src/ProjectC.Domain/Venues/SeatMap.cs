namespace ProjectC.Domain.Venues;

public sealed class SeatMap
{
    private readonly List<Seat> _seats = [];

    public Guid Id { get; }
    public Guid VenueId { get; }
    public IReadOnlyList<Seat> Seats => _seats;

    public SeatMap(Guid id, Guid venueId)
    {
        Id = id;
        VenueId = venueId;
    }

    public Seat AddSeat(string zoneCode, string seatNumber)
    {
        if (string.IsNullOrWhiteSpace(zoneCode))
            throw new ArgumentException("Zone code is required.", nameof(zoneCode));
        if (string.IsNullOrWhiteSpace(seatNumber))
            throw new ArgumentException("Seat number is required.", nameof(seatNumber));

        if (_seats.Any(s => s.ZoneCode == zoneCode && s.SeatNumber == seatNumber))
            throw new InvalidOperationException($"Seat '{zoneCode}-{seatNumber}' already exists in this seat map.");

        var seat = new Seat(Guid.NewGuid(), Id, zoneCode, seatNumber);
        _seats.Add(seat);
        return seat;
    }
}
