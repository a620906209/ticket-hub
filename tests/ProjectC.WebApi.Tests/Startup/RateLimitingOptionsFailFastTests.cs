using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ProjectC.Infrastructure.Tickets;

namespace ProjectC.WebApi.Tests.Startup;

// api-rate-limiting spec「限流設定值須為正數，缺漏時採用明確預設值」RL-009：RateLimitingOptions 沒有
// ValidateOnStart()，但 Program.cs 在 app.Build() 後強制解析一次 IOptions&lt;RateLimitingOptions&gt;，
// 讓 DataAnnotations 驗證確實在應用程式啟動過程中執行（design.md 決策 1）。RL-008（缺漏時採用預設值）
// 見 RateLimitingOptionsTests（Application.Tests，直接驗證 C# 層級預設值）。
public class RateLimitingOptionsFailFastTests
{
    private static Dictionary<string, string?> BaseConfiguration(int permitLimit, int windowSeconds) => new()
    {
        ["Jwt:Issuer"] = "ProjectC.Tests",
        ["Jwt:Audience"] = "ProjectC.Tests.Client",
        ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-prod-32+",
        ["Jwt:AccessTokenExpirationMinutes"] = "30",
        [$"{TicketSigningOptions.SectionName}:{nameof(TicketSigningOptions.SigningKey)}"] = new string('x', 32),
        ["RateLimiting:PermitLimit"] = permitLimit.ToString(),
        ["RateLimiting:WindowSeconds"] = windowSeconds.ToString(),
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

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(20, 0)]
    [InlineData(20, -1)]
    public void CreatingHost_WithNonPositiveRateLimitingValues_ThrowsOptionsValidationException(int permitLimit, int windowSeconds)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
                configBuilder.AddInMemoryCollection(BaseConfiguration(permitLimit, windowSeconds)));
        });

        var act = () => factory.Server;

        act.Should().Throw<Exception>()
            .Where(e => e is OptionsValidationException || ContainsOptionsValidationException(e));
    }
}
