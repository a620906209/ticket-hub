using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using ProjectC.Application.Authentication.Login;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Captcha;

// CAPTCHA-RATE-001／004：captcha policy（design.md 決策 6）。每個測試方法各自建立獨立的 factory
// （比照既有 QueryCachingComponentTests 手法），不透過 IClassFixture 共用——固定的分區鍵（同一 IP）
// 若跨測試方法共用同一個限流器實例，額度會互相污染。
public class CaptchaRateLimitTests
{
    private static Task<HttpResponseMessage> AttemptLoginAsync(HttpClient client)
        => client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(AuthTestHelper.NewEmail(), "WrongPassword1", FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));

    [Fact]
    public async Task Get_WithOneMoreRequestThanThePermitLimit_IsRejectedWith429AndRetryAfter()
    {
        using var factory = new CaptchaRateLimitedWebApplicationFactory();
        using var client = factory.CreateClient();

        for (var i = 0; i < CaptchaRateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            await client.GetAsync("/api/captcha");
        }

        var response = await client.GetAsync("/api/captcha");

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.TryGetValues("Retry-After", out _).Should().BeTrue();
    }

    // CAPTCHA-RATE-004（方向一）：captcha policy 額度用盡後，login policy 依自身額度正常處理。
    [Fact]
    public async Task CaptchaLimitExhausted_LoginPolicyStillProcessesIndependently()
    {
        using var factory = new CaptchaRateLimitedWebApplicationFactory();
        using var client = factory.CreateClient();

        for (var i = 0; i < CaptchaRateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            await client.GetAsync("/api/captcha");
        }
        (await client.GetAsync("/api/captcha")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "captcha policy 額度應該已用盡");

        var loginResponse = await AttemptLoginAsync(client);

        loginResponse.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "login policy 是獨立計數的 policy，不受 captcha 額度影響");
    }

    // CAPTCHA-RATE-004（方向二）：login policy 額度用盡後，captcha policy 依自身額度正常處理。
    [Fact]
    public async Task LoginLimitExhausted_CaptchaPolicyStillProcessesIndependently()
    {
        using var factory = new CaptchaRateLimitedWebApplicationFactory();
        using var client = factory.CreateClient();

        for (var i = 0; i < CaptchaRateLimitedWebApplicationFactory.LoginPermitLimit; i++)
        {
            await AttemptLoginAsync(client);
        }
        (await AttemptLoginAsync(client)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "login policy 額度應該已用盡");

        var captchaResponse = await client.GetAsync("/api/captcha");

        captchaResponse.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "captcha policy 是獨立計數的 policy，不受 login 額度影響");
    }
}
