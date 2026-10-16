namespace ProjectC.Application.Orders.PlaceOrder;

/// <summary>
/// 建立訂單的系統上限，不論活動是否設定 <c>MaxTicketsPerOrder</c> 都適用；建立活動的限購值也不得超過它。
/// 放 Application 而非 Domain：這是 API 輸入的防濫用上限，不是 Event 的不變量（place-order-request-limits design.md 決策 1）。
/// </summary>
public static class PlaceOrderLimits
{
    public const int MaxTicketsPerOrder = 50;
}
