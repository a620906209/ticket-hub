using Microsoft.Extensions.Hosting;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.PurchaseQueue;
using Serilog.Context;

namespace ProjectC.WebApi.BackgroundServices;

/// <summary>
/// 週期性推進每個開啟熱門搶購模式活動的排隊入場名額，並將已逾時的 Admitted 紀錄標記為 Expired
/// （見 rate-limiting-queue design.md 決策 3）。
/// </summary>
public sealed class PurchaseQueueAdmissionService : BackgroundService
{
    // 涵蓋整輪推進的單一固定 key，不分活動（design.md 決策 1）。
    private const string LeaderElectionLockKey = "purchase-queue-admission:lock";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly PurchaseQueueOptions _options;
    private readonly IDistributedLock _distributedLock;
    private readonly DistributedLockOptions _lockOptions;
    private readonly ILogger<PurchaseQueueAdmissionService> _logger;

    public PurchaseQueueAdmissionService(
        IServiceScopeFactory scopeFactory,
        IDateTimeProvider dateTimeProvider,
        PurchaseQueueOptions options,
        IDistributedLock distributedLock,
        DistributedLockOptions lockOptions,
        ILogger<PurchaseQueueAdmissionService> logger)
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
            // TraceId scope 必須包住整個 try/catch（含週期層級失敗的 LogError），理由與
            // ExpiredOrderCleanupService.ExecuteAsync 相同（實測發現，見該檔案註解）。
            using (LogContext.PushProperty("TraceId", Guid.NewGuid().ToString()))
            {
                try
                {
                    await AdvanceQueueOnceWithLeaderElectionAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Purchase queue admission cycle failed; will retry next interval.");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollingIntervalSeconds)), stoppingToken);
        }
    }

    /// <summary>供整合測試直接呼叫（不透過 DI 容器解析這個服務本身），公開一輪完整推進的邏輯，
    /// 含這一輪專屬的 TraceId scope（與 <see cref="ExecuteAsync"/> 走的正式排程路徑各自獨立產生一個
    /// 新值，語意一致：兩者都代表「一輪」，只是觸發來源不同）。刻意不含取鎖邏輯，供既有
    /// purchase-queue 測試沿用，避免意外依賴 Redis 連線（design.md 決策 6）。</summary>
    public async Task AdvanceQueueOnceAsync(CancellationToken cancellationToken)
    {
        using (LogContext.PushProperty("TraceId", Guid.NewGuid().ToString()))
        {
            await AdvanceQueueOnceCoreAsync(cancellationToken);
        }
    }

    /// <summary>正式輪詢路徑（<see cref="ExecuteAsync"/>）與本次所有涉及分散式鎖的整合測試的
    /// 正確進入點：先嘗試取得本輪的 leader election 鎖，取得成功或 Redis 不可用（fail-open）時
    /// 才執行既有的 <see cref="AdvanceQueueOnceAsync"/>；鎖已被其他實例持有時直接跳過本輪
    /// （design.md 決策 4、6）。</summary>
    public async Task AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken cancellationToken)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(1, _options.PollingIntervalSeconds) * _lockOptions.LockTtlMultiplier);
        var lockResult = await _distributedLock.TryAcquireAsync(LeaderElectionLockKey, ttl, cancellationToken);

        if (lockResult.LockResult == LockResult.HeldByOther)
        {
            _logger.LogDebug("Skipping this purchase queue admission cycle; lock {LockKey} is held by another instance.", LeaderElectionLockKey);
            return;
        }

        try
        {
            await AdvanceQueueOnceAsync(cancellationToken);
        }
        finally
        {
            // RedisUnavailable 代表本來就沒有真的鎖，跳過釋放（design.md 決策 6）。
            if (lockResult.LockResult == LockResult.Acquired)
            {
                await _distributedLock.ReleaseAsync(LeaderElectionLockKey, lockResult.OwnerToken!, cancellationToken);
            }
        }
    }

    private async Task AdvanceQueueOnceCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> queueModeEventIds;
        using (var scanScope = _scopeFactory.CreateScope())
        {
            var eventRepository = scanScope.ServiceProvider.GetRequiredService<IEventRepository>();
            var events = await eventRepository.GetAllAsync(cancellationToken);
            queueModeEventIds = events.Where(e => e.IsQueueModeEnabled).Select(e => e.Id).ToList();
        }

        foreach (var eventId in queueModeEventIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await AdvanceEventQueueAsync(eventId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unexpected error while advancing purchase queue for event {EventId}.", eventId);
            }
        }
    }

    private async Task AdvanceEventQueueAsync(Guid eventId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var eventRepository = scope.ServiceProvider.GetRequiredService<IEventRepository>();
        var purchaseQueueRepository = scope.ServiceProvider.GetRequiredService<IPurchaseQueueRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // 同一活動的悲觀鎖批次查詢：確保單一活動同時只有一次推進在進行，不會超額入場（design.md 決策 3）。
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // Queue Mode 切換的線性化時點，比照決策 4（審查後新增）：AdvanceQueueOnceAsync 交易外的初始掃描
        // 只是快速篩選、不具權威性——若 Admin 在掃描之後、本活動實際處理之前關閉熱門搶購模式，仍須以交易
        // 內鎖定後的最新值為準跳過，不放行任何一筆入場。鎖定順序維持 Event → PurchaseQueueEntry，與
        // OrderService.PlaceOrderAsync 一致，不引入新的死鎖風險（見 design.md 決策 4 鎖定順序段落）。
        var lockedEvent = await eventRepository.GetForUpdateAsync(eventId, cancellationToken);
        if (lockedEvent is null || !lockedEvent.IsQueueModeEnabled)
        {
            return;
        }

        var entries = await purchaseQueueRepository.GetForAdmissionAsync(eventId, cancellationToken);
        var now = _dateTimeProvider.UtcNow;

        var admittedCount = 0;
        foreach (var entry in entries)
        {
            if (entry.Status != PurchaseQueueEntryStatus.Admitted)
            {
                continue;
            }

            if (entry.AdmissionExpiresAtUtc <= now)
            {
                entry.Expire();
            }
            else
            {
                admittedCount++;
            }
        }

        var availableSlots = Math.Max(0, _options.MaxConcurrentAdmittedBuyers - admittedCount);
        if (availableSlots > 0)
        {
            var admissionExpiresAtUtc = now.AddSeconds(_options.AdmissionTtlSeconds);

            // entries 已依 JoinedAtUtc ASC, Id ASC 排序（GetForAdmissionAsync），由舊到新依序推進。
            foreach (var entry in entries.Where(e => e.Status == PurchaseQueueEntryStatus.Waiting).Take(availableSlots))
            {
                entry.Admit(now, admissionExpiresAtUtc);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
