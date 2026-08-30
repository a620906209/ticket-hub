using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Orders;

/// <summary>
/// post-commit 通知資料的防呆組裝器，不是一般領域規則驗證器：只負責「這幾筆重新查回來的資料夠不夠格
/// 組成一份通知內容」，不重新驗證任何業務規則（見 email-notification design.md 決策 2）。
/// </summary>
public static class TicketIssuedNotificationContentFactory
{
    public static TicketIssuedNotificationContent Create(Guid orderId, Order? order, Event? @event, Member? buyer)
    {
        if (order is null)
        {
            throw new InvalidOperationException($"Order '{orderId}' was not found when preparing ticket-issued notification.");
        }

        if (@event is null)
        {
            throw new InvalidOperationException($"Event '{order.EventId}' was not found when preparing ticket-issued notification for order '{orderId}'.");
        }

        if (buyer is null || string.IsNullOrWhiteSpace(buyer.Email))
        {
            throw new InvalidOperationException($"Buyer email is missing when preparing ticket-issued notification for order '{orderId}'.");
        }

        return new TicketIssuedNotificationContent(buyer.Email, @event.Title, orderId, order.Items.Sum(i => i.Quantity));
    }
}
