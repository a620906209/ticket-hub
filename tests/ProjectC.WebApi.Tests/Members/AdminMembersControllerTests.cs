using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Members;

public class AdminMembersControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AdminMembersControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Client, Guid MemberId)> CreateAuthenticatedMemberAsync(string email)
    {
        var client = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var profile = await client.GetFromJsonAsync<MemberProfileResponse>("/api/members/me");
        return (client, profile!.Id);
    }

    private async Task<HttpClient> CreateAuthenticatedAdminClientAsync(string email)
    {
        await AuthTestHelper.RegisterAsync(_factory.CreateClient(), email);
        await AuthTestHelper.PromoteToAdminAsync(_factory.Services, email);

        var adminClient = _factory.CreateClient();
        var tokens = await AuthTestHelper.LoginAsync(adminClient, email);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return adminClient;
    }

    [Fact]
    public async Task Deactivate_AsAdmin_DeactivatesTargetMember()
    {
        var (_, targetMemberId) = await CreateAuthenticatedMemberAsync(AuthTestHelper.NewEmail());
        var adminClient = await CreateAuthenticatedAdminClientAsync(AuthTestHelper.NewEmail());

        var response = await adminClient.PostAsync($"/api/admin/members/{targetMemberId}/deactivate", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Activate_AsAdmin_ReactivatesTargetMember()
    {
        var (_, targetMemberId) = await CreateAuthenticatedMemberAsync(AuthTestHelper.NewEmail());
        var adminClient = await CreateAuthenticatedAdminClientAsync(AuthTestHelper.NewEmail());
        await adminClient.PostAsync($"/api/admin/members/{targetMemberId}/deactivate", content: null);

        var response = await adminClient.PostAsync($"/api/admin/members/{targetMemberId}/activate", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Deactivate_AsNonAdminMember_Returns403()
    {
        var (memberClient, targetMemberId) = await CreateAuthenticatedMemberAsync(AuthTestHelper.NewEmail());

        var response = await memberClient.PostAsync($"/api/admin/members/{targetMemberId}/deactivate", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Deactivate_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync($"/api/admin/members/{Guid.NewGuid()}/deactivate", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record MemberProfileResponse(Guid Id, string Email, string DisplayName, string Role, bool IsActive);
}
