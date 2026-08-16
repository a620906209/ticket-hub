using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeUnitOfWork : IUnitOfWork
{
    public int BeginTransactionCallCount { get; private set; }
    public FakeUnitOfWorkTransaction? LastTransaction { get; private set; }

    public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        BeginTransactionCallCount++;
        LastTransaction = new FakeUnitOfWorkTransaction();
        return Task.FromResult<IUnitOfWorkTransaction>(LastTransaction);
    }
}

public sealed class FakeUnitOfWorkTransaction : IUnitOfWorkTransaction
{
    public bool Committed { get; private set; }
    public bool RolledBack { get; private set; }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        Committed = true;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        RolledBack = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
