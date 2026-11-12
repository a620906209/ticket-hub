using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ProjectC.Domain.Notifications;
using ProjectC.Domain.Payments;
using ProjectC.Infrastructure.Tickets;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog.Core;
using Serilog.Events;

namespace ProjectC.WebApi.Tests.Startup;

// mock-service-production-guard MOCKGUARD-001～007：Production 綁定 Mock 付款／Email 時預設拒絕啟動，
// 明示放行時記一筆 Warning。比照 CaptchaOptionsFailFastTests／CaptchaAcceptAnyWarningTests，不需容器。
public class MockServicesProductionGuardTests
{
    private const string WarningMarker = "Mocks:AllowInProduction";

    private sealed class NonMockPaymentGateway : IPaymentGateway
    {
        public Task<PaymentResult> ChargeAsync(Guid orderId, decimal amount, CancellationToken cancellationToken)
            => Task.FromResult(PaymentResult.Succeeded);
    }

    private sealed class NonMockEmailNotificationService : IEmailNotificationService
    {
        public Task NotifyTicketsIssuedAsync(string toEmail, string eventTitle, Guid orderId, int ticketCount, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Flags]
    private enum Replace
    {
        None = 0,
        Payment = 1,
        Email = 2,
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string environmentName,
        string? allowFlag,
        Replace replace,
        InMemoryLogEventSink sink)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environmentName);
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Jwt:Issuer"] = "ProjectC.Tests",
                    ["Jwt:Audience"] = "ProjectC.Tests.Client",
                    ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-prod-32+",
                    ["Jwt:AccessTokenExpirationMinutes"] = "30",
                    [$"{TicketSigningOptions.SectionName}:{nameof(TicketSigningOptions.SigningKey)}"] = new string('x', 32),
                    // api 容器的環境變數可能帶有 DevelopmentSeed／AdminBootstrap 設定，明確清空，避免 Production 啟動時動到資料庫。
                    ["AdminBootstrap:Email"] = string.Empty,
                    ["AdminBootstrap:Password"] = string.Empty,
                };
                if (allowFlag is not null)
                {
                    values["Mocks:AllowInProduction"] = allowFlag;
                }

                configBuilder.AddInMemoryCollection(values);
            });
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ILogEventSink>(sink);
                if (replace.HasFlag(Replace.Payment))
                {
                    services.RemoveAll<IPaymentGateway>();
                    services.AddSingleton<IPaymentGateway, NonMockPaymentGateway>();
                }

                if (replace.HasFlag(Replace.Email))
                {
                    services.RemoveAll<IEmailNotificationService>();
                    services.AddSingleton<IEmailNotificationService, NonMockEmailNotificationService>();
                }
            });
        });

    private static List<LogEvent> StartHostAndCaptureGuardWarnings(string environmentName, string? allowFlag, Replace replace)
    {
        var sink = new InMemoryLogEventSink();
        using var factory = CreateFactory(environmentName, allowFlag, replace, sink);

        _ = factory.Server;

        return sink.Events
            .Where(logEvent => logEvent.Level == LogEventLevel.Warning && logEvent.RenderMessage().Contains(WarningMarker))
            .ToList();
    }

    private static Exception CaptureStartupFailure(string environmentName, string? allowFlag, Replace replace)
    {
        using var factory = CreateFactory(environmentName, allowFlag, replace, new InMemoryLogEventSink());

        var act = () => factory.Server;

        return act.Should().Throw<Exception>().Which;
    }

    private static OptionsValidationException FindOptionsValidationException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is OptionsValidationException validation)
            {
                return validation;
            }
        }

        throw new Xunit.Sdk.XunitException($"例外鏈中沒有 OptionsValidationException：{exception}");
    }

    // MOCKGUARD-001／002
    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "false")]
    [InlineData("production", null)]
    public void CreatingHost_InProductionWithoutFlagAndMocks_ThrowsOptionsValidationMentioningPaymentAndEmail(
        string environmentName, string? allowFlag)
    {
        var exception = CaptureStartupFailure(environmentName, allowFlag, Replace.None);

        var message = FindOptionsValidationException(exception).Message;
        message.Should().Contain("IPaymentGateway");
        message.Should().Contain("IEmailNotificationService");
        message.Should().Contain(WarningMarker);
    }

    // MOCKGUARD-003
    [Fact]
    public void CreatingHost_InProductionWithOnlyEmailMock_ThrowsMentioningOnlyEmail()
    {
        var exception = CaptureStartupFailure("Production", null, Replace.Payment);

        var message = FindOptionsValidationException(exception).Message;
        message.Should().Contain("IEmailNotificationService");
        message.Should().NotContain("IPaymentGateway");
    }

    // MOCKGUARD-004：非空前提——同一擷取方法在 006 情境必須抓得到 Warning，否則空集合會假通過。
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("true")]
    public void StartingHost_InProductionWithNonMockServices_StartsWithoutWarning(string? allowFlag)
    {
        var warnings = StartHostAndCaptureGuardWarnings("Production", allowFlag, Replace.Payment | Replace.Email);

        warnings.Should().BeEmpty();
    }

    // MOCKGUARD-005
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void StartingHost_InNonProductionWithMocks_StartsWithoutWarning(string environmentName)
    {
        var warnings = StartHostAndCaptureGuardWarnings(environmentName, null, Replace.None);

        warnings.Should().BeEmpty();
    }

    // MOCKGUARD-006
    [Fact]
    public void StartingHost_InProductionWithFlagAndMocks_StartsAndLogsExactlyOneWarning()
    {
        var warnings = StartHostAndCaptureGuardWarnings("Production", "true", Replace.None);

        warnings.Should().ContainSingle();
        var rendered = warnings[0].RenderMessage();
        rendered.Should().Contain("IPaymentGateway");
        rendered.Should().Contain("IEmailNotificationService");
    }

    // MOCKGUARD-006：只有一項為 Mock 時，Warning 只列該項。
    [Fact]
    public void StartingHost_InProductionWithFlagAndOnlyEmailMock_WarningListsOnlyEmail()
    {
        var warnings = StartHostAndCaptureGuardWarnings("Production", "true", Replace.Payment);

        warnings.Should().ContainSingle();
        var rendered = warnings[0].RenderMessage();
        rendered.Should().Contain("IEmailNotificationService");
        rendered.Should().NotContain("IPaymentGateway");
    }

    // MOCKGUARD-007：無法解析成 bool 時必須失敗，不得以 false 靜默取代（Development 也一樣，旗標值本身無效）。
    [Fact]
    public void CreatingHost_WithNonBooleanFlag_Throws()
    {
        var act = () =>
        {
            using var factory = CreateFactory("Testing", "yes", Replace.None, new InMemoryLogEventSink());
            _ = factory.Server;
        };

        act.Should().Throw<Exception>();
    }
}
