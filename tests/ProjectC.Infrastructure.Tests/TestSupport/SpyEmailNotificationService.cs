using ProjectC.Domain.Notifications;

namespace ProjectC.Infrastructure.Tests.TestSupport;

public sealed record EmailNotificationCall(string ToEmail, string EventTitle, Guid OrderId, int TicketCount);

/// <summary>
/// <see cref="IEmailNotificationService"/> 的測試替身，記錄每次呼叫的參數，並提供
/// <see cref="ExceptionToThrow"/>／<see cref="OnNotifyAsync"/> 兩種方式模擬通知失敗
/// （見 email-notification tasks.md 5.2）。
/// </summary>
public sealed class SpyEmailNotificationService : IEmailNotificationService
{
    private readonly List<EmailNotificationCall> _calls = [];

    public IReadOnlyList<EmailNotificationCall> Calls => _calls;

    public Exception? ExceptionToThrow { get; set; }

    /// <summary>
    /// 每次呼叫時（記錄參數之後、<see cref="ExceptionToThrow"/> 判斷之前）若有設定就先 await 執行。
    /// 拋出的例外直接往外傳播（視同通知服務在這次呼叫中失敗），此時不再檢查
    /// <see cref="ExceptionToThrow"/>；<see cref="ExceptionToThrow"/> 只有在這個 callback 未設定、
    /// 或已設定但正常完成（沒有拋出例外）時才判斷是否丟出。
    /// </summary>
    public Func<CancellationToken, Task>? OnNotifyAsync { get; set; }

    public async Task NotifyTicketsIssuedAsync(string toEmail, string eventTitle, Guid orderId, int ticketCount, CancellationToken cancellationToken)
    {
        _calls.Add(new EmailNotificationCall(toEmail, eventTitle, orderId, ticketCount));

        if (OnNotifyAsync is not null)
        {
            await OnNotifyAsync(cancellationToken);
        }

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }
    }
}
