namespace ProjectC.Domain.Venues;

public sealed class Venue
{
    public Guid Id { get; }
    public string Name { get; }

    public Venue(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Venue name is required.", nameof(name));

        Id = id;
        Name = name;
    }
}
