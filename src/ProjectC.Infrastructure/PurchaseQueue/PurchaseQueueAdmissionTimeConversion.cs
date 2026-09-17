namespace ProjectC.Infrastructure.PurchaseQueue;

/// <summary>
/// Redis Sorted Set score（IEEE754 double）與 UTC DateTime 之間的 Unix 毫秒轉換
/// （purchase-queue-redis-admission design.md Decision 2：現在時間戳約 1.7×10^12，
/// 遠低於 double 53 bit 精度上限，無精度疑慮）。
/// </summary>
public static class PurchaseQueueAdmissionTimeConversion
{
    public static double ToUnixMilliseconds(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public static DateTime FromUnixMilliseconds(double unixMilliseconds) => DateTimeOffset.FromUnixTimeMilliseconds((long)unixMilliseconds).UtcDateTime;
}
