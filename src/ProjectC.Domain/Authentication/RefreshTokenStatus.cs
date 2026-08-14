namespace ProjectC.Domain.Authentication;

public enum RefreshTokenStatus
{
    Active = 0,
    Used = 1,
    Revoked = 2,
}
