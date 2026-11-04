using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProjectC.WebApi.Logging;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog;
using Serilog.Core;
using Serilog.Extensions.Logging;

namespace ProjectC.WebApi.Tests.Logging;

// exception-log-masking tasks.md 4.4：遮蔽涵蓋 Console、Seq 與框架來源，而不只記憶體 sink。
[Collection(GlobalLoggingStateGroup.Name)]
public sealed class AllSinksMaskingTests : IDisposable
{
    private const string Secret = "s3cret-pw";

    private readonly TextWriter _originalConsoleOut = Console.Out;
    private readonly StringWriter _consoleOut = new();

    public AllSinksMaskingTests()
    {
        Console.SetOut(_consoleOut);
    }

    public void Dispose()
    {
        Console.SetOut(_originalConsoleOut);
    }

    // 對應 OBS-EXMASK-001（Console）
    [Fact]
    public void Console_WhenExceptionHasPassword_OutputIsMasked()
    {
        var logger = BuildLogger(new InMemoryLogEventSink(), seqServerUrl: null);

        logger.Error(new InvalidOperationException("Password=" + Secret), "console-probe");
        ((IDisposable)logger).Dispose();

        _consoleOut.ToString().Should().Contain("Password=***").And.NotContain(Secret);
    }

    // 對應 OBS-EXMASK-001（Seq）
    [Fact]
    public void Seq_WhenExceptionHasPassword_RequestBodyIsMasked()
    {
        using var fakeSeq = new FakeSeqIngestionServer();
        var logger = BuildLogger(new InMemoryLogEventSink(), fakeSeq.BaseUrl);

        logger.Error(new InvalidOperationException("Password=" + Secret), "seq-probe");
        ((IDisposable)logger).Dispose();

        var body = string.Concat(fakeSeq.Bodies);
        body.Should().Contain("seq-probe").And.Contain("Password=***").And.NotContain(Secret);
    }

    // 對應 OBS-EXMASK-016：框架來源（ILoggerFactory 類別名稱為框架命名空間）走同一條管線。
    [Fact]
    public void FrameworkSource_WhenExceptionHasPassword_AllSinksAreMasked()
    {
        using var fakeSeq = new FakeSeqIngestionServer();
        var memorySink = new InMemoryLogEventSink();
        var logger = BuildLogger(memorySink, fakeSeq.BaseUrl);
        using (var loggerFactory = new SerilogLoggerFactory(logger, dispose: false))
        {
            loggerFactory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics")
                .LogError(new InvalidOperationException("Password=" + Secret), "framework-probe");
        }

        ((IDisposable)logger).Dispose();

        memorySink.Events.Single().Exception!.ToString().Should().Contain("Password=***").And.NotContain(Secret);
        _consoleOut.ToString().Should().Contain("framework-probe").And.NotContain(Secret);
        var body = string.Concat(fakeSeq.Bodies);
        body.Should().NotBeEmpty().And.Contain("Password=***").And.NotContain(Secret);
    }

    private static Serilog.Core.Logger BuildLogger(InMemoryLogEventSink memorySink, string? seqServerUrl)
    {
        var values = new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Information",
            ["Serilog:WriteTo:0:Name"] = "Console",
        };
        if (seqServerUrl is not null)
        {
            values["Seq:ServerUrl"] = seqServerUrl;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection().AddSingleton<ILogEventSink>(memorySink).BuildServiceProvider();
        var hostContext = new HostBuilderContext(new Dictionary<object, object>()) { Configuration = configuration };

        var loggerConfiguration = new LoggerConfiguration();
        SerilogConfigurator.Configure(hostContext, services, loggerConfiguration);
        return loggerConfiguration.CreateLogger();
    }
}
