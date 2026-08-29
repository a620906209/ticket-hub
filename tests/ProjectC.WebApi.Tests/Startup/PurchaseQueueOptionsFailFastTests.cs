using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ProjectC.Infrastructure.Tickets;

namespace ProjectC.WebApi.Tests.Startup;

// purchase-queue spec「入場名額上限、逾時時間與推進間隔須為正數設定」PQ-CONFIG-001／002
// （rate-limiting-queue design.md 決策 3；比照既有 JwtOptionsFailFastTests 的手法）。
public class PurchaseQueueOptionsFailFastTests
{
    private static Dictionary<string, string?> BaseConfiguration(int maxConcurrentAdmittedBuyers, int admissionTtlSeconds, int pollingIntervalSeconds) => new()
    {
        ["Jwt:Issuer"] = "ProjectC.Tests",
        ["Jwt:Audience"] = "ProjectC.Tests.Client",
        ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-prod-32+",
        ["Jwt:AccessTokenExpirationMinutes"] = "30",
        [$"{TicketSigningOptions.SectionName}:{nameof(TicketSigningOptions.SigningKey)}"] = new string('x', 32),
        ["PurchaseQueue:MaxConcurrentAdmittedBuyers"] = maxConcurrentAdmittedBuyers.ToString(),
        ["PurchaseQueue:AdmissionTtlSeconds"] = admissionTtlSeconds.ToString(),
        ["PurchaseQueue:PollingIntervalSeconds"] = pollingIntervalSeconds.ToString(),
    };

    private static bool ContainsOptionsValidationException(Exception exception)
    {
        var current = exception;
        while (current is not null)
        {
            if (current is OptionsValidationException)
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    [Fact]
    public void CreatingHost_WithAllPositiveValues_DoesNotThrow()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
                configBuilder.AddInMemoryCollection(BaseConfiguration(maxConcurrentAdmittedBuyers: 50, admissionTtlSeconds: 300, pollingIntervalSeconds: 5)));
        });

        var act = () => factory.Server;

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0, 300, 5)]
    [InlineData(-1, 300, 5)]
    [InlineData(50, 0, 5)]
    [InlineData(50, -1, 5)]
    [InlineData(50, 300, 0)]
    [InlineData(50, 300, -1)]
    public void CreatingHost_WithAnyNonPositiveValue_ThrowsOptionsValidationException(
        int maxConcurrentAdmittedBuyers, int admissionTtlSeconds, int pollingIntervalSeconds)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
                configBuilder.AddInMemoryCollection(BaseConfiguration(maxConcurrentAdmittedBuyers, admissionTtlSeconds, pollingIntervalSeconds)));
        });

        var act = () => factory.Server;

        act.Should().Throw<Exception>()
            .Where(e => e is OptionsValidationException || ContainsOptionsValidationException(e));
    }
}
