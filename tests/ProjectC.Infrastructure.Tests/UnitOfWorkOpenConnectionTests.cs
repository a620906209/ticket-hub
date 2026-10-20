using System.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

// order-placement-p95-phase2 design.md 決策 2：下單整段只向連線池借一次連線。
[Collection(PostgresCollection.Name)]
public class UnitOfWorkOpenConnectionTests
{
    private readonly PostgresFixture _fixture;

    public UnitOfWorkOpenConnectionTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OpenConnectionAsync_WhenQueriesRun_ConnectionStaysOpenOnSameBackend()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var unitOfWork = new UnitOfWork(dbContext);

        await using var connection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);
        var firstBackendPid = await GetBackendPidAsync(dbContext);
        var secondBackendPid = await GetBackendPidAsync(dbContext);

        // EF Core 預設每次查詢後歸還連線；同一個 backend 才代表兩次查詢之間沒有回到連線池重新排隊。
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Open);
        secondBackendPid.Should().Be(firstBackendPid);
    }

    [Fact]
    public async Task DisposeAsync_AfterOpen_ClosesConnection()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var unitOfWork = new UnitOfWork(dbContext);

        var connection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);
        await connection.DisposeAsync();

        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotCloseConnectionOpenedLater()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var unitOfWork = new UnitOfWork(dbContext);

        var firstConnection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);
        await firstConnection.DisposeAsync();
        await using var secondConnection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);

        // 重複 dispose 若再關一次，會把之後另一個呼叫端開的連線提早關掉。
        var act = async () => await firstConnection.DisposeAsync();

        await act.Should().NotThrowAsync();
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Open);
    }

    [Fact]
    public async Task OpenConnectionAsync_WhenAlreadyOpen_ThrowsInvalidOperationException()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var unitOfWork = new UnitOfWork(dbContext);
        await using var connection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);

        var act = () => unitOfWork.OpenConnectionAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Open, "拒絕巢狀開啟不得影響既有連線");
    }

    [Fact]
    public async Task OpenConnectionAsync_WhenTransactionInProgress_ThrowsInvalidOperationException()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var unitOfWork = new UnitOfWork(dbContext);
        await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);

        var act = () => unitOfWork.OpenConnectionAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task OpenConnectionAsync_ThenTransactionCommits_PersistsChangesAndConnectionClosesOnlyOnDispose()
    {
        var venueId = Guid.NewGuid();
        await using (var dbContext = _fixture.CreateDbContext())
        {
            var unitOfWork = new UnitOfWork(dbContext);
            var connection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);

            await using (var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None))
            {
                dbContext.Venues.Add(new Venue(venueId, "Open Connection Commit Venue"));
                await transaction.CommitAsync(CancellationToken.None);
            }

            dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Open, "交易結束不得關掉呼叫端自己開的連線");

            var act = async () => await connection.DisposeAsync();
            await act.Should().NotThrowAsync();
            dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        }

        await using var readDbContext = _fixture.CreateDbContext();
        (await readDbContext.Venues.AnyAsync(v => v.Id == venueId)).Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_AfterCommitBeforeTransactionDisposed_ClosesConnectionAndTransactionDisposeDoesNotThrow()
    {
        var venueId = Guid.NewGuid();
        await using (var dbContext = _fixture.CreateDbContext())
        {
            var unitOfWork = new UnitOfWork(dbContext);
            var connection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);
            var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            dbContext.Venues.Add(new Venue(venueId, "Close Before Transaction Dispose Venue"));
            await transaction.CommitAsync(CancellationToken.None);

            // OrderService 的順序：commit 後先關連線（讓 Redis 呼叫不占連線），方法結束時才 dispose 交易。
            await connection.DisposeAsync();
            dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);

            var act = async () => await transaction.DisposeAsync();
            await act.Should().NotThrowAsync();
            dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        }

        await using var readDbContext = _fixture.CreateDbContext();
        (await readDbContext.Venues.AnyAsync(v => v.Id == venueId)).Should().BeTrue();
    }

    [Fact]
    public async Task OpenConnectionAsync_WhenTransactionDisposedWithoutCommit_RollsBackAndConnectionStillCloses()
    {
        var venueId = Guid.NewGuid();
        await using (var dbContext = _fixture.CreateDbContext())
        {
            var unitOfWork = new UnitOfWork(dbContext);
            var connection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);

            await using (var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None))
            {
                dbContext.Venues.Add(new Venue(venueId, "Open Connection Rollback Venue"));
                await dbContext.SaveChangesAsync();
            }

            await connection.DisposeAsync();
            dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        }

        await using var readDbContext = _fixture.CreateDbContext();
        (await readDbContext.Venues.AnyAsync(v => v.Id == venueId)).Should().BeFalse("未 commit 的交易必須回滾");
    }

    [Fact]
    public async Task DisposeAsync_WhenCloseFails_LogsWarningAndDoesNotThrow()
    {
        var closeFailure = new InvalidOperationException("close failed");
        await using var dbContext = _fixture.CreateDbContext(new ThrowingCloseInterceptor(closeFailure));
        var logger = new ListLogger<UnitOfWork>();
        var unitOfWork = new UnitOfWork(dbContext, logger);
        var connection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);

        // dispose 常在例外展開途中執行；重拋會蓋掉呼叫端原本的例外（design.md 決策 2）。
        var act = async () => await connection.DisposeAsync();

        await act.Should().NotThrowAsync();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Exception == closeFailure);
    }

    // 以下 UOW-TX-024～026：連線歸屬依工作單元記錄的呼叫端連線判斷，不看連線目前是否開啟；
    // 否則先前關閉失敗遺留的開啟連線會讓下單主路徑的 OpenConnectionAsync 失敗，或讓之後的交易被誤判為呼叫端連線。

    [Fact]
    public async Task OpenConnectionAsync_OnLeftoverOpenConnection_TakesOverAndClosesOnDispose()
    {
        var closeInterceptor = new SwitchableCloseInterceptor(new InvalidOperationException("close failed"));
        await using var dbContext = _fixture.CreateDbContext(closeInterceptor);
        var unitOfWork = new UnitOfWork(dbContext);
        await using (var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None))
        {
            await transaction.CommitAsync(CancellationToken.None);
        }
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Open, "前提：提交後的關閉失敗留下開啟的連線");
        closeInterceptor.IsFailing = false;

        var connection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);

        // 只標記記錄、不經 EF 開啟的話，遺留連線計數為 0，第一次查詢後就被 EF 關閉歸還，釋放後一樣是 Closed，
        // 所以須斷言查詢之間連線維持開啟、在同一 backend（呼叫端連線的單一連線保證）。
        var firstBackendPid = await GetBackendPidAsync(dbContext);
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Open, "接手後查詢結束不得歸還連線");
        var secondBackendPid = await GetBackendPidAsync(dbContext);
        secondBackendPid.Should().Be(firstBackendPid);
        await connection.DisposeAsync();
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed, "接手的遺留連線須在呼叫端釋放時關閉");
    }

    [Fact]
    public async Task BeginTransactionAsync_AfterCallerConnectionDisposed_OwnsConnection()
    {
        var venueId = Guid.NewGuid();
        var closeFailure = new InvalidOperationException("close failed");
        var closeInterceptor = new SwitchableCloseInterceptor(closeFailure) { IsFailing = false };
        await using var dbContext = _fixture.CreateDbContext(closeInterceptor);
        var logger = new ListLogger<UnitOfWork>();
        var unitOfWork = new UnitOfWork(dbContext, logger);
        await (await unitOfWork.OpenConnectionAsync(CancellationToken.None)).DisposeAsync();
        closeInterceptor.IsFailing = true;

        await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        dbContext.Venues.Add(new Venue(venueId, "After Caller Connection Disposed Venue"));
        var commit = () => transaction.CommitAsync(CancellationToken.None);

        // 釋放後記錄未重設的話，交易會被判為呼叫端連線，由 EF 在提交時關閉，關閉失敗便從 CommitAsync 傳出。
        await commit.Should().NotThrowAsync();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Exception.Should().BeSameAs(closeFailure);
        await using var readDbContext = _fixture.CreateDbContext();
        (await readDbContext.Venues.AnyAsync(v => v.Id == venueId)).Should().BeTrue();
    }

    [Fact]
    public async Task BeginTransactionAsync_AfterCallerConnectionCloseFailed_OwnsConnection()
    {
        var venueId = Guid.NewGuid();
        var closeFailure = new InvalidOperationException("close failed");
        await using var dbContext = _fixture.CreateDbContext(new ThrowingCloseInterceptor(closeFailure));
        var logger = new ListLogger<UnitOfWork>();
        var unitOfWork = new UnitOfWork(dbContext, logger);
        await (await unitOfWork.OpenConnectionAsync(CancellationToken.None)).DisposeAsync();

        var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        dbContext.Venues.Add(new Venue(venueId, "After Caller Connection Close Failed Venue"));

        // 呼叫端連線關閉失敗時記錄仍須重設；否則交易被判為呼叫端連線，由 EF 在提交時關閉，關閉失敗便從 CommitAsync 傳出。
        var commit = () => transaction.CommitAsync(CancellationToken.None);
        await commit.Should().NotThrowAsync();
        var dispose = async () => await transaction.DisposeAsync();
        await dispose.Should().NotThrowAsync();

        logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Exception)
            .Should().Equal(closeFailure, closeFailure);
        await using var readDbContext = _fixture.CreateDbContext();
        (await readDbContext.Venues.AnyAsync(v => v.Id == venueId)).Should().BeTrue();
    }

    // 以下 UOW-TX-029、030：交易未完成就釋放呼叫端連線時，提交後的關閉改由 EF 執行，關閉失敗會從 CommitAsync 傳出；
    // 以 Warning 讓違反順序的呼叫端在 log 可見（design.md 決策 6）。

    [Fact]
    public async Task DisposeAsync_WhileTransactionInProgress_LogsWarningAndCommitStillPersists()
    {
        var venueId = Guid.NewGuid();
        await using var dbContext = _fixture.CreateDbContext();
        var logger = new ListLogger<UnitOfWork>();
        var unitOfWork = new UnitOfWork(dbContext, logger);
        var connection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);
        await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        dbContext.Venues.Add(new Venue(venueId, "Dispose Connection Before Commit Venue"));

        var dispose = async () => await connection.DisposeAsync();
        await dispose.Should().NotThrowAsync();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Exception.Should().BeNull();

        var commit = () => transaction.CommitAsync(CancellationToken.None);
        await commit.Should().NotThrowAsync();
        // 只記 Warning 而跳過關閉的話計數殘留，提交後連線仍開啟、留到 DbContext 結束（design.md 決策 6）。
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        await using var readDbContext = _fixture.CreateDbContext();
        (await readDbContext.Venues.AnyAsync(v => v.Id == venueId)).Should().BeTrue();
    }

    [Fact]
    public async Task OpenConnectionAsync_OnLeftoverOpenConnection_WhenCancelled_ThrowsAndDoesNotTakeOver()
    {
        var closeInterceptor = new SwitchableCloseInterceptor(new InvalidOperationException("close failed"));
        await using var dbContext = _fixture.CreateDbContext(closeInterceptor);
        var unitOfWork = new UnitOfWork(dbContext);
        await using (var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None))
        {
            await transaction.CommitAsync(CancellationToken.None);
        }
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Open, "前提：提交後的關閉失敗留下開啟的連線");
        closeInterceptor.IsFailing = false;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // 遺留連線上 EF 只加計數、不檢查 token，取消會被靜默吞掉，要到下一次查詢才拋出。
        var open = () => unitOfWork.OpenConnectionAsync(cancellation.Token);
        await open.Should().ThrowAsync<OperationCanceledException>();

        var connection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);
        await connection.DisposeAsync();
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed, "取消的開啟不得接手或增加計數");
    }

    [Fact]
    public async Task DisposeAsync_AfterCommit_DoesNotLogWarning()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var logger = new ListLogger<UnitOfWork>();
        var unitOfWork = new UnitOfWork(dbContext, logger);
        var connection = await unitOfWork.OpenConnectionAsync(CancellationToken.None);
        var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        dbContext.Venues.Add(new Venue(Guid.NewGuid(), "Dispose Connection After Commit Venue"));
        await transaction.CommitAsync(CancellationToken.None);

        // OrderService 的正常順序，不得被誤判為違反順序。
        await connection.DisposeAsync();
        await transaction.DisposeAsync();

        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
    }

    private static Task<int> GetBackendPidAsync(ApplicationDbContext dbContext)
        => dbContext.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
}
