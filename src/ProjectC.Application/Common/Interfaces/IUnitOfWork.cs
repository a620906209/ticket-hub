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

    /// <summary>
    /// 開啟資料庫連線並保持到 dispose，讓之後的查詢與交易都用同一條連線，不再每次查詢後歸還連線池、下次重新排隊
    /// （order-placement-p95-phase2 design.md 決策 2：高併發時每次排隊都排到隊尾，尾端延遲被放大）。
    /// <list type="bullet">
    /// <item>MUST 在開啟交易之前呼叫，並以 <c>await using</c> 宣告在交易之前，讓例外與提早 return 時先回滾交易再關連線。</item>
    /// <item>連線已開啟或已有進行中的交易時 MUST 拋出 <see cref="InvalidOperationException"/>，避免巢狀使用造成另一個呼叫端的連線被提早關閉。</item>
    /// <item>持有期間連線不回連線池，不得在持有期間等待資料庫以外的慢速 I/O（例如 Redis），應先 dispose。</item>
    /// <item>專案目前未啟用 <c>EnableRetryOnFailure</c>；若未來啟用，手動開連線加使用者交易必須包在
    /// <c>CreateExecutionStrategy().ExecuteAsync</c> 內，否則 EF Core 會拋例外。</item>
    /// </list>
    /// </summary>
    Task<IUnitOfWorkConnection> OpenConnectionAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 代表 <see cref="IUnitOfWork.OpenConnectionAsync"/> 開啟的連線；<see cref="IAsyncDisposable.DisposeAsync"/> 關閉連線、歸還連線池，
/// 重複 dispose 不做事。關閉失敗不拋出，避免蓋掉呼叫端原本的例外。
/// </summary>
public interface IUnitOfWorkConnection : IAsyncDisposable
{
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
