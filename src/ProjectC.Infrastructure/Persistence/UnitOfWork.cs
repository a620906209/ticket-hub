using Microsoft.EntityFrameworkCore.Storage;
using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Infrastructure.Persistence;

public class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationDbContext _dbContext;

    public UnitOfWork(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (_dbContext.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("A transaction is already in progress on this unit of work.");

        var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        return new UnitOfWorkTransaction(_dbContext, transaction);
    }
}

internal sealed class UnitOfWorkTransaction : IUnitOfWorkTransaction
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IDbContextTransaction _transaction;
    private bool _completed;

    public UnitOfWorkTransaction(ApplicationDbContext dbContext, IDbContextTransaction transaction)
    {
        _dbContext = dbContext;
        _transaction = transaction;
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        EnsureNotCompleted();

        // 唯一能落地資料的路徑：SaveChanges 與資料庫交易的 Commit 視為同一個原子操作，
        // 呼叫端不需要（也不應該）另外呼叫 SaveChangesAsync（design.md 決策 4）。
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _transaction.CommitAsync(cancellationToken);
        _completed = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        EnsureNotCompleted();

        await _transaction.RollbackAsync(cancellationToken);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        // 未呼叫 CommitAsync/RollbackAsync 就 Dispose，視為呼叫端放棄這筆交易，自動回滾，
        // 不留下懸而未決的交易（design.md 決策 4 的安全預設值）。
        if (!_completed)
        {
            await _transaction.RollbackAsync();
            _completed = true;
        }

        await _transaction.DisposeAsync();
    }

    private void EnsureNotCompleted()
    {
        if (_completed)
            throw new InvalidOperationException("This transaction has already been committed or rolled back.");
    }
}
