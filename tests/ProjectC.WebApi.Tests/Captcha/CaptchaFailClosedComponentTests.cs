using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Authentication.Login;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Members.Register;
using ProjectC.Application.PurchaseQueue.JoinPurchaseQueue;
using ProjectC.Domain.Members;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Captcha;

// CAPTCHA-FAIL-001／002：Redis 無法連線時，MUST 回傳明確的技術性錯誤（5xx），MUST NOT 視為驗證通過
// ——不得因無法讀取 Redis 而預設視為驗證通過，回應必須與「驗證碼答案錯誤」的一般 400 Validation
// 明確不同（captcha-verification design.md 決策 5）。指向 127.0.0.1:1 這個無人監聽的 endpoint 驗證的是
// 「TCP 連線被拒絕」這種快速失敗路徑，不涵蓋 CAPTCHA-FAIL-003「連線存在但命令逾時」——後者已在
// RedisCaptchaServiceFailClosedTests（mock 拋出 RedisTimeoutException）與 RedisConnectionConfigurationTests
// （組態驗證）以可決定性重現的方式涵蓋，這裡 MUST NOT 額外嘗試用真實網路逾時重現。
public class CaptchaFailClosedComponentTests : IClassFixture<CaptchaFailClosedWebApplicationFactory>
{
    private readonly CaptchaFailClosedWebApplicationFactory _factory;

    public CaptchaFailClosedComponentTests(CaptchaFailClosedWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static void AssertTechnicalError(HttpResponseMessage response)
    {
        ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(500, "Redis 無法連線 MUST 回傳技術性錯誤，不得包裝成一般 400 Validation");
    }

    [Fact]
    public async Task GetCaptcha_WhenRedisUnavailable_ReturnsTechnicalErrorWithoutTokenOrImage()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/captcha");

        AssertTechnicalError(response);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("imageBase64");
    }

    [Fact]
    public async Task Login_WhenRedisUnavailable_ReturnsTechnicalErrorAndIssuesNoTokens()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(AuthTestHelper.NewEmail(), AuthTestHelper.DefaultPassword, FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));

        AssertTechnicalError(response);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("accessToken");
        body.Should().NotContain("refreshToken");
    }

    [Fact]
    public async Task Register_WhenRedisUnavailable_ReturnsTechnicalErrorAndCreatesNoMember()
    {
        using var client = _factory.CreateClient();
        var email = AuthTestHelper.NewEmail();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterMemberRequest(email, AuthTestHelper.DefaultPassword, "Alice", FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));

        AssertTechnicalError(response);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await dbContext.Members.AsNoTracking().AnyAsync(m => m.Email == email)).Should().BeFalse();
    }

    [Fact]
    public async Task JoinQueue_WhenRedisUnavailable_ReturnsTechnicalErrorAndCreatesNoEntry()
    {
        using var client = _factory.CreateClient();

        // 驗證碼檢查發生在 JoinPurchaseQueueHandler 查詢活動之前（見 design.md 決策 5），
        // 不需要真的建立活動——用不存在的 eventId 即可，Redis 故障會在活動查詢前就讓請求技術性失敗。
        // 註冊／登入本身也需要驗證碼（此刻 Redis 已不可用，無法透過 HTTP 走正常註冊/登入流程），
        // 改直接用 ITokenService 產生一組合法 JWT（不落地 DB，只需要 claims 正確即可通過 [Authorize]）。
        using var scope = _factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        var member = Member.Register(AuthTestHelper.NewEmail(), "Alice", "unused-hash");
        var accessToken = tokenService.GenerateAccessToken(member);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.PostAsJsonAsync(
            $"/api/events/{Guid.NewGuid()}/queue/entries",
            new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));

        AssertTechnicalError(response);

        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await dbContext.PurchaseQueueEntries.AsNoTracking().AnyAsync(e => e.MemberId == member.Id)).Should().BeFalse();
    }
}
