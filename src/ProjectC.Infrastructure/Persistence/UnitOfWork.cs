using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Infrastructure.Persistence;

public class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<UnitOfWork> _logger;

    public UnitOfWork(ApplicationDbContext dbContext, ILogger<UnitOfWork>? logger = null)
    {
        _dbContext = dbContext;
        _logger = logger ?? NullLogger<UnitOfWork>.Instance;
    }

    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (_dbContext.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("A transaction is already in progress on this unit of work.");

        var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        return new UnitOfWorkTransaction(_dbContext, transaction);
    }

    public async Task<IUnitOfWorkConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (_dbContext.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Cannot open a connection while a transaction is in progress on this unit of work.");

        if (_dbContext.Database.GetDbConnection().State != ConnectionState.Closed)
            throw new InvalidOperationException("A connection is already open on this unit of work.");

        await _dbContext.Database.OpenConnectionAsync(cancellationToken);
        return new UnitOfWorkConnection(_dbContext, _logger);
    }
}

internal sealed class UnitOfWorkConnection : IUnitOfWorkConnection
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger _logger;
    private bool _disposed;

    public UnitOfWorkConnection(ApplicationDbContext dbContext, ILogger logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async ValueTask DisposeAsync()
    {
        // EF Core 以計數追蹤手動開啟的連線，重複關閉會把之後另一個呼叫端開的連線提早關掉。
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            await _dbContext.Database.CloseConnectionAsync();
        }
        catch (Exception exception)
        {
            // 不重拋：dispose 常發生在例外或取消的展開途中，重拋會蓋掉原本的例外。連線損毀時 Npgsql 直接丟棄、
            // 不回連線池，伺服器端交易隨連線中斷自動回滾、鎖釋放，所以記錄後不需要其他補救（design.md 決策 2）。
            _logger.LogWarning(exception, "Failed to close the unit of work database connection.");
        }
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
