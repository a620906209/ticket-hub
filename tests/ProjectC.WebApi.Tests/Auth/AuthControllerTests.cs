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
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Login_WithCorrectCredentials_ReturnsTokens()
    {
        var email = AuthTestHelper.NewEmail();
        await AuthTestHelper.RegisterAsync(_client, email);

        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, AuthTestHelper.DefaultPassword));

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

        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "WrongPassword1"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_WithUnknownEmail_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(AuthTestHelper.NewEmail(), AuthTestHelper.DefaultPassword));

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

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, AuthTestHelper.DefaultPassword));
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

    [Fact]
    public async Task Logout_WithoutAccessToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest("does-not-matter"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

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

        var loginWithNewPassword = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "NewPassword1"));
        loginWithNewPassword.StatusCode.Should().Be(HttpStatusCode.OK);

        var loginWithOldPassword = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, AuthTestHelper.DefaultPassword));
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
