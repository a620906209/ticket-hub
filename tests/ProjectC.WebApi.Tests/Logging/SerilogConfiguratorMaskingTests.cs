using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProjectC.WebApi.Logging;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog;
using Serilog.Core;

namespace ProjectC.WebApi.Tests.Logging;

// exception-log-masking tasks.md 4.0：外層（等級＋遮蔽）／內層（所有輸出 sink）結構的行為斷言。
[Collection(GlobalLoggingStateGroup.Name)]
public sealed class SerilogConfiguratorMaskingTests : IDisposable
{
    private const string Secret = "s3cret-pw";

    private readonly TextWriter _originalConsoleOut = Console.Out;
    private readonly StringWriter _consoleOut = new();

    public SerilogConfiguratorMaskingTests()
    {
        Console.SetOut(_consoleOut);
    }

    public void Dispose()
    {
        Console.SetOut(_originalConsoleOut);
    }

    [Fact]
    public void Configure_ErrorWithSecretInException_ConsoleOutputHasNoPlaintext()
    {
        var memorySink = new InMemoryLogEventSink();
        var logger = BuildLogger(memorySink, [("Serilog:MinimumLevel:Default", "Information")]);

        logger.Error(new InvalidOperationException("Password=" + Secret), "boom");
        ((IDisposable)logger).Dispose();

        _consoleOut.ToString().Should().Contain("Password=***").And.NotContain(Secret);
        memorySink.Events.Single().Exception!.ToString().Should().NotContain(Secret);
    }

    [Fact]
    public void Configure_EnvironmentVariableOverride_StillAppliesToOuterLevels()
    {
        const string variable = "Serilog__MinimumLevel__Override__ProjectC.EnvProbe";
        Environment.SetEnvironmentVariable(variable, "Debug");
        try
        {
            var memorySink = new InMemoryLogEventSink();
            var logger = BuildLogger(
                memorySink,
                [("Serilog:MinimumLevel:Default", "Information")],
                addEnvironmentVariables: true);

            var context = logger.ForContext(Constants.SourceContextPropertyName, "ProjectC.EnvProbe");
            context.Debug("env-debug");
            ((IDisposable)logger).Dispose();

            memorySink.Events.Select(e => e.MessageTemplate.Text).Should().Contain("env-debug");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void Configure_DebugOverrideWithExceptionAndConsoleRestricted_ProducesNoConsoleOutputAndDoesNotThrow()
    {
        var memorySink = new InMemoryLogEventSink();
        var logger = BuildLogger(
            memorySink,
            [
                ("Serilog:MinimumLevel:Default", "Information"),
                ("Serilog:MinimumLevel:Override:ProjectC.Quiet", "Debug"),
            ]);

        var context = logger.ForContext(Constants.SourceContextPropertyName, "ProjectC.Quiet");
        var act = () => context.Debug(new InvalidOperationException("Password=" + Secret), "quiet-debug");
        act.Should().NotThrow();
        ((IDisposable)logger).Dispose();

        _consoleOut.ToString().Should().BeEmpty();
        memorySink.Events.Single().Exception!.ToString().Should().NotContain(Secret);
    }

    [Fact]
    public void Configure_AuditToConsoleDeclaredInConfiguration_ConsoleOutputHasNoPlaintext()
    {
        var memorySink = new InMemoryLogEventSink();
        var logger = BuildLogger(
            memorySink,
            [
                ("Serilog:MinimumLevel:Default", "Information"),
                ("Serilog:AuditTo:0:Name", "Console"),
            ],
            includeWriteTo: false);

        logger.Error(new InvalidOperationException("Password=" + Secret), "boom");
        ((IDisposable)logger).Dispose();

        _consoleOut.ToString().Should().Contain("Password=***").And.NotContain(Secret);
    }

    private static Serilog.Core.Logger BuildLogger(
        InMemoryLogEventSink memorySink,
        (string Key, string Value)[] settings,
        bool addEnvironmentVariables = false,
        bool includeWriteTo = true)
    {
        var values = new Dictionary<string, string?>();
        if (includeWriteTo)
        {
            values["Serilog:WriteTo:0:Name"] = "Console";
            values["Serilog:WriteTo:0:Args:restrictedToMinimumLevel"] = "Information";
        }

        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var builder = new ConfigurationBuilder().AddInMemoryCollection(values);
        if (addEnvironmentVariables)
        {
            builder.AddEnvironmentVariables();
        }

        var services = new ServiceCollection().AddSingleton<ILogEventSink>(memorySink).BuildServiceProvider();
        var hostContext = new HostBuilderContext(new Dictionary<object, object>()) { Configuration = builder.Build() };

        var loggerConfiguration = new LoggerConfiguration();
        SerilogConfigurator.Configure(hostContext, services, loggerConfiguration);
        return loggerConfiguration.CreateLogger();
    }
}
