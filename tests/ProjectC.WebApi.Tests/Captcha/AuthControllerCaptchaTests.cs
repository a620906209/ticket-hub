using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ProjectC.Application.Authentication.Login;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Captcha;

// CAPTCHA-AUTH-001／002（HTTP 端到端）：直接使用 CustomWebApplicationFactory（已預設把
// ICaptchaService 替換為 FakeCaptchaService，見該檔案），附帶已知的固定 token／答案送出登入請求
// ——真正的雜湊、TTL、一次性語意已在 RedisCaptchaServiceTests 用真實 Redis 驗證，此處不重複驗證。
public class AuthControllerCaptchaTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AuthControllerCaptchaTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Login_WithCorrectCaptcha_Succeeds()
    {
        var email = AuthTestHelper.NewEmail();
        await AuthTestHelper.RegisterAsync(_client, email);

        var response = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(email, AuthTestHelper.DefaultPassword, FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // CAPTCHA-AUTH-001：驗證碼答案錯誤時被拒絕、不核發任何 Token。Title MUST 為可區分的
    // "CaptchaInvalid"（而非泛用 "Validation"），前端據此判斷是否為驗證碼錯誤而不是任何 400
    // （見 ResultExtensions.cs、captcha-verification design.md 決策 8 補充）。
    [Fact]
    public async Task Login_WithWrongCaptchaAnswer_Returns400WithCaptchaInvalidTitleAndDoesNotIssueTokens()
    {
        var email = AuthTestHelper.NewEmail();
        await AuthTestHelper.RegisterAsync(_client, email);

        var response = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(email, AuthTestHelper.DefaultPassword, FakeCaptchaService.ValidToken, "WRONG"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("accessToken");
        body.Should().NotContain("refreshToken");
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("title").GetString().Should().Be("CaptchaInvalid");
    }

    // CAPTCHA-AUTH-002：缺漏 CaptchaAnswer 欄位時被拒絕。這是 FluentValidation 欄位驗證失敗
    // （而不是驗證碼答案錯誤），Title MUST 維持泛用的 "Validation"，與上面 CaptchaInvalid 明確不同
    // ——兩者雖然都是 400，但語意不同，前端須能區分。
    [Fact]
    public async Task Login_WithMissingCaptchaAnswer_Returns400WithValidationTitle()
    {
        var email = AuthTestHelper.NewEmail();
        await AuthTestHelper.RegisterAsync(_client, email);

        var response = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(email, AuthTestHelper.DefaultPassword, FakeCaptchaService.ValidToken, string.Empty));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("title").GetString().Should().Be("Validation");
    }
}
