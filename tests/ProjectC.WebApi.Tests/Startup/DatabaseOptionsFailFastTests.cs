using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ProjectC.Infrastructure.Tickets;

namespace ProjectC.WebApi.Tests.Startup;

// db-timeout-pool-tuning DBL-005／006／007：Database 設定範圍外的值須在啟動階段擋下。
// 比照 CaptchaOptionsFailFastTests：不需要容器，只驗證 host 建立是否失敗。
public class DatabaseOptionsFailFastTests
{
    private const string PasswordSentinel = "SECRET-SENTINEL";

    private static WebApplicationFactory<Program> CreateFactory(string settingName, int value)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configBuilder) => configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "ProjectC.Tests",
                ["Jwt:Audience"] = "ProjectC.Tests.Client",
                ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-prod-32+",
                ["Jwt:AccessTokenExpirationMinutes"] = "30",
                [$"{TicketSigningOptions.SectionName}:{nameof(TicketSigningOptions.SigningKey)}"] = new string('x', 32),
                ["ConnectionStrings:DefaultConnection"] = $"Host=unused;Database=unused;Username=unused;Password={PasswordSentinel}",
                [$"Database:{settingName}"] = value.ToString(),
            }));
        });

    private static bool ContainsOptionsValidationException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OptionsValidationException)
            {
                return true;
            }
        }

        return false;
    }

    [Theory]
    [InlineData("CommandTimeoutSeconds", 0)]
    [InlineData("CommandTimeoutSeconds", 3601)]
    [InlineData("ConnectionTimeoutSeconds", 0)]
    [InlineData("ConnectionTimeoutSeconds", 301)]
    [InlineData("MaxPoolSize", 0)]
    [InlineData("MaxPoolSize", 1001)]
    public void CreatingHost_WithOutOfRangeDatabaseSetting_ThrowsOptionsValidationMentioningSettingName(string settingName, int value)
    {
        using var factory = CreateFactory(settingName, value);

        var act = () => factory.Server;

        var exception = act.Should().Throw<Exception>().Where(e => ContainsOptionsValidationException(e)).Which;
        exception.ToString().Should().Contain(settingName);
    }

    [Theory]
    [InlineData("CommandTimeoutSeconds", 1)]
    [InlineData("CommandTimeoutSeconds", 3600)]
    [InlineData("ConnectionTimeoutSeconds", 1)]
    [InlineData("ConnectionTimeoutSeconds", 300)]
    [InlineData("MaxPoolSize", 1)]
    [InlineData("MaxPoolSize", 1000)]
    public void CreatingHost_WithBoundaryDatabaseSettings_StartsNormally(string settingName, int value)
    {
        using var factory = CreateFactory(settingName, value);

        var act = () => factory.Server;

        act.Should().NotThrow();
    }

    [Fact]
    public void CreatingHost_WithInvalidDatabaseSetting_DoesNotLeakConnectionStringPassword()
    {
        using var factory = CreateFactory("MaxPoolSize", 0);

        var act = () => factory.Server;

        var exception = act.Should().Throw<Exception>().Which;
        exception.ToString().Should().NotContain(PasswordSentinel);
    }
}
