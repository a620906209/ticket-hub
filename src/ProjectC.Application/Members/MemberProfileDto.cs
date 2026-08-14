namespace ProjectC.Application.Members;

public sealed record MemberProfileDto(Guid Id, string Email, string DisplayName, string Role, bool IsActive);
