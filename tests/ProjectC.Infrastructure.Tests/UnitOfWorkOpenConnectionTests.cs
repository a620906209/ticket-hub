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

    private sealed class ThrowingCloseInterceptor(Exception failure) : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionClosingAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
            => throw failure;
    }

    private static Task<int> GetBackendPidAsync(ApplicationDbContext dbContext)
        => dbContext.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
}
