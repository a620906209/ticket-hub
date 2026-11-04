using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Logging;

// exception-log-masking tasks.md 4.3（Scenario 015）：啟動階段失敗走 Program.cs 的 Log.Fatal，
// 經由 bootstrap logger 輸出到 Console，例外文字也要遮蔽。
[Collection(GlobalLoggingStateGroup.Name)]
public sealed class BootstrapLoggerMaskingTests : IDisposable
{
    private const string Secret = "s3cret-pw";

    private readonly TextWriter _originalConsoleOut = Console.Out;
    private readonly StringWriter _consoleOut = new();

    public BootstrapLoggerMaskingTests()
    {
        Console.SetOut(_consoleOut);
    }

    public void Dispose()
    {
        Console.SetOut(_originalConsoleOut);
    }

    [Fact]
    public void Startup_WhenHostBuildFailsWithPassword_FatalLogOnConsoleIsMasked()
    {
        using var factory = new StartupFailingWebApplicationFactory();

        var act = () => factory.CreateClient();

        act.Should().Throw<Exception>();
        var output = _consoleOut.ToString();
        output.Should().Contain("Application terminated unexpectedly")
            .And.Contain("Password=***")
            .And.NotContain(Secret);
    }

    private sealed class StartupFailingWebApplicationFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(_ => throw new InvalidOperationException("Host=db;Password=" + Secret + ";Database=x"));
        }
    }
}
