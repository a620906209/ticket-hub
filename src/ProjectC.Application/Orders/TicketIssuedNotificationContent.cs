namespace ProjectC.Application.Orders;

public sealed record TicketIssuedNotificationContent(string ToEmail, string EventTitle, Guid OrderId, int TicketCount);
