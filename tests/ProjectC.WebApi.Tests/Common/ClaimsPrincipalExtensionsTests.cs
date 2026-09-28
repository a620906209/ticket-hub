using System.Security.Claims;
using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Tests.Common;

/// <summary>
/// 直接測試 <see cref="ClaimsPrincipalExtensions.TryGetOrganizerId"/> 本身的解析與驗證邏輯
/// （見 organizer-management tasks.md 4.3、spec.md ORG-POLICY-001～004）。
/// </summary>
public class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal BuildPrincipal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "TestAuthType"));

    [Fact]
    public void TryGetOrganizerId_WithValidNonEmptyGuidClaim_ReturnsTrueAndOutputsGuid()
    {
        var organizerId = Guid.NewGuid();
        var user = BuildPrincipal(new Claim(CustomClaimTypes.OrganizerId, organizerId.ToString()));

        var result = user.TryGetOrganizerId(out var value);

        result.Should().BeTrue();
        value.Should().Be(organizerId);
    }

    [Fact]
    public void TryGetOrganizerId_WithoutClaim_ReturnsFalse()
    {
        var user = BuildPrincipal();

        var result = user.TryGetOrganizerId(out var value);

        result.Should().BeFalse();
        value.Should().Be(Guid.Empty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void TryGetOrganizerId_WithEmptyOrUnparseableClaimValue_ReturnsFalseWithoutThrowing(string claimValue)
    {
        var user = BuildPrincipal(new Claim(CustomClaimTypes.OrganizerId, claimValue));

        var act = () => user.TryGetOrganizerId(out _);

        act.Should().NotThrow();
        user.TryGetOrganizerId(out var value).Should().BeFalse();
        value.Should().Be(Guid.Empty);
    }

    [Fact]
    public void TryGetOrganizerId_WithGuidEmptyClaimValue_ReturnsFalse()
    {
        var user = BuildPrincipal(new Claim(CustomClaimTypes.OrganizerId, Guid.Empty.ToString()));

        var result = user.TryGetOrganizerId(out var value);

        result.Should().BeFalse();
        value.Should().Be(Guid.Empty);
    }
}
