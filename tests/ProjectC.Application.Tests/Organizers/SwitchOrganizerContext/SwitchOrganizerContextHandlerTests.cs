using FluentAssertions;
using ProjectC.Application.Authentication.Refresh;
using ProjectC.Application.Common;
using ProjectC.Application.Organizers.SwitchOrganizerContext;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Authentication;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;

namespace ProjectC.Application.Tests.Organizers.SwitchOrganizerContext;

public class SwitchOrganizerContextHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly FakeTokenService _tokenService = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();
    private readonly SwitchOrganizerContextHandler _handler;

    public SwitchOrganizerContextHandlerTests()
    {
        _handler = new SwitchOrganizerContextHandler(_dbContext, _tokenService, _dateTimeProvider, new SwitchOrganizerContextRequestValidator());
    }

    private Member SeedMember()
    {
        var member = Member.Register($"{Guid.NewGuid():N}@example.com", "Test Member", "hash");
        _dbContext.MemberData.Add(member);
        return member;
    }

    private (RefreshToken Token, string PlainText) SeedActiveRefreshToken(Guid memberId, Guid? organizerId = null)
    {
        var plainText = _tokenService.GenerateOpaqueToken();
        var token = RefreshToken.Issue(memberId, _tokenService.HashOpaqueToken(plainText), _dateTimeProvider.UtcNow.AddDays(14), organizerId: organizerId);
        _dbContext.RefreshTokenData.Add(token);
        return (token, plainText);
    }

    private Domain.Organizers.Organizer SeedOrganizer(OrganizerStatus status, Guid? memberId = null, bool addMembership = true)
    {
        var applicantId = memberId ?? Guid.NewGuid();
        var organizer = Domain.Organizers.Organizer.Apply(Guid.NewGuid(), "Org", applicantId, _dateTimeProvider.UtcNow);
        if (status is OrganizerStatus.Approved or OrganizerStatus.Suspended)
        {
            organizer.Approve(applicantId, _dateTimeProvider.UtcNow);
        }
        if (status == OrganizerStatus.Suspended)
        {
            organizer.Suspend();
        }
        if (status == OrganizerStatus.Rejected)
        {
            organizer.Reject(applicantId, _dateTimeProvider.UtcNow);
        }
        _dbContext.OrganizerData.Add(organizer);
        if (memberId is { } id && addMembership)
        {
            _dbContext.OrganizerMemberData.Add(new OrganizerMember(Guid.NewGuid(), organizer.Id, id, OrganizerMemberRole.Owner));
        }
        return organizer;
    }

    // ORG-SWITCH-001
    [Fact]
    public async Task HandleAsync_ToOwnApprovedOrganizerWithValidRefreshToken_IssuesAccessTokenWithOrganizerIdClaimAndUpdatesOnlyThatToken()
    {
        var member = SeedMember();
        var (token, plainText) = SeedActiveRefreshToken(member.Id);
        var organizer = SeedOrganizer(OrganizerStatus.Approved, member.Id);

        var result = await _handler.HandleAsync(member.Id, organizer.Id, new SwitchOrganizerContextRequest(plainText), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.AccessToken.Should().Be($"access-token:{member.Id}:org:{organizer.Id}");
        token.OrganizerId.Should().Be(organizer.Id);
    }

    // ORG-SWITCH-007／ORG-REFRESH-001：只更新這一筆 Refresh Token 記錄
    [Fact]
    public async Task HandleAsync_WhenMemberHoldsAnotherActiveRefreshToken_DoesNotAffectTheOtherTokensOrganizerId()
    {
        var member = SeedMember();
        var (switchingToken, switchingPlainText) = SeedActiveRefreshToken(member.Id);
        var (otherToken, _) = SeedActiveRefreshToken(member.Id);
        var organizer = SeedOrganizer(OrganizerStatus.Approved, member.Id);

        var result = await _handler.HandleAsync(member.Id, organizer.Id, new SwitchOrganizerContextRequest(switchingPlainText), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        switchingToken.OrganizerId.Should().Be(organizer.Id);
        otherToken.OrganizerId.Should().BeNull();
    }

    // ORG-SWITCH-007：不觸發 Refresh Token 輪替
    [Fact]
    public async Task HandleAsync_OnSuccess_DoesNotRotateRefreshTokenAndSubsequentRefreshStillSucceeds()
    {
        var member = SeedMember();
        var (_, plainText) = SeedActiveRefreshToken(member.Id);
        var organizer = SeedOrganizer(OrganizerStatus.Approved, member.Id);

        var switchResult = await _handler.HandleAsync(member.Id, organizer.Id, new SwitchOrganizerContextRequest(plainText), CancellationToken.None);

        switchResult.IsSuccess.Should().BeTrue();
        _dbContext.RefreshTokenData.Should().ContainSingle("切換不建立任何新的 RefreshToken 資料列");

        var refreshHandler = new RefreshTokenHandler(_dbContext, _tokenService, _dateTimeProvider, new AuthOptions());
        var refreshResult = await refreshHandler.HandleAsync(new RefreshTokenRequest(plainText), CancellationToken.None);

        refreshResult.IsSuccess.Should().BeTrue("切換後該筆 Refresh Token 仍應能正常用於既有換發端點");
    }

    // ORG-SWITCH-002
    [Fact]
    public async Task HandleAsync_ToOrganizerCallerIsNotMemberOf_ReturnsForbiddenWithoutIssuingTokenOrUpdatingRecord()
    {
        var member = SeedMember();
        var (token, plainText) = SeedActiveRefreshToken(member.Id);
        var organizer = SeedOrganizer(OrganizerStatus.Approved, memberId: null);

        var result = await _handler.HandleAsync(member.Id, organizer.Id, new SwitchOrganizerContextRequest(plainText), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
        token.OrganizerId.Should().BeNull();
    }

    // ORG-SWITCH-003
    [Theory]
    [InlineData(OrganizerStatus.Pending)]
    [InlineData(OrganizerStatus.Rejected)]
    [InlineData(OrganizerStatus.Suspended)]
    public async Task HandleAsync_ToOrganizerNotApproved_ReturnsConflictWithoutIssuingTokenOrUpdatingRecord(OrganizerStatus status)
    {
        var member = SeedMember();
        var (token, plainText) = SeedActiveRefreshToken(member.Id);
        var organizer = SeedOrganizer(status, member.Id);

        var result = await _handler.HandleAsync(member.Id, organizer.Id, new SwitchOrganizerContextRequest(plainText), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        token.OrganizerId.Should().BeNull();
    }

    // ORG-APPLY-005：Pending 狀態的 Organizer 無法被其成員切換（ORG-SWITCH-003 的特化情境）
    [Fact]
    public async Task HandleAsync_ToOwnPendingOrganizer_ReturnsConflict()
    {
        var member = SeedMember();
        var (_, plainText) = SeedActiveRefreshToken(member.Id);
        var organizer = SeedOrganizer(OrganizerStatus.Pending, member.Id);

        var result = await _handler.HandleAsync(member.Id, organizer.Id, new SwitchOrganizerContextRequest(plainText), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
    }

    // ORG-SWITCH-004
    [Fact]
    public async Task HandleAsync_ToNonExistentOrganizer_ReturnsNotFoundWithoutIssuingTokenOrUpdatingRecord()
    {
        var member = SeedMember();
        var (token, plainText) = SeedActiveRefreshToken(member.Id);

        var result = await _handler.HandleAsync(member.Id, Guid.NewGuid(), new SwitchOrganizerContextRequest(plainText), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        token.OrganizerId.Should().BeNull();
    }

    // ORG-SWITCH-005
    [Fact]
    public async Task HandleAsync_WithEmptyRefreshToken_ReturnsValidationErrorWithoutTouchingDatabase()
    {
        var member = SeedMember();
        var organizer = SeedOrganizer(OrganizerStatus.Approved, member.Id);

        var result = await _handler.HandleAsync(member.Id, organizer.Id, new SwitchOrganizerContextRequest(""), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _dbContext.SaveChangesCallCount.Should().Be(0);
    }

    // ORG-SWITCH-006
    [Fact]
    public async Task HandleAsync_WithNonExistentRefreshToken_ReturnsUnauthorized()
    {
        var member = SeedMember();
        var organizer = SeedOrganizer(OrganizerStatus.Approved, member.Id);

        var result = await _handler.HandleAsync(member.Id, organizer.Id, new SwitchOrganizerContextRequest("does-not-exist"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Unauthorized);
    }

    // ORG-SWITCH-006
    [Fact]
    public async Task HandleAsync_WithAlreadyUsedRefreshToken_ReturnsUnauthorized()
    {
        var member = SeedMember();
        var (token, plainText) = SeedActiveRefreshToken(member.Id);
        token.MarkAsUsed();
        var organizer = SeedOrganizer(OrganizerStatus.Approved, member.Id);

        var result = await _handler.HandleAsync(member.Id, organizer.Id, new SwitchOrganizerContextRequest(plainText), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Unauthorized);
    }

    // ORG-SWITCH-006
    [Fact]
    public async Task HandleAsync_WithExpiredRefreshToken_ReturnsUnauthorized()
    {
        var member = SeedMember();
        var plainText = _tokenService.GenerateOpaqueToken();
        var expiredToken = RefreshToken.Issue(member.Id, _tokenService.HashOpaqueToken(plainText), _dateTimeProvider.UtcNow.AddMinutes(-1));
        _dbContext.RefreshTokenData.Add(expiredToken);
        var organizer = SeedOrganizer(OrganizerStatus.Approved, member.Id);

        var result = await _handler.HandleAsync(member.Id, organizer.Id, new SwitchOrganizerContextRequest(plainText), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Unauthorized);
    }

    // ORG-SWITCH-006
    [Fact]
    public async Task HandleAsync_WithRefreshTokenBelongingToAnotherMember_ReturnsUnauthorized()
    {
        var member = SeedMember();
        var otherMember = SeedMember();
        var (_, otherMembersPlainText) = SeedActiveRefreshToken(otherMember.Id);
        var organizer = SeedOrganizer(OrganizerStatus.Approved, member.Id);

        var result = await _handler.HandleAsync(member.Id, organizer.Id, new SwitchOrganizerContextRequest(otherMembersPlainText), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Unauthorized);
    }
}
