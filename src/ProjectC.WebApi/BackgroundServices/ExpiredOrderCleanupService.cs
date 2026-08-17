using Microsoft.Extensions.Hosting;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Orders;
using ProjectC.Domain.Orders;

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
            try
            {
                await CleanupOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Expired order cleanup cycle failed; will retry next interval.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds)), stoppingToken);
        }
    }

    /// <summary>供整合測試直接呼叫（不透過 DI 容器解析這個服務本身），公開一輪完整清理的邏輯。</summary>
    public async Task CleanupOnceAsync(CancellationToken cancellationToken)
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

            using var orderScope = _scopeFactory.CreateScope();
            var orderService = orderScope.ServiceProvider.GetRequiredService<OrderService>();

            try
            {
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
