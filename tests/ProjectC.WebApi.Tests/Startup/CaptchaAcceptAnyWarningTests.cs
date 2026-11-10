using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Infrastructure.Tickets;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog.Core;
using Serilog.Events;

namespace ProjectC.WebApi.Tests.Startup;

// captcha-test-mode CAPTCHA-MODE-007／009：AcceptAny 啟用時啟動階段記一筆 Warning，Enforced／缺漏時不記。
// 專案以 Serilog 取代預設 logger provider，故改用既有的 DI ILogEventSink 擷取（見 SerilogConfigurator）。
public class CaptchaAcceptAnyWarningTests
{
    private const string WarningText = "驗證碼核對已停用";

    private static List<LogEvent> StartHostAndCaptureWarnings(string environmentName, string? verificationMode)
    {
        var sink = new InMemoryLogEventSink();
        var values = new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "ProjectC.Tests",
            ["Jwt:Audience"] = "ProjectC.Tests.Client",
            ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-prod-32+",
            ["Jwt:AccessTokenExpirationMinutes"] = "30",
            [$"{TicketSigningOptions.SectionName}:{nameof(TicketSigningOptions.SigningKey)}"] = new string('x', 32),
        };
        if (verificationMode is not null)
        {
            values["Captcha:VerificationMode"] = verificationMode;
        }

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environmentName);
            builder.ConfigureAppConfiguration((_, configBuilder) => configBuilder.AddInMemoryCollection(values));
            builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(sink));
        });

        _ = factory.Server;

        return sink.Events
            .Where(logEvent => logEvent.Level == LogEventLevel.Warning && logEvent.RenderMessage().Contains(WarningText))
            .ToList();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void StartingHost_InNonProductionWithAcceptAny_LogsExactlyOneDisabledWarning(string environmentName)
    {
        var warnings = StartHostAndCaptureWarnings(environmentName, "AcceptAny");

        warnings.Should().ContainSingle();
        warnings[0].Properties["SourceContext"].ToString().Should().Be("\"ProjectC.Captcha\"");
    }

    [Theory]
    [InlineData("Enforced")]
    [InlineData(null)]
    public void StartingHost_WithEnforcedOrMissingMode_LogsNoDisabledWarning(string? verificationMode)
    {
        StartHostAndCaptureWarnings("Testing", verificationMode).Should().BeEmpty();
    }
}
