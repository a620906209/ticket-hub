using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;

namespace ProjectC.WebApi.Tests.TestSupport;

// TestServer 不經過 Kestrel，KestrelServerOptions.Limits（全域本文上限、MinRequestBodyDataRate）在
// TestServer 上不生效，改用 UseKestrel(0) 監聽隨機 port 跑真正的 Kestrel（request-body-size-limits
// design.md 決策 5）。LogCapturingWebApplicationFactory 是 sealed，這裡比照它自行掛 ILogEventSink。
public sealed class KestrelWebApplicationFactory : CustomWebApplicationFactory
{
    public KestrelWebApplicationFactory()
    {
        UseKestrel(0);
    }

    public InMemoryLogEventSink LogSink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(LogSink));
    }
}
