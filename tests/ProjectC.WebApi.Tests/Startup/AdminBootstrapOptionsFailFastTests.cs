using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ProjectC.Infrastructure.Tickets;

namespace ProjectC.WebApi.Tests.Startup;

// production-admin-bootstrap ADMIN-BOOT-005／006／007／010：設定不完整或不合法時啟動階段擋下，
// 且錯誤訊息只含設定項名稱與規則描述、不含密碼值。比照 CaptchaOptionsFailFastTests：不需要容器，只驗證 host 建立是否失敗。
public class AdminBootstrapOptionsFailFastTests
{
    private const string ValidEmail = "first-admin@example.com";
    private const string ValidPassword = "Valid-Passw0rd";

    private static WebApplicationFactory<Program> CreateFactory(string environmentName, string? email, string? password)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environmentName);
            builder.ConfigureAppConfiguration((_, configBuilder) => configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "ProjectC.Tests",
                ["Jwt:Audience"] = "ProjectC.Tests.Client",
                ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-prod-32+",
                ["Jwt:AccessTokenExpirationMinutes"] = "30",
                [$"{TicketSigningOptions.SectionName}:{nameof(TicketSigningOptions.SigningKey)}"] = new string('x', 32),
                ["AdminBootstrap:Email"] = email,
                ["AdminBootstrap:Password"] = password,
            }));
        });

    private static string CollectExceptionText(Exception exception)
    {
        var texts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            texts.Add(current.ToString());
        }

        return string.Join(Environment.NewLine, texts);
    }

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

    private static string CreateHostAndCollectFailureText(string environmentName, string? email, string? password)
    {
        using var factory = CreateFactory(environmentName, email, password);

        var act = () => factory.Server;

        var exception = act.Should().Throw<Exception>().Where(e => ContainsOptionsValidationException(e)).Which;
        return CollectExceptionText(exception);
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    public void CreatingHost_WithOnlyEmail_FailsMentioningPasswordSetting(string environmentName)
    {
        var text = CreateHostAndCollectFailureText(environmentName, ValidEmail, null);

        text.Should().Contain("AdminBootstrap:Password");
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    public void CreatingHost_WithOnlyPassword_FailsMentioningEmailSettingAndNotPassword(string environmentName)
    {
        var text = CreateHostAndCollectFailureText(environmentName, null, ValidPassword);

        text.Should().Contain("AdminBootstrap:Email");
        text.Should().NotContain(ValidPassword);
    }

    [Theory]
    [InlineData("Testing", "not-an-email")]
    [InlineData("Testing", " first-admin@example.com")]
    [InlineData("Testing", "first-admin@example.com ")]
    [InlineData("Development", "not-an-email")]
    [InlineData("Development", " first-admin@example.com")]
    public void CreatingHost_WithInvalidEmail_FailsMentioningEmailSetting(string environmentName, string email)
    {
        var text = CreateHostAndCollectFailureText(environmentName, email, ValidPassword);

        text.Should().Contain("AdminBootstrap:Email");
        text.Should().NotContain(ValidPassword);
    }

    [Theory]
    [InlineData("Testing", "Ab1")]
    [InlineData("Testing", "NoDigitsHere")]
    [InlineData("Testing", "12345678")]
    [InlineData("Development", "Ab1")]
    [InlineData("Development", "NoDigitsHere")]
    [InlineData("Development", "12345678")]
    public void CreatingHost_WithWeakPassword_FailsMentioningPasswordSettingWithoutLeakingValue(string environmentName, string password)
    {
        var text = CreateHostAndCollectFailureText(environmentName, ValidEmail, password);

        text.Should().Contain("AdminBootstrap:Password");
        text.Should().NotContain(password);
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    [InlineData("Production")]
    public void CreatingHost_WithNeitherSettingConfigured_StartsNormally(string environmentName)
    {
        using var factory = CreateFactory(environmentName, null, null);

        var act = () => factory.Server;

        act.Should().NotThrow();
    }

    [Fact]
    public void CreatingHost_WithWhitespaceOnlySettings_StartsNormally()
    {
        using var factory = CreateFactory("Testing", "  ", "  ");

        var act = () => factory.Server;

        act.Should().NotThrow();
    }
}
