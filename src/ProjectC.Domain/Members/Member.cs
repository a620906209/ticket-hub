namespace ProjectC.Domain.Members;

public class Member
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public MemberRole Role { get; private set; }
    public bool IsActive { get; private set; }

    private Member()
    {
    }

    public static Member Register(string email, string displayName, string passwordHash)
    {
        return new Member
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = displayName,
            PasswordHash = passwordHash,
            Role = MemberRole.Member,
            IsActive = true,
        };
    }

    public void ChangeDisplayName(string displayName)
    {
        DisplayName = displayName;
    }

    public void ChangePasswordHash(string passwordHash)
    {
        PasswordHash = passwordHash;
    }

    public void Activate()
    {
        IsActive = true;
    }

    public void Deactivate()
    {
        IsActive = false;
    }
}
