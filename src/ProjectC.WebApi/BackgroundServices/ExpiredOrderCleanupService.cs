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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly OrderCleanupOptions _options;
    private readonly ILogger<ExpiredOrderCleanupService> _logger;

    public ExpiredOrderCleanupService(
        IServiceScopeFactory scopeFactory,
        IDateTimeProvider dateTimeProvider,
        OrderCleanupOptions options,
        ILogger<ExpiredOrderCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _dateTimeProvider = dateTimeProvider;
        _options = options;
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
                    await CleanupOnceCoreAsync(stoppingToken);
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
