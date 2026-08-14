using FluentAssertions;
using ProjectC.Application.Authentication.PasswordReset;
using ProjectC.Application.Common;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Tests.Authentication.PasswordReset;

public class RequestPasswordResetHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly FakeTokenService _tokenService = new();
    private readonly RequestPasswordResetHandler _handler;

    public RequestPasswordResetHandlerTests()
    {
        _handler = new RequestPasswordResetHandler(_dbContext, _tokenService, new FakeDateTimeProvider(), new AuthOptions());
    }

    [Fact]
    public async Task HandleAsync_WithExistingEmail_CreatesResetTokenAndReturnsPlainTextValue()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed:secret");
        _dbContext.MemberData.Add(member);

        var result = await _handler.HandleAsync(new RequestPasswordResetRequest("user@example.com"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNullOrEmpty();
        _dbContext.PasswordResetTokenData.Should().ContainSingle(t => t.MemberId == member.Id);
    }

    [Fact]
    public async Task HandleAsync_WithUnknownEmail_StillSucceedsWithoutTokenToAvoidEnumeration()
    {
        var result = await _handler.HandleAsync(new RequestPasswordResetRequest("nobody@example.com"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
        _dbContext.PasswordResetTokenData.Should().BeEmpty();
    }
}
