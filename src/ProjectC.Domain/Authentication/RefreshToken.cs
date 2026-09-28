namespace ProjectC.Domain.Authentication;

public class RefreshToken
{
    public Guid Id { get; private set; }
    public Guid MemberId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public RefreshTokenStatus Status { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public Guid? PreviousTokenId { get; private set; }
    public Guid? OrganizerId { get; private set; }

    private RefreshToken()
    {
    }

    public static RefreshToken Issue(Guid memberId, string tokenHash, DateTime expiresAt, Guid? previousTokenId = null, Guid? organizerId = null)
    {
        return new RefreshToken
        {
            Id = Guid.NewGuid(),
            MemberId = memberId,
            TokenHash = tokenHash,
            Status = RefreshTokenStatus.Active,
            ExpiresAt = expiresAt,
            PreviousTokenId = previousTokenId,
            OrganizerId = organizerId,
        };
    }

    /// <summary>切換操作情境的原地更新：只改 <see cref="OrganizerId"/>，不觸發 Token 輪替（見 organizer-management design.md 決策 1）。</summary>
    public void UpdateOrganizerContext(Guid organizerId)
    {
        OrganizerId = organizerId;
    }

    public bool IsActive(DateTime nowUtc) => Status == RefreshTokenStatus.Active && ExpiresAt > nowUtc;

    public void MarkAsUsed()
    {
        if (Status != RefreshTokenStatus.Active)
        {
            throw new InvalidOperationException($"Refresh token {Id} 目前狀態為 {Status}，無法標記為已使用。");
        }

        Status = RefreshTokenStatus.Used;
    }

    public void Revoke()
    {
        if (Status == RefreshTokenStatus.Revoked)
        {
            return;
        }

        Status = RefreshTokenStatus.Revoked;
    }
}
