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
    private bool _hasCallerConnection;

    public UnitOfWork(ApplicationDbContext dbContext, ILogger<UnitOfWork>? logger = null)
    {
        _dbContext = dbContext;
        _logger = logger ?? NullLogger<UnitOfWork>.Instance;
    }

    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (_dbContext.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("A transaction is already in progress on this unit of work.");

        // 沒有呼叫端連線時，連線一律由工作單元開啟、在提交／回滾完成或釋放交易時關閉，包含先前關閉失敗遺留的開啟連線（開啟只加計數）：
        // 交易自行開啟連線時，EF 會在提交成功後於同一呼叫內關閉連線，關閉失敗會讓已落地的提交以例外傳給呼叫端。
        // 歸屬依記錄判斷而非連線狀態，遺留的開啟連線才不會被誤判為呼叫端連線（unit-of-work-connection-ownership design.md 決策 1、2）。
        var ownsConnection = !_hasCallerConnection;
        if (ownsConnection)
            await _dbContext.Database.OpenConnectionAsync(cancellationToken);

        IDbContextTransaction transaction;
        try
        {
            transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        }
        catch (Exception) when (ownsConnection)
        {
            await CloseConnectionAfterFailedStartAsync();
            throw;
        }

        return new UnitOfWorkTransaction(_dbContext, transaction, ownsConnection, _logger);
    }

    /// <summary>
    /// 開始交易失敗時 EF 不會減回交易內部那次開啟計數，只關一次連線仍維持開啟，所以關到 <c>Closed</c> 為止
    /// （unit-of-work-connection-ownership design.md 決策 3）。依賴 EF Core（目前 10.0.11）開始失敗後計數殘留、
    /// 遺留連線接手時只加計數的內部行為，不是公開契約；升級 EF 時以 UOW-TX-018～026 作為回歸偵測。
    /// 每次關閉前先檢查狀態，EF 日後若自行減回計數，迴圈第一次就停下，不會多關。
    /// 不傳 CancellationToken：開始途中被取消時 token 已取消，傳入會讓關閉本身也被取消。
    /// </summary>
    private async Task CloseConnectionAfterFailedStartAsync()
    {
        const int maxCloseAttempts = 3;
        var connection = _dbContext.Database.GetDbConnection();
        for (var attempt = 0; attempt < maxCloseAttempts && connection.State != ConnectionState.Closed; attempt++)
        {
            try
            {
                await _dbContext.Database.CloseConnectionAsync();
            }
            catch (Exception exception)
            {
                // 不重拋：會蓋掉開始交易的原例外。關閉失敗後狀態無法判斷，停止重試，連線可能留到 DbContext 結束。
                _logger.LogWarning(exception, "Failed to close the unit of work connection after the transaction failed to start.");
                return;
            }
        }

        // 計數殘留時觸發：呼叫端連線上開始交易失敗（Non-Goal，計數各殘留 1）兩次以上，釋放後再於自有交易開始失敗，3 次關閉仍不足（design.md 決策 3）。
        if (connection.State != ConnectionState.Closed)
            _logger.LogWarning("Connection remained open after the unit of work transaction failed to start.");
    }

    public async Task<IUnitOfWorkConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (_dbContext.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Cannot open a connection while a transaction is in progress on this unit of work.");

        if (_hasCallerConnection)
            throw new InvalidOperationException("A connection is already open on this unit of work.");

        // 連線因先前關閉失敗仍開啟時，這一步只加計數、接手該連線，呼叫端釋放時計數歸 0 並實際關閉。
        // EF 對已開啟的連線不檢查 token，先自行檢查，取消才不會被靜默吞掉（design.md 決策 2）。
        cancellationToken.ThrowIfCancellationRequested();
        await _dbContext.Database.OpenConnectionAsync(cancellationToken);
        _hasCallerConnection = true;
        return new UnitOfWorkConnection(_dbContext, _logger, () => _hasCallerConnection = false);
    }
}

internal sealed class UnitOfWorkConnection : IUnitOfWorkConnection
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger _logger;
    private readonly Action _onDisposing;
    private bool _disposed;

    public UnitOfWorkConnection(ApplicationDbContext dbContext, ILogger logger, Action onDisposing)
    {
        _dbContext = dbContext;
        _logger = logger;
        _onDisposing = onDisposing;
    }

    public async ValueTask DisposeAsync()
    {
        // EF Core 以計數追蹤手動開啟的連線，重複關閉會把之後另一個呼叫端開的連線提早關掉。
        if (_disposed)
            return;

        _disposed = true;
        // 在關閉之前重設工作單元的呼叫端連線記錄：關閉失敗時連線仍開啟，之後的交易須由工作單元負責關閉
        // （unit-of-work-connection-ownership design.md 決策 1）。
        _onDisposing();
        // 交易未完成就釋放時，提交後的關閉改由 EF 執行，關閉失敗會從 CommitAsync 傳出。只記錄不拋出：
        // dispose 常在例外展開途中執行；仍釋放呼叫端的持有（計數減 1，實體連線於交易結束時關閉），否則計數殘留、
        // 連線留到 DbContext 結束（design.md 決策 6）。
        if (_dbContext.Database.CurrentTransaction is not null)
            _logger.LogWarning("The unit of work connection was disposed while a transaction was still in progress.");

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
    private bool _ownsConnection;
    private readonly ILogger _logger;
    private bool _completed;

    public UnitOfWorkTransaction(ApplicationDbContext dbContext, IDbContextTransaction transaction, bool ownsConnection, ILogger logger)
    {
        _dbContext = dbContext;
        _transaction = transaction;
        _ownsConnection = ownsConnection;
        _logger = logger;
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        EnsureNotCompleted();

        // 唯一能落地資料的路徑：SaveChanges 與資料庫交易的 Commit 視為同一個原子操作，
        // 呼叫端不需要（也不應該）另外呼叫 SaveChangesAsync（design.md 決策 4）。
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _transaction.CommitAsync(cancellationToken);
        _completed = true;
        await CloseOwnedConnectionAsync();
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        EnsureNotCompleted();

        await _transaction.RollbackAsync(cancellationToken);
        _completed = true;
        await CloseOwnedConnectionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // 未呼叫 CommitAsync/RollbackAsync 就 Dispose，視為呼叫端放棄這筆交易，自動回滾，
        // 不留下懸而未決的交易（design.md 決策 4 的安全預設值）。
        try
        {
            if (!_completed)
                await _transaction.RollbackAsync();
        }
        catch (Exception exception)
        {
            // 不重拋：理由同 UnitOfWorkConnection.DisposeAsync，回滾失敗多代表連線已損毀，伺服器端交易隨連線中斷自動回滾。
            // 交易仍須在 finally 釋放，否則交易與連線會留到 DbContext 結束（unit-of-work-rollback-dispose design.md 決策 1）。
            _logger.LogWarning(exception, "Automatic rollback of the unit of work transaction failed on dispose.");
        }
        finally
        {
            _completed = true;
            try
            {
                await _transaction.DisposeAsync();
            }
            catch (Exception exception)
            {
                // 不重拋：同上，避免蓋掉呼叫端原本的例外；釋放失敗時連線可能留到 DbContext 結束，此層無法補救。
                _logger.LogWarning(exception, "Failed to dispose the unit of work transaction.");
            }

            await CloseOwnedConnectionAsync();
        }
    }

    /// <summary>
    /// 提交或回滾完成後立即歸還連線，不等到釋放交易：呼叫端常在提交後、離開交易範圍前呼叫 Redis 等慢速 I/O，
    /// 持有連線等待會占住連線池（IUnitOfWork.OpenConnectionAsync 契約）。只關一次，重複關閉會使 EF 開啟計數多減，
    /// 把之後呼叫端開啟的連線提早關掉。
    /// </summary>
    private async Task CloseOwnedConnectionAsync()
    {
        if (!_ownsConnection)
            return;

        _ownsConnection = false;
        try
        {
            await _dbContext.Database.CloseConnectionAsync();
        }
        catch (Exception exception)
        {
            // 不重拋：提交或回滾已完成，外傳會讓呼叫端誤判結果；釋放時外傳則會蓋掉呼叫端原本的例外。
            // 關閉失敗的連線可能留到 DbContext 結束，此層無法補救。
            _logger.LogWarning(exception, "Failed to close the unit of work transaction connection.");
        }
    }

    private void EnsureNotCompleted()
    {
        if (_completed)
            throw new InvalidOperationException("This transaction has already been committed or rolled back.");
    }
}
