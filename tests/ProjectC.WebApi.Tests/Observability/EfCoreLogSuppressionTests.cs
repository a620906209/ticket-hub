using FluentAssertions;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog.Events;

namespace ProjectC.WebApi.Tests.Observability;

public class EfCoreLogSuppressionTests : IClassFixture<ObservabilityWebApplicationFactory>
{
    private readonly ObservabilityWebApplicationFactory _factory;

    public EfCoreLogSuppressionTests(ObservabilityWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.LogSink.Clear();
    }

    // 對應 AC: OBS-EF-COMMAND-LOG-SUPPRESSED
    [Fact]
    public async Task NormalEfCoreQuery_ProducesNoInformationLevelDatabaseCommandLog()
    {
        var client = _factory.CreateClient();

        // GetEventsHandler 內部會經由 EF Core 查詢資料庫，觸發至少一次 SQL 指令。
        var response = await client.GetAsync("/api/events");
        response.EnsureSuccessStatusCode();

        var suppressedEvents = _factory.LogSink.Events.Where(IsInformationOrBelowDatabaseCommandLog).ToList();

        suppressedEvents.Should().BeEmpty(
            "appsettings.json 的 Serilog:MinimumLevel:Override 已把 Microsoft.EntityFrameworkCore.Database.Command 調到 Warning，一般查詢不應產生 Information 等級的 SQL 指令日誌");
    }

    private static bool IsInformationOrBelowDatabaseCommandLog(LogEvent logEvent)
    {
        if (logEvent.Level > LogEventLevel.Information)
        {
            return false;
        }

        return logEvent.Properties.TryGetValue("SourceContext", out var sourceContext)
            && sourceContext is ScalarValue { Value: string context }
            && context.StartsWith("Microsoft.EntityFrameworkCore.Database.Command", StringComparison.Ordinal);
    }
}
