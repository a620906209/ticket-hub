namespace ProjectC.Domain.Authentication;

public class PasswordResetToken
{
    public Guid Id { get; private set; }
    public Guid MemberId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTime ExpiresAt { get; private set; }
    public bool IsUsed { get; private set; }

    private PasswordResetToken()
    {
    }

    public static PasswordResetToken Issue(Guid memberId, string tokenHash, DateTime expiresAt)
    {
        return new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            MemberId = memberId,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt,
            IsUsed = false,
        };
    }

    public bool IsValid(DateTime nowUtc) => !IsUsed && ExpiresAt > nowUtc;

    public void MarkAsUsed()
    {
        if (IsUsed)
        {
            throw new InvalidOperationException($"Password reset token {Id} 已經被使用過。");
        }

        IsUsed = true;
    }
}
