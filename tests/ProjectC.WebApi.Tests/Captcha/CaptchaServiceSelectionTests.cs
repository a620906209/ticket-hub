using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Infrastructure.Captcha;
using ProjectC.WebApi.Extensions;
using StackExchange.Redis;

namespace ProjectC.WebApi.Tests.Captcha;

// captcha-test-mode design.md 決策 2：AddCaptcha 依 Captcha:VerificationMode 選擇 ICaptchaService 實作，
// 缺漏時必須是強制驗證的 RedisCaptchaService（CAPTCHA-MODE-001）。
public class CaptchaServiceSelectionTests
{
    private static ICaptchaService ResolveCaptchaService(string? mode)
    {
        var values = new Dictionary<string, string?>();
        if (mode is not null)
        {
            values["Captcha:VerificationMode"] = mode;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IConnectionMultiplexer>());
        services.AddSingleton(Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == "Testing"));
        services.AddCaptcha(configuration);

        var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ICaptchaService>();
    }

    [Fact]
    public void ResolveCaptchaService_WhenModeIsAcceptAny_ReturnsAcceptAnyCaptchaService()
    {
        ResolveCaptchaService("AcceptAny").Should().BeOfType<AcceptAnyCaptchaService>();
    }

    [Theory]
    [InlineData("Enforced")]
    [InlineData(null)]
    public void ResolveCaptchaService_WhenModeIsEnforcedOrMissing_ReturnsRedisCaptchaService(string? mode)
    {
        ResolveCaptchaService(mode).Should().BeOfType<RedisCaptchaService>();
    }
}
