using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Organizers.ApplyForOrganizer;
using ProjectC.Domain.Organizers;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Organizers;

/// <summary>
/// 驗證 <see cref="ProjectC.WebApi.Controllers.OrganizerController"/>／
/// <see cref="ProjectC.WebApi.Controllers.AdminOrganizersController"/> 的 <c>[Authorize]</c>／
/// <c>[Authorize(Policy = AuthorizationPolicies.AdminOnly)]</c> 屬性實際生效，比照既有
/// AdminEventsControllerTests 的元件測試慣例。
/// </summary>
public class OrganizersAuthorizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public OrganizersAuthorizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> CreateAuthenticatedMemberClientAsync()
    {
        var client = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private async Task<OrganizerStatus> GetOrganizerStatusAsync(Guid organizerId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var organizer = await dbContext.Organizers.AsNoTracking().SingleAsync(o => o.Id == organizerId);
        return organizer.Status;
    }

    private async Task<bool> OrganizerExistsWithNameAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await dbContext.Organizers.AsNoTracking().AnyAsync(o => o.Name == name);
    }

    [Fact]
    public async Task Apply_WithoutAuthentication_Returns401AndDoesNotCreateOrganizer()
    {
        var anonymousClient = _factory.CreateClient();
        var uniqueName = $"Unauthenticated Apply {Guid.NewGuid():N}";

        var response = await anonymousClient.PostAsJsonAsync("/api/organizers", new ApplyForOrganizerRequest(uniqueName));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await OrganizerExistsWithNameAsync(uniqueName)).Should().BeFalse();
    }

    [Fact]
    public async Task ApproveAndReject_AsNonAdminMember_Return403AndDoNotChangeOrganizerStatus()
    {
        var applicantClient = await CreateAuthenticatedMemberClientAsync();
        var applyResponse = await applicantClient.PostAsJsonAsync(
            "/api/organizers", new ApplyForOrganizerRequest($"Pending Org {Guid.NewGuid():N}"));
        var organizerId = (await applyResponse.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;

        var nonAdminClient = await CreateAuthenticatedMemberClientAsync();

        var approveResponse = await nonAdminClient.PatchAsync($"/api/admin/organizers/{organizerId}/approve", content: null);
        approveResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetOrganizerStatusAsync(organizerId)).Should().Be(OrganizerStatus.Pending);

        var rejectResponse = await nonAdminClient.PatchAsync($"/api/admin/organizers/{organizerId}/reject", content: null);
        rejectResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetOrganizerStatusAsync(organizerId)).Should().Be(OrganizerStatus.Pending);
    }

    [Fact]
    public async Task GetPending_AsNonAdminMember_Returns403WithoutAnyListData()
    {
        var nonAdminClient = await CreateAuthenticatedMemberClientAsync();

        var response = await nonAdminClient.GetAsync("/api/admin/organizers?status=Pending");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("createdByDisplayName");
    }

    [Fact]
    public async Task Suspend_AsNonAdminMember_Returns403AndDoesNotChangeOrganizerStatus()
    {
        var ownerClient = await CreateAuthenticatedMemberClientAsync();
        var ownerProfile = await ownerClient.GetFromJsonAsync<MemberProfileResponse>("/api/members/me");
        var organizerId = await AuthTestHelper.CreateApprovedOrganizerAsync(_factory.Services, ownerProfile!.Id, $"Approved Org {Guid.NewGuid():N}");

        var nonAdminClient = await CreateAuthenticatedMemberClientAsync();

        var response = await nonAdminClient.PatchAsync($"/api/admin/organizers/{organizerId}/suspend", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetOrganizerStatusAsync(organizerId)).Should().Be(OrganizerStatus.Approved);
    }

    private sealed record MemberProfileResponse(Guid Id);
}
