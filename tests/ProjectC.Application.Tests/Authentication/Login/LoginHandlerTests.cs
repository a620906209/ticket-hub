using FluentAssertions;
using ProjectC.Application.Authentication.Login;
using ProjectC.Application.Common;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Tests.Authentication.Login;

public class LoginHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly LoginHandler _handler;

    public LoginHandlerTests()
    {
        _handler = new LoginHandler(
            _dbContext,
            _passwordHasher,
            new FakeTokenService(),
            new FakeDateTimeProvider(),
            new AuthOptions(),
            new LoginRequestValidator());
    }

    private Member SeedActiveMember(string email = "user@example.com", string password = "Password123")
    {
        var member = Member.Register(email, "Alice", _passwordHasher.HashPassword(password));
        _dbContext.MemberData.Add(member);
        return member;
    }

    [Fact]
    public async Task HandleAsync_WithCorrectCredentialsAndActiveAccount_ReturnsTokens()
    {
        SeedActiveMember();

        var result = await _handler.HandleAsync(new LoginRequest("user@example.com", "Password123"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.AccessToken.Should().NotBeNullOrEmpty();
        result.Value!.RefreshToken.Should().NotBeNullOrEmpty();
        _dbContext.RefreshTokenData.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_WithWrongPassword_ReturnsUnauthorized()
    {
        SeedActiveMember();

        var result = await _handler.HandleAsync(new LoginRequest("user@example.com", "WrongPassword1"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task HandleAsync_WithUnknownEmail_ReturnsUnauthorized()
    {
        var result = await _handler.HandleAsync(new LoginRequest("nobody@example.com", "Password123"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task HandleAsync_WithDeactivatedAccount_ReturnsForbidden()
    {
        var member = SeedActiveMember();
        member.Deactivate();

        var result = await _handler.HandleAsync(new LoginRequest("user@example.com", "Password123"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
    }
}
