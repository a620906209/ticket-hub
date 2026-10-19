using System.Data;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public class UnitOfWorkTests
{
    private readonly PostgresFixture _fixture;

    public UnitOfWorkTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task BeginTransactionAsync_CalledTwiceOnSameContext_ThrowsInvalidOperationException()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var unitOfWork = new UnitOfWork(dbContext);

        await using var firstTransaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);

        var act = () => unitOfWork.BeginTransactionAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CommitAsync_PersistsChanges()
    {
        var venueId = Guid.NewGuid();

        await using (var dbContext = _fixture.CreateDbContext())
        {
            var unitOfWork = new UnitOfWork(dbContext);
            await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);

            dbContext.Venues.Add(new Venue(venueId, "Commit Test Venue"));
            await transaction.CommitAsync(CancellationToken.None);
        }

        await using var readDbContext = _fixture.CreateDbContext();
        var exists = await readDbContext.Venues.AnyAsync(v => v.Id == venueId);
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_WithoutCommitOrRollback_AutomaticallyRollsBack()
    {
        var venueId = Guid.NewGuid();

        await using (var dbContext = _fixture.CreateDbContext())
        {
            var unitOfWork = new UnitOfWork(dbContext);
            var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);

            dbContext.Venues.Add(new Venue(venueId, "Dispose Without Commit Venue"));
            await dbContext.SaveChangesAsync(); // 寫進 change tracker 對應的交易裡，但還沒 Commit

            await transaction.DisposeAsync(); // 沒呼叫 CommitAsync/RollbackAsync，視為放棄
        }

        await using var readDbContext = _fixture.CreateDbContext();
        var exists = await readDbContext.Venues.AnyAsync(v => v.Id == venueId);
        exists.Should().BeFalse("Dispose 前沒有呼叫 CommitAsync，應該自動回滾");
    }

    [Fact]
    public async Task CommitAsync_CalledTwice_ThrowsInvalidOperationException()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var unitOfWork = new UnitOfWork(dbContext);
        var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

        var act = () => transaction.CommitAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RollbackAsync_AfterCommit_ThrowsInvalidOperationException()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var unitOfWork = new UnitOfWork(dbContext);
        var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

        var act = () => transaction.RollbackAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task WithoutTransaction_ChangesAddedToContext_AreNeverPersisted()
    {
        var venueId = Guid.NewGuid();

        await using (var dbContext = _fixture.CreateDbContext())
        {
            // 沒有透過 IUnitOfWork 開交易，只是把物件加進 change tracker，永遠不會有東西呼叫 SaveChanges。
            dbContext.Venues.Add(new Venue(venueId, "No Transaction Venue"));
        }

        await using var readDbContext = _fixture.CreateDbContext();
        var exists = await readDbContext.Venues.AnyAsync(v => v.Id == venueId);
        exists.Should().BeFalse();
    }

    // 以下 UOW-TX-002～006：釋放常發生在 await using 因例外展開的途中，
    // 回滾或釋放失敗若外傳會蓋掉呼叫端原本的例外，跳過釋放則交易與連線會留到 DbContext 結束。

    [Fact]
    public async Task DisposeAsync_WhenRollbackFails_LogsWarningAndDoesNotThrow()
    {
        var rollbackFailure = new InvalidOperationException("rollback failed");
        await using var dbContext = _fixture.CreateDbContext(new ThrowingTransactionInterceptor(rollbackFailure: rollbackFailure));
        var logger = new ListLogger<UnitOfWork>();
        var transaction = await new UnitOfWork(dbContext, logger).BeginTransactionAsync(CancellationToken.None);

        var act = async () => await transaction.DisposeAsync();

        await act.Should().NotThrowAsync();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Exception.Should().BeSameAs(rollbackFailure);
    }

    [Fact]
    public async Task DisposeAsync_WhenRollbackFails_ReleasesTransactionAndDiscardsWrites()
    {
        var venueId = Guid.NewGuid();
        var rollbackFailure = new InvalidOperationException("rollback failed");
        await using var dbContext = _fixture.CreateDbContext(new ThrowingTransactionInterceptor(rollbackFailure: rollbackFailure));
        var unitOfWork = new UnitOfWork(dbContext);
        var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        dbContext.Venues.Add(new Venue(venueId, "Rollback Failure Venue"));
        await dbContext.SaveChangesAsync();

        // 只判定釋放結果；釋放是否拋例外由 DisposeAsync_WhenRollbackFails_LogsWarningAndDoesNotThrow 判定。
        _ = await Record.ExceptionAsync(async () => await transaction.DisposeAsync());

        dbContext.Database.CurrentTransaction.Should().BeNull("回滾失敗仍須釋放交易");
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        var beginAgain = async () => await (await unitOfWork.BeginTransactionAsync(CancellationToken.None)).DisposeAsync();
        await beginAgain.Should().NotThrowAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        (await readDbContext.Venues.AnyAsync(v => v.Id == venueId)).Should().BeFalse("未提交的寫入不得落地");
    }

    [Fact]
    public async Task DisposeAsync_WhenRollbackFailsDuringExceptionUnwind_PreservesOriginalException()
    {
        var rollbackFailure = new InvalidOperationException("rollback failed");
        var businessFailure = new InvalidOperationException("business failure");
        await using var dbContext = _fixture.CreateDbContext(new ThrowingTransactionInterceptor(rollbackFailure: rollbackFailure));
        var logger = new ListLogger<UnitOfWork>();
        var unitOfWork = new UnitOfWork(dbContext, logger);

        var act = async () =>
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            throw businessFailure;
        };

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(businessFailure);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Exception.Should().BeSameAs(rollbackFailure);
    }

    [Fact]
    public async Task DisposeAsync_WhenRollbackAndTransactionDisposeFail_LogsBothWarningsAndDoesNotThrow()
    {
        var rollbackFailure = new InvalidOperationException("rollback failed");
        var closeFailure = new InvalidOperationException("close failed");
        // 回滾在完成前失敗時，交易自行開啟的連線改在釋放交易時才關閉，關閉失敗即釋放失敗（design.md 決策 3）。
        var dbContext = _fixture.CreateDbContext(
            new ThrowingTransactionInterceptor(rollbackFailure: rollbackFailure), new ThrowingCloseInterceptor(closeFailure));
        try
        {
            var logger = new ListLogger<UnitOfWork>();
            var transaction = await new UnitOfWork(dbContext, logger).BeginTransactionAsync(CancellationToken.None);

            var act = async () => await transaction.DisposeAsync();

            await act.Should().NotThrowAsync();
            logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Exception)
                .Should().Equal(rollbackFailure, closeFailure);
        }
        finally
        {
            // 交易未被釋放時，DbContext 釋放會代為釋放交易並觸發關閉例外，蓋掉上方斷言的失敗訊息。
            _ = await Record.ExceptionAsync(async () => await dbContext.DisposeAsync());
        }
    }

    [Fact]
    public async Task DisposeAsync_AfterCommitFails_PreservesCommitExceptionAndReleasesTransaction()
    {
        var venueId = Guid.NewGuid();
        var commitFailure = new InvalidOperationException("commit failed");
        var rollbackFailure = new InvalidOperationException("rollback failed");
        await using var dbContext = _fixture.CreateDbContext(
            new ThrowingTransactionInterceptor(rollbackFailure: rollbackFailure, commitFailure: commitFailure));
        var logger = new ListLogger<UnitOfWork>();
        var unitOfWork = new UnitOfWork(dbContext, logger);

        var exception = await Record.ExceptionAsync(async () =>
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            dbContext.Venues.Add(new Venue(venueId, "Commit Failure Venue"));
            await transaction.CommitAsync(CancellationToken.None);
        });

        exception.Should().BeSameAs(commitFailure);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Exception.Should().BeSameAs(rollbackFailure);
        dbContext.Database.CurrentTransaction.Should().BeNull();

        await using var readDbContext = _fixture.CreateDbContext();
        (await readDbContext.Venues.AnyAsync(v => v.Id == venueId)).Should().BeFalse("提交失敗的寫入不得落地");
    }

    [Fact]
    public async Task DisposeAsync_WhenRollbackFailsOnCallerOpenedConnection_ReleasesTransactionAndKeepsConnectionOpen()
    {
        var venueId = Guid.NewGuid();
        var rollbackFailure = new InvalidOperationException("rollback failed");
        await using var dbContext = _fixture.CreateDbContext(new ThrowingTransactionInterceptor(rollbackFailure: rollbackFailure));
        var unitOfWork = new UnitOfWork(dbContext);
        // 下單主路徑先借連線再開交易；連線由 IUnitOfWorkConnection 持有，交易釋放時不得把它關掉。
        var connection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);
        var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        dbContext.Venues.Add(new Venue(venueId, "Caller Connection Venue"));
        await dbContext.SaveChangesAsync();

        var act = async () => await transaction.DisposeAsync();

        await act.Should().NotThrowAsync();
        dbContext.Database.CurrentTransaction.Should().BeNull("回滾失敗仍須釋放交易");
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Open);
        var beginAgain = async () => await (await unitOfWork.BeginTransactionAsync(CancellationToken.None)).DisposeAsync();
        await beginAgain.Should().NotThrowAsync();

        await connection.DisposeAsync();
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        await using var readDbContext = _fixture.CreateDbContext();
        (await readDbContext.Venues.AnyAsync(v => v.Id == venueId)).Should().BeFalse("未提交的寫入不得落地");
    }

    [Fact]
    public async Task DisposeAsync_WhenConnectionCloseFailsDuringRollback_LogsWarningAndReleasesTransaction()
    {
        var closeFailure = new InvalidOperationException("close failed");
        // 回滾本身成功；EF 在回滾內關閉交易自行開啟的連線時失敗，例外會從回滾傳出。
        await using var dbContext = _fixture.CreateDbContext(new ThrowingCloseInterceptor(closeFailure));
        var logger = new ListLogger<UnitOfWork>();
        var transaction = await new UnitOfWork(dbContext, logger).BeginTransactionAsync(CancellationToken.None);

        var act = async () => await transaction.DisposeAsync();

        await act.Should().NotThrowAsync();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Exception.Should().BeSameAs(closeFailure);
        dbContext.Database.CurrentTransaction.Should().BeNull();
    }

    /// <summary>在 EF Core 實際提交／回滾前拋出指定例外，模擬連線中斷造成的提交或回滾失敗。</summary>
    private sealed class ThrowingTransactionInterceptor(Exception? rollbackFailure = null, Exception? commitFailure = null)
        : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
            => rollbackFailure is null
                ? base.TransactionRollingBackAsync(transaction, eventData, result, cancellationToken)
                : throw rollbackFailure;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
            => commitFailure is null
                ? base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken)
                : throw commitFailure;
    }
}
