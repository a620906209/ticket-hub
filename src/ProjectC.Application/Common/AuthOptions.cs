namespace ProjectC.Application.Common;

public sealed class AuthOptions
{
    public int RefreshTokenExpirationDays { get; set; } = 14;
    public int PasswordResetTokenExpirationMinutes { get; set; } = 15;
}
