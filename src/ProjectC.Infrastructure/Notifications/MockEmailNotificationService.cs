using Microsoft.Extensions.Logging;
using ProjectC.Domain.Notifications;

namespace ProjectC.Infrastructure.Notifications;

/// <summary>
/// 比照 <see cref="ProjectC.Infrastructure.Payments.MockPaymentGateway"/> 的作法，用結構化 log 記錄
/// 「原本會寄出的通知內容」，不架設真實 SMTP server、不具備真實寄信能力（見 email-notification design.md）。
/// </summary>
public sealed class MockEmailNotificationService : IEmailNotificationService
{
    private readonly MockEmailNotificationServiceOptions _options;
    private readonly ILogger<MockEmailNotificationService> _logger;

    public MockEmailNotificationService(MockEmailNotificationServiceOptions options, ILogger<MockEmailNotificationService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task NotifyTicketsIssuedAsync(string toEmail, string eventTitle, Guid orderId, int ticketCount, CancellationToken cancellationToken)
    {
        if (!_options.AlwaysSucceed)
        {
            // 例外訊息 MUST NOT 包含 toEmail 或任何 Email 相關內容——呼叫端（OrderService）會直接記錄
            // 整個例外物件，不會另外遮蔽例外訊息內容（見 design.md 決策 5 第三輪外部審查段落、
            // IEmailNotificationService 的介面契約）。
            throw new InvalidOperationException("Simulated email delivery failure.");
        }

        _logger.LogInformation(
            "Ticket-issued notification would be sent to {ToEmail} for event {EventTitle}, order {OrderId}, {TicketCount} ticket(s).",
            EmailMasker.Mask(toEmail), eventTitle, orderId, ticketCount);

        return Task.CompletedTask;
    }
}
