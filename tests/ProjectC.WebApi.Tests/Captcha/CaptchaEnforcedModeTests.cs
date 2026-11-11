using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ProjectC.Application.Authentication.Login;
using ProjectC.Application.Members.Register;
using ProjectC.Application.PurchaseQueue.JoinPurchaseQueue;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Captcha;

// CAPTCHA-MODE-001／002：Enforced（含設定缺漏）下，login、register、join purchase queue 三流程
// 以錯誤答案呼叫皆回 400 CaptchaInvalid，且 Redis 的一次性核對（GETDEL）照常刪除 key。
// 這些是既有行為的回歸保護；敏感度以 tasks.md 1.3 的 mutation 驗證。
public class CaptchaEnforcedModeTests : IClassFixture<CaptchaModeTestWebApplicationFactory>
{
    private readonly CaptchaModeTestWebApplicationFactory _factory;

    public CaptchaEnforcedModeTests(CaptchaModeTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<string?> EnforcedModes => new() { null, "Enforced" };

    private static async Task AssertCaptchaInvalidAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("title").GetString().Should().Be("CaptchaInvalid");
    }

    [Theory]
    [MemberData(nameof(EnforcedModes))]
    public async Task Login_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(string? mode)
    {
        var email = AuthTestHelper.NewEmail();
        await AuthTestHelper.RegisterAsync(_factory.WithFakeCaptcha().CreateClient(), email);
        var modeFactory = _factory.WithMode(mode);
        var token = Guid.NewGuid().ToString();
        await CaptchaModeTestData.SeedCaptchaAsync(modeFactory.Services, token, "ABCD");

        var response = await modeFactory.CreateClient().PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(email, AuthTestHelper.DefaultPassword, token, CaptchaModeTestData.WrongAnswer));

        await AssertCaptchaInvalidAsync(response);
        (await CaptchaModeTestData.CaptchaKeyExistsAsync(modeFactory.Services, token)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(EnforcedModes))]
    public async Task Register_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(string? mode)
    {
        var modeFactory = _factory.WithMode(mode);
        var token = Guid.NewGuid().ToString();
        await CaptchaModeTestData.SeedCaptchaAsync(modeFactory.Services, token, "ABCD");

        var response = await modeFactory.CreateClient().PostAsJsonAsync(
            "/api/auth/register",
            new RegisterMemberRequest(AuthTestHelper.NewEmail(), AuthTestHelper.DefaultPassword, "Alice", token, CaptchaModeTestData.WrongAnswer));

        await AssertCaptchaInvalidAsync(response);
        (await CaptchaModeTestData.CaptchaKeyExistsAsync(modeFactory.Services, token)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(EnforcedModes))]
    public async Task JoinPurchaseQueue_WithWrongAnswer_Returns400CaptchaInvalidAndConsumesKey(string? mode)
    {
        var setupFactory = _factory.WithFakeCaptcha();
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(setupFactory);
        var eventId = await CaptchaModeTestData.SeedQueueModeEnabledEventAsync(adminClient);
        var memberClient = await CaptchaModeTestData.CreateAuthenticatedMemberClientAsync(setupFactory, AuthTestHelper.NewEmail());
        var modeFactory = _factory.WithMode(mode);
        var token = Guid.NewGuid().ToString();
        await CaptchaModeTestData.SeedCaptchaAsync(modeFactory.Services, token, "ABCD");

        var modeClient = modeFactory.CreateClient();
        modeClient.DefaultRequestHeaders.Authorization = memberClient.DefaultRequestHeaders.Authorization;
        var response = await modeClient.PostAsJsonAsync(
            $"/api/events/{eventId}/queue/entries",
            new JoinPurchaseQueueRequest(token, CaptchaModeTestData.WrongAnswer));

        await AssertCaptchaInvalidAsync(response);
        (await CaptchaModeTestData.CaptchaKeyExistsAsync(modeFactory.Services, token)).Should().BeFalse();
    }
}
