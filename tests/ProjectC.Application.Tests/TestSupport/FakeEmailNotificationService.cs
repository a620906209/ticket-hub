using ProjectC.Domain.Notifications;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeEmailNotificationService : IEmailNotificationService
{
    public Task NotifyTicketsIssuedAsync(string toEmail, string eventTitle, Guid orderId, int ticketCount, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
