namespace ProjectC.Domain.Orders;

/// <summary>核銷與查詢持票人在持鎖期間需要的歸屬資訊，以單一投影取得以縮短鎖持有時間。</summary>
public sealed record RedemptionContext(Guid OrganizerId, bool IsRealNameRequired, Guid BuyerId);
