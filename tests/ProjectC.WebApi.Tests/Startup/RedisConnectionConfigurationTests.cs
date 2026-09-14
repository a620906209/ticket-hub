using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Infrastructure.Tickets;
using StackExchange.Redis;

namespace ProjectC.WebApi.Tests.Startup;

// captcha-verification design.md 決策 11／CAPTCHA-FAIL-003 的組態層級驗證：確認共用 IConnectionMultiplexer
// 的逾時設定確實顯式生效，不依賴 StackExchange.Redis 函式庫的預設值。比照既有 RateLimitingOptionsFailFastTests
// 手法，用可決定性重現的方式驗證，不用真實逾時計時。
public class RedisConnectionConfigurationTests
{
    private static Dictionary<string, string?> BaseConfiguration() => new()
    {
        ["Jwt:Issuer"] = "ProjectC.Tests",
        ["Jwt:Audience"] = "ProjectC.Tests.Client",
        ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-prod-32+",
        ["Jwt:AccessTokenExpirationMinutes"] = "30",
        [$"{TicketSigningOptions.SectionName}:{nameof(TicketSigningOptions.SigningKey)}"] = new string('x', 32),
        ["ConnectionStrings:Redis"] = "redis:6379",
    };

    [Fact]
    public void ConnectionMultiplexer_HasExplicitTimeoutsConfigured()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configBuilder) => configBuilder.AddInMemoryCollection(BaseConfiguration()));
        });

        var multiplexer = factory.Services.GetRequiredService<IConnectionMultiplexer>();
        var configurationOptions = ConfigurationOptions.Parse(multiplexer.Configuration);

        configurationOptions.ConnectTimeout.Should().Be(2000);
        configurationOptions.SyncTimeout.Should().Be(2000);
        configurationOptions.AsyncTimeout.Should().Be(2000);
        configurationOptions.ConnectRetry.Should().Be(1);
    }
}
