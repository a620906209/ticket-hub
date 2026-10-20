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
        // 自動回滾失敗後，工作單元自行開啟的連線在釋放交易的最後一步關閉；關閉失敗與回滾失敗各記一筆 Warning。
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
        // 自動回滾本身成功；之後關閉工作單元自行開啟的連線時失敗。
        await using var dbContext = _fixture.CreateDbContext(new ThrowingCloseInterceptor(closeFailure));
        var logger = new ListLogger<UnitOfWork>();
        var transaction = await new UnitOfWork(dbContext, logger).BeginTransactionAsync(CancellationToken.None);

        var act = async () => await transaction.DisposeAsync();

        await act.Should().NotThrowAsync();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Exception.Should().BeSameAs(closeFailure);
        dbContext.Database.CurrentTransaction.Should().BeNull();
    }

    // 以下 UOW-TX-009、010、013～017：交易自行開啟連線時，EF 在提交成功後於同一呼叫內關閉連線，關閉失敗會讓已落地的提交被誤判為失敗；
    // 連線改由工作單元開啟，提交／回滾完成後由工作單元關閉並吞下關閉失敗，未完成時於釋放交易時關閉。

    [Fact]
    public async Task CommitAsync_WhenConnectionCloseFailsAfterCommit_DoesNotThrowAndPersistsChanges()
    {
        var venueId = Guid.NewGuid();
        var closeFailure = new InvalidOperationException("close failed");
        await using var dbContext = _fixture.CreateDbContext(new ThrowingCloseInterceptor(closeFailure));
        var logger = new ListLogger<UnitOfWork>();
        var transaction = await new UnitOfWork(dbContext, logger).BeginTransactionAsync(CancellationToken.None);
        dbContext.Venues.Add(new Venue(venueId, "Close After Commit Venue"));

        var commit = () => transaction.CommitAsync(CancellationToken.None);
        await commit.Should().NotThrowAsync("資料已落地，關閉連線失敗不得讓呼叫端誤判提交失敗");
        var dispose = async () => await transaction.DisposeAsync();
        await dispose.Should().NotThrowAsync();

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Exception.Should().BeSameAs(closeFailure);
        await using var readDbContext = _fixture.CreateDbContext();
        (await readDbContext.Venues.AnyAsync(v => v.Id == venueId)).Should().BeTrue();
    }

    [Fact]
    public async Task CommitAsync_OnCallerOpenedConnection_KeepsConnectionOpenUntilCallerDisposes()
    {
        var venueId = Guid.NewGuid();
        await using var dbContext = _fixture.CreateDbContext();
        var unitOfWork = new UnitOfWork(dbContext);
        var connection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);

        await using (var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None))
        {
            dbContext.Venues.Add(new Venue(venueId, "Caller Connection Commit Venue"));
            await transaction.CommitAsync(CancellationToken.None);
            dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Open, "提交不得關閉呼叫端開啟的連線");
        }

        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Open, "釋放交易不得關閉呼叫端開啟的連線");
        var beginAgain = async () => await (await unitOfWork.BeginTransactionAsync(CancellationToken.None)).DisposeAsync();
        await beginAgain.Should().NotThrowAsync();
        await connection.DisposeAsync();
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        await using var readDbContext = _fixture.CreateDbContext();
        (await readDbContext.Venues.AnyAsync(v => v.Id == venueId)).Should().BeTrue();
    }

    [Fact]
    public async Task CommitAsync_OnOwnedConnection_ClosesConnectionBeforeDispose()
    {
        var venueId = Guid.NewGuid();
        await using var dbContext = _fixture.CreateDbContext();
        var logger = new ListLogger<UnitOfWork>();
        var unitOfWork = new UnitOfWork(dbContext, logger);

        await using (var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None))
        {
            dbContext.Venues.Add(new Venue(venueId, "Successful Commit Venue"));
            await transaction.CommitAsync(CancellationToken.None);
            // 呼叫端常在提交後、離開交易範圍前呼叫 Redis；連線若留到釋放才關，會在等待 Redis 時占住連線池（IUnitOfWork 契約）。
            dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed, "提交成功後須立即歸還連線");
        }

        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
        var beginAgain = async () => await (await unitOfWork.BeginTransactionAsync(CancellationToken.None)).DisposeAsync();
        await beginAgain.Should().NotThrowAsync();
        await using var readDbContext = _fixture.CreateDbContext();
        (await readDbContext.Venues.AnyAsync(v => v.Id == venueId)).Should().BeTrue();
    }

    [Fact]
    public async Task CommitAsync_WhenSaveChangesFails_PreservesExceptionAndDiscardsWrites()
    {
        var venueId = Guid.NewGuid();
        await using var dbContext = _fixture.CreateDbContext();
        var unitOfWork = new UnitOfWork(dbContext);
        var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        dbContext.Venues.Add(new Venue(venueId, "Save Failure Venue"));
        await dbContext.SaveChangesAsync();
        // 清掉追蹤後再加入同 Id 的場館，讓 CommitAsync 內的 SaveChangesAsync 撞主鍵，在提交前失敗。
        dbContext.ChangeTracker.Clear();
        dbContext.Venues.Add(new Venue(venueId, "Duplicate Save Failure Venue"));

        var exception = await Record.ExceptionAsync(() => transaction.CommitAsync(CancellationToken.None));
        var dispose = async () => await transaction.DisposeAsync();
        await dispose.Should().NotThrowAsync();

        exception.Should().BeOfType<DbUpdateException>();
        dbContext.Database.CurrentTransaction.Should().BeNull();
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        await using var readDbContext = _fixture.CreateDbContext();
        (await readDbContext.Venues.AnyAsync(v => v.Id == venueId)).Should().BeFalse("提交前失敗的寫入不得落地");
    }

    [Fact]
    public async Task RollbackAsync_OnOwnedConnection_ClosesConnectionBeforeDisposeAndDiscardsWrites()
    {
        var venueId = Guid.NewGuid();
        await using var dbContext = _fixture.CreateDbContext();
        var logger = new ListLogger<UnitOfWork>();
        var unitOfWork = new UnitOfWork(dbContext, logger);

        await using (var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None))
        {
            dbContext.Venues.Add(new Venue(venueId, "Explicit Rollback Venue"));
            await dbContext.SaveChangesAsync();
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed, "回滾成功後須立即歸還連線");
        }

        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
        await using var readDbContext = _fixture.CreateDbContext();
        (await readDbContext.Venues.AnyAsync(v => v.Id == venueId)).Should().BeFalse("回滾的寫入不得落地");
    }

    [Fact]
    public async Task RollbackAsync_WhenConnectionCloseFailsAfterRollback_DoesNotThrowAndLogsWarning()
    {
        var closeFailure = new InvalidOperationException("close failed");
        await using var dbContext = _fixture.CreateDbContext(new ThrowingCloseInterceptor(closeFailure));
        var logger = new ListLogger<UnitOfWork>();
        var transaction = await new UnitOfWork(dbContext, logger).BeginTransactionAsync(CancellationToken.None);

        var rollback = () => transaction.RollbackAsync(CancellationToken.None);
        await rollback.Should().NotThrowAsync("回滾已完成，關閉連線失敗不得讓呼叫端誤判回滾失敗");
        var dispose = async () => await transaction.DisposeAsync();
        await dispose.Should().NotThrowAsync();

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Exception.Should().BeSameAs(closeFailure);
        dbContext.Database.CurrentTransaction.Should().BeNull();
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotCloseConnectionOpenedAfterwards()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var unitOfWork = new UnitOfWork(dbContext);
        var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        await transaction.DisposeAsync();
        var connection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);

        // EF 以計數追蹤手動開啟的連線，重複釋放若再關一次，會把之後呼叫端開啟的連線提早關掉。
        await transaction.DisposeAsync();

        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Open, "重複釋放交易不得關閉呼叫端之後開啟的連線");
        await connection.DisposeAsync();
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
    }

    // 以下 UOW-TX-018～021：開始交易失敗時 EF 的開啟計數不會減回，工作單元不處理的話連線會留到 DbContext 釋放（request 結束）。

    [Fact]
    public async Task BeginTransactionAsync_WhenStartFails_ClosesConnectionAndRethrows()
    {
        var startFailure = new InvalidOperationException("start failed");
        var interceptor = new ThrowingTransactionInterceptor(startFailure: startFailure);
        await using var dbContext = _fixture.CreateDbContext(interceptor);
        var logger = new ListLogger<UnitOfWork>();
        var unitOfWork = new UnitOfWork(dbContext, logger);

        var exception = await Record.ExceptionAsync(() => unitOfWork.BeginTransactionAsync(CancellationToken.None));

        exception.Should().BeSameAs(startFailure);
        dbContext.Database.CurrentTransaction.Should().BeNull();
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed, "開始失敗後工作單元開啟的連線須立即歸還");
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
        interceptor.StartFailure = null;
        var beginAgain = async () => await (await unitOfWork.BeginTransactionAsync(CancellationToken.None)).DisposeAsync();
        await beginAgain.Should().NotThrowAsync();
    }

    [Fact]
    public async Task BeginTransactionAsync_WhenCancelledDuringStart_ClosesConnection()
    {
        // 連線開啟後、交易開始途中的取消無法用真實 token 穩定打中，改由 interceptor 拋出取消例外模擬（design.md 決策 3）。
        var interceptor = new ThrowingTransactionInterceptor(startFailure: new OperationCanceledException());
        await using var dbContext = _fixture.CreateDbContext(interceptor);
        var unitOfWork = new UnitOfWork(dbContext);

        var act = () => unitOfWork.BeginTransactionAsync(CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed, "取消後工作單元開啟的連線須立即歸還");
        interceptor.StartFailure = null;
        var beginAgain = async () => await (await unitOfWork.BeginTransactionAsync(CancellationToken.None)).DisposeAsync();
        await beginAgain.Should().NotThrowAsync();
    }

    [Fact]
    public async Task BeginTransactionAsync_WhenStartAndConnectionCloseFail_PreservesStartExceptionAndLogsWarning()
    {
        var startFailure = new InvalidOperationException("start failed");
        var closeFailure = new InvalidOperationException("close failed");
        await using var dbContext = _fixture.CreateDbContext(
            new ThrowingTransactionInterceptor(startFailure: startFailure), new ThrowingCloseInterceptor(closeFailure));
        var logger = new ListLogger<UnitOfWork>();
        var unitOfWork = new UnitOfWork(dbContext, logger);

        var exception = await Record.ExceptionAsync(() => unitOfWork.BeginTransactionAsync(CancellationToken.None));

        exception.Should().BeSameAs(startFailure, "關閉失敗不得蓋掉開始交易的原例外");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Exception.Should().BeSameAs(closeFailure);
    }

    [Fact]
    public async Task BeginTransactionAsync_WhenStartFailsOnCallerOpenedConnection_KeepsCallerConnectionOpen()
    {
        var startFailure = new InvalidOperationException("start failed");
        await using var dbContext = _fixture.CreateDbContext(new ThrowingTransactionInterceptor(startFailure: startFailure));
        var logger = new ListLogger<UnitOfWork>();
        var unitOfWork = new UnitOfWork(dbContext, logger);
        await using var connection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => unitOfWork.BeginTransactionAsync(CancellationToken.None));

        // 呼叫端可能還在使用這條連線（下單主路徑），開始失敗的處理不得替呼叫端關閉。
        exception.Should().BeSameAs(startFailure);
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Open);
        (await dbContext.Database.SqlQuery<int>($"SELECT 1 AS \"Value\"").SingleAsync()).Should().Be(1);
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    // 以下 UOW-TX-022、023：提交後關閉失敗會讓連線停在開啟狀態；之後的交易若以連線狀態判斷歸屬，會誤判為呼叫端連線，
    // 改由 EF 在提交時關閉，關閉再失敗時已落地的提交會以例外傳給呼叫端。

    [Fact]
    public async Task CommitAsync_OnLeftoverOpenConnection_WhenCloseFailsAgain_DoesNotThrowAndPersistsChanges()
    {
        var venueId = Guid.NewGuid();
        var closeFailure = new InvalidOperationException("close failed");
        await using var dbContext = _fixture.CreateDbContext(new SwitchableCloseInterceptor(closeFailure));
        var logger = new ListLogger<UnitOfWork>();
        var unitOfWork = new UnitOfWork(dbContext, logger);
        await using (var firstTransaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None))
        {
            await firstTransaction.CommitAsync(CancellationToken.None);
        }
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Open, "前提：第一次提交後的關閉失敗留下開啟的連線");

        var secondTransaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        dbContext.Venues.Add(new Venue(venueId, "Leftover Connection Close Failure Venue"));

        var commit = () => secondTransaction.CommitAsync(CancellationToken.None);
        await commit.Should().NotThrowAsync("資料已落地，關閉連線失敗不得讓呼叫端誤判提交失敗");
        var dispose = async () => await secondTransaction.DisposeAsync();
        await dispose.Should().NotThrowAsync();

        logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Exception)
            .Should().Equal(closeFailure, closeFailure);
        await using var readDbContext = _fixture.CreateDbContext();
        (await readDbContext.Venues.AnyAsync(v => v.Id == venueId)).Should().BeTrue();
    }

    [Fact]
    public async Task CommitAsync_OnLeftoverOpenConnection_ClosesConnectionBeforeDispose()
    {
        var venueId = Guid.NewGuid();
        var closeInterceptor = new SwitchableCloseInterceptor(new InvalidOperationException("close failed"));
        await using var dbContext = _fixture.CreateDbContext(closeInterceptor);
        var unitOfWork = new UnitOfWork(dbContext);
        await using (var firstTransaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None))
        {
            await firstTransaction.CommitAsync(CancellationToken.None);
        }
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Open, "前提：第一次提交後的關閉失敗留下開啟的連線");
        closeInterceptor.IsFailing = false;

        await using (var secondTransaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None))
        {
            dbContext.Venues.Add(new Venue(venueId, "Leftover Connection Commit Venue"));
            await secondTransaction.CommitAsync(CancellationToken.None);
            dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed, "接手遺留連線後，提交成功仍須立即歸還連線");
        }

        await using var readDbContext = _fixture.CreateDbContext();
        (await readDbContext.Venues.AnyAsync(v => v.Id == venueId)).Should().BeTrue();
    }

    /// <summary>在 EF Core 實際開始／提交／回滾前拋出指定例外，模擬連線中斷造成的開始、提交或回滾失敗。
    /// <see cref="StartFailure"/> 設為 null 即停止注入開始失敗，用於驗證失敗後可再開始交易。</summary>
    private sealed class ThrowingTransactionInterceptor(
        Exception? rollbackFailure = null, Exception? commitFailure = null, Exception? startFailure = null)
        : DbTransactionInterceptor
    {
        public Exception? StartFailure { get; set; } = startFailure;

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
            => StartFailure is null
                ? base.TransactionStartingAsync(connection, eventData, result, cancellationToken)
                : throw StartFailure;

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
