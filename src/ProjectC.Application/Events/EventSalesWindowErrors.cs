using ProjectC.Application.Common;
using ProjectC.Domain.Events;

namespace ProjectC.Application.Events;

/// <summary>
/// 下單與加入排隊共用的販售期間判斷（event-sales-window design.md 決策 3／4／5），
/// 兩處交易外快速失敗與交易內權威檢查都用同一份對應，避免錯誤類型或訊息分歧。
/// </summary>
public static class EventSalesWindowErrors
{
    /// <summary>活動於 <paramref name="nowUtc"/> 不在販售期間內時回傳對應錯誤；可售時回傳 null。訊息只含活動 Id。</summary>
    public static Error? GetErrorOrNull(Event @event, DateTime nowUtc) => @event.GetSalesStatus(nowUtc) switch
    {
        EventSalesStatus.Open => null,
        EventSalesStatus.NotOpen => Error.SalesNotOpen($"Event '{@event.Id}' sales have not started."),
        EventSalesStatus.Closed => Error.SalesClosed($"Event '{@event.Id}' sales have ended."),
        var status => throw new InvalidOperationException($"Unhandled EventSalesStatus '{status}'."),
    };
}
