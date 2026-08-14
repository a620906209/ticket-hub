using FluentAssertions;
using ProjectC.Application.Members.Deactivate;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Authentication;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Tests.Members.Deactivate;

public class DeactivateMemberHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly DeactivateMemberHandler _handler;

    public DeactivateMemberHandlerTests()
    {
        _handler = new DeactivateMemberHandler(_dbContext);
    }

    [Fact]
    public async Task HandleAsync_WhenCalled_SetsIsActiveFalseAndRevokesAllRefreshTokens()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed:secret");
        _dbContext.MemberData.Add(member);

        var activeToken = RefreshToken.Issue(member.Id, "hash-active", DateTime.UtcNow.AddDays(14));
        var usedToken = RefreshToken.Issue(member.Id, "hash-used", DateTime.UtcNow.AddDays(14));
        usedToken.MarkAsUsed();
        var alreadyRevokedToken = RefreshToken.Issue(member.Id, "hash-revoked", DateTime.UtcNow.AddDays(14));
        alreadyRevokedToken.Revoke();

        _dbContext.RefreshTokenData.AddRange([activeToken, usedToken, alreadyRevokedToken]);

        var result = await _handler.HandleAsync(member.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        member.IsActive.Should().BeFalse();
        activeToken.Status.Should().Be(RefreshTokenStatus.Revoked);
        usedToken.Status.Should().Be(RefreshTokenStatus.Revoked);
        alreadyRevokedToken.Status.Should().Be(RefreshTokenStatus.Revoked);
    }
}
