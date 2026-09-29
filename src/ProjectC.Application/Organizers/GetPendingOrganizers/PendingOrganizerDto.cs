namespace ProjectC.Application.Organizers.GetPendingOrganizers;

public sealed record PendingOrganizerDto(Guid Id, string Name, Guid CreatedByMemberId, string CreatedByDisplayName, DateTime CreatedAtUtc);
