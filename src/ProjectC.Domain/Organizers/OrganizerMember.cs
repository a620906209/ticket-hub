namespace ProjectC.Domain.Organizers;

public sealed class OrganizerMember
{
    public Guid Id { get; private set; }
    public Guid OrganizerId { get; private set; }
    public Guid MemberId { get; private set; }
    public OrganizerMemberRole Role { get; private set; }

    private OrganizerMember()
    {
    }

    public OrganizerMember(Guid id, Guid organizerId, Guid memberId, OrganizerMemberRole role)
    {
        if (organizerId == Guid.Empty)
            throw new ArgumentException("Organizer id is required.", nameof(organizerId));
        if (memberId == Guid.Empty)
            throw new ArgumentException("Member id is required.", nameof(memberId));

        Id = id;
        OrganizerId = organizerId;
        MemberId = memberId;
        Role = role;
    }
}
