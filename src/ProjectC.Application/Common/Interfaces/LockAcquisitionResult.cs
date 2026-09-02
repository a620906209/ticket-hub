namespace ProjectC.Application.Common.Interfaces;

public enum LockResult
{
    Acquired,
    HeldByOther,
    RedisUnavailable,
}

// OwnerToken 僅 Acquired 時有值，供後續 ReleaseAsync 使用（design.md 決策 2）。
public sealed record LockAcquisitionResult(LockResult LockResult, string? OwnerToken);
