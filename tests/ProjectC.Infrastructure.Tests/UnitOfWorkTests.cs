using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
}
