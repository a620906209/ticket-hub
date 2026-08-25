namespace ProjectC.Infrastructure.Persistence;

internal static class ApplicationDbContextTransactionExtensions
{
    /// <summary>PostgreSQL 的列鎖只在交易存續期間有效；沒有進行中的交易時，SELECT ... FOR UPDATE
    /// 一返回鎖就釋放了，等於完全沒有鎖定保護，所以呼叫端須在這裡 fail fast。</summary>
    public static void EnsureActiveTransaction(this ApplicationDbContext dbContext, string callerMemberName)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                $"{callerMemberName} must be called within an active transaction " +
                "(see IUnitOfWork.BeginTransactionAsync); otherwise the row lock is released " +
                "as soon as this query returns.");
        }
    }
}
