namespace ProjectC.Domain.Members;

public class Member
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public MemberRole Role { get; private set; }
    public bool IsActive { get; private set; }
    public string? RealName { get; private set; }
    public string? NationalIdLast4 { get; private set; }

    public bool HasRegisteredRealName => RealName != null;

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

    /// <summary>一次性登記實名；已登記時回傳 false 且不改變原值（登記後不可變更，防止改實名繞過實名轉賣）。
    /// 並發保證不在此處：落地由 <see cref="IMemberRealNameRepository.TryRegisterAsync"/> 的條件式更新負責。</summary>
    public bool RegisterRealName(string realName, string nationalIdLast4)
    {
        if (string.IsNullOrWhiteSpace(realName))
            throw new ArgumentException("Real name is required.", nameof(realName));
        if (string.IsNullOrWhiteSpace(nationalIdLast4))
            throw new ArgumentException("National id last 4 is required.", nameof(nationalIdLast4));

        if (HasRegisteredRealName)
            return false;

        RealName = realName;
        NationalIdLast4 = nationalIdLast4;
        return true;
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
