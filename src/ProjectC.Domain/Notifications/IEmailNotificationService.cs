namespace ProjectC.Domain.Notifications;

public interface IEmailNotificationService
{
    /// <summary>
    /// 通知 <paramref name="toEmail"/> 這個買家，其訂單 <paramref name="orderId"/> 已確認、電子票券已產出。
    /// 失敗以例外表達（不是 <see cref="System.Threading.Tasks.Task{TResult}"/> 型別的 Result）——呼叫端
    /// 只需要「盡力嘗試，失敗就記錄下來」，不依成功/失敗分流任何業務邏輯（見 email-notification design.md 決策 1）。
    /// 實作拋出例外時，例外的 <see cref="Exception.Message"/> MUST NOT 包含完整、未遮蔽的收件 Email——
    /// 呼叫端會直接記錄整個例外物件，不會另外對例外訊息內容做字串遮蔽（見 email-notification design.md 決策 5）。
    /// </summary>
    Task NotifyTicketsIssuedAsync(string toEmail, string eventTitle, Guid orderId, int ticketCount, CancellationToken cancellationToken);
}
