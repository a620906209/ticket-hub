namespace ProjectC.Application.Common.Interfaces;

/// <summary>
/// 資料庫交易的開啟入口，目前只有售票（Ticketing）Repository 的寫入會透過這個介面存檔；
/// 會員系統（Membership）維持既有的 <c>IApplicationDbContext.SaveChangesAsync</c> 寫入方式，不受影響。
/// 這個介面刻意不提供交易之外的獨立 <c>SaveChangesAsync</c>：使用這個介面的呼叫端，任何寫入
/// （即使不需要悲觀鎖，例如新增一筆 Venue）都必須包在 <see cref="BeginTransactionAsync"/>
/// 開出的交易裡，透過 <see cref="IUnitOfWorkTransaction.CommitAsync"/> 才會真的落地。
/// </summary>
public interface IUnitOfWork
{
    /// <summary>開啟一筆新交易。若目前已有進行中的交易，MUST 拋出 <see cref="InvalidOperationException"/>。</summary>
    Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 代表一筆進行中的資料庫交易。標準用法：
/// <c>await using var tx = await unitOfWork.BeginTransactionAsync(cancellationToken); ... await tx.CommitAsync(cancellationToken);</c>
/// 失敗路徑不需要手動呼叫 <see cref="RollbackAsync"/>：若 <see cref="DisposeAsync"/> 前未呼叫過
/// <see cref="CommitAsync"/> 或 <see cref="RollbackAsync"/>，視為放棄這筆交易，MUST 自動回滾。
/// </summary>
public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    /// <summary>依序執行 SaveChanges 與資料庫交易的 Commit，視為同一個原子操作。呼叫端不需要另外呼叫 SaveChanges。
    /// 對已經 Commit 或 Rollback 過的 handle 再次呼叫 MUST 拋出 <see cref="InvalidOperationException"/>。</summary>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>回滾交易，不落地任何變更。
    /// 對已經 Commit 或 Rollback 過的 handle 再次呼叫 MUST 拋出 <see cref="InvalidOperationException"/>。</summary>
    Task RollbackAsync(CancellationToken cancellationToken);
}
