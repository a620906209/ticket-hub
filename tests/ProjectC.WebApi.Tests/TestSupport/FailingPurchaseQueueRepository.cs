using Microsoft.Extensions.DependencyInjection;
using ProjectC.Domain.PurchaseQueue;

namespace ProjectC.WebApi.Tests.TestSupport;

/// <summary>包住真正的 IPurchaseQueueRepository，可設定特定方法對特定 entryId 拋例外，模擬「Redis 端
/// 決策已透過真實 Lua Script 完成、但 Postgres 落地失敗／持有決策的程序中止」的情境，不需要手動竄改
/// Redis 資料本身來偽造過渡態（purchase-queue-redis-admission design.md Decision 4／5，
/// 見 tasks.md 7.6／9.3／9.4／9.5／9.15a）。</summary>
internal sealed class FailingPurchaseQueueRepository : IPurchaseQueueRepository
{
    private readonly IPurchaseQueueRepository _inner;
    private readonly HashSet<Guid> _failOnAdmitBatchIds;
    private readonly HashSet<Guid> _failOnAdmitIfWaitingIds;
    private readonly HashSet<Guid> _failOnExpireBatchIds;

    public FailingPurchaseQueueRepository(
        IPurchaseQueueRepository inner,
        IEnumerable<Guid>? failOnAdmitBatchIds = null,
        IEnumerable<Guid>? failOnAdmitIfWaitingIds = null,
        IEnumerable<Guid>? failOnExpireBatchIds = null)
    {
        _inner = inner;
        _failOnAdmitBatchIds = failOnAdmitBatchIds?.ToHashSet() ?? [];
        _failOnAdmitIfWaitingIds = failOnAdmitIfWaitingIds?.ToHashSet() ?? [];
        _failOnExpireBatchIds = failOnExpireBatchIds?.ToHashSet() ?? [];
    }

    public Task<PurchaseQueueEntry?> GetCurrentAsync(Guid eventId, Guid memberId, CancellationToken cancellationToken)
        => _inner.GetCurrentAsync(eventId, memberId, cancellationToken);

    public Task<PurchaseQueueEntry?> GetForUpdateAsync(Guid eventId, Guid memberId, CancellationToken cancellationToken)
        => _inner.GetForUpdateAsync(eventId, memberId, cancellationToken);

    public Task<IReadOnlyList<PurchaseQueueEntry>> GetActiveForReconciliationAsync(Guid eventId, CancellationToken cancellationToken)
        => _inner.GetActiveForReconciliationAsync(eventId, cancellationToken);

    public Task<int> CountWaitingAheadAsync(Guid eventId, DateTime joinedAtUtc, Guid entryId, CancellationToken cancellationToken)
        => _inner.CountWaitingAheadAsync(eventId, joinedAtUtc, entryId, cancellationToken);

    public Task<int> AdmitBatchAsync(IReadOnlyCollection<Guid> entryIds, DateTime admittedAtUtc, DateTime admissionExpiresAtUtc, CancellationToken cancellationToken)
    {
        if (entryIds.Any(_failOnAdmitBatchIds.Contains))
        {
            throw new InvalidOperationException("Simulated AdmitBatchAsync failure for test.");
        }

        return _inner.AdmitBatchAsync(entryIds, admittedAtUtc, admissionExpiresAtUtc, cancellationToken);
    }

    public Task<int> ExpireBatchAsync(IReadOnlyCollection<Guid> entryIds, CancellationToken cancellationToken)
    {
        if (entryIds.Any(_failOnExpireBatchIds.Contains))
        {
            throw new InvalidOperationException("Simulated ExpireBatchAsync failure for test.");
        }

        return _inner.ExpireBatchAsync(entryIds, cancellationToken);
    }

    public Task<int> AdmitIfWaitingAsync(Guid entryId, DateTime admittedAtUtc, DateTime admissionExpiresAtUtc, CancellationToken cancellationToken)
    {
        if (_failOnAdmitIfWaitingIds.Contains(entryId))
        {
            throw new InvalidOperationException("Simulated AdmitIfWaitingAsync failure for test.");
        }

        return _inner.AdmitIfWaitingAsync(entryId, admittedAtUtc, admissionExpiresAtUtc, cancellationToken);
    }

    public Task<PurchaseQueueEntry> AddOrGetExistingAsync(PurchaseQueueEntry newEntry, CancellationToken cancellationToken)
        => _inner.AddOrGetExistingAsync(newEntry, cancellationToken);
}

/// <summary>比照既有 BlockingScopeFactory（PurchaseQueueAdmissionServiceLeaderElectionTests）的攔截模式，
/// 但攔截對象換成 IPurchaseQueueRepository，讓呼叫端可注入任意包裝邏輯（例如 FailingPurchaseQueueRepository）
/// 而不需要為每種情境各寫一個專屬的 ScopeFactory。</summary>
internal sealed class PurchaseQueueRepositoryInterceptingScopeFactory : IServiceScopeFactory
{
    private readonly IServiceScopeFactory _inner;
    private readonly Func<IPurchaseQueueRepository, IPurchaseQueueRepository> _wrap;

    public PurchaseQueueRepositoryInterceptingScopeFactory(IServiceScopeFactory inner, Func<IPurchaseQueueRepository, IPurchaseQueueRepository> wrap)
    {
        _inner = inner;
        _wrap = wrap;
    }

    public IServiceScope CreateScope() => new InterceptingServiceScope(_inner.CreateScope(), _wrap);

    private sealed class InterceptingServiceScope : IServiceScope
    {
        private readonly IServiceScope _inner;

        public InterceptingServiceScope(IServiceScope inner, Func<IPurchaseQueueRepository, IPurchaseQueueRepository> wrap)
        {
            _inner = inner;
            ServiceProvider = new InterceptingServiceProvider(inner.ServiceProvider, wrap);
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose() => _inner.Dispose();
    }

    private sealed class InterceptingServiceProvider : IServiceProvider
    {
        private readonly IServiceProvider _inner;
        private readonly Func<IPurchaseQueueRepository, IPurchaseQueueRepository> _wrap;

        public InterceptingServiceProvider(IServiceProvider inner, Func<IPurchaseQueueRepository, IPurchaseQueueRepository> wrap)
        {
            _inner = inner;
            _wrap = wrap;
        }

        public object? GetService(Type serviceType)
        {
            var service = _inner.GetService(serviceType);
            return service is IPurchaseQueueRepository repository ? _wrap(repository) : service;
        }
    }
}
