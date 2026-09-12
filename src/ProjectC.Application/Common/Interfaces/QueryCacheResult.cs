namespace ProjectC.Application.Common.Interfaces;

// Value 僅 IsHit 為 true 時有意義（比照 LockAcquisitionResult 的 OwnerToken 慣例）。
public sealed record QueryCacheResult<T>(bool IsHit, T? Value)
{
    public static QueryCacheResult<T> Hit(T value) => new(true, value);

    public static QueryCacheResult<T> Miss() => new(false, default);
}
