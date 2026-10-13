using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Authentication;
using ProjectC.Application.Authentication.Login;
using ProjectC.Application.Authentication.Logout;
using ProjectC.Application.Authentication.PasswordReset;
using ProjectC.Application.Authentication.Refresh;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Auth;

public class AuthControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AuthControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Register_WithNewEmail_Returns201()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            email = AuthTestHelper.NewEmail(),
            password = AuthTestHelper.DefaultPassword,
            displayName = "Alice",
            captchaToken = FakeCaptchaService.ValidToken,
            captchaAnswer = FakeCaptchaService.ValidAnswer,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_Returns409()
    {
        var email = AuthTestHelper.NewEmail();
        await AuthTestHelper.RegisterAsync(_client, email);

        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = AuthTestHelper.DefaultPassword,
            displayName = "Alice Again",
            captchaToken = FakeCaptchaService.ValidToken,
            captchaAnswer = FakeCaptchaService.ValidAnswer,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Login_WithCorrectCredentials_ReturnsTokens()
    {
        var email = AuthTestHelper.NewEmail();
        await AuthTestHelper.RegisterAsync(_client, email);

        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, AuthTestHelper.DefaultPassword, FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokensDto>();
        tokens!.AccessToken.Should().NotBeNullOrEmpty();
        tokens.RefreshToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        var email = AuthTestHelper.NewEmail();
        await AuthTestHelper.RegisterAsync(_client, email);

        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "WrongPassword1", FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_WithUnknownEmail_Returns401()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(AuthTestHelper.NewEmail(), AuthTestHelper.DefaultPassword, FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_WithDeactivatedAccount_Returns403()
    {
        // 自己升級成 Admin 後停用自己的帳號，純粹是測試上的簡化（省去建立第二個帳號），
        // 重點在驗證「停用後登入回 403」這個行為，不代表產品上支援自我停用。
        var email = AuthTestHelper.NewEmail();
        await AuthTestHelper.RegisterAsync(_client, email);
        await AuthTestHelper.PromoteToAdminAsync(_factory.Services, email);

        using var adminClient = _factory.CreateClient();
        var adminTokens = await AuthTestHelper.LoginAsync(adminClient, email);
        adminClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminTokens.AccessToken);

        var member = await adminClient.GetFromJsonAsync<MemberProfileResponse>("/api/members/me");
        var deactivateResponse = await adminClient.PostAsync($"/api/admin/members/{member!.Id}/deactivate", content: null);
        deactivateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, AuthTestHelper.DefaultPassword, FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));
        loginResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Refresh_WithValidToken_ReturnsNewTokens()
    {
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(_client);

        var response = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(tokens.RefreshToken));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var newTokens = await response.Content.ReadFromJsonAsync<AuthTokensDto>();
        newTokens!.RefreshToken.Should().NotBe(tokens.RefreshToken);
    }

    [Fact]
    public async Task Refresh_ReusingAlreadyRotatedToken_Returns401()
    {
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(_client);
        var firstRefresh = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(tokens.RefreshToken));
        firstRefresh.StatusCode.Should().Be(HttpStatusCode.OK);

        // 用同一組已被輪替過的舊 Refresh Token 再次換發，應被視為疑似遭竊而拒絕。
        var secondRefresh = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(tokens.RefreshToken));

        secondRefresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_ThenRefreshWithSameToken_Returns401()
    {
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var logoutResponse = await _client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(tokens.RefreshToken));
        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        _client.DefaultRequestHeaders.Authorization = null;
        var refreshResponse = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(tokens.RefreshToken));

        refreshResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // AUTH-LOGOUT-002：前端登出時 store 已清空、不帶 Authorization；過去端點要求 [Authorize] 會 401，
    // 伺服器端 Refresh Token 永遠不會被撤銷。這裡用「之後換發 401」證明撤銷真的發生，而非只看 204。
    [Fact]
    public async Task Logout_WithoutAccessToken_RevokesTokenAndReturns204()
    {
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(_factory.CreateClient());
        var anonymousClient = _factory.CreateClient();

        var logoutResponse = await anonymousClient.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(tokens.RefreshToken));
        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refreshResponse = await anonymousClient.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(tokens.RefreshToken));
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_WithInvalidAccessToken_RevokesTokenAndReturns204()
    {
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(_factory.CreateClient());
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-valid-jwt");

        var logoutResponse = await client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(tokens.RefreshToken));
        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refreshResponse = await _factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(tokens.RefreshToken));
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // AUTH-LOGOUT-002 的核心情境（Access Token 預設 15 分鐘過期）。過期與簽章無效在 JwtBearer 走不同失敗路徑，
    // 不能互相代表。對照組：同 claim 未過期的 Token 呼叫受保護端點成功、過期 Token 呼叫同端點 401，
    // 證明這個 Token 只差在過期，且確實會被 JwtBearer 拒絕。
    [Fact]
    public async Task Logout_WithExpiredAccessToken_RevokesTokenAndReturns204()
    {
        var email = AuthTestHelper.NewEmail();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(_factory.CreateClient(), email);
        var memberId = await GetMemberIdAsync(tokens.AccessToken);

        var validClient = CreateClientWithBearer(SignedAccessTokenFactory.CreateSignedAccessToken(
            _factory.Services, memberId, email, organizerId: null, expiresAtUtc: DateTime.UtcNow.AddMinutes(5)));
        var expiredClient = CreateClientWithBearer(SignedAccessTokenFactory.CreateSignedAccessToken(
            _factory.Services, memberId, email, organizerId: null, expiresAtUtc: DateTime.UtcNow.AddMinutes(-5)));

        (await validClient.GetAsync("/api/members/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await expiredClient.GetAsync("/api/members/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var logoutResponse = await expiredClient.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(tokens.RefreshToken));
        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refreshResponse = await _factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(tokens.RefreshToken));
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // AUTH-LOGOUT-003：查無 Token 與成功回應相同，不提供存在性探測。
    [Fact]
    public async Task Logout_WithUnknownToken_Returns204()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/logout", new LogoutRequest("does-not-exist"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // AUTH-LOGOUT-006：缺欄位與 null 都靠 [ApiController] 的 nullable 模型驗證擋下，沒有另設 Validator（design.md 決策 5）。
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"refreshToken\":null}")]
    public async Task Logout_WithoutRefreshTokenField_Returns400(string body)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var response = await _factory.CreateClient().PostAsync("/api/auth/logout", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private HttpClient CreateClientWithBearer(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private async Task<Guid> GetMemberIdAsync(string accessToken)
    {
        var profile = await CreateClientWithBearer(accessToken).GetFromJsonAsync<MemberIdResponse>("/api/members/me");
        return profile!.Id;
    }

    private sealed record MemberIdResponse(Guid Id);

    [Fact]
    public async Task PasswordReset_RequestThenConfirmWithValidToken_AllowsLoginWithNewPassword()
    {
        var email = AuthTestHelper.NewEmail();
        await AuthTestHelper.RegisterAsync(_client, email);

        // Email 寄送不在本次範圍內；透過 Application Handler 直接取得明文 Token 來驅動整合測試（見 design.md）。
        using var scope = _factory.Services.CreateScope();
        var requestHandler = scope.ServiceProvider.GetRequiredService<RequestPasswordResetHandler>();
        var requestResult = await requestHandler.HandleAsync(new RequestPasswordResetRequest(email), CancellationToken.None);
        requestResult.IsSuccess.Should().BeTrue();
        var plainTextResetToken = requestResult.Value!;

        var confirmResponse = await _client.PostAsJsonAsync("/api/auth/password-reset/confirm", new ResetPasswordRequest(plainTextResetToken, "NewPassword1"));
        confirmResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var loginWithNewPassword = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "NewPassword1", FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));
        loginWithNewPassword.StatusCode.Should().Be(HttpStatusCode.OK);

        var loginWithOldPassword = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, AuthTestHelper.DefaultPassword, FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));
        loginWithOldPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PasswordReset_ConfirmWithUnknownToken_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/password-reset/confirm", new ResetPasswordRequest("not-a-real-token", "NewPassword1"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PasswordReset_RequestWithUnknownEmail_StillReturns200()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/password-reset/request", new RequestPasswordResetRequest(AuthTestHelper.NewEmail()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record MemberProfileResponse(Guid Id, string Email, string DisplayName, string Role, bool IsActive);
}
