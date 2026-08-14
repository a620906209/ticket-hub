namespace ProjectC.Domain.Authentication;

public class RefreshToken
{
    public Guid Id { get; private set; }
    public Guid MemberId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public RefreshTokenStatus Status { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public Guid? PreviousTokenId { get; private set; }

    private RefreshToken()
    {
    }

    public static RefreshToken Issue(Guid memberId, string tokenHash, DateTime expiresAt, Guid? previousTokenId = null)
    {
        return new RefreshToken
        {
            Id = Guid.NewGuid(),
            MemberId = memberId,
            TokenHash = tokenHash,
            Status = RefreshTokenStatus.Active,
            ExpiresAt = expiresAt,
            PreviousTokenId = previousTokenId,
        };
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
