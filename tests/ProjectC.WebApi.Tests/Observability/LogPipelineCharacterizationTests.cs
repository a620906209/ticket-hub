using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProjectC.WebApi.Logging;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ProjectC.WebApi.Tests.Observability;

// exception-log-masking tasks.md 1.1：鎖定 SerilogConfigurator 現行的等級／sink 行為，作為改成
// 外層（等級＋遮蔽）／內層（sink）雙 logger 結構前後的不變條件。這是特性測試，不是 RED。
[Collection(GlobalLoggingStateGroup.Name)]
public sealed class LogPipelineCharacterizationTests : IDisposable
{
    private readonly TextWriter _originalConsoleOut = Console.Out;
    private readonly StringWriter _consoleOut = new();

    public LogPipelineCharacterizationTests()
    {
        Console.SetOut(_consoleOut);
    }

    public void Dispose()
    {
        Console.SetOut(_originalConsoleOut);
    }

    [Fact]
    public void Configure_WithAppSettingsStyleOverride_SuppressesCategoryBelowThreshold()
    {
        var memorySink = new InMemoryLogEventSink();
        using var logger = BuildLogger(
            memorySink,
            ("Serilog:MinimumLevel:Default", "Information"),
            ("Serilog:MinimumLevel:Override:Microsoft.AspNetCore", "Warning"));

        var framework = logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.AspNetCore.Hosting.Diagnostics");
        framework.Information("framework-info");
        framework.Warning("framework-warning");

        memorySink.Events.Select(e => e.MessageTemplate.Text).Should().BeEquivalentTo(["framework-warning"]);
    }

    [Fact]
    public void Configure_ApplicationInformationLog_ReachesInjectedSink()
    {
        var memorySink = new InMemoryLogEventSink();
        using var logger = BuildLogger(memorySink, ("Serilog:MinimumLevel:Default", "Information"));

        logger.ForContext(Constants.SourceContextPropertyName, "ProjectC.Something").Information("app-info");

        memorySink.Events.Select(e => e.MessageTemplate.Text).Should().Contain("app-info");
    }

    [Fact]
    public void Configure_WithSeqServerUrl_AddsSeqSink()
    {
        using var fakeSeq = new FakeSeqIngestionServer();
        var memorySink = new InMemoryLogEventSink();
        var logger = BuildLogger(
            memorySink,
            ("Serilog:MinimumLevel:Default", "Information"),
            ("Seq:ServerUrl", fakeSeq.BaseUrl));

        logger.Information("seq-probe");
        ((IDisposable)logger).Dispose();

        string.Concat(fakeSeq.Bodies).Should().Contain("seq-probe");
    }

    [Fact]
    public void Configure_OverrideBelowDefault_ReachesInjectedSinkButNotRestrictedConsole()
    {
        var memorySink = new InMemoryLogEventSink();
        var logger = BuildLogger(
            memorySink,
            ("Serilog:MinimumLevel:Default", "Information"),
            ("Serilog:MinimumLevel:Override:ProjectC.X", "Debug"));

        var context = logger.ForContext(Constants.SourceContextPropertyName, "ProjectC.X");
        context.Debug("debug-below-default");
        context.Information("info-visible-on-console");
        ((IDisposable)logger).Dispose();

        memorySink.Events.Select(e => e.MessageTemplate.Text)
            .Should().Contain(["debug-below-default", "info-visible-on-console"]);
        _consoleOut.ToString().Should().Contain("info-visible-on-console").And.NotContain("debug-below-default");
    }

    [Fact]
    public void Configure_DefaultWarningWithBroaderInformationOverride_BehavesTheSame()
    {
        var memorySink = new InMemoryLogEventSink();
        var logger = BuildLogger(
            memorySink,
            ("Serilog:MinimumLevel:Default", "Warning"),
            ("Serilog:MinimumLevel:Override:ProjectC", "Information"));

        logger.ForContext(Constants.SourceContextPropertyName, "ProjectC.Y").Information("info-from-override");
        logger.ForContext(Constants.SourceContextPropertyName, "Other.Z").Information("info-suppressed");
        ((IDisposable)logger).Dispose();

        memorySink.Events.Select(e => e.MessageTemplate.Text).Should().BeEquivalentTo(["info-from-override"]);
        _consoleOut.ToString().Should().Contain("info-from-override").And.NotContain("info-suppressed");
    }

    private static Serilog.Core.Logger BuildLogger(InMemoryLogEventSink memorySink, params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["Serilog:WriteTo:0:Name"] = "Console",
            ["Serilog:WriteTo:0:Args:restrictedToMinimumLevel"] = "Information",
        };
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection().AddSingleton<ILogEventSink>(memorySink).BuildServiceProvider();
        var hostContext = new HostBuilderContext(new Dictionary<object, object>()) { Configuration = configuration };

        var loggerConfiguration = new LoggerConfiguration();
        SerilogConfigurator.Configure(hostContext, services, loggerConfiguration);
        return loggerConfiguration.CreateLogger();
    }
}
