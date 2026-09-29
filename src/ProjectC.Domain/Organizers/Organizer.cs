namespace ProjectC.Domain.Organizers;

public sealed class Organizer
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public OrganizerStatus Status { get; private set; }
    public Guid CreatedByMemberId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public Guid? ReviewedByMemberId { get; private set; }
    public DateTime? ReviewedAtUtc { get; private set; }

    private Organizer()
    {
    }

    public static Organizer Apply(Guid id, string name, Guid createdByMemberId, DateTime createdAtUtc)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length is < 1 or > 100)
            throw new ArgumentException("Organizer name must be between 1 and 100 characters after trimming.", nameof(name));

        return new Organizer
        {
            Id = id,
            Name = trimmedName,
            Status = OrganizerStatus.Pending,
            CreatedByMemberId = createdByMemberId,
            CreatedAtUtc = createdAtUtc,
        };
    }

    public void Approve(Guid reviewedByMemberId, DateTime reviewedAtUtc)
    {
        if (Status != OrganizerStatus.Pending)
        {
            throw new InvalidOperationException($"Organizer {Id} 目前狀態為 {Status}，無法核准。");
        }

        Status = OrganizerStatus.Approved;
        ReviewedByMemberId = reviewedByMemberId;
        ReviewedAtUtc = reviewedAtUtc;
    }

    public void Reject(Guid reviewedByMemberId, DateTime reviewedAtUtc)
    {
        if (Status != OrganizerStatus.Pending)
        {
            throw new InvalidOperationException($"Organizer {Id} 目前狀態為 {Status}，無法駁回。");
        }

        Status = OrganizerStatus.Rejected;
        ReviewedByMemberId = reviewedByMemberId;
        ReviewedAtUtc = reviewedAtUtc;
    }

    public void Suspend()
    {
        if (Status != OrganizerStatus.Approved)
        {
            throw new InvalidOperationException($"Organizer {Id} 目前狀態為 {Status}，無法停權。");
        }

        Status = OrganizerStatus.Suspended;
    }
}
