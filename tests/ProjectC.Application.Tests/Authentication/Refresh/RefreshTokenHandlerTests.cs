using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Authentication.Refresh;
using ProjectC.Application.Common;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Authentication;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;

namespace ProjectC.Application.Tests.Authentication.Refresh;

public class RefreshTokenHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly FakeTokenService _tokenService = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();
    private readonly RefreshTokenHandler _handler;

    public RefreshTokenHandlerTests()
    {
        _handler = new RefreshTokenHandler(_dbContext, _tokenService, _dateTimeProvider, new AuthOptions());
    }

    private (Member Member, RefreshToken Token, string PlainTextToken) SeedActiveRefreshToken()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed:secret");
        _dbContext.MemberData.Add(member);

        var plainTextToken = _tokenService.GenerateOpaqueToken();
        var token = RefreshToken.Issue(member.Id, _tokenService.HashOpaqueToken(plainTextToken), _dateTimeProvider.UtcNow.AddDays(14));
        _dbContext.RefreshTokenData.Add(token);

        return (member, token, plainTextToken);
    }

    [Fact]
    public async Task HandleAsync_WithActiveUnexpiredToken_RotatesAndReturnsNewTokens()
    {
        var (_, token, plainTextToken) = SeedActiveRefreshToken();

        var result = await _handler.HandleAsync(new RefreshTokenRequest(plainTextToken), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        token.Status.Should().Be(RefreshTokenStatus.Used);
        _dbContext.RefreshTokenData.Should().HaveCount(2);
        _dbContext.RefreshTokenData.Should().Contain(t => t.PreviousTokenId == token.Id);
    }

    [Fact]
    public async Task HandleAsync_WithExpiredToken_ReturnsUnauthorizedWithoutRevokingOtherTokens()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed:secret");
        _dbContext.MemberData.Add(member);
        var plainTextToken = _tokenService.GenerateOpaqueToken();
        var expiredToken = RefreshToken.Issue(member.Id, _tokenService.HashOpaqueToken(plainTextToken), _dateTimeProvider.UtcNow.AddMinutes(-1));
        _dbContext.RefreshTokenData.Add(expiredToken);

        var result = await _handler.HandleAsync(new RefreshTokenRequest(plainTextToken), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Unauthorized);
        expiredToken.Status.Should().Be(RefreshTokenStatus.Active);
    }

    [Fact]
    public async Task HandleAsync_WithAlreadyUsedToken_RevokesAllTokensForThatMember()
    {
        var (member, token, plainTextToken) = SeedActiveRefreshToken();
        token.MarkAsUsed();

        var otherActiveToken = RefreshToken.Issue(member.Id, "hash-other", _dateTimeProvider.UtcNow.AddDays(14));
        _dbContext.RefreshTokenData.Add(otherActiveToken);

        var result = await _handler.HandleAsync(new RefreshTokenRequest(plainTextToken), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Unauthorized);
        otherActiveToken.Status.Should().Be(RefreshTokenStatus.Revoked);
    }

    [Fact]
    public async Task HandleAsync_WithDeactivatedMember_ReturnsUnauthorized()
    {
        var (member, _, plainTextToken) = SeedActiveRefreshToken();
        member.Deactivate();

        var result = await _handler.HandleAsync(new RefreshTokenRequest(plainTextToken), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task HandleAsync_WhenConcurrentRequestAlreadyConsumedToken_ReturnsUnauthorizedWithoutTriggeringReuseDetection()
    {
        var (member, token, plainTextToken) = SeedActiveRefreshToken();
        var otherActiveToken = RefreshToken.Issue(member.Id, "hash-other", _dateTimeProvider.UtcNow.AddDays(14));
        _dbContext.RefreshTokenData.Add(otherActiveToken);

        _dbContext.ExceptionToThrowOnNextSaveChanges = new DbUpdateConcurrencyException("並發衝突：Token 已被其他請求消費。");

        var result = await _handler.HandleAsync(new RefreshTokenRequest(plainTextToken), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Unauthorized);
        otherActiveToken.Status.Should().Be(RefreshTokenStatus.Active);
    }

    private (Member Member, Domain.Organizers.Organizer Organizer, RefreshToken Token, string PlainTextToken) SeedActiveTokenWithOrganizerContext(
        bool addOrganizerMembership = true)
    {
        var member = Member.Register("user@example.com", "Alice", "hashed:secret");
        _dbContext.MemberData.Add(member);

        var organizer = Domain.Organizers.Organizer.Apply(Guid.NewGuid(), "Org", member.Id, _dateTimeProvider.UtcNow);
        organizer.Approve(member.Id, _dateTimeProvider.UtcNow);
        _dbContext.OrganizerData.Add(organizer);
        if (addOrganizerMembership)
        {
            _dbContext.OrganizerMemberData.Add(new OrganizerMember(Guid.NewGuid(), organizer.Id, member.Id, OrganizerMemberRole.Owner));
        }

        var plainTextToken = _tokenService.GenerateOpaqueToken();
        var token = RefreshToken.Issue(member.Id, _tokenService.HashOpaqueToken(plainTextToken), _dateTimeProvider.UtcNow.AddDays(14), organizerId: organizer.Id);
        _dbContext.RefreshTokenData.Add(token);

        return (member, organizer, token, plainTextToken);
    }

    // ORG-REFRESH-002
    [Fact]
    public async Task HandleAsync_WhenCurrentTokenHasOrganizerIdAndStillEligible_NewAccessTokenAndRefreshTokenKeepSameOrganizerId()
    {
        var (_, organizer, _, plainTextToken) = SeedActiveTokenWithOrganizerContext();

        var result = await _handler.HandleAsync(new RefreshTokenRequest(plainTextToken), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.AccessToken.Should().Contain($"org:{organizer.Id}");
        var newToken = _dbContext.RefreshTokenData.Should().ContainSingle(t => t.PreviousTokenId != null).Subject;
        newToken.OrganizerId.Should().Be(organizer.Id);
    }

    // ORG-REFRESH-003
    [Fact]
    public async Task HandleAsync_WhenCurrentTokenOrganizerHasBeenSuspended_NewAccessTokenAndRefreshTokenHaveNoOrganizerId()
    {
        var (_, organizer, _, plainTextToken) = SeedActiveTokenWithOrganizerContext();
        organizer.Suspend();

        var result = await _handler.HandleAsync(new RefreshTokenRequest(plainTextToken), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.AccessToken.Should().NotContain("org:");
        var newToken = _dbContext.RefreshTokenData.Should().ContainSingle(t => t.PreviousTokenId != null).Subject;
        newToken.OrganizerId.Should().BeNull();
    }

    // ORG-REFRESH-004
    [Fact]
    public async Task HandleAsync_WhenCallerNoLongerHasOrganizerMembership_NewAccessTokenAndRefreshTokenHaveNoOrganizerId()
    {
        var (_, _, _, plainTextToken) = SeedActiveTokenWithOrganizerContext(addOrganizerMembership: false);

        var result = await _handler.HandleAsync(new RefreshTokenRequest(plainTextToken), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.AccessToken.Should().NotContain("org:");
        var newToken = _dbContext.RefreshTokenData.Should().ContainSingle(t => t.PreviousTokenId != null).Subject;
        newToken.OrganizerId.Should().BeNull();
    }
}
