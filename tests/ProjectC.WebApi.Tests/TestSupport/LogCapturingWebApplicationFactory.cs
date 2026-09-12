using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;

namespace ProjectC.WebApi.Tests.TestSupport;

// 比照 ObservabilityWebApplicationFactory：Redis／Postgres 皆正常（沿用 CustomWebApplicationFactory
// 既有的隔離容器），只額外掛一個 ILogEventSink 供測試斷言結構化 log（query-caching tasks.md
// 7.1c／7.1d：Redis 連線正常但快取內容損壞導致 JsonException 的情境）。
public sealed class LogCapturingWebApplicationFactory : CustomWebApplicationFactory
{
    public InMemoryLogEventSink LogSink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(LogSink));
    }
}
