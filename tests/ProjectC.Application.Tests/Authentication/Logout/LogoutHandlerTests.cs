using FluentAssertions;
using ProjectC.Application.Authentication.Logout;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Authentication;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Tests.Authentication.Logout;

public class LogoutHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly FakeTokenService _tokenService = new();
    private readonly LogoutHandler _handler;

    public LogoutHandlerTests()
    {
        _handler = new LogoutHandler(_dbContext, _tokenService);
    }

    [Fact]
    public async Task HandleAsync_WithOwnedActiveToken_RevokesToken()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed:secret");
        _dbContext.MemberData.Add(member);

        var plainTextToken = _tokenService.GenerateOpaqueToken();
        var token = RefreshToken.Issue(member.Id, _tokenService.HashOpaqueToken(plainTextToken), DateTime.UtcNow.AddDays(14));
        _dbContext.RefreshTokenData.Add(token);

        var result = await _handler.HandleAsync(member.Id, new LogoutRequest(plainTextToken), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        token.Status.Should().Be(RefreshTokenStatus.Revoked);
    }

    [Fact]
    public async Task HandleAsync_WithUnknownToken_StillSucceedsIdempotently()
    {
        var result = await _handler.HandleAsync(Guid.NewGuid(), new LogoutRequest("does-not-exist"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}
