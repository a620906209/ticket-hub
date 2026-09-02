using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ProjectC.Application.Common;
using ProjectC.Infrastructure.Security;
using ProjectC.WebApi.BackgroundServices;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace ProjectC.WebApi.Tests.Observability;

// 補上「週期層級失敗」（掃描階段本身拋例外，不是個別項目處理失敗）這條路徑的 TraceId 覆蓋——
// BackgroundServiceTraceIdTests 只涵蓋個別項目失敗的 LogWarning，涵蓋不到 ExecuteAsync 外層
// catch 的 LogError 這條路徑（實測發現：先前的實作這裡沒有 TraceId，已於
// ExpiredOrderCleanupService/PurchaseQueueAdmissionService 修正）。
public class BackgroundServiceCycleLevelFailureTraceIdTests
{
    // 對應 AC: OBS-BACKGROUND-CYCLE-TRACE
    [Fact]
    public async Task ExpiredOrderCleanupService_WhenScanPhaseThrows_CycleFailureLogHasTraceId()
    {
        var sink = new InMemoryLogEventSink();
        var serilogLogger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        using var loggerFactory = new SerilogLoggerFactory(serilogLogger, dispose: true);
        var logger = loggerFactory.CreateLogger<ExpiredOrderCleanupService>();

        // 讓掃描階段（GetExpiredPendingOrderIdsAsync 之前的 CreateScope）直接拋例外，
        // 這條路徑在個別項目的 try/catch 之外，正是 bug 發生的位置。
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Throws(new InvalidOperationException("simulated scan-phase failure"));

        var service = new ExpiredOrderCleanupService(
            scopeFactory.Object, new SystemDateTimeProvider(), new OrderCleanupOptions(), logger);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await WaitUntilAsync(
            () => sink.Events.Any(e => e.RenderMessage().Contains("cleanup cycle failed", StringComparison.Ordinal)),
            "ExpiredOrderCleanupService 應該在掃描階段拋例外後記錄一筆 cleanup cycle failed 日誌");
        await service.StopAsync(CancellationToken.None);

        var cycleFailureEvents = sink.Events
            .Where(e => e.RenderMessage().Contains("cleanup cycle failed", StringComparison.Ordinal))
            .ToList();

        cycleFailureEvents.Should().ContainSingle("掃描階段拋例外應該觸發一次週期層級失敗記錄");
        cycleFailureEvents.Single().Properties.Should().ContainKey("TraceId",
            "週期層級失敗日誌也應該帶有這一輪的 TraceId，不能只有個別項目的 LogWarning 有");
    }

    // 對應 AC: OBS-BACKGROUND-CYCLE-TRACE
    [Fact]
    public async Task PurchaseQueueAdmissionService_WhenScanPhaseThrows_CycleFailureLogHasTraceId()
    {
        var sink = new InMemoryLogEventSink();
        var serilogLogger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        using var loggerFactory = new SerilogLoggerFactory(serilogLogger, dispose: true);
        var logger = loggerFactory.CreateLogger<PurchaseQueueAdmissionService>();

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Throws(new InvalidOperationException("simulated scan-phase failure"));

        var service = new PurchaseQueueAdmissionService(
            scopeFactory.Object,
            new SystemDateTimeProvider(),
            new PurchaseQueueOptions(),
            new FakeDistributedLock(),
            new DistributedLockOptions(),
            logger);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await WaitUntilAsync(
            () => sink.Events.Any(e => e.RenderMessage().Contains("admission cycle failed", StringComparison.Ordinal)),
            "PurchaseQueueAdmissionService 應該在掃描階段拋例外後記錄一筆 admission cycle failed 日誌");
        await service.StopAsync(CancellationToken.None);

        var cycleFailureEvents = sink.Events
            .Where(e => e.RenderMessage().Contains("admission cycle failed", StringComparison.Ordinal))
            .ToList();

        cycleFailureEvents.Should().ContainSingle("掃描階段拋例外應該觸發一次週期層級失敗記錄");
        cycleFailureEvents.Single().Properties.Should().ContainKey("TraceId",
            "週期層級失敗日誌也應該帶有這一輪的 TraceId，不能只有個別項目的 LogWarning 有");
    }

    /// <summary>輪詢等待條件成立，取代固定 <c>Task.Delay</c>——背景服務何時記錄失敗日誌本質上是
    /// 非同步、非固定時間的，用固定延遲賭一個時間點在理論上仍可能 flaky（系統負載高時執行緒被
    /// 延後排程），外部審查抓到這點。逾時直接拋出明確例外（而不是靜默返回讓後面的斷言自己失敗）——
    /// 外部審查提醒：靜默返回會讓測試失敗訊息只顯示「找不到事件」，看不出「是背景服務真的沒有
    /// 觸發，還是單純逾時太短」這個關鍵區別，拋例外能把「等待逾時」本身當成明確、可辨識的失敗原因。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string timeoutMessage, int timeoutMs = 5000, int pollIntervalMs = 25)
    {
        var elapsed = 0;
        while (!condition())
        {
            if (elapsed >= timeoutMs)
            {
                throw new TimeoutException($"等待條件在 {timeoutMs}ms 內未成立：{timeoutMessage}");
            }

            await Task.Delay(pollIntervalMs);
            elapsed += pollIntervalMs;
        }
    }
}
