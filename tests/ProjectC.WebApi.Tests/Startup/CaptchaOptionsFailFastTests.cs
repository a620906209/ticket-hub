using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ProjectC.Infrastructure.Tickets;

namespace ProjectC.WebApi.Tests.Startup;

// captcha-test-mode CAPTCHA-MODE-006／008：Production 不得使用 AcceptAny、模式值無效時啟動階段擋下。
// 比照 RateLimitingOptionsFailFastTests：不需要容器，只驗證 host 建立是否失敗。
public class CaptchaOptionsFailFastTests
{
    private static WebApplicationFactory<Program> CreateFactory(string environmentName, string verificationMode)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environmentName);
            builder.ConfigureAppConfiguration((_, configBuilder) => configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "ProjectC.Tests",
                ["Jwt:Audience"] = "ProjectC.Tests.Client",
                ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-prod-32+",
                ["Jwt:AccessTokenExpirationMinutes"] = "30",
                // 本測試不驗證 Mock 守門；Production 案例須明示放行，否則會先被守門擋下（mock-service-production-guard）。
                ["Mocks:AllowInProduction"] = "true",
                [$"{TicketSigningOptions.SectionName}:{nameof(TicketSigningOptions.SigningKey)}"] = new string('x', 32),
                ["Captcha:VerificationMode"] = verificationMode,
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
    [InlineData("Production")]
    [InlineData("production")]
    public void CreatingHost_InProductionWithAcceptAny_ThrowsOptionsValidationMentioningProduction(string environmentName)
    {
        using var factory = CreateFactory(environmentName, "AcceptAny");

        var act = () => factory.Server;

        var exception = act.Should().Throw<Exception>().Where(e => ContainsOptionsValidationException(e)).Which;
        exception.ToString().Should().Contain("Production");
    }

    // 不對應任何模式的數值（ConfigurationBinder 會把 99 綁成未定義的列舉值）必須由驗證器擋下。
    [Fact]
    public void CreatingHost_WithUndefinedNumericVerificationMode_ThrowsOptionsValidationException()
    {
        using var factory = CreateFactory("Testing", "99");

        var act = () => factory.Server;

        act.Should().Throw<Exception>().Where(e => ContainsOptionsValidationException(e));
    }

    // 拼錯的名稱在無驗證器時 ConfigurationBinder 本就會拋例外，故只是回歸保護，不作為 RED 依據。
    [Fact]
    public void CreatingHost_WithMisspelledVerificationMode_Throws()
    {
        using var factory = CreateFactory("Testing", "acceptany1");

        var act = () => factory.Server;

        act.Should().Throw<Exception>();
    }

    // 防止驗證器誤擋：Production + Enforced 必須能正常啟動。
    [Fact]
    public void CreatingHost_InProductionWithEnforced_StartsNormally()
    {
        using var factory = CreateFactory("Production", "Enforced");

        var act = () => factory.Server;

        act.Should().NotThrow();
    }
}
