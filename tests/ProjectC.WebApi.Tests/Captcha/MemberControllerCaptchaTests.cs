using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ProjectC.Application.Authentication.Login;
using ProjectC.Application.Members.Register;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Captcha;

// CAPTCHA-REG-001／002（HTTP 端到端）：直接使用 CustomWebApplicationFactory（已預設把
// ICaptchaService 替換為 FakeCaptchaService，見該檔案），附帶已知的固定 token／答案送出註冊請求
// ——真正的雜湊、TTL、一次性語意已在 RedisCaptchaServiceTests 用真實 Redis 驗證，此處不重複驗證。
public class MemberControllerCaptchaTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public MemberControllerCaptchaTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Register_WithCorrectCaptcha_Succeeds()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterMemberRequest(AuthTestHelper.NewEmail(), AuthTestHelper.DefaultPassword, "Alice", FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // CAPTCHA-REG-001：驗證碼答案錯誤時被拒絕、不建立任何會員。Title MUST 為可區分的 "CaptchaInvalid"
    // （而非泛用 "Validation"），前端據此判斷是否為驗證碼錯誤而不是任何 400。
    [Fact]
    public async Task Register_WithWrongCaptchaAnswer_Returns400WithCaptchaInvalidTitleAndDoesNotCreateMember()
    {
        var email = AuthTestHelper.NewEmail();

        var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterMemberRequest(email, AuthTestHelper.DefaultPassword, "Alice", FakeCaptchaService.ValidToken, "WRONG"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        using (var document = JsonDocument.Parse(body))
        {
            document.RootElement.GetProperty("title").GetString().Should().Be("CaptchaInvalid");
        }

        var loginAttempt = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(email, AuthTestHelper.DefaultPassword, FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));
        loginAttempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "沒有建立任何會員，帳密登入應失敗（而非 200）");
    }

    // CAPTCHA-REG-002：缺漏 CaptchaToken 欄位時被拒絕。
    [Fact]
    public async Task Register_WithMissingCaptchaToken_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterMemberRequest(AuthTestHelper.NewEmail(), AuthTestHelper.DefaultPassword, "Alice", string.Empty, FakeCaptchaService.ValidAnswer));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
