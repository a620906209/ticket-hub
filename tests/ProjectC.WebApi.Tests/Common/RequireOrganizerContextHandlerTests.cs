using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using ProjectC.Application.Common;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Tests.Common;

/// <summary>
/// 驗證 organizer-management spec.md「RequireOrganizerContext Authorization Policy MUST 驗證 claim
/// 格式並 fail-closed」（ORG-POLICY-001～004）。純粹的 Policy Handler 自身邏輯單元測試，不透過真實
/// HTTP pipeline（本次未套用至任何 Controller，見 tasks.md 4.3a）。
/// </summary>
public class RequireOrganizerContextHandlerTests
{
    private readonly RequireOrganizerContextHandler _handler = new();

    private static ClaimsPrincipal BuildPrincipal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "TestAuthType"));

    private async Task<AuthorizationHandlerContext> RunAsync(ClaimsPrincipal user)
    {
        var context = new AuthorizationHandlerContext([new RequireOrganizerContextRequirement()], user, resource: null);
        await _handler.HandleAsync(context);
        return context;
    }

    [Fact]
    public async Task HandleAsync_WithValidNonEmptyOrganizerIdClaim_Succeeds()
    {
        var user = BuildPrincipal(new Claim(CustomClaimTypes.OrganizerId, Guid.NewGuid().ToString()));

        var context = await RunAsync(user);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithoutOrganizerIdClaim_DoesNotSucceed()
    {
        var user = BuildPrincipal(new Claim(ClaimTypes.Role, "Member"));

        var context = await RunAsync(user);

        context.HasSucceeded.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task HandleAsync_WithUnparseableOrganizerIdClaim_DoesNotSucceedAndDoesNotThrow(string claimValue)
    {
        var user = BuildPrincipal(new Claim(CustomClaimTypes.OrganizerId, claimValue));

        var act = async () => await RunAsync(user);

        var context = await act.Should().NotThrowAsync();
        context.Subject.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_WithGuidEmptyOrganizerIdClaim_DoesNotSucceed()
    {
        var user = BuildPrincipal(new Claim(CustomClaimTypes.OrganizerId, Guid.Empty.ToString()));

        var context = await RunAsync(user);

        context.HasSucceeded.Should().BeFalse();
    }
}
