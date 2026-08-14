using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Members;

public class MembersControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MembersControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string email)
    {
        var client = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    [Fact]
    public async Task GetMe_WithValidToken_ReturnsOwnProfile()
    {
        var email = AuthTestHelper.NewEmail();
        var client = await CreateAuthenticatedClientAsync(email);

        var response = await client.GetAsync("/api/members/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await response.Content.ReadFromJsonAsync<MemberProfileResponse>();
        profile!.Email.Should().Be(email);
        profile.Role.Should().Be("Member");
        profile.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetMe_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/members/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdateMe_WithValidDisplayName_UpdatesProfile()
    {
        var client = await CreateAuthenticatedClientAsync(AuthTestHelper.NewEmail());

        var response = await client.PutAsJsonAsync("/api/members/me", new { displayName = "Updated Name" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await response.Content.ReadFromJsonAsync<MemberProfileResponse>();
        profile!.DisplayName.Should().Be("Updated Name");
    }

    [Fact]
    public async Task UpdateMe_WithExtraRoleFieldInBody_IgnoresRoleAndKeepsMember()
    {
        var client = await CreateAuthenticatedClientAsync(AuthTestHelper.NewEmail());

        // UpdateMyProfileRequest 只有 DisplayName 屬性，多送的 role 欄位在模型繫結時會被忽略。
        var response = await client.PutAsJsonAsync("/api/members/me", new { displayName = "Still Member", role = "Admin" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await response.Content.ReadFromJsonAsync<MemberProfileResponse>();
        profile!.Role.Should().Be("Member");
    }

    [Fact]
    public async Task UpdateMe_WithEmptyDisplayName_Returns400()
    {
        var client = await CreateAuthenticatedClientAsync(AuthTestHelper.NewEmail());

        var response = await client.PutAsJsonAsync("/api/members/me", new { displayName = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record MemberProfileResponse(Guid Id, string Email, string DisplayName, string Role, bool IsActive);
}
