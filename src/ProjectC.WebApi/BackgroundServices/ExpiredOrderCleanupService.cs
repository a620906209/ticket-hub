using Microsoft.Extensions.Hosting;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Orders;
using ProjectC.Domain.Orders;
using Serilog.Context;

namespace ProjectC.WebApi.BackgroundServices;

/// <summary>
/// 週期性掃描逾時仍為 Pending 的訂單並取消，讓資料庫的持久化狀態真正反映訂單已終結
/// （見 ticketing-order-management design.md 決策 2）。
/// </summary>
public sealed class ExpiredOrderCleanupService : BackgroundService
{
    private const string LeaderElectionLockKey = "order-cleanup:lock";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly OrderCleanupOptions _options;
    private readonly IDistributedLock _distributedLock;
    private readonly DistributedLockOptions _lockOptions;
    private readonly ILogger<ExpiredOrderCleanupService> _logger;

    public ExpiredOrderCleanupService(
        IServiceScopeFactory scopeFactory,
        IDateTimeProvider dateTimeProvider,
        OrderCleanupOptions options,
        IDistributedLock distributedLock,
        DistributedLockOptions lockOptions,
        ILogger<ExpiredOrderCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _dateTimeProvider = dateTimeProvider;
        _options = options;
        _distributedLock = distributedLock;
        _lockOptions = lockOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // TraceId scope 必須包住整個 try/catch（含週期層級失敗的 LogError），不能只包 happy path——
            // 否則掃描階段本身拋出的例外會在 using 已經 Dispose、TraceId 已跳出 scope 後才被外層 catch
            // 記錄，導致這筆「整輪失敗」的日誌反而沒有這一輪的 TraceId（實測發現，不符合 spec「同一輪次
            // 所有日誌共用同一個關聯值」的要求）。直接呼叫 CleanupOnceCoreAsync（不透過下方公開的
            // CleanupOnceAsync），避免巢狀 push 出兩個不同的 TraceId。
            using (LogContext.PushProperty("TraceId", Guid.NewGuid().ToString()))
            {
                try
                {
                    await CleanupOnceWithLeaderElectionAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Expired order cleanup cycle failed; will retry next interval.");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds)), stoppingToken);
        }
    }

    /// <summary>供整合測試直接呼叫（不透過 DI 容器解析這個服務本身），公開一輪完整清理的邏輯，
    /// 含這一輪專屬的 TraceId scope（與 <see cref="ExecuteAsync"/> 走的正式排程路徑各自獨立產生一個
    /// 新值，語意一致：兩者都代表「一輪」，只是觸發來源不同）。</summary>
    public async Task CleanupOnceAsync(CancellationToken cancellationToken)
    {
        using (LogContext.PushProperty("TraceId", Guid.NewGuid().ToString()))
        {
            await CleanupOnceCoreAsync(cancellationToken);
        }
    }

    /// <summary>正式排程路徑（<see cref="ExecuteAsync"/>）的進入點：先取得整輪清理的 leader lock，取得者才清理，
    /// 鎖被其他實例持有時跳過本輪。Redis 不可用（含取鎖拋出非連線類 Redis 例外）時 fail-open：記 Warning 後照常清理，
    /// 與排隊入場推進的 fail-closed 刻意不同——清理的正確性由資料庫的取消操作保證，Redis 鎖只是避免重複工作的
    /// 效率優化（order-cleanup-leader-lock design.md 決策 3）。</summary>
    public async Task CleanupOnceWithLeaderElectionAsync(CancellationToken cancellationToken)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds) * _lockOptions.LockTtlMultiplier);
        var (lockResult, acquireException) = await TryAcquireLeaderLockAsync(ttl, cancellationToken);

        if (lockResult.LockResult == LockResult.HeldByOther)
        {
            _logger.LogDebug("Skipping this expired order cleanup cycle; lock {LockKey} is held by another instance.", LeaderElectionLockKey);
            return;
        }

        if (lockResult.LockResult == LockResult.RedisUnavailable)
        {
            _logger.LogWarning(
                acquireException,
                "Redis is unavailable; running this expired order cleanup cycle without lock {LockKey} (fail-open, correctness relies on database row locks).",
                LeaderElectionLockKey);
            await CleanupOnceCoreAsync(cancellationToken);
            return;
        }

        try
        {
            await CleanupOnceCoreAsync(cancellationToken);
        }
        finally
        {
            await ReleaseLeaderLockAsync(lockResult.OwnerToken!);
        }
    }

    // RedisDistributedLock 只把連線／逾時例外轉成 RedisUnavailable；failover 時的 RedisServerException（READONLY／LOADING／OOM）
    // 會直接拋出，在這裡一併視同 RedisUnavailable，不放寬共用鎖的 catch（會改變排隊推進的 fail-closed 語意）。
    private async Task<(LockAcquisitionResult LockResult, Exception? AcquireException)> TryAcquireLeaderLockAsync(
        TimeSpan ttl, CancellationToken cancellationToken)
    {
        try
        {
            return (await _distributedLock.TryAcquireAsync(LeaderElectionLockKey, ttl, cancellationToken), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (new LockAcquisitionResult(LockResult.RedisUnavailable, null), exception);
        }
    }

    // 釋放失敗只記 Warning（TTL 會兜底），不得遮蔽 finally 前清理本身拋出的例外；Warning 不帶 owner token。
    // 釋放刻意不帶呼叫端的 token：關機取消（stoppingToken）正是 finally 最需要釋放的時機，帶著已取消的 token 會讓釋放被跳過、
    // 鎖殘留到 TTL（預設 15 分鐘）才失效；單次 Redis 指令的耗時由連線逾時設定界定。
    private async Task ReleaseLeaderLockAsync(string ownerToken)
    {
        try
        {
            await _distributedLock.ReleaseAsync(LeaderElectionLockKey, ownerToken, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Failed to release lock {LockKey}; it will expire by TTL.", LeaderElectionLockKey);
        }
    }

    private async Task CleanupOnceCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> expiredOrderIds;
        using (var scanScope = _scopeFactory.CreateScope())
        {
            var orderRepository = scanScope.ServiceProvider.GetRequiredService<IOrderRepository>();
            expiredOrderIds = await orderRepository.GetExpiredPendingOrderIdsAsync(_dateTimeProvider.UtcNow, cancellationToken);
        }

        foreach (var orderId in expiredOrderIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var orderScope = _scopeFactory.CreateScope();
                var orderService = orderScope.ServiceProvider.GetRequiredService<OrderService>();

                var result = await orderService.CancelExpiredOrderAsync(orderId, cancellationToken);
                if (!result.IsSuccess)
                {
                    _logger.LogWarning(
                        "Expired order {OrderId} was not cancelled: {ErrorType} {ErrorMessage}",
                        orderId, result.Error!.Type, result.Error.Message);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unexpected error while cancelling expired order {OrderId}.", orderId);
            }
        }
    }
}
