namespace ProjectC.Infrastructure.PurchaseQueue;

/// <summary>
/// 入場推進 Redis key 命名規則（purchase-queue-redis-admission design.md Decision 1／9／10）。
/// 沿用既有慣例：單一 DB（index 0）+ 專屬前綴隔離，不新增 DB index。
/// </summary>
public static class PurchaseQueueAdmissionRedisKeys
{
    private const string Prefix = "pq:admit:";

    public static string Waiting(Guid eventId) => $"{Prefix}{eventId}:waiting";

    public static string Admitted(Guid eventId) => $"{Prefix}{eventId}:admitted";

    public static string Pending(Guid entryId) => $"{Prefix}pending:{entryId}";

    // Advance Lua Script（design.md Decision 4）內部用來組出 pending key 的前綴，透過 ARGV 傳入，
    // 不在 Script 文字內字串拼接 eventId／entryId。
    public const string PendingKeyPrefix = Prefix + "pending:";
}
