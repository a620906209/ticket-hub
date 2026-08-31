using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog;
using Serilog.Events;

namespace ProjectC.WebApi.Tests.Observability;

public class LogLevelConfigurationTests
{
    // 對應 AC: OBS-LOG-LEVEL-VIA-CONFIG
    [Fact]
    public void MinimumLevel_FromConfiguration_FiltersEventsBelowThreshold()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Warning",
            })
            .Build();

        var sink = new InMemoryLogEventSink();
        var logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("這筆 Information 等級的日誌應該被設定的門檻擋掉");
        logger.Warning("這筆 Warning 等級的日誌應該正常輸出");

        sink.Events.Should().ContainSingle();
        sink.Events.Single().Level.Should().Be(LogEventLevel.Warning);
    }
}
